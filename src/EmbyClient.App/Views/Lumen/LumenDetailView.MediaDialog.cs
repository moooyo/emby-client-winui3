using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private Task ShowMediaInformationAsync()
    {
        var item = _library.Detail.IsFolder ? _library.PlayableDetail.Item : _library.Detail.Item;
        var detailId = _library.Detail.Id;
        return RunDetailActionAsync(async (session, token) =>
        {
            if (string.IsNullOrWhiteSpace(item.Id)) return;
            if (item.MediaSources is not { Length: > 0 } && item.MediaStreams is not { Length: > 0 })
                item = await session.Api.GetItemAsync(item.Id, token);
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id != detailId) return;
            var information = new MediaInformationViewModel(item);
            var content = new StackPanel { Spacing = 20, MinWidth = 420 };
            var identity = LumenUi.Text(item.Name ?? string.Empty, 20, true);
            content.Children.Add(identity);
            foreach (var source in information.Sources)
            {
                var section = new StackPanel { Spacing = 10 };
                var name = LumenUi.Text(source.Name, 16);
                name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                section.Children.Add(name);
                if (!string.IsNullOrWhiteSpace(source.Summary))
                {
                    var summary = LumenUi.Text(source.Summary, 12);
                    summary.Foreground = LumenTheme.Brush("Sub");
                    summary.TextWrapping = TextWrapping.Wrap;
                    section.Children.Add(summary);
                }
                foreach (var group in new[] { source.Video, source.Audio, source.Subtitles })
                {
                    if (group.Tracks.Count == 0) continue;
                    var heading = LumenUi.Text(LumenText.Get(group.Title), 14);
                    heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    heading.Margin = new Thickness(0, 8, 0, 0);
                    section.Children.Add(heading);
                    foreach (var track in group.Tracks)
                    {
                        var rows = new StackPanel { Spacing = 7 };
                        var title = LumenUi.Text(track.Title, 13);
                        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                        rows.Children.Add(title);
                        var visibleTags = track.Tags.Select(CleanVideoRange).Where(tag => tag.Length > 0).ToArray();
                        if (visibleTags.Length > 0)
                        {
                            var tags = LumenUi.Text(string.Join(" \u00b7 ", visibleTags.Select(tag => LumenText.Get(tag))), 12);
                            tags.Foreground = LumenTheme.Brush("Sub");
                            rows.Children.Add(tags);
                        }
                        if (!string.IsNullOrWhiteSpace(track.Summary)) rows.Children.Add(LumenUi.Text(track.Summary, 12));
                        var values = new Grid { RowSpacing = 6, ColumnSpacing = 14 };
                        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
                        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        foreach (var field in track.Fields.Concat(track.TechnicalFields))
                        {
                            var row = values.RowDefinitions.Count;
                            values.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                            var label = LumenUi.Text(LumenText.Get(field.Label), 12);
                            label.Foreground = LumenTheme.Brush("Muted");
                            Grid.SetRow(label, row);
                            values.Children.Add(label);
                            var value = LumenUi.Text(field.Value, 12);
                            value.TextWrapping = TextWrapping.Wrap;
                            Grid.SetRow(value, row);
                            Grid.SetColumn(value, 1);
                            values.Children.Add(value);
                        }
                        rows.Children.Add(values);
                        section.Children.Add(new Border { Child = rows, Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
                            Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(1) });
                    }
                }
                content.Children.Add(section);
                content.Children.Add(LumenUi.Divider());
            }
            if (information.Sources.Count == 0) content.Children.Add(LumenUi.Text(LumenText.Get("No media information is available."), 14));
            var scroll = new ScrollViewer { Content = content, MaxHeight = Math.Max(180, Math.Min(560, (XamlRoot?.Size.Height ?? 900) - 220)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var dialog = CreateDialog("Media information", scroll);
            dialog.CloseButtonText = LumenText.Get("Close");
            await ShowDialogAsync(dialog);
        });
    }
}
