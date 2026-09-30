using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using WinUIEx;

namespace EmbyClient.App;

public partial class App : Application
{
    public static Window? CurrentWindow { get; private set; }
    private MainWindow? _window;
    private readonly WindowPlacementStore _windowPlacementStore = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => RecordAcceptanceFailure(args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            WindowManager.PersistenceStorage = _windowPlacementStore;
            _window = new MainWindow();
            CurrentWindow = _window;
            _window.Activate();
        }
        catch (Exception exception)
        {
            RecordAcceptanceFailure(exception);
            throw;
        }
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
