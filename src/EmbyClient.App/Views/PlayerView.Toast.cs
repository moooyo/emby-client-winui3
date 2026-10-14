using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly DispatcherTimer _playerToastTimer = new() { Interval = TimeSpan.FromMilliseconds(3400) };
    private Func<Task>? _playerToastAction;
    private bool _playerToastPointerInside;
    private bool _playerToastFocusInside;

    private void InitializePlayerToast()
    {
        PlayerToast.Background = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 28, 26, 23), TintOpacity = .94,
            TintLuminosityOpacity = .65, FallbackColor = Windows.UI.Color.FromArgb(248, 28, 26, 23)
        };
        _playerToastTimer.Tick += (_, _) => ClosePlayerToast();
        PlayerToast.PointerEntered += (_, _) => { _playerToastPointerInside = true; _playerToastTimer.Stop(); };
        PlayerToast.PointerExited += (_, _) => { _playerToastPointerInside = false; ResumePlayerToastTimer(); };
        PlayerToast.GotFocus += (_, _) => { _playerToastFocusInside = true; _playerToastTimer.Stop(); };
        PlayerToast.LostFocus += (_, _) =>
        {
            _playerToastFocusInside = IsWithin(XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, PlayerToast);
            ResumePlayerToastTimer();
        };
    }

    private void ShowPlayerToast(string message, string? actionLabel = null)
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        _playerToastAction = null;
        _playerToastPointerInside = _playerToastFocusInside = false;
        PlayerToastText.Text = LumenText.Get(message);
        PlayerToastActionButton.Content = actionLabel is null ? string.Empty : LumenText.Get(actionLabel);
        PlayerToastActionButton.Visibility = actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        PlayerToastActionButton.IsEnabled = false;
        PlayerToast.MaxWidth = Math.Max(0, PlayerRoot.ActualWidth - 32);
        PlayerToast.Visibility = Visibility.Visible;
        _playerToastTimer.Stop();
        _playerToastTimer.Start();
    }

    private void SetPlayerToastAction(Func<Task>? action, string helpText)
    {
        _playerToastAction = action;
        PlayerToastActionButton.IsEnabled = action is not null;
        SetControlLabel(PlayerToastActionButton, PlayerToastActionButton.Content?.ToString() ?? string.Empty);
        ToolTipService.SetToolTip(PlayerToastActionButton, LumenText.Get(helpText));
    }

    private void ClosePlayerToast()
    {
        _playerToastTimer.Stop();
        _playerToastAction = null;
        _playerToastPointerInside = _playerToastFocusInside = false;
        PlayerToast.Visibility = Visibility.Collapsed;
    }

    private void ResumePlayerToastTimer()
    {
        if (PlayerToast.Visibility == Visibility.Visible && !_playerToastPointerInside && !_playerToastFocusInside)
            _playerToastTimer.Start();
    }

    private async void PlayerToastActionClicked(object sender, RoutedEventArgs args)
    {
        if (IsModalOpen) return;
        var action = _playerToastAction;
        ClosePlayerToast();
        if (action is not null) await TryRunPlayerAdjustmentAsync(action);
    }
}
