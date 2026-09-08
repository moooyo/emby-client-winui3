using EmbyClient.Api;

namespace EmbyClient.Playback;

/// <summary>
/// Creates a deliberately limited capability baseline for the Windows native player.
/// </summary>
public static class ConservativeDeviceProfile
{
    /// <summary>
    /// Creates a fresh profile so callers do not share mutable capability arrays.
    /// </summary>
    /// <param name="maxStreamingBitrate">The maximum combined streaming bitrate, in bits per second.</param>
    /// <param name="enableExternalWebVtt">
    /// Whether the adapter can attach authenticated external WebVTT subtitles.
    /// </param>
    public static DeviceProfile Create(
        long maxStreamingBitrate = 20_000_000,
        bool enableExternalWebVtt = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStreamingBitrate);

        List<SubtitleProfile> subtitleProfiles = [];
        if (enableExternalWebVtt)
        {
            subtitleProfiles.Add(new SubtitleProfile { Format = "vtt", Method = "External" });
        }

        // The adapter does not promise embedded subtitle selection or ASS styling.
        // Keep burn-in available even when external WebVTT delivery is enabled.
        foreach (string format in new[] { "srt", "vtt", "ass", "ssa", "sub", "pgssub", "dvdsub" })
        {
            subtitleProfiles.Add(new SubtitleProfile { Format = format, Method = "Encode" });
        }

        return new DeviceProfile
        {
            Name = "Windows Native AVC AAC Baseline",
            MaxStreamingBitrate = maxStreamingBitrate,
            MaxStaticBitrate = maxStreamingBitrate,
            DirectPlayProfiles =
            [
                new DirectPlayProfile
                {
                    Type = "Video",
                    Container = "mp4",
                    VideoCodec = "h264",
                    AudioCodec = "aac"
                }
            ],
            TranscodingProfiles =
            [
                new TranscodingProfile
                {
                    Type = "Video",
                    Container = "ts",
                    Protocol = "hls",
                    Context = "Streaming",
                    VideoCodec = "h264",
                    AudioCodec = "aac",
                    MaxAudioChannels = "2",
                    BreakOnNonKeyFrames = false,
                    CopyTimestamps = false
                }
            ],
            CodecProfiles =
            [
                new CodecProfile
                {
                    Type = "Video",
                    Codec = "h264",
                    Conditions =
                    [
                        RequiredCondition("EqualsAny", "VideoProfile", "high|main|baseline|constrained baseline"),
                        RequiredCondition("LessThanEqual", "VideoLevel", "41"),
                        RequiredCondition("LessThanEqual", "VideoBitDepth", "8"),
                        RequiredCondition("Equals", "IsInterlaced", "false")
                    ]
                },
                new CodecProfile
                {
                    Type = "VideoAudio",
                    Codec = "aac",
                    Conditions =
                    [
                        RequiredCondition("LessThanEqual", "AudioChannels", "2")
                    ]
                }
            ],
            SubtitleProfiles = [.. subtitleProfiles]
        };
    }

    // Unknown stream properties must not silently widen the direct-stream baseline.
    private static ProfileCondition RequiredCondition(string condition, string property, string value) => new()
    {
        Condition = condition,
        Property = property,
        Value = value,
        IsRequired = true
    };
}
