using EmbyClient.NativeProbe;
using EmbyClient.Playback;
using Windows.Media.Playback;
using NativeHttpClient = Windows.Web.Http.HttpClient;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    internal bool HasAdaptiveCreationResponseForProbe => _current?.AdaptiveCreationResponse is not null;
    internal bool HasProductAdaptiveFilterForProbe => _current?.AdaptiveFilter is not null;
    internal NativeHlsHttpControlObservation? NativeHttpControlObservation { get; init; }
    internal bool ReuseNativeHttpControlPlayer { get; init; }
    private MediaPlayer? _sharedNativeControlPlayer;
    private int _sharedNativeControlCreated;
    private int _sharedNativeControlDisposed;
    private int _sharedNativeControlAssignments;
    private int _sharedNativeControlSourceClearChecks;
    private readonly HashSet<Guid> _sharedNativeControlPlaybackIds = [];

    internal bool SharedNativeControlSourceBoundForProbe => _sharedNativeControlPlayer is { Source: not null }
        && ReferenceEquals(_current?.Player, _sharedNativeControlPlayer);
    internal bool SharedNativeControlSourceClearedForProbe => _sharedNativeControlPlayer is { Source: null };

    internal SharedNativePlayerControlSample CaptureSharedNativePlayerControl() => new()
    {
        PlayersCreated = _sharedNativeControlCreated,
        PlayersDisposed = _sharedNativeControlDisposed,
        SessionAssignments = _sharedNativeControlAssignments,
        DistinctPlaybackIds = _sharedNativeControlPlaybackIds.Count,
        SourceClearChecks = _sharedNativeControlSourceClearChecks
    };

    internal void DisposeSharedNativePlayerControl()
    {
        if (_sharedNativeControlPlayer is not { } player) return;
        var sourceWasBound = false;
        try { sourceWasBound = player.Source is not null; }
        finally
        {
            _sharedNativeControlPlayer = null;
            player.Dispose();
            _sharedNativeControlDisposed++;
        }
        if (sourceWasBound) throw new PlaybackException("SharedPlayerSourceStillBoundAtFinalDispose");
    }

    partial void CreateSessionPlayer(PlaybackEngineRequest request, ref MediaPlayer? player, ref bool ownsPlayer)
    {
        AttachOpeningCancellationForProbe(request);
        if (!ReuseNativeHttpControlPlayer) return;
        if (!request.MediaUri.IsLoopback || request.MediaUri.Port != 19096 || request.MediaUri.Scheme != "http"
            || request.MediaUri.UserInfo.Length != 0)
            throw new PlaybackException("OwnedSharedPlayerControlEndpointRequired");
        if (_sharedNativeControlPlayer is null)
        {
            _sharedNativeControlPlayer = new MediaPlayer();
            _sharedNativeControlCreated++;
        }
        if (_sharedNativeControlPlayer.Source is not null) throw new PlaybackException("SharedPlayerPreviousSourceNotCleared");
        _sharedNativeControlSourceClearChecks++;
        _sharedNativeControlAssignments++;
        if (!_sharedNativeControlPlaybackIds.Add(request.PlaybackId)) throw new PlaybackException("SharedPlayerPlaybackIdReused");
        player = _sharedNativeControlPlayer;
        ownsPlayer = false;
    }

    partial void CreateAdaptiveHttpClient(PlaybackEngineRequest request, ref NativeHttpClient? client,
        ref IDisposable? filterOwner)
    {
        if (NativeHttpControlObservation is not { } observation) return;
        var filter = new NativeHlsHttpControlFilter(request, observation);
        try
        {
            client = new NativeHttpClient(filter);
            filterOwner = filter;
        }
        catch
        {
            filter.Dispose();
            throw;
        }
    }
}
