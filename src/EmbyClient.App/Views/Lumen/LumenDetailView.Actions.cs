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
    private CancellationTokenSource? _itemActionCancellation;

    private async Task RunItemActionAsync(MediaCardViewModel item, Func<ConnectedSession, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(item);
        var session = _session;
        if (session is null || string.IsNullOrWhiteSpace(item.Id) || _actionBusy || _activeDialog is not null) return;
        var lifetime = new CancellationTokenSource();
        _itemActionCancellation = lifetime;
        var token = lifetime.Token;
        _actionBusy = true;
        UpdateActionState();
        try
        {
            await action(session, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception) || exception is ArgumentException)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(_session?.Api, session.Api) && XamlRoot is not null)
            {
                var message = LumenUi.Text(DescribeRequestFailure(exception), 14);
                message.TextWrapping = TextWrapping.Wrap;
                var dialog = CreateDialog("The operation could not be completed. Try again.", message);
                dialog.CloseButtonText = LumenText.Get("Close");
                await ShowDialogAsync(dialog);
            }
        }
        finally
        {
            if (ReferenceEquals(_itemActionCancellation, lifetime))
            {
                _itemActionCancellation = null;
                _actionBusy = false;
                UpdateActionState();
            }
            lifetime.Dispose();
        }
    }

    private Button CreateMoreButton()
    {
        var button = LumenUi.IconButton("ellipsis", LumenText.Get("More"), 46, true);
        var popup = new Flyout { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, FlyoutPresenterStyle = PopupStyle(260) };
        popup.Opening += (_, _) =>
        {
            _hasOpenDetailFlyout = true;
            UpdateStageInteraction();
            var rows = new StackPanel { Spacing = 2 };
            void Add(string key, string icon, Action action, bool enabled = true)
            {
                var row = PopupAction(LumenText.Get(key), icon);
                row.IsEnabled = enabled && !_actionBusy;
                row.Click += (_, _) => { popup.Hide(); action(); };
                rows.Children.Add(row);
            }
            var series = _library.Detail.Item.Type is "Series" or "Season";
            Add(series ? "Play from the first episode" : "Play from beginning", "rotate-ccw",
                () => { if (series) _ = PlaySeriesFromBeginningAsync(); else RaisePlay(0); },
                _library.PlayableDetail.CanPlay && _library.IsPlaybackAllowed);
            if (!series && _library.PlayableDetail.CanPlay)
                Add("Add to play queue", "list-video", () => PlayRequested?.Invoke(this,
                    new LumenPlayRequestEventArgs(_library.PlayableDetail.Item, addToQueue: true)), _library.IsPlaybackAllowed);
            var resumeItem = _library.Detail.IsFolder ? _library.PlayableDetail : _library.Detail;
            if (resumeItem.CanResume) Add("Remove from continue watching", "x", () => _ = RemoveFromContinueWatchingAsync(resumeItem));
            Add("Add to collection", "plus", () => _ = AddToCollectionAsync(), _library.Detail.Item.Type != "BoxSet");
            var divider = LumenUi.Divider();
            divider.Margin = new Thickness(10, 5, 10, 5);
            rows.Children.Add(divider);
            Add("Refresh metadata", "refresh", () => _ = RequestMetadataRefreshAsync(), _session is not null);
            Add("Edit metadata", "sliders-horizontal", () => _ = EditMetadataAsync(), CanEditMetadata());
            Add("Media information", "info", () => _ = ShowMediaInformationAsync(), SelectedSource is not null || _library.PlayableDetail.CanPlay);
            popup.Content = rows;
        };
        popup.Closed += (_, _) => { _hasOpenDetailFlyout = false; UpdateStageInteraction(); };
        button.Flyout = popup;
        return button;
    }

    private Task PlaySeriesFromBeginningAsync()
    {
        var detail = _library.Detail.Item;
        var seriesId = detail.Type == "Season" ? detail.SeriesId : detail.Id;
        if (string.IsNullOrWhiteSpace(seriesId)) return Task.CompletedTask;
        return RunDetailActionAsync(async (session, token) =>
        {
            var seasonId = detail.Type == "Season" ? detail.Id : null;
            if (seasonId is null)
            {
                var seasons = await session.Api.GetSeasonsAsync(seriesId, token);
                var ordered = seasons.Items.Where(season => !string.IsNullOrWhiteSpace(season.Id))
                    .OrderBy(season => season.IndexNumber ?? int.MaxValue).ToArray();
                seasonId = ordered.FirstOrDefault(season => season.IndexNumber is > 0)?.Id ?? ordered.FirstOrDefault()?.Id;
            }
            var episodes = await session.Api.GetEpisodesAsync(seriesId, seasonId, token);
            var first = episodes.Items.OrderBy(episode => episode.IndexNumber ?? int.MaxValue)
                .Select(episode => new MediaCardViewModel(episode)).FirstOrDefault(episode => episode.CanPlay);
            token.ThrowIfCancellationRequested();
            if (_library.Detail.Id != detail.Id || !_library.IsPlaybackAllowed) return;
            if (first is null) SetNotice(LumenText.Get("No playable media is available."));
            else PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(first.Item));
        });
    }

    private static Button PopupAction(string label, string icon)
    {
        var row = LumenUi.Button(label, icon, onImage: true, height: 38);
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        row.HorizontalContentAlignment = HorizontalAlignment.Left;
        row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        row.BorderThickness = new Thickness(0);
        row.CornerRadius = new CornerRadius(10);
        row.Padding = new Thickness(12, 0, 12, 0);
        LumenUi.SetResource(row.Resources, "ButtonBackgroundPointerOver", ImageBrush(20));
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
