using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Core;

namespace EmbyClient.App.Playback;

/// <summary>A real native image subtitle, raised on the player dispatcher with single-consumer bitmap ownership.</summary>
public sealed partial class NativeAssSubtitleFrameChangedEventArgs : EventArgs, IDisposable
{
    private NativeAssFrameLease? _frame;

    internal NativeAssSubtitleFrameChangedEventArgs(Guid playbackId, long presentationEpoch,
        NativeAssFrameMetadata metadata, NativeAssFrameLease? frame)
    {
        PlaybackId = playbackId;
        PresentationEpoch = presentationEpoch;
        Metadata = metadata;
        _frame = frame;
    }

    public Guid PlaybackId { get; }
    public long PresentationEpoch { get; }
    public bool HasImage => Metadata.HasImage;
    public ulong TrackId => Metadata.TrackId;
    public ulong Generation => Metadata.Generation;
    public ulong Sequence => Metadata.Sequence;
    public AssSubtitleGeometry Geometry => Metadata.Geometry;
    internal NativeAssFrameMetadata Metadata { get; }

    // Only one presenter may retain the independently owned snapshot after the event returns.
    internal NativeAssFrameLease? TakeFrame() => Interlocked.Exchange(ref _frame, null);
    public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Dispose();
}

public sealed partial class NativePlaybackEngine
{
    private long _assPresentationEpoch;

    public event EventHandler<NativeAssSubtitleFrameChangedEventArgs>? SubtitleFrameChanged;

    internal bool IsAssSubtitleFrameCurrent(Guid playbackId, long presentationEpoch) =>
        dispatcher.HasThreadAccess && Find(playbackId) is { } session
            && session.AssPresentationEpoch == presentationEpoch && session.AssPresentationTrack is not null;

    private void RefreshAssSubtitleFramePresentation(Session session, PlaybackEngineState? state = null)
    {
        try { RefreshAssSubtitleFramePresentationCore(session, state); }
        catch (Exception exception)
        {
            RecordSubtitleFailure("NativeImage/Read", exception);
            StopAssSubtitleFramePresentation(session);
            if (IsActive(session)) Fail(session, "UnsupportedSubtitle");
        }
    }

    private void RefreshAssSubtitleFramePresentationCore(Session session, PlaybackEngineState? state)
    {
        if (!IsActive(session)) return;
        var snapshot = Snapshot;
        if (state is null && snapshot?.PlaybackId == session.Request.PlaybackId) state = snapshot.State;
        var terminal = state is PlaybackEngineState.Ended or PlaybackEngineState.Failed or PlaybackEngineState.Stopped;
        if (terminal || SubtitleFrameChanged is null || !session.IsOpened || !session.NativeSubtitlePresentationActive
            || session.DecoderSource is null || !NativeAssInterop.IsFrameBridgeAvailable)
        {
            StopAssSubtitleFramePresentation(session);
            return;
        }
        // A confirmed seek refreshes from the real player position before presentation resumes.
        if (session.UserSeeking || session.InitialSeekPending) return;
        var track = NativePlatformSubtitleTrack(session);
        if (track?.TimedMetadataKind != TimedMetadataKind.ImageSubtitle)
        {
            StopAssSubtitleFramePresentation(session);
            return;
        }
        if (session.AssPresentationTrack != track)
        {
            InvalidateAssSubtitleImage(session, force: true);
            session.AssPresentationTrack = track;
        }
        if (session.AssFrameTimer is null)
        {
            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000d / 30);
            timer.IsRepeating = true;
            session.AssFrameTimer = timer;
            session.AssFrameTimerTick = (_, _) =>
            {
                if (!IsActive(session)) return;
                RefreshAssSubtitleFramePresentation(session, Snapshot?.State);
            };
            timer.Tick += session.AssFrameTimerTick;
            timer.Start();
        }

        var metadata = NativeAssInterop.ReadFrame(session.DecoderSource);
        var previous = session.LastAssFrame;
        if (previous is { } known && metadata.TrackId == known.TrackId
            && metadata.Generation == known.Generation && metadata.Sequence == known.Sequence
            && metadata.HasImage == known.HasImage && metadata.PresentationMode == known.PresentationMode) return;
        var visible = metadata.HasImage && metadata.PresentationMode == 2;
        if (previous is { } identity && (metadata.TrackId != identity.TrackId || metadata.Generation != identity.Generation
            || !visible && identity.HasImage))
            InvalidateAssSubtitleImage(session, force: true);

