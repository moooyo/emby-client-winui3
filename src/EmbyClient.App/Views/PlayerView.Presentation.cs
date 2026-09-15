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
using System.Runtime.InteropServices;
using Windows.System;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private enum PlayerSidePanel { None, Queue, Settings }

    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibilitySettings = new();
    private PlayerSidePanel _sidePanelMode;
    private FlyoutBase? _openQuickFlyout;
    private Control? _sidePanelTrigger;
    private bool _fullscreen;
    private bool _pointerOnChrome;
    private bool _synchronizingAutoPlay;
    private bool _windowTitleBarIntegrated;
    private bool _sidePanelKeyboardFocus;
    private bool _highContrastSubscribed;
    private bool _sidePanelOverlay;
    private readonly Dictionary<PlayerSidePanel, Control> _panelFocus = [];

    private void InitializePresentation()
    {
        QueueList.ItemsSource = _queue.Items;
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
        var title = item.Name ?? fallback ?? "Now playing";
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)) return title;
        var number = item.IndexNumber is { } episode
            ? item.ParentIndexNumber is { } season ? $"S{season:00} E{episode:00}" : $"Episode {episode}"
            : null;
        return string.Join(" · ", new[] { item.SeriesName, number, title }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string PlaybackSubtitle(BaseItemDto? item)
    {
        if (item is null) return string.Empty;
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
            return item.ProductionYear?.ToString() ?? string.Empty;
        var episode = item.IndexNumber is { } number
            ? item.ParentIndexNumber is { } season ? $"S{season:00} E{number:00}" : $"Episode {number}"
            : null;
        return string.Join(" · ", new[] { item.SeriesName, episode }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public void SetWindowTitleBarIntegrated(bool enabled)
    {
        _windowTitleBarIntegrated = enabled;
        UpdatePlayerLayout();
    }

    private void PlayerRootSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePlayerLayout();

    private void UpdatePlayerLayout()
    {
        if (VideoStage is null || SidePanel is null) return;
        PlayerHeader.Visibility = _windowTitleBarIntegrated && !_fullscreen ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(VideoStage, _fullscreen ? 0 : 1);
        Grid.SetRowSpan(VideoStage, _fullscreen ? ReserveControlArea.IsOn ? 2 : 3 : 1);
        var sideOpen = _sidePanelMode != PlayerSidePanel.None;
        var inline = sideOpen && PlayerRoot.ActualWidth >= 1040;
        SideColumn.Width = new GridLength(inline ? 360 : 0);
        Grid.SetColumn(SidePanel, inline ? 1 : 0);
        SidePanel.Width = Math.Min(360, Math.Max(0, PlayerRoot.ActualWidth));
        var wasOverlay = _sidePanelOverlay;
        _sidePanelOverlay = sideOpen && !inline;
        PanelScrim.Visibility = _sidePanelOverlay ? Visibility.Visible : Visibility.Collapsed;
        SidePanel.TabFocusNavigation = _sidePanelOverlay ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Local;
        if (_sidePanelOverlay && !wasOverlay && !IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, SidePanel))
            CloseSidePanelButton.Focus(FocusState.Programmatic);
        // Keep ThemeResource expressions attached so a system theme change updates every surface.
        var overlay = _fullscreen && !_accessibilitySettings.HighContrast;
        HeaderSurface.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        HeaderGradient.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
        DockSurface.Visibility = overlay && !ReserveControlArea.IsOn ? Visibility.Collapsed : Visibility.Visible;
        DockGradient.Visibility = overlay && !ReserveControlArea.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaybackNoticeMargin();
    }

    private void PlayerHeaderSizeChanged(object sender, SizeChangedEventArgs args) => UpdatePlaybackNoticeMargin();

    private void UpdatePlaybackNoticeMargin()
    {
        if (PlaybackNoticeHost is not null)
            PlaybackNoticeHost.Margin = new Thickness(24, _fullscreen ? Math.Max(48, PlayerHeader.ActualHeight) + 16 : 24, 24, 24);
    }

    private void PlayerHighContrastChanged(Windows.UI.ViewManagement.AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdatePlayerLayout);

    private void PlayerMainSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var width = args.NewSize.Width;
        var compact = width < 1100;
        Grid.SetColumn(TransportButtons, compact ? 0 : 1);
        Grid.SetColumnSpan(TransportButtons, compact ? 3 : 1);
        Grid.SetRow(VolumeButton, compact ? 1 : 0);
        Grid.SetRow(UtilityButtons, compact ? 1 : 0);
        StateText.Visibility = width >= 680 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleButton.Visibility = Visibility.Visible;
        QualityButton.Visibility = width >= 920 ? Visibility.Visible : Visibility.Collapsed;
        QueueButton.Visibility = Visibility.Visible;
        QueueCountText.Visibility = width >= 920 ? Visibility.Visible : Visibility.Collapsed;
        ControlDock.Padding = new Thickness(width >= 680 ? 20 : 12, 0, width >= 680 ? 20 : 12, 12);
        DockSurface.Margin = DockGradient.Margin = new Thickness(width >= 680 ? -20 : -12, 0, width >= 680 ? -20 : -12, -12);
        VolumeButton.Visibility = Visibility.Visible;
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
        _pointerOnChrome = PlayerHeader.Visibility == Visibility.Visible && point.Y <= PlayerHeader.ActualHeight
            || point.Y >= PlayerMain.ActualHeight - ControlDock.ActualHeight || IsWithin(source, SidePanel);
        RevealControls();
    }

    private bool CanHideControls()
    {
        if (!_fullscreen || Visibility != Visibility.Visible || !IsLoaded || IsModalOpen || IsSettingsOpen
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
        if (!IsSettingsOpen && !IsModalOpen && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), VideoFocusTarget))
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
    private async void ForwardClicked(object sender, RoutedEventArgs args) => await SeekRelativeAsync(10);
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

    private void SubtitleMenuOpening(object sender, object args)
    {
        SubtitleMenu.Items.Clear();
        AddTrackSubmenu("Audio", AudioSelector);
        AddTrackSubmenu("Subtitles", SubtitleSelector);
        QuickFlyoutOpening(sender, args);
    }

    private void AddTrackSubmenu(string title, ComboBox selector)
    {
        var submenu = new MenuFlyoutSubItem { Text = title };
        foreach (var option in selector.Items.OfType<ComboBoxItem>())
        {
            var entry = new ToggleMenuFlyoutItem { Text = option.Content?.ToString() ?? "Off",
                IsChecked = ReferenceEquals(selector.SelectedItem, option), IsEnabled = selector.IsEnabled };
            entry.Click += (_, _) => selector.SelectedItem = option;
            submenu.Items.Add(entry);
        }
        if (submenu.Items.Count == 0) submenu.Items.Add(new MenuFlyoutItem { Text = "No track available", IsEnabled = false });
        SubtitleMenu.Items.Add(submenu);
    }

    private void UpdateSelectionPresentation()
    {
        if (SourceSelector is null || SourceValue is null) return;
        ShowSelectionOrValue(SourceSelector, SourceReadOnly, SourceValue, "No version available");
        ShowSelectionOrValue(AudioSelector, AudioReadOnly, AudioValue, EmptyAudioSelectionText());
        ShowSelectionOrValue(SubtitleSelector, SubtitleReadOnly, SubtitleValue, "Off");
        QualityText.Text = $"{_bitrate / 1_000_000d:0.#} Mbps";
        var limit = _session?.User.Policy?.RemoteClientBitrateLimit;
        QualityLimitText.Visibility = Visibility.Visible;
        QualityLimitText.Text = limit is > 0 ? $"Your account allows up to {limit.Value / 1_000_000d:0.#} Mbps. Actual bitrate varies with playback."
            : "A streaming limit, not the video resolution or actual bitrate.";
        SetControlLabel(QualityButton, $"Maximum bitrate, {QualityText.Text}");
        SettingsMediaText.Text = _item?.Name ?? "Now playing";
        SettingsIdentityText.Text = PlaybackSubtitle(_item);
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
        if (mode == PlayerSidePanel.Queue && _retrySettingsDraft is not null) CancelRetrySettings();
        _sidePanelTrigger = trigger;
        _sidePanelKeyboardFocus = trigger.FocusState == FocusState.Keyboard
            || FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard };
        _sidePanelMode = mode;
        SidePanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = mode == PlayerSidePanel.Settings ? Visibility.Visible : Visibility.Collapsed;
        QueuePanel.Visibility = mode == PlayerSidePanel.Queue ? Visibility.Visible : Visibility.Collapsed;
        SidePanelTitle.Text = mode == PlayerSidePanel.Queue ? "Queue" : _retrySettingsDraft is not null ? "Retry settings" : "Playback settings";
        RetrySettingsFooter.Visibility = mode == PlayerSidePanel.Settings && _retrySettingsDraft is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionPresentation();
        UpdateQueuePanel();
        UpdatePlayerLayout();
        RevealControls();
        SetControlLabel(CloseSidePanelButton, mode == PlayerSidePanel.Queue ? "Close queue" : "Close playback settings", "Close (Escape)");
        var focus = _panelFocus.TryGetValue(mode, out var remembered) && remembered.IsLoaded && remembered.IsEnabled
            && remembered.Visibility == Visibility.Visible ? remembered : CloseSidePanelButton;
        var focusState = _sidePanelKeyboardFocus ? FocusState.Keyboard : FocusState.Programmatic;
        if (!focus.Focus(focusState)) CloseSidePanelButton.Focus(focusState);
    }

    private void CloseSidePanelClicked(object sender, RoutedEventArgs args) =>
        CloseSidePanel(keyboard: CloseSidePanelButton.FocusState == FocusState.Keyboard);

    private void CloseSidePanel(bool restoreFocus = true, bool keyboard = false)
    {
        if (_sidePanelMode == PlayerSidePanel.None) return;
        RememberPanelFocus();
        CancelRetrySettings();
        _sidePanelMode = PlayerSidePanel.None;
        SidePanel.Visibility = Visibility.Collapsed;
        QueueFooter.Visibility = RetrySettingsFooter.Visibility = Visibility.Collapsed;
        UpdatePlayerLayout();
        if (restoreFocus && _sidePanelTrigger is { IsLoaded: true, IsEnabled: true } trigger)
        {
            var visible = trigger.Visibility == Visibility.Visible
                && !(IsWithin(trigger, PlayerHeader) && PlayerHeader.Visibility == Visibility.Collapsed);
            (visible ? trigger : PlaybackSettingsButton).Focus(keyboard || _sidePanelKeyboardFocus ? FocusState.Keyboard : FocusState.Programmatic);
        }
        _sidePanelTrigger = null;
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
        QueueSummaryText.Text = $"{_queue.Count} queued {(_queue.Count == 1 ? "item" : "items")}";
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
        QueueNextDescription.Text = _queue.Next is { } head ? $"Starts {head.Title}." : string.Empty;
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
            : $"{index + 1} of {_queue.Count}, {entry.AutomationName}{(index == 0 ? ", next in queue" : string.Empty)}");
        if (container.ContentTemplateRoot is not { } templateRoot) return;
        var template = templateRoot.As<FrameworkElement>();
        if (template.FindName("OrderText") is { } order) order.As<TextBlock>().Text = index < 0 ? string.Empty : (index + 1).ToString();
        if (template.FindName("NextText") is { } next) next.As<TextBlock>().Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoPlayNextToggled(object sender, RoutedEventArgs args)
    {
        if (_synchronizingAutoPlay || QueueAutoPlayNext is null) return;
        _synchronizingAutoPlay = true;
        QueueAutoPlayNext.IsOn = AutoPlayNext.IsOn;
        _synchronizingAutoPlay = false;
    }

    private void QueueAutoPlayNextToggled(object sender, RoutedEventArgs args)
    {
        if (_synchronizingAutoPlay || AutoPlayNext is null) return;
        _synchronizingAutoPlay = true;
        AutoPlayNext.IsOn = QueueAutoPlayNext.IsOn;
        _synchronizingAutoPlay = false;
    }
}
