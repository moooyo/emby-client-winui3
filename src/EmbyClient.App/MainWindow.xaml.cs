using EmbyClient.App.Services;
using EmbyClient.App.Views.Lumen;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinUIEx;

namespace EmbyClient.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly LumenShellPage _page;
    private bool _closing;
    private bool _allowClose;
    private bool _compactOverlay;

    public MainWindow()
    {
        InitializeComponent();
        Width = 1440;
        Height = 900;
        MinWidth = 640;
        MinHeight = 600;
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _page = new LumenShellPage();
        RootFrame.Content = _page;
        _page.FullscreenRequested += (_, _) =>
        {
            var fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            SetPresenter(fullscreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
        };
        _page.ExitFullscreenRequested += (_, _) =>
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Default) SetPresenter(AppWindowPresenterKind.Default);
        };
        _page.CompactOverlayRequested += (_, _) =>
        {
            SetPresenter(_compactOverlay ? AppWindowPresenterKind.Default : AppWindowPresenterKind.CompactOverlay);
        };
        _page.ChromeChanged += (_, _) => UpdateChrome();
        WindowRoot.SizeChanged += (_, _) => UpdateDragRegions();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange) UpdatePresentation();
            if (args.DidSizeChange) UpdateDragRegions();
        };
        PersistenceId = "MainWindow";
        AppWindow.Closing += OnClosing;
        UpdatePresentation();
    }

    private void UpdateChrome()
    {
        if (_closing) return;
        WindowRoot.RequestedTheme = _page.PreferredTheme;
        // Windows ignores caption foreground alpha. Retain native window controls in windowed mode.
        var hoverInk = _page.IsImagePage ? Colors.White : LumenTheme.Color("Ink");
        var ink = hoverInk;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = ink;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ink;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(28, ink.R, ink.G, ink.B);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(42, ink.R, ink.G, ink.B);
        AppWindow.TitleBar.ButtonHoverForegroundColor = hoverInk;
        AppWindow.TitleBar.ButtonPressedForegroundColor = hoverInk;
        UpdateDragRegions();
    }

    private void UpdateDragRegions()
    {
        if (_closing || _page is null || !AppWindow.TitleBar.ExtendsContentIntoTitleBar
            || AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) return;
        var scale = _page.XamlRoot?.RasterizationScale ?? 1;
        var width = WindowRoot.ActualWidth;
        if (width <= 0) return;
        var available = Math.Max(0, width - AppWindow.TitleBar.RightInset / scale);
        var shift = width < 900 ? -92 : 0;
        var left = Math.Max(0, (width - 354 + shift) / 2 - 12);
        var right = Math.Min(available, (width + 354 + shift) / 2 + 12);
        var regions = new List<RectInt32>
        {
            new(0, 0, (int)(available * scale), (int)(8 * scale))
        };
        if (!_page.IsPlayerVisible)
        {
            // Interactive navigation and Back stay outside the window-drag region.
            var start = width < 900 ? 56d : 184d;
            if (left > start) regions.Add(new((int)(start * scale), (int)(8 * scale), (int)((left - start) * scale), (int)(48 * scale)));
            if (available > right) regions.Add(new((int)(right * scale), (int)(8 * scale), (int)((available - right) * scale), (int)(48 * scale)));
        }
        AppWindow.TitleBar.SetDragRectangles(regions.ToArray());
    }

    private void UpdatePresentation()
    {
        if (_closing) return;
        _compactOverlay = AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
        _page.SetCompactOverlayState(_compactOverlay);
        _page.SetFullscreenState(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        UpdateChrome();
    }

    private void SetPresenter(AppWindowPresenterKind kind)
    {
        // Apply limits before the native presenter chooses its size. Normal-page limits distort a video overlay.
        var compact = kind == AppWindowPresenterKind.CompactOverlay;
        MinWidth = compact ? 320 : 640;
        MinHeight = compact ? 180 : 600;
        AppWindow.SetPresenter(kind);
        UpdatePresentation();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { await _page.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* Closing remains bounded when the server is unavailable. */ }
        _allowClose = true;
        Close();
    }
}
