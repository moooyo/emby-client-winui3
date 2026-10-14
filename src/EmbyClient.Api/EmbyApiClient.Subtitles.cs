using System.Net.Http.Headers;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    public const int MaximumTextSubtitleBytes = 4 * 1024 * 1024;

    /// <summary>Resolves internal subtitle routes against the API mount, including reverse-proxy prefixes.</summary>
    public Uri ResolveTextSubtitleUri(string deliveryUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryUrl);
        if (deliveryUrl.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase)
            || deliveryUrl.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase)) deliveryUrl = deliveryUrl[1..];
        return ResolveMediaUri(deliveryUrl);
    }

    /// <summary>Downloads a negotiated full-text subtitle without sending account credentials to another origin.</summary>
    public async Task<byte[]> GetTextSubtitleAsync(string deliveryUrl, CancellationToken cancellationToken = default)
    {
        var target = ResolveTextSubtitleUri(deliveryUrl);
        if (!IsSameOrigin(target, ApiRoot))
            throw new ArgumentException("Text subtitle downloads must use the configured Emby origin.", nameof(deliveryUrl));
        if (!target.AbsolutePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            && !target.AbsolutePath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Text subtitle downloads support SRT and WebVTT delivery only.", nameof(deliveryUrl));

        using var request = CreateRequest(HttpMethod.Get, target.AbsoluteUri, null, null, true);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/vtt"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-subrip"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", .8));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", .6));
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            // The shared transport disables automatic redirects. A redirect is an error, not permission
            // to replay either the account headers or a negotiated token query at a different origin.
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is > MaximumTextSubtitleBytes)
                throw new EmbyProtocolException("The requested subtitle exceeds the client size limit.");

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !IsTextSubtitleContentType(contentType))
                throw new EmbyProtocolException("Emby returned a non-subtitle response for a text subtitle request.");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumTextSubtitleBytes)
                    throw new EmbyProtocolException("The requested subtitle exceeds the client size limit.");
                output.Write(buffer, 0, read);
            }
            if (output.Length == 0) throw new EmbyProtocolException("Emby returned an empty text subtitle.");
            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby subtitle request timed out.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new EmbyTransportException("The Emby subtitle transfer failed.");
        }
    }

    private static bool IsTextSubtitleContentType(string contentType) => contentType.ToLowerInvariant() is
        "text/vtt" or "text/plain" or "text/srt" or "application/x-subrip" or "application/srt"
        or "application/octet-stream";
}
