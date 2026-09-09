using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Downloads media without forwarding an origin's credentials to another origin.</summary>
internal sealed partial class ScopedMediaTransport : IDisposable, IAsyncDisposable
{
    internal const int DefaultMaximumBytes = 32 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly Uri _origin;
    private readonly KeyValuePair<string, string>[] _headers;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private readonly object _requestsLock = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeRequests;
    private bool _resourcesDisposed;
    private int _disposed;

    internal ScopedMediaTransport(Uri origin, IReadOnlyDictionary<string, string> headers,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(headers);
        ValidateUri(origin);
        _origin = origin;
        _headers = headers.ToArray();
        foreach (var header in _headers)
        {
            if (!IsHeaderName(header.Key) || header.Value is null || header.Value.Any(char.IsControl)
                || IsReservedHeader(header.Key)) throw new PlaybackException("NotAllowed");
        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _lifetimeToken = _lifetime.Token;
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal Task<MediaResource> DownloadAsync(Uri uri, long? rangeOffset = null,
        long? rangeLength = null, int maximumBytes = DefaultMaximumBytes, CancellationToken ct = default)
        => DownloadCoreAsync(uri, rangeOffset, rangeLength, maximumBytes, null, null, null, ct);

    internal Task<MediaResource> DownloadRangeAsync(Uri uri, long offset, int length,
        long? expectedTotalLength, string? entityTag, DateTimeOffset? lastModified, CancellationToken ct)
        => DownloadCoreAsync(uri, offset, length, length, expectedTotalLength, entityTag, lastModified, ct);

    private async Task<MediaResource> DownloadCoreAsync(Uri uri, long? rangeOffset,
        long? rangeLength, int maximumBytes, long? expectedTotalLength, string? entityTag,
        DateTimeOffset? lastModified, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lifetimeToken.ThrowIfCancellationRequested();
        using var lease = RegisterRequest();
        ArgumentNullException.ThrowIfNull(uri);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        ValidateRange(rangeOffset, rangeLength);
        if (rangeLength > maximumBytes) throw new PlaybackException("UnsupportedFormat");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        operation.CancelAfter(RequestTimeout);
        try
        {
            using var response = await SendAsync(uri, rangeOffset, rangeLength, entityTag,
                lastModified, operation.Token).ConfigureAwait(false);
            var content = response.Content.Headers;
            var totalLength = content.ContentRange?.Length;
            if (expectedTotalLength.HasValue && totalLength != expectedTotalLength)
                throw new PlaybackException("NetworkFailure");
            if (entityTag is not null && response.Headers.ETag is { } actualTag
                && !string.Equals(entityTag, actualTag.ToString(), StringComparison.Ordinal))
                throw new PlaybackException("NetworkFailure");
            if (lastModified.HasValue && content.LastModified.HasValue
                && lastModified != content.LastModified) throw new PlaybackException("NetworkFailure");

            var bytes = await ReadBoundedAsync(response, maximumBytes, operation.Token).ConfigureAwait(false);
            return new MediaResource(bytes, content.ContentType?.MediaType ?? "application/octet-stream",
                response.RequestMessage!.RequestUri!, response.StatusCode, content.ContentRange?.ToString(),
                totalLength, response.Headers.ETag?.ToString(), content.LastModified);
        }
        catch (OperationCanceledException)
        {
            // Do not expose an HTTP exception's inner message, which may contain an authenticated URI.
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (_lifetimeToken.IsCancellationRequested) throw new OperationCanceledException(_lifetimeToken);
            throw new PlaybackException("NetworkFailure");
        }
        catch (HttpRequestException)
        {
            throw new PlaybackException("NetworkFailure");
        }
        catch (IOException)
        {
            throw new PlaybackException("NetworkFailure");
        }
        catch (FormatException)
        {
            throw new PlaybackException("UnsupportedFormat");
        }
        catch (InvalidOperationException)
        {
            if (_lifetimeToken.IsCancellationRequested) throw new OperationCanceledException(_lifetimeToken);
            throw new PlaybackException("NetworkFailure");
        }
        catch (ArgumentException)
        {
            throw new PlaybackException("NotAllowed");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, long? rangeOffset, long? rangeLength,
        string? entityTag, DateTimeOffset? lastModified, CancellationToken ct)
    {
        var current = ScopeUri(uri);
        for (var redirects = 0; ; redirects++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (SameOrigin(current, _origin))
            {
                foreach (var header in _headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        throw new PlaybackException("NotAllowed");
                }
            }

            if (rangeOffset.HasValue)
            {
                var end = rangeLength.HasValue ? rangeOffset.Value + rangeLength.Value - 1 : (long?)null;
                request.Headers.Range = new RangeHeaderValue(rangeOffset, end);
            }
            if (entityTag is not null && EntityTagHeaderValue.TryParse(entityTag, out var parsedTag)
                && !parsedTag.IsWeak)
            {
                request.Headers.IfMatch.Add(parsedTag);
            }
            else if (lastModified.HasValue)
            {
                request.Headers.IfUnmodifiedSince = lastModified;
            }

            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            try
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= MaximumRedirects || response.Headers.Location is not { } location)
                        throw new PlaybackException("NetworkFailure");
                    current = ScopeUri(location.IsAbsoluteUri ? location : new Uri(current, location));
                    response.Dispose();
                    continue;
                }

                ValidateResponse(response, rangeOffset, rangeLength);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static void ValidateResponse(HttpResponseMessage response, long? rangeOffset, long? rangeLength)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new PlaybackException("AuthenticationRequired");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new PlaybackException("NotAllowed");
        if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.UnsupportedMediaType)
            throw new PlaybackException("UnsupportedFormat");
        if (!response.IsSuccessStatusCode) throw new PlaybackException("NetworkFailure");
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException("UnsupportedFormat");

        if (!rangeOffset.HasValue)
        {
            if (response.StatusCode == HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not null)
                throw new PlaybackException("UnsupportedFormat");
            return;
        }

        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range is null
            || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || !range.From.HasValue || !range.To.HasValue || range.From != rangeOffset
            || range.To < range.From || range.To == long.MaxValue
            || (range.Length.HasValue && range.To >= range.Length))
            throw new PlaybackException("UnsupportedFormat");
        if (rangeLength.HasValue)
        {
            var requestedEnd = rangeOffset.Value + rangeLength.Value - 1;
            var expectedEnd = range.Length.HasValue ? Math.Min(requestedEnd, range.Length.Value - 1) : requestedEnd;
            if (range.To != expectedEnd) throw new PlaybackException("UnsupportedFormat");
        }
        if (response.Content.Headers.ContentLength is { } contentLength
            && contentLength != range.To.Value - range.From.Value + 1)
            throw new PlaybackException("UnsupportedFormat");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximumBytes, CancellationToken ct)
    {
        var headers = response.Content.Headers;
        var expectedLength = headers.ContentRange is { From: { } from, To: { } to }
            ? to - from + 1 : headers.ContentLength;
        if (expectedLength > maximumBytes) throw new PlaybackException("UnsupportedFormat");
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destination = new MemoryStream(expectedLength.HasValue ? (int)expectedLength.Value : Math.Min(maximumBytes, 65536));
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (true)
            {
                var allowed = (int)Math.Min(buffer.Length, maximumBytes - destination.Length + 1L);
                var read = await source.ReadAsync(buffer.AsMemory(0, allowed), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (destination.Length + read > maximumBytes) throw new PlaybackException("UnsupportedFormat");
                destination.Write(buffer, 0, read);
            }
            ct.ThrowIfCancellationRequested();
            if (expectedLength.HasValue && destination.Length != expectedLength.Value)
                throw new PlaybackException("NetworkFailure");
            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private Uri ScopeUri(Uri uri)
    {
        ValidateUri(uri);
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (!SameOrigin(uri, _origin))
        {
            // Both separators are accepted by deployed HTTP servers. Decode names before matching.
            var parts = uri.Query.TrimStart('?').Split(['&', ';'], StringSplitOptions.None);
            if (parts.Any(IsCredentialParameter))
                builder.Query = string.Join("&", parts.Where(part => !IsCredentialParameter(part)));
        }
        return builder.Uri;
    }

    private static bool IsCredentialParameter(string part)
    {
        var separator = part.IndexOf('=');
        var name = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
        return name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AccessToken", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)) throw new PlaybackException("NotAllowed");
    }

    private static void ValidateRange(long? offset, long? length)
    {
        if (offset < 0 || length <= 0 || (!offset.HasValue && length.HasValue)
            || (offset.HasValue && length.HasValue && offset.Value > long.MaxValue - (length.Value - 1)))
            throw new PlaybackException("UnsupportedFormat");
    }

    private static bool SameOrigin(Uri first, Uri second)
        => first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase)
            && first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsHeaderName(string name)
        => !string.IsNullOrEmpty(name) && name.All(value => char.IsAsciiLetterOrDigit(value)
            || "!#$%&'*+-.^_`|~".Contains(value));

    private static bool IsReservedHeader(string name)
        => name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Range", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("If-Match", StringComparison.OrdinalIgnoreCase)
            || name.Equals("If-Range", StringComparison.OrdinalIgnoreCase)
            || name.Equals("If-Unmodified-Since", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _client.Dispose();
        lock (_requestsLock)
        {
            _resourcesDisposed = true;
            CompleteDrainIfReady();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _drained.Task.ConfigureAwait(false);
    }

    private RequestLease RegisterRequest()
    {
        lock (_requestsLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _activeRequests++;
            return new RequestLease(this);
        }
    }

    private void CompleteRequest()
    {
        lock (_requestsLock)
        {
            _activeRequests--;
            CompleteDrainIfReady();
        }
    }

    private void CompleteDrainIfReady()
    {
        if (!_resourcesDisposed || _activeRequests != 0) return;
        _lifetime.Dispose();
        _drained.TrySetResult();
    }

    private readonly struct RequestLease(ScopedMediaTransport transport) : IDisposable
    {
        public void Dispose() => transport.CompleteRequest();
    }
}

/// <summary>Contains the bytes and checked metadata of one bounded HTTP representation.</summary>
internal sealed partial class MediaResource(byte[] data, string contentType, Uri effectiveUri,
    HttpStatusCode statusCode, string? contentRange, long? totalLength, string? entityTag,
    DateTimeOffset? lastModified)
{
    public byte[] Data { get; } = data;
    public string ContentType { get; } = contentType;
    public Uri EffectiveUri { get; } = effectiveUri;
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? ContentRange { get; } = contentRange;
    internal long? TotalLength { get; } = totalLength;
    internal string? EntityTag { get; } = entityTag;
    internal DateTimeOffset? LastModified { get; } = lastModified;
    public override string ToString() => $"{nameof(MediaResource)} {{ Length = {Data.Length}, StatusCode = {(int)StatusCode} }}";
}
