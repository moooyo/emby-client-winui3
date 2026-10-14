using EmbyClient.App.Services;
using Microsoft.UI.Windowing;
using WinUIEx.Messaging;

namespace EmbyClient.App;

public sealed partial class MainWindow
{
    private readonly ContentRefreshRateService _contentRefreshRate = new();
    private WindowMessageMonitor? _displayMessageMonitor;

    private void InitializeContentRefreshRate()
    {
        _contentRefreshRate.StatusChanged += ContentRefreshRateStatusChanged;
        _page.RefreshRatePlaybackChanged += RefreshRatePlaybackChanged;
        _page.WindowsIntegrationPreferencesChanged += RefreshRatePlaybackChanged;
        _page.SetRefreshRateStatus(_contentRefreshRate.Status);
        _ = RefreshHdrDisplayEligibilitySafelyAsync();
        _displayMessageMonitor = new WindowMessageMonitor(this);
        _displayMessageMonitor.WindowMessageReceived += DisplayMessageReceived;
        QueueContentRefreshRateUpdate();
    }

    private void RefreshRatePlaybackChanged(object? sender, EventArgs args) => QueueContentRefreshRateUpdate();

    private void ContentRefreshRateStatusChanged(object? sender, EventArgs args) =>
        _chromeDispatcher.TryEnqueue(() =>
        {
            if (!_closing) _page.SetRefreshRateStatus(_contentRefreshRate.Status);
        });

    private void DisplayMessageReceived(object? sender, WindowMessageEventArgs args)
    {
        // WM_DISPLAYCHANGE and WM_DEVICECHANGE also cover an external display-mode or topology change.
        if (!_closing && args.Message.MessageId is 0x007E or 0x0219)
            _chromeDispatcher.TryEnqueue(QueueContentRefreshRateUpdate);
    }

    private void QueueContentRefreshRateUpdate()
    {
        if (_closing) return;
        _ = RefreshHdrDisplayEligibilitySafelyAsync();
        _ = _contentRefreshRate.UpdateAsync(new ContentRefreshRateRequest(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen,
            _page.WindowsIntegrationPreferences.MatchRefreshRate, _page.RefreshRatePlayback));
    }

    private async Task RefreshHdrDisplayEligibilitySafelyAsync()
    {
        try { await _page.RefreshHdrDisplayEligibilityAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("HDR display refresh failed: {0}.", exception.GetType().Name);
        }
    }

    private async Task RetireContentRefreshRateAsync()
    {
        _page.RefreshRatePlaybackChanged -= RefreshRatePlaybackChanged;
        _page.WindowsIntegrationPreferencesChanged -= RefreshRatePlaybackChanged;
        _contentRefreshRate.StatusChanged -= ContentRefreshRateStatusChanged;
        if (_displayMessageMonitor is not null)
        {
            _displayMessageMonitor.WindowMessageReceived -= DisplayMessageReceived;
            _displayMessageMonitor.Dispose();
            _displayMessageMonitor = null;
        }
        await _contentRefreshRate.DisposeAsync();
        if (_contentRefreshRate.NeedsRestoreRetry)
        {
            await _contentRefreshRate.DisposeAsync();
            if (_contentRefreshRate.NeedsRestoreRetry)
                System.Diagnostics.Trace.TraceWarning("The owned display mode could not be restored before window retirement.");
        }
    }
}
