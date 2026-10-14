using System.Net.NetworkInformation;
using System.Net.Sockets;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private readonly SavedServerStore _savedServerStore = new();
    private readonly List<(Button Control, bool Persists)> _serverRowControls = [];
    private IReadOnlyList<SavedServer> _savedSignInServers = [];
    private IReadOnlyList<LanDiscoveredServer> _nearbySignInServers = [];
    private CancellationTokenSource? _serverDiscoveryRequest;
    private Button _discoverServers = null!;
    private Button _cancelServerDiscovery = null!;
    private ProgressRing _serverDiscoveryProgress = null!;
    private TextBlock _serverDiscoveryStatus = null!;
    private TextBlock _savedServerError = null!;
    private bool _savedServersReady;
    private bool _reportDiscoveryCancellation;

    private UIElement CreateServerDiscoveryControls()
    {
        RegisterServerDiscoveryText();
        var content = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _discoverServers = LumenUi.Button(LumenText.Get("Find nearby servers"), "search", height: 44);
        _discoverServers.Padding = new Thickness(16, 0, 16, 0);
        _discoverServers.IsEnabled = false;
        AutomationProperties.SetAutomationId(_discoverServers, "LumenDiscoverServers");
        _discoverServers.Click += async (_, _) => await DiscoverSignInServersAsync();
        actions.Children.Add(_discoverServers);
        _serverDiscoveryProgress = new ProgressRing
        {
            Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(_serverDiscoveryProgress);
        _cancelServerDiscovery = LumenUi.Button(LumenText.Get("Cancel"), height: 44);
        _cancelServerDiscovery.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_cancelServerDiscovery, LumenText.Get("Cancel nearby server discovery"));
        _cancelServerDiscovery.Click += (_, _) => CancelServerDiscovery(showCancelledStatus: true);
        actions.Children.Add(_cancelServerDiscovery);
        content.Children.Add(actions);
        _serverDiscoveryStatus = LumenUi.Text(LumenText.Get("Servers can be saved before signing in."), 12);
        _serverDiscoveryStatus.Foreground = LumenTheme.Brush("Muted");
        _serverDiscoveryStatus.LineHeight = 19;
        _serverDiscoveryStatus.MinHeight = 19;
        AutomationProperties.SetLiveSetting(_serverDiscoveryStatus, AutomationLiveSetting.Polite);
        content.Children.Add(_serverDiscoveryStatus);
        _savedServerError = SignInError();
        content.Children.Add(_savedServerError);
        return content;
    }

    private async Task LoadSavedSignInServersAsync()
    {
        try
        {
            var servers = await _savedServerStore.LoadAsync();
            if (_closing) return;
            _savedSignInServers = servers;
            _savedServersReady = true;
            _savedServerError.Visibility = Visibility.Collapsed;
        }
        catch (SavedServerStoreException exception)
        {
            if (_closing) return;
            _savedServersReady = false;
            _savedServerError.Text = LumenText.Get(exception.Message);
            _savedServerError.Visibility = Visibility.Visible;
        }
    }

    private async Task DiscoverSignInServersAsync()
    {
        if (!_settingsReady || _connectionRequest is not null || _serverDiscoveryRequest is not null || _transitioning || _closing) return;
        using var request = new CancellationTokenSource();
        _serverDiscoveryRequest = request;
        _reportDiscoveryCancellation = false;
        _serverDiscoveryStatus.Text = LumenText.Get("Searching nearby servers...");
        SetServerDiscoveryControlsEnabled(true);
        try
        {
            var servers = await _connections.DiscoverLanServersAsync(request.Token);
            if (_closing || _transitioning || request.IsCancellationRequested) return;
            _nearbySignInServers = servers;
            RefreshKnownSignInServers();
            _serverDiscoveryStatus.Text = servers.Count == 0
                ? LumenText.Get("No nearby servers found. Enter a server address below.")
                : LumenText.Get("{0} nearby servers found.", servers.Count);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!_closing && _session is null && _reportDiscoveryCancellation)
                _serverDiscoveryStatus.Text = LumenText.Get("Discovery cancelled.");
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            if (!_closing && !request.IsCancellationRequested)
                _serverDiscoveryStatus.Text = LumenText.Get("Nearby discovery is unavailable. You can enter a server address.");
        }
        finally
        {
            if (ReferenceEquals(_serverDiscoveryRequest, request)) _serverDiscoveryRequest = null;
            if (!_closing) SetServerDiscoveryControlsEnabled(_settingsReady && _connectionRequest is null && !_transitioning);
        }
    }

    private void CancelServerDiscovery(bool showCancelledStatus = false)
    {
        _reportDiscoveryCancellation = showCancelledStatus;
        _serverDiscoveryRequest?.Cancel();
        if (_cancelServerDiscovery is not null) _cancelServerDiscovery.IsEnabled = false;
    }

    private void RefreshSignInServerSources()
    {
        foreach (var server in _signInServers)
        {
            server.IsSaved = false;
            server.IsDiscovered = false;
            server.HasSavedAccount = false;
        }
        foreach (var saved in _savedSignInServers)
            FindOrAddSignInServer(saved.ApiRoot, saved.ServerId, saved.Name).IsSaved = true;
        foreach (var account in _connections.Settings.Accounts)
            FindOrAddSignInServer(account.ApiRoot, account.ServerId, account.ServerName).HasSavedAccount = true;
        foreach (var nearby in _nearbySignInServers)
            FindOrAddSignInServer(nearby.ApiRoot, nearby.ServerId, nearby.Name).IsDiscovered = true;
        _signInServers.RemoveAll(server => !server.IsSaved && !server.IsDiscovered && !server.HasSavedAccount && !server.SeenDuringSession);
    }

    private SignInServer FindOrAddSignInServer(string address, string serverId, string name)
    {
        var server = _signInServers.FirstOrDefault(candidate => SameSignInAddress(candidate.ApiRoot, address));
        if (server is not null) return server;
        var displayName = string.IsNullOrWhiteSpace(name) && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : name;
        server = new SignInServer(address, serverId, displayName);
        _signInServers.Add(server);
        return server;
    }

    private Button CreateSavedServerAction(SignInServer server)
    {
        var action = LumenUi.IconButton(server.IsSaved ? "minus" : "plus", LumenText.Get(server.IsSaved ? "Remove saved server" : "Save server"));
        action.VerticalAlignment = VerticalAlignment.Center;
        action.IsEnabled = _savedServersReady && !_connections.IsTemporarySession && _settingsReady
            && _connectionRequest is null && !_transitioning && !_closing;
        AutomationProperties.SetName(action, LumenText.Get(server.IsSaved ? "Remove {0} from saved servers" : "Save {0} on this PC", server.Name));
        ToolTipService.SetToolTip(action, LumenText.Get(server.IsSaved ? "Remove {0} from saved servers" : "Save {0} on this PC", server.Name));
        action.Click += async (_, _) => await ChangeSavedSignInServerAsync(server, remove: server.IsSaved);
        _serverRowControls.Add((action, true));
        return action;
    }

    private async Task ChangeSavedSignInServerAsync(SignInServer server, bool remove)
    {
        if (!_settingsReady || !_savedServersReady || _connections.IsTemporarySession || _connectionRequest is not null || _transitioning || _closing) return;
        if (!remove && server.Connection is null)
        {
            await ProbeSignInServerAsync(server.ApiRoot);
            if (_closing || _transitioning || server.Connection is null) return;
        }
        using var request = new CancellationTokenSource();
        _connectionRequest = request;
        SetConnecting(true, LumenText.Get(remove ? "Removing saved server..." : "Saving server..."));
        try
        {
            var servers = remove
                ? await _savedServerStore.RemoveAsync(server.ServerId, request.Token)
                : await _savedServerStore.UpsertAsync(SavedServer.Create(server.ApiRoot, server.ServerId, server.Name), request.Token);
            if (_closing) return;
            _savedSignInServers = servers;
            RefreshKnownSignInServers();
            _serverDiscoveryStatus.Text = LumenText.Get(remove ? "Removed saved server." : "Server saved on this PC.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (SavedServerStoreException exception)
        {
            if (!_closing)
            {
                _serverDiscoveryStatus.Text = LumenText.Get(exception.Message);
                if (exception.Error is SavedServerStoreError.ReadFailed or SavedServerStoreError.InvalidData)
                {
                    _savedServersReady = false;
                    _savedServerError.Text = LumenText.Get(exception.Message);
                    _savedServerError.Visibility = Visibility.Visible;
                }
            }
        }
        catch (ArgumentException)
        {
            if (!_closing) _serverDiscoveryStatus.Text = LumenText.Get("This server returned invalid identity or name data.");
        }
        finally
        {
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            SetConnecting(false);
        }
    }

    private void SetServerDiscoveryControlsEnabled(bool enabled)
    {
        if (_discoverServers is null) return;
        var discovering = _serverDiscoveryRequest is not null;
        _discoverServers.IsEnabled = enabled && !discovering;
        _serverDiscoveryProgress.IsActive = discovering;
        _serverDiscoveryProgress.Visibility = discovering ? Visibility.Visible : Visibility.Collapsed;
        _cancelServerDiscovery.Visibility = discovering ? Visibility.Visible : Visibility.Collapsed;
        _cancelServerDiscovery.IsEnabled = discovering && _serverDiscoveryRequest?.IsCancellationRequested != true;
        foreach (var (control, persists) in _serverRowControls)
            control.IsEnabled = enabled && (!persists || _savedServersReady && !_connections.IsTemporarySession);
    }

    private static string SignInServerOrigin(SignInServer server) => server.IsSaved && server.IsDiscovered
        ? LumenText.Get("Saved · Nearby")
        : LumenText.Get(server.IsSaved ? "Saved server" : server.IsDiscovered ? "Nearby server" : server.HasSavedAccount ? "Saved account" : "Connected server");

    private static void RegisterServerDiscoveryText() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Find nearby servers"] = "\u67e5\u627e\u5c40\u57df\u7f51\u670d\u52a1\u5668",
        ["Cancel nearby server discovery"] = "\u53d6\u6d88\u5c40\u57df\u7f51\u670d\u52a1\u5668\u641c\u7d22",
        ["Servers can be saved before signing in."] = "\u767b\u5f55\u524d\u5373\u53ef\u4fdd\u5b58\u670d\u52a1\u5668\u3002",
        ["Searching nearby servers..."] = "\u6b63\u5728\u641c\u7d22\u5c40\u57df\u7f51\u670d\u52a1\u5668\u2026",
        ["No nearby servers found. Enter a server address below."] = "\u672a\u627e\u5230\u5c40\u57df\u7f51\u670d\u52a1\u5668\uff0c\u8bf7\u5728\u4e0b\u65b9\u8f93\u5165\u5730\u5740\u3002",
        ["{0} nearby servers found."] = "\u627e\u5230 {0} \u53f0\u5c40\u57df\u7f51\u670d\u52a1\u5668\u3002",
        ["Discovery cancelled."] = "\u5df2\u53d6\u6d88\u670d\u52a1\u5668\u641c\u7d22\u3002",
        ["Nearby discovery is unavailable. You can enter a server address."] = "\u65e0\u6cd5\u641c\u7d22\u5c40\u57df\u7f51\u670d\u52a1\u5668\uff0c\u53ef\u624b\u52a8\u8f93\u5165\u5730\u5740\u3002",
        ["Save server"] = "\u4fdd\u5b58\u670d\u52a1\u5668",
        ["Remove saved server"] = "\u79fb\u9664",
        ["Save {0} on this PC"] = "\u5728\u672c\u673a\u4fdd\u5b58 {0}",
        ["Remove {0} from saved servers"] = "\u4ece\u5df2\u4fdd\u5b58\u7684\u670d\u52a1\u5668\u4e2d\u79fb\u9664 {0}",
        ["Saving server..."] = "\u6b63\u5728\u4fdd\u5b58\u670d\u52a1\u5668\u2026",
        ["Removing saved server..."] = "\u6b63\u5728\u79fb\u9664\u5df2\u4fdd\u5b58\u7684\u670d\u52a1\u5668\u2026",
        ["Server saved on this PC."] = "\u670d\u52a1\u5668\u5df2\u4fdd\u5b58\u5728\u672c\u673a\u3002",
        ["Removed saved server."] = "\u5df2\u79fb\u9664\u4fdd\u5b58\u7684\u670d\u52a1\u5668\u3002",
        ["Saved · Nearby"] = "\u5df2\u4fdd\u5b58 \u00b7 \u5c40\u57df\u7f51",
        ["Saved server"] = "\u5df2\u4fdd\u5b58",
        ["Nearby server"] = "\u5c40\u57df\u7f51",
        ["Saved account"] = "\u5df2\u4fdd\u5b58\u8d26\u6237",
        ["Connected server"] = "\u5df2\u8fde\u63a5",
        ["Saved servers could not be read. Check access to the application data folder."] = "\u65e0\u6cd5\u8bfb\u53d6\u5df2\u4fdd\u5b58\u7684\u670d\u52a1\u5668\uff0c\u8bf7\u68c0\u67e5\u5e94\u7528\u6570\u636e\u76ee\u5f55\u7684\u8bbf\u95ee\u6743\u9650\u3002",
        ["Saved servers are damaged or incompatible. The saved file has been retained."] = "\u670d\u52a1\u5668\u5217\u8868\u5df2\u635f\u574f\u6216\u4e0d\u517c\u5bb9\uff0c\u5df2\u4fdd\u7559\u539f\u6587\u4ef6\u3002",
        ["Saved servers could not be written. The existing list has been retained."] = "\u65e0\u6cd5\u4fdd\u5b58\u670d\u52a1\u5668\uff0c\u539f\u6709\u5217\u8868\u5df2\u4fdd\u7559\u3002",
        ["The saved server list is full. Remove a server before adding another."] = "\u5df2\u4fdd\u5b58\u7684\u670d\u52a1\u5668\u6570\u91cf\u5df2\u8fbe\u4e0a\u9650\uff0c\u8bf7\u5148\u79fb\u9664\u670d\u52a1\u5668\u3002",
        ["The saved server list exceeds the application size limit."] = "\u670d\u52a1\u5668\u5217\u8868\u8d85\u8fc7\u5e94\u7528\u5927\u5c0f\u9650\u5236\u3002",
        ["This server returned invalid identity or name data."] = "\u670d\u52a1\u5668\u8fd4\u56de\u7684\u8eab\u4efd\u6216\u540d\u79f0\u6570\u636e\u65e0\u6548\u3002",
        ["This address belongs to a different server. Select or add the server again before signing in."] = "\u6b64\u5730\u5740\u5bf9\u5e94\u53e6\u4e00\u53f0\u670d\u52a1\u5668\uff0c\u8bf7\u91cd\u65b0\u9009\u62e9\u6216\u6dfb\u52a0\u540e\u767b\u5f55\u3002",
    });
}
