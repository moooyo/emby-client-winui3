using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private MediaSourceInfo[] _sources = [];
    private string? _selectionItemId;
    private string? _selectedSourceKey;
    private readonly PlaybackAudioSelectionIntent _audioSelectionIntent = new();
    private int? _selectedAudioStreamIndex => _audioSelectionIntent.DisplayIndex;
    private int? _selectedSubtitleStreamIndex;
    private LumenPreferences _preferences = new();
    private string? _pendingSelectorFocus;
    private readonly List<(Button Button, TextBlock Heading, TextBlock Value)> _detailStreamSelectors = [];

    private MediaSourceInfo? SelectedSource => _sources.Select((source, index) => (source, key: SourceKey(source, index)))
        .FirstOrDefault(entry => entry.key == _selectedSourceKey).source ?? _sources.FirstOrDefault();
    private MediaStream[] SourceStreams => SelectedSource?.MediaStreams ?? [];

    private void ResetStreamSelection()
    {
        _pendingSelectorFocus = null;
        _pendingMediaSourceFocusKey = null;
        _detailStreamSelectors.Clear();
        _sources = [];
        _selectionItemId = _selectedSourceKey = null;
        _audioSelectionIntent.UseDefault(null);
        _selectedSubtitleStreamIndex = null;
    }

    private void UpdateStreamSelection(BaseItemDto item)
    {
        var newItem = _selectionItemId != item.Id;
        _selectionItemId = item.Id;
        _sources = item.MediaSources?.ToArray() ?? [];
        if (_sources.Length == 0 && item.MediaStreams is { Length: > 0 })
            _sources = [new MediaSourceInfo { MediaStreams = item.MediaStreams, RunTimeTicks = item.RunTimeTicks }];
        else if (_sources.Length > 0 && _sources[0].MediaStreams is not { Length: > 0 } && item.MediaStreams is { Length: > 0 })
            _sources[0] = _sources[0] with { MediaStreams = item.MediaStreams };
        var hasSource = _sources.Select((source, index) => SourceKey(source, index)).Contains(_selectedSourceKey);
        if (newItem || !hasSource)
        {
            _selectedSourceKey = _sources.Select((source, index) => (source, index))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.source.Id)).Select(entry => SourceKey(entry.source, entry.index)).FirstOrDefault()
                ?? (_sources.Length > 0 ? SourceKey(_sources[0], 0) : null);
            SelectDefaultStreams();
        }
        else
        {
            if (_selectedAudioStreamIndex is { } audioIndex && !SourceStreams.Any(stream => stream.Type == "Audio" && stream.Index == audioIndex))
                _audioSelectionIntent.UseDefault(DefaultAudioIndex());
            if (_selectedSubtitleStreamIndex is >= 0 and var subtitleIndex
                && !SourceStreams.Any(stream => stream.Type == "Subtitle" && stream.Index == subtitleIndex))
                _selectedSubtitleStreamIndex = DefaultSubtitleIndex();
        }
    }

    private void SelectDefaultStreams()
    {
        _audioSelectionIntent.UseDefault(DefaultAudioIndex());
        _selectedSubtitleStreamIndex = DefaultSubtitleIndex();
    }

    private int? DefaultAudioIndex()
    {
        var audio = SourceStreams.Where(stream => stream.Type == "Audio").ToArray();
        if (SelectedSource?.DefaultAudioStreamIndex is { } index && audio.Any(stream => stream.Index == index)) return index;
        var preference = _session?.User.Configuration?.AudioLanguagePreference;
        if (_session?.User.Configuration?.PlayDefaultAudioTrack != true && !string.IsNullOrWhiteSpace(preference)
            && audio.FirstOrDefault(stream => SameLanguage(stream.Language, preference)) is { } preferred) return preferred.Index;
        return audio.FirstOrDefault(stream => stream.IsDefault == true)?.Index ?? audio.FirstOrDefault()?.Index;
    }

    private int DefaultSubtitleIndex()
    {
        var subtitles = SourceStreams.Where(stream => stream.Type == "Subtitle").ToArray();
        if (_preferences.SubtitleMode is "None" or "Off" || subtitles.Length == 0) return -1;
        var serverDefault = SelectedSource?.DefaultSubtitleStreamIndex;
        if (serverDefault is not { } defaultIndex || defaultIndex >= 0 && !subtitles.Any(stream => stream.Index == defaultIndex))
            serverDefault = subtitles.FirstOrDefault(stream => stream.IsDefault == true)?.Index ?? -1;
        if (_preferences.SubtitleMode == "Default") return serverDefault ?? -1;
        var matches = string.IsNullOrWhiteSpace(_preferences.SubtitleLanguage) ? subtitles
            : subtitles.Where(stream => SubtitlePreferenceMatches(stream.Language, _preferences.SubtitleLanguage)).ToArray();
        var forced = matches.FirstOrDefault(stream => stream.IsForced == true);
        var preferred = matches.FirstOrDefault(stream => stream.IsDefault == true) ?? matches.FirstOrDefault();
        if (_preferences.SubtitleMode is "OnlyForced" or "Forced") return forced?.Index ?? -1;
        if (_preferences.SubtitleMode == "HearingImpaired") return matches.FirstOrDefault(stream => stream.IsHearingImpaired == true)?.Index
            ?? preferred?.Index ?? serverDefault ?? -1;
        if (_preferences.SubtitleMode == "Always") return preferred?.Index ?? serverDefault ?? -1;
        if (forced is not null) return forced.Index;
        var audio = SourceStreams.FirstOrDefault(stream => stream.Type == "Audio" && stream.Index == _selectedAudioStreamIndex);
        return audio is not null && SubtitlePreferenceMatches(audio.Language, _preferences.SubtitleLanguage) ? -1
            : preferred?.Index ?? serverDefault ?? -1;
    }

    private static bool SubtitlePreferenceMatches(string? language, string preferred) => preferred.ToLowerInvariant() switch
    {
        "chi" => language?.ToLowerInvariant() is "chi" or "zho" or "zh" or "chs" or "zh-cn" or "zh-hans",
        "zht" => language?.ToLowerInvariant() is "zht" or "cht" or "zh-tw" or "zh-hant",
        "eng" => language?.ToLowerInvariant() is "eng" or "en" or "en-us" or "en-gb",
        _ => string.Equals(language, preferred, StringComparison.OrdinalIgnoreCase)
    };

    private FlowPanel CreateStreamSelectors()
    {
        _detailStreamSelectors.Clear();
        var row = new FlowPanel { Spacing = 10, RowSpacing = 10 };
        row.Children.Add(CreateStreamSelector("Version", VersionTitle(SelectedSource), () => _sources.Select((source, index) =>
            new StreamOption(SourceKey(source, index), VersionTitle(source), SourceSummary(source), SourceKey(source, index) == _selectedSourceKey,
                () => { _selectedSourceKey = SourceKey(source, index); SelectDefaultStreams(); }, !string.IsNullOrWhiteSpace(source.Id))).ToArray(),
                _sources.Length > 0));
        var audio = SourceStreams.Where(stream => stream.Type == "Audio").ToArray();
        var selectedAudio = audio.FirstOrDefault(stream => stream.Index == _selectedAudioStreamIndex);
        row.Children.Add(CreateStreamSelector("Audio track", selectedAudio is null ? LumenText.Get("No audio tracks") : StreamTitle(selectedAudio),
            () => audio.Select(stream => new StreamOption(stream.Index.ToString(CultureInfo.InvariantCulture), StreamTitle(stream), StreamSummary(stream),
                stream.Index == _selectedAudioStreamIndex, () => _audioSelectionIntent.Select(stream.Index))).ToArray(), audio.Length > 0));
        var subtitles = SourceStreams.Where(stream => stream.Type == "Subtitle").ToArray();
        var selectedSubtitle = subtitles.FirstOrDefault(stream => stream.Index == _selectedSubtitleStreamIndex);
        row.Children.Add(CreateStreamSelector("Subtitles", selectedSubtitle is null ? LumenText.Get("Off") : StreamTitle(selectedSubtitle), () =>
        {
            var options = new List<StreamOption> { new("off", LumenText.Get("Off"), string.Empty, _selectedSubtitleStreamIndex is null or < 0,
                () => _selectedSubtitleStreamIndex = -1) };
            options.AddRange(subtitles.Select(stream => new StreamOption(stream.Index.ToString(CultureInfo.InvariantCulture), StreamTitle(stream),
                StreamSummary(stream), stream.Index == _selectedSubtitleStreamIndex, () => _selectedSubtitleStreamIndex = stream.Index)));
            return options.ToArray();
        }, subtitles.Length > 0));
        return row;
    }

    private Button CreateStreamSelector(string label, string value, Func<StreamOption[]> options, bool enabled)
    {
        var button = LumenUi.Button(string.Empty, onImage: true, height: 60);
        button.Height = double.NaN;
        button.Padding = new Thickness(14, 8, 14, 8);
        button.CornerRadius = new CornerRadius(14);
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LumenUi.SetResource(button.Resources, "ButtonBackground", button.Background);
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", ImageBrush(20));
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPressed", ImageBrush(30));
        button.IsEnabled = enabled && !_actionBusy;
        var content = new Grid { ColumnSpacing = 6 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var heading = ImageText(LumenText.Get(label), 12, opacity: .55, weight: true);
        heading.CharacterSpacing = 160;
        heading.TextWrapping = TextWrapping.NoWrap;
        text.Children.Add(heading);
        var current = ImageText(value, 15, weight: true);
        current.TextWrapping = TextWrapping.NoWrap;
        current.MaxLines = 1;
        current.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(current);
        content.Children.Add(text);
        var chevron = LumenUi.Icon("chevron-down", 13, true);
        chevron.VerticalAlignment = VerticalAlignment.Bottom;
        chevron.Margin = new Thickness(0, 0, 0, 4);
        chevron.Opacity = .6;
        Grid.SetColumn(chevron, 1);
        content.Children.Add(chevron);
        button.Content = content;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _detailStreamSelectors.Add((button, heading, current));
        UpdateStreamSelectorWidths();
        ToolTipService.SetToolTip(button, LumenText.Get(label) + ": " + value);
        AutomationProperties.SetName(button, LumenText.Get(label) + ": " + value);
        var popup = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, FlyoutPresenterStyle = PopupStyle(300) };
        popup.Opening += (_, _) =>
        {
            _hasOpenDetailFlyout = true;
            UpdateStageInteraction();
            var list = new StackPanel { Spacing = 2, MinWidth = 286 };
            var menuHeading = ImageText(LumenText.Get(label), 12, opacity: .5, weight: true);
            menuHeading.CharacterSpacing = 140;
            menuHeading.Margin = new Thickness(12, 8, 12, 6);
            list.Children.Add(menuHeading);
            foreach (var option in options())
            {
                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = option.Selected ? ImageBrush(20) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    IsEnabled = option.Enabled,
                    BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 9, 18, 9), MinHeight = 44
                };
                var item = new Grid { ColumnSpacing = 12 };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                var check = LumenUi.Icon("check", 16, true);
                check.Opacity = option.Selected ? 1 : 0;
                check.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(check, 1);
                item.Children.Add(check);
                var labels = new StackPanel { Spacing = 3 };
                var primary = ImageText(option.Title, 14, weight: true);
                primary.TextWrapping = TextWrapping.Wrap;
                labels.Children.Add(primary);
                if (!string.IsNullOrWhiteSpace(option.Summary))
                {
                    var secondary = ImageText(option.Summary, 12, opacity: .55);
                    secondary.TextWrapping = TextWrapping.Wrap;
                    labels.Children.Add(secondary);
                }
                item.Children.Add(labels);
                row.Content = item;
                AutomationProperties.SetName(row, option.Title + (option.Selected ? ", " + LumenText.Get("Selected") : string.Empty));
                row.Click += (_, _) =>
                {
                    popup.Hide();
                    option.Select();
                    _pendingSelectorFocus = label;
                    _forceRender = true;
                    Refresh();
                };
                list.Children.Add(row);
            }
            popup.Content = list;
            chevron.RenderTransform = new RotateTransform { Angle = 180, CenterX = 6, CenterY = 6 };
        };
        popup.Closed += (_, _) =>
        {
            chevron.RenderTransform = null;
            _hasOpenDetailFlyout = false;
            UpdateStageInteraction();
        };
        button.Flyout = popup;
        if (_pendingSelectorFocus == label)
        {
            _pendingSelectorFocus = null;
            DispatcherQueue.TryEnqueue(() => button.Focus(FocusState.Programmatic));
        }
        return button;
    }

    private void UpdateStreamSelectorWidths()
    {
        var viewportWidth = ActualWidth > 0 ? ActualWidth : 1280;
        var available = Math.Max(120, Math.Min(1040, viewportWidth - 2 * DetailInset + 14));
        foreach (var (button, heading, value) in _detailStreamSelectors)
        {
            heading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = Math.Max(heading.DesiredSize.Width, value.DesiredSize.Width);
            button.Width = Math.Min(available, Math.Ceiling(textWidth) + 28 + 13 + 6);
        }
    }

    private sealed record StreamOption(string Key, string Title, string Summary, bool Selected, Action Select, bool Enabled = true);
    private static string SourceKey(MediaSourceInfo source, int index) => !string.IsNullOrWhiteSpace(source.Id) ? source.Id : $"source-{index}";
}
