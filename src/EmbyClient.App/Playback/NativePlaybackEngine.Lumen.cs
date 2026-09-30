using System.Diagnostics.CodeAnalysis;
using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Application-presented external text, raised on the player dispatcher.</summary>
public sealed class NativeSubtitleTextChangedEventArgs(
    Guid playbackId, string text, double fontSize, bool outline, string position, double delaySeconds) : EventArgs
{
    public Guid PlaybackId { get; } = playbackId;
    public string Text { get; } = text;
    public double FontSize { get; } = fontSize;
    public bool Outline { get; } = outline;
    public string Position { get; } = position;
    public double DelaySeconds { get; } = delaySeconds;
}

public sealed partial class NativePlaybackEngine
{
    private const double MinimumPlaybackRate = .5;
    private const double MaximumPlaybackRate = 2;
    private const int MaximumSubtitleCues = 50_000;
    private double _desiredPlaybackRate = 1;
    private long _playbackRateChangeSequence;
    private bool _supportsPlaybackRate;
    private bool _supportsSubtitlePreferences;
    private int _videoWidth;
    private int _videoHeight;
    private double _subtitleFontSize = 30;
    private bool _subtitleOutline = true;
    private string _subtitlePosition = "Bottom";
    private double _subtitleDelaySeconds;

    /// <summary>True only when the current native source supports the complete offered rate range.</summary>
    public bool SupportsPlaybackRate => Volatile.Read(ref _supportsPlaybackRate);

    /// <summary>The actual rate of the current snapshot, or the preferred rate before the first source.</summary>
    public double PlaybackRate => Snapshot?.PlaybackRate ?? Volatile.Read(ref _desiredPlaybackRate);

    /// <summary>Burned subtitles have no editable text. The application must provide a subtitle presenter.</summary>
    public bool SupportsSubtitlePreferences => Volatile.Read(ref _supportsSubtitlePreferences);

    /// <summary>Current native video width in pixels; zero before opening or when no video is active.</summary>
    public int VideoWidth => Volatile.Read(ref _videoWidth);

    /// <summary>Current native video height in pixels; zero before opening or when no video is active.</summary>
    public int VideoHeight => Volatile.Read(ref _videoHeight);

    /// <summary>
    /// The consumer must recheck PlaybackId before updating its subtitle layer. Subscribing before OpenAsync
    /// selects application presentation and prevents Windows from drawing duplicate external subtitles.
    /// </summary>
    public event EventHandler<NativeSubtitleTextChangedEventArgs>? SubtitleTextChanged;

