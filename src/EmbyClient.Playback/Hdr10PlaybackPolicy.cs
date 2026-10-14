using EmbyClient.Api;

namespace EmbyClient.Playback;

/// <summary>Limits HDR delivery to a known HDR10 HEVC input, independently of open-ended VideoRange labels.</summary>
public static class Hdr10PlaybackPolicy
{
    public static bool IsExplicitHdr10(MediaStream video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return EqualsValue(video.ExtendedVideoSubType, "Hdr10")
            && (string.IsNullOrWhiteSpace(video.ExtendedVideoType) || EqualsValue(video.ExtendedVideoType, "Hdr10"))
            && EqualsValue(video.Codec, "hevc")
            && video.BitDepth == 10
            && (EqualsValue(video.Profile, "Main 10") || EqualsValue(video.Profile, "main10"))
            && video.IsInterlaced == false
            && EqualsValue(video.ColorTransfer, "smpte2084")
            && EqualsValue(video.ColorPrimaries, "bt2020")
            && !EqualsValue(video.VideoRange, "SDR");
    }

    public static bool RequiresServerConversion(MediaStream video, bool hdr10OutputEligible)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (hdr10OutputEligible && IsExplicitHdr10(video)) return false;

        // The extra HEVC profile is only for explicitly identified HDR10. It never widens unknown inputs.
        return EqualsValue(video.Codec, "hevc")
            || video.BitDepth is > 8
            || !string.IsNullOrWhiteSpace(video.ExtendedVideoSubType) && !EqualsValue(video.ExtendedVideoSubType, "None")
            || !string.IsNullOrWhiteSpace(video.ExtendedVideoType) && !EqualsValue(video.ExtendedVideoType, "None")
            || !string.IsNullOrWhiteSpace(video.VideoRange) && !EqualsValue(video.VideoRange, "SDR")
            || EqualsValue(video.ColorTransfer, "smpte2084")
            || EqualsValue(video.ColorTransfer, "arib-std-b67");
    }

    public static bool IsSupportedContainer(string? container) => EqualsValue(container, "mkv") || EqualsValue(container, "mp4");

    private static bool EqualsValue(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
