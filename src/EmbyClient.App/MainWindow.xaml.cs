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
            UpdatePresentation();
        };
        _page.ExitFullscreenRequested += (_, _) =>
        {
            if (AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            UpdatePresentation();
        };
        _page.ThemePreferenceChanged += (_, _) => WindowRoot.RequestedTheme = _page.RequestedTheme;
        WindowRoot.ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateCaptionColors);
        AppTitleBar.RegisterPropertyChangedCallback(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty,
            (_, _) => DispatcherQueue.TryEnqueue(UpdateCaptionColors));
        AppWindow.Changed += (_, args) => { if (args.DidPresenterChange) UpdatePresentation(); };
        RootFrame.Content = _page;
        PersistenceId = "MainWindow";
        AppWindow.Closing += OnClosing;
        UpdatePresentation();
        UpdateCaptionColors();
    }

    private void UpdatePresentation()
    {
        var fullscreen = AppWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        AppTitleBar.Visibility = fullscreen ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        _page.SetFullscreenState(fullscreen);
    }

    private void UpdateCaptionColors()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = (AppTitleBar.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
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
