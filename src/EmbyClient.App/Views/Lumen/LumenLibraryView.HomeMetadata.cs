using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private void RenderHeroMetadata(MediaCardViewModel item)
    {
        _heroMetadata.Blocks.Clear();
        var line = new Paragraph();
        var values = new[]
        {
            item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
            item.Item.Genres is { Length: > 0 } genres ? string.Join(" / ", genres.Take(2)) : null,
            item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons
                ? LumenText.Get("{0} seasons", seasons) : RuntimeText(item.Item.RunTimeTicks)
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) line.Inlines.Add(new Run { Text = "  \u00b7  ", Foreground = LumenTheme.Brush("ImageSub") });
            line.Inlines.Add(new Run { Text = values[index] });
        }
        if (item.Item.CommunityRating is { } rating)
        {
            if (line.Inlines.Count > 0) line.Inlines.Add(new Run { Text = "  " });
            var score = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            score.Children.Add(LumenUi.Icon("star", 14, true));
            var label = LumenUi.Text(rating.ToString("0.0", CultureInfo.InvariantCulture), 13);
            label.Foreground = LumenTheme.Brush("ImageInk");
            label.TextWrapping = TextWrapping.NoWrap;
            score.Children.Add(label);
            line.Inlines.Add(new InlineUIContainer { Child = score });
        }
        foreach (var tag in HeroTechnicalTags(item.Item))
        {
            if (line.Inlines.Count > 0) line.Inlines.Add(new Run { Text = "  " });
            var label = LumenUi.Text(tag, 11);
            label.Foreground = LumenTheme.Brush("ImageInk");
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Inlines.Add(new InlineUIContainer
            {
                Child = new Border
                {
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255)),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1, 6, 1), MaxWidth = 220, Child = label
                }
            });
        }
        _heroMetadata.Visibility = line.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _heroMetadata.Blocks.Add(line);
    }

    private static IEnumerable<string> HeroTechnicalTags(BaseItemDto item)
    {
        var streams = item.MediaStreams is { Length: > 0 } mediaStreams
            ? mediaStreams : item.MediaSources?.FirstOrDefault()?.MediaStreams ?? [];
        var video = streams.FirstOrDefault(stream => stream.Type == "Video");
        var audio = streams.FirstOrDefault(stream => stream.Type == "Audio" && stream.IsDefault == true)
            ?? streams.FirstOrDefault(stream => stream.Type == "Audio");
        var tags = new List<string>();
        if (video?.Height is >= 2160) tags.Add("4K");
        else if (video?.Height is > 0 and var height) tags.Add($"{height}p");
        var range = video?.ExtendedVideoSubTypeDescription?.Trim();
        if (string.IsNullOrWhiteSpace(range) || range.Equals("None", StringComparison.OrdinalIgnoreCase)
            || range.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) range = video?.VideoRange?.Trim();
        if (!string.IsNullOrWhiteSpace(range) && !range.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !range.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase))
            tags.Add(range);
        if (new[] { audio?.DisplayTitle, audio?.Title, audio?.Profile }
            .Any(value => value?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true)) tags.Add("Dolby Atmos");
        if (streams.Any(stream => stream.Type == "Subtitle" && stream.Language?.Trim().ToLowerInvariant()
            is "zh" or "chi" or "zho" or "chs" or "cht" or "zht" or "zh-cn" or "zh-tw" or "zh-hans" or "zh-hant"))
            tags.Add(LumenText.Get("Chinese subtitles"));
        return tags.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
