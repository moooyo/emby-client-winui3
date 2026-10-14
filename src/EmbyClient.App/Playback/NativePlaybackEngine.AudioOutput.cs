using EmbyClient.Playback;
using FFmpegInteropX;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace EmbyClient.App.Playback;

public enum AudioOutputState { Off, Pending, Bitstream, Pcm, FallbackRequired, Stopped }

/// <summary>Describes the native output that has actually accepted compressed audio for this playback.</summary>
public sealed record AudioOutputSnapshot(Guid PlaybackId, AudioPassthroughMode RequestedMode,
    AudioOutputState State, int? StreamIndex, string SourceCodec, string? OutputFormat = null,
    string? EndpointName = null, int TransportSampleRate = 0, uint TransportChannels = 0,
    uint ContentSampleRate = 0, uint ContentChannels = 0, bool ActualFormatConfirmed = false,
    ulong Generation = 0, ulong SubmittedFrameCount = 0, long DriftTicks = 0, long ClockPositionTicks = 0,
    string? FallbackReason = null)
{
    public bool IsBitstream => State == AudioOutputState.Bitstream && ActualFormatConfirmed
        && SubmittedFrameCount > 0;
}

public sealed partial class NativePlaybackEngine
{
    private AudioOutputSnapshot? _audioOutput;

    public AudioOutputSnapshot? AudioOutput => Volatile.Read(ref _audioOutput);
    public static bool AudioPassthroughAvailable => NativeAudioInterop.IsAvailable;
    public bool NativeAudioPassthroughEligible => dispatcher.HasThreadAccess && !_disposed
        && NativeAudioInterop.IsAvailable;

    /// <summary>Attempts exclusive output before the playback item chooses its audio descriptors.</summary>
    private bool PrepareNativeAudioOutput(Session session, FFmpegMediaSource source)
    {
        EnsureActive(session);
        var requested = session.Request.AudioPassthroughMode;
        var index = session.Request.AudioStreamIndex ?? session.Request.Source.DefaultAudioStreamIndex;
        var metadata = (session.Request.Source.MediaStreams ?? []).Where(candidate =>
            string.Equals(candidate.Type, "Audio", StringComparison.OrdinalIgnoreCase) && candidate.Index == index).ToArray();
        var stream = metadata.Length == 1 ? metadata[0] : null;
        session.AudioOutputSnapshot = new(session.Request.PlaybackId, session.AudioRecoveryRequestedMode ?? requested,
            AudioOutputState.Pcm, index,
            stream?.Codec?.ToUpperInvariant() ?? "Unknown",
            FallbackReason: session.AudioRecoveryFallbackReason);
        if (source.AudioStreams.Count == 0)
        {
            session.AudioOutputSnapshot = session.AudioOutputSnapshot with
            {
                State = AudioOutputState.Off,
                FallbackReason = null
            };
            PublishAudioOutputSnapshot(session);
            return false;
        }
        if (requested == AudioPassthroughMode.Off)
        {
            PublishAudioOutputSnapshot(session);
            return false;
        }
        string? earlyReason = null;
        if (session.Request.DeliveryMethod != PlaybackDeliveryMethod.DirectStream
            || session.Request.TimelineKind != PlaybackTimelineKind.FullSource
            || MediaDeliveryClassifier.IsHls(session.Request))
            earlyReason = "DeliveryUnsupported";
        else if (!NativeAudioInterop.IsAvailable)
            earlyReason = "NativeAudioApiUnavailable";
        else if (!session.Request.NativeAudioPassthroughEligible)
            earlyReason = "PassthroughNotEligible";
        else if (metadata.Length > 1)
            earlyReason = "AudioStreamMappingUnavailable";
        else if (!AudioPassthroughPolicy.SupportsUnchangedBitstreamControls(
            _volumeLevel, _isMuted, Volatile.Read(ref _desiredPlaybackRate)))
            earlyReason = _isMuted ? "MuteRequested" : _volumeLevel != 100 ? "VolumeRequested" : "RateUnsupported";
        else if (index is null || stream is null)
            earlyReason = "MetadataUnavailable";
        else if (!AudioPassthroughPolicy.IsCodecAllowed(requested, stream.Codec, stream.Profile))
            earlyReason = "CodecUnsupported";
        else
        {
            // Container indices must identify exactly one native stream before suppressing PCM output.
            var matching = source.AudioStreams.Where(candidate => candidate.StreamIndex == index.Value).ToArray();
            if (matching.Length != 1 || !SameAudioCodec(stream.Codec, matching[0].CodecName))
                earlyReason = "AudioStreamMappingUnavailable";
        }
        if (earlyReason is not null)
        {
            SetEarlyPcmAudioOutput(session, earlyReason);
            return false;
        }
        var opened = NativeAudioInterop.TryOpenBitstream(source, requested, index!.Value);
        if (!opened.ExclusiveOpened || opened.Observation is not { } observation)
        {
            SetEarlyPcmAudioOutput(session, opened.FallbackReason ?? "AudioOutputOpenFailed");
            return false;
        }
        session.NativeAudioOutputOpened = true;
        session.NativeAudioGeneration = observation.Generation;
        session.AudioOutputSnapshot = AudioSnapshot(session, observation);
        PublishAudioOutputSnapshot(session);
        return true;
    }

