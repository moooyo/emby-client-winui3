using EmbyClient.Playback;
using FFmpegInteropX;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private bool _supportsAudioDelay;
    private double _desiredAudioDelaySeconds;
    private double _audioDelaySeconds;

    /// <summary>True when the current decoder exposes audio sample timestamps that can be shifted.</summary>
    public bool SupportsAudioDelay => Volatile.Read(ref _supportsAudioDelay);

    /// <summary>The applied audio timestamp offset. A positive value renders audio later than video.</summary>
    public double AudioDelaySeconds => Volatile.Read(ref _audioDelaySeconds);

    /// <summary>
    /// Shifts real decoder sample timestamps without seeking the video or changing server playback progress.
    /// The offset applies to every audio stream so changing an audio track preserves synchronization.
    /// Samples already queued in the Windows audio renderer retain their previous timestamps.
    /// </summary>
    public Task SetAudioDelayAsync(Guid playbackId, double delaySeconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(delaySeconds) || delaySeconds is < -10 or > 10)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        var delay = TimeSpan.FromSeconds(Math.Round(delaySeconds, 1, MidpointRounding.AwayFromZero));
        return ControlAsync(playbackId, session =>
        {
            if (!session.IsOpened || session.DecoderSource is not { } source)
                throw new PlaybackException("AudioDelayNotSupported");
            ApplyAudioStreamDelay(source, delay);
            if (!IsActive(session)) return;
            Volatile.Write(ref _desiredAudioDelaySeconds, delay.TotalSeconds);
            Volatile.Write(ref _audioDelaySeconds, delay.TotalSeconds);
            Volatile.Write(ref _supportsAudioDelay, true);
            Publish(session, PlaybackEngineEventKind.StateChanged);
        }, cancellationToken);
    }

    private void InitializeAudioDelay(Session session)
    {
        Volatile.Write(ref _supportsAudioDelay, false);
        Volatile.Write(ref _audioDelaySeconds, 0);
        if (session.DecoderSource is not { } source) return;
        try
        {
            var delay = TimeSpan.FromSeconds(Volatile.Read(ref _desiredAudioDelaySeconds));
            ApplyAudioStreamDelay(source, delay);
            if (!IsActive(session)) return;
            Volatile.Write(ref _audioDelaySeconds, delay.TotalSeconds);
            Volatile.Write(ref _supportsAudioDelay, true);
        }
        catch
        {
            // An unsupported adjustment must not prevent an otherwise playable replacement from opening.
            try { ApplyAudioStreamDelay(source, TimeSpan.Zero); }
            catch { }
        }
    }

    private static void ApplyAudioStreamDelay(FFmpegMediaSource source, TimeSpan delay)
    {
        AudioStreamInfo[] streams = [];
        TimeSpan[] previous = [];
        try
        {
            streams = source.AudioStreams.ToArray();
            if (streams.Length == 0) throw new PlaybackException("AudioDelayNotSupported");
            previous = streams.Select(stream => source.GetStreamDelay(stream)).ToArray();
            foreach (var stream in streams)
            {
                source.SetStreamDelay(stream, delay);
                if (source.GetStreamDelay(stream) != delay) throw new PlaybackException("AudioDelayNotSupported");
            }
        }
        catch
        {
            for (var index = 0; index < previous.Length; index++)
            {
                try { source.SetStreamDelay(streams[index], previous[index]); }
                catch { }
            }
            throw new PlaybackException("AudioDelayNotSupported");
        }
    }
}
