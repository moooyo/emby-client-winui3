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
            try
            {
                _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
                _textScaleSubscribed = true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Desktop hosts may not expose this optional WinRT notification source.
                // Normal layout callbacks also refresh the current text scale.
            }
        }
        UpdateTextScale();
    }

    private void LibraryAccessibility_Unloaded(object sender, RoutedEventArgs args)
    {
        if (!_textScaleSubscribed) return;
        try { _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged; }
        catch (System.Runtime.InteropServices.COMException) { }
        _textScaleSubscribed = false;
    }

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed) UpdateTextScale(); });

    private void UpdateTextScale()
    {
        _textScaleFactor = ReadTextScale();
        foreach (var shelf in _loadedShelves)
            if (shelf.Tag is MediaShelfViewModel model) SizeHomeShelf(shelf, model.IsLandscape);
        foreach (var tile in _loadedLibraryTiles) SizeLibraryTile(tile);
        MyMediaGrid.Height = Math.Max(64, 44 * _textScaleFactor + 20) + 20;
        UpdateDetailShelfSize();
        UpdateDetailHeroLayout(DetailHero.ActualWidth);
        QueueViewportUpdate();
    }

    private void RefreshTextScaleIfNeeded()
    {
        if (IsLoaded && Math.Abs(_textScaleFactor - ReadTextScale()) > 0.001)
            UpdateTextScale();
    }

    private double ReadTextScale()
    {
        try { return Math.Max(1, _displaySettings.TextScaleFactor); }
        catch (System.Runtime.InteropServices.COMException) { return _textScaleFactor; }
    }

    private void SizeHomeShelf(GridView shelf, bool landscape)
    {
        // The outer page scrolls vertically, so the horizontal virtualized viewport must be bounded.
        shelf.Height = (landscape ? 228 : 324) + 58 * (_textScaleFactor - 1);
    }

    private void UpdateDetailShelfSize()
    {
        EpisodeLayoutButton.Visibility = ViewModel.DetailItemsAreEpisodes ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(EpisodeLayoutButton, UseEpisodeList ? "Switch to episode cards" : "Switch to episode list");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(EpisodeLayoutButton, UseEpisodeList ? "Episode list view" : "Episode card view");
        var itemTemplate = (DataTemplate)Resources[UseEpisodeList ? "EpisodeRowTemplate" : ViewModel.DetailItemsAreEpisodes ? "EpisodeCardTemplate" : "MediaCardTemplate"];
        if (!ReferenceEquals(DetailItemsGrid.ItemTemplate, itemTemplate)) DetailItemsGrid.ItemTemplate = itemTemplate;
        var containerStyle = (Style)Resources[UseEpisodeList ? "EpisodeRowContainerStyle" : "ShelfContainerStyle"];
        if (!ReferenceEquals(DetailItemsGrid.ItemContainerStyle, containerStyle)) DetailItemsGrid.ItemContainerStyle = containerStyle;
        var panelTemplate = (ItemsPanelTemplate)Resources[UseEpisodeList ? "DetailVerticalPanelTemplate" : "DetailHorizontalPanelTemplate"];
        if (!ReferenceEquals(DetailItemsGrid.ItemsPanel, panelTemplate))
        {
            // Select the orientation before the native panel is created, including while the shelf is hidden.
            DetachCollectionScroller(_detailItemsScroller);
            _detailItemsScroller = null;
            DetailItemsGrid.ItemsPanel = panelTemplate;
            _resetDetailItemsScroll = true;
            QueueViewportUpdate();
        }
        ScrollViewer.SetHorizontalScrollMode(DetailItemsGrid, UseEpisodeList ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(DetailItemsGrid, UseEpisodeList ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(DetailItemsGrid, UseEpisodeList ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(DetailItemsGrid, UseEpisodeList ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        DetailItemsGrid.Height = UseEpisodeList ? Math.Clamp(ViewModel.Items.Count, 1, 5) * (136 + 100 * (_textScaleFactor - 1))
            : ViewModel.DetailItemsAreEpisodes ? 328 + 112 * (_textScaleFactor - 1) : 332 + 58 * (_textScaleFactor - 1);
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
        tile.Height = Math.Max(64, 44 * _textScaleFactor + 20);
    }

    private void LibraryTile_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Grid tile) return;
        _loadedLibraryTiles.Remove(tile);
        tile.Unloaded -= LibraryTile_Unloaded;
    }

    public void FocusNavigation()
    {
        if ((LibraryNavigation.DisplayMode != NavigationViewDisplayMode.Minimal || LibraryNavigation.IsPaneOpen)
            && HomeNavigationItem.Focus(FocusState.Programmatic)) return;
        if (ViewModel.HomeLibrariesVisibility == Visibility.Visible && MyMediaGrid.Focus(FocusState.Programmatic)) return;
        PageOptionsButton.Focus(FocusState.Programmatic);
    }

    private void FocusBrowseDestination()
    {
        if (_historyRestorePending) return;
        if (ViewModel.IsPerson) { FocusPersonDestination(); return; }
        if (ViewModel.HasDetails) FocusCurrentDetails();
        else if (ViewModel.IsHome) FocusNavigation();
        else if (ViewModel.HasItems && MediaGrid.Focus(FocusState.Programmatic)) return;
        else if (ViewModel.IsSearch) FocusSearchInput(FocusState.Programmatic);
        else PageOptionsButton.Focus(FocusState.Programmatic);
    }
}