    public Task SetPlaybackRateAsync(Guid playbackId, double rate, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(rate) || rate is < MinimumPlaybackRate or > MaximumPlaybackRate)
            throw new ArgumentOutOfRangeException(nameof(rate));
        return ControlAsync(playbackId, session =>
        {
            var native = session.NativeSession!;
            if (rate > 1 && session.Item?.CanSkip == false || !native.IsSupportedPlaybackRateRange(rate, rate))
                throw new PlaybackException("PlaybackRateNotSupported");
            var previousRate = Volatile.Read(ref _desiredPlaybackRate);
            var sequence = ++_playbackRateChangeSequence;
            Volatile.Write(ref _desiredPlaybackRate, rate);
            try { native.PlaybackRate = rate; }
            catch
            {
                if (sequence == _playbackRateChangeSequence) Volatile.Write(ref _desiredPlaybackRate, previousRate);
                if (!IsActive(session)) return;
                throw new PlaybackException("PlaybackRateNotSupported");
            }
            if (!IsActive(session) || sequence != _playbackRateChangeSequence) return;
            RefreshPlaybackRateSupport(session);
            Publish(session, PlaybackEngineEventKind.StateChanged);
        }, cancellationToken);
    }

    /// <summary>
    /// The application presenter consumes the style values from SubtitleTextChanged. A positive delay displays
    /// text later; matching uses the native media clock, not elapsed wall time, so pause, seek, and rate stay aligned.
    /// </summary>
    public Task ApplySubtitlePreferencesAsync(Guid playbackId, double fontSize, bool outline, string position,
        double delaySeconds, CancellationToken cancellationToken = default)
    {
        if (fontSize is not (24 or 30 or 38)) throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (position is not ("Bottom" or "BlackBars")) throw new ArgumentOutOfRangeException(nameof(position));
        if (!double.IsFinite(delaySeconds) || delaySeconds is < -10 or > 10)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        return ControlAsync(playbackId, session =>
        {
            if (!CanPresentExternalText(session)) throw new PlaybackException("SubtitlePreferencesNotSupported");
            _subtitleFontSize = fontSize;
            _subtitleOutline = outline;
            _subtitlePosition = position;
            _subtitleDelaySeconds = Math.Round(delaySeconds, 1, MidpointRounding.AwayFromZero);
            RefreshSubtitlePresentation(session, force: true);
        }, cancellationToken);
    }

    private void InitializeLumenControls(Session session)
    {
        Volatile.Write(ref _supportsPlaybackRate, false);
        Volatile.Write(ref _supportsSubtitlePreferences, false);
        Volatile.Write(ref _videoWidth, 0);
        Volatile.Write(ref _videoHeight, 0);
        session.PlaybackRateChanged = (_, _) => Queue(() =>
        {
            if (!IsActive(session)) return;
            RefreshPlaybackRateSupport(session);
            Publish(session, PlaybackEngineEventKind.StateChanged);
            RefreshSubtitlePresentation(session);
        });
        session.SupportedPlaybackRatesChanged = (_, _) => Queue(() =>
        {
            if (!IsActive(session)) return;
            if (session.IsOpened) RestorePreferredPlaybackRate(session);
            else RefreshPlaybackRateSupport(session);
            if (IsActive(session)) Publish(session, PlaybackEngineEventKind.StateChanged);
        });
        session.VideoSizeChanged = (_, _) => Queue(() =>
        {
            if (!IsActive(session)) return;
            if (RefreshVideoSize(session) && IsActive(session)) Publish(session, PlaybackEngineEventKind.StateChanged);
        });
        session.NativeSession!.PlaybackRateChanged += session.PlaybackRateChanged;
        session.NativeSession.SupportedPlaybackRatesChanged += session.SupportedPlaybackRatesChanged;
        session.NativeSession.NaturalVideoSizeChanged += session.VideoSizeChanged;
    }

    private bool RefreshVideoSize(Session session)
    {
        if (!IsActive(session)) return false;
        var width = 0;
        var height = 0;
        if (session.IsOpened && session.NativeSession is { } native)
        {
            try
            {
                width = checked((int)native.NaturalVideoWidth);
                height = checked((int)native.NaturalVideoHeight);
            }
            catch
            {
                width = 0;
                height = 0;
            }
        }
        var changed = Volatile.Read(ref _videoWidth) != width || Volatile.Read(ref _videoHeight) != height;
        Volatile.Write(ref _videoWidth, width);
        Volatile.Write(ref _videoHeight, height);
        return changed;
    }

    private void RestorePreferredPlaybackRate(Session session)
    {
        var native = session.NativeSession;
        if (native is null) return;
        var preferred = Volatile.Read(ref _desiredPlaybackRate);
        try
        {
            var supported = (preferred <= 1 || session.Item?.CanSkip != false)
                && native.IsSupportedPlaybackRateRange(preferred, preferred);
            native.PlaybackRate = supported ? preferred : 1;
        }
        catch
        {
            // Rate support is source-specific; a replacement must remain playable at its normal rate.
            try { native.PlaybackRate = 1; }
            catch { }
        }
        RefreshPlaybackRateSupport(session);
    }

    private void RefreshPlaybackRateSupport(Session session)
    {
        if (!IsActive(session)) return;
        var supported = false;
        try
        {
            supported = session.IsOpened && session.Item?.CanSkip != false && session.NativeSession is { } native
                && native.IsSupportedPlaybackRateRange(MinimumPlaybackRate, MaximumPlaybackRate);
        }
        catch { }
        Volatile.Write(ref _supportsPlaybackRate, supported);
    }

    private void InitializeSubtitlePresentation(Session session)
    {
        foreach (var track in session.ExternalTracks)
        {
            TypedEventHandler<TimedMetadataTrack, MediaCueEventArgs> changed = (_, _) => Queue(() =>
            {
                if (IsActive(session)) RefreshSubtitlePresentation(session);
            });
            session.SubtitleTrackHandlers.Add((track, changed));
            track.CueEntered += changed;
            track.CueExited += changed;
        }
        CaptureSubtitleCues(session);
        if (SubtitleTextChanged is null) return;
        session.SubtitleTimer = dispatcher.CreateTimer();
        session.SubtitleTimer.Interval = TimeSpan.FromMilliseconds(40);
        session.SubtitleTimer.IsRepeating = true;
        session.SubtitleTimerTick = (_, _) =>
        {
            if (!IsActive(session)) return;
            try { RefreshSubtitlePresentation(session); }
            catch (Exception exception)
            {
                RecordSubtitleFailure("Timer", exception);
                Fail(session, "UnsupportedSubtitle");
            }
        };
        session.SubtitleTimer.Tick += session.SubtitleTimerTick;
        session.SubtitleTimer.Start();
    }

    // IMediaCue returns its concrete RCW by runtime class name; NativeAOT must retain its factory members.
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextCue))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextLine))]
    private static void CaptureSubtitleCues(Session session)
    {
        var cues = new List<SubtitleCue>();
        var count = 0;
        var textCount = 0;
        var wrapperTypes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var track in session.ExternalTracks)
            {
                count = checked(count + track.Cues.Count);
                if (count > MaximumSubtitleCues) throw new PlaybackException("UnsupportedSubtitle");
                foreach (var cue in track.Cues)
                {
                    if (wrapperTypes.Count < 4) wrapperTypes.Add(cue.GetType().Name);
                    if (cue is not TimedTextCue text) continue;
                    textCount++;
                    if (text.Duration.Ticks <= 0) continue;
                    var content = string.Join(Environment.NewLine, text.Lines.Select(line => line.Text)).Trim();
                    if (content.Length == 0) continue;
                    var start = text.StartTime.Ticks;
                    var duration = text.Duration.Ticks;
                    var end = start > long.MaxValue - duration ? long.MaxValue : start + duration;
                    cues.Add(new(start, end, content));
                }
            }
            session.SubtitleCueCount = count;
            session.SubtitleCues = [.. cues.OrderBy(cue => cue.StartTicks)];
            session.SubtitleMaximumEndTicks = new long[session.SubtitleCues.Length];
            var maximumEnd = long.MinValue;
            for (var index = 0; index < session.SubtitleCues.Length; index++)
            {
                maximumEnd = Math.Max(maximumEnd, session.SubtitleCues[index].EndTicks);
                session.SubtitleMaximumEndTicks[index] = maximumEnd;
            }
            RecordSubtitleCapture(session.ExternalTracks.Length, count, textCount, cues.Count, wrapperTypes);
        }
        catch (Exception exception)
        {
            RecordSubtitleCapture(session.ExternalTracks.Length, count, textCount, cues.Count, wrapperTypes, exception);
            throw;
        }
    }

    private bool CanPresentExternalText(Session session) => IsActive(session) && SubtitleTextChanged is not null
        && (session.LocalSubtitle is not null || session.Request.SubtitleStreamIndex != -1)
        && session.ExternalTextReady && session.ExternalTracks.Length > 0;

    private void RefreshSubtitlePresentation(Session session, bool force = false)
    {
        if (!IsActive(session)) return;
        var supported = CanPresentExternalText(session);
        Volatile.Write(ref _supportsSubtitlePreferences, supported);
        if (!supported || !session.IsOpened || session.NativeSession is null)
        {
            PublishSubtitleText(session, string.Empty, force);
            return;
        }

        var cueCount = session.ExternalTracks.Sum(track => track.Cues.Count);
        if (cueCount != session.SubtitleCueCount) CaptureSubtitleCues(session);
        var position = Math.Max(0, session.NativeSession.Position.Ticks);
        if (session.LocalSubtitle is not null)
        {
            // Local files use the complete item's timeline even when a converted URL starts at engine zero.
            var offset = session.Request.TimelineOffsetTicks;
            position = position > long.MaxValue - offset ? long.MaxValue : position + offset;
        }
        var delay = TimeSpan.FromSeconds(_subtitleDelaySeconds).Ticks;
        position = delay < 0 && position > long.MaxValue + delay ? long.MaxValue : position - delay;
        var cues = session.SubtitleCues;
        var lower = 0;
        var upper = cues.Length;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (cues[middle].StartTicks <= position) lower = middle + 1;
            else upper = middle;
        }

        // Prefix maxima preserve overlapping cues without scanning an entire subtitle file each frame.
        List<string>? active = null;
        for (var index = lower - 1; index >= 0 && session.SubtitleMaximumEndTicks[index] > position; index--)
        {
            if (cues[index].EndTicks <= position) continue;
            (active ??= []).Add(cues[index].Text);
        }
        active?.Reverse();
        var text = active is null ? string.Empty
            : string.Join(Environment.NewLine, active.Distinct(StringComparer.Ordinal));
        PublishSubtitleText(session, text, force);
    }

    private void PublishSubtitleText(Session session, string text, bool force = false)
    {
        if (!force && string.Equals(session.SubtitleText, text, StringComparison.Ordinal)) return;
        session.SubtitleText = text;
        RecordSubtitlePresentation(text.Length);
        try
        {
            SubtitleTextChanged?.Invoke(this, new(session.Request.PlaybackId, text, _subtitleFontSize,
                _subtitleOutline, _subtitlePosition, _subtitleDelaySeconds));
        }
        catch (Exception exception) { RecordSubtitleFailure("Presenter", exception); }
    }

    private void RetireLumenControls(Session session)
    {
        if (session.NativeSession is { } native)
        {
            Release(session, "UnsubscribePlaybackRate", () => native.PlaybackRateChanged -= session.PlaybackRateChanged);
            Release(session, "UnsubscribeSupportedRates", () => native.SupportedPlaybackRatesChanged -= session.SupportedPlaybackRatesChanged);
            Release(session, "UnsubscribeVideoSize", () => native.NaturalVideoSizeChanged -= session.VideoSizeChanged);
        }
        RetireSubtitlePresentation(session);
        RetireLocalSubtitles(session);
        session.PlaybackRateChanged = null;
        session.SupportedPlaybackRatesChanged = null;
        session.VideoSizeChanged = null;
        if (!ReferenceEquals(_current, session)) return;
        Volatile.Write(ref _supportsPlaybackRate, false);
        Volatile.Write(ref _supportsSubtitlePreferences, false);
        Volatile.Write(ref _videoWidth, 0);
        Volatile.Write(ref _videoHeight, 0);
        PublishSubtitleText(session, string.Empty, force: true);
    }

    private void RetireSubtitlePresentation(Session session)
    {
        if (session.SubtitleTimer is { } timer)
        {
            Release(session, "StopSubtitleTimer", timer.Stop);
            Release(session, "UnsubscribeSubtitleTimer", () => timer.Tick -= session.SubtitleTimerTick);
        }
        foreach (var (track, handler) in session.SubtitleTrackHandlers)
        {
            Release(session, "UnsubscribeSubtitleCueEntered", () => track.CueEntered -= handler);
            Release(session, "UnsubscribeSubtitleCueExited", () => track.CueExited -= handler);
        }
        session.SubtitleTrackHandlers.Clear();
        session.SubtitleCues = [];
        session.SubtitleMaximumEndTicks = [];
        session.SubtitleTimer = null;
        session.SubtitleTimerTick = null;
    }

    private readonly record struct SubtitleCue(long StartTicks, long EndTicks, string Text);

    private sealed partial class Session
    {
        public TypedEventHandler<MediaPlaybackSession, object>? PlaybackRateChanged { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? SupportedPlaybackRatesChanged { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? VideoSizeChanged { get; set; }
        public List<(TimedMetadataTrack Track, TypedEventHandler<TimedMetadataTrack, MediaCueEventArgs> Handler)> SubtitleTrackHandlers { get; } = [];
        public DispatcherQueueTimer? SubtitleTimer { get; set; }
        public TypedEventHandler<DispatcherQueueTimer, object>? SubtitleTimerTick { get; set; }
        public SubtitleCue[] SubtitleCues { get; set; } = [];
        public long[] SubtitleMaximumEndTicks { get; set; } = [];
        public int SubtitleCueCount { get; set; }
        public string? SubtitleText { get; set; }
    }
}
