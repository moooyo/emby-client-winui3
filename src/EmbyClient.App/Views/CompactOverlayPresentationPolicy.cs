using System;

namespace EmbyClient.App.Views;

internal readonly record struct CompactOverlayLayout(
    double TransportTop, double FooterTop, double SubtitleBottom, bool HasRoom);

internal readonly record struct CompactOverlayClock(
    long PositionTicks, long? DurationTicks, bool HasProgress, double Progress);

/// <summary>Positions compact player controls using measured bands without changing their visibility.</summary>
internal static class CompactOverlayPresentationPolicy
{
    private const double TransportHeight = 48;
    private const double ControlClearance = 8;
    private const double BottomInset = 10;
    private const double SubtitleClearance = 12;

    public static CompactOverlayLayout CalculateLayout(double viewportHeight, double headerBottom, double footerHeight)
    {
        var height = NonnegativeFinite(viewportHeight);
        var header = NonnegativeFinite(headerBottom);
        var footer = NonnegativeFinite(footerHeight);
        var footerTop = Math.Max(0, height - BottomInset - footer);
        var subtitleBottom = Math.Min(height, BottomInset + footer + SubtitleClearance);
        var idealTop = height * .5 - TransportHeight * .62;

        var minimumTop = header + ControlClearance;
        var maximumTop = footerTop - ControlClearance - TransportHeight;
        // Large finite coordinates must retain both the real gap and an ordered representable interval.
        var hasRoom = footerTop - header >= TransportHeight + 2 * ControlClearance && minimumTop <= maximumTop;
        var transportTop = hasRoom
            ? Math.Clamp(idealTop, minimumTop, maximumTop)
            : Math.Clamp(idealTop, 0, Math.Max(0, height - TransportHeight));

        // An impossible measurement still yields a bounded position for the unchanged control group.
        return new(transportTop, footerTop, subtitleBottom, hasRoom);
    }

    public static CompactOverlayClock GetClock(long positionTicks, long? durationTicks)
    {
        var position = Math.Max(0, positionTicks);
        var duration = durationTicks is >= 0 ? durationTicks : null;
        if (duration is { } knownDuration) position = Math.Min(position, knownDuration);
        var hasProgress = duration is > 0;
        var progress = hasProgress ? Math.Clamp((double)position / duration!.Value, 0, 1) : 0;
        return new(position, duration, hasProgress, progress);
    }

    private static double NonnegativeFinite(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}
