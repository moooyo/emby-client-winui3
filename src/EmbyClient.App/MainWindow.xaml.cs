using WinUIEx;

namespace EmbyClient.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly MainPage _page;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Width = 1280;
        Height = 840;
        MinWidth = 860;
        MinHeight = 600;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _page = new MainPage();
        _page.FullscreenRequested += (_, _) =>
        {
            var fullscreen = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
            AppWindow.SetPresenter(fullscreen ? Microsoft.UI.Windowing.AppWindowPresenterKind.Default : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            AppTitleBar.Visibility = fullscreen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        };
        _page.ExitFullscreenRequested += (_, _) =>
        {
            if (AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;
            AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            AppTitleBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        };
        RootFrame.Content = _page;
        AppWindow.Closing += OnClosing;
    }

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { await _page.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception) { /* Shutdown is bounded even when the server is unavailable. */ }
        _allowClose = true;
        Close();
    }
}
