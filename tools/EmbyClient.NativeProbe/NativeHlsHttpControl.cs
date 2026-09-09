using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Web.Http.Filters;
using NativeHttpProgress = Windows.Web.Http.HttpProgress;
using NativeHttpRequest = Windows.Web.Http.HttpRequestMessage;
using NativeHttpResponse = Windows.Web.Http.HttpResponseMessage;

namespace EmbyClient.NativeProbe;

internal sealed class NativeHlsHttpControlObservation
{
    private int _created;
    private int _disposed;
    private int _accepted;
    private int _rejected;

    internal void Created() => Interlocked.Increment(ref _created);
    internal void Disposed() => Interlocked.Increment(ref _disposed);
    internal void Accepted() => Interlocked.Increment(ref _accepted);
    internal void Rejected() => Interlocked.Increment(ref _rejected);

    internal NativeHlsHttpControlSample Capture() => new()
    {
        FiltersCreated = Volatile.Read(ref _created),
        FiltersDisposed = Volatile.Read(ref _disposed),
        RequestsAccepted = Volatile.Read(ref _accepted),
        RequestsRejected = Volatile.Read(ref _rejected)
    };
}

internal sealed class NativeHlsHttpControlSample
{
    public int FiltersCreated { get; init; }
    public int FiltersDisposed { get; init; }
    public int RequestsAccepted { get; init; }
    public int RequestsRejected { get; init; }
}

internal sealed class SharedNativePlayerControlSample
{
    public int PlayersCreated { get; init; }
    public int PlayersDisposed { get; init; }
    public int SessionAssignments { get; init; }
    public int DistinctPlaybackIds { get; init; }
    public int SourceClearChecks { get; init; }
}

/// <summary>Forwards native HTTP operations after a synchronous guard; it never wraps or observes their completion.</summary>
internal sealed partial class NativeHlsHttpControlFilter : IHttpFilter
{
    private readonly object _gate = new();
    private readonly string _originHost;
    private readonly Dictionary<string, string> _headers;
    private readonly NativeHlsHttpControlObservation _observation;
    private readonly HttpBaseProtocolFilter _inner;
    private bool _disposed;

    internal NativeHlsHttpControlFilter(PlaybackEngineRequest request, NativeHlsHttpControlObservation observation)
    {
        if (!IsOwnedEndpoint(request.MediaUri)) throw new PlaybackException("OwnedNativeHttpControlEndpointRequired");
        _originHost = request.MediaUri.Host;
        _headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        _observation = observation;
        _inner = new HttpBaseProtocolFilter
        {
            AllowAutoRedirect = false,
            AllowUI = false,
            UseProxy = false,
            AutomaticDecompression = false,
            CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies
        };
        _inner.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
        _inner.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
        _observation.Created();
    }

    public IAsyncOperationWithProgress<NativeHttpResponse, NativeHttpProgress> SendRequestAsync(NativeHttpRequest request)
    {
        lock (_gate)
        {
            if (_disposed || request.RequestUri is not { } uri || !IsOwnedEndpoint(uri)
                || !uri.Host.Equals(_originHost, StringComparison.OrdinalIgnoreCase)
                || !request.Method.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                _observation.Rejected();
                throw new PlaybackException("NativeHttpControlRequestRejected");
            }

            foreach (var header in _headers)
            {
                request.Headers.Remove(header.Key);
                if (!request.Headers.TryAppendWithoutValidation(header.Key, header.Value))
                {
                    _observation.Rejected();
                    throw new PlaybackException("NativeHttpControlHeaderRejected");
                }
            }
            request.Headers.Remove("Accept-Encoding");
            if (!request.Headers.TryAppendWithoutValidation("Accept-Encoding", "identity"))
            {
                _observation.Rejected();
                throw new PlaybackException("NativeHttpControlHeaderRejected");
            }
            _observation.Accepted();
            return _inner.SendRequestAsync(request);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _headers.Clear();
            _inner.Dispose();
            _observation.Disposed();
        }
    }

    private static bool IsOwnedEndpoint(Uri uri) => uri.IsAbsoluteUri && uri.IsLoopback && uri.Port == 19096
        && uri.Scheme == "http" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}
