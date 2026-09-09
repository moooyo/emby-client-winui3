using LibVLCSharp.Platforms.Windows;
using Microsoft.UI.Xaml;

namespace EmbyClient.VlcProbe;

public sealed partial class ProbeWindow : Window
{
    private readonly TaskCompletionSource<string[]> _ready;
    private readonly Action<string> _milestone;

    internal ProbeWindow(TaskCompletionSource<string[]> ready, Action<string> milestone)
    {
        _ready = ready;
        _milestone = milestone;
        InitializeComponent();
    }

    internal VideoView Video => VideoView;

    private void VideoView_Initialized(object? sender, InitializedEventArgs args) => _ready.TrySetResult(args.SwapChainOptions);

    private void VideoView_Loaded(object sender, RoutedEventArgs args) => _milestone("VideoViewLoaded");
}
