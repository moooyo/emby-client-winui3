using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EmbyClient.App.Views;

public sealed partial class LibraryView
{
    private bool _historyRestorePending;
    private int _historyRestoreVersion;
    private string? _lastInvokedItemId;

    private void CaptureBrowseViewport(object? sender, EventArgs args)
    {
        CapturePersonPageState();
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        var container = focused as GridViewItem ?? (focused is null ? null : FindAncestor<GridViewItem>(focused));
        var focusedId = (container?.Content as MediaCardViewModel)?.Id ?? _lastInvokedItemId;
        _lastInvokedItemId = null;
        var grid = ViewModel.HasDetails ? DetailItemsGrid : MediaGrid;
        var scroller = ViewModel.HasDetails ? _detailItemsScroller : _gridScroller;
        var anchorIndex = 0;
        string? anchorId = null;
        if (scroller is not null)
        {
            for (var index = 0; index < ViewModel.Items.Count; index++)
            {
                if (grid.ContainerFromIndex(index) is not FrameworkElement element) continue;
                var position = element.TransformToVisual(scroller).TransformPoint(new Windows.Foundation.Point());
                if (position.Y + element.ActualHeight <= 0 || position.X + element.ActualWidth <= 0) continue;
                anchorIndex = index;
                anchorId = ViewModel.Items[index].Id;
                break;
            }
        }
        ViewModel.ViewportState = new(ViewModel.IsHome ? HomeScroller.VerticalOffset : _gridScroller?.VerticalOffset ?? 0,
            DetailScroller.VerticalOffset, UseEpisodeList ? _detailItemsScroller?.VerticalOffset ?? 0
                : _detailItemsScroller?.HorizontalOffset ?? 0, anchorId, anchorIndex, focusedId,
            OverviewToggle.IsChecked == true, EpisodeLayoutButton.IsChecked == true, _navigationLibraryId,
            scroller?.ViewportWidth ?? 0);
    }

    private void RestoreBrowseViewport(object? sender, EventArgs args)
    {
        _historyRestorePending = true;
        _collectionLayoutVersion++;
        _autoLoadStalled = false;
        _resetDetailItemsScroll = false;
        _navigationLibraryId = ViewModel.ViewportState.NavigationLibraryId;
        var version = ViewModel.NavigationRevision;
        _historyRestoreVersion = version;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != ViewModel.NavigationRevision || !IsLoaded)
            {
                if (_historyRestoreVersion == version) _historyRestorePending = false;
                return;
            }
            var state = ViewModel.ViewportState;
            if (ViewModel.IsPerson)
            {
                UpdatePersonPage();
                FocusPersonDestination();
                _historyRestorePending = false;
                return;
            }
            EpisodeLayoutButton.IsChecked = state.EpisodeList;
            OverviewToggle.IsChecked = state.OverviewExpanded;
            OverviewText.MaxLines = state.OverviewExpanded ? 0 : 4;
            OverviewToggle.Content = state.OverviewExpanded ? "Show less" : "Read more";
            UpdateDetailShelfSize();
            _resetDetailItemsScroll = false;
            var grid = ViewModel.HasDetails ? DetailItemsGrid : MediaGrid;
            grid.UpdateLayout();
            ReconnectMediaGridScroller();
            ReconnectDetailItemsScroller();
            var anchor = ViewModel.Items.FirstOrDefault(item => item.Id == state.AnchorId)
                ?? ViewModel.Items.ElementAtOrDefault(Math.Clamp(state.AnchorIndex, 0, Math.Max(0, ViewModel.Items.Count - 1)));
            if (anchor is not null && !ViewModel.IsHome) grid.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
            grid.UpdateLayout();
            HomeScroller.UpdateLayout();
            DetailScroller.UpdateLayout();
            HomeScroller.ChangeView(null, state.VerticalOffset, null, true);
            if (_gridScroller is { } wall && Math.Abs(wall.ViewportWidth - state.CollectionWidth) < 1)
                wall.ChangeView(null, state.VerticalOffset, null, true);
            DetailScroller.ChangeView(null, state.DetailOffset, null, true);
            _detailItemsScroller?.ChangeView(UseEpisodeList ? 0 : state.ChildrenOffset,
                UseEpisodeList ? state.ChildrenOffset : 0, null, true);
            RestoreCardFocus(state.FocusedItemId, grid);
            _historyRestorePending = false;
            QueueViewportUpdate();
        });
    }

    private void RestoreCardFocus(string? itemId, GridView grid)
    {
        if (ViewModel.IsHome && itemId is not null)
        {
            grid = _loadedShelves.FirstOrDefault(shelf => shelf.Items.OfType<MediaCardViewModel>().Any(item => item.Id == itemId))
                ?? MyMediaGrid;
        }
        var item = grid.Items.OfType<MediaCardViewModel>().FirstOrDefault(card => card.Id == itemId);
        if (item is null) return;
        Control? container = grid.ContainerFromItem(item) as Control;
        if (container is null)
        {
            grid.ScrollIntoView(item);
            grid.UpdateLayout();
            container = grid.ContainerFromItem(item) as Control;
        }
        container?.Focus(FocusState.Programmatic);
    }
}
