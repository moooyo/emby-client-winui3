using EmbyClient.Api;
using EmbyClient.App.ViewModels;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    public async Task ShowItemMediaInformationAsync(MediaCardViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_session is not { } session || _actionBusy || _activeDialog is not null) return;
        var target = item;
        if (item.Item.Type == "Episode" && item.Item.SeriesId is { Length: > 0 } seriesId)
        {
            // Only the real parent identity is needed here. Navigation fetches its server metadata.
            target = new MediaCardViewModel(new BaseItemDto
            {
                Id = seriesId, Name = item.Item.SeriesName, Type = "Series"
            });
        }
        if (_library.HasDetails && _library.Detail.Id == target.Id)
        {
            ShowCurrentMediaInformationPage();
            return;
        }
        var opening = _library.ShowItemAsync(target);
        var navigationRevision = _library.NavigationRevision;
        await opening;
        if (!ReferenceEquals(_session?.Api, session.Api) || navigationRevision != _library.NavigationRevision
            || !_library.HasDetails || _library.Detail.Id != target.Id) return;
        ShowCurrentMediaInformationPage();
    }

    private Task ShowMediaInformationAsync(MediaCardViewModel? selectedItem = null)
    {
        if (selectedItem is not null) return ShowItemMediaInformationAsync(selectedItem);
        if (_library.HasDetails && _activeDialog is null && !_actionBusy) ShowCurrentMediaInformationPage();
        return Task.CompletedTask;
    }

    private void ShowCurrentMediaInformationPage()
    {
        var hasChildren = _library.Detail.IsFolder || _library.Detail.Item.Type == "Episode" && _library.NextEpisode is not null;
        var index = hasChildren ? 3 : 2;
        if (_detailId == _library.Detail.Id && _pageConfiguration is not null)
        {
            ConfigureDetailPages(_library.Detail);
            _deck.GoToPage(index);
        }
        else
        {
            _pendingPageIndex = index;
            _pendingScrollOffset = null;
            _forceRender = true;
            Refresh();
        }
        FocusPrimaryAction();
    }
}
