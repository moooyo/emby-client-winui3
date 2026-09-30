using System.Globalization;

namespace EmbyClient.App.Services;

public readonly record struct LumenSearchCount
{
    private LumenSearchCount(long value, bool isExact)
    {
        Value = value;
        IsExact = isExact;
    }

    public long Value { get; }
    public bool IsExact { get; }

    public static LumenSearchCount From(int? total, int loaded, bool hasMore, bool loading, bool scopeComplete = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(loaded);
        return total is >= 0 ? new(total.Value, true) : new(loaded, scopeComplete && !hasMore && !loading);
    }

    public static LumenSearchCount Sum(LumenSearchCount first, LumenSearchCount second) =>
        new(checked(first.Value + second.Value), first.IsExact && second.IsExact);

    public string Format(IFormatProvider? culture = null, bool hideZeroLowerBound = false) =>
        hideZeroLowerBound && Value == 0 && !IsExact ? string.Empty
            : Value.ToString("N0", culture ?? CultureInfo.CurrentCulture) + (IsExact ? string.Empty : "+");
}
