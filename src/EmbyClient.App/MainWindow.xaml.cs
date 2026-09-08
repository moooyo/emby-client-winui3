using WinUIEx;

namespace EmbyClient.App;

public sealed partial class MainWindow : WindowEx
{
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
        RootFrame.Content = new MainPage();
    }
}