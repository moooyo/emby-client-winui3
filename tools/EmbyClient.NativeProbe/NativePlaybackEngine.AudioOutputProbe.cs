using EmbyClient.NativeProbe;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    internal NativeAudioOutputProbeObserver? NativeAudioOutputProbeObserver { get; init; }

    internal NativeAudioProbeState CaptureNativeAudioStateForProbe()
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("The native audio probe must observe on the player dispatcher.");
        var session = _current;
        var source = session?.DecoderSource;
        var audio = source?.CurrentAudioStream;
        return new NativeAudioProbeState
        {
            HasDecoderSource = source is not null,
            ExclusiveOutputOpened = session?.NativeAudioOutputOpened == true,
            CleanupSucceeded = session?.NativeAudioCleanupSucceeded ?? true,
            Retired = session?.Retired ?? true,
            NativeAudioStreamIndex = audio is null ? -1 : checked((int)audio.StreamIndex),
            NativeAudioCodec = audio?.CodecName,
            RawOutput = source is null ? null : NativeAudioInterop.Read(source)
        };
    }

    internal Func<bool> RetainNativeAudioCleanupObservationForProbe()
    {
        if (!dispatcher.HasThreadAccess || _current is not { } session)
            throw new InvalidOperationException("An active dispatcher-owned native session is required.");
        // Retain only this bounded case's owner so source retirement can be checked after _current changes.
        return () => session.Retired && session.NativeAudioCleanupSucceeded && !session.NativeAudioOutputOpened;
    }
}
