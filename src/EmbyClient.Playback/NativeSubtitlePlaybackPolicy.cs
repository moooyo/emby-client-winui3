using EmbyClient.Api;

namespace EmbyClient.Playback;

/// <summary>Recognizes original embedded inputs without promising index preservation through a remux.</summary>
public static class NativeSubtitlePlaybackPolicy
{
    public static bool IsSupportedEmbeddedBitmap(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
            && stream.IsExternal == false && stream.IsTextSubtitleStream != true
            && stream.Codec?.ToLowerInvariant() is "pgssub" or "hdmv_pgs_subtitle" or "dvdsub" or "dvd_subtitle" or "vobsub";
    }

    public static bool IsSupportedEmbeddedAss(MediaStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)
            && stream.IsExternal == false && stream.IsTextSubtitleStream != false
            && stream.Codec?.ToLowerInvariant() is "ass" or "ssa";
    }

    public static bool IsSupportedEmbeddedSubtitle(MediaStream stream, bool nativeAssEligible) =>
        IsSupportedEmbeddedBitmap(stream) || nativeAssEligible && IsSupportedEmbeddedAss(stream);
}
