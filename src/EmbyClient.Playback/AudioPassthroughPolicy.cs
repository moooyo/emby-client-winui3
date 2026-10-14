namespace EmbyClient.Playback;

/// <summary>Limits bitstream requests to explicitly identified codec families and unchanged audio controls.</summary>
public static class AudioPassthroughPolicy
{
    public static bool IsCodecAllowed(AudioPassthroughMode mode, string? codec, string? profile = null)
    {
        if (mode is not (AudioPassthroughMode.DolbyDts or AudioPassthroughMode.All)
            || string.IsNullOrWhiteSpace(codec)) return false;

        return codec.Trim().ToLowerInvariant() switch
        {
            "ac3" or "ac-3" or "eac3" or "e-ac-3" => true,
            "dts" or "dca" => mode == AudioPassthroughMode.All || !IsDtsHdProfile(profile),
            "truehd" or "true-hd" or "true_hd" or "dts-hd" or "dtshd" or "dts_hd" =>
                mode == AudioPassthroughMode.All,
            _ => false
        };
    }

    public static bool SupportsUnchangedBitstreamControls(int volumeLevel, bool isMuted, double playbackRate) =>
        volumeLevel == 100 && !isMuted && double.IsFinite(playbackRate) && playbackRate == 1d;

    private static bool IsDtsHdProfile(string? profile) => profile?.Trim().ToLowerInvariant() is
        "hd" or "ma" or "hra"
        or "dts-hd" or "dts hd" or "dtshd" or "dts_hd"
        or "dts-hd ma" or "dts hd ma" or "dts_hd_ma"
        or "dts-hd hra" or "dts hd hra" or "dts_hd_hra"
        or "master audio" or "dts-hd master audio" or "dts hd master audio"
        or "high resolution audio" or "dts-hd high resolution audio" or "dts hd high resolution audio";
}
