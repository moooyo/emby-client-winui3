using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views;

public sealed partial class LibraryView
{
    private readonly UISettings _displaySettings = new();
    private readonly HashSet<GridView> _loadedShelves = [];
    private readonly HashSet<Grid> _loadedLibraryTiles = [];
    private double _textScaleFactor = 1;
    private bool _textScaleSubscribed;

    private void LibraryAccessibility_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_textScaleSubscribed)
        {
            _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
            _textScaleSubscribed = true;
        }
        UpdateTextScale();
    }

    private void LibraryAccessibility_Unloaded(object sender, RoutedEventArgs args)
    {
        if (!_textScaleSubscribed) return;
        _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged;
        _textScaleSubscribed = false;
    }

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed) UpdateTextScale(); });

    private void UpdateTextScale()
    {
        _textScaleFactor = Math.Max(1, _displaySettings.TextScaleFactor);
        foreach (var shelf in _loadedShelves)
            if (shelf.Tag is MediaShelfViewModel model) SizeHomeShelf(shelf, model.IsLandscape);
        foreach (var tile in _loadedLibraryTiles) SizeLibraryTile(tile);
        var tileScale = Math.Min(1.5, _textScaleFactor);
        MyMediaGrid.MinHeight = 124 * tileScale + 28;
        CastGrid.MinHeight = 248 + 38 * (_textScaleFactor - 1);
        UpdateDetailShelfSize();
        QueueViewportUpdate();
    }

    private void SizeHomeShelf(GridView shelf, bool landscape)
    {
        shelf.Height = double.NaN;
        shelf.MinHeight = (landscape ? 232 : 312) + 38 * (_textScaleFactor - 1);
    }

    private void UpdateDetailShelfSize()
    {
        DetailItemsGrid.Height = double.NaN;
        DetailItemsGrid.MinHeight = ViewModel.DetailItemsAreEpisodes
            ? 328 + 112 * (_textScaleFactor - 1)
            : 312 + 38 * (_textScaleFactor - 1);
    }

    private void HomeShelf_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not GridView shelf) return;
        _loadedShelves.Remove(shelf);
        shelf.Unloaded -= HomeShelf_Unloaded;
    }

    private void LibraryTile_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Grid tile) return;
        if (_loadedLibraryTiles.Add(tile)) tile.Unloaded += LibraryTile_Unloaded;
        SizeLibraryTile(tile);
    }

    private void SizeLibraryTile(Grid tile)
    {
        var scale = Math.Min(1.5, _textScaleFactor);
        tile.Width = 220 * scale;
        tile.Height = 124 * scale;
    }

    private void LibraryTile_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Grid tile) return;
        _loadedLibraryTiles.Remove(tile);
        tile.Unloaded -= LibraryTile_Unloaded;
    }

    public void FocusNavigation() => HomeNavigationItem.Focus(FocusState.Programmatic);

    private void FocusBrowseDestination()
    {
        if (ViewModel.HasDetails) FocusCurrentDetails();
        else if (ViewModel.IsHome) FocusNavigation();
        else if (ViewModel.HasItems) MediaGrid.Focus(FocusState.Programmatic);
        else SearchBox.Focus(FocusState.Programmatic);
    }
}