        if (!visible)
        {
            session.LastAssFrame = metadata;
            PublishAssSubtitleFrame(session, metadata with { HasImage = false }, null);
            return;
        }

        var frame = NativeAssInterop.AcquireFrame(session.DecoderSource, metadata.Sequence);
        if (frame is null) return;
        try
        {
            if (!IsActive(session) || session.UserSeeking || frame.Metadata.PresentationMode != 2) return;
            session.LastAssFrame = frame.Metadata;
            PublishAssSubtitleFrame(session, frame.Metadata, frame);
            frame = null;
        }
        finally { frame?.Dispose(); }
    }

    private void PublishAssSubtitleFrame(Session session, NativeAssFrameMetadata metadata, NativeAssFrameLease? frame)
    {
        using var args = new NativeAssSubtitleFrameChangedEventArgs(session.Request.PlaybackId,
            session.AssPresentationEpoch, metadata, frame);
        try { SubtitleFrameChanged?.Invoke(this, args); }
        catch (Exception exception) { RecordSubtitleFailure("AssImage/Presenter", exception); }
    }

    private void InvalidateAssSubtitleImage(Session session, bool force = false)
    {
        if (!force && session.AssPresentationTrack is null && session.LastAssFrame is null) return;
        session.AssPresentationEpoch = ++_assPresentationEpoch;
        session.LastAssFrame = null;
        PublishAssSubtitleFrame(session, new(false, 0, 0, 0, 0, -1, -1, default, 0), null);
    }

    private void StopAssSubtitleFramePresentation(Session session)
    {
        if (session.AssFrameTimer is { } timer)
        {
            Release(session, "StopAssImageTimer", timer.Stop);
            Release(session, "UnsubscribeAssImageTimer", () => timer.Tick -= session.AssFrameTimerTick);
            session.AssFrameTimer = null;
            session.AssFrameTimerTick = null;
        }
        InvalidateAssSubtitleImage(session);
        session.AssPresentationTrack = null;
    }

    internal void ReportAssSubtitlePresentationFailure(Guid playbackId, long presentationEpoch, Exception exception)
    {
        if (!IsAssSubtitleFrameCurrent(playbackId, presentationEpoch) || Find(playbackId) is not { } session) return;
        RecordSubtitleFailure("AssImage/Upload", exception);
        Fail(session, "UnsupportedSubtitle");
    }

    internal void RecordAssSubtitleImageUpload(Guid playbackId, long presentationEpoch, NativeAssFrameMetadata metadata)
    {
        if (!IsAssSubtitleFrameCurrent(playbackId, presentationEpoch) || Find(playbackId) is not { } session) return;
        ++session.AssImageUploadCount;
        var now = Environment.TickCount64;
        if (session.LastAssImageUploadLogTick is { } previous && now - previous < 1000) return;
        session.LastAssImageUploadLogTick = now;
        RecordSubtitleDiagnostic(FormattableString.Invariant(
            $"NativeImageUpload frames={session.AssImageUploadCount} sequence={metadata.Sequence} generation={metadata.Generation} mediaTicks={metadata.MediaPositionTicks} scriptTicks={metadata.ScriptPositionTicks} alphaPixels={metadata.NonzeroAlphaPixels} rect={metadata.Geometry.X},{metadata.Geometry.Y},{metadata.Geometry.Width},{metadata.Geometry.Height} video={metadata.Geometry.VideoWidth},{metadata.Geometry.VideoHeight}"));
    }

    private sealed partial class Session
    {
        public DispatcherQueueTimer? AssFrameTimer { get; set; }
        public TypedEventHandler<DispatcherQueueTimer, object>? AssFrameTimerTick { get; set; }
        public TimedMetadataTrack? AssPresentationTrack { get; set; }
        public long AssPresentationEpoch { get; set; }
        public NativeAssFrameMetadata? LastAssFrame { get; set; }
        public ulong AssImageUploadCount { get; set; }
        public long? LastAssImageUploadLogTick { get; set; }
    }
}
