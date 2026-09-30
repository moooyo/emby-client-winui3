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
    private readonly Dictionary<StackPanel, TrackRowsSnapshot> _trackRowsSnapshots = [];

    private sealed record TrackRowSnapshot(ComboBoxItem Option, object? Content, object? Tag, string Label);
    private sealed record TrackRowsSnapshot(ComboBox Selector, object? SelectedItem, bool IsEnabled,
        bool CanClearLocalSubtitle, string? LocalSubtitleName, string Empty, TrackRowSnapshot[] Rows);

    public event EventHandler<LumenPreferences>? PreferencesChanged;

    public void ApplyPreferences(LumenPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences;
        _synchronizingAutoPlay = true;
        AutoPlayNext.IsOn = QueueAutoPlayNext.IsOn = preferences.AutoPlayNext;
        _synchronizingAutoPlay = false;
        FontFamily = LumenTheme.SansFont;
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
        if (_sidePanelMode == PlayerSidePanel.Episodes) { CloseSidePanel(); return; }
        OpenSidePanel(PlayerSidePanel.Episodes, EpisodeDrawerButton);
    }

    private void UpdatePanelButtonStates()
    {
        SubtitleButton.Background = new SolidColorBrush(_sidePanelMode == PlayerSidePanel.Tracks
            ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Colors.Transparent);
        EpisodeDrawerButton.Background = new SolidColorBrush(_sidePanelMode == PlayerSidePanel.Episodes
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
                if (isSubtitle && ReferenceEquals(selector.SelectedItem, option) && _localSubtitleName is not null)
                {
                    await ClearCurrentLocalSubtitleAsync();
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
        if (ReferenceEquals(selector, SubtitleSelector) && _localSubtitleName is { } localName)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(PlayerIcon("checkmark_16_filled", size: 14));
            content.Children.Add(new TextBlock { Text = localName, FontSize = 13, MaxWidth = 172, TextTrimming = TextTrimming.CharacterEllipsis });
            var local = new Button { Style = (Style)Resources["PlayerTrackRowStyle"], Content = content, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255)) };
            SetControlLabel(local, LumenText.Get("Local subtitles: {0}", localName));
            local.Click += ExternalSubtitleClicked;
            host.Children.Add(local);
        }
        _trackRowsSnapshots[host] = snapshot;
    }

    private void UpdateStreamPresentation(PlaybackContext context)
    {
        var direct = context.DeliveryMethod == PlaybackDeliveryMethod.DirectStream;
        StateText.Text = LumenText.Get(direct ? "Direct play" : "Transcoding");
        StreamOnlineDot.Fill = direct ? LumenTheme.Brush("Online") : Resources["PlayerAccentBrush"] as Brush;
        var video = context.Source.MediaStreams?.FirstOrDefault(value => string.Equals(value.Type, "Video", StringComparison.OrdinalIgnoreCase));
        var format = new List<string>();
        if (_engine?.VideoHeight is > 0) format.Add($"{_engine.VideoHeight}p");
        else if (direct && video?.Height is > 0) format.Add($"{video.Height}p");
        if (direct && !string.IsNullOrWhiteSpace(video?.Codec)) format.Add(video.Codec.ToUpperInvariant());
        if (direct && !string.IsNullOrWhiteSpace(video?.VideoRange) && video.VideoRange != "SDR") format.Add(video.VideoRange);
        StreamInfoText.Text = string.Join(" \u00b7 ", format);
        if (direct && context.Source.Bitrate is > 0)
            StreamInfoText.Text += $" | {context.Source.Bitrate.Value / 1_000_000d:0.#} Mbps";
        else if (!direct)
        {
            var limit = context.Selection.MaxStreamingBitrate == int.MaxValue ? LumenText.Get("No client bitrate cap")
                : LumenText.Get("{0} Mbps limit", $"{context.Selection.MaxStreamingBitrate / 1_000_000d:0.#}");
            StreamInfoText.Text = string.Join(" | ", new[] { StreamInfoText.Text, limit }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        ToolTipService.SetToolTip(StreamInfoPill, $"{StateText.Text} | {StreamInfoText.Text}");
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
                    if (ReferenceEquals(engine, _engine)) RateText.Text = FormatPlaybackRate(engine.PlaybackRate);
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

    private static string FormatPlaybackRate(double rate) => rate.ToString(rate == 1 ? "0.0" : "0.##", CultureInfo.InvariantCulture) + "\u00d7";

    private double PreferredSubtitleFontSize => _preferences.SubtitleSize switch { "Small" => 24, "Large" => 38, _ => 30 };

    private void NativeSubtitleTextChanged(object? sender, NativeSubtitleTextChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _engine)) return;
        if (_coordinator?.ActiveContext?.PlaybackId != args.PlaybackId)
        {
            if (args.Text.Length == 0 && _subtitlePlaybackId == args.PlaybackId) ClearSubtitleOverlay();
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

    private void ClearSubtitleOverlay()
    {
        _subtitlePlaybackId = _appliedSubtitlePlaybackId = null;
        _localSubtitlePlaybackId = null;
        _localSubtitleName = null;
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
        var editable = _engine?.SupportsSubtitlePreferences == true;
        var blackBars = editable && _preferences.SubtitlePosition == "BlackBars";
        // A real black band is reserved for the explicit black-bar setting, never for transport controls.
        var band = _compactOverlay ? _preferences.SubtitleSize == "Large" ? 112 : 96 : _preferences.SubtitleSize == "Large" ? 240 : 208;
        var videoMargin = new Thickness(0, 0, 0, blackBars ? band : 0);
        if (!VideoSurface.Margin.Equals(videoMargin)) VideoSurface.Margin = videoMargin;
        double bottom = _compactOverlay ? AreControlsVisible ? 64 : 16 : blackBars ? AreControlsVisible ? 114 : 36 : AreControlsVisible ? PlayerRoot.ActualWidth < 780 ? 236 : 190 : 40;
        var gutter = _compactOverlay ? 16d : PlayerRoot.ActualWidth < 640 ? 24d : 64d;
        var alignment = HorizontalAlignment.Center;
        var maxWidth = Math.Max(0, Math.Min(1100, PlayerRoot.ActualWidth - 2 * gutter));
        if (!_compactOverlay && !UsesNarrowCaptionBand)
        {
            double? obstacle = _sidePanelMode != PlayerSidePanel.None
                ? PlayerRoot.ActualWidth - SidePanel.Margin.Right - SidePanel.Width
                : NextEpisodeCountdown.Visibility == Visibility.Visible ? PlayerRoot.ActualWidth - 56 - 320 : null;
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
        var fontSize = _compactOverlay ? _preferences.SubtitleSize switch { "Small" => 14, "Large" => 18, _ => 16 } : PreferredSubtitleFontSize;
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
        var editable = _engine?.SupportsSubtitlePreferences == true && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        SubtitleSmallButton.IsEnabled = SubtitleMediumButton.IsEnabled = SubtitleLargeButton.IsEnabled = editable;
        SubtitleDelayBackward.IsEnabled = editable && _subtitleDelay > -10;
        SubtitleDelayForward.IsEnabled = editable && _subtitleDelay < 10;
        foreach (var button in new[] { SubtitleSmallButton, SubtitleMediumButton, SubtitleLargeButton })
        {
            var selected = button.Tag as string == _preferences.SubtitleSize;
            button.Background = new SolidColorBrush(selected ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Colors.Transparent);
            ToolTipService.SetToolTip(button, editable ? button.Content : LumenText.Get("Subtitle appearance is unavailable for server-rendered subtitles."));
        }
        SubtitleDelayText.Text = LumenText.Get("{0}s", _subtitleDelay.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture));
        SubtitleRenderingNotice.Text = LumenText.Get(editable
            ? "Subtitle adjustments apply to this external text track."
            : "Subtitle appearance and timing are controlled by the server.");
        SubtitleRenderingNotice.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
        ExternalSubtitleButton.IsEnabled = !_externalSubtitlePickerOpen && !_advancing
            && _coordinator?.ActiveContext is not null && _coordinator.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        SetControlLabel(ExternalSubtitleButton, LumenText.Get("Open local WebVTT subtitles"));
        if (editable) ApplySubtitlePreferencesToEngine();
    }

    private async void ApplySubtitlePreferencesToEngine()
    {
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        if (engine?.SupportsSubtitlePreferences != true || playbackId is null || _appliedSubtitlePlaybackId == playbackId) return;
        _appliedSubtitlePlaybackId = playbackId;
        var preferences = _preferences;
        var delay = _subtitleDelay;
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(engine, _engine) || _coordinator?.ActiveContext?.PlaybackId != playbackId) return;
            await engine.ApplySubtitlePreferencesAsync(playbackId.Value, preferences.SubtitleSize switch { "Small" => 24, "Large" => 38, _ => 30 },
                preferences.SubtitleOutline, preferences.SubtitlePosition == "BlackBars" ? "BlackBars" : "Bottom", delay);
        });
    }

    private void SubtitleSizeClicked(object sender, RoutedEventArgs args)
    {
        if (_engine?.SupportsSubtitlePreferences != true || sender is not Button { Tag: string size }) return;
        _preferences = _preferences with { SubtitleSize = size };
        _appliedSubtitlePlaybackId = null;
        UpdateSubtitleOverlayLayout();
        QueueCaptionLayout();
        UpdateSubtitleControls();
        PreferencesChanged?.Invoke(this, _preferences);
    }

    private void SubtitleDelayBackwardClicked(object sender, RoutedEventArgs args) => AdjustSubtitleDelay(-.1);
    private void SubtitleDelayForwardClicked(object sender, RoutedEventArgs args) => AdjustSubtitleDelay(.1);

    private void AdjustSubtitleDelay(double delta)
    {
        if (_engine?.SupportsSubtitlePreferences != true) return;
        _subtitleDelay = Math.Clamp(Math.Round(_subtitleDelay + delta, 1), -10, 10);
        _appliedSubtitlePlaybackId = null;
        UpdateSubtitleControls();
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
            picker.FileTypeFilter.Add(".vtt");
            var result = await picker.PickSingleFileAsync();
            if (result is null || !OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            var file = await StorageFile.GetFileFromPathAsync(result.Path);
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            var properties = await file.GetBasicPropertiesAsync();
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            if (properties.Size > 4 * 1024 * 1024) throw new PlaybackException("UnsupportedSubtitle");
            var text = await FileIO.ReadTextAsync(file);
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != playbackId) return;
            await coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 });
            if (!OwnsItem() || coordinator.ActiveContext is not { } active) return;
            await engine.LoadLocalSubtitleAsync(active.PlaybackId, text, _playRequest?.Token ?? CancellationToken.None);
            if (!OwnsItem() || coordinator.ActiveContext?.PlaybackId != active.PlaybackId) return;
            _localSubtitleName = file.Name;
            _localSubtitlePlaybackId = active.PlaybackId;
            _appliedSubtitlePlaybackId = null;
            UpdateSubtitleControls();
            RebuildTrackPanel();
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
            if (OwnsItem() && _sidePanelMode == PlayerSidePanel.Tracks) RebuildTrackPanel();
            RevealControls();
        }
    }

    private bool CanClearCurrentLocalSubtitle() => _localSubtitleName is not null && _session is not null
        && _engine is not null && !_expiredReported && !_updating && !_advancing && !_retryInProgress && !IsModalOpen
        && _preparationIntent != _playIntent && _coordinator is { ActiveContext: { } context,
            Status: PlaybackStatus.Playing or PlaybackStatus.Paused } && _localSubtitlePlaybackId == context.PlaybackId;

    private Task ClearCurrentLocalSubtitleAsync()
    {
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        _localSubtitleName = null;
        _localSubtitlePlaybackId = null;
        if (engine is null || playbackId is null) return Task.CompletedTask;
        return RunAsync(() => engine.ClearLocalSubtitleAsync(playbackId.Value));
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
