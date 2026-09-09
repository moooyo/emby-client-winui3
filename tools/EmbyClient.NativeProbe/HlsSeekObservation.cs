using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

internal sealed partial class HlsSeekObservation : IDisposable
{
    private readonly NativePlaybackEngine _engine;
    private readonly Guid _id;
    private readonly MediaPlaybackSession _session;
    private readonly RealHlsLoop _loop;
    private readonly TypedEventHandler<MediaPlaybackSession, object> _seekCompleted;
    private bool _retired;

    internal HlsSeekObservation(NativePlaybackEngine engine, Guid id, MediaPlayer player,
        DispatcherQueue dispatcher, RealHlsLoop loop)
    {
        _engine = engine; _id = id; _session = player.PlaybackSession; _loop = loop;
        _seekCompleted = (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_retired) return;
            try { _loop.NativeSeekCompletedPositions.Add(_session.Position.Ticks); }
            catch (Exception exception) { _loop.SeekObservationHResult = exception.HResult; }
        });
        _session.SeekCompleted += _seekCompleted;
        engine.EventReceived += OnEngineEvent;
    }

    internal void Sample(string point)
    {
        if (_retired) return;
        _loop.SeekObservationPoint = point;
        try
        {
            _loop.SeekLastNativePositionTicks = _session.Position.Ticks;
            _loop.SeekLastNativeDurationTicks = _session.NaturalDuration.Ticks;
            _loop.SeekLastNativeState = _session.PlaybackState.ToString();
            _loop.SeekLastCanSeek = _session.CanSeek;
            _loop.SeekLastSeekableRanges = _session.GetSeekableRanges().Select(value => new NativeTimeRangeSummary
                { StartTicks = value.Start.Ticks, EndTicks = value.End.Ticks }).ToList();
            _loop.SeekLastBufferedRanges = _session.GetBufferedRanges().Select(value => new NativeTimeRangeSummary
                { StartTicks = value.Start.Ticks, EndTicks = value.End.Ticks }).ToList();
            _loop.TargetInsideLastSeekableRange = _loop.SeekLastSeekableRanges.Any(value =>
                value.StartTicks <= _loop.SeekTargetTicks && value.EndTicks >= _loop.SeekTargetTicks);
            _loop.SeekLastSnapshotPositionTicks = _engine.Snapshot?.PositionTicks;
            _loop.SeekLastSnapshotState = _engine.Snapshot?.State.ToString();
        }
        catch (Exception exception) { _loop.SeekObservationHResult = exception.HResult; }
    }

    private void OnEngineEvent(object? sender, PlaybackEngineEventArgs args)
    {
        if (args.Snapshot.PlaybackId == _id && args.Snapshot.State == PlaybackEngineState.Stopped)
        {
            Sample("BeforeNativePlayerDispose");
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_retired) return;
        _retired = true;
        _engine.EventReceived -= OnEngineEvent;
        try { _session.SeekCompleted -= _seekCompleted; }
        catch (Exception exception) { _loop.SeekObservationHResult = exception.HResult; }
    }
}
