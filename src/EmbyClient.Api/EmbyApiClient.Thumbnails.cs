using System.Net;
using System.Net.Http.Headers;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    /// <summary>
    /// Reads server-generated thumbnail metadata. SDK 4.10.1.0 requires Width and supplies no per-sample
    /// image URL or PositionTicks-to-image-index mapping. Image retrieval must use an established contract.
    /// </summary>
    public async Task<ThumbnailSetInfo?> GetThumbnailSetAsync(string itemId, int width,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The thumbnail width must be positive.");
        try
        {
            return await GetAsync($"Items/{Segment(itemId)}/ThumbnailSet", new Query().Add("Width", width),
                EmbyJsonContext.Default.ThumbnailSetInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbyApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the official BIF route and reads its published Roku index and still-image format.
    /// The current operation has no MediaSourceId contract; callers must establish item-timeline ownership.
    /// </summary>
    public async Task<BifThumbnailSet?> GetBifThumbnailsAsync(string itemId, int width,
        long? maximumPositionTicks = null, CancellationToken cancellationToken = default)
    {
        if (width is <= 0 or > BifThumbnailReader.MaximumImageDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "The BIF width must fit the client image limit.");
        if (maximumPositionTicks < 0) throw new ArgumentOutOfRangeException(nameof(maximumPositionTicks));
        using var request = CreateRequest(HttpMethod.Get, $"Videos/{Segment(itemId)}/index.bif",
            new Query().Add("Width", width), null, true);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            // The configured transport disables redirects, so no account headers are replayed elsewhere.
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is > BifThumbnailReader.MaximumPackageBytes)
                throw new EmbyProtocolException("The requested BIF archive exceeds the client size limit.");
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new EmbyProtocolException("Emby returned a non-BIF response for a thumbnail archive request.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > BifThumbnailReader.MaximumPackageBytes)
                    throw new EmbyProtocolException("The requested BIF archive exceeds the client size limit.");
                output.Write(buffer, 0, read);
            }
            return BifThumbnailReader.Read(output.GetBuffer().AsSpan(0, (int)output.Length), maximumPositionTicks, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby BIF request timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new EmbyTransportException("The Emby BIF transfer failed.");
        }
    }
}
