using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView : UserControl
{
    private ConnectedSession? _session;
    private PlaybackCoordinator? _coordinator;
    private NativePlaybackEngine? _engine;
    private BaseItemDto? _item;
    private readonly Queue<BaseItemDto> _queue = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _updating;
    private bool _scrubbing;
    private bool _advancing;
    private Guid? _displayedPlayback;
    private long _bitrate = 20_000_000;
    private CancellationTokenSource? _playRequest;
    private long _playIntent;
    private bool _expiredReported;

    public event EventHandler? BackRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? SessionExpired;

    public PlayerView()
    {
        InitializeComponent();
        Timeline.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), true);
        Timeline.AddHandler(PointerReleasedEvent, new PointerEventHandler(TimelineReleased), true);
        _clock.Tick += (_, _) => UpdatePosition();
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
        _coordinator = new PlaybackCoordinator(session.Api, engine);
        _coordinator.StatusChanged += CoordinatorStatusChanged;
        _coordinator.Diagnostic += (sender, diagnostic) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _coordinator)) return;
            if (diagnostic.Operation == "Fallback") return;
            if (diagnostic.ErrorCode == "AuthenticationExpired") { ReportExpiredSession(); return; }
            PlaybackNotice.Message = "A playback update could not be confirmed by the server. Check your connection if your progress is not saved.";
            PlaybackNotice.Severity = InfoBarSeverity.Warning;
            PlaybackNotice.IsOpen = true;
        });
        AutoPlayNext.IsChecked = session.User.Configuration?.EnableNextEpisodeAutoPlay != false;
        _bitrate = QualitySelector.SelectedItem is ComboBoxItem { Tag: long desiredBitrate } ? desiredBitrate : 20_000_000;
        if (session.User.Policy?.RemoteClientBitrateLimit is long limit && limit > 0)
            _bitrate = Math.Min(_bitrate, limit);
        _clock.Start();
    }

    public void Enqueue(BaseItemDto item)
    {
        _queue.Enqueue(item);
        NextButton.IsEnabled = true;
        NextButton.Content = $"Next in queue ({_queue.Count})";
    }

    public async Task PlayItemAsync(BaseItemDto item, long startPositionTicks = 0)
    {
        var session = _session;
        var coordinator = _coordinator;
        if (session is null || coordinator is null || item.Id is null) return;
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = new CancellationTokenSource();
        var cancellationToken = _playRequest.Token;
        var intent = ++_playIntent;
        PlaybackNotice.IsOpen = false;
        SetTransportAvailability(false);
        PauseButton.IsEnabled = false;
        TitleText.Text = item.Name ?? "Now playing";
        _displayedPlayback = null;
        _item = item;
        await RunAsync(async () =>
        {
            var detail = await session.Api.GetItemAsync(item.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (intent != _playIntent || !ReferenceEquals(session, _session)) return;
            _item = detail;
            TitleText.Text = _item.Name ?? item.Name ?? "Now playing";
            PopulateSources();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = item.Id, StartPositionTicks = Math.Max(0, startPositionTicks),
                MaxStreamingBitrate = _bitrate
            }, cancellationToken);
        });
    }

    private void CoordinatorStatusChanged(object? sender, PlaybackStatusChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _coordinator)) return;
            StateText.Text = args.Status.ToString();
            BufferingRing.IsActive = args.Status is PlaybackStatus.Negotiating or PlaybackStatus.Opening or PlaybackStatus.Buffering;
            var canStart = args.Status is PlaybackStatus.Paused or PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle;
            PauseButton.Content = new SymbolIcon(canStart ? Symbol.Play : Symbol.Pause);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton,
                args.Status == PlaybackStatus.Paused ? "Resume" : canStart ? "Play again" : "Pause");
            if (args.Context is { } context)
            {
                StateText.Text = $"{args.Status} · {context.DeliveryMethod}";
                if (_displayedPlayback != context.PlaybackId) PopulateTracks(context);
            }
            if (args.Status == PlaybackStatus.Failed)
                ShowPlaybackError(args.ErrorCode);
            if (args.Status is PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle)
                Timeline.IsEnabled = false;
            UpdatePosition();
            SetTransportAvailability(args.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Buffering or PlaybackStatus.Seeking);
            PauseButton.IsEnabled = args.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle;
            if (args.Status == PlaybackStatus.Ended && AutoPlayNext.IsChecked == true)
                _ = AdvanceAsync();
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
            SourceSelector.Items.Add(new ComboBoxItem { Content = source.Name ?? source.Container ?? "Original", Tag = source.Id });
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
        var context = _coordinator?.ActiveContext;
        if (context is null) return;
        _engine?.UpdateMediaControls(context, _coordinator!.Status, TitleText.Text);
        var duration = context.Source.RunTimeTicks;
        PositionText.Text = FormatTime(context.PositionTicks);
        DurationText.Text = duration.HasValue ? FormatTime(duration.Value) : "Live";
        Timeline.IsEnabled = context.CanSeek && duration > 0;
        if (!_scrubbing)
        {
            Timeline.Maximum = Math.Max(1, duration.GetValueOrDefault() / (double)TimeSpan.TicksPerSecond);
            Timeline.Value = Math.Clamp(context.PositionTicks / (double)TimeSpan.TicksPerSecond, 0, Timeline.Maximum);
        }
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
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(coordinator, _coordinator) || coordinator.ActiveContext?.PlaybackId != args.PlaybackId) return;
            switch (args.Command)
            {
                case NativeMediaCommand.Play:
                    await coordinator.ResumeAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Pause:
                    await coordinator.PauseAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Stop:
                    await coordinator.StopAsync(args.PlaybackId);
                    break;
                case NativeMediaCommand.Seek when args.PositionTicks is { } ticks:
                    await coordinator.SeekAsync(args.PlaybackId, ticks);
                    break;
            }
        });
    }

    private async void TimelineReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        await SeekFromSliderAsync();
    }

    private async void TimelineKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
            await SeekFromSliderAsync();
    }

    private Task SeekFromSliderAsync() => RunAsync(async () =>
    {
        if (_coordinator is not null && Timeline.IsEnabled)
            await _coordinator.SeekAsync(checked((long)(Timeline.Value * TimeSpan.TicksPerSecond)));
    });

    private async void PauseClicked(object sender, RoutedEventArgs args) => await TogglePauseAsync();

    public Task TogglePauseAsync() => RunAsync(async () =>
    {
        if (_coordinator is null) return;
        if (_coordinator.Status == PlaybackStatus.Paused) await _coordinator.ResumeAsync();
        else if (_coordinator.Status is PlaybackStatus.Ended or PlaybackStatus.Failed or PlaybackStatus.Idle) await _coordinator.ReplayAsync();
        else await _coordinator.PauseAsync();
    });

    public Task SeekRelativeAsync(int seconds) => RunAsync(async () =>
    {
        var coordinator = _coordinator;
        if (coordinator?.ActiveContext is not { CanSeek: true } context) return;
        await coordinator.SeekAsync(Math.Max(0, checked(context.PositionTicks + seconds * TimeSpan.TicksPerSecond)));
    });

    private async void ReplayClicked(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_coordinator is not null) await _coordinator.ReplayAsync();
    });

    private async void VolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || _coordinator is null) return;
        await RunAsync(() => _coordinator.SetVolumeAsync((int)args.NewValue, MuteButton.IsChecked == true));
    }

    private async void MuteClicked(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (_coordinator is not null) await _coordinator.SetVolumeAsync((int)Volume.Value, MuteButton.IsChecked == true);
    });

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
    private void FullscreenClicked(object sender, RoutedEventArgs args) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
    private void BackClicked(object sender, RoutedEventArgs args) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async Task AdvanceAsync()
    {
        var session = _session;
        var item = _item;
        var intent = _playIntent;
        var cancellationToken = _playRequest?.Token ?? CancellationToken.None;
        if (_advancing || session is null) return;
        _advancing = true;
        try
        {
            BaseItemDto? next = _queue.Count > 0 ? _queue.Dequeue() : null;
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
            if (intent != _playIntent || !ReferenceEquals(session, _session)) return;
            NextButton.IsEnabled = _queue.Count > 0;
            NextButton.Content = _queue.Count > 0 ? $"Next in queue ({_queue.Count})" : "Next in queue";
            if (next is not null) await PlayItemAsync(next);
        }
        catch (EmbyApiException ex) when (ex.IsAuthenticationFailure && ex.ApplicationErrorCode != "ParentalControl")
        { if (ReferenceEquals(session, _session)) ReportExpiredSession(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _session) && intent == _playIntent)
            { PlaybackNotice.Message = UiErrors.Describe(ex); PlaybackNotice.IsOpen = true; }
        }
        finally { _advancing = false; }
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
    }

    private void ShowPlaybackError(string? code)
    {
        if (code == "AuthenticationExpired") { ReportExpiredSession(); return; }
        PlaybackNotice.Severity = InfoBarSeverity.Error;
        PlaybackNotice.Message = code switch
        {
            "NoCompatibleSource" or "NoCompatibleTranscode" => "The server has no compatible stream for these playback settings.",
            "NegotiationRejected" => "The server did not allow this playback. Check your account permissions and quality settings.",
            "AccessRestricted" => "The server's access rules do not allow this playback.",
            "UnsupportedTrack" or "UnsupportedSubtitle" => "This track cannot be played with the current settings. Choose another track or turn subtitles off.",
            _ => "Playback could not start or was interrupted. Check your connection, or choose another version or quality."
        };
        PlaybackNotice.IsOpen = true;
        BufferingRing.IsActive = false;
    }

    private void ReportExpiredSession()
    {
        if (_expiredReported || _session is null) return;
        _expiredReported = true;
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        ++_playIntent;
        _playRequest?.Cancel();
        var coordinator = _coordinator;
        return RunAsync(async () => { if (coordinator is not null) await coordinator.StopAsync(); });
    }

    public async Task DisconnectAsync()
    {
        _clock.Stop();
        ++_playIntent;
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = null;
        var coordinator = _coordinator;
        var engine = _engine;
        _engine = null;
        if (engine is not null) engine.MediaCommandRequested -= SystemMediaCommandRequested;
        _coordinator = null;
        _session = null;
        if (coordinator is not null)
        {
            coordinator.StatusChanged -= CoordinatorStatusChanged;
            await coordinator.DisposeAsync();
        }
        _queue.Clear();
        _item = null;
        _displayedPlayback = null;
        NextButton.IsEnabled = false;
    }
}
