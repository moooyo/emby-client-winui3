using Microsoft.UI.Xaml.Data;

namespace EmbyClient.App.Views;

public sealed partial class PlaybackTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double seconds || !double.IsFinite(seconds)) return string.Empty;
        var total = (long)Math.Clamp(Math.Floor(seconds), 0, TimeSpan.MaxValue.TotalSeconds);
        return total >= 3600
            ? FormattableString.Invariant($"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : FormattableString.Invariant($"{total / 60}:{total % 60:00}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
