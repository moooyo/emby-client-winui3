using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Routes the selected delivery without confusing it with an available transcoding alternative.</summary>
internal static class MediaDeliveryClassifier
{
    internal static bool IsHls(PlaybackEngineRequest request) =>
        request.MediaUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        || request.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && string.Equals(request.Source.TranscodingSubProtocol, "hls", StringComparison.OrdinalIgnoreCase);

    // A paused native HLS seek can stall at a buffered-range edge without raising SeekCompleted.
    // The coordinator reopens HLS at the requested position and restores pause after confirmed playback.
    internal static bool CanSeekInPlace(PlaybackEngineRequest request, bool nativeCanSeek) =>
        nativeCanSeek && !IsHls(request);
}