    private static bool SameAudioCodec(string? serverCodec, string? nativeCodec) =>
        AudioCodecFamily(serverCodec) is { } family && family == AudioCodecFamily(nativeCodec);

    private static string? AudioCodecFamily(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "ac3" or "ac-3" or "ac3_fixed" or "ac3_float" => "AC3",
        "eac3" or "e-ac-3" => "EAC3",
        "dts" or "dca" or "dts-hd" or "dtshd" or "dts_hd" => "DTS",
        "truehd" or "true-hd" or "true_hd" => "TrueHD",
        _ => null
    };

    private void SetEarlyPcmAudioOutput(Session session, string reason)
    {
        session.NativeAudioOutputOpened = false;
        session.AudioOutputSnapshot = session.AudioOutputSnapshot! with
        {
            State = AudioOutputState.Pcm,
            FallbackReason = reason,
            ActualFormatConfirmed = false
        };
        PublishAudioOutputSnapshot(session);
        ReportAudioOutputDiagnostic(session.Request.PlaybackId, "AudioOutput/Open", reason);
    }

    /// <summary>The native provider follows the source's MediaPlaybackSession binding as its only transport.</summary>
    private void InitializeNativeAudioOutput(Session session)
    {
        EnsureActive(session);
        if (!session.NativeAudioOutputOpened) return;
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        session.AudioOutputObservationTick = (_, _) =>
        {
            if (!IsActive(session)) return;
            RefreshAudioOutput(session);
            TryCompleteOpen(session);
        };
        session.AudioOutputObservationTimer = timer;
        timer.Tick += session.AudioOutputObservationTick;
        timer.Start();
        RefreshAudioOutput(session);
    }

    private void RefreshAudioOutput(Session session)
    {
        if (!IsActive(session)) return;
        if (!session.NativeAudioOutputOpened || session.AudioFallbackTask is not null)
        {
            PublishAudioOutputSnapshot(session);
            return;
        }
        if (session.DecoderSource is not { } source)
        {
            BeginPcmAudioRecovery(session, "AudioObservationUnavailable");
            return;
        }
        var observation = NativeAudioInterop.Read(source);
        if (observation is null || observation.Generation == 0
            || observation.Generation < session.NativeAudioGeneration
            || observation.RequestedMode != session.Request.AudioPassthroughMode
            || observation.StreamIndex != session.AudioOutputSnapshot?.StreamIndex)
        {
            BeginPcmAudioRecovery(session, "AudioObservationUnavailable");
            return;
        }
        // Flush and seek advance the same provider's queue generation; retired sessions remain excluded.
        session.NativeAudioGeneration = observation.Generation;
        session.AudioOutputSnapshot = AudioSnapshot(session, observation);
        PublishAudioOutputSnapshot(session);
        if (observation.State is AudioOutputState.FallbackRequired or AudioOutputState.Pcm or AudioOutputState.Off
            || observation.State == AudioOutputState.Stopped && session.NativeSession?.PlaybackState
                is not (Windows.Media.Playback.MediaPlaybackState.None or Windows.Media.Playback.MediaPlaybackState.Paused))
            BeginPcmAudioRecovery(session, observation.FallbackReason ?? "AudioOutputFailure");
    }

    private static AudioOutputSnapshot AudioSnapshot(Session session, NativeAudioObservation observation)
    {
        var state = observation.State == AudioOutputState.Bitstream
            && (!observation.ActualFormatConfirmed || observation.SubmittedFrameCount == 0)
            ? AudioOutputState.Pending : observation.State;
        return new(session.Request.PlaybackId, observation.RequestedMode, state, observation.StreamIndex,
            observation.SourceCodecName, observation.OutputFormatName, observation.EndpointName,
            observation.TransportSampleRate, observation.TransportChannels, observation.ContentSampleRate,
            observation.ContentChannels, observation.ActualFormatConfirmed, observation.Generation,
            observation.SubmittedFrameCount, observation.DriftTicks, observation.ClockPositionTicks,
            observation.FallbackReason);
    }

    private void PublishAudioOutputSnapshot(Session session)
    {
        if (IsActive(session) && session.AudioOutputSnapshot is { } observation)
            Volatile.Write(ref _audioOutput, observation);
    }

