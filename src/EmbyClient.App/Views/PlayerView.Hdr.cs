using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    public HdrDisplayEligibilitySnapshot HdrDisplayEligibility { get; private set; } = new(
        "Auto", false, HdrMetadataConfigurationState.Pending);

    public event EventHandler? HdrDisplayEligibilityChanged;

    private Guid? _hdrDisplayConversionId;
    private long _nextHdrDisplayEligibilityRefresh;

    private readonly record struct NativePlaybackCapabilities(bool Hdr10OutputEligible,
        bool NativeEmbeddedSubtitlesEligible, bool NativeAssSubtitlesEligible, bool NativeAudioPassthroughEligible);

    private NativePlaybackCapabilities ReadCurrentNativeCapabilities()
    {
        var engine = _engine;
        HdrDisplayEligibilitySnapshot snapshot;
        if (engine is null)
        {
            snapshot = new(_preferences.HdrMode, false,
                _preferences.HdrMode == "Off" ? HdrMetadataConfigurationState.Disabled : HdrMetadataConfigurationState.Unavailable,
                UnavailableReason: "PlaybackEngineUnavailable");
        }
        else
        {
            engine.ConfigureHdrOutput(_preferences.HdrMode,
                App.CurrentWindow is MainWindow window ? window.AppWindow.Id.Value : 0);
            // This getter uses the same official display and native API checks as source opening.
            snapshot = engine.HdrDisplayEligibility;
        }
        if (snapshot != HdrDisplayEligibility)
        {
            HdrDisplayEligibility = snapshot;
            HdrDisplayEligibilityChanged?.Invoke(this, EventArgs.Empty);
        }
        return new NativePlaybackCapabilities(snapshot.Eligible, engine?.NativeEmbeddedSubtitlesEligible == true,
            engine?.NativeAssSubtitlesEligible == true, engine?.NativeAudioPassthroughEligible == true);
    }

    private PlaybackSelection WithCurrentNativeCapabilities(PlaybackSelection selection)
    {
        var capabilities = ReadCurrentNativeCapabilities();
        return selection with
        {
            Hdr10OutputEligible = capabilities.Hdr10OutputEligible,
            NativeEmbeddedSubtitlesEligible = capabilities.NativeEmbeddedSubtitlesEligible,
            NativeAssSubtitlesEligible = capabilities.NativeAssSubtitlesEligible,
            NativeAudioPassthroughEligible = capabilities.NativeAudioPassthroughEligible,
            AudioPassthroughMode = PreferredAudioPassthroughMode()
        };
    }

    private PlaybackSelectionChange WithCurrentNativeCapabilities(PlaybackSelectionChange change)
    {
        var capabilities = ReadCurrentNativeCapabilities();
        return change with
        {
            Hdr10OutputEligible = capabilities.Hdr10OutputEligible,
            NativeEmbeddedSubtitlesEligible = capabilities.NativeEmbeddedSubtitlesEligible,
            NativeAssSubtitlesEligible = capabilities.NativeAssSubtitlesEligible,
            NativeAudioPassthroughEligible = capabilities.NativeAudioPassthroughEligible,
            AudioPassthroughMode = change.AudioPassthroughMode ?? PreferredAudioPassthroughMode()
        };
    }

    private AudioPassthroughMode PreferredAudioPassthroughMode() => _preferences.AudioPassthrough switch
    {
        "DolbyDts" => AudioPassthroughMode.DolbyDts,
        "All" => AudioPassthroughMode.All,
        _ => AudioPassthroughMode.Off
    };

    public Task RefreshHdrDisplayEligibilityAsync()
    {
        if (DispatcherQueue.HasThreadAccess) return RefreshHdrDisplayEligibilityCoreAsync();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await RefreshHdrDisplayEligibilityCoreAsync(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new PlaybackException("PlayerDispatcherUnavailable"));
        return completion.Task;
    }

    private void RefreshHdrDisplayEligibilityWhenDue()
    {
        if (_coordinator?.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused
            or PlaybackStatus.Buffering or PlaybackStatus.Seeking)
            || Environment.TickCount64 < _nextHdrDisplayEligibilityRefresh) return;
        _ = RefreshHdrDisplayEligibilityAsync();
    }

    private async Task RefreshHdrDisplayEligibilityCoreAsync()
    {
        _nextHdrDisplayEligibilityRefresh = Environment.TickCount64 + 5_000;
        var capabilities = ReadCurrentNativeCapabilities();
        var coordinator = _coordinator;
        var engine = _engine;
        var session = _session;
        var context = coordinator?.ActiveContext;
        var native = engine?.Snapshot;
        var hasCurrentHdrOutput = context is not null && native?.PlaybackId == context.PlaybackId
            && engine?.HdrOutput is { NativeHdrMetadataEnabled: true, StreamHasHdrMetadata: true,
                StreamHdrMetadataApplied: true } hdr && hdr.PlaybackId == context.PlaybackId;
        if (capabilities.Hdr10OutputEligible || _expiredReported || session is null || engine is null
            || coordinator is null || context is null || native is null || native.PlaybackId != context.PlaybackId
            || native.State is not (PlaybackEngineState.Playing or PlaybackEngineState.Paused
                or PlaybackEngineState.Buffering or PlaybackEngineState.Seeking)
            || (context.DeliveryMethod != PlaybackDeliveryMethod.DirectStream && !hasCurrentHdrOutput)
            || !context.Selection.Hdr10OutputEligible || _hdrDisplayConversionId == context.PlaybackId
            || coordinator.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Buffering or PlaybackStatus.Seeking)
            || !(context.Source.MediaStreams ?? []).Any(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase)
                && Hdr10PlaybackPolicy.IsExplicitHdr10(stream))) return;

        var intent = _playIntent;
        var token = _playRequest?.Token ?? CancellationToken.None;
        _hdrDisplayConversionId = context.PlaybackId;
        bool OwnsRequest() => !token.IsCancellationRequested && !_expiredReported && intent == _playIntent
            && ReferenceEquals(session, _session) && ReferenceEquals(coordinator, _coordinator)
            && ReferenceEquals(engine, _engine) && coordinator.ActiveContext?.PlaybackId == context.PlaybackId;
        try
        {
            await RunAsync(async () =>
            {
                if (!OwnsRequest() || ReadCurrentNativeCapabilities().Hdr10OutputEligible) return;
                // Removing HDR metadata does not convert PQ video. Request fresh server conversion instead.
                await coordinator.ChangeSelectionAsync(WithCurrentNativeCapabilities(new PlaybackSelectionChange
                {
                    ForceTranscoding = true
                }), token);
            });
        }
        finally
        {
            if (_hdrDisplayConversionId == context.PlaybackId) _hdrDisplayConversionId = null;
            if (ReferenceEquals(engine, _engine)) ReadCurrentNativeCapabilities();
        }
    }
}
