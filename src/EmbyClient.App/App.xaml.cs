using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace EmbyClient.App;

public partial class App : Application
{
    private MainWindow? _window;
    private readonly WindowPlacementStore _windowPlacementStore = new();

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WindowManager.PersistenceStorage = _windowPlacementStore;
        _window = new MainWindow();
        _window.Activate();
    }
}
