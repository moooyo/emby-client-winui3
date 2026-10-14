using EmbyClient.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace EmbyClient.App;

public partial class App : Application
{
    public static Window? CurrentWindow { get; private set; }
    private MainWindow? _window;
    private WindowPlacementStore? _windowPlacementStore;
    private readonly WindowsActivationService _activationService = new();
    private readonly DispatcherQueue _activationDispatcher;
    private WindowsActivationRoute? _pendingWindowsRoute;
    private IReadOnlyDictionary<string, string>? _pendingEpisodeNotification;
    private bool _launchStarted;
    private long _activationVersion;

    public App()
    {
        InitializeComponent();
        _activationDispatcher = DispatcherQueue.GetForCurrentThread();
        _activationService.ActivationRequested += WindowsActivationRequested;
        _activationService.EpisodeNotificationActivationRequested += EpisodeNotificationActivationRequested;
        UnhandledException += (_, args) => RecordAcceptanceFailure(args.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_launchStarted) return;
        _launchStarted = true;
        try
        {
            if (!WindowsJumpListService.TryInitializeProcessIdentity(out var identityError))
                System.Diagnostics.Trace.TraceWarning("Windows shell identity is unavailable (0x{0:X8}).", identityError ?? 0);
            var registrationVersion = _activationVersion;
            var registration = await _activationService.RegisterAsync();
            if (registration.Redirected) { Exit(); return; }
            if (registrationVersion == _activationVersion)
            {
                _pendingWindowsRoute = registration.Route;
                _pendingEpisodeNotification = registration.NotificationArguments;
            }
            var preferences = new LumenPreferences();
            try { preferences = await new LumenPreferenceStore().LoadAsync(); }
            catch (Exception exception)
            {
                // The shell presents preference-storage errors when it initializes; startup stays asynchronous.
                System.Diagnostics.Trace.TraceWarning("Window preferences load failed (0x{0:X8}).", exception.HResult);
            }
            _windowPlacementStore = new WindowPlacementStore(enabled: preferences.RememberWindowPlacement);
            WindowManager.PersistenceStorage = _windowPlacementStore;
            _window = new MainWindow();
            CurrentWindow = _window;
            _window.Closed += (_, _) =>
            {
                _activationService.ActivationRequested -= WindowsActivationRequested;
                _activationService.EpisodeNotificationActivationRequested -= EpisodeNotificationActivationRequested;
                _activationService.Dispose();
                CurrentWindow = null;
                _window = null;
            };
            _window.Activate();
            if (_pendingEpisodeNotification is { } notification) _window.ActivateEpisodeNotification(notification);
            else if (_pendingWindowsRoute is { } route) _window.ActivateWindowsRoute(route);
            _pendingWindowsRoute = null;
            _pendingEpisodeNotification = null;
        }
        catch (Exception exception)
        {
            RecordAcceptanceFailure(exception);
            throw;
        }
    }

    internal static void ApplyWindowPlacementPreference(bool enabled)
    {
        if (Current is App app) app._windowPlacementStore?.SetEnabled(enabled);
    }

    private void WindowsActivationRequested(object? sender, WindowsActivationRoute? route)
    {
        DispatchActivation(() =>
        {
            _activationVersion++;
            if (_window is { } window) window.ActivateWindowsRoute(route);
            else { _pendingWindowsRoute = route; _pendingEpisodeNotification = null; }
        });
    }

    private void EpisodeNotificationActivationRequested(object? sender, IReadOnlyDictionary<string, string> arguments)
    {
        DispatchActivation(() =>
        {
            _activationVersion++;
            if (_window is { } window) window.ActivateEpisodeNotification(arguments);
            else { _pendingEpisodeNotification = arguments; _pendingWindowsRoute = null; }
        });
    }

    private void DispatchActivation(Action action)
    {
        if (_activationDispatcher.HasThreadAccess) action();
        else _activationDispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception exception) { RecordAcceptanceFailure(exception); }
        });
    }

    private static void RecordAcceptanceFailure(Exception exception)
    {
        var root = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            // Opt-in acceptance profiles retain code locations, never exception payloads or credentials.
            File.AppendAllText(Path.Combine(root, "application-failures.log"),
                $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name} 0x{exception.HResult:X8}\n{exception.StackTrace}\n");
        }
        catch (Exception) { /* Diagnostics must not change application failure handling. */ }
    }
}
