using Windows.Foundation;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Statistics directly reported by the Windows playback session. Progress is a fraction, not seconds.</summary>
public sealed record NativePlaybackStatisticsSnapshot(Guid PlaybackId, double? BufferingProgress,
    double? NativeVideoFramesPerSecond)
{
    /// <summary>The reported position on the native media URL's timeline, before any Emby timeline offset.</summary>
    public long? NativeUrlPositionTicks { get; init; }

    /// <summary>Continuous media time buffered ahead at NativeUrlPositionTicks, never inferred from progress or playback rate.</summary>
    public TimeSpan? BufferedDuration { get; init; }
}

public sealed partial class NativePlaybackEngine
{
    private NativePlaybackStatisticsSnapshot? _playbackStatistics;

    public NativePlaybackStatisticsSnapshot? PlaybackStatistics => Volatile.Read(ref _playbackStatistics);

    private void InitializePlaybackStatistics(Session session)
    {
        EnsureActive(session);
        if (session.NativeSession is not { } native) return;
        Interlocked.Exchange(ref session.PlaybackStatisticsRetired, 0);
        Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0);
        session.PlaybackStatisticsChanged = (_, _) => QueuePlaybackStatisticsRefresh(session);
        native.BufferedRangesChanged += session.PlaybackStatisticsChanged;
        native.BufferingProgressChanged += session.PlaybackStatisticsChanged;
    }

    private void QueuePlaybackStatisticsRefresh(Session session)
    {
        if (Volatile.Read(ref session.PlaybackStatisticsRetired) != 0
            || Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 1) != 0) return;
        if (Volatile.Read(ref session.PlaybackStatisticsRetired) != 0)
        {
            Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0);
            return;
        }
        try
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                // Coalesce adjacent native events; permit one next refresh if ranges change while this one reads.
                Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0);
                try
                {
                    if (Volatile.Read(ref session.PlaybackStatisticsRetired) == 0 && IsActive(session))
                        RefreshPlaybackStatistics(session);
                }
                catch { /* Statistics availability never interrupts playback. */ }
            })) Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0);
        }
        catch { Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0); }
    }

    private void RetirePlaybackStatistics(Session session)
    {
        Interlocked.Exchange(ref session.PlaybackStatisticsRetired, 1);
        Interlocked.Exchange(ref session.PlaybackStatisticsRefreshQueued, 0);
        var changed = session.PlaybackStatisticsChanged;
        session.PlaybackStatisticsChanged = null;
        if (session.NativeSession is not { } native || changed is null) return;
        Release(session, "UnsubscribeBufferedRanges", () => native.BufferedRangesChanged -= changed);
        Release(session, "UnsubscribeBufferingProgress", () => native.BufferingProgressChanged -= changed);
    }

    private void RefreshPlaybackStatistics(Session session)
    {
        if (!IsActive(session)) return;
        double? buffering = null;
        double? frameRate = null;
        long? nativeUrlPosition = null;
        TimeSpan? bufferedDuration = null;
        try
        {
            if (session.IsOpened && session.NativeSession is { } native)
            {
                var progress = native.BufferingProgress;
                if (double.IsFinite(progress) && progress is >= 0 and <= 1) buffering = progress;
            }
        }
        catch
        {
            // Missing statistics never change playback, and no duration is inferred from this fraction.
        }
        try
        {
            if (session.IsOpened && session.NativeSession is { } native)
            {
                var position = native.Position.Ticks;
                if (position >= 0)
                {
                    nativeUrlPosition = position;
                    var ranges = native.GetBufferedRanges();
                    bufferedDuration = BufferedPlaybackRangePolicy.GetBufferedAhead(position,
                        ranges.Select(range => new BufferedPlaybackRange(range.Start.Ticks, range.End.Ticks)).ToArray());
                }
            }
        }
        catch
        {
            // Some source pipelines do not expose buffered ranges. Keep duration unavailable for those sources.
        }
        try
        {
            if (session.IsOpened && session.DecoderSource?.CurrentVideoStream is { } video
                && video.PixelWidth > 0 && video.PixelHeight > 0 && double.IsFinite(video.FramesPerSecond)
                && video.FramesPerSecond is > 0 and <= 1000)
                frameRate = video.FramesPerSecond;
        }
        catch
        {
            // This is the native stream's nominal frame rate, never a measured presentation cadence.
        }
        if (!IsActive(session)) return;
        Volatile.Write(ref _playbackStatistics, new NativePlaybackStatisticsSnapshot(session.Request.PlaybackId, buffering, frameRate)
        {
            NativeUrlPositionTicks = nativeUrlPosition,
            BufferedDuration = bufferedDuration
        });
    }

    private sealed partial class Session
    {
        public TypedEventHandler<MediaPlaybackSession, object>? PlaybackStatisticsChanged { get; set; }
        public int PlaybackStatisticsRefreshQueued;
        public int PlaybackStatisticsRetired;
    }
}
