using System.Globalization;
using System.Net;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private LumenPreferences _preferences = new();
    private double _subtitleDelay;
    private Guid? _subtitlePlaybackId;
    private Guid? _appliedSubtitlePlaybackId;
    private bool _externalSubtitlePickerOpen;
    private bool _playerTextLocalized;
    private bool _subtitleLayoutLoaded;
    private bool _subtitleLayoutEnabled;
    private long _subtitleLayoutEpoch;
    private bool _subtitleLayoutQueued;
    private string _subtitleText = string.Empty;
    private string? _localSubtitleName;
    private Guid? _localSubtitlePlaybackId;
    private readonly List<NativePreparedSubtitle> _localSubtitleChoices = [];
    private NativePreparedSubtitle? _selectedLocalSubtitle;
    private Guid? _localSubtitleRestorePlaybackId;
    private readonly Dictionary<StackPanel, TrackRowsSnapshot> _trackRowsSnapshots = [];

    private sealed record TrackRowSnapshot(ComboBoxItem Option, object? Content, object? Tag, string Label);
    private sealed record TrackRowsSnapshot(ComboBox Selector, object? SelectedItem, bool IsEnabled,
        bool CanClearLocalSubtitle, string? LocalSubtitleName, string Empty, TrackRowSnapshot[] Rows);

    public event EventHandler<LumenPreferences>? PreferencesChanged;

    public void ApplyPreferences(LumenPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
        _engine?.ConfigureVideoDecoderApi(preferences.VideoDecoderApi);
        _engine?.ConfigureHdrOutput(preferences.HdrMode,
            App.CurrentWindow is MainWindow window ? window.AppWindow.Id.Value : 0);
        _engine?.SetSystemMediaControlsEnabled(preferences.SystemMediaControls);
        _displayRequest.SetEnabled(preferences.PreventSleepDuringPlayback);
        UpdateDisplayRequest(_coordinator?.ActiveContext?.PlaybackId, useCurrentPlayback: true);
        _synchronizingAutoPlay = true;
        AutoPlayNext.IsOn = QueueAutoPlayNext.IsOn = preferences.AutoPlayNext;
        _synchronizingAutoPlay = false;
        FontFamily = LumenTheme.SansFont;
        PlaybackEndedTitle.FontFamily = LumenTheme.SerifFont;
        var accentColor = preferences.Accent switch
        {
            "Coral" => Windows.UI.Color.FromArgb(255, 232, 134, 106),
            "Sage" => Windows.UI.Color.FromArgb(255, 159, 194, 164),
            "Blue" => Windows.UI.Color.FromArgb(255, 169, 184, 232),
            _ => Windows.UI.Color.FromArgb(255, 226, 184, 107)
        };
        var accent = Resources["PlayerAccentBrush"] as SolidColorBrush;
        if (accent is not null) accent.Color = accentColor;
        foreach (var key in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed" })
            if (PlayerRoot.Resources[key] is SolidColorBrush brush) brush.Color = accentColor;
        var desiredBitrate = IsLocalServer(_session?.Api.ApiRoot) ? preferences.LocalMaxBitrate : preferences.InternetMaxBitrate;
        _bitrate = desiredBitrate <= 0 ? int.MaxValue : Math.Max(1_500_000, desiredBitrate);
        if (_session?.User.Policy?.RemoteClientBitrateLimit is long limit && limit > 0) _bitrate = Math.Min(_bitrate, limit);
        PopulateQualityOptions(_bitrate);
        UpdateSelectionPresentation();
        UpdateEpisodeDrawerControls();
        _appliedSubtitlePlaybackId = null;
        UpdateSubtitleOverlayLayout();
        QueueCaptionLayout();
        ApplySubtitlePreferencesToEngine();
        RefreshChapterControls();
        UpdateTaskbarState();
        _ = RefreshHdrDisplayEligibilityAsync();
    }

    private static bool IsLocalServer(Uri? uri)
    {
        if (uri is null) return false;
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.Host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

    private int? PreferredSubtitleIndex(BaseItemDto item, string? mediaSourceId, int? audioStreamIndex)
    {
        if (_preferences.SubtitleMode == "Default") return null;
        if (_preferences.SubtitleMode == "None") return -1;
        var source = item.MediaSources?.FirstOrDefault(value => value.Id == mediaSourceId) ?? item.MediaSources?.FirstOrDefault();
        var streams = source?.MediaStreams is { Length: > 0 } ? source.MediaStreams : item.MediaStreams ?? [];
        var subtitles = streams.Where(value => string.Equals(value.Type, "Subtitle", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (subtitles.Length == 0) return null;
        bool LanguageMatches(MediaStream stream) => SubtitleLanguageMatches(stream.Language, _preferences.SubtitleLanguage);
        var matches = string.IsNullOrWhiteSpace(_preferences.SubtitleLanguage) ? subtitles : subtitles.Where(LanguageMatches).ToArray();
        var forced = matches.FirstOrDefault(value => value.IsForced == true);
        if (_preferences.SubtitleMode is "OnlyForced" or "Forced") return forced?.Index ?? -1;
        var preferred = matches.FirstOrDefault(value => value.IsDefault == true) ?? matches.FirstOrDefault();
        if (_preferences.SubtitleMode == "HearingImpaired") return matches.FirstOrDefault(value => value.IsHearingImpaired == true)?.Index
            ?? preferred?.Index ?? source?.DefaultSubtitleStreamIndex;
        if (_preferences.SubtitleMode == "Always") return preferred?.Index ?? source?.DefaultSubtitleStreamIndex;
        if (forced is not null) return forced.Index;
        var audioIndex = audioStreamIndex ?? source?.DefaultAudioStreamIndex;
        var audio = streams.FirstOrDefault(value => value.Index == audioIndex && string.Equals(value.Type, "Audio", StringComparison.OrdinalIgnoreCase));
        if (audio is not null && !string.IsNullOrWhiteSpace(_preferences.SubtitleLanguage) && LanguageMatches(audio)) return -1;
        return preferred?.Index ?? source?.DefaultSubtitleStreamIndex;
    }

    private static bool SubtitleLanguageMatches(string? value, string desired) => desired.ToLowerInvariant() switch
    {
        "chi" => value?.ToLowerInvariant() is "chi" or "zho" or "zh" or "chs" or "zh-cn" or "zh-hans",
        "zht" => value?.ToLowerInvariant() is "zht" or "cht" or "zh-tw" or "zh-hant",
        "eng" => value?.ToLowerInvariant() is "eng" or "en" or "en-us" or "en-gb",
        _ => string.Equals(value, desired, StringComparison.OrdinalIgnoreCase)
    };

    private static Image PlayerIcon(string name, bool dark = false, double size = 24) => new()
    {
        Width = size, Height = size, Stretch = Stretch.Uniform,
        Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/{(dark ? "d" : "w")}/{name}.svg"))
    };

    private void TracksClicked(object sender, RoutedEventArgs args)
    {
        if (_sidePanelMode == PlayerSidePanel.Tracks) { CloseSidePanel(); return; }
        OpenSidePanel(PlayerSidePanel.Tracks, SubtitleButton);
    }

    private void EpisodeDrawerClicked(object sender, RoutedEventArgs args)
    {
        if (_sidePanelMode is PlayerSidePanel.Episodes or PlayerSidePanel.Queue) { CloseSidePanel(); return; }
        OpenSidePanel(string.Equals(_item?.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            ? PlayerSidePanel.Episodes : PlayerSidePanel.Queue, EpisodeDrawerButton);
    }

    private void UpdatePanelButtonStates()
    {
        SubtitleButton.Background = new SolidColorBrush(_sidePanelMode == PlayerSidePanel.Tracks
            ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Colors.Transparent);
        EpisodeDrawerButton.Background = new SolidColorBrush(_sidePanelMode is PlayerSidePanel.Episodes or PlayerSidePanel.Queue
            ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Colors.Transparent);
    }

    private void RebuildTrackPanel()
    {
        BuildTrackRows(AudioSelector, AudioTrackItems, "No audio track");
        BuildTrackRows(SubtitleSelector, SubtitleTrackItems, "No subtitle track");
    }

    private void BuildTrackRows(ComboBox selector, StackPanel host, string empty)
    {
        var isSubtitle = ReferenceEquals(selector, SubtitleSelector);
        var canClearLocalSubtitle = isSubtitle && CanClearCurrentLocalSubtitle();
        var rows = selector.Items.OfType<ComboBoxItem>().Select(option => new TrackRowSnapshot(
            option, option.Content, option.Tag, option.Content?.ToString() ?? LumenText.Get("Off"))).ToArray();
        var snapshot = new TrackRowsSnapshot(selector, selector.SelectedItem, selector.IsEnabled,
            canClearLocalSubtitle, isSubtitle ? _localSubtitleName : null, empty, rows);
        if (_trackRowsSnapshots.TryGetValue(host, out var previous)
            && ReferenceEquals(previous.Selector, snapshot.Selector)
            && ReferenceEquals(previous.SelectedItem, snapshot.SelectedItem)
            && previous.IsEnabled == snapshot.IsEnabled
            && previous.CanClearLocalSubtitle == snapshot.CanClearLocalSubtitle
            && string.Equals(previous.LocalSubtitleName, snapshot.LocalSubtitleName, StringComparison.Ordinal)
            && string.Equals(previous.Empty, snapshot.Empty, StringComparison.Ordinal)
            && previous.Rows.Length == rows.Length
            && previous.Rows.Zip(rows).All(pair => ReferenceEquals(pair.First.Option, pair.Second.Option)
                && Equals(pair.First.Content, pair.Second.Content) && Equals(pair.First.Tag, pair.Second.Tag)
                && string.Equals(pair.First.Label, pair.Second.Label, StringComparison.Ordinal))) return;
        // Retain focus during periodic reports, but replace handlers when selector options are replaced.
        host.Children.Clear();
        foreach (var row in rows)
        {
            var option = row.Option;
            var selected = ReferenceEquals(selector.SelectedItem, option) && (!isSubtitle || _localSubtitleName is null);
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (selected) content.Children.Add(PlayerIcon("checkmark_16_filled", size: 14));
            var label = new TextBlock
            {
                Text = row.Label, FontSize = 13,
                Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            var button = new Button
            {
                Style = (Style)Resources["PlayerTrackRowStyle"], Content = content,
                Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(20, 255, 255, 255) : Colors.Transparent),
                IsEnabled = selector.IsEnabled || canClearLocalSubtitle && row.Tag is -1
            };
            SetControlLabel(button, label.Text);
            AutomationProperties.SetHelpText(button, selected ? LumenText.Get("Selected") : string.Empty);
            button.Click += async (_, _) =>
            {
                if (!selector.IsEnabled && !(isSubtitle && option.Tag is -1 && CanClearCurrentLocalSubtitle())) return;
                if (isSubtitle && ReferenceEquals(selector.SelectedItem, option))
                {
                    BeginExplicitSubtitleSelection();
                    if (option.Tag is -1 || _localSubtitleName is not null || _selectedLocalSubtitle is not null)
                        await ClearCurrentLocalSubtitleAsync();
                    if (option.Tag is int selectedIndex && _coordinator?.ActiveContext is { } active
                        && (active.Selection.SubtitleStreamIndex ?? active.Source.DefaultSubtitleStreamIndex ?? -1) == selectedIndex)
                        RememberServerSubtitleSelection(selectedIndex);
                    RebuildTrackPanel();
                    return;
                }
                selector.SelectedItem = option;
                RebuildTrackPanel();
            };
            host.Children.Add(button);
        }
        if (host.Children.Count == 0)
            host.Children.Add(new TextBlock { Text = LumenText.Get(empty), FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 142, 135, 124)), Margin = new Thickness(12, 10, 12, 10), TextWrapping = TextWrapping.Wrap });
        if (ReferenceEquals(selector, SubtitleSelector))
        {
            foreach (var prepared in _localSubtitleChoices)
            {
                var selected = IsLocalSubtitleChoiceSelected(prepared);
                var content = new Grid { ColumnSpacing = 10 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (selected) content.Children.Add(PlayerIcon("checkmark_16_filled", size: 14));
                var label = new TextBlock { Text = LocalSubtitleDisplayName(prepared.FileName), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(label, 1);
                content.Children.Add(label);
                var local = new Button { Style = (Style)Resources["PlayerTrackRowStyle"], Content = content,
                    IsEnabled = CanLoadLocalSubtitle(),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(selected ? (byte)20 : (byte)0, 255, 255, 255)) };
                SetControlLabel(local, LumenText.Get("Local subtitles: {0}", label.Text));
                ToolTipService.SetToolTip(local, LocalSubtitleDisplayName(LocalSubtitleChoicePath(prepared) ?? prepared.FileName));
                local.Click += async (_, _) =>
                {
                    if (CanLoadLocalSubtitle()) await ActivateLocalSubtitleChoiceAsync(prepared);
                };
                host.Children.Add(local);
            }
        }
        _trackRowsSnapshots[host] = snapshot;
    }

    private void UpdateStreamPresentation(PlaybackContext context)
    {
        var direct = context.DeliveryMethod == PlaybackDeliveryMethod.DirectStream;
        StateText.Text = LumenText.Get(direct ? "Direct play" : "Transcoding");
        StreamOnlineDot.Fill = direct ? LumenTheme.Brush("Online") : Resources["PlayerAccentBrush"] as Brush;
        var video = context.Source.MediaStreams?.FirstOrDefault(value => string.Equals(value.Type, "Video", StringComparison.OrdinalIgnoreCase));
        var currentEngine = _engine?.Snapshot?.PlaybackId == context.PlaybackId;
        var decoding = currentEngine ? _engine?.VideoDecoding : null;
        var serverTranscoding = CurrentServerTranscodingInformation(context);
        var format = new List<string>();
        if (currentEngine && _engine?.VideoHeight is > 0) format.Add($"{_engine.VideoHeight}p");
        else if (direct && video?.Height is > 0) format.Add($"{video.Height}p");
        else if (!direct && serverTranscoding?.Height is > 0) format.Add($"{serverTranscoding.Height}p");
        var codec = direct ? video?.Codec : decoding is { ActualApi: not "Pending", Codec: not "Unknown" }
            ? decoding.Codec : serverTranscoding?.VideoCodec;
        if (!string.IsNullOrWhiteSpace(codec)) format.Add(codec.ToUpperInvariant());
        if (direct && !string.IsNullOrWhiteSpace(video?.VideoRange) && video.VideoRange != "SDR")
            format.Add(LumenText.Get("Source {0}", video.VideoRange));
        StreamInfoText.Text = string.Join(" \u00b7 ", format);
        if (direct && context.Source.Bitrate is > 0)
            StreamInfoText.Text += $" | {context.Source.Bitrate.Value / 1_000_000d:0.#} Mbps";
        if (decoding is { ActualApi: not "Pending" })
        {
            StreamInfoText.Text = string.Join(" | ", new[] { StreamInfoText.Text, DecoderApiName(decoding.ActualApi) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        ToolTipService.SetToolTip(StreamInfoPill, $"{StateText.Text} | {StreamInfoText.Text} | {PlaybackBitrateInformation(context, video)}");
    }

    private void RateMenuOpening(object sender, object args)
    {
        RateMenu.Items.Clear();
        foreach (var rate in new[] { .5, .75, 1, 1.25, 1.5, 2 })
        {
            var entry = new ToggleMenuFlyoutItem
            {
                Text = rate.ToString(rate == 1 ? "0.0" : "0.##", CultureInfo.InvariantCulture) + "\u00d7",
                IsChecked = Math.Abs((_engine?.PlaybackRate ?? 1) - rate) < .001,
                IsEnabled = _engine?.SupportsPlaybackRate == true
            };
            entry.Click += async (_, _) =>
            {
                var engine = _engine;
                var playbackId = _coordinator?.ActiveContext?.PlaybackId;
                if (engine is null || playbackId is null) return;
                await RunAsync(async () =>
                {
                    if (!ReferenceEquals(engine, _engine) || _coordinator?.ActiveContext?.PlaybackId != playbackId) return;
                    await engine.SetPlaybackRateAsync(playbackId.Value, rate);
                    if (ReferenceEquals(engine, _engine))
                    {
                        RateText.Text = FormatPlaybackRate(engine.PlaybackRate);
                        SavePlaybackPreferences();
                        ShowPlayerOsd("top_speed_24_regular", LumenText.Get("Speed {0}", RateText.Text));
                    }
                });
            };
            RateMenu.Items.Add(entry);
        }
        QuickFlyoutOpening(sender, args);
    }

    private void VolumeButtonClicked(object sender, RoutedEventArgs args)
    {
        MuteButton.IsChecked = MuteButton.IsChecked != true;
        MuteClicked(sender, args);
    }

    private void PictureInPictureClicked(object sender, RoutedEventArgs args) => CompactOverlayRequested?.Invoke(this, EventArgs.Empty);

    private static string FormatPlaybackRate(double rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "\u00d7";

    private double PreferredSubtitleFontSize => _preferences.SubtitleSize switch { "Small" => 24, "Large" => 38, _ => 30 };

    private void NativeSubtitleTextChanged(object? sender, NativeSubtitleTextChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        if (_coordinator?.ActiveContext?.PlaybackId != args.PlaybackId)
        {
            if (args.Text.Length == 0 && _subtitlePlaybackId == args.PlaybackId)
                ClearSubtitleOverlay(preserveLocalPresentation: _localSubtitlePlaybackId is { } localId && localId != args.PlaybackId);
            return;
        }
        _subtitlePlaybackId = args.PlaybackId;
        _subtitleText = args.Text;
        SetSubtitleText(SubtitleText, args.Text, args.FontSize);
        foreach (var outline in SubtitleOutlineLayers())
        {
            SetSubtitleText(outline, args.Text, args.FontSize);
            outline.Visibility = args.Outline ? Visibility.Visible : Visibility.Collapsed;
        }
        SubtitleOverlay.Visibility = args.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSubtitleOverlayLayout();
        QueueCaptionLayout();
        UpdateSubtitleControls();
    }

    private IEnumerable<TextBlock> SubtitleOutlineLayers() => [SubtitleOutlineText, SubtitleOutlineLeft, SubtitleOutlineRight, SubtitleOutlineTop, SubtitleOutlineBottom];

    private static void SetSubtitleText(TextBlock target, string text, double fontSize)
    {
        target.Inlines.Clear();
        target.FontSize = fontSize;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var bilingual = lines.Length == 2 && lines[0].Any(character => character is >= '\u4e00' and <= '\u9fff')
            && lines[1].Any(character => char.IsAsciiLetter(character));
        if (bilingual)
        {
            target.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = lines[0], FontSize = fontSize });
            target.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            target.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = lines[1], FontSize = fontSize * .72 });
        }
        else target.Text = text;
    }

    private void ClearSubtitleOverlay(bool preserveLocalPresentation = false)
    {
        _subtitlePlaybackId = _appliedSubtitlePlaybackId = null;
        if (!preserveLocalPresentation)
        {
            _localSubtitlePlaybackId = null;
            _localSubtitleName = null;
        }
        _subtitleText = string.Empty;
        SubtitleText.Text = string.Empty;
        foreach (var outline in SubtitleOutlineLayers()) outline.Text = string.Empty;
        SubtitleOverlay.Visibility = Visibility.Collapsed;
        VideoSurface.Margin = new Thickness(0);
        UpdateChapterPreviewClearance();
        QueueCaptionLayout();
    }

    private bool UsesNarrowCaptionBand => !_compactOverlay && PlayerRoot.ActualWidth < 780
        && _preferences.SubtitlePosition != "BlackBars"
        && (_sidePanelMode is PlayerSidePanel.Tracks or PlayerSidePanel.Episodes)
        && _subtitleText.Length > 0 && SubtitleOverlay.Visibility == Visibility.Visible;

    private void UpdateSubtitleOverlayLayout()
    {
        if (SubtitleOverlay is null || VideoSurface is null) return;
        var editable = _engine?.SupportsSubtitleStyling == true;
        var blackBars = editable && _preferences.SubtitlePosition == "BlackBars";
        // A real black band is reserved for the explicit black-bar setting, never for transport controls.
        var band = _compactOverlay ? _preferences.SubtitleSize == "Large" ? 112 : 96 : _preferences.SubtitleSize == "Large" ? 240 : 208;
        var videoMargin = new Thickness(0, 0, 0, blackBars ? band : 0);
        if (!VideoSurface.Margin.Equals(videoMargin)) VideoSurface.Margin = videoMargin;
        double bottom = _compactOverlay ? AreControlsVisible ? 64 : 16 : blackBars ? AreControlsVisible ? 114 : 26 : AreControlsVisible ? 150 : 64;
        var gutter = _compactOverlay ? 16d : PlayerRoot.ActualWidth < 640 ? 24d : 40d;
        var alignment = HorizontalAlignment.Center;
        var maxWidth = Math.Max(0, Math.Min(1100, PlayerRoot.ActualWidth - 2 * gutter));
        if (!_compactOverlay && !UsesNarrowCaptionBand)
        {
            double? obstacle = _sidePanelMode != PlayerSidePanel.None
                ? PlayerRoot.ActualWidth - SidePanel.Margin.Right - SidePanel.Width
                : NextEpisodeCountdown.Visibility == Visibility.Visible ? PlayerRoot.ActualWidth - 48 - NextEpisodeCountdown.Width : null;
            if (obstacle is { } left)
            {
                var centeredRoom = (left - PlayerRoot.ActualWidth / 2 - 16) * 2;
                if (centeredRoom >= 320 && _sidePanelMode == PlayerSidePanel.Tracks && !blackBars)
                {
                    // Short cues keep their natural width and viewport center; only actual intersections need clearance.
                    var captionRight = (PlayerRoot.ActualWidth + SubtitleOverlay.ActualWidth) / 2;
                    var panelTop = PlayerRoot.ActualHeight - SidePanel.Margin.Bottom - SidePanel.ActualHeight;
                    var captionBottom = PlayerRoot.ActualHeight - bottom;
                    var captionTop = captionBottom - SubtitleOverlay.ActualHeight;
                    if (_subtitleText.Length > 0 && SubtitleOverlay.Visibility == Visibility.Visible
                        && SidePanel.ActualHeight > 0 && captionRight > left
                        && captionBottom > panelTop && captionTop < PlayerRoot.ActualHeight - SidePanel.Margin.Bottom)
                    {
                        var raisedBottom = SidePanel.Margin.Bottom + SidePanel.ActualHeight + 12;
                        var headerBottom = AreControlsVisible && PlayerHeader.Visibility == Visibility.Visible ? 64d : 0d;
                        if (PlayerRoot.ActualHeight - raisedBottom - SubtitleOverlay.ActualHeight >= headerBottom + 12)
                            bottom = Math.Max(bottom, raisedBottom);
                        else
                        {
                            alignment = HorizontalAlignment.Left;
                            maxWidth = Math.Max(0, left - gutter - 16);
                        }
                    }
                }
                else if (centeredRoom >= 320) maxWidth = Math.Min(maxWidth, centeredRoom);
                else
                {
                    alignment = HorizontalAlignment.Left;
                    maxWidth = Math.Max(0, left - gutter - 16);
                }
            }
        }
        if (SubtitleOverlay.HorizontalAlignment != alignment) SubtitleOverlay.HorizontalAlignment = alignment;
        if (Math.Abs(SubtitleOverlay.MaxWidth - maxWidth) > .01) SubtitleOverlay.MaxWidth = maxWidth;
        var subtitleMargin = new Thickness(gutter, 0, gutter, bottom);
        if (!SubtitleOverlay.Margin.Equals(subtitleMargin)) SubtitleOverlay.Margin = subtitleMargin;
        var fontSize = _compactOverlay ? _preferences.SubtitleSize switch { "Small" => 14, "Large" => 18, _ => 16 }
            : Math.Round(PreferredSubtitleFontSize * Math.Clamp(PlayerRoot.ActualWidth / 1440, .72, 1));
        foreach (var target in SubtitleOutlineLayers().Append(SubtitleText))
            if (Math.Abs(target.FontSize - fontSize) > .01) SetSubtitleText(target, _subtitleText, fontSize);
        foreach (var outline in SubtitleOutlineLayers()) outline.Visibility = _preferences.SubtitleOutline ? Visibility.Visible : Visibility.Collapsed;
        UpdateChapterPreviewClearance();
    }

    private void CaptionLayoutSizeChanged(object sender, SizeChangedEventArgs args) => QueueCaptionLayout();

    private void ActivateCaptionLayout()
    {
        if (_subtitleLayoutEnabled || !_subtitleLayoutLoaded || _session is null || _coordinator is null || _engine is null) return;
        _subtitleLayoutEpoch++;
        _subtitleLayoutQueued = false;
        _subtitleLayoutEnabled = true;
    }

    private void InvalidateCaptionLayout()
    {
        _subtitleLayoutEnabled = false;
        _subtitleLayoutEpoch++;
        _subtitleLayoutQueued = false;
    }

    private void QueueCaptionLayout()
    {
        // Lifecycle checks stay managed so a drained callback never queries a retired XAML owner.
        if (!_subtitleLayoutEnabled || !_subtitleLayoutLoaded || _subtitleLayoutQueued
            || _session is not { } session || _coordinator is not { } coordinator || _engine is not { } engine) return;
        var epoch = _subtitleLayoutEpoch;
        var intent = _playIntent;
        _subtitleLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            // An old callback must not clear a newer lifecycle's coalescing flag.
            if (epoch != _subtitleLayoutEpoch) return;
            _subtitleLayoutQueued = false;
            if (!_subtitleLayoutEnabled || !_subtitleLayoutLoaded || intent != _playIntent
                || !ReferenceEquals(session, _session) || !ReferenceEquals(coordinator, _coordinator)
                || !ReferenceEquals(engine, _engine)) return;
            UpdatePlayerLayout();
        }) && epoch == _subtitleLayoutEpoch)
            _subtitleLayoutQueued = false;
    }

    private void UpdateSubtitleControls()
    {
        if (SubtitleSmallButton is null) return;
        var controls = CurrentSubtitleControls();
        SubtitleSmallButton.IsEnabled = SubtitleMediumButton.IsEnabled = SubtitleLargeButton.IsEnabled = controls.CanAdjustAppearance;
        SubtitleSizeOptions.Opacity = controls.CanAdjustAppearance ? 1 : .35;
        SubtitleTimingOptions.Opacity = controls.CanAdjustTiming ? 1 : .35;
        SubtitleDelayBackward.IsEnabled = controls.CanAdjustTiming && _subtitleDelay > -10;
        SubtitleDelayForward.IsEnabled = controls.CanAdjustTiming && _subtitleDelay < 10;
        SetControlLabel(SubtitleDelayBackward, LumenText.Get("Show subtitles 0.1 seconds earlier"));
        SetControlLabel(SubtitleDelayForward, LumenText.Get("Show subtitles 0.1 seconds later"));
        AutomationProperties.SetHelpText(SubtitleDelayBackward, controls.Notice ?? string.Empty);
        AutomationProperties.SetHelpText(SubtitleDelayForward, controls.Notice ?? string.Empty);
        foreach (var button in new[] { SubtitleSmallButton, SubtitleMediumButton, SubtitleLargeButton })
        {
            var selected = button.Tag as string == _preferences.SubtitleSize;
            button.Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Colors.Transparent);
            ToolTipService.SetToolTip(button, controls.Notice ?? button.Content);
        }
        SubtitleDelayText.Text = LumenText.Get("{0}s", (controls.IsBurnedIn ? 0 : _subtitleDelay).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture));
        SubtitleRenderingNotice.Text = controls.Notice ?? string.Empty;
        SubtitleRenderingNoticeContainer.Visibility = controls.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(SubtitleSizeOptions, controls.Notice ?? string.Empty);
        AutomationProperties.SetHelpText(SubtitleTimingOptions, controls.Notice ?? string.Empty);
        ExternalSubtitleButton.IsEnabled = CanLoadLocalSubtitle();
        SetControlLabel(ExternalSubtitleButton, LumenText.Get("Load local subtitles..."));
        if (controls.CanAdjustTiming) ApplySubtitlePreferencesToEngine();
    }

    private async void ApplySubtitlePreferencesToEngine()
    {
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        var controls = CurrentSubtitleControls();
        if (engine is null || !controls.CanAdjustTiming || playbackId is null || _appliedSubtitlePlaybackId == playbackId) return;
        _appliedSubtitlePlaybackId = playbackId;
        var preferences = _preferences;
        var delay = _subtitleDelay;
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(engine, _engine) || _coordinator?.ActiveContext?.PlaybackId != playbackId) return;
            if (controls.CanAdjustAppearance)
                await engine.ApplySubtitlePreferencesAsync(playbackId.Value, preferences.SubtitleSize switch { "Small" => 24, "Large" => 38, _ => 30 },
                    preferences.SubtitleOutline, preferences.SubtitlePosition == "BlackBars" ? "BlackBars" : "Bottom", delay);
            else await engine.SetSubtitleDelayAsync(playbackId.Value, delay);
            RememberAppliedSubtitleDelay(engine, playbackId.Value, engine.SubtitleDelaySeconds);
        });
    }

    private void SubtitleSizeClicked(object sender, RoutedEventArgs args)
    {
        if (!CurrentSubtitleControls().CanAdjustAppearance || sender is not Button { Tag: string size }) return;
        _preferences = _preferences with { SubtitleSize = size };
        _appliedSubtitlePlaybackId = null;
        UpdateSubtitleOverlayLayout();
        QueueCaptionLayout();
        UpdateSubtitleControls();
        PreferencesChanged?.Invoke(this, _preferences);
    }

    private void SubtitleDelayBackwardClicked(object sender, RoutedEventArgs args) => AdjustSubtitleDelay(-.1);
    private void SubtitleDelayForwardClicked(object sender, RoutedEventArgs args) => AdjustSubtitleDelay(.1);

    private async void AdjustSubtitleDelay(double delta)
    {
        var controls = CurrentSubtitleControls();
        if (!controls.CanAdjustTiming)
        {
            ShowPlayerOsd("closed_caption_24_regular", LumenText.Get(controls.IsBurnedIn
                ? "Burned-in subtitles cannot adjust delay" : "Subtitle timing is unavailable for this track"));
            return;
        }
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        if (engine is null || playbackId is null) return;
        var delay = Math.Clamp(Math.Round(_subtitleDelay + delta, 1), -10, 10);
        if (!await TryRunPlayerAdjustmentAsync(() => engine.SetSubtitleDelayAsync(playbackId.Value, delay))) return;
        if (!ReferenceEquals(engine, _engine) || _coordinator?.ActiveContext?.PlaybackId != playbackId) return;
        _subtitleDelay = engine.SubtitleDelaySeconds;
        _appliedSubtitlePlaybackId = playbackId;
        RememberAppliedSubtitleDelay(engine, playbackId.Value, _subtitleDelay);
        UpdateSubtitleControls();
        ShowPlayerOsd("closed_caption_24_regular", LumenText.Get("Subtitle delay {0}s",
            _subtitleDelay.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)));
    }

    private async void ExternalSubtitleClicked(object sender, RoutedEventArgs args)
    {
        var session = _session;
        var coordinator = _coordinator;
        var engine = _engine;
        var itemId = _item?.Id;
        var intent = _playIntent;
        var playbackId = coordinator?.ActiveContext?.PlaybackId;
        if (_externalSubtitlePickerOpen || session is null || coordinator is null || engine is null
            || playbackId is null || App.CurrentWindow is null) return;
        BeginExplicitSubtitleSelection();
        bool OwnsItem() => ReferenceEquals(session, _session) && ReferenceEquals(coordinator, _coordinator)
            && ReferenceEquals(engine, _engine) && intent == _playIntent && _item?.Id == itemId;
        _externalSubtitlePickerOpen = true;
        UpdateQueueControls();
        UpdateSubtitleControls();
        RevealControls();
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow));
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(windowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.VideosLibrary
            };
            foreach (var extension in NativePlaybackEngine.SupportedLocalSubtitleExtensions) picker.FileTypeFilter.Add(extension);
            var result = await picker.PickSingleFileAsync();
            if (result is null || !OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            var file = await StorageFile.GetFileFromPathAsync(result.Path);
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            await LoadLocalSubtitleFileAsync(file, ownsBusyState: false);
        }
        catch (OperationCanceledException) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure && exception.ApplicationErrorCode != "ParentalControl")
        { if (OwnsItem()) ReportExpiredSession(); }
        catch (Exception exception) { if (OwnsItem()) ShowOperationError(exception); }
        finally
        {
            _externalSubtitlePickerOpen = false;
            UpdateQueueControls();
            UpdateSubtitleControls();
            _trackRowsSnapshots.Remove(SubtitleTrackItems);
            if (OwnsItem() && _sidePanelMode == PlayerSidePanel.Tracks) RebuildTrackPanel();
            RevealControls();
        }
    }

    private async Task LoadLocalSubtitleFileAsync(StorageFile file, bool ownsBusyState = true)
    {
        var session = _session;
        var coordinator = _coordinator;
        var engine = _engine;
        var intent = _playIntent;
        var playbackId = coordinator?.ActiveContext?.PlaybackId;
        if (session is null || coordinator is null || engine is null || playbackId is null) return;
        var revision = BeginExplicitSubtitleSelection();
        var token = SubtitleSelectionToken;
        bool OwnsRequest() => ReferenceEquals(session, _session) && ReferenceEquals(coordinator, _coordinator)
            && ReferenceEquals(engine, _engine) && intent == _playIntent && revision == _subtitleSelectionRevision
            && coordinator.ActiveContext?.PlaybackId == playbackId;
        if (ownsBusyState)
        {
            if (!CanLoadLocalSubtitle()) return;
            _externalSubtitlePickerOpen = true;
            UpdateQueueControls();
            UpdateSubtitleControls();
        }
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            if (!OwnsRequest()) return;
            if (properties.Size is 0 or > 4 * 1024 * 1024) throw new PlaybackException("UnsupportedSubtitle");
            var buffer = await FileIO.ReadBufferAsync(file);
            if (!OwnsRequest()) return;
            var data = new byte[checked((int)buffer.Length)];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(data);
            NativePlaybackEngine.ValidateLocalSubtitleData(file.Name, data);
            var prepared = await engine.PrepareLocalSubtitleAsync(playbackId.Value, file.Name, data, token);
            if (!OwnsRequest()) return;
            RegisterLocalSubtitleFile(prepared, file.Path, data);
            await ApplyPreparedLocalSubtitleAsync(prepared, token, revision);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AcceptanceDiagnostics.Record(exception, "SubtitlePreparation/File");
            throw;
        }
        finally
        {
            if (ownsBusyState)
            {
                _externalSubtitlePickerOpen = false;
                UpdateQueueControls();
                UpdateSubtitleControls();
                _trackRowsSnapshots.Remove(SubtitleTrackItems);
                RebuildTrackPanel();
                RevealControls();
            }
        }
    }

    private async Task ActivateLocalSubtitleChoiceAsync(NativePreparedSubtitle prepared)
    {
        if (!CanLoadLocalSubtitle()) return;
        var revision = BeginExplicitSubtitleSelection();
        var token = SubtitleSelectionToken;
        _externalSubtitlePickerOpen = true;
        UpdateQueueControls();
        UpdateSubtitleControls();
        try { await TryRunPlayerAdjustmentAsync(() => ApplyPreparedLocalSubtitleAsync(prepared, token, revision)); }
        finally
        {
            _externalSubtitlePickerOpen = false;
            UpdateQueueControls();
            UpdateSubtitleControls();
            _trackRowsSnapshots.Remove(SubtitleTrackItems);
            RebuildTrackPanel();
            RevealControls();
        }
    }

    private async Task ApplyPreparedLocalSubtitleAsync(NativePreparedSubtitle prepared,
        CancellationToken? cancellationToken = null, long? expectedSubtitleRevision = null)
    {
        var session = _session;
        var coordinator = _coordinator;
        var engine = _engine;
        var itemId = _item?.Id;
        var intent = _playIntent;
        var context = coordinator?.ActiveContext;
        var token = cancellationToken ?? SubtitleSelectionToken;
        var ownerToken = _playRequest?.Token ?? CancellationToken.None;
        var revision = expectedSubtitleRevision ?? _subtitleSelectionRevision;
        if (session is null || coordinator is null || engine is null || context is null) return;
        bool OwnsItem() => ReferenceEquals(session, _session) && ReferenceEquals(coordinator, _coordinator)
            && ReferenceEquals(engine, _engine) && intent == _playIntent && _item?.Id == itemId
            && revision == _subtitleSelectionRevision && !token.IsCancellationRequested;
        if (!OwnsItem()) return;
        var previousSubtitle = context.Selection.SubtitleStreamIndex ?? context.Source.DefaultSubtitleStreamIndex ?? -1;
        if (context.Selection.SubtitleStreamIndex != -1)
            // A replacement source belongs to the item, not the cancellable subtitle preparation.
            await coordinator.ChangeSelectionAsync(WithCurrentNativeCapabilities(new PlaybackSelectionChange { SubtitleStreamIndex = -1 }), ownerToken);
        if (!OwnsItem() || coordinator.ActiveContext is not { } active) return;
        try
        {
            await engine.LoadLocalSubtitleAsync(active.PlaybackId, prepared, token);
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != active.PlaybackId) return;
            AddLocalSubtitleChoice(prepared);
            _localSubtitleName = prepared.FileName;
            _localSubtitlePlaybackId = active.PlaybackId;
            _selectedLocalSubtitle = prepared;
            _localSubtitleRestorePlaybackId = active.PlaybackId;
            _appliedSubtitlePlaybackId = null;
            _trackRowsSnapshots.Remove(SubtitleTrackItems);
            UpdateSubtitleControls();
            RebuildTrackPanel();
            RememberLocalSubtitleSelection(prepared);
            ShowPlayerOsd("closed_caption_24_regular", LumenText.Get("Subtitles loaded: {0}", prepared.FileName));
        }
        catch
        {
            // Invalid data is rejected before selection changes; a later native failure restores the previous server track.
            if (OwnsItem() && coordinator.ActiveContext?.PlaybackId == active.PlaybackId
                && previousSubtitle != -1 && !token.IsCancellationRequested)
                await coordinator.ChangeSelectionAsync(WithCurrentNativeCapabilities(new PlaybackSelectionChange { SubtitleStreamIndex = previousSubtitle }), ownerToken);
            throw;
        }
    }

    private bool CanClearCurrentLocalSubtitle() => _localSubtitleName is not null && _session is not null
        && _engine is not null && !_expiredReported && !_updating && !_advancing && !_retryInProgress && !IsModalOpen
        && _preparationIntent != _playIntent && _coordinator is { ActiveContext: { } context,
            Status: PlaybackStatus.Playing or PlaybackStatus.Paused } && _localSubtitlePlaybackId == context.PlaybackId;

    private async Task ClearCurrentLocalSubtitleAsync()
    {
        var engine = _engine;
        var coordinator = _coordinator;
        var intent = _playIntent;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        var revision = BeginExplicitSubtitleSelection();
        var token = SubtitleSelectionToken;
        if (engine is null || playbackId is null) return;
        await RunAsync(async () =>
        {
            await engine.ClearLocalSubtitleAsync(playbackId.Value, token);
            if (!ReferenceEquals(engine, _engine) || !ReferenceEquals(coordinator, _coordinator)
                || intent != _playIntent || revision != _subtitleSelectionRevision
                || coordinator?.ActiveContext?.PlaybackId != playbackId) return;
            _localSubtitleName = null;
            _localSubtitlePlaybackId = null;
            _selectedLocalSubtitle = null;
            _localSubtitleRestorePlaybackId = null;
            RememberServerSubtitleSelection(-1);
        });
    }

    private async void RestoreLocalSubtitleForContext(PlaybackContext context)
    {
        var prepared = _selectedLocalSubtitle;
        var engine = _engine;
        var coordinator = _coordinator;
        var session = _session;
        var intent = _playIntent;
        var revision = _subtitleSelectionRevision;
        var token = SubtitleSelectionToken;
        if (prepared is null || engine is null || coordinator is null || session is null
            || _externalSubtitlePickerOpen || _localSubtitleRestorePlaybackId == context.PlaybackId
            || context.Selection.SubtitleStreamIndex != -1
            || coordinator.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused)) return;
        _localSubtitleRestorePlaybackId = context.PlaybackId;
        bool OwnsRequest() => ReferenceEquals(session, _session) && ReferenceEquals(engine, _engine)
            && ReferenceEquals(coordinator, _coordinator) && ReferenceEquals(prepared, _selectedLocalSubtitle)
            && intent == _playIntent && revision == _subtitleSelectionRevision
            && coordinator.ActiveContext?.PlaybackId == context.PlaybackId;
        try
        {
            await engine.LoadLocalSubtitleAsync(context.PlaybackId, prepared, token);
            if (!OwnsRequest()) return;
            _localSubtitleName = prepared.FileName;
            _localSubtitlePlaybackId = context.PlaybackId;
            _appliedSubtitlePlaybackId = null;
            _trackRowsSnapshots.Remove(SubtitleTrackItems);
            UpdateSubtitleControls();
            RebuildTrackPanel();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (OwnsRequest()) ShowOperationError(exception); }
    }

    private void LocalizePlayerTree(DependencyObject root)
    {
        if (ReferenceEquals(root, PlayerRoot))
        {
            if (_playerTextLocalized) return;
            _playerTextLocalized = true;
        }
        // Server data and captions stay unchanged; their renderers localize command labels.
        if (root is ComboBoxItem || root is ContentControl { Content: PlaybackQueueEntry }
            || ReferenceEquals(root, AudioTrackItems) || ReferenceEquals(root, SubtitleTrackItems)
            || ReferenceEquals(root, EpisodeDrawerItems) || ReferenceEquals(root, SubtitleOverlay)
            || ReferenceEquals(root, NextEpisodeText) || ReferenceEquals(root, ChapterPreviewText)
            || ReferenceEquals(root, TitleText) || ReferenceEquals(root, EpisodeIdentityText)
            || ReferenceEquals(root, StageMediaText) || ReferenceEquals(root, QueueNowPlayingText)
            || ReferenceEquals(root, SettingsMediaText) || ReferenceEquals(root, SettingsIdentityText)
            || ReferenceEquals(root, SourceValue) || ReferenceEquals(root, AudioValue) || ReferenceEquals(root, SubtitleValue)) return;
        if (root is TextBlock text && !string.IsNullOrEmpty(text.Text)) text.Text = LumenText.Get(text.Text);
        if (root is ContentControl { Content: string content } control) control.Content = LumenText.Get(content);
        if (root is ComboBox { Header: string header } selector) selector.Header = LumenText.Get(header);
        if (root is ToggleSwitch toggle)
        {
            if (toggle.Header is string caption) toggle.Header = LumenText.Get(caption);
            toggle.OnContent = LumenText.Get("On"); toggle.OffContent = LumenText.Get("Off");
        }
        if (root is AppBarButton command) command.Label = LumenText.Get(command.Label);
        var name = AutomationProperties.GetName(root);
        if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(root, LumenText.Get(name));
        if (root is FrameworkElement element && ToolTipService.GetToolTip(element) is string tooltip)
            ToolTipService.SetToolTip(element, LumenText.Get(tooltip));
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) LocalizePlayerTree(VisualTreeHelper.GetChild(root, index));
    }

    private static void LocalizePlayerMenu(MenuFlyout menu)
    {
        foreach (var entry in menu.Items.OfType<MenuFlyoutItem>()) entry.Text = LumenText.Get(entry.Text);
    }
}
