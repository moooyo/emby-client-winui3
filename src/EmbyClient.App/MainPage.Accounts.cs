using EmbyClient.App.Services;
using EmbyClient.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App;

public sealed partial class MainPage
{
    private bool _accountActionPending;
    private ContentDialog? _accountDialog;
    private TaskCompletionSource? _accountActionCompletion;
    private ConnectedSession? _pendingSignOut;
    private bool _pendingLocalSignOut;
    private bool _pendingServerSignOut;

    private void UpdateConnectionPresentation()
    {
        if (ManualSignIn is null || RestoreButton is null) return;
        var savedSignIn = SavedAccounts.SelectedItem is ComboBoxItem
            { Tag: SavedAccount { ProtectedToken.Length: > 0 } account }
            && _connections.GetAccountRestriction(account.Key) is null && _manualAccountRestriction is null;
        RestoreButton.Content = "Continue with this account";
        RestoreButton.Visibility = savedSignIn ? Visibility.Visible : Visibility.Collapsed;
        ManualSignIn.Header = savedSignIn ? "Use a password or another account"
            : SavedAccounts.Items.Count == 0 ? "Account details" : "Sign in with a password";
        RestoreButton.Style = ManualSignIn.IsExpanded ? null : (Style)Resources["SavedSignInAccentStyle"];
        UpdateDirectSignInLayout();
    }

    private async void AddAccountClicked(object sender, RoutedEventArgs args) =>
        await ShowAccountConnectionAsync(addAccount: true);

