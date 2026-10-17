using System.Diagnostics;
using EmbyClient.App.Services;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

internal readonly record struct NativePlaybackClockSample(
    DateTimeOffset ObservedAtUtc,
    long StartedTimestamp,
    long CompletedTimestamp,
    Guid? PlaybackId,
    bool Available,
    long? RawPositionTicks,
    long? RawNaturalDurationTicks,
    double? RawPlaybackRate,
    MediaPlaybackState? RawPlaybackState,
    string? UnavailableReason,
    string? ExceptionType);

public sealed partial class NativePlaybackEngine
{
    internal static bool AcceptanceNativeClockDiagnosticsEnabled
    {
        get
        {
            try
            {
                var root = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
                return !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && Directory.Exists(root);
            }
            catch (Exception) { return false; }
        }
    }

    internal NativePlaybackClockSample ReadAcceptanceNativeClock(Guid expectedPlaybackId)
    {
        var started = Stopwatch.GetTimestamp();
        Guid? playbackId = null;
        NativePlaybackClockSample Unavailable(string reason, string? exceptionType = null) => new(
            DateTimeOffset.UtcNow, started, Stopwatch.GetTimestamp(), playbackId, false,
            null, null, null, null, reason, exceptionType);

        try
        {
            if (!AcceptanceNativeClockDiagnosticsEnabled) return Unavailable("IsolatedProfileRequired");
            if (!dispatcher.HasThreadAccess) return Unavailable("UiThreadRequired");
            var session = _current;
            if (session is null) return Unavailable("NoCurrentSession");
            playbackId = session.Request.PlaybackId;
            if (playbackId != expectedPlaybackId) return Unavailable("PlaybackIdMismatch");
            if (!IsActive(session)) return Unavailable("InactiveSession");
            var native = session.NativeSession;
            if (native is null) return Unavailable("NoNativeSession");

            // These consecutive property reads are bounded by timestamps, not an atomic native snapshot.
            var position = native.Position.Ticks;
            var duration = native.NaturalDuration.Ticks;
            var rate = native.PlaybackRate;
            var state = native.PlaybackState;
            if (!IsActive(session) || !ReferenceEquals(_current, session)
                || !ReferenceEquals(session.NativeSession, native) || session.Request.PlaybackId != expectedPlaybackId)
                return Unavailable("SessionChangedDuringRead");

            return new(DateTimeOffset.UtcNow, started, Stopwatch.GetTimestamp(), playbackId, true,
                position, duration, rate, state, null, null);
        }
        catch (Exception exception)
        {
            return Unavailable("NativeClockUnavailable", exception.GetType().Name);
        }
    }
}
