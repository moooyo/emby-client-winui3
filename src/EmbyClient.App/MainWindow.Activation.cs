using System.Runtime.InteropServices;
using EmbyClient.App.Services;
using Microsoft.UI.Windowing;

namespace EmbyClient.App;

public sealed partial class MainWindow
{
    private readonly WindowsJumpListService _jumpList = new();

    private void InitializeWindowsActivation()
    {
        _page.WindowsIntegrationStateChanged += WindowsIntegrationStateChanged;
        _page.WindowsIntegrationPreferencesChanged += WindowsIntegrationPreferencesChanged;
        _page.ForegroundRequested += WindowsForegroundRequested;
        _page.ShutdownRequested += PageShutdownRequested;
        PublishWindowsJumpList();
    }

    private void WindowsIntegrationStateChanged(object? sender, EventArgs args)
    {
        if (!_closing) PublishWindowsJumpList();
    }

    private void WindowsIntegrationPreferencesChanged(object? sender, EventArgs args)
    {
        if (_closing) return;
        App.ApplyWindowPlacementPreference(_page.WindowsIntegrationPreferences.RememberWindowPlacement);
        PublishWindowsJumpList();
    }

    private void PublishWindowsJumpList() => _jumpList.Update(new WindowsJumpListPresentation(
        LumenText.Get("Search media library"), LumenText.Get("Settings"), LumenText.Get("Switch server or user"),
        LumenText.Get("Continue watching"), _page.WindowsIntegrationAccountKey, _page.WindowsContinueWatching));

    private void WindowsForegroundRequested(object? sender, EventArgs args) => ActivateWindowsRoute(null);
    private void PageShutdownRequested(object? sender, EventArgs args) { if (!_closing) Close(); }

    public void ActivateWindowsRoute(WindowsActivationRoute? route)
    {
        DispatchWindowsActivation(() =>
        {
            BringWindowsActivationForward();
            if (route is not null) _page.ActivateWindowsRoute(route);
        });
    }

    public void ActivateEpisodeNotification(IReadOnlyDictionary<string, string> arguments)
    {
        if (!EpisodeNotificationTarget.TryParse(arguments, out var target)
            || target!.Intent == EpisodeNotificationIntent.RemindLater) return;
        DispatchWindowsActivation(() =>
        {
            BringWindowsActivationForward();
            _page.ActivateEpisodeNotification(arguments);
        });
    }

    private void DispatchWindowsActivation(Action action)
    {
        void Run()
        {
            if (_closing) return;
            try { action(); }
            catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "MainWindow.WindowsActivation"); }
        }
        if (_chromeDispatcher.HasThreadAccess) Run();
        else _chromeDispatcher.TryEnqueue(Run);
    }

    private void BringWindowsActivationForward()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void DisposeWindowsActivation()
    {
        _page.WindowsIntegrationStateChanged -= WindowsIntegrationStateChanged;
        _page.WindowsIntegrationPreferencesChanged -= WindowsIntegrationPreferencesChanged;
        _page.ForegroundRequested -= WindowsForegroundRequested;
        _page.ShutdownRequested -= PageShutdownRequested;
        _jumpList.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parent, nint window);
}
