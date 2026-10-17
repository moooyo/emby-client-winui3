using System.Diagnostics;
using System.Globalization;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private long? _lastAcceptanceNativeClockSampleTimestamp;

    private void RecordAcceptanceNativeClockSample()
    {
        // Library and retired playback ticks must not inspect the diagnostic profile directory.
        if (!IsLoaded || Visibility != Microsoft.UI.Xaml.Visibility.Visible
            || _coordinator?.ActiveContext is not { } context || context.PlaybackId == Guid.Empty
            || _engine is not { } engine || engine.Snapshot is not { } activeSnapshot
            || activeSnapshot.PlaybackId != context.PlaybackId
            || activeSnapshot.State is not (PlaybackEngineState.Opening or PlaybackEngineState.Playing
                or PlaybackEngineState.Paused or PlaybackEngineState.Buffering or PlaybackEngineState.Seeking)) return;
        if (!NativePlaybackEngine.AcceptanceNativeClockDiagnosticsEnabled) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastAcceptanceNativeClockSampleTimestamp is { } previous
                && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(1)) return;
            var playbackId = context.PlaybackId;
            _lastAcceptanceNativeClockSampleTimestamp = now;
            var sample = engine.ReadAcceptanceNativeClock(playbackId);
            var snapshot = engine.Snapshot;
            var snapshotMatches = snapshot?.PlaybackId == playbackId;
            AcceptanceDiagnostics.Trace(FormattableString.Invariant(
                $"native-clock kind=sample observedUtc={sample.ObservedAtUtc:O} startTimestamp={sample.StartedTimestamp} endTimestamp={sample.CompletedTimestamp} timestampFrequency={Stopwatch.Frequency} expectedPlaybackId={playbackId:N} actualPlaybackId={sample.PlaybackId?.ToString("N") ?? "none"} available={sample.Available} rawPositionTicks={sample.RawPositionTicks?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} rawNaturalDurationTicks={sample.RawNaturalDurationTicks?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} rawRate={sample.RawPlaybackRate?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable"} rawState={sample.RawPlaybackState?.ToString() ?? "unavailable"} snapshotMatches={snapshotMatches} snapshotPositionTicks={(snapshotMatches ? snapshot?.PositionTicks : null)?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} snapshotState={(snapshotMatches ? snapshot?.State.ToString() : null) ?? "unavailable"} unavailableReason={sample.UnavailableReason ?? "none"} exceptionType={sample.ExceptionType ?? "none"}"));
        }
        catch (Exception exception)
        {
            AcceptanceDiagnostics.Trace($"native-clock kind=sample available=False unavailableReason=DiagnosticUnavailable exceptionType={exception.GetType().Name}");
        }
    }

    private void RecordAcceptanceNativeClockTerminal(object? sender, PlaybackEngineEventArgs args)
    {
        if (args.Kind is not (PlaybackEngineEventKind.Ended or PlaybackEngineEventKind.Failed)
            || !NativePlaybackEngine.AcceptanceNativeClockDiagnosticsEnabled || !ReferenceEquals(sender, _engine)) return;
        try
        {
            var snapshot = args.Snapshot;
            var currentPlaybackId = _engine?.Snapshot?.PlaybackId;
            // This is the existing engine event's observation time, not MediaEnded callback arrival.
            AcceptanceDiagnostics.Trace(FormattableString.Invariant(
                $"native-clock kind=terminal origin=EngineEventSnapshot mediaEndedArrival=Unobserved observedUtc={DateTimeOffset.UtcNow:O} observationTimestamp={Stopwatch.GetTimestamp()} timestampFrequency={Stopwatch.Frequency} eventKind={args.Kind} playbackId={snapshot.PlaybackId:N} currentPlaybackId={currentPlaybackId?.ToString("N") ?? "none"} snapshotPositionTicks={snapshot.PositionTicks} snapshotNaturalDurationTicks={snapshot.DurationTicks?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} snapshotRate={snapshot.PlaybackRate:R} snapshotState={snapshot.State}"));
        }
        catch (Exception exception)
        {
            AcceptanceDiagnostics.Trace($"native-clock kind=terminal unavailableReason=DiagnosticUnavailable exceptionType={exception.GetType().Name}");
        }
    }
}
