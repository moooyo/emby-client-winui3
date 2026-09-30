using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private Button CreateMoreButton()
    {
        var button = LumenUi.IconButton("ellipsis", LumenText.Get("More"), 46, true);
        var popup = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterStyle = PopupStyle(236) };
        popup.Opening += (_, _) =>
        {
            var rows = new StackPanel { Spacing = 2 };
            void Add(string key, string icon, Action action, bool enabled = true)
            {
                var row = PopupAction(LumenText.Get(key), icon);
                row.IsEnabled = enabled && !_actionBusy;
                row.Click += (_, _) => { popup.Hide(); action(); };
                rows.Children.Add(row);
            }
            Add("Play from beginning", "rotate-ccw", () => RaisePlay(0), _library.PlayableDetail.CanPlay && _library.IsPlaybackAllowed);
            Add(_library.Detail.IsPlayed ? "Mark unwatched" : "Mark watched", "check", () => _ = MutateDetailUserDataAsync(false));
            Add("Add to collection", "plus", () => _ = AddToCollectionAsync(), _library.Detail.Item.Type != "BoxSet");
            var resumeItem = _library.Detail.IsFolder ? _library.PlayableDetail : _library.Detail;
            Add("Remove from continue watching", "x", () => _ = RemoveFromContinueWatchingAsync(resumeItem), resumeItem.CanResume);
            var divider = LumenUi.Divider();
            divider.Margin = new Thickness(10, 5, 10, 5);
            rows.Children.Add(divider);
            Add("Refresh metadata", "refresh", () => _ = RequestMetadataRefreshAsync(), _session is not null);
            Add("Edit metadata", "sliders-horizontal", () => _ = EditMetadataAsync(), CanEditMetadata());
            Add("Media information", "info", () => _ = ShowMediaInformationAsync(), SelectedSource is not null || _library.PlayableDetail.CanPlay);
            popup.Content = rows;
        };
        button.Flyout = popup;
        return button;
    }

    private static Button PopupAction(string label, string icon)
    {
        var row = LumenUi.Button(label, icon, height: 38);
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.HorizontalContentAlignment = HorizontalAlignment.Left;
        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.BorderThickness = new Thickness(0);
        row.CornerRadius = new CornerRadius(10);
        row.Padding = new Thickness(12, 0, 12, 0);
        row.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("ControlStrong");
        return row;
    }

    private bool CanEditMetadata() => _session is not null && (_library.Detail.Item.CanEditItems == true
        || _library.Detail.Item.CanEditItems is null && _session.User.Policy?.IsAdministrator == true);

    private async Task MutateDetailUserDataAsync(bool favorite)
    {
        if (_actionBusy || _session is null || _detailCancellation is null || _detailId is null) return;
        var id = _detailId;
        var version = _detailVersion;
        var token = _detailCancellation.Token;
        _actionBusy = true;
        UpdateActionState();
        try
        {
            if (favorite) await _library.ToggleFavoriteAsync(token);
            else await _library.TogglePlayedAsync(token);
            if (!IsCurrentDetail(id, version)) return;
            SetNotice(_library.HasError ? LumenText.Get("The operation could not be completed. Try again.") : string.Empty, _library.HasError);
            _forceRender = true;
            Refresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception))
        { if (IsCurrentDetail(id, version)) SetNotice(DescribeRequestFailure(exception), true); }
        finally
        {
            if (IsCurrentDetail(id, version)) { _actionBusy = false; UpdateActionState(); }
        }
    }

    private async Task RunDetailActionAsync(Func<ConnectedSession, CancellationToken, Task> action, string? successKey = null)
    {
        var session = _session;
        if (_actionBusy || session is null || _detailCancellation is null || _detailId is null) return;
        var id = _detailId;
        var version = _detailVersion;
        var token = _detailCancellation.Token;
        _actionBusy = true;
        UpdateActionState();
        try
        {
            await action(session, token);
            if (!IsCurrentDetail(id, version)) return;
            if (successKey is not null) SetNotice(LumenText.Get(successKey));
            _forceRender = true;
            Refresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception) || exception is ArgumentException)
        { if (IsCurrentDetail(id, version)) SetNotice(DescribeRequestFailure(exception), true); }
        finally
        {
            if (IsCurrentDetail(id, version)) { _actionBusy = false; UpdateActionState(); }
        }
    }

    private Task RemoveFromContinueWatchingAsync(MediaCardViewModel item) => RunDetailActionAsync(
        (_, token) => _library.RemoveFromContinueWatchingAsync(item, token), "Removed from continue watching.");

    private Task RequestMetadataRefreshAsync()
    {
        if (_session is null) return Task.CompletedTask;
        var id = _library.Detail.Id;
        return RunDetailActionAsync((session, token) => session.Api.RefreshItemMetadataAsync(id,
            new MetadataRefreshOptions { MetadataRefreshMode = "FullRefresh", ImageRefreshMode = "FullRefresh" }, token), "Refresh requested.");
    }

    private ContentDialog CreateDialog(string title, UIElement content, string? primary = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Title = LumenText.Get(title), Content = content, CloseButtonText = LumenText.Get("Cancel"),
            PrimaryButtonText = primary is null ? string.Empty : LumenText.Get(primary),
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary
        };
        LumenDialogTheme.Apply(dialog);
        return dialog;
    }

    private static TextBox MetadataField(string key, string? value, bool multiline = false)
    {
        var field = new TextBox
        {
            Header = LumenText.Get(key), Text = value ?? string.Empty, FontFamily = LumenTheme.SansFont,
            Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), CornerRadius = new CornerRadius(8),
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 110 : 40, MaxLength = multiline ? 100_000 : 1000
        };
        LumenDialogTheme.ApplyField(field);
        AutomationProperties.SetName(field, LumenText.Get(key));
        return field;
    }
}