    private async Task ShowAccountConnectionAsync(bool addAccount) =>
        await RunAccountActionAsync(async () =>
        {
            var current = _session;
            if (current is null) return;
            AccountMenu.Hide();
            var dialog = new AccountConnectionDialog(_connections, current.AccountKey, addAccount)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = RequestedTheme
            };
            _accountDialog = dialog;
            try { await dialog.ShowAsync(); }
            finally { if (ReferenceEquals(_accountDialog, dialog)) _accountDialog = null; }
            var connected = dialog.ConnectedSession;
            if (connected is null || _shuttingDown || !ReferenceEquals(_session, current)) return;
            // Only commit the switch after the chooser has authenticated the new account.
            await LeaveSessionAsync(false);
            if (_shuttingDown) return;
            await ActivateSessionAsync(connected, CancellationToken.None);
        }, () =>
        {
            if (_session is not null) AccountButton.Focus(FocusState.Programmatic);
            else FocusSignInAction();
        });

    private async void SignOutClicked(object sender, RoutedEventArgs args) =>
        await RunAccountActionAsync(async () =>
        {
            var session = _session;
            if (session is null) return;
            if (!await ConfirmAccountActionAsync("Sign out?",
                $"Sign out of {session.User.Name ?? "this account"} on {AccountConnectionDialog.AccountHost(session.Api.ApiRoot.AbsoluteUri)}? You will need your password to sign in again on this device.",
                "Sign out")) return;
            if (_shuttingDown || !ReferenceEquals(_session, session)) return;
            await LeaveSessionAsync(true);
            if (!_shuttingDown) ManualSignIn.IsExpanded = true;
        }, () =>
        {
            if (_session is null) FocusSignInAction();
            else AccountButton.Focus(FocusState.Programmatic);
        });

    private async void RemoveSavedClicked(object sender, RoutedEventArgs args)
    {
        if (SavedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount account })
            await RemoveAccountAsync(account);
    }

    private async void RemoveCurrentAccountClicked(object sender, RoutedEventArgs args)
    {
        var account = _connections.Settings.Accounts.Find(value => value.Key == _session?.AccountKey);
        if (account is not null) await RemoveAccountAsync(account);
        else ShowNotice("This account has not been saved on this device. You can sign out to close this session.", InfoBarSeverity.Informational);
    }

    private async Task RemoveAccountAsync(SavedAccount account) =>
        await RunAccountActionAsync(async () =>
        {
            if (!await ConfirmAccountActionAsync("Remove saved account?",
                $"Remove the saved sign-in for {account.UserName} on {account.ServerName} ({AccountConnectionDialog.AccountHost(account.ApiRoot)}) from this device? Your account and media on the server will stay available.",
                "Remove from this device")) return;
            if (_shuttingDown) return;
            if (_session?.AccountKey == account.Key) await LeaveSessionAsync(false);
            if (_shuttingDown) return;
            await _connections.RemoveSavedAccountAsync(account.Key);
            if (_shuttingDown) return;
            LoadSavedAccounts();
            if (SavedAccounts.Items.Count == 0)
            {
                ServerAddress.Text = string.Empty;
                UserName.Text = string.Empty;
                Password.Password = string.Empty;
            }
        }, () =>
        {
            if (_session is not null) AccountButton.Focus(FocusState.Programmatic);
            else if (SavedAccounts.Items.Count > 0) SavedAccounts.Focus(FocusState.Programmatic);
            else ServerAddress.Focus(FocusState.Programmatic);
        });

    private async Task<bool> ConfirmAccountActionAsync(string title, string message, string action)
    {
        AccountMenu.Hide();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = RequestedTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        _accountDialog = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { if (ReferenceEquals(_accountDialog, dialog)) _accountDialog = null; }
    }

    private void ShowPartialSignOut(ConnectedSession session, bool localFailed, bool serverFailed)
    {
        _pendingSignOut = session;
        _pendingLocalSignOut = localFailed;
        _pendingServerSignOut = serverFailed;
        var message = localFailed && serverFailed
            ? "This session is closed, but the saved sign-in may remain on this device and the server could not confirm revocation. Retry sign-out. If it keeps failing, restore access to settings and revoke this device's access from the server."
            : localFailed
                ? "This session is closed, but Windows could not remove its saved sign-in from this device. Retry when access to saved settings is available."
                : "The saved sign-in was removed from this device, but the server could not confirm revocation. Retry to finish signing out from the server.";
        ShowNotice(message, InfoBarSeverity.Warning);
        Notice.IsClosable = false;
        var retry = new Button { Content = "Retry sign-out" };
        retry.Click += RetrySignOutClicked;
        Notice.ActionButton = retry;
    }

    private async void RetrySignOutClicked(object sender, RoutedEventArgs args) =>
        await RunAccountActionAsync(async () =>
        {
            var pending = _pendingSignOut;
            if (pending is null || _session is not null) return;
            var localFailed = false;
            var serverFailed = false;
            if (_pendingLocalSignOut)
            {
                try { await _connections.ForgetTokenAsync(pending.AccountKey); }
                catch (Exception) { localFailed = true; }
            }
            if (_pendingServerSignOut)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                serverFailed = await ConnectionService.RevokeSessionAsync(pending, deadline.Token) is not null;
            }
            if (_shuttingDown) return;
            LoadSavedAccounts(pending.AccountKey);
            if (localFailed || serverFailed) ShowPartialSignOut(pending, localFailed, serverFailed);
            else
            {
                _pendingSignOut = null;
                Notice.ActionButton = null;
                Notice.IsClosable = true;
                Notice.IsOpen = false;
                ShowConnectionNotice("Sign-out is complete. Sign in with a password to continue.", InfoBarSeverity.Success);
            }
        }, FocusSignInAction);

    private async Task RunAccountActionAsync(Func<Task> action, Action restoreFocus)
    {
        if (!_settingsReady || _accountActionPending || _themeSavePending || _sessionTransition
            || _shuttingDown || _connectionRequest is not null || Player.IsModalOpen || Library.IsPersonDialogOpen) return;
        _accountActionPending = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _accountActionCompletion = completion;
        SetConnecting(false);
        try { await action(); }
        catch (Exception error)
        {
            if (!_shuttingDown)
            {
                if (_session is null) ShowConnectionNotice(UiErrors.Describe(error), InfoBarSeverity.Error);
                else ShowNotice(UiErrors.Describe(error), InfoBarSeverity.Error);
            }
        }
        finally
        {
            _accountActionPending = false;
            if (!_shuttingDown)
            {
                SetConnecting(_connectionRequest is not null);
                restoreFocus();
            }
            if (ReferenceEquals(_accountActionCompletion, completion)) _accountActionCompletion = null;
            completion.TrySetResult();
        }
    }
}
