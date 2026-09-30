using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RenderCast(MediaCardViewModel detail)
    {
        _castSection.Children.Clear();
        _castScroll = null;
        var people = (detail.Item.People ?? []).Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .DistinctBy(person => person.Id ?? person.Name, StringComparer.Ordinal).Take(24).ToArray();
        _castSection.Visibility = people.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (people.Length == 0) return;
        _castSection.Children.Add(SectionHeading("Cast & crew"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26 };
        foreach (var person in people)
        {
            var item = new MediaCardViewModel(new BaseItemDto
            {
                Id = person.Id, Name = person.Name, Type = "Person",
                ImageTags = string.IsNullOrWhiteSpace(person.PrimaryImageTag) ? null : new() { ["Primary"] = person.PrimaryImageTag }
            }, person.Role ?? person.Type);
            var labels = new StackPanel { Width = 96, Spacing = 0 };
            var portrait = new Grid { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
            var circle = new Border { Background = PersonColor(person.Name!), CornerRadius = new CornerRadius(42), BorderBrush = ImageBrush(31), BorderThickness = new Thickness(1) };
            portrait.Children.Add(circle);
            var initial = ImageText(StringInfo.GetNextTextElement(person.Name!.Trim()), 26, weight: true);
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            if (!string.IsNullOrWhiteSpace(person.PrimaryImageTag) && !string.IsNullOrWhiteSpace(person.Id))
            {
                var artwork = new LumenArtwork { CornerRadius = new CornerRadius(42), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                artwork.Set(item, _library, ArtworkKind.Poster, 192);
                artwork.Image.ImageOpened += (_, _) => initial.Visibility = Visibility.Collapsed;
                portrait.Children.Add(artwork);
            }
            portrait.Children.Add(initial);
            labels.Children.Add(portrait);
            var name = LumenUi.Text(person.Name!, 13);
            name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxLines = 2;
            name.Margin = new Thickness(0, 10, 0, 0);
            labels.Children.Add(name);
            var role = LumenUi.Text(!string.IsNullOrWhiteSpace(person.Role) ? person.Role : LumenText.Get(person.Type ?? "Cast"), 12);
            role.Foreground = LumenTheme.Brush("Muted");
            role.TextAlignment = TextAlignment.Center;
            role.TextWrapping = TextWrapping.Wrap;
            role.TextTrimming = TextTrimming.CharacterEllipsis;
            role.MaxLines = 2;
            role.Margin = new Thickness(0, 2, 0, 0);
            labels.Children.Add(role);
            var button = new Button
            {
                Content = labels, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(8),
                IsEnabled = !string.IsNullOrWhiteSpace(item.Id), UseSystemFocusVisuals = true
            };
            button.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("Control");
            AutomationProperties.SetName(button, person.Name + (string.IsNullOrWhiteSpace(person.Role) ? string.Empty : ", " + person.Role));
            button.Click += (_, _) => PersonRequested?.Invoke(this, item);
            row.Children.Add(button);
        }
        _castScroll = HorizontalScroll(row);
        _castSection.Children.Add(_castScroll);
    }

    private void RenderMediaInformation(MediaCardViewModel detail)
    {
        _mediaSection.Children.Clear();
        _mediaTiles = null;
        _mediaSection.Visibility = SelectedSource is not null && !detail.IsFolder ? Visibility.Visible : Visibility.Collapsed;
        if (_mediaSection.Visibility == Visibility.Collapsed) return;
        _mediaSection.Children.Add(SectionHeading("Media information"));
        _mediaTiles = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        var source = SelectedSource!;
        var video = SourceStreams.FirstOrDefault(stream => stream.Type == "Video");
        var audio = SourceStreams.FirstOrDefault(stream => stream.Type == "Audio" && stream.Index == _selectedAudioStreamIndex)
            ?? SourceStreams.FirstOrDefault(stream => stream.Type == "Audio");
        var subtitle = SourceStreams.FirstOrDefault(stream => stream.Type == "Subtitle" && stream.Index == _selectedSubtitleStreamIndex);
        var videoRows = new List<string>();
        if (video is not null)
        {
            AddInformation(videoRows, video.Codec?.ToUpperInvariant(), video.Profile);
            AddInformation(videoRows, video.Width is > 0 && video.Height is > 0 ? $"{video.Width} \u00d7 {video.Height}" : video.Height is > 0 ? $"{video.Height}p" : null,
                video.AverageFrameRate is > 0 ? $"{video.AverageFrameRate.Value:0.###} fps" : video.RealFrameRate is > 0 ? $"{video.RealFrameRate.Value:0.###} fps" : null);
            AddInformation(videoRows, VideoRange(video), video.BitDepth is > 0 ? $"{video.BitDepth} bit" : null);
            AddInformation(videoRows, video.BitRate is > 0 ? Bitrate(video.BitRate.Value) : null);
        }
        var audioRows = new List<string>();
        if (audio is not null)
        {
            AddInformation(audioRows, StreamTitle(audio));
            AddInformation(audioRows, audio.Codec?.ToUpperInvariant(), ChannelName(audio));
            AddInformation(audioRows, audio.SampleRate is > 0 ? $"{audio.SampleRate.Value / 1000d:0.##} kHz" : null,
                audio.BitRate is > 0 ? Bitrate(audio.BitRate.Value) : null);
            if (SourceStreams.Count(stream => stream.Type == "Audio") > 1)
                audioRows.Add(Format("{0} audio tracks", SourceStreams.Count(stream => stream.Type == "Audio")));
        }
        var subtitleRows = new List<string> { subtitle is null ? LumenText.Get("Off") : StreamTitle(subtitle) };
        if (subtitle is not null) AddInformation(subtitleRows, StreamSummary(subtitle));
        if (SourceStreams.Any(stream => stream.Type == "Subtitle")) subtitleRows.Add(Format("{0} subtitle tracks", SourceStreams.Count(stream => stream.Type == "Subtitle")));
        var fileRows = new List<string>();
        AddInformation(fileRows, SafeFileName(source.Path) ?? source.Name);
        AddInformation(fileRows, source.Container?.ToUpperInvariant(), source.Size is > 0 ? FileSize(source.Size.Value) : null);
        AddInformation(fileRows, source.Bitrate is > 0 ? Bitrate(source.Bitrate.Value) : null,
            source.RunTimeTicks is > 0 ? Runtime(source.RunTimeTicks.Value) : null);
        _mediaTiles.Children.Add(InformationTile("Video", "film", videoRows));
        _mediaTiles.Children.Add(InformationTile("Audio", "music", audioRows));
        _mediaTiles.Children.Add(InformationTile("Subtitles", "captions", subtitleRows));
        _mediaTiles.Children.Add(InformationTile("File", "server", fileRows));
        _mediaSection.Children.Add(_mediaTiles);
        UpdateResponsiveLayout();
    }

    private static Border InformationTile(string label, string icon, IReadOnlyList<string> rows)
    {
        var content = new StackPanel { Spacing = 8 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(LumenUi.Icon(icon, 16));
        var title = LumenUi.Text(LumenText.Get(label), 13);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        heading.Children.Add(title);
        content.Children.Add(heading);
        foreach (var row in rows.Count > 0 ? rows : new[] { LumenText.Get("Not available") })
        {
            var text = LumenUi.Text(row, 12);
            text.Foreground = LumenTheme.Brush("Sub");
            text.TextWrapping = TextWrapping.Wrap;
            text.LineHeight = 17.4;
            content.Children.Add(text);
        }
        return new Border { Background = LumenTheme.Brush("Card"), BorderBrush = LumenTheme.Brush("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 16, 18, 16), Child = content };
    }

    private static void AddInformation(ICollection<string> rows, params string?[] values)
    {
        var content = string.Join(" \u00b7 ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (content.Length > 0) rows.Add(content);
    }

    private void RenderSimilarItems(MediaCardViewModel detail)
    {
        _similarSection.Children.Clear();
        _similarScroll = null;
        _similarSection.Visibility = _supplementaryLoading || _similarFailed || _similarItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_similarSection.Visibility == Visibility.Collapsed) return;
        _similarSection.Children.Add(SectionHeading(detail.Item.Type is "Series" or "Season" or "Episode" ? "Similar series"
            : detail.Item.Type == "Movie" ? "Similar movies" : "Similar items"));
        if (_similarItems.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(0, 6, 0, 10) };
            foreach (var item in _similarItems) row.Children.Add(CreateMediaCard(item));
            _similarScroll = HorizontalScroll(row);
            _similarSection.Children.Add(_similarScroll);
        }
        if (_supplementaryLoading)
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, Foreground = LumenTheme.Brush("Accent") });
            loading.Children.Add(LumenUi.Text(LumenText.Get("Loading recommendations..."), 13));
            _similarSection.Children.Add(loading);
        }
        else if (_similarFailed)
        {
            var error = LumenUi.Text(LumenText.Get("Recommendations could not be loaded."), 13);
            error.Foreground = LumenTheme.Brush("Sub");
            _similarSection.Children.Add(error);
            var retry = LumenUi.Button(LumenText.Get("Try again"), "refresh");
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Click += async (_, _) =>
            {
                if (_detailId is not null && _detailCancellation is not null)
                    await LoadSupplementaryAsync(_detailId, _detailVersion, _detailCancellation.Token);
            };
            _similarSection.Children.Add(retry);
        }
    }

    private static SolidColorBrush PersonColor(string name)
    {
        Color[] colors = [Color.FromArgb(255, 128, 65, 70), Color.FromArgb(255, 23, 103, 107), Color.FromArgb(255, 72, 101, 64),
            Color.FromArgb(255, 105, 82, 116), Color.FromArgb(255, 103, 83, 33), Color.FromArgb(255, 61, 98, 128),
            Color.FromArgb(255, 123, 77, 58), Color.FromArgb(255, 41, 105, 85), Color.FromArgb(255, 91, 81, 129)];
        var hash = 17;
        foreach (var character in name) hash = unchecked(hash * 31 + character);
        return new SolidColorBrush(colors[(int)((uint)hash % (uint)colors.Length)]);
    }
}