    /// <summary>Waits for ordinary PCM output before applying controls that cannot change encoded bursts.</summary>
    private async Task EnsurePcmAudioOutputAsync(Guid playbackId, string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitForAudioRecoveryAsync(playbackId, cancellationToken).ConfigureAwait(false);
        var recovery = await OnDispatcherAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Find(playbackId) is not { } session) return Task.CompletedTask;
            if (session.AudioFallbackTask is { } pending) return pending;
            return session.NativeAudioOutputOpened ? BeginPcmAudioRecovery(session, reason) : Task.CompletedTask;
        }).ConfigureAwait(false);
        await recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BeginPcmAudioRecovery(Session session, string reason)
    {
        if (session.AudioFallbackTask is { } pending) return pending;
        if (!IsActive(session) || !session.NativeAudioOutputOpened) return Task.CompletedTask;
        SuspendNativeAudioOutput(session);
        if (session.AudioOutputSnapshot is { } observation)
        {
            session.AudioOutputSnapshot = observation with
            {
                State = AudioOutputState.FallbackRequired,
                ActualFormatConfirmed = false,
                FallbackReason = reason
            };
            PublishAudioOutputSnapshot(session);
        }
        ReportAudioOutputDiagnostic(session.Request.PlaybackId, "AudioOutput/RecoverPcm", reason);
        var recovery = RecoverAudioOutputAsPcmAsync(session, reason);
        session.AudioFallbackTask = recovery;
        _ = ObserveAudioOutputRecoveryAsync(recovery);
        return recovery;
    }

    private static async Task ObserveAudioOutputRecoveryAsync(Task recovery)
    {
        try { await recovery.ConfigureAwait(false); }
        catch { }
    }

    private async Task RecoverAudioOutputAsPcmAsync(Session session, string reason)
    {
        // Register the one recovery task before it can retire this session or process more native events.
        await Task.Yield();
        try
        {
            if (!await OnDispatcherAsync(() => IsActive(session)).ConfigureAwait(false)) return;
            await ReopenAudioAsPcmAsync(session, reason).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
        catch (PlaybackException exception)
        {
            await OnDispatcherAsync(() =>
            {
                if (IsActive(session)) Fail(session, exception.ErrorCode);
                else ReportAudioOutputDiagnostic(session.Request.PlaybackId, "AudioOutput/RecoverPcm", exception.ErrorCode);
            }).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await OnDispatcherAsync(() =>
            {
                if (IsActive(session)) Fail(session, "AudioOutputRecoveryFailed");
                else ReportAudioOutputDiagnostic(session.Request.PlaybackId, "AudioOutput/RecoverPcm", "AudioOutputRecoveryFailed");
            }).ConfigureAwait(false);
            throw new PlaybackException("AudioOutputRecoveryFailed");
        }
    }

    private void SuspendNativeAudioOutput(Session session)
    {
        if (!session.NativeAudioOutputOpened || session.NativeAudioOutputSuspended) return;
        session.NativeAudioOutputSuspended = true;
        if (session.DecoderSource is { } source && !NativeAudioInterop.Suspend(source))
            NativeAudioInterop.Close(source);
    }

    private void ReportAudioOutputDiagnostic(Guid playbackId, string operation, string reason)
    {
        try { Diagnostic?.Invoke(this, new(playbackId, operation, reason)); }
        catch { }
    }

    private void RetireNativeAudioOutput(Session session)
    {
        if (session.AudioOutputObservationTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= session.AudioOutputObservationTick;
        }
        session.AudioOutputObservationTick = null;
        session.AudioOutputObservationTimer = null;
        if (session.NativeAudioOutputOpened && session.DecoderSource is { } source)
            Release(session, "CloseNativeAudioOutput", () =>
            {
                session.NativeAudioCleanupSucceeded = NativeAudioInterop.Close(source);
                if (!session.NativeAudioCleanupSucceeded) throw new PlaybackException("AudioOutputCleanupPending");
            });
        session.NativeAudioOutputOpened = false;
        session.NativeAudioOutputSuspended = true;
        if (ReferenceEquals(_current, session) && session.AudioOutputSnapshot is { } observation)
            Volatile.Write(ref _audioOutput, observation with
            {
                State = AudioOutputState.Stopped,
                ActualFormatConfirmed = false
            });
    }

    private sealed partial class Session
    {
        public bool NativeAudioOutputOpened { get; set; }
        public bool NativeAudioCleanupSucceeded { get; set; } = true;
        public bool NativeAudioOutputSuspended { get; set; }
        public ulong NativeAudioGeneration { get; set; }
        public AudioOutputSnapshot? AudioOutputSnapshot { get; set; }
        public Task? AudioFallbackTask { get; set; }
        public DispatcherQueueTimer? AudioOutputObservationTimer { get; set; }
        public TypedEventHandler<DispatcherQueueTimer, object>? AudioOutputObservationTick { get; set; }
    }
}
