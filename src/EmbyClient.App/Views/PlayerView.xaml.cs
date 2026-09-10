using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView : UserControl
{
    private ConnectedSession? _session;
    private PlaybackCoordinator? _coordinator;
    private NativePlaybackEngine? _engine;
    private BaseItemDto? _item;
    private readonly TransientPlaybackQueue _queue = new();
    private PlaybackQueueDialog? _queueDialog;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly PlaybackDisplayRequest _displayRequest = new();
    private readonly PlaybackNotificationOwner _notificationOwner = new();
    private readonly PlaybackTimelineDrag _timelineDrag = new();
    private bool _updating;
    private Guid? _keyboardTimelinePlaybackId;
    private ItemPreparationRetry? _preparationRetry;
    private long? _preparationIntent;
    private bool _advancing;
    private Guid? _displayedPlayback;
    private long _bitrate = 20_000_000;
    private CancellationTokenSource? _playRequest;
    private long _playIntent;
    private bool _expiredReported;
    private Guid? _retryRecoveryId;
    private long _retryIntent;
    private bool _retryInProgress;

    public event EventHandler? BackRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? SessionExpired;
    public event EventHandler? QueueChanged;
    public int QueueCount => _queue.Count;
    public bool IsQueueOpen => _queueDialog is not null;
    public bool IsSettingsOpen { get; private set; }

    partial void ObservationClockTick();
    partial void ObservationPositionUpdate();
    partial void ObservationClockState(bool enabled);

    public PlayerView()
    {
        InitializeComponent();
        Timeline.ThumbToolTipValueConverter = new PlaybackTimeConverter();
        _queue.Changed += (_, _) => UpdateQueueControls();
        Timeline.AddHandler(PointerPressedEvent, new PointerEventHandler(TimelinePressed), true);
        Timeline.AddHandler(PointerReleasedEvent, new PointerEventHandler(TimelineReleased), true);
        Timeline.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(TimelineCaptureLost), true);
        Timeline.AddHandler(PointerCanceledEvent, new PointerEventHandler(TimelineCanceled), true);
        // Slider handles its adjustment keys; observe them without replacing native value changes.
        Timeline.AddHandler(KeyDownEvent, new KeyEventHandler(TimelineKeyDown), true);
        Timeline.AddHandler(KeyUpEvent, new KeyEventHandler(TimelineKeyUp), true);
        Unloaded += (_, _) => ClearTimelineInteraction();
        _clock.Tick += (_, _) =>
        {
            ObservationClockTick();
            UpdateDisplayRequest(_coordinator?.ActiveContext?.PlaybackId);
            UpdateRecoveryControls();
            UpdatePosition();
            UpdatePresentationClock();
        };
        _updating = true;
        foreach (var mbps in new[] { 5, 10, 20, 40, 80 })
        {
            var option = new ComboBoxItem { Content = $"{mbps} Mbps", Tag = (long)mbps * 1_000_000 };
            QualitySelector.Items.Add(option);
            if (mbps == 20) QualitySelector.SelectedItem = option;
        }
        _updating = false;
    }

    public async Task SetSessionAsync(ConnectedSession session)
    {
        await DisconnectAsync();
        _session = session;
        _expiredReported = false;
        var engine = new NativePlaybackEngine(DispatcherQueue, VideoSurface,
            initialVolumeLevel: (int)Volume.Value, initialMuted: MuteButton.IsChecked == true);
        _engine = engine;
        engine.MediaCommandRequested += SystemMediaCommandRequested;
        engine.EventReceived += EnginePlaybackStateChanged;
        engine.Diagnostic += EngineDiagnosticReceived;
        _coordinator = new PlaybackCoordinator(session.Api, engine);
        _coordinator.StatusChanged += CoordinatorStatusChanged;
        _coordinator.Diagnostic += CoordinatorDiagnosticReceived;
        BeginDiagnosticPlayback(_playIntent);
        AutoPlayNext.IsOn = session.User.Configuration?.EnableNextEpisodeAutoPlay != false;
        _bitrate = QualitySelector.SelectedItem is ComboBoxItem { Tag: long desiredBitrate } ? desiredBitrate : 20_000_000;
        if (session.User.Policy?.RemoteClientBitrateLimit is long limit && limit > 0)
            _bitrate = Math.Min(_bitrate, limit);
        UpdateQueueControls();
        UpdatePresentationClock();
    }

    private void UpdatePresentationClock()
    {
        var coordinator = _coordinator;
        // Keep active playback polling, including pause and system-media updates. A failed
        // display release also needs retries after playback has already reached a terminal state.
        if (coordinator is not null && !NeedsPlaybackPolling(coordinator.Status))
        {
            // A queued terminal notification may carry a retired ID and be ignored by the
            // display owner. Reconcile live ownership before removing its final polling path.
            UpdateDisplayRequest(null, useCurrentPlayback: true);
        }
        var shouldRun = coordinator is not null
            && (_displayRequest.NeedsReleaseRetry || NeedsPlaybackPolling(coordinator.Status));
        if (shouldRun == _clock.IsEnabled) return;
        if (shouldRun) _clock.Start();
        else _clock.Stop();
        ObservationClockState(_clock.IsEnabled);
    }

    private static bool NeedsPlaybackPolling(PlaybackStatus status) => status is PlaybackStatus.Opening
        or PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Buffering or PlaybackStatus.Seeking;

    internal QueueAddResult Enqueue(BaseItemDto item) => _session is null ? QueueAddResult.InvalidItem : _queue.TryAdd(item);

    public async Task ShowQueueAsync(XamlRoot xamlRoot, ElementTheme theme, Control? trigger = null)
    {
        if (_session is null || IsModalOpen) return;
        var restoreFocus = CaptureDialogFocus(trigger, xamlRoot);
        var dialog = new PlaybackQueueDialog(_queue) { XamlRoot = xamlRoot, RequestedTheme = theme };
        _queueDialog = dialog;
        UpdateQueueControls();
        try { await dialog.ShowAsync(); }
        finally
        {
            dialog.Detach();
            if (ReferenceEquals(_queueDialog, dialog)) _queueDialog = null;
            UpdateQueueControls();
            restoreFocus();
        }
    }

    private Action CaptureDialogFocus(Control? trigger, XamlRoot xamlRoot)
    {
        var session = _session;
        var intent = _playIntent;
        var playerVisibility = Visibility;
        return () =>
        {
            if (trigger is null || IsModalOpen || !ReferenceEquals(session, _session)
                || intent != _playIntent || playerVisibility != Visibility) return;
            try
            {
                if (!trigger.IsLoaded || !trigger.IsEnabled || !ReferenceEquals(trigger.XamlRoot, xamlRoot)
                    || trigger.ActualWidth <= 0 || trigger.ActualHeight <= 0) return;
                for (DependencyObject? element = trigger; element is not null; element = VisualTreeHelper.GetParent(element))
                    if (element is UIElement visual && visual.Visibility != Visibility.Visible) return;
                // The caller has re-enabled both toolbar entries before restoring the original target.
                trigger.Focus(FocusState.Programmatic);
            }
            catch (Exception) { /* Focus restoration is best effort when the window is closing. */ }
        };
    }

    private void UpdateQueueControls()
    {
        QueueCountText.Text = _queue.Count.ToString();
        SetControlLabel(QueueButton, $"Open play queue, {_queue.Count} items", $"Play queue ({_queue.Count})");
        QueueButton.IsEnabled = _session is not null && !IsModalOpen;
        DiagnosticsButton.IsEnabled = !IsModalOpen;
        NextButton.IsEnabled = _session is not null && _queue.Count > 0 && !_advancing;
        var nextLabel = _queue.Count > 0 ? $"Next in queue ({_queue.Count})" : "Next in queue";
        SetControlLabel(NextButton, nextLabel);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetControlLabel(Control control, string name, string? tooltip = null)
    {
        AutomationProperties.SetName(control, name);
        ToolTipService.SetToolTip(control, tooltip ?? name);
    }

    public void SetFullscreenState(bool fullscreen)
    {
        FullscreenButton.Content = new SymbolIcon(fullscreen ? Symbol.BackToWindow : Symbol.FullScreen);
        var label = fullscreen ? "Exit fullscreen" : "Enter fullscreen";
        SetControlLabel(FullscreenButton, label, $"{label} (F11)");
    }

    public Task PlayItemAsync(BaseItemDto item, long startPositionTicks = 0) => PlayItemCoreAsync(item, startPositionTicks);

    private async Task PlayItemCoreAsync(BaseItemDto item, long startPositionTicks, Guid? queuedEntryId = null)
    {
        var session = _session;
        var coordinator = _coordinator;
        if (session is null || coordinator is null || item.Id is null) return;
        var (intent, cancellationToken) = BeginPlayRequest(coordinator);
        _preparationIntent = intent;
        PlaybackNotice.IsOpen = false;
        StateText.Text = "Loading item";
        BufferingRing.IsActive = true;
        SetTransportAvailability(false);
        Timeline.IsEnabled = false;
        PauseButton.IsEnabled = false;
        bool OwnsRequest() => intent == _playIntent && ReferenceEquals(session, _session)
            && ReferenceEquals(coordinator, _coordinator);
        await RunAsync(() => PlaybackItemPreparation.RunAsync<BaseItemDto>(
            async token => queuedEntryId is { } entryId
                ? await _queue.TryPrepareAndConsumeAsync(entryId,
                    (queued, requestToken) => session.Api.GetItemAsync(queued.Id!, requestToken), OwnsRequest, token)
                : await session.Api.GetItemAsync(item.Id, token),
            async (detail, token) =>
            {
                _item = detail;
                TitleText.Text = _item.Name ?? item.Name ?? "Now playing";
                PopulateSources();
                _notificationOwner.Arm(intent);
                try
                {
                    await coordinator.PlayAsync(new PlaybackSelection
                    {
                        ItemId = item.Id, StartPositionTicks = Math.Max(0, startPositionTicks),
                        MaxStreamingBitrate = _bitrate
                    }, token);
                }
                finally
                {
                    if (OwnsRequest()) _preparationIntent = null;
                }
            }, OwnsRequest, () => coordinator.StopAsync(), outcome =>
            {
                // The old owner's cancellation may already have retired its context. The scoped
                // preparation policy waits for cleanup without accepting the old Ended notification.
                _notificationOwner.Invalidate(intent);
                _preparationIntent = null;
                _displayRequest.Suspend();
                ClearTimelineInteraction();
                _preparationRetry = outcome.PreparationFailed
                    && (queuedEntryId is null || _queue.Next?.EntryId == queuedEntryId)
                    ? new(item, startPositionTicks, queuedEntryId) : null;
                BufferingRing.IsActive = false;
                Timeline.IsEnabled = false;
                SetTransportAvailability(false);
                var failed = outcome.PreparationFailed || outcome.CleanupFailed || coordinator.Status == PlaybackStatus.Failed;
                StateText.Text = failed ? "Unable to play" : "Ready to play";
                PauseButton.Content = new SymbolIcon(Symbol.Play);
                PauseButton.IsEnabled = _preparationRetry is not null || _item is not null;
                SetControlLabel(PauseButton,
                    _preparationRetry is not null ? "Retry loading item" : "Play again");
                if (failed) PlaybackNotice.Severity = InfoBarSeverity.Error;
                if (outcome.CleanupFailed || coordinator.Status == PlaybackStatus.Failed)
                    ShowPlaybackError("EngineStopFailed");
                UpdateRecoveryControls();
                UpdatePresentationClock();
            }, cancellationToken));
    }

    private void CoordinatorStatusChanged(object? sender, PlaybackStatusChangedEventArgs args)
    {
        if (sender is not PlaybackCoordinator coordinator || !ReferenceEquals(coordinator, _coordinator)) return;
        var notification = _notificationOwner.Capture(coordinator, args.Context?.PlaybackId,
            coordinator.ActiveContext?.PlaybackId, isOpening: args.Status == PlaybackStatus.Opening,
            isNegotiating: args.Status == PlaybackStatus.Negotiating, isEnded: args.Status == PlaybackStatus.Ended);
        if (notification is not { } ticket) return;
        RecordDiagnosticStatus(ticket, args);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _coordinator) || !_notificationOwner.IsCurrent(ticket)) return;
            if (_preparationIntent == ticket.Intent) _preparationIntent = null;
            UpdateDisplayRequest(args.Context?.PlaybackId);
            StateText.Text = args.Status switch
            {
                PlaybackStatus.Negotiating or PlaybackStatus.Opening => "Preparing playback",
                PlaybackStatus.Playing => "Playing",
                PlaybackStatus.Paused => "Paused",
                PlaybackStatus.Buffering => "Buffering",
                PlaybackStatus.Seeking => "Seeking",
                PlaybackStatus.Ended => "Playback finished",
                PlaybackStatus.Failed => "Unable to play",
                _ => "Ready to play"
            };
            BufferingRing.IsActive = args.Status is PlaybackStatus.Negotiating or PlaybackStatus.Opening or PlaybackStatus.Buffering;
            var canStart = args.Status is PlaybackStatus.Paused or PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle;
            PauseButton.Content = new SymbolIcon(canStart ? Symbol.Play : Symbol.Pause);
            SetControlLabel(PauseButton,
                args.Status == PlaybackStatus.Paused ? "Resume" : canStart ? "Play again" : "Pause");
            if (args.Context is { } context)
            {
                if (_displayedPlayback != context.PlaybackId) PopulateTracks(context);
            }
            if (args.Status == PlaybackStatus.Failed)
            {
                SetRecoveryCandidate(args.Recovery);
                ShowPlaybackError(args.ErrorCode);
            }
            if (args.Status is PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle)
                Timeline.IsEnabled = false;
            UpdatePosition();
            SetTransportAvailability(args.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Buffering or PlaybackStatus.Seeking);
            PauseButton.IsEnabled = args.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle;
            UpdateRecoveryControls();
            UpdatePresentationClock();
            if (args.Status == PlaybackStatus.Ended && AutoPlayNext.IsOn)
                _ = AdvanceAsync(ticket);
        });
    }

    private (long Intent, CancellationToken Token) BeginPlayRequest(PlaybackCoordinator coordinator, bool preserveRecovery = false)
    {
        ClearTimelineInteraction();
        _preparationRetry = null;
        _preparationIntent = null;
        var intent = ++_playIntent;
        // Invalidate old notifications before cancellation can publish another terminal status.
        _notificationOwner.BeginPlay(coordinator, intent, coordinator.ActiveContext?.PlaybackId);
        BeginDiagnosticPlayback(intent);
        _displayRequest.ResumeTracking();
        // Recovery deliberately retains its retired owner's token until Core consumes the target.
        // Disposing that CTS releases its resources without cancelling recovery; normal Play/Restart still cancel.
        if (!preserveRecovery) _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = new CancellationTokenSource();
        _displayedPlayback = null;
        _retryRecoveryId = null;
        UpdateRecoveryControls();
        return (intent, _playRequest.Token);
    }

    private void SetRecoveryCandidate(PlaybackRecovery? recovery)
    {
        _retryRecoveryId = recovery?.RecoveryId;
        _retryIntent = _playIntent;
        UpdateRecoveryControls();
    }

    private void UpdateRecoveryControls()
    {
        var coordinator = _coordinator;
        var available = !_retryInProgress && _retryIntent == _playIntent && _retryRecoveryId.HasValue
            && coordinator is not null && coordinator.Status == PlaybackStatus.Failed && coordinator.CanRetry
            && coordinator.Recovery?.RecoveryId == _retryRecoveryId;
        RetryButton.IsEnabled = available;
        RetryButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RetryClicked(object sender, RoutedEventArgs args)
    {
        var coordinator = _coordinator;
        var recovery = coordinator?.Recovery;
        if (_retryInProgress || _retryIntent != _playIntent || _retryRecoveryId is not { } expected
            || coordinator is null || coordinator.Status != PlaybackStatus.Failed || !coordinator.CanRetry
            || recovery?.RecoveryId != expected) return;
        _retryInProgress = true;
        var (intent, token) = BeginPlayRequest(coordinator, preserveRecovery: true);
        try
        {
            await RunAsync(async () =>
            {
                if (!ReferenceEquals(coordinator, _coordinator) || intent != _playIntent
                    || coordinator.Recovery?.RecoveryId != expected) return;
                _notificationOwner.Arm(intent);
                await coordinator.RetryAsync(expected, token);
            });
        }
        finally
        {
            _retryInProgress = false;
            if (ReferenceEquals(coordinator, _coordinator) && intent == _playIntent)
                SetRecoveryCandidate(coordinator.Recovery);
            UpdateRecoveryControls();
        }
    }

    private Task ReplayCurrentAsync()
    {
        if (_preparationRetry is { } preparation)
            return PlayItemCoreAsync(preparation.Item, preparation.StartPositionTicks, preparation.QueueEntryId);
        var coordinator = _coordinator;
        if (coordinator is null) return Task.CompletedTask;
        var (intent, token) = BeginPlayRequest(coordinator);
        return RunAsync(async () =>
        {
            _notificationOwner.Arm(intent);
            await coordinator.ReplayAsync(token);
        });
    }

    private void SetTransportAvailability(bool available)
    {
        SourceSelector.IsEnabled = available && SourceSelector.Items.Count > 1;
        AudioSelector.IsEnabled = available && AudioSelector.Items.Count > 1;
        SubtitleSelector.IsEnabled = available && SubtitleSelector.Items.Count > 1;
        QualitySelector.IsEnabled = available;
        Volume.IsEnabled = available;
        MuteButton.IsEnabled = available;
    }

    private void PopulateSources()
    {
        _updating = true;
        SourceSelector.Items.Clear();
        foreach (var source in _item?.MediaSources ?? [])
        {
            var option = new ComboBoxItem { Content = source.Name ?? source.Container ?? "Original", Tag = source.Id };
            ToolTipService.SetToolTip(option, option.Content);
            SourceSelector.Items.Add(option);
        }
        SourceSelector.IsEnabled = SourceSelector.Items.Count > 1;
        _updating = false;
    }

    private void PopulateTracks(PlaybackContext context)
    {
        _updating = true;
        _displayedPlayback = context.PlaybackId;
        foreach (var option in SourceSelector.Items.OfType<ComboBoxItem>())
            if ((string?)option.Tag == context.Source.Id) SourceSelector.SelectedItem = option;
        AudioSelector.Items.Clear();
        SubtitleSelector.Items.Clear();
        var off = new ComboBoxItem { Content = "Off", Tag = -1 };
        SubtitleSelector.Items.Add(off);
        SubtitleSelector.SelectedItem = off;
        var selectedAudio = context.Selection.AudioStreamIndex ?? context.Source.DefaultAudioStreamIndex;
        var selectedSubtitle = context.Selection.SubtitleStreamIndex ?? context.Source.DefaultSubtitleStreamIndex ?? -1;
        foreach (var stream in context.Source.MediaStreams ?? [])
        {
            var option = new ComboBoxItem
            {
                Content = stream.DisplayTitle ?? stream.Title ?? $"{stream.Language ?? "Unknown"} · {stream.Codec}",
                Tag = stream.Index
            };
            ToolTipService.SetToolTip(option, option.Content);
            if (string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                AudioSelector.Items.Add(option);
                if (stream.Index == selectedAudio) AudioSelector.SelectedItem = option;
            }
            if (string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
            {
                SubtitleSelector.Items.Add(option);
                if (stream.Index == selectedSubtitle) SubtitleSelector.SelectedItem = option;
            }
        }
        AudioSelector.IsEnabled = AudioSelector.Items.Count > 1;
        _updating = false;
    }

    private void UpdatePosition()
    {
        ObservationPositionUpdate();
        if (_preparationIntent == _playIntent) return;
        var context = _coordinator?.ActiveContext;
        if (context is null)
        {
            ClearTimelineInteraction();
            return;
        }
        _engine?.UpdateMediaControls(context, _coordinator!.Status, TitleText.Text);
        var duration = context.Source.RunTimeTicks;
        PositionText.Text = FormatTime(context.PositionTicks);
        DurationText.Text = duration.HasValue ? FormatTime(duration.Value) : "Live";
        Timeline.IsEnabled = context.CanSeek && duration > 0;
        _timelineDrag.Reconcile(context.PlaybackId, Timeline.IsEnabled);
        if (_keyboardTimelinePlaybackId != context.PlaybackId || !Timeline.IsEnabled)
            _keyboardTimelinePlaybackId = null;
        if (!_timelineDrag.IsActive && _keyboardTimelinePlaybackId is null)
        {
            Timeline.Maximum = Math.Max(1, duration.GetValueOrDefault() / (double)TimeSpan.TicksPerSecond);
            Timeline.Value = Math.Clamp(context.PositionTicks / (double)TimeSpan.TicksPerSecond, 0, Timeline.Maximum);
        }
    }

    private void EnginePlaybackStateChanged(object? sender, PlaybackEngineEventArgs args)
    {
        if (args.Kind == PlaybackEngineEventKind.PositionChanged) return;
        // Native pause/buffering can precede a coordinator report waiting on the server.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _engine)) return;
            UpdateDisplayRequest(args.Snapshot.PlaybackId);
            UpdatePresentationClock();
        });
    }

    private void UpdateDisplayRequest(Guid? notificationPlaybackId, bool useCurrentPlayback = false)
    {
        var coordinator = _coordinator;
        var context = coordinator?.ActiveContext;
        var snapshot = _engine?.Snapshot;
        var isPlayingVideo = coordinator?.Status == PlaybackStatus.Playing
            && snapshot?.PlaybackId == context?.PlaybackId && snapshot?.State == PlaybackEngineState.Playing
            && context?.Source.MediaStreams?.Any(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase)) == true;
        _displayRequest.Update(context?.PlaybackId, useCurrentPlayback ? context?.PlaybackId : notificationPlaybackId, isPlayingVideo);
    }

    private static string FormatTime(long ticks)
    {
        var time = TimeSpan.FromTicks(Math.Max(0, ticks));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private async void SystemMediaCommandRequested(object? sender, NativeMediaCommandEventArgs args)
    {
        var coordinator = _coordinator;
        if (!ReferenceEquals(sender, _engine) || coordinator?.ActiveContext?.PlaybackId != args.PlaybackId) return;
        if (args.Command == NativeMediaCommand.Stop)
        {
            ClearTimelineInteraction();
            _preparationRetry = null;
            _preparationIntent = null;
            _notificationOwner.BeginStop(coordinator, ++_playIntent, args.PlaybackId);
            _retryRecoveryId = null;
            UpdateRecoveryControls();
        }
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(coordinator, _coordinator) || coordinator.ActiveContext?.PlaybackId != args.PlaybackId) return;
            switch (args.Command)
            {
                case NativeMediaCommand.Play:
                    _displayRequest.ResumeTracking();
                    await coordinator.ResumeAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Pause:
                    await coordinator.PauseAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Stop:
                    _displayRequest.Suspend();
                    await coordinator.StopAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Seek when args.PositionTicks is { } ticks:
                    await coordinator.SeekAsync(args.PlaybackId, ticks);
                    break;
            }
        });
    }

    private void TimelinePressed(object sender, PointerRoutedEventArgs args)
    {
        if (Timeline.IsEnabled && _coordinator?.ActiveContext is { CanSeek: true } context)
            _timelineDrag.Begin(args.Pointer.PointerId, context.PlaybackId);
    }

    private async void TimelineReleased(object sender, PointerRoutedEventArgs args)
    {
        var coordinator = _coordinator;
        var value = Timeline.Value;
        var playbackId = _timelineDrag.Complete(args.Pointer.PointerId,
            coordinator?.ActiveContext?.PlaybackId, Timeline.IsEnabled);
        if (coordinator is not null && playbackId is { } expected)
            await RunAsync(() => coordinator.SeekAsync(expected, checked((long)(value * TimeSpan.TicksPerSecond))));
    }

    private void TimelineCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_timelineDrag.Capture(args.Pointer.PointerId) is not { } ticket) return;
        // Slider can release capture inside its own PointerReleased handler, before our routed
        // handler observes the release. Defer cancellation so that normal release can commit once.
        if (!DispatcherQueue.TryEnqueue(() => _timelineDrag.Cancel(ticket))) _timelineDrag.Cancel(ticket);
    }

    private void TimelineCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_timelineDrag.Capture(args.Pointer.PointerId) is { } ticket) _timelineDrag.Cancel(ticket);
    }

    private static bool IsTimelineAdjustmentKey(VirtualKey key) => key is VirtualKey.Left or VirtualKey.Right
        or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;

    private void TimelineKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsTimelineAdjustmentKey(args.Key) && Timeline.IsEnabled
            && _coordinator?.ActiveContext is { CanSeek: true } context)
            _keyboardTimelinePlaybackId ??= context.PlaybackId;
    }

    private async void TimelineKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (!IsTimelineAdjustmentKey(args.Key) || _keyboardTimelinePlaybackId is not { } playbackId) return;
        var value = Timeline.Value;
        var coordinator = _coordinator;
        _keyboardTimelinePlaybackId = null;
        if (coordinator is not null && Timeline.IsEnabled)
            await RunAsync(() => coordinator.SeekAsync(playbackId, checked((long)(value * TimeSpan.TicksPerSecond))));
    }

    private void TimelineLostFocus(object sender, RoutedEventArgs args) => ClearTimelineInteraction();

    private void ClearTimelineInteraction()
    {
        _keyboardTimelinePlaybackId = null;
        _timelineDrag.Clear();
    }

    private async void PauseClicked(object sender, RoutedEventArgs args) => await TogglePauseAsync();

    public Task TogglePauseAsync() => RunAsync(async () =>
    {
        if (_coordinator is null) return;
        if (_preparationRetry is not null)
        {
            await ReplayCurrentAsync();
        }
        else if (_coordinator.Status == PlaybackStatus.Paused)
        {
            _displayRequest.ResumeTracking();
            await _coordinator.ResumeAsync();
        }
        else if (_coordinator.Status is PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle)
        {
            await ReplayCurrentAsync();
        }
        else await _coordinator.PauseAsync();
    });

    public Task SeekRelativeAsync(int seconds) => RunAsync(async () =>
    {
        var coordinator = _coordinator;
        if (coordinator?.ActiveContext is not { CanSeek: true } context) return;
        await coordinator.SeekAsync(Math.Max(0, checked(context.PositionTicks + seconds * TimeSpan.TicksPerSecond)));
    });

    private async void ReplayClicked(object sender, RoutedEventArgs args) => await ReplayCurrentAsync();

    private async void VolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || _coordinator is null) return;
        await RunAsync(() => _coordinator.SetVolumeAsync((int)args.NewValue, MuteButton.IsChecked == true));
    }

    private async void MuteClicked(object sender, RoutedEventArgs args)
    {
        var muted = MuteButton.IsChecked == true;
        MuteButton.Content = new SymbolIcon(muted ? Symbol.Mute : Symbol.Volume);
        SetControlLabel(MuteButton, muted ? "Unmute" : "Mute");
        await RunAsync(async () =>
        {
            if (_coordinator is not null) await _coordinator.SetVolumeAsync((int)Volume.Value, muted);
        });
    }

    private async void SourceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _coordinator?.ActiveContext is not { } context
            || SourceSelector.SelectedItem is not ComboBoxItem { Tag: string source } || source == context.Source.Id) return;
        await RunAsync(() => _coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            MediaSourceId = source, MaxStreamingBitrate = _bitrate
        }));
    }

    private async void AudioChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _coordinator?.ActiveContext is null || AudioSelector.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        await RunAsync(() => _coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { AudioStreamIndex = index }));
    }

    private async void SubtitleChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || _coordinator?.ActiveContext is null || SubtitleSelector.SelectedItem is not ComboBoxItem { Tag: int index }) return;
        await RunAsync(() => _coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = index }));
    }

    private async void QualityChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updating || QualitySelector.SelectedItem is not ComboBoxItem { Tag: long bitrate }) return;
        _bitrate = bitrate;
        if (_session?.User.Policy?.RemoteClientBitrateLimit is long limit && limit > 0) _bitrate = Math.Min(_bitrate, limit);
        if (_coordinator?.ActiveContext is not null)
            await RunAsync(() => _coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = _bitrate }));
    }

    private async void NextClicked(object sender, RoutedEventArgs args) => await AdvanceAsync();
    private async void QueueClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => ShowQueueAsync(XamlRoot, RequestedTheme, sender as Control));
    private async void DiagnosticsClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => ShowDiagnosticsAsync(XamlRoot, RequestedTheme, sender as Control));
    private void PlaybackSettingsOpening(object sender, object args) => IsSettingsOpen = true;
    private void PlaybackSettingsClosed(object sender, object args) => IsSettingsOpen = false;
    private void FullscreenClicked(object sender, RoutedEventArgs args) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    private void BackClicked(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async Task AdvanceAsync(PlaybackNotificationOwner.Ticket? completion = null)
    {
        var session = _session;
        var item = _item;
        var intent = _playIntent;
        var cancellationToken = _playRequest?.Token ?? CancellationToken.None;
        if (_advancing || session is null) return;
        if (completion is { } ended && !_notificationOwner.TryBeginAdvance(ended)) return;
        _advancing = true;
        UpdateQueueControls();
        try
        {
            BaseItemDto? next = _queue.Next?.Item;
            if (next is null && item?.SeriesId is { } seriesId)
            {
                var episodes = await session.Api.GetEpisodesAsync(seriesId, item.SeasonId, cancellationToken);
                var index = Array.FindIndex(episodes.Items, x => x.Id == item.Id);
                if (index >= 0 && index + 1 < episodes.Items.Length) next = episodes.Items[index + 1];
                else
                {
                    var suggestions = await session.Api.GetNextUpAsync(new NextUpQuery { SeriesId = seriesId, Limit = 1 }, cancellationToken);
                    next = suggestions.Items.FirstOrDefault(x => x.Id != item.Id);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (intent != _playIntent || !ReferenceEquals(session, _session)
                || completion is { } completed && !_notificationOwner.IsCurrent(completed)) return;
            // Explicit queue entries take priority even when added during the automatic episode lookup.
            // The entry remains editable until its detail request succeeds and the same head still owns playback.
            if (_queue.Next is { } queued) await PlayItemCoreAsync(queued.Item, 0, queued.EntryId);
            else if (next is not null) await PlayItemAsync(next);
        }
        catch (EmbyApiException ex) when (ex.IsAuthenticationFailure && ex.ApplicationErrorCode != "ParentalControl")
        {
            if (ReferenceEquals(session, _session) && intent == _playIntent
                && (!completion.HasValue || _notificationOwner.IsCurrent(completion.Value))) ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _session) && intent == _playIntent
                && (!completion.HasValue || _notificationOwner.IsCurrent(completion.Value)))
            { PlaybackNotice.Message = UiErrors.Describe(ex); PlaybackNotice.IsOpen = true; }
        }
        finally { _advancing = false; UpdateQueueControls(); }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        var owner = _coordinator;
        var intent = _playIntent;
        PlaybackNotice.IsOpen = false;
        try { await operation(); }
        catch (Exception) when (!ReferenceEquals(owner, _coordinator) || intent != _playIntent) { }
        catch (PlaybackException ex) { ShowPlaybackError(ex.ErrorCode); }
        catch (EmbyApiException ex) when (ex.IsAuthenticationFailure && ex.ApplicationErrorCode != "ParentalControl")
        { ReportExpiredSession(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PlaybackNotice.Message = UiErrors.Describe(ex); PlaybackNotice.IsOpen = true; }
        finally { UpdatePresentationClock(); }
    }

    private void ShowPlaybackError(string? code)
    {
        if (code is "AuthenticationExpired" or "AuthenticationRequired") { ReportExpiredSession(); return; }
        PlaybackNotice.Severity = InfoBarSeverity.Error;
        PlaybackNotice.Message = code switch
        {
            "NoCompatibleSource" or "NoCompatibleTranscode" => "The server has no compatible stream for these playback settings.",
            "NegotiationRejected" => "The server did not allow this playback. Check your account permissions and quality settings.",
            "AccessRestricted" or "NotAllowed" => "The server's access rules do not allow this playback.",
            "UnsupportedTrack" or "UnsupportedSubtitle" => "This track cannot be played with the current settings. Choose another track or turn subtitles off.",
            _ => "Playback could not start or was interrupted. Check your connection, or choose another version or quality."
        };
        PlaybackNotice.IsOpen = true;
        BufferingRing.IsActive = false;
    }

    private void ReportExpiredSession()
    {
        if (_expiredReported || _session is null) return;
        _displayRequest.Suspend();
        _expiredReported = true;
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        ClearTimelineInteraction();
        _preparationRetry = null;
        _preparationIntent = null;
        _displayRequest.Suspend();
        var intent = ++_playIntent;
        _retryRecoveryId = null;
        UpdateRecoveryControls();
        var coordinator = _coordinator;
        if (coordinator is not null) _notificationOwner.BeginStop(coordinator, intent, coordinator.ActiveContext?.PlaybackId);
        else _notificationOwner.Invalidate(intent);
        _playRequest?.Cancel();
        return RunAsync(async () => { if (coordinator is not null) await coordinator.StopAsync(); });
    }

    public async Task DisconnectAsync()
    {
        ClearTimelineInteraction();
        _preparationRetry = null;
        _preparationIntent = null;
        _displayRequest.Suspend();
        _queueDialog?.Hide();
        _diagnosticsDialog?.Hide();
        _clock.Stop();
        ObservationClockState(false);
        _notificationOwner.Invalidate(++_playIntent);
        _retryRecoveryId = null;
        UpdateRecoveryControls();
        _queue.Clear();
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = null;
        var coordinator = _coordinator;
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            engine.MediaCommandRequested -= SystemMediaCommandRequested;
            engine.EventReceived -= EnginePlaybackStateChanged;
        }
        _coordinator = null;
        _session = null;
        UpdateQueueControls();
        if (coordinator is not null)
        {
            coordinator.StatusChanged -= CoordinatorStatusChanged;
            var retired = false;
            try { await coordinator.DisposeAsync(); retired = true; }
            finally
            {
                coordinator.Diagnostic -= CoordinatorDiagnosticReceived;
                if (engine is not null) engine.Diagnostic -= EngineDiagnosticReceived;
                RecordDiagnosticRetirement(retired);
                _displayRequest.Suspend();
            }
        }
        _item = null;
        _displayedPlayback = null;
    }

    private sealed record ItemPreparationRetry(BaseItemDto Item, long StartPositionTicks, Guid? QueueEntryId);
}
