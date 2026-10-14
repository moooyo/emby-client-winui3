using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromMilliseconds(1100) };
    private readonly DispatcherTimer _playbackFlashTimer = new() { Interval = TimeSpan.FromMilliseconds(520) };
    private readonly DispatcherTimer _videoTapTimer = new();
    private readonly DispatcherTimer _subtitleDropTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly ImageCache _playerArtworkCache = new();
    private bool _showRemainingTime;
    private bool _captionPointerActive;
    private bool _windowTitleBarIntegrated = true;
    private bool _playbackInfoOpen;
    private bool _playerFileDialogOpen;
    private long _suppressVideoClickUntil;
    private string _playbackInfoCopy = string.Empty;
    private Guid? _mediaArtworkPlaybackId;
    private string? _nextArtworkItemId;
    private IReadOnlyList<(string Name, string Value)> _playbackInfoValues = [];

    public IReadOnlyList<FrameworkElement> CaptionInteractiveElements => [BackButton, StreamInfoButton, MoreButton];

    public void SetCaptionPointerActive(bool active)
    {
        if (_captionPointerActive == active) return;
        _captionPointerActive = active;
        if (active) RevealControls();
        else ScheduleChromeAutoHide();
    }

    private void InitializeV2Interaction()
    {
        InitializeEndRecommendations();
        InitializePlayerToast();
        SidePanel.Background = new AcrylicBrush { TintColor = Windows.UI.Color.FromArgb(255, 28, 26, 23),
            TintOpacity = .84, TintLuminosityOpacity = .65, FallbackColor = Windows.UI.Color.FromArgb(248, 28, 26, 23) };
        PlaybackInfoPanel.Background = new AcrylicBrush { TintColor = Windows.UI.Color.FromArgb(255, 24, 22, 20),
            TintOpacity = .88, TintLuminosityOpacity = .65, FallbackColor = Windows.UI.Color.FromArgb(248, 24, 22, 20) };
        PlayerOsd.Background = new AcrylicBrush { TintColor = Windows.UI.Color.FromArgb(255, 20, 18, 16),
            TintOpacity = .6, TintLuminosityOpacity = .6, FallbackColor = Windows.UI.Color.FromArgb(240, 20, 18, 16) };
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); PlayerOsd.Visibility = Visibility.Collapsed; };
        _playbackFlashTimer.Tick += (_, _) => { _playbackFlashTimer.Stop(); PlaybackFlash.Visibility = Visibility.Collapsed; };
        _subtitleDropTimer.Tick += (_, _) => { _subtitleDropTimer.Stop(); SubtitleDropOverlay.Visibility = Visibility.Collapsed; };
        _videoTapTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(GetDoubleClickTime(), 150, 600));
        _videoTapTimer.Tick += async (_, _) =>
        {
            _videoTapTimer.Stop();
            if (!IsLoaded || Visibility != Visibility.Visible) return;
            await TogglePauseAsync();
        };
        VideoFocusTarget.PointerEntered += (_, _) => ScheduleChromeAutoHide();
        VolumeGroup.PointerWheelChanged += VideoSurfaceWheelChanged;
        foreach (var key in new[] { VirtualKey.Left, VirtualKey.Right, VirtualKey.PageUp, VirtualKey.PageDown })
        {
            var accelerator = new KeyboardAccelerator { Key = key, ScopeOwner = PlayerRoot,
                Modifiers = key is VirtualKey.Left or VirtualKey.Right ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None };
            accelerator.Invoked += async (_, args) =>
            {
                if (!IsLoaded || Visibility != Visibility.Visible || IsSettingsOpen || IsModalOpen) return;
                if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or ComboBox) return;
                args.Handled = true;
                await HandlePlaybackShortcutAsync(key, key is VirtualKey.Left or VirtualKey.Right, false);
            };
            PlayerRoot.KeyboardAccelerators.Add(accelerator);
        }
        Unloaded += (_, _) => ResetV2Interaction();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) ResetV2Interaction();
        });
        LocalizeV2Controls();
    }

    private void ResetV2Interaction()
    {
        _osdTimer.Stop();
        _playbackFlashTimer.Stop();
        _videoTapTimer.Stop();
        _subtitleDropTimer.Stop();
        PlayerOsd.Visibility = PlaybackFlash.Visibility = SubtitleDropOverlay.Visibility = Visibility.Collapsed;
        PlaybackInfoPanel.Visibility = Visibility.Collapsed;
        PlaybackEndedOverlay.Visibility = Visibility.Collapsed;
        ResetEndRecommendations();
        _playbackInfoOpen = false;
        ResetServerPlaybackInformation();
        ClosePlayerToast();
        _captionPointerActive = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void ShowPlayerOsd(string icon, string text, double? volume = null)
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        PlayerOsdIcon.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/w/{icon}.svg"));
        PlayerOsdText.Text = text;
        PlayerOsdText.MaxWidth = Math.Max(80, PlayerRoot.ActualWidth - (volume.HasValue ? 272 : 112));
        PlayerOsdVolume.Visibility = volume.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (volume.HasValue) PlayerOsdVolume.Value = Math.Clamp(volume.Value, 0, 100);
        PlayerOsd.Visibility = Visibility.Visible;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    private void ShowPlaybackFlash(bool playing)
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        PlaybackFlashIcon.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/w/{(playing ? "play" : "pause")}_24_filled.svg"));
        PlaybackFlash.Visibility = Visibility.Visible;
        var visual = ElementCompositionPreview.GetElementVisual(PlaybackFlash);
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 1);
            opacity.InsertKeyFrame(1, 0);
            opacity.Duration = TimeSpan.FromMilliseconds(450);
            visual.StartAnimation("Opacity", opacity);
        }
        else visual.Opacity = 1;
        _playbackFlashTimer.Stop();
        _playbackFlashTimer.Start();
    }

    private void TimeReadoutClicked(object sender, RoutedEventArgs args)
    {
        _showRemainingTime = !_showRemainingTime;
        SetControlLabel(TimeReadout, LumenText.Get(_showRemainingTime ? "Show elapsed time" : "Show remaining time"));
        UpdatePosition();
    }

    private void VideoSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        _videoTapTimer.Stop();
        _suppressVideoClickUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 3;
        args.Handled = true;
        if (!IsModalOpen) FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void VideoSurfaceWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (IsModalOpen || _sidePanelMode != PlayerSidePanel.None || !Volume.IsEnabled) return;
        var properties = args.GetCurrentPoint(PlayerRoot).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0) return;
        args.Handled = true;
        AdjustPlayerVolume(properties.MouseWheelDelta > 0 ? 5 : -5);
    }

    private void AdjustPlayerVolume(int delta)
    {
        if (!Volume.IsEnabled) return;
        var next = Math.Clamp((int)Volume.Value + delta, 0, 100);
        MuteButton.IsChecked = next == 0;
        if (Math.Abs(Volume.Value - next) < .01) MuteClicked(MuteButton, new RoutedEventArgs());
        else Volume.Value = next;
        UpdateVolumeIcon();
        ShowPlayerOsd("speaker_2_24_regular", $"{next}%", next);
        RevealControls();
    }

    private void UpdateVolumeIcon()
    {
        var muted = MuteButton.IsChecked == true || Volume.Value <= 0;
        var content = new Grid { Width = 22, Height = 22 };
        content.Children.Add(PlayerIcon("speaker_2_24_regular", size: 20));
        if (muted)
            content.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 24, Height = 2, RadiusX = 1, RadiusY = 1,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                RenderTransform = new RotateTransform { Angle = -45 }, RenderTransformOrigin = new Windows.Foundation.Point(.5, .5)
            });
        VolumeButton.Content = content;
        VolumeButton.Opacity = 1;
        SetControlLabel(VolumeButton, LumenText.Get(muted ? "Unmute" : "Mute"));
        SetControlLabel(MuteButton, LumenText.Get(muted ? "Unmute" : "Mute"));
    }

    public async Task<bool> HandlePlaybackShortcutAsync(VirtualKey key, bool control, bool shift)
    {
        if (IsModalOpen || _openQuickFlyout is not null || _session is null || _expiredReported) return false;
        if (key == VirtualKey.I && !control) { TogglePlaybackInfo(); return true; }
        if (_sidePanelMode != PlayerSidePanel.None || _playbackInfoOpen) return false;
        if (control)
        {
            if (key is not (VirtualKey.Left or VirtualKey.Right)) return false;
            await StepChapterAsync(key == VirtualKey.Right ? 1 : -1);
            return true;
        }
        if (!IsPlaybackShortcutKey(key)) return false;
        RevealControls();
        if (_advancing || _retryInProgress || _preparationIntent == _playIntent) return true;
        switch (key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                await TogglePauseAsync();
                break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                await SeekRelativeAsync(key == VirtualKey.Left ? -10 : 30);
                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                AdjustPlayerVolume(key == VirtualKey.Up ? 5 : -5);
                break;
            case VirtualKey.M:
                VolumeButtonClicked(VolumeButton, new RoutedEventArgs());
                break;
            case VirtualKey.PageUp:
            case VirtualKey.PageDown:
            case VirtualKey.N:
                await ChangeEpisodeWithFeedbackAsync(key == VirtualKey.PageUp);
                break;
            case VirtualKey.C:
                await CyclePlayerTrackAsync(SubtitleSelector);
                break;
            case VirtualKey.A:
                await CyclePlayerTrackAsync(AudioSelector);
                break;
            case VirtualKey.Z:
            case VirtualKey.X:
                if (shift) await AdjustAudioDelayAsync(key == VirtualKey.X ? .1 : -.1);
                else AdjustSubtitleDelay(key == VirtualKey.X ? .1 : -.1);
                break;
            case (VirtualKey)219:
            case (VirtualKey)221:
                await StepPlaybackRateAsync(key == (VirtualKey)221 ? 1 : -1);
                break;
            case VirtualKey.F:
            case VirtualKey.F11:
                FullscreenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.P:
                CompactOverlayRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
        return true;
    }

    private static bool IsPlaybackShortcutKey(VirtualKey key) => key is VirtualKey.Space or VirtualKey.K
        or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.M
        or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.N or VirtualKey.C or VirtualKey.A
        or VirtualKey.Z or VirtualKey.X or VirtualKey.F or VirtualKey.F11 or VirtualKey.P or VirtualKey.I
        or (VirtualKey)219 or (VirtualKey)221;

    private async Task StepChapterAsync(int direction)
    {
        var coordinator = _coordinator;
        var context = coordinator?.ActiveContext;
        if (coordinator is null || context is null || !CanUseChapterCommand(context)) return;
        var duration = context.Source.RunTimeTicks.GetValueOrDefault();
        var chapters = PlaybackChapters(context).Where(chapter => ValidChapterStart(chapter, duration)
                && !IsChapterBoundaryMarker(chapter))
            .OrderBy(chapter => chapter.StartPositionTicks).DistinctBy(chapter => chapter.StartPositionTicks).ToArray();
        if (chapters.Length == 0)
        {
            ShowPlayerOsd("text_bullet_list_ltr_20_regular", LumenText.Get("No chapters available"));
            return;
        }
        var current = Math.Max(0, Array.FindLastIndex(chapters,
            chapter => chapter.StartPositionTicks <= context.PositionTicks + TimeSpan.TicksPerSecond / 2));
        var next = direction > 0 ? current + 1
            : context.PositionTicks - chapters[current].StartPositionTicks > 3 * TimeSpan.TicksPerSecond ? current : current - 1;
        if (next < 0 || next >= chapters.Length)
        {
            ShowPlayerOsd("text_bullet_list_ltr_20_regular", LumenText.Get(next < 0 ? "First chapter reached" : "Last chapter reached"));
            return;
        }
        var chapter = chapters[next];
        await RunAsync(() => coordinator.SeekAsync(context.PlaybackId, chapter.StartPositionTicks));
        if (ReferenceEquals(coordinator, _coordinator) && coordinator.ActiveContext?.Selection.ItemId == context.Selection.ItemId)
            ShowPlayerOsd("text_bullet_list_ltr_20_regular", LumenText.Get("Chapter {0} · {1}", next + 1,
                chapter.Name ?? LumenText.Get("Untitled chapter")));
    }

    private async Task ChangeEpisodeWithFeedbackAsync(bool previous)
    {
        if (!CanChangeEpisode()) return;
        var target = _episodeNeighborsItemId == _item?.Id ? previous ? _episodeNeighbors?.Previous : _episodeNeighbors?.Next : null;
        if (target is null)
        {
            ShowPlayerOsd("next_24_filled", LumenText.Get(_resolvingEpisodeNeighbors ? "Finding episode order"
                : previous ? "No previous episode is available." : "No next episode is available."));
            return;
        }
        await ChangeEpisodeAsync(previous);
        if (_item?.Id == target.Id) ShowPlayerOsd("next_24_filled", PlaybackSubtitle(target));
    }

    private async Task CyclePlayerTrackAsync(ComboBox selector)
    {
        if (!selector.IsEnabled || selector.Items.Count == 0) return;
        if (ReferenceEquals(selector, SubtitleSelector)) BeginExplicitSubtitleSelection();
        if (ReferenceEquals(selector, SubtitleSelector) && _localSubtitleName is not null)
        {
            await ClearCurrentLocalSubtitleAsync();
            selector.SelectedIndex = 0;
            RebuildTrackPanel();
        }
        else
        {
            var nextIndex = (selector.SelectedIndex + 1 + selector.Items.Count) % selector.Items.Count;
            if (ReferenceEquals(selector, SubtitleSelector) && nextIndex == selector.SelectedIndex
                && selector.SelectedItem is ComboBoxItem { Tag: int selectedIndex })
            {
                if (selectedIndex == -1) await ClearCurrentLocalSubtitleAsync();
                else RememberServerSubtitleSelection(selectedIndex);
            }
            selector.SelectedIndex = nextIndex;
        }
        var name = (selector.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? LumenText.Get("Off");
        ShowPlayerOsd(ReferenceEquals(selector, SubtitleSelector) ? "closed_caption_24_regular" : "headphones_20_regular",
            LumenText.Get(ReferenceEquals(selector, SubtitleSelector) ? "Subtitles: {0}" : "Audio: {0}", name));
    }

    private async Task StepPlaybackRateAsync(int direction)
    {
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        if (engine?.SupportsPlaybackRate != true || playbackId is null) return;
        double[] rates = [.5, .75, 1, 1.25, 1.5, 2];
        var closest = Enumerable.Range(0, rates.Length).MinBy(index => Math.Abs(rates[index] - engine.PlaybackRate));
        var rate = rates[Math.Clamp(closest + direction, 0, rates.Length - 1)];
        if (!await TryRunPlayerAdjustmentAsync(() => engine.SetPlaybackRateAsync(playbackId.Value, rate))) return;
        if (ReferenceEquals(engine, _engine) && _coordinator?.ActiveContext?.PlaybackId == playbackId)
        {
            RateText.Text = FormatPlaybackRate(engine.PlaybackRate);
            SavePlaybackPreferences();
            ShowPlayerOsd("top_speed_24_regular", LumenText.Get("Speed {0}", RateText.Text));
        }
    }

    private async Task AdjustAudioDelayAsync(double delta)
    {
        var engine = _engine;
        var playbackId = _coordinator?.ActiveContext?.PlaybackId;
        if (engine?.SupportsAudioDelay != true || playbackId is null)
        {
            ShowPlayerOsd("headphones_20_regular", LumenText.Get("Audio delay is unavailable for this stream"));
            return;
        }
        var delay = Math.Clamp(Math.Round(engine.AudioDelaySeconds + delta, 1), -10, 10);
        if (!await TryRunPlayerAdjustmentAsync(() => engine.SetAudioDelayAsync(playbackId.Value, delay))) return;
        if (ReferenceEquals(engine, _engine) && _coordinator?.ActiveContext?.PlaybackId == playbackId)
        {
            SavePlaybackPreferences();
            ShowPlayerOsd("headphones_20_regular", LumenText.Get("Audio delay {0}s",
                engine.AudioDelaySeconds.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)));
        }
    }

    private async Task<bool> TryRunPlayerAdjustmentAsync(Func<Task> adjustment)
    {
        var engine = _engine;
        var coordinator = _coordinator;
        var intent = _playIntent;
        try
        {
            await adjustment();
            return ReferenceEquals(engine, _engine) && ReferenceEquals(coordinator, _coordinator) && intent == _playIntent;
        }
        catch (Exception) when (!ReferenceEquals(engine, _engine) || !ReferenceEquals(coordinator, _coordinator) || intent != _playIntent) { }
        catch (OperationCanceledException) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure && exception.ApplicationErrorCode != "ParentalControl")
        { ReportExpiredSession(); }
        catch (Exception exception) { ShowOperationError(exception); }
        return false;
    }

    private void RebuildPlaybackContextMenu()
    {
        MoreMenu.Items.Clear();
        var playing = _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Buffering;
        AddContextCommand(playing ? "Pause" : "Resume", playing ? "pause_24_filled" : "play_24_filled", "Space",
            async () => await TogglePauseAsync(), PauseButton.IsEnabled);
        AddContextCommand("Back 10 seconds", "skip_back_10_24_regular", "←", async () => await SeekRelativeAsync(-10), Timeline.IsEnabled);
        AddContextCommand("Forward 30 seconds", "skip_forward_30_24_regular", "→", async () => await SeekRelativeAsync(30), Timeline.IsEnabled);
        MoreMenu.Items.Add(new MenuFlyoutSeparator());
        AddContextTracks("Audio", "headphones_20_regular", AudioSelector);
        AddContextTracks("Subtitles", "closed_caption_24_regular", SubtitleSelector);
        var rates = new MenuFlyoutSubItem { Text = LumenText.Get("Playback speed"), Icon = ContextIcon("top_speed_24_regular") };
        foreach (var rate in new[] { .5, .75, 1, 1.25, 1.5, 2 })
        {
            var option = new ToggleMenuFlyoutItem { Text = FormatPlaybackRate(rate),
                IsChecked = Math.Abs((_engine?.PlaybackRate ?? 1) - rate) < .001, IsEnabled = RateButton.IsEnabled };
            option.Click += async (_, _) =>
            {
                var engine = _engine;
                var playbackId = _coordinator?.ActiveContext?.PlaybackId;
                if (engine is null || playbackId is null) return;
                if (await TryRunPlayerAdjustmentAsync(() => engine.SetPlaybackRateAsync(playbackId.Value, rate)))
                {
                    SavePlaybackPreferences();
                    ShowPlayerOsd("top_speed_24_regular", LumenText.Get("Speed {0}", FormatPlaybackRate(engine.PlaybackRate)));
                }
            };
            rates.Items.Add(option);
        }
        MoreMenu.Items.Add(rates);
        MoreMenu.Items.Add(new MenuFlyoutSeparator());
        AddContextCommand("Picture in picture", "picture_in_picture_24_regular", "P",
            () => { PictureInPictureClicked(PictureInPictureButton, new RoutedEventArgs()); return Task.CompletedTask; }, PictureInPictureButton.IsEnabled);
        AddContextCommand(_fullscreen ? "Exit fullscreen" : "Enter fullscreen", "full_screen_maximize_24_regular", "F11",
            () => { FullscreenRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; });
        AddContextCommand("Playback information", "info_20_regular", "I",
            () => { TogglePlaybackInfo(); return Task.CompletedTask; });
        MoreMenu.Items.Add(new MenuFlyoutSeparator());
        AddContextCommand("Playback settings", "settings_20_regular", string.Empty,
            () => { OpenSidePanel(PlayerSidePanel.Settings, VideoFocusTarget); return Task.CompletedTask; });
        DiagnosticsButton.Text = LumenText.Get("Playback diagnostics");
        MoreMenu.Items.Add(DiagnosticsButton);
    }

    private static ImageIcon ContextIcon(string name) => new()
    {
        Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/w/{name}.svg"))
    };

    private void AddContextCommand(string label, string icon, string shortcut, Func<Task> action, bool enabled = true)
    {
        var command = new MenuFlyoutItem { Text = LumenText.Get(label), Icon = ContextIcon(icon),
            KeyboardAcceleratorTextOverride = shortcut, IsEnabled = enabled };
        command.Click += async (_, _) => await action();
        MoreMenu.Items.Add(command);
    }

    private void AddContextTracks(string heading, string icon, ComboBox selector)
    {
        var submenu = new MenuFlyoutSubItem { Text = LumenText.Get(heading), Icon = ContextIcon(icon) };
        foreach (var item in selector.Items.OfType<ComboBoxItem>())
        {
            var option = new ToggleMenuFlyoutItem { Text = item.Content?.ToString() ?? LumenText.Get("Off"),
                IsEnabled = selector.IsEnabled, IsChecked = ReferenceEquals(selector.SelectedItem, item)
                    && (!ReferenceEquals(selector, SubtitleSelector) || _localSubtitleName is null) };
            option.Click += async (_, _) =>
            {
                if (ReferenceEquals(selector, SubtitleSelector) && ReferenceEquals(selector.SelectedItem, item))
                {
                    BeginExplicitSubtitleSelection();
                    if (item.Tag is -1 || _localSubtitleName is not null || _selectedLocalSubtitle is not null)
                        await ClearCurrentLocalSubtitleAsync();
                    if (item.Tag is int selectedIndex && _coordinator?.ActiveContext is { } active
                        && (active.Selection.SubtitleStreamIndex ?? active.Source.DefaultSubtitleStreamIndex ?? -1) == selectedIndex)
                        RememberServerSubtitleSelection(selectedIndex);
                    RebuildTrackPanel();
                }
                else selector.SelectedItem = item;
            };
            submenu.Items.Add(option);
        }
        if (ReferenceEquals(selector, SubtitleSelector))
        {
            foreach (var prepared in _localSubtitleChoices)
            {
                var option = new ToggleMenuFlyoutItem { Text = LocalSubtitleDisplayName(prepared.FileName), IsChecked = IsLocalSubtitleChoiceSelected(prepared),
                    IsEnabled = CanLoadLocalSubtitle() };
                ToolTipService.SetToolTip(option, LocalSubtitleDisplayName(LocalSubtitleChoicePath(prepared) ?? prepared.FileName));
                option.Click += async (_, _) => await ActivateLocalSubtitleChoiceAsync(prepared);
                submenu.Items.Add(option);
            }
        }
        submenu.IsEnabled = submenu.Items.Count > 0;
        MoreMenu.Items.Add(submenu);
    }

    private void DrawerEpisodesTabClicked(object sender, RoutedEventArgs args) => OpenSidePanel(PlayerSidePanel.Episodes, EpisodeDrawerButton);
    private void DrawerQueueTabClicked(object sender, RoutedEventArgs args) => OpenSidePanel(PlayerSidePanel.Queue, EpisodeDrawerButton);

    private void UpdateDrawerTabs()
    {
        var isEpisode = string.Equals(_item?.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        DrawerEpisodesTab.Visibility = isEpisode ? Visibility.Visible : Visibility.Collapsed;
        DrawerQueueTab.Content = LumenText.Get("Queue ({0})", _queue.Count);
        DrawerEpisodesTab.Background = new SolidColorBrush(_sidePanelMode == PlayerSidePanel.Episodes
            ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Microsoft.UI.Colors.Transparent);
        DrawerQueueTab.Background = new SolidColorBrush(_sidePanelMode == PlayerSidePanel.Queue
            ? Windows.UI.Color.FromArgb(41, 255, 255, 255) : Microsoft.UI.Colors.Transparent);
        EpisodeSeasonSelector.Visibility = _sidePanelMode == PlayerSidePanel.Episodes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlayerDragOver(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems) || !CanLoadLocalSubtitle())
        {
            args.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = LumenText.Get("Drop to load subtitles");
        args.DragUIOverride.IsCaptionVisible = true;
        args.DragUIOverride.IsContentVisible = false;
        SubtitleDropOverlay.Visibility = Visibility.Visible;
        _subtitleDropTimer.Stop();
        _subtitleDropTimer.Start();
        RevealControls();
        args.Handled = true;
    }

    private void PlayerDragLeave(object sender, DragEventArgs args)
    {
        _subtitleDropTimer.Stop();
        _subtitleDropTimer.Start();
    }

    private async void PlayerDrop(object sender, DragEventArgs args)
    {
        _subtitleDropTimer.Stop();
        SubtitleDropOverlay.Visibility = Visibility.Collapsed;
        if (!args.DataView.Contains(StandardDataFormats.StorageItems) || !CanLoadLocalSubtitle()) return;
        args.Handled = true;
        var intent = _playIntent;
        var session = _session;
        var deferral = args.GetDeferral();
        try
        {
            var items = await args.DataView.GetStorageItemsAsync();
            if (intent != _playIntent || !ReferenceEquals(session, _session) || !CanLoadLocalSubtitle()) return;
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is null) return;
            if (!NativePlaybackEngine.IsSupportedLocalSubtitleExtension(file.Name))
            {
                ShowPlayerOsd("closed_caption_24_regular", LumenText.Get("Unsupported subtitle file type"));
                return;
            }
            await LoadLocalSubtitleFileAsync(file);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (intent == _playIntent && ReferenceEquals(session, _session)) ShowOperationError(exception);
        }
        finally { deferral.Complete(); }
    }

    private bool CanLoadLocalSubtitle() => !_externalSubtitlePickerOpen && !_playerFileDialogOpen && !_advancing
        && !_retryInProgress && !_expiredReported && _session is not null && _engine is not null
        && _preparationIntent != _playIntent && _coordinator is { ActiveContext: not null,
            Status: PlaybackStatus.Playing or PlaybackStatus.Paused };

    private void LocalizeV2Controls()
    {
        SetControlLabel(TimeReadout, LumenText.Get("Show remaining time"));
        SetControlLabel(VideoFocusTarget, LumenText.Get("Play or pause"));
        SetControlLabel(StreamInfoButton, LumenText.Get("Playback information"), LumenText.Get("Playback information (I)"));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}
