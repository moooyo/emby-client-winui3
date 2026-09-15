using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App.Views;

public sealed partial class AccountConnectionDialog : ContentDialog
{
    private readonly ConnectionService _connections;
    private readonly string _currentAccountKey;
    private readonly ComboBox _accounts = new() { Header = "Saved account", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _server = new() { Header = "Server address", PlaceholderText = "https://emby.example.com" };
    private readonly TextBox _user = new() { Header = "Username", PlaceholderText = "Your Emby username" };
    private readonly PasswordBox _password = new() { Header = "Password" };
    private readonly CheckBox _remember = new() { Content = "Remember this sign-in", IsChecked = true };
    private readonly Expander _manual = new() { Header = "Use a password", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _notice = new() { IsClosable = false };
    private readonly StackPanel _status = new() { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _progress = new() { Width = 20, Height = 20 };
    private readonly TextBlock _statusText = new() { Text = "Connecting to your server…", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _request;
    private bool _cancelRequested;
    private bool _canClose;
    private bool _loadingAccount;
    private string? _manualRestriction;

    public AccountConnectionDialog(ConnectionService connections, string currentAccountKey, bool addAccount)
    {
        _connections = connections;
        _currentAccountKey = currentAccountKey;
        Title = addAccount ? "Add account" : "Switch account";
        PrimaryButtonText = addAccount ? "Sign in" : "Switch account";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        var fields = new StackPanel { Spacing = 14 };
        fields.Children.Add(_server);
        fields.Children.Add(_user);
        fields.Children.Add(_password);
        fields.Children.Add(_remember);
        _manual.Content = fields;
        _status.Children.Add(_progress);
        _status.Children.Add(_statusText);
        AutomationProperties.SetLiveSetting(_statusText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var panel = new StackPanel { Spacing = 16, MinWidth = 280, MaxWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your current library stays open until the new account is connected. You can cancel to return to it.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(_accounts);
        panel.Children.Add(_notice);
        panel.Children.Add(_manual);
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var account in connections.Settings.Accounts)
        {
            var label = $"{account.UserName} · {AccountHost(account.ApiRoot)}";
            if (account.Key == currentAccountKey) label += " (current)";
            var option = new ComboBoxItem { Content = label, Tag = account };
            ToolTipService.SetToolTip(option, $"{account.ServerName}\n{account.ApiRoot}");
            _accounts.Items.Add(option);
        }
        _accounts.Visibility = addAccount ? Visibility.Collapsed : Visibility.Visible;
        _manual.IsExpanded = addAccount || _accounts.Items.Count == 0;
        _manual.Header = addAccount ? "Account details" : "Use a password";
        _remember.IsEnabled = !connections.IsTemporarySession;
        _remember.IsChecked = !connections.IsTemporarySession;
        _accounts.SelectionChanged += AccountChanged;
        _server.TextChanged += CredentialsChanged;
        _user.TextChanged += CredentialsChanged;
        _password.KeyDown += PasswordKeyDown;
        _manual.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (_, _) => UpdateActions());
        PrimaryButtonClick += ConnectClicked;
        CloseButtonClick += (_, args) =>
        {
            if (_request is null) return;
            args.Cancel = true;
            CancelRequest();
        };
        Closing += (_, args) =>
        {
            if (_canClose || _request is null) return;
            args.Cancel = true;
            CancelRequest();
        };
        Opened += (_, _) =>
        {
            if (addAccount || _accounts.Items.Count == 0) _server.Focus(FocusState.Programmatic);
        };
        if (!addAccount && _accounts.Items.Count > 0)
        {
            _accounts.SelectedItem = _accounts.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is SavedAccount account && account.Key != currentAccountKey)
                ?? _accounts.Items[0];
        }
        UpdateActions();
    }

    public ConnectedSession? ConnectedSession { get; private set; }

    public static string AccountHost(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
        ? uri.IsDefaultPort ? uri.Host : uri.Authority
        : address;

    public void CancelRequest()
    {
        _cancelRequested = true;
        _request?.Cancel();
        _statusText.Text = "Cancelling connection…";
        IsPrimaryButtonEnabled = false;
    }

    private SavedAccount? SelectedAccount => (_accounts.SelectedItem as ComboBoxItem)?.Tag as SavedAccount;
    private bool UseSavedSignIn => _accounts.Visibility == Visibility.Visible && !_manual.IsExpanded
        && SelectedAccount is { ProtectedToken.Length: > 0 };

    private void AccountChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SelectedAccount is not { } account) return;
        _loadingAccount = true;
        _server.Text = account.ApiRoot;
        _user.Text = account.UserName;
        _password.Password = string.Empty;
        _manualRestriction = _connections.GetAccountRestriction(account.Key);
        _manual.IsExpanded = account.ProtectedToken.Length == 0 && _manualRestriction is null;
        _loadingAccount = false;
        _notice.IsOpen = false;
        if (_manualRestriction is not null) ShowError(_manualRestriction);
        else if (account.ProtectedToken.Length == 0)
            ShowError("This saved account needs a password before you can switch to it.", InfoBarSeverity.Informational);
        UpdateActions();
    }

    private void CredentialsChanged(object sender, TextChangedEventArgs args)
    {
        if (_loadingAccount) return;
        _manualRestriction = null;
        _notice.IsOpen = false;
        _password.Password = string.Empty;
        AutomationProperties.SetHelpText(_server, string.Empty);
        AutomationProperties.SetHelpText(_user, string.Empty);
        UpdateActions();
    }

    private async void ConnectClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        await ConnectAsync();
    }

    private async void PasswordKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || !IsPrimaryButtonEnabled) return;
        args.Handled = true;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_request is not null || !IsPrimaryButtonEnabled) return;
        var restore = UseSavedSignIn;
        if (!restore && !ValidateCredentials()) return;
        var request = new CancellationTokenSource();
        _request = request;
        _cancelRequested = false;
        _notice.IsOpen = false;
        UpdateActions();
        _status.StartBringIntoView();
        try
        {
            var session = restore && SelectedAccount is { } account
                ? await _connections.RestoreAsync(account, request.Token)
                : await _connections.SignInAsync(_server.Text, _user.Text, _password.Password,
                    _remember.IsChecked == true, request.Token);
            // A successful service result has crossed the account commit boundary.
            // A later Cancel must not discard a connection whose settings were saved.
            ConnectedSession = session;
            _password.Password = string.Empty;
            _canClose = true;
            Hide();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowError(UiErrors.Describe(error));
            if (UiErrors.IsAccountRestriction(error))
            {
                _manualRestriction = UiErrors.Describe(error);
                _password.Password = string.Empty;
            }
            else if (UiErrors.RequiresPassword(error))
            {
                _password.Password = string.Empty;
                _manual.IsExpanded = true;
            }
        }
        finally
        {
            _request = null;
            request.Dispose();
            UpdateActions();
            if (_cancelRequested)
            {
                _password.Password = string.Empty;
                _canClose = true;
                Hide();
            }
            else if (ConnectedSession is null)
            {
                if (_manualRestriction is not null)
                {
                    if (_accounts.Visibility == Visibility.Visible && _accounts.Items.Count > 0)
                        _accounts.Focus(FocusState.Programmatic);
                    else _user.Focus(FocusState.Programmatic);
                }
                else if (_manual.IsExpanded) _password.Focus(FocusState.Programmatic);
                else _accounts.Focus(FocusState.Programmatic);
            }
        }
    }

    private bool ValidateCredentials()
    {
        var validAddress = Uri.TryCreate(_server.Text.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host);
        if (!validAddress)
        {
            const string message = "Enter a complete server address beginning with http:// or https://.";
            ShowError(message);
            AutomationProperties.SetHelpText(_server, message);
            _server.Focus(FocusState.Programmatic);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            const string message = "Enter your Emby username.";
            ShowError(message);
            AutomationProperties.SetHelpText(_user, message);
            _user.Focus(FocusState.Programmatic);
            return false;
        }
        return true;
    }

    private void UpdateActions()
    {
        var busy = _request is not null;
        _accounts.IsEnabled = !busy;
        _manual.IsEnabled = !busy;
        IsPrimaryButtonEnabled = !busy && _manualRestriction is null
            && (!UseSavedSignIn || SelectedAccount?.Key != _currentAccountKey);
        PrimaryButtonText = UseSavedSignIn ? "Switch account" : "Sign in";
        _progress.IsActive = busy;
        _status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) _statusText.Text = "Connecting to your server…";
    }

    private void ShowError(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        _notice.Message = message;
        _notice.Severity = severity;
        _notice.IsOpen = true;
    }
}
