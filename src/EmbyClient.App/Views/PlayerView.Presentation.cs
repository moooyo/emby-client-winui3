using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private enum PlayerSidePanel { None, Queue, Settings, Tracks, Episodes }

    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibilitySettings = new();
    private PlayerSidePanel _sidePanelMode;
    private FlyoutBase? _openQuickFlyout;
    private Control? _sidePanelTrigger;
    private bool _fullscreen;
    private bool _compactOverlay;
    private bool _pointerOnChrome;
    private bool _synchronizingAutoPlay;
    private bool _sidePanelKeyboardFocus;
    private bool _highContrastSubscribed;
    private bool _sidePanelOverlay;
    private readonly Dictionary<PlayerSidePanel, Control> _panelFocus = [];
    public bool AreControlsVisible { get; private set; } = true;

    private void InitializePresentation()
    {
        Loaded += (_, _) =>
        {
            _subtitleLayoutLoaded = true;
            ActivateCaptionLayout();
        };
        Unloaded += (_, _) =>
        {
            _subtitleLayoutLoaded = false;
            InvalidateCaptionLayout();
        };
        QueueList.ItemsSource = _queue.Items;
        VideoFocusTarget.ContextFlyout = MoreMenu;
        InitializeEpisodeDrawer();
        InitializeChapters();
        _chromeTimer.Tick += (_, _) =>
        {
            _chromeTimer.Stop();
            if (CanHideControls()) SetControlsVisible(false);
        };
        PlayerRoot.AddHandler(PointerMovedEvent, new PointerEventHandler(PlayerPointerMoved), true);
        PlayerRoot.AddHandler(KeyDownEvent, new KeyEventHandler(PlayerKeyDown), true);
        PlayerRoot.PointerExited += (_, args) =>
        {
            var point = args.GetCurrentPoint(PlayerRoot).Position;
            if (point.X >= 0 && point.Y >= 0 && point.X < PlayerRoot.ActualWidth && point.Y < PlayerRoot.ActualHeight) return;
            _pointerOnChrome = false;
            ScheduleChromeAutoHide();
        };
        PlayerRoot.GotFocus += (_, _) => RevealControls();
        PlayerRoot.LostFocus += (_, _) => ScheduleChromeAutoHide();
        PlayerRoot.ActualThemeChanged += (_, _) => UpdatePlayerLayout();
        Loaded += (_, _) =>
        {
            LocalizePlayerTree(PlayerRoot);
            LocalizePlayerMenu(MoreMenu);
            SubscribeHighContrastChanged();
            UpdatePlayerLayout();
            UpdateQueuePanel();
            RevealControls();
        };
        Unloaded += (_, _) =>
        {
            _chromeTimer.Stop();
            UnsubscribeHighContrastChanged();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible)
            {
                _chromeTimer.Stop();
                _openQuickFlyout?.Hide();
                CloseSidePanel(restoreFocus: false);
            }
            else RevealControls();
        });
    }

    private void SubscribeHighContrastChanged()
    {
        if (_highContrastSubscribed) return;
        try
        {
            _accessibilitySettings.HighContrastChanged += PlayerHighContrastChanged;
            _highContrastSubscribed = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Some Windows sessions cannot register this optional notification. Theme and size
            // changes still read the current high-contrast state when updating the presentation.
        }
    }

    private void UnsubscribeHighContrastChanged()
    {
        if (!_highContrastSubscribed) return;
        try
        {
            _accessibilitySettings.HighContrastChanged -= PlayerHighContrastChanged;
            _highContrastSubscribed = false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Retain ownership on failure so a later unload can retry without adding a duplicate.
        }
    }

    private static string PlaybackTitle(BaseItemDto item, string? fallback)
    {
        var title = item.Name ?? fallback ?? LumenText.Get("Now playing");
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)) return title;
        var number = item.IndexNumber is { } episode
            ? item.ParentIndexNumber is { } season ? $"S{season:00} E{episode:00}" : LumenText.Get("Episode {0}", episode)
            : null;
        return string.Join(" · ", new[] { item.SeriesName, number, title }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string PlaybackSubtitle(BaseItemDto? item)
    {
        if (item is null) return string.Empty;
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
            return item.ProductionYear?.ToString() ?? string.Empty;
        var episode = item.IndexNumber is { } number
            ? item.ParentIndexNumber is { } season ? LumenText.Get("Season {0} · Episode {1}", season, number) : LumenText.Get("Episode {0}", number)
            : null;
        return string.Join(" · ", new[] { episode, item.Name }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public void SetWindowTitleBarIntegrated(bool enabled)
    {
        UpdatePlayerLayout();
    }

    public void SetCompactOverlayState(bool compactOverlay)
    {
        if (_compactOverlay == compactOverlay) return;
        _compactOverlay = compactOverlay;
        if (compactOverlay)
        {
            _openQuickFlyout?.Hide();
            CloseSidePanel(restoreFocus: false);
            HideChapterPreview();
            SkipIntroButton.Visibility = Visibility.Collapsed;
        }
        UpdateTransportLayout(PlayerMain.ActualWidth);
        UpdatePlayerLayout();
        SetControlLabel(PictureInPictureButton, LumenText.Get(compactOverlay ? "Exit picture in picture" : "Picture in picture"));
        PictureInPictureButton.Background = new SolidColorBrush(compactOverlay ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Microsoft.UI.Colors.Transparent);
        RevealControls();
    }

    private void PlayerRootSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePlayerLayout();

    private void UpdatePlayerLayout()
    {
        if (VideoStage is null || SidePanel is null) return;
        PlayerHeader.Visibility = _compactOverlay ? Visibility.Collapsed : Visibility.Visible;
        var dockHeight = _compactOverlay ? 120d : PlayerRoot.ActualWidth < 780 ? 372d : 320d;
        if (ControlDock.Height != dockHeight) ControlDock.Height = dockHeight;
        var sideOpen = _sidePanelMode != PlayerSidePanel.None;
        SideColumn.Width = new GridLength(0);
        Grid.SetColumn(SidePanel, 0);
        var tracks = _sidePanelMode == PlayerSidePanel.Tracks;
        var right = tracks && PlayerRoot.ActualWidth >= 700 ? 44d : 16d;
        var narrowCaptionBand = UsesNarrowCaptionBand;
        if (narrowCaptionBand) UpdateSubtitleOverlayLayout();
        var panelBottom = PlayerRoot.ActualWidth < 780 ? 170d : 118d;
        if (narrowCaptionBand)
        {
            // Caption width is independent of the panel; its measured height alone reserves this band.
            // The first layout gets a provisional two-line slot until native caption geometry is available.
            var captionHeight = SubtitleOverlay.ActualHeight > 0
                ? SubtitleOverlay.ActualHeight : PreferredSubtitleFontSize * 2 * 1.45;
            panelBottom = Math.Max(panelBottom, SubtitleOverlay.Margin.Bottom + captionHeight + 12);
        }
        var panelWidth = Math.Min(tracks ? 480 : 400, Math.Max(0, PlayerRoot.ActualWidth - right - 16));
        if (Math.Abs(SidePanel.Width - panelWidth) > .01) SidePanel.Width = panelWidth;
        var panelMargin = new Thickness(16, 64, right, panelBottom);
        if (!SidePanel.Margin.Equals(panelMargin)) SidePanel.Margin = panelMargin;
        var panelAlignment = tracks ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        if (SidePanel.VerticalAlignment != panelAlignment) SidePanel.VerticalAlignment = panelAlignment;
        var panelMaxHeight = Math.Max(0, PlayerRoot.ActualHeight - 64 - panelBottom);
        if (Math.Abs(SidePanel.MaxHeight - panelMaxHeight) > .01) SidePanel.MaxHeight = panelMaxHeight;
        var trackMaxHeight = Math.Max(0, panelMaxHeight - 80);
        if (Math.Abs(TrackListScroll.MaxHeight - trackMaxHeight) > .01) TrackListScroll.MaxHeight = trackMaxHeight;
        SidePanelHeader.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
        var wasOverlay = _sidePanelOverlay;
        _sidePanelOverlay = sideOpen;
        PanelScrim.Visibility = _sidePanelOverlay ? Visibility.Visible : Visibility.Collapsed;
        SidePanel.TabFocusNavigation = _sidePanelOverlay ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        if (_sidePanelOverlay && !wasOverlay && !IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SidePanel))
            CloseSidePanelButton.Focus(FocusState.Programmatic);
        // Keep ThemeResource expressions attached so a system theme change updates every surface.
        var overlay = !_accessibilitySettings.HighContrast;
        HeaderSurface.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        HeaderGradient.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        DockSurface.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        DockGradient.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        if (!narrowCaptionBand) UpdateSubtitleOverlayLayout();
        else UpdateChapterPreviewClearance();
        NextEpisodeCountdown.VerticalAlignment = _compactOverlay ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        NextEpisodeCountdown.Margin = _compactOverlay ? new Thickness(16, 32, 16, 0) : new Thickness(0, 0, 56, 128);
        NextEpisodeCountdown.Padding = new Thickness(_compactOverlay ? 12 : 20, _compactOverlay ? 10 : 16, _compactOverlay ? 12 : 20, _compactOverlay ? 10 : 16);
        NextEpisodeCountdown.MaxWidth = Math.Max(0, PlayerRoot.ActualWidth - 32);
        NextEpisodeCountdown.Width = _compactOverlay ? Math.Max(0, PlayerRoot.ActualWidth - 32) : 320;
        NextCountdownText.MaxLines = _compactOverlay ? 1 : 2;
        NextCountdownText.Height = _compactOverlay ? 20 : 40;
        NextCountdownText.TextTrimming = TextTrimming.CharacterEllipsis;
        RebuildChapterTicks();
        UpdatePlaybackNoticeMargin();
    }

    private void PlayerHeaderSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePlaybackNoticeMargin();

    private void UpdatePlaybackNoticeMargin()
    {
        if (PlaybackNoticeHost is not null)
            PlaybackNoticeHost.Margin = new Thickness(24, 96, 24, 128);
    }

    private void PlayerHighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args)
    {
        var epoch = _subtitleLayoutEpoch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_subtitleLayoutLoaded || !_subtitleLayoutEnabled || epoch != _subtitleLayoutEpoch) return;
            UpdatePlayerLayout();
        });
    }

    private void PlayerMainSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateTransportLayout(args.NewSize.Width);
    }

    private void UpdateTransportLayout(double width)
    {
        var compact = !_compactOverlay && width < 780;
        TransportRow.Height = _compactOverlay ? 40 : compact ? 104 : 52;
        TransportRow.RowDefinitions.Clear();
        TransportRow.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_compactOverlay ? 40 : 52) });
        if (compact) TransportRow.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        Grid.SetRow(UtilityButtons, compact ? 1 : 0);
        Grid.SetColumn(UtilityButtons, compact ? 0 : 3);
        Grid.SetColumnSpan(UtilityButtons, compact ? 4 : 1);
        UtilityButtons.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        VolumeGroup.Visibility = !_compactOverlay && width >= 460 ? Visibility.Visible : Visibility.Collapsed;
        Volume.Visibility = width >= 680 ? Visibility.Visible : Visibility.Collapsed;
        TimeReadout.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        TimeReadout.Visibility = _compactOverlay ? Visibility.Collapsed : Visibility.Visible;
        TimelineHost.Visibility = _compactOverlay ? Visibility.Collapsed : Visibility.Visible;
        RewindButton.Visibility = ForwardButton.Visibility = SubtitleButton.Visibility = RateButton.Visibility = FullscreenButton.Visibility
            = _compactOverlay ? Visibility.Collapsed : Visibility.Visible;
        EpisodeDrawerButton.Visibility = NextEpisodeButton.Visibility = !_compactOverlay && string.Equals(_item?.Type, "Episode", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Width = PauseButton.Height = _compactOverlay ? 40 : 52;
        PauseButton.CornerRadius = new CornerRadius(_compactOverlay ? 20 : 26);
        PauseButton.Padding = new Thickness(_compactOverlay ? 8 : 14);
        PauseButton.Margin = new Thickness(0, 0, _compactOverlay ? 0 : 6, 0);
        PictureInPictureButton.Width = PictureInPictureButton.Height = _compactOverlay ? 40 : 44;
        StreamInfoPill.Visibility = width >= 820 ? Visibility.Visible : Visibility.Collapsed;
        var gutter = !_compactOverlay && width >= 780 ? 44d : 16d;
        var bottom = _compactOverlay ? 16d : 22d;
        ControlDock.Padding = new Thickness(gutter, 0, gutter, bottom);
        DockSurface.Margin = DockGradient.Margin = new Thickness(-gutter, 0, -gutter, -bottom);
        TimelineHost.Margin = new Thickness(12, 0, 12, compact ? 112 : 60);
        ChapterPreview.Margin = new Thickness(ChapterPreview.Margin.Left, 0, 0, compact ? 166 : 114);
        UpdatePlayerLayout();
    }

    private static bool IsWithin(DependencyObject? child, DependencyObject parent)
    {
        for (; child is not null; child = VisualTreeHelper.GetParent(child))
            if (ReferenceEquals(child, parent)) return true;
        return false;
    }

    private void PlayerPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        var point = args.GetCurrentPoint(PlayerMain).Position;
        _pointerOnChrome = PlayerHeader.Visibility == Visibility.Visible && point.Y <= 64 || point.Y >= PlayerMain.ActualHeight - (_compactOverlay ? 60 : PlayerRoot.ActualWidth < 780 ? 166 : 114)
            || IsWithin(source, SidePanel);
        RevealControls();
    }

    private bool CanHideControls()
    {
        if (Visibility != Visibility.Visible || !IsLoaded || IsModalOpen || IsSettingsOpen
            || _pointerOnChrome || _timelineDrag.IsActive || _keyboardTimelinePlaybackId is not null
            || _preparationIntent == _playIntent
            || PlaybackNotice.IsOpen || _coordinator?.Status != PlaybackStatus.Playing
            || _engine?.Snapshot?.State != PlaybackEngineState.Playing) return false;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        return focused is not Control { FocusState: FocusState.Keyboard }
            || !(IsWithin(focused, PlayerHeader) || IsWithin(focused, ControlDock) || IsWithin(focused, SidePanel));
    }

    private void SetControlsVisible(bool visible)
    {
        // Opacity keeps the layout and keyboard order stable; focus immediately reveals the controls.
        PlayerHeader.Opacity = ControlDock.Opacity = visible ? 1 : 0;
        PlayerHeader.IsHitTestVisible = ControlDock.IsHitTestVisible = visible;
        if (AreControlsVisible != visible)
        {
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                foreach (var element in new FrameworkElement[] { PlayerHeader, ControlDock })
                {
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
                    animation.InsertKeyFrame(0, visible ? 0 : 1);
                    animation.InsertKeyFrame(1, visible ? 1 : 0);
                    animation.Duration = TimeSpan.FromMilliseconds(180);
                    visual.StartAnimation("Opacity", animation);
                }
            }
            AreControlsVisible = visible;
            UpdateSubtitleOverlayLayout();
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RevealControls()
    {
        if (PlayerHeader is null) return;
        SetControlsVisible(true);
        ScheduleChromeAutoHide();
    }

    private void ScheduleChromeAutoHide()
    {
        _chromeTimer.Stop();
        if (CanHideControls()) _chromeTimer.Start();
    }

    private void UpdateChromeForPlaybackState()
    {
        if (_coordinator?.Status != PlaybackStatus.Playing || _engine?.Snapshot?.State != PlaybackEngineState.Playing)
            RevealControls();
        else ScheduleChromeAutoHide();
    }

    private void VideoSurfaceClicked(object sender, RoutedEventArgs args)
    {
        CloseSidePanel(restoreFocus: false);
        RevealControls();
    }

    private async void PlayerKeyDown(object sender, KeyRoutedEventArgs args)
    {
        RevealControls();
        if (!args.Handled && args.Key == VirtualKey.F11 && !IsModalOpen && _openQuickFlyout is null)
        {
            args.Handled = true;
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        // The video focus target is a Button, so the host deliberately leaves its keys to this view.
        if (!IsSettingsOpen && !IsModalOpen && _session is not null && !_expiredReported && !_advancing
            && !_retryInProgress && _preparationIntent != _playIntent
            && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), VideoFocusTarget))
        {
            if (args.Key == VirtualKey.Space) { args.Handled = true; await TogglePauseAsync(); return; }
            if (args.Key is VirtualKey.Left or VirtualKey.Right)
            { args.Handled = true; await SeekRelativeAsync(args.Key == VirtualKey.Left ? -10 : 10); return; }
        }
        if (args.Handled || args.Key != VirtualKey.Escape) return;
        if (_openQuickFlyout is not null) { _openQuickFlyout.Hide(); args.Handled = true; }
        else if (_sidePanelMode != PlayerSidePanel.None) { CloseSidePanel(keyboard: true); args.Handled = true; }
    }

    private async void RewindClicked(object sender, RoutedEventArgs args) => await SeekRelativeAsync(-10);
    private async void ForwardClicked(object sender, RoutedEventArgs args) => await SeekRelativeAsync(30);
    private void ReserveControlAreaToggled(object sender, RoutedEventArgs args) => UpdatePlayerLayout();

    private void QuickFlyoutOpening(object sender, object args)
    {
        _openQuickFlyout = sender as FlyoutBase;
        RevealControls();
    }

    private void QuickFlyoutClosed(object sender, object args)
    {
        if (ReferenceEquals(_openQuickFlyout, sender)) _openQuickFlyout = null;
        ScheduleChromeAutoHide();
    }

    private void QualityMenuOpening(object sender, object args)
    {
        QualityMenu.Items.Clear();
        foreach (var option in QualitySelector.Items.OfType<ComboBoxItem>())
        {
            var entry = new ToggleMenuFlyoutItem { Text = option.Content?.ToString() ?? string.Empty,
                IsChecked = ReferenceEquals(QualitySelector.SelectedItem, option), Tag = option };
            entry.Click += (_, _) => QualitySelector.SelectedItem = option;
            QualityMenu.Items.Add(entry);
        }
        QuickFlyoutOpening(sender, args);
    }

    private void UpdateSelectionPresentation()
    {
        if (SourceSelector is null || SourceValue is null) return;
        ShowSelectionOrValue(SourceSelector, SourceReadOnly, SourceValue, LumenText.Get("No version available"));
        ShowSelectionOrValue(AudioSelector, AudioReadOnly, AudioValue, LumenText.Get(EmptyAudioSelectionText()));
        ShowSelectionOrValue(SubtitleSelector, SubtitleReadOnly, SubtitleValue, LumenText.Get("Off"));
        QualityText.Text = _bitrate == int.MaxValue ? LumenText.Get("Unlimited") : $"{_bitrate / 1_000_000d:0.#} Mbps";
        var limit = _session?.User.Policy?.RemoteClientBitrateLimit;
        QualityLimitText.Visibility = Visibility.Visible;
        QualityLimitText.Text = limit is > 0 ? LumenText.Get("Your account allows up to {0} Mbps. Actual bitrate varies with playback.", $"{limit.Value / 1_000_000d:0.#}")
            : LumenText.Get("A streaming limit, not the video resolution or actual bitrate.");
        SetControlLabel(QualityButton, LumenText.Get("Maximum bitrate, {0}", QualityText.Text));
        SettingsMediaText.Text = _item?.Name ?? LumenText.Get("Now playing");
        SettingsIdentityText.Text = PlaybackSubtitle(_item);
        if (_sidePanelMode == PlayerSidePanel.Tracks) RebuildTrackPanel();
        UpdateSubtitleControls();
    }

    private string EmptyAudioSelectionText()
    {
        var source = _retrySettingsDraft is { } draft
            ? FindRetrySource(draft.MediaSourceId)
            : SourceSelector.SelectedItem is ComboBoxItem { Tag: string sourceId }
                ? FindRetrySource(sourceId)
                : _lastPlaybackContext is { } context && context.PlaybackId == _displayedPlayback ? context.Source : null;
        return source?.MediaStreams is { } streams
            && !streams.Any(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))
                ? "No audio track" : "Audio tracks unavailable";
    }

    private static void ShowSelectionOrValue(ComboBox selector, FrameworkElement readOnly, TextBlock value, string empty)
    {
        var canChoose = selector.Items.Count > 1;
        selector.Visibility = canChoose ? Visibility.Visible : Visibility.Collapsed;
        readOnly.Visibility = canChoose ? Visibility.Collapsed : Visibility.Visible;
        value.Text = (selector.SelectedItem as ComboBoxItem ?? selector.Items.OfType<ComboBoxItem>().FirstOrDefault())?.Content?.ToString() ?? empty;
    }

    private void PlaybackSettingsClicked(object sender, RoutedEventArgs args)
    {
        if (_sidePanelMode == PlayerSidePanel.Settings) { CloseSidePanel(); return; }
        if (_coordinator?.Status == PlaybackStatus.Failed && _coordinator.CanRetry) BeginRetrySettings();
        OpenSidePanel(PlayerSidePanel.Settings, sender is MenuFlyoutItem ? MoreButton : PlaybackSettingsButton);
    }

    private void OpenSidePanel(PlayerSidePanel mode, Control trigger)
    {
        if (IsModalOpen) return;
        _openQuickFlyout?.Hide();
        RememberPanelFocus();
        if (mode != PlayerSidePanel.Settings && _retrySettingsDraft is not null) CancelRetrySettings();
        _sidePanelTrigger = trigger;
        _sidePanelKeyboardFocus = trigger.FocusState == FocusState.Keyboard
            || FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard };
        _sidePanelMode = mode;
        SidePanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = mode == PlayerSidePanel.Settings ? Visibility.Visible : Visibility.Collapsed;
        QueuePanel.Visibility = mode == PlayerSidePanel.Queue ? Visibility.Visible : Visibility.Collapsed;
        TrackPanel.Visibility = mode == PlayerSidePanel.Tracks ? Visibility.Visible : Visibility.Collapsed;
        EpisodesPanel.Visibility = mode == PlayerSidePanel.Episodes ? Visibility.Visible : Visibility.Collapsed;
        EpisodeSeasonHeading.Visibility = mode == PlayerSidePanel.Episodes ? Visibility.Visible : Visibility.Collapsed;
        SidePanelTitle.Visibility = mode == PlayerSidePanel.Episodes ? Visibility.Collapsed : Visibility.Visible;
        SidePanelTitle.Text = LumenText.Get(mode == PlayerSidePanel.Queue ? "Queue" : _retrySettingsDraft is not null ? "Retry settings" : "Playback settings");
        RetrySettingsFooter.Visibility = mode == PlayerSidePanel.Settings && _retrySettingsDraft is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionPresentation();
        UpdateQueuePanel();
        UpdatePlayerLayout();
        RevealControls();
        SetControlLabel(CloseSidePanelButton, LumenText.Get(mode == PlayerSidePanel.Queue ? "Close queue" : mode == PlayerSidePanel.Episodes ? "Close episodes" : "Close playback settings"), LumenText.Get("Close"));
        var focus = _panelFocus.TryGetValue(mode, out var remembered) && remembered.IsLoaded && remembered.IsEnabled
            && remembered.Visibility == Visibility.Visible ? remembered : mode == PlayerSidePanel.Tracks
                ? AudioTrackItems.Children.OfType<Control>().FirstOrDefault(control => control.IsEnabled)
                    ?? SubtitleTrackItems.Children.OfType<Control>().FirstOrDefault(control => control.IsEnabled) ?? trigger
                : CloseSidePanelButton;
        var focusState = _sidePanelKeyboardFocus ? FocusState.Keyboard : FocusState.Programmatic;
        if (!focus.Focus(focusState) && mode != PlayerSidePanel.Tracks) CloseSidePanelButton.Focus(focusState);
        if (mode == PlayerSidePanel.Episodes) _ = PopulateEpisodeDrawerAsync();
        SkipIntroButton.Visibility = NextEpisodeCountdown.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() => LocalizePlayerTree(SidePanel));
        UpdatePanelButtonStates();
        RefreshChapterControls();
        AnimatePanelEntrance();
    }

    private void AnimatePanelEntrance()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(SidePanel, true);
        var visual = ElementCompositionPreview.GetElementVisual(SidePanel);
        visual.CenterPoint = new Vector3((float)SidePanel.Width, (float)SidePanel.ActualHeight, 0);
        var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1));
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(1, 1);
        opacity.Duration = TimeSpan.FromMilliseconds(200);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(.96f, .96f, 1)); scale.InsertKeyFrame(1, Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(250);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(0, -6, 0)); translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.Duration = TimeSpan.FromMilliseconds(250);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
        visual.Properties.StartAnimation("Translation", translation);
    }

    private void CloseSidePanelClicked(object sender, RoutedEventArgs args) =>
        CloseSidePanel(keyboard: CloseSidePanelButton.FocusState == FocusState.Keyboard);

    private void CloseSidePanel(bool restoreFocus = true, bool keyboard = false)
    {
        if (_sidePanelMode == PlayerSidePanel.None) return;
        RememberPanelFocus();
        if (_sidePanelMode == PlayerSidePanel.Episodes) CancelEpisodeDrawerRequest();
        CancelRetrySettings();
        _sidePanelMode = PlayerSidePanel.None;
        SidePanel.Visibility = Visibility.Collapsed;
        TrackPanel.Visibility = EpisodesPanel.Visibility = Visibility.Collapsed;
        QueueFooter.Visibility = RetrySettingsFooter.Visibility = Visibility.Collapsed;
        UpdatePlayerLayout();
        if (restoreFocus && _sidePanelTrigger is { IsLoaded: true, IsEnabled: true } trigger)
        {
            var visible = trigger.Visibility == Visibility.Visible
                && !(IsWithin(trigger, PlayerHeader) && PlayerHeader.Visibility == Visibility.Collapsed);
            (visible ? trigger : PlaybackSettingsButton).Focus(keyboard || _sidePanelKeyboardFocus ? FocusState.Keyboard : FocusState.Programmatic);
        }
        _sidePanelTrigger = null;
        UpdatePanelButtonStates();
        RefreshChapterControls();
        RevealControls();
    }

    private void RememberPanelFocus()
    {
        if (_sidePanelMode != PlayerSidePanel.None && FocusManager.GetFocusedElement(XamlRoot) is Control focused
            && IsWithin(focused, SidePanel)) _panelFocus[_sidePanelMode] = focused;
    }

    private void UpdateQueuePanel()
    {
        if (QueueList is null || QueueSummaryText is null) return;
        QueueSummaryText.Text = LumenText.Get("{0} queued items", _queue.Count);
        QueueEmptyText.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var index = QueueList.SelectedItem is PlaybackQueueEntry entry ? _queue.IndexOf(entry.EntryId) : -1;
        QueueMoveUpButton.IsEnabled = index > 0;
        QueueMoveDownButton.IsEnabled = index >= 0 && index < _queue.Count - 1;
        QueueRemoveButton.IsEnabled = index >= 0;
        QueueClearButton.IsEnabled = _queue.Count > 0;
        QueueActions.Visibility = _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueFooter.Visibility = _sidePanelMode == PlayerSidePanel.Queue && _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueuePlayNextButton.IsEnabled = _queue.Count > 0 && CanStartQueuedItem();
        QueueNextDescription.Text = _queue.Next is { } head ? LumenText.Get("Starts {0}.", head.Title) : string.Empty;
        UpdateEpisodeControls();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateQueueRows);
    }

    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateQueuePanel();
    private void QueueMoveUpClicked(object sender, RoutedEventArgs args) => MoveQueueSelection(up: true);
    private void QueueMoveDownClicked(object sender, RoutedEventArgs args) => MoveQueueSelection(up: false);

    private void MoveQueueSelection(bool up)
    {
        if (QueueList.SelectedItem is not PlaybackQueueEntry entry) return;
        if (!(up ? _queue.MoveUp(entry.EntryId) : _queue.MoveDown(entry.EntryId))) return;
        QueueList.SelectedItem = entry;
        QueueList.ScrollIntoView(entry);
        UpdateQueuePanel();
        if (!(up ? QueueMoveUpButton : QueueMoveDownButton).IsEnabled) FocusQueueSelection();
    }

    private void QueueRemoveClicked(object sender, RoutedEventArgs args) => RemoveQueueSelection();

    private bool RemoveQueueSelection()
    {
        if (QueueList.SelectedItem is not PlaybackQueueEntry entry) return false;
        var index = _queue.IndexOf(entry.EntryId);
        if (!_queue.Remove(entry.EntryId)) return false;
        QueueList.SelectedIndex = Math.Min(index, _queue.Count - 1);
        UpdateQueuePanel();
        FocusQueueSelection();
        return true;
    }

    private void QueueClearClicked(object sender, RoutedEventArgs args)
    {
        _queue.Clear();
        QueueList.SelectedItem = null;
        UpdateQueuePanel();
        CloseSidePanelButton.Focus(FocusState.Programmatic);
    }

    private void FocusQueueSelection() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_sidePanelMode != PlayerSidePanel.Queue) return;
        if (QueueList.SelectedItem is PlaybackQueueEntry entry && QueueList.ContainerFromItem(entry) is Control container)
            container.Focus(FocusState.Programmatic);
        else CloseSidePanelButton.Focus(FocusState.Programmatic);
    });

    private void QueueListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Delete && RemoveQueueSelection()) args.Handled = true;
    }

    private void QueueContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        UpdateQueueRow(args.ItemContainer, args.InRecycleQueue ? null : args.Item as PlaybackQueueEntry);
        if (!args.InRecycleQueue && args.Phase == 0)
            args.RegisterUpdateCallback((_, update) => UpdateQueueRow(update.ItemContainer,
                update.InRecycleQueue ? null : update.Item as PlaybackQueueEntry));
    }

    private void UpdateQueueRows()
    {
        if (!QueueList.IsLoaded) return;
        QueueList.UpdateLayout();
        foreach (var entry in _queue.Items)
            if (QueueList.ContainerFromItem(entry) is { } container)
                // Native AOT can return the base container projection after a collection change.
                UpdateQueueRow(container.As<ListViewItem>(), entry);
    }

    private void UpdateQueueRow(ContentControl container, PlaybackQueueEntry? entry)
    {
        var index = entry is null ? -1 : _queue.IndexOf(entry.EntryId);
        AutomationProperties.SetName(container, index < 0 || entry is null ? string.Empty
            : LumenText.Get("{0} of {1}, {2}", index + 1, _queue.Count,
                $"{entry.Title}, {QueueEntryDetail(entry.Item)}{(index == 0 ? ", " + LumenText.Get("Next in queue") : string.Empty)}"));
        if (container.ContentTemplateRoot is not { } templateRoot) return;
        var template = templateRoot.As<FrameworkElement>();
        if (template.FindName("OrderText") is { } order) order.As<TextBlock>().Text = index < 0 ? string.Empty : (index + 1).ToString();
        if (entry is not null && template.FindName("QueueDetailText") is { } detail) detail.As<TextBlock>().Text = QueueEntryDetail(entry.Item);
        if (template.FindName("NextText") is { } next)
        {
            next.As<TextBlock>().Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            next.As<TextBlock>().Text = LumenText.Get("Next in queue");
        }
    }

    private static string QueueEntryDetail(BaseItemDto item)
    {
        var parts = new List<string>();
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(item.SeriesName)) parts.Add(item.SeriesName);
            if (item.ParentIndexNumber is { } season) parts.Add(LumenText.Get("Season {0}", season));
            if (item.IndexNumber is { } episode) parts.Add(LumenText.Get("Episode {0}", episode));
        }
        else
        {
            if (item.ProductionYear is { } year) parts.Add(year.ToString());
            parts.Add(LumenText.Get(item.Type ?? "Video"));
        }
        return string.Join(" · ", parts);
    }

    private void AutoPlayNextToggled(object sender, RoutedEventArgs args)
    {
        if (_synchronizingAutoPlay || QueueAutoPlayNext is null) return;
        _synchronizingAutoPlay = true;
        QueueAutoPlayNext.IsOn = AutoPlayNext.IsOn;
        _synchronizingAutoPlay = false;
        SynchronizeAutomaticPreference();
    }

    private void QueueAutoPlayNextToggled(object sender, RoutedEventArgs args)
    {
        if (_synchronizingAutoPlay || AutoPlayNext is null) return;
        _synchronizingAutoPlay = true;
        AutoPlayNext.IsOn = QueueAutoPlayNext.IsOn;
        _synchronizingAutoPlay = false;
        SynchronizeAutomaticPreference();
    }

    private void SynchronizeAutomaticPreference()
    {
        if (_preferences.AutoPlayNext != AutoPlayNext.IsOn)
        {
            _preferences = _preferences with { AutoPlayNext = AutoPlayNext.IsOn };
            PreferencesChanged?.Invoke(this, _preferences);
        }
        RefreshChapterControls();
    }
}
