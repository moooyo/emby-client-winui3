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

    /// <summary>Owns a streaming response until its asynchronous lease is disposed; no body is buffered here.</summary>
    internal async Task<MediaStreamLease> OpenStreamAsync(Uri uri, bool isHead = false,
        string? range = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _lifetimeToken.ThrowIfCancellationRequested();
        var requestLease = RegisterRequest();
        CancellationTokenSource? operation = null;
        HttpResponseMessage? response = null;
        Stream? stream = null;
        var transferred = false;
        try
        {
            ArgumentNullException.ThrowIfNull(uri);
            RangeHeaderValue? requestedRange = null;
            if (!isHead && range is not null
                && (!RangeHeaderValue.TryParse(range, out requestedRange)
                    || !requestedRange.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
                    || requestedRange.Ranges.Count != 1)) throw new PlaybackException("UnsupportedFormat");
            operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
            operation.CancelAfter(RequestTimeout);
            response = await SendAsync(uri, null, null, null, null, operation.Token,
                isHead ? HttpMethod.Head : HttpMethod.Get, requestedRange, streaming: true).ConfigureAwait(false);
            stream = isHead || !response.IsSuccessStatusCode ? Stream.Null
                : await response.Content.ReadAsStreamAsync(operation.Token).ConfigureAwait(false);
            operation.CancelAfter(Timeout.InfiniteTimeSpan);
            var result = new MediaStreamLease(response, stream, operation, ct, _lifetimeToken,
                requestLease.Dispose, isHead, RequestTimeout);
            transferred = true;
            try { result.AttachCancellation(); }
            catch
            {
                await result.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (_lifetimeToken.IsCancellationRequested) throw new OperationCanceledException(_lifetimeToken);
            throw new PlaybackException("NetworkFailure");
        }
        catch (HttpRequestException) { throw new PlaybackException("NetworkFailure"); }
        catch (IOException) { throw new PlaybackException("NetworkFailure"); }
        catch (FormatException) { throw new PlaybackException("UnsupportedFormat"); }
        catch (InvalidOperationException)
        {
            if (_lifetimeToken.IsCancellationRequested) throw new OperationCanceledException(_lifetimeToken);
            throw new PlaybackException("NetworkFailure");
        }
        catch (ArgumentException) { throw new PlaybackException("NotAllowed"); }
        finally
        {
            if (!transferred)
            {
                try
                {
                    response?.Dispose();
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    operation?.Dispose();
                    requestLease.Dispose();
                }
            }
        }
    }

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
        string? entityTag, DateTimeOffset? lastModified, CancellationToken ct,
        HttpMethod? method = null, RangeHeaderValue? streamingRange = null, bool streaming = false)
    {
        var current = ScopeUri(uri);
        for (var redirects = 0; ; redirects++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, current);
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
            else if (streamingRange is not null) request.Headers.Range = streamingRange;
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

                if (streaming) ValidateStreamingResponse(response, streamingRange, method == HttpMethod.Head);
                else ValidateResponse(response, rangeOffset, rangeLength);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static void ValidateStreamingResponse(HttpResponseMessage response, RangeHeaderValue? requestedRange, bool isHead)
    {
        // Error bodies are never read or forwarded. In particular, 416 is a legitimate range response,
        // not evidence of a network failure; preserve its actual status and Content-Range metadata.
        if (!response.IsSuccessStatusCode) return;
        // HttpClient removes chunk framing but preserves Content-Length and does not decode
        // arbitrary transfer codings. Never turn ambiguous or still-encoded bytes into media.
        if (response.Headers.NonValidated.TryGetValues("Transfer-Encoding", out var unfilteredTransferEncoding))
        {
            // The typed collection skips invalid appended values. NonValidated retains them,
            // including an illegal second field following an otherwise valid chunked field.
            if (response.Content.Headers.Contains("Content-Length") || unfilteredTransferEncoding.Count != 1
                || !unfilteredTransferEncoding.Single().Trim(' ', '\t').Equals("chunked", StringComparison.OrdinalIgnoreCase))
                throw new PlaybackException("UnsupportedFormat");
            var transferEncoding = response.Headers.TransferEncoding;
            if (transferEncoding.Count != 1
                || !transferEncoding.Single().Value.Equals("chunked", StringComparison.OrdinalIgnoreCase)
                || transferEncoding.Single().Parameters.Count != 0)
                throw new PlaybackException("UnsupportedFormat");
        }
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)
            || response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException("UnsupportedFormat");
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (range is not null) throw new PlaybackException("UnsupportedFormat");
            return;
        }
        if (isHead || requestedRange is null || range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || !range.From.HasValue || !range.To.HasValue || range.To < range.From || range.To == long.MaxValue
            || (range.Length.HasValue && range.To >= range.Length)) throw new PlaybackException("UnsupportedFormat");
        var requested = requestedRange.Ranges.Single();
        if (requested.From.HasValue)
        {
            if (range.From != requested.From || requested.To.HasValue && range.To > requested.To)
                throw new PlaybackException("UnsupportedFormat");
        }
        else
        {
            if (!requested.To.HasValue || requested.To <= 0 || range.To - range.From + 1 > requested.To)
                throw new PlaybackException("UnsupportedFormat");
            if (range.Length.HasValue && (range.From != Math.Max(0, range.Length.Value - requested.To.Value)
                || range.To != range.Length - 1)) throw new PlaybackException("UnsupportedFormat");
        }
        if (response.Content.Headers.ContentLength is { } length && length != range.To - range.From + 1)
            throw new PlaybackException("UnsupportedFormat");
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

