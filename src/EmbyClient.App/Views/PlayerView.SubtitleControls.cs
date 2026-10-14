using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly record struct SubtitleControlState(bool IsBurnedIn, bool IsBitmap,
        bool CanAdjustAppearance, bool CanAdjustTiming, string? Notice);

    private SubtitleControlState CurrentSubtitleControls()
    {
        var context = _coordinator?.ActiveContext;
        var local = context is not null && _localSubtitlePlaybackId == context.PlaybackId && _localSubtitleName is not null;
        var subtitleIndex = context?.Selection.SubtitleStreamIndex ?? context?.Source.DefaultSubtitleStreamIndex;
        var subtitle = local ? null : context?.Source.MediaStreams?.FirstOrDefault(stream => stream.Index == subtitleIndex
            && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
        var native = _engine?.SubtitlePlayback;
        var currentNative = context is not null && native?.PlaybackId == context.PlaybackId;
        // Video conversion alone does not imply that the subtitle has been encoded into its pixels.
        var burned = !local && (currentNative ? native!.IsBurnedIn : subtitleIndex is >= 0
            && context?.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && string.Equals(subtitle?.DeliveryMethod, "Encode", StringComparison.OrdinalIgnoreCase));
        var bitmap = !local && (currentNative ? native!.IsBitmapSource : NativePlaybackEngine.IsBitmapSubtitleStream(subtitle));
        var active = _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        var appearance = active && currentNative && !burned && !bitmap && native!.CanAdjustAppearance
            && _engine?.SupportsSubtitleStyling == true;
        var timing = active && currentNative && !burned && native!.CanAdjustTiming && _engine?.SupportsSubtitleDelay == true;
        var notice = burned ? LumenText.Get("These subtitles are burned into the video by the server. Size and delay cannot be changed.")
            : bitmap ? LumenText.Get(timing
                ? "Bitmap subtitles cannot change size. Delay can still be adjusted."
                : "Bitmap subtitles cannot change size. Timing is unavailable in this playback engine.")
            : currentNative && native!.UsesNativePresentation && native.PreservesSourceStyles
                ? LumenText.Get(timing
                    ? "Source subtitle styles are preserved. Size and position cannot be changed; delay remains adjustable."
                    : "Source subtitle styles are preserved. Size and position cannot be changed.")
                : null;
        return new SubtitleControlState(burned, bitmap, appearance, timing, notice);
    }

    private static string LocalSubtitleDisplayName(string fileName) => LumenText.Get("{0} \u00b7 Only on this PC", fileName);
}
