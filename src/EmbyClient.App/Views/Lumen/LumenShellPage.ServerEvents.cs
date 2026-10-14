using System.ComponentModel;
using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private readonly LibraryEventAccumulator _libraryEvents = new();
    private readonly EpisodeNotificationService _episodeNotifications = new();
    private readonly DispatcherTimer _libraryEventTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _episodeRetryTimer = new();
    private readonly Border _offlineBanner = new()
    {
        CornerRadius = new(16), Padding = new(18, 12, 12, 12), Margin = new(24, 96, 24, 0),
        MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _offlineText = LumenUi.Text(string.Empty, 13);
    private ServerEventMonitor? _serverEvents;
    private ServerAvailabilityMonitor? _serverAvailability;
    private EpisodeNotificationTarget? _pendingEpisodeNotification;
    private bool _refreshingLibraryEvents;
    private bool _loadingEpisodeNotifications;
    private bool _eventSocketWasDisconnected;
    private bool _eventSocketEverConnected;
    private bool _serverRefreshAwaitingHealth;
    private bool _episodeRetryAwaitingHealth;
    private int _serverRefreshFailureCount;
    private int _episodeFailureCount;
    private long _episodeActivationVersion;
    private CancellationTokenSource? _episodeActivationRequest;
    private bool _noticeAfterServerRefresh;

    private bool CanReceiveEpisodeNotifications => !_closing && _session is not null
        && _preferences.NewEpisodeNotifications && !_fullscreen
        && _serverAvailability?.Status == ServerAvailabilityStatus.Online
        && _serverEvents?.Status == ServerEventConnectionStatus.Connected;

    private long UpdateEpisodeNotificationContext()
    {
        var session = _session;
        var revision = _episodeNotifications.UpdateContext(session?.Server.Id, session?.User.Id,
            !_closing && _serverAvailability?.Status == ServerAvailabilityStatus.Online
                && _serverEvents?.Status == ServerEventConnectionStatus.Connected,
            _fullscreen, _preferences.NewEpisodeNotifications);
        if (!CanReceiveEpisodeNotifications)
        {
            _libraryEvents.TakeAdditions();
            _episodeRetryTimer.Stop();
            _episodeRetryAwaitingHealth = false;
            _episodeFailureCount = 0;
        }
        return revision;
    }

    private bool OwnsEpisodeDelivery(ConnectedSession session, int epoch, long revision) =>
        epoch == _sessionEpoch && ReferenceEquals(session, _session) && CanReceiveEpisodeNotifications
        && _episodeNotifications.DeliveryRevision == revision;

    private void InitializeServerIntegration()
    {
        LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Server disconnected. Previously loaded content is shown. Reconnecting..."] = "\u670d\u52a1\u5668\u5df2\u65ad\u5f00\uff0c\u5f53\u524d\u663e\u793a\u5df2\u52a0\u8f7d\u7684\u5185\u5bb9\u3002\u6b63\u5728\u91cd\u65b0\u8fde\u63a5...",
            ["Server disconnected. Previously loaded content is shown. Choose Retry to reconnect."] = "\u670d\u52a1\u5668\u5df2\u65ad\u5f00\uff0c\u5f53\u524d\u663e\u793a\u5df2\u52a0\u8f7d\u7684\u5185\u5bb9\u3002\u70b9\u51fb\u91cd\u8bd5\u53ef\u91cd\u65b0\u8fde\u63a5\u3002",
            ["Server disconnected. Reconnecting..."] = "\u670d\u52a1\u5668\u5df2\u65ad\u5f00\uff0c\u6b63\u5728\u91cd\u65b0\u8fde\u63a5...",
            ["Server disconnected. Choose Retry to reconnect."] = "\u670d\u52a1\u5668\u5df2\u65ad\u5f00\uff0c\u70b9\u51fb\u91cd\u8bd5\u53ef\u91cd\u65b0\u8fde\u63a5\u3002",
            ["The server response could not be confirmed. Previously loaded content may be out of date. Choose Retry."] = "\u65e0\u6cd5\u786e\u8ba4\u670d\u52a1\u5668\u54cd\u5e94\uff0c\u5df2\u52a0\u8f7d\u7684\u5185\u5bb9\u53ef\u80fd\u5df2\u8fc7\u671f\u3002\u8bf7\u91cd\u8bd5\u3002",
            ["Connection restored."] = "\u8fde\u63a5\u5df2\u6062\u590d\u3002",
            ["Reconnected to \"{0}\". Content has been refreshed."] = "\u5df2\u91cd\u65b0\u8fde\u63a5\u300c{0}\u300d\uff0c\u5185\u5bb9\u5df2\u5237\u65b0",
            ["Disconnected from \"{0}\". Previously loaded content is shown."] = "\u4e0e\u300c{0}\u300d\u7684\u8fde\u63a5\u5df2\u65ad\u5f00\uff0c\u6b63\u5728\u663e\u793a\u7f13\u5b58\u5185\u5bb9",
            ["This account is currently restricted by the server. Choose another account or check the server's access rules."] = "\u5f53\u524d\u8d26\u6237\u53d7\u670d\u52a1\u5668\u8bbf\u95ee\u89c4\u5219\u9650\u5236\uff0c\u8bf7\u5207\u6362\u8d26\u6237\u6216\u68c0\u67e5\u670d\u52a1\u5668\u7684\u8bbf\u95ee\u8bbe\u7f6e\u3002",
            ["This address now belongs to another server. Confirm the server before signing in again."] = "\u6b64\u5730\u5740\u73b0\u5728\u5bf9\u5e94\u53e6\u4e00\u53f0\u670d\u52a1\u5668\uff0c\u8bf7\u786e\u8ba4\u670d\u52a1\u5668\u540e\u91cd\u65b0\u767b\u5f55\u3002",
            ["New episode available"] = "\u6709\u65b0\u5267\u96c6\u53ef\u89c2\u770b",
            ["New episodes: {0}"] = "\u65b0\u5267\u96c6\uff1a{0}",
            ["This notification belongs to another server or account. Choose that account to open it."] = "\u6b64\u901a\u77e5\u5c5e\u4e8e\u53e6\u4e00\u53f0\u670d\u52a1\u5668\u6216\u8d26\u6237\uff0c\u8bf7\u5207\u6362\u5230\u5bf9\u5e94\u8d26\u6237\u540e\u6253\u5f00\u3002"
        });
        _offlineBanner.Background = LumenTheme.Brush("Pop");
        _offlineBanner.BorderBrush = LumenTheme.Brush("LineStrong");
        _offlineBanner.BorderThickness = new(1);
        _offlineText.TextWrapping = TextWrapping.Wrap;
        _offlineText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_offlineText, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_offlineBanner, "ServerConnectionBanner");
        var content = new Grid { ColumnSpacing = 14 };
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(_offlineText);
        var retry = LumenUi.Button("Retry", "refresh-cw", height: 32);
        retry.Click += (_, _) =>
        {
            _serverAvailability?.RequestProbe();
            _serverEvents?.Retry();
        };
        Grid.SetColumn(retry, 1);
        content.Children.Add(retry);
        _offlineBanner.Child = content;
        _root.Children.Add(_offlineBanner);
        _libraryEventTimer.Tick += async (_, _) => await RefreshServerChangesAsync();
        _episodeRetryTimer.Tick += async (_, _) =>
        {
            _episodeRetryTimer.Stop();
            if (_session is { } session) await NotifyAddedEpisodesAsync(session, _sessionEpoch);
        };
        _library.ViewModel.PropertyChanged += LibraryConnectionStateChanged;
        _library.ViewModel.Images.UsageChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) _settings.SetImageCacheUsage(_library.ViewModel.Images.Usage);
        });
    }

    private async Task ActivateServerIntegrationAsync(ConnectedSession session)
    {
        await StopServerIntegrationAsync();
        if (!ReferenceEquals(session, _session) || _closing || _sessionLifetime is null) return;
        var epoch = _sessionEpoch;
        bool OwnsSession() => !_closing && epoch == _sessionEpoch && ReferenceEquals(session, _session);
        var availability = new ServerAvailabilityMonitor(async token =>
        {
            var server = await session.Api.GetPublicSystemInfoAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(server.Id)) return ServerAvailabilityStatus.Incompatible;
            if (!string.Equals(server.Id, session.Server.Id, StringComparison.Ordinal)) return ServerAvailabilityStatus.ServerChanged;
            var user = await session.Api.GetCurrentUserAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user.Id)) return ServerAvailabilityStatus.Incompatible;
            if (!string.Equals(user.Id, session.User.Id, StringComparison.Ordinal)) return ServerAvailabilityStatus.ServerChanged;
            return user.Policy?.IsDisabled == true ? ServerAvailabilityStatus.AccountRestricted : ServerAvailabilityStatus.Online;
        }) { AutoReconnect = _preferences.AutoReconnect };
        var events = new ServerEventMonitor(session.Api) { AutoReconnect = _preferences.AutoReconnect };
        _serverAvailability = availability;
        _serverEvents = events;
        availability.StatusChanged += (_, args) =>
        {
            if (args.Status != ServerAvailabilityStatus.Online && OwnsSession()
                && ReferenceEquals(availability, _serverAvailability))
                _episodeNotifications.SuspendContext(session.Server.Id, session.User.Id);
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (OwnsSession() && ReferenceEquals(availability, _serverAvailability))
                    await ApplyServerAvailabilityAsync(session, args);
            });
        };
        availability.OnlineConfirmed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!OwnsSession() || !ReferenceEquals(availability, _serverAvailability)) return;
            UpdateEpisodeNotificationContext();
            if (_serverRefreshAwaitingHealth)
            {
                _serverRefreshAwaitingHealth = false;
                ScheduleServerRefresh();
            }
            if (_episodeRetryAwaitingHealth && CanReceiveEpisodeNotifications && _libraryEvents.HasPendingAdditions)
            {
                _episodeRetryAwaitingHealth = false;
                _episodeRetryTimer.Interval = RetryDelay(_episodeFailureCount);
                _episodeRetryTimer.Start();
            }
        });
        events.ConnectionStatusChanged += (_, args) =>
        {
            if (args.Status != ServerEventConnectionStatus.Connected && OwnsSession()
                && ReferenceEquals(events, _serverEvents))
                _episodeNotifications.SuspendContext(session.Server.Id, session.User.Id);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!OwnsSession() || !ReferenceEquals(events, _serverEvents)) return;
                UpdateEpisodeNotificationContext();
                if (args.Status == ServerEventConnectionStatus.Connected)
                {
                    _eventSocketEverConnected = true;
                    if (_eventSocketWasDisconnected)
                    {
                        _eventSocketWasDisconnected = false;
                        _noticeAfterServerRefresh = true;
                        _libraryEvents.RequestRefresh();
                        _library.ViewModel.InvalidateHomeSnapshots();
                        ScheduleServerRefresh();
                    }
                }
                else
                {
                    // An unavailable WebSocket route does not establish that authenticated HTTP is offline.
                    _eventSocketWasDisconnected |= _eventSocketEverConnected;
                    _libraryEvents.TakeAdditions();
                    _episodeRetryTimer.Stop();
                    _episodeRetryAwaitingHealth = false;
                    if (args.Status != ServerEventConnectionStatus.Connecting) availability.RequestProbe();
                }
            });
        };
        events.LibraryChanged += (_, args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!OwnsSession() || !ReferenceEquals(events, _serverEvents)) return;
            UpdateEpisodeNotificationContext();
            _libraryEvents.Add(CanReceiveEpisodeNotifications ? args.ItemsAdded : [], hasChanges: true);
            _library.ViewModel.InvalidateHomeSnapshots();
            if (CanReceiveEpisodeNotifications) _ = NotifyAddedEpisodesAsync(session, epoch);
            ScheduleServerRefresh();
        });
        availability.Start(_sessionLifetime.Token);
        events.Start(_sessionLifetime.Token);
        await OpenPendingEpisodeNotificationAsync();
    }

    private void LibraryConnectionStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LibraryViewModel.HasError) && _library.ViewModel.HasError)
            _serverAvailability?.RequestProbe();
        if (args.PropertyName == nameof(LibraryViewModel.IsBusy) && !_library.ViewModel.IsBusy)
            ScheduleServerRefresh();
    }

    private async Task ApplyServerAvailabilityAsync(ConnectedSession session, ServerAvailabilityChangedEventArgs args)
    {
        UpdateEpisodeNotificationContext();
        switch (args.Status)
        {
            case ServerAvailabilityStatus.Online:
                _offlineBanner.Visibility = Visibility.Collapsed;
                if (args.WasOffline)
                {
                    _noticeAfterServerRefresh = true;
                    _libraryEvents.RequestRefresh();
                    _library.ViewModel.InvalidateHomeSnapshots();
                    _serverEvents?.Retry();
                    ScheduleServerRefresh();
                }
                break;
            case ServerAvailabilityStatus.Offline:
            case ServerAvailabilityStatus.Incompatible:
                UpdateConnectionBanner();
                break;
            case ServerAvailabilityStatus.AuthenticationRequired:
                await LeaveSessionAsync(false, expired: true);
                break;
            case ServerAvailabilityStatus.AccountRestricted:
                await LeaveSessionAsync(false);
                if (!_closing) ShowNotice(LumenText.Get("This account is currently restricted by the server. Choose another account or check the server's access rules."), InfoBarSeverity.Warning);
                break;
            case ServerAvailabilityStatus.ServerChanged:
                await LeaveSessionAsync(false);
                if (!_closing) ShowNotice(LumenText.Get("This address now belongs to another server. Confirm the server before signing in again."), InfoBarSeverity.Warning);
                break;
        }
    }

    private void ApplyServerIntegrationPreferences()
    {
        if (_serverAvailability is { } availability) availability.AutoReconnect = _preferences.AutoReconnect;
        if (_serverEvents is { } events) events.AutoReconnect = _preferences.AutoReconnect;
        _library.ViewModel.Images.MaximumBytes = (long)_preferences.ImageCacheMegabytes * 1024 * 1024;
        _settings.SetImageCacheUsage(_library.ViewModel.Images.Usage);
        UpdateEpisodeNotificationContext();
        if (!_preferences.NewEpisodeNotifications)
        {
            _episodeRetryTimer.Stop();
            _episodeRetryAwaitingHealth = false;
            _libraryEvents.TakeAdditions();
            _ = _episodeNotifications.ClearAsync();
        }
        UpdateConnectionBanner();
    }

    private void UpdateConnectionBanner()
    {
        var status = _serverAvailability?.Status;
        var show = _session is not null && !_closing && status is ServerAvailabilityStatus.Offline or ServerAvailabilityStatus.Incompatible;
        _offlineBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var cached = _library.ViewModel.HasItems || _library.ViewModel.HasDetails || _library.ViewModel.HomeRows.Any(row => row.Items.Count > 0);
        _offlineText.Text = status == ServerAvailabilityStatus.Incompatible
            ? LumenText.Get("The server response could not be confirmed. Previously loaded content may be out of date. Choose Retry.")
            : cached ? LumenText.Get("Disconnected from \"{0}\". Previously loaded content is shown.", _session?.Server.ServerName ?? LumenText.Get("Server"))
            : LumenText.Get(_preferences.AutoReconnect ? "Server disconnected. Reconnecting..."
                : "Server disconnected. Choose Retry to reconnect.");
        _offlineBanner.Margin = new(24, IsPlayerVisible ? 84 : 96, 24, 0);
    }

    private void ScheduleServerRefresh()
    {
        if (_closing || _session is null || !_libraryEvents.NeedsRefresh || _refreshingLibraryEvents || _serverRefreshAwaitingHealth
            || _episodeActivationRequest is not null || _processingWindowsActivation
            || IsPlayerVisible || _settingsVisible || _transitioning || _returning
            || _serverAvailability?.Status is ServerAvailabilityStatus.Offline or ServerAvailabilityStatus.Incompatible) return;
        if (!_libraryEventTimer.IsEnabled) _libraryEventTimer.Start();
    }

    private async Task RefreshServerChangesAsync()
    {
        _libraryEventTimer.Stop();
        var session = _session;
        var epoch = _sessionEpoch;
        if (session is null || _closing || _transitioning || _returning || IsPlayerVisible || _settingsVisible
            || _refreshingLibraryEvents || !_libraryEvents.NeedsRefresh || _episodeActivationRequest is not null || _processingWindowsActivation
            || _serverAvailability?.Status is ServerAvailabilityStatus.Offline or ServerAvailabilityStatus.Incompatible) return;
        if (_library.ViewModel.IsBusy || _library.ViewModel.IsMutating || _library.ViewModel.HasPendingSearch)
        {
            _libraryEventTimer.Start();
            return;
        }
        _refreshingLibraryEvents = true;
        var revision = _libraryEvents.Revision;
        try
        {
            var token = _sessionLifetime?.Token ?? CancellationToken.None;
            var currentSucceeded = true;
            var navigationRevision = _library.ViewModel.NavigationRevision;
            if (!_library.ViewModel.IsHome)
            {
                var currentRefresh = _library.RefreshAsync(token);
                // Refresh advances the page revision before its first await; capture the revision it owns.
                navigationRevision = _library.ViewModel.NavigationRevision;
                await currentRefresh;
                if (epoch != _sessionEpoch || !ReferenceEquals(session, _session) || _closing) return;
                if (token.IsCancellationRequested || navigationRevision != _library.ViewModel.NavigationRevision) return;
                currentSucceeded = !_library.ViewModel.HasError && !_library.ViewModel.IsBusy
                    && !_library.ViewModel.HasPendingSearch;
            }
            var homeSucceeded = await _library.RefreshHomeShelvesAsync(token);
            if (epoch != _sessionEpoch || !ReferenceEquals(session, _session) || _closing) return;
            // A restored cached page needs its own refresh even when the Home read completed successfully.
            if (token.IsCancellationRequested || navigationRevision != _library.ViewModel.NavigationRevision) return;
            if (!currentSucceeded || !homeSucceeded)
            {
                WaitToRetryServerRefresh();
                return;
            }
            _libraryEvents.MarkRefreshed(revision);
            await RefreshWindowsResumeItemsAsync();
            if (epoch != _sessionEpoch || !ReferenceEquals(session, _session) || _closing) return;
            if (_noticeAfterServerRefresh && !_libraryEvents.NeedsRefresh)
            {
                _noticeAfterServerRefresh = false;
                ShowNotice(LumenText.Get("Reconnected to \"{0}\". Content has been refreshed.", session.Server.ServerName ?? LumenText.Get("Server")));
            }
            _serverRefreshFailureCount = 0;
            _libraryEventTimer.Interval = TimeSpan.FromMilliseconds(750);
            await NotifyAddedEpisodesAsync(session, epoch);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (epoch == _sessionEpoch && !_closing)
            {
                WaitToRetryServerRefresh();
                ShowError(exception);
            }
        }
        finally
        {
            if (epoch == _sessionEpoch)
            {
                _refreshingLibraryEvents = false;
                ScheduleServerRefresh();
                UpdateChrome();
            }
        }
    }

    private async Task NotifyAddedEpisodesAsync(ConnectedSession session, int epoch)
    {
        var deliveryRevision = UpdateEpisodeNotificationContext();
        if (!CanReceiveEpisodeNotifications || _loadingEpisodeNotifications || _sessionLifetime is null
            || _episodeRetryAwaitingHealth || _episodeRetryTimer.IsEnabled) return;
        _loadingEpisodeNotifications = true;
        var token = _sessionLifetime.Token;
        var ids = _libraryEvents.TakeAdditions();
        var index = 0;
        var failed = false;
        var followed = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            for (; index < ids.Length; index++)
            {
                var id = ids[index];
                if (!OwnsEpisodeDelivery(session, epoch, deliveryRevision)) return;
                BaseItemDto item;
                try { item = await session.Api.GetItemAsync(id, token); }
                catch (EmbyApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                { continue; }
                if (!OwnsEpisodeDelivery(session, epoch, deliveryRevision)) return;
                if (item.Type != "Episode" || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.SeriesId)) continue;
                if (!followed.TryGetValue(item.SeriesId, out var isFollowed))
                {
                    try { isFollowed = await EpisodeNotificationEligibility.IsFollowedAsync(session.Api, item, token); }
                    catch (EmbyApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                    { isFollowed = false; }
                    followed[item.SeriesId] = isFollowed;
                }
                if (!isFollowed || !OwnsEpisodeDelivery(session, epoch, deliveryRevision)) continue;
                var episode = new MediaCardViewModel(item);
                var target = new EpisodeNotificationTarget(session.Server.Id!, session.User.Id!, item.Id);
                var description = episode.SceneIdentity;
                byte[]? artwork = null;
                try
                {
                    artwork = await _library.ViewModel.Images.GetAsync(session.Api, session.Server.Id!, session.User.Id!,
                        item, 720, 360, ArtworkKind.Landscape, token);
                }
                catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException)
                { /* The same online episode remains useful when its artwork is unavailable. */ }
                UpdateEpisodeNotificationContext();
                if (!OwnsEpisodeDelivery(session, epoch, deliveryRevision)
                    || !_episodeNotifications.CanDeliver(target, deliveryRevision) || !_libraryEvents.MarkNotified(item.Id)) continue;
                var shown = await _episodeNotifications.TryShowAsync(target,
                    EpisodeNotificationContent.Create(item, session.Server.ServerName), artwork, deliveryRevision, token);
                if (!shown && OwnsEpisodeDelivery(session, epoch, deliveryRevision)
                    && _episodeNotifications.CanDeliver(target, deliveryRevision))
                    ShowNotice(LumenText.Get("New episodes: {0}", description));
            }
            _episodeFailureCount = 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException)
        {
            failed = true;
            if (OwnsEpisodeDelivery(session, epoch, deliveryRevision))
            {
                _libraryEvents.Add(ids.Skip(index), hasChanges: false);
                _episodeRetryAwaitingHealth = true;
                _episodeFailureCount = Math.Min(_episodeFailureCount + 1, 5);
                _serverAvailability?.RequestProbe();
            }
        }
        finally
        {
            if (epoch == _sessionEpoch)
            {
                _loadingEpisodeNotifications = false;
                if (!failed && _libraryEvents.HasPendingAdditions && CanReceiveEpisodeNotifications)
                    _ = NotifyAddedEpisodesAsync(session, epoch);
            }
        }
    }

    private static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Clamp(failures, 1, 5)));

    private void WaitToRetryServerRefresh()
    {
        _serverRefreshAwaitingHealth = true;
        _serverRefreshFailureCount = Math.Min(_serverRefreshFailureCount + 1, 5);
        _libraryEventTimer.Interval = RetryDelay(_serverRefreshFailureCount);
        _serverAvailability?.RequestProbe();
    }

    public void ActivateEpisodeNotification(IReadOnlyDictionary<string, string> arguments)
    {
        if (_closing || !EpisodeNotificationTarget.TryParse(arguments, out var target)
            || target!.Intent == EpisodeNotificationIntent.RemindLater) return;
        QueueEpisodeNotification(target!);
    }

    private void QueueEpisodeNotification(EpisodeNotificationTarget target)
    {
        CancelEpisodeNotificationActivation(clearPending: true);
        _pendingWindowsActivation = null;
        _windowsActivationIntent++;
        _windowsActivationRequest?.Cancel();
        if (_processingWindowsActivation) _resumeDialog?.Hide();
        _pendingEpisodeNotification = target;
        App.CurrentWindow?.Activate();
        _ = OpenPendingEpisodeNotificationAsync();
    }

    private void CancelEpisodeNotificationActivation(bool clearPending)
    {
        _episodeActivationVersion++;
        _episodeActivationRequest?.Cancel();
        if (clearPending) _pendingEpisodeNotification = null;
    }

    private async Task OpenPendingEpisodeNotificationAsync()
    {
        var target = _pendingEpisodeNotification;
        var session = _session;
        var epoch = _sessionEpoch;
        if (target is null || session is null || _closing || _transitioning || _returning || _startingPlayback || _shellDialog is not null) return;
        if (!target.Matches(session.Server.Id, session.User.Id))
        {
            ShowNotice(LumenText.Get("This notification belongs to another server or account. Choose that account to open it."), InfoBarSeverity.Warning);
            return;
        }
        _pendingEpisodeNotification = null;
        var version = _episodeActivationVersion;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime?.Token ?? CancellationToken.None);
        _episodeActivationRequest = request;
        bool OwnsActivation() => !request.IsCancellationRequested && version == _episodeActivationVersion
            && epoch == _sessionEpoch && ReferenceEquals(session, _session) && !_closing && !_transitioning;
        try
        {
            if (IsPlayerVisible) await ReturnToLibraryAsync();
            if (!OwnsActivation()) return;
            await NavigateAsync("home");
            if (!OwnsActivation()) return;
            var navigation = _library.ViewModel.NavigationRevision;
            var item = await session.Api.GetItemAsync(target.ItemId, request.Token);
            if (!OwnsActivation() || navigation != _library.ViewModel.NavigationRevision
                || IsPlayerVisible || _settingsVisible || _startingPlayback) return;
            if (item.Id != target.ItemId || item.Type != "Episode")
                throw new EmbyProtocolException("The requested episode is no longer available.");
            if (target.Intent == EpisodeNotificationIntent.PlayNow)
            {
                if (session.User.Policy?.EnableMediaPlayback == false)
                {
                    ShowNotice(LumenText.Get("Playback is disabled for this account."), InfoBarSeverity.Warning);
                    return;
                }
                await StartLibraryPlaybackAsync(new LumenPlayRequestEventArgs(item, 0));
                return;
            }
            var detailItem = string.IsNullOrWhiteSpace(item.SeriesId) ? item
                : await session.Api.GetItemAsync(item.SeriesId, request.Token);
            if (!OwnsActivation() || navigation != _library.ViewModel.NavigationRevision) return;
            await _library.ViewModel.ShowItemAsync(new MediaCardViewModel(detailItem), request.Token);
            if (!OwnsActivation()) return;
            if (_library.ViewModel.HasDetails && _library.ViewModel.Detail.Id == detailItem.Id) _library.FocusCurrentDetails();
            UpdateChrome();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (OwnsActivation()) ShowError(exception); }
        finally
        {
            if (ReferenceEquals(_episodeActivationRequest, request))
            {
                _episodeActivationRequest = null;
                ScheduleServerRefresh();
            }
        }
    }

    private async Task StopServerIntegrationAsync()
    {
        _libraryEventTimer.Stop();
        _episodeRetryTimer.Stop();
        CancelEpisodeNotificationActivation(clearPending: false);
        var availability = _serverAvailability;
        var events = _serverEvents;
        _serverAvailability = null;
        _serverEvents = null;
        UpdateEpisodeNotificationContext();
        _offlineBanner.Visibility = Visibility.Collapsed;
        _libraryEvents.Clear();
        _refreshingLibraryEvents = false;
        _loadingEpisodeNotifications = false;
        _eventSocketWasDisconnected = false;
        _eventSocketEverConnected = false;
        _noticeAfterServerRefresh = false;
        _serverRefreshAwaitingHealth = _episodeRetryAwaitingHealth = false;
        _serverRefreshFailureCount = _episodeFailureCount = 0;
        _libraryEventTimer.Interval = TimeSpan.FromMilliseconds(750);
        if (availability is not null) await availability.DisposeAsync();
        if (events is not null) await events.DisposeAsync();
        await _episodeNotifications.ClearAsync();
    }

    private async Task ShutdownServerIntegrationAsync()
    {
        _library.ViewModel.PropertyChanged -= LibraryConnectionStateChanged;
        _pendingEpisodeNotification = null;
        await StopServerIntegrationAsync();
        await _episodeNotifications.DisposeAsync();
    }
}
