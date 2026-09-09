using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using Windows.Web.Http.Headers;
using HttpMethod = Windows.Web.Http.HttpMethod;
using HttpRequestMessage = Windows.Web.Http.HttpRequestMessage;
using HttpResponseMessage = Windows.Web.Http.HttpResponseMessage;
using HttpStatusCode = Windows.Web.Http.HttpStatusCode;

namespace EmbyClient.App.Playback;

/// <summary>Routes every native HLS request through the same origin-scoped managed transport.</summary>
internal sealed partial class ScopedAdaptiveHttpFilter(ScopedMediaTransport transport, Action<string> failed) : IHttpFilter
{
    private readonly object _requestsLock = new();
    private readonly HashSet<Task> _requests = [];
    private bool _disposed;

    public IAsyncOperationWithProgress<HttpResponseMessage, HttpProgress> SendRequestAsync(HttpRequestMessage request)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requestsLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Register before starting work so retirement cannot overlook a request entering the filter.
            _requests.Add(completion.Task);
        }

        try
        {
            return AsyncInfo.Run<HttpResponseMessage, HttpProgress>((cancellationToken, _) =>
                RunRegisteredAsync(request, cancellationToken, completion));
        }
        catch
        {
            CompleteRequest(completion);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_requestsLock) _disposed = true;
    }

    public async Task DrainAsync()
    {
        Dispose();
        Task[] pending;
        lock (_requestsLock) pending = [.. _requests];
        // The engine cancels the shared transport before closing native consumers and draining this filter.
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { }
    }

    private async Task<HttpResponseMessage> RunRegisteredAsync(HttpRequestMessage request,
        CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        try { return await DownloadAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { CompleteRequest(completion); }
    }

    private void CompleteRequest(TaskCompletionSource completion)
    {
        lock (_requestsLock)
        {
            completion.TrySetResult();
            _requests.Remove(completion.Task);
        }
    }

    private async Task<HttpResponseMessage> DownloadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            if (!request.Method.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.RequestUri is null)
                throw new PlaybackException("UnsupportedFormat");
            var (offset, length) = ParseRange(request);
            var resource = await transport.DownloadAsync(request.RequestUri, offset, length,
                maximumBytes: 64 * 1024 * 1024, ct: cancellationToken).ConfigureAwait(false);
            var response = new HttpResponseMessage((HttpStatusCode)(int)resource.StatusCode)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, resource.EffectiveUri),
                Content = new HttpBufferContent(resource.Data.AsBuffer())
            };
            response.Content.Headers.ContentType = new HttpMediaTypeHeaderValue(resource.ContentType);
            if (resource.ContentRange is not null)
                response.Content.Headers.TryAppendWithoutValidation("Content-Range", resource.ContentRange);
            return response;
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException exception)
        {
            failed(exception.ErrorCode);
            throw;
        }
        catch
        {
            failed("NetworkFailure");
            throw new PlaybackException("NetworkFailure");
        }
    }

    private static (long? Offset, long? Length) ParseRange(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValue("Range", out var range)) return (null, null);
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(','))
            throw new PlaybackException("UnsupportedFormat");
        var bounds = range[6..].Split('-', 2);
        if (bounds.Length != 2 || !long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new PlaybackException("UnsupportedFormat");
        if (bounds[1].Length == 0) return (offset, null);
        if (!long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) || end < offset || end == long.MaxValue)
            throw new PlaybackException("UnsupportedFormat");
        return (offset, end - offset + 1);
    }
}