/// <summary>Holds a real HTTP response and bounded, serialized reads until cancellation or disposal drains them.</summary>
internal sealed partial class MediaStreamLease : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _operation;
    private readonly CancellationToken _operationToken;
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action _completeRequest;
    private readonly TimeSpan _readTimeout;
    private readonly long? _expectedLength;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _sync = new();
    private readonly TaskCompletionSource _readsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<Task> _dispose;
    private CancellationTokenRegistration _callerRegistration;
    private CancellationTokenRegistration _lifetimeRegistration;
    private int _activeReads;
    private long _bytesRead;
    private bool _disposed;
    private bool _resourcesDisposed;

    internal MediaStreamLease(HttpResponseMessage response, Stream stream, CancellationTokenSource operation,
        CancellationToken callerToken, CancellationToken lifetimeToken, Action completeRequest, bool isHead, TimeSpan readTimeout)
    {
        _response = response;
        _stream = stream;
        _operation = operation;
        _operationToken = operation.Token;
        _callerToken = callerToken;
        _lifetimeToken = lifetimeToken;
        _completeRequest = completeRequest;
        _readTimeout = readTimeout;
        StatusCode = response.StatusCode;
        ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        ContentLength = response.Content.Headers.ContentLength;
        ContentRange = response.Content.Headers.ContentRange?.ToString();
        var range = response.Content.Headers.ContentRange;
        _expectedLength = isHead || !IsSuccess ? 0 : ContentLength
            ?? (range is { From: { } from, To: { } to } ? to - from + 1 : null);
        var acceptRanges = response.Headers.AcceptRanges;
        if (acceptRanges.Count == 1 && acceptRanges.Single().ToLowerInvariant() is "bytes" or "none")
            AcceptRanges = acceptRanges.Single().ToLowerInvariant();
        _dispose = new Lazy<Task>(DisposeCoreAsync);
    }

    internal HttpStatusCode StatusCode { get; }
    internal string ContentType { get; }
    internal long? ContentLength { get; }
    internal string? ContentRange { get; }
    internal string? AcceptRanges { get; }
    internal bool IsSuccess => StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent;
    internal string? FailureCode => StatusCode switch
    {
        HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable => null,
        HttpStatusCode.Unauthorized => "AuthenticationRequired",
        HttpStatusCode.Forbidden => "NotAllowed",
        HttpStatusCode.UnsupportedMediaType => "UnsupportedFormat",
        _ => "NetworkFailure"
    };

    internal void AttachCancellation()
    {
        AttachRegistration(_callerToken, caller: true);
        AttachRegistration(_lifetimeToken, caller: false);
    }

    private void AttachRegistration(CancellationToken token, bool caller)
    {
        var registration = token.UnsafeRegister(static state => ((MediaStreamLease)state!).CancelFromOwner(), this);
        lock (_sync)
        {
            // A pre-canceled token can synchronously start and finish disposal before Register returns.
            if (!_resourcesDisposed)
            {
                if (caller) _callerRegistration = registration;
                else _lifetimeRegistration = registration;
                return;
            }
        }
        registration.Dispose();
    }

    private async void CancelFromOwner()
    {
        // The normal owner awaits the same disposal task and observes any cleanup error.
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    internal async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_disposed) throw new OperationCanceledException(_operationToken);
            _activeReads++;
        }
        var entered = false;
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct, _operationToken);
            read.CancelAfter(_readTimeout);
            await _readGate.WaitAsync(read.Token).ConfigureAwait(false);
            entered = true;
            if (buffer.Length == 0) return 0;
            if (_expectedLength is { } expected)
            {
                // Even a range with known bounds can arrive chunked. Confirm its real terminator;
                // otherwise a truncated chunked response could be mistaken for a complete range.
                buffer = buffer[..(int)Math.Min(buffer.Length, Math.Max(1, expected - _bytesRead))];
            }
            var count = await _stream.ReadAsync(buffer, read.Token).ConfigureAwait(false);
            if (count == 0 && _expectedLength.HasValue && _bytesRead != _expectedLength)
                throw new PlaybackException("NetworkFailure");
            if (_expectedLength.HasValue && count > _expectedLength.Value - _bytesRead)
                throw new PlaybackException("NetworkFailure");
            _bytesRead += count;
            return count;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (_callerToken.IsCancellationRequested) throw new OperationCanceledException(_callerToken);
            if (_lifetimeToken.IsCancellationRequested) throw new OperationCanceledException(_lifetimeToken);
            if (_operationToken.IsCancellationRequested) throw new OperationCanceledException(_operationToken);
            throw new PlaybackException("NetworkFailure");
        }
        catch (HttpRequestException) { throw new PlaybackException("NetworkFailure"); }
        catch (IOException) { throw new PlaybackException("NetworkFailure"); }
        catch (ObjectDisposedException)
        {
            if (_operationToken.IsCancellationRequested) throw new OperationCanceledException(_operationToken);
            throw new PlaybackException("NetworkFailure");
        }
        finally
        {
            if (entered) _readGate.Release();
            lock (_sync)
            {
                _activeReads--;
                if (_disposed && _activeReads == 0) _readsDrained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync() => new(_dispose.Value);

    private async Task DisposeCoreAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            if (_activeReads == 0) _readsDrained.TrySetResult();
        }
        Exception? failure = null;
        try { _operation.Cancel(); }
        catch (Exception error) { failure = error; }
        try { _response.Dispose(); }
        catch (Exception error) { failure ??= error; }
        try
        {
            await _readsDrained.Task.ConfigureAwait(false);
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        finally
        {
            CancellationTokenRegistration caller;
            CancellationTokenRegistration lifetime;
            lock (_sync)
            {
                _resourcesDisposed = true;
                caller = _callerRegistration;
                lifetime = _lifetimeRegistration;
            }
            await caller.DisposeAsync().ConfigureAwait(false);
            await lifetime.DisposeAsync().ConfigureAwait(false);
            _readGate.Dispose();
            _operation.Dispose();
            _completeRequest();
        }
        if (failure is not null) throw new PlaybackException("NetworkFailure");
    }

    public override string ToString() => $"{nameof(MediaStreamLease)} {{ StatusCode = {(int)StatusCode} }}";
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
