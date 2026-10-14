namespace EmbyClient.App.Playback;

/// <summary>A buffered interval on the native media URL's timeline, before any Emby timeline offset.</summary>
internal readonly record struct BufferedPlaybackRange(long StartTicks, long EndTicks);

/// <summary>Calculates media time buffered ahead from reported intervals without crossing an unbuffered gap.</summary>
internal static class BufferedPlaybackRangePolicy
{
    public static TimeSpan? GetBufferedAhead(long nativeUrlPositionTicks, IReadOnlyList<BufferedPlaybackRange>? ranges)
    {
        if (nativeUrlPositionTicks < 0 || ranges is not { Count: > 0 }) return null;

        var ordered = new BufferedPlaybackRange[ranges.Count];
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            if (range.StartTicks < 0 || range.EndTicks <= range.StartTicks) return null;
            ordered[index] = range;
        }
        Array.Sort(ordered, static (left, right) => left.StartTicks.CompareTo(right.StartTicks));

        var start = ordered[0].StartTicks;
        var end = ordered[0].EndTicks;
        for (var index = 1; index < ordered.Length; index++)
        {
            var range = ordered[index];
            if (range.StartTicks <= end)
            {
                end = Math.Max(end, range.EndTicks);
                continue;
            }

            if (nativeUrlPositionTicks >= start && nativeUrlPositionTicks <= end)
                return TimeSpan.FromTicks(end - nativeUrlPositionTicks);
            start = range.StartTicks;
            end = range.EndTicks;
        }

        return nativeUrlPositionTicks >= start && nativeUrlPositionTicks <= end
            ? TimeSpan.FromTicks(end - nativeUrlPositionTicks) : null;
    }
}
