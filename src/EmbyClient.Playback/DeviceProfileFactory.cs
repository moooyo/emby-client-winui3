using EmbyClient.Api;

namespace EmbyClient.Playback;

/// <summary>Creates a source-opening profile from the current output eligibility, keeping conversion conservative.</summary>
public static class DeviceProfileFactory
{
    public static DeviceProfile Create(PlaybackSelection selection, bool enableExternalWebVtt = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var profile = ConservativeDeviceProfile.Create(selection.MaxStreamingBitrate, enableExternalWebVtt);
        var hdr10 = selection.Hdr10OutputEligible && !selection.ForceTranscoding;
        var nativeSubtitles = selection.NativeEmbeddedSubtitlesEligible && !selection.ForceTranscoding && !selection.ForceSubtitleBurnIn;
        var nativeAss = selection.NativeAssSubtitlesEligible && !selection.ForceTranscoding && !selection.ForceSubtitleBurnIn;
        var nativeAudio = selection.NativeAudioPassthroughEligible && !selection.ForceTranscoding
            && selection.AudioPassthroughMode is AudioPassthroughMode.DolbyDts or AudioPassthroughMode.All;
        if (!hdr10 && !nativeSubtitles && !nativeAss && !nativeAudio) return profile;

        // These are source formats consumed by the native decoder, never an endpoint capability claim.
        // Opening checks the exact selected stream and WASAPI format and keeps a real PCM fallback.
        var audioCodecs = nativeAudio ? selection.AudioPassthroughMode == AudioPassthroughMode.All
            ? "aac,ac3,eac3,dts,dtshd,truehd" : "aac,ac3,eac3,dts" : "aac";

        List<DirectPlayProfile> direct = [.. (profile.DirectPlayProfiles ?? [])];
        List<CodecProfile> codecs = [.. (profile.CodecProfiles ?? [])];
        List<SubtitleProfile> subtitles = [.. (profile.SubtitleProfiles ?? [])];
        if (nativeSubtitles)
        {
            // Selection and opening must verify IsExternal=false and the original container's actual stream index.
            subtitles.InsertRange(0,
            [
                new SubtitleProfile { Format = "pgssub", Method = "Embed" },
                new SubtitleProfile { Format = "dvdsub", Method = "Embed" }
            ]);
        }

        if (nativeAss)
        {
            subtitles.InsertRange(0,
            [
                new SubtitleProfile { Format = "ass", Method = "External" },
                new SubtitleProfile { Format = "ssa", Method = "External" },
                new SubtitleProfile { Format = "ass", Method = "Embed" },
                new SubtitleProfile { Format = "ssa", Method = "Embed" }
            ]);
        }

        if (nativeSubtitles || nativeAss || nativeAudio)
            direct.Add(new DirectPlayProfile { Type = "Video", Container = "mp4,mkv", VideoCodec = "h264", AudioCodec = audioCodecs });

        if (nativeAudio)
        {
            codecs.Add(new CodecProfile
            {
                Type = "VideoAudio",
                Codec = audioCodecs[4..],
                Conditions = [new ProfileCondition { Condition = "LessThanEqual", Property = "AudioChannels", Value = "8", IsRequired = true }]
            });
        }

        if (hdr10)
        {
            direct.Add(new DirectPlayProfile { Type = "Video", Container = "mp4,mkv", VideoCodec = "hevc", AudioCodec = audioCodecs });
            codecs.Add(new CodecProfile
            {
                Type = "Video",
                Codec = "hevc",
                Conditions =
                [
                    new ProfileCondition { Condition = "Equals", Property = "VideoBitDepth", Value = "10", IsRequired = true },
                    new ProfileCondition { Condition = "Equals", Property = "IsInterlaced", Value = "false", IsRequired = true }
                ]
            });
        }

        // HDR type and color metadata are checked by the client before opening. VideoRange is not a profile condition.
        return profile with
        {
            Name = "Windows Native AVC AAC with Conditional Output",
            DirectPlayProfiles = [.. direct],
            CodecProfiles = [.. codecs],
            SubtitleProfiles = [.. subtitles]
        };
    }
}
