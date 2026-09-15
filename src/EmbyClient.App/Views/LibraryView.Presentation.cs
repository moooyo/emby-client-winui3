using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views;

public sealed partial class LibraryView
{
    private bool? _expandedPanePreference;
    private int _navigationModeVersion;
    private int _searchFocusRequestVersion;

    public event EventHandler? NavigationStateChanged;
    private bool UseEpisodeList => ViewModel.DetailItemsAreEpisodes && EpisodeLayoutButton.IsChecked == true;

    public void ToggleNavigationPane()
    {
        _navigationModeVersion++;
        _searchFocusRequestVersion++;
        var open = !LibraryNavigation.IsPaneOpen;
        if (LibraryNavigation.DisplayMode == NavigationViewDisplayMode.Expanded) _expandedPanePreference = open;
        LibraryNavigation.IsPaneOpen = open;
        if (open) (LibraryNavigation.SelectedItem as Control ?? HomeNavigationItem).Focus(FocusState.Programmatic);
    }

    private void NavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        var mode = args.DisplayMode;
        var version = ++_navigationModeVersion;
        // NavigationView raises this event before finishing its SplitView layout transition.
        // Apply the user's preference afterwards so its list width and pane state stay in sync.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_componentInitialized || version != _navigationModeVersion || sender.DisplayMode != mode) return;
            sender.IsPaneOpen = mode == NavigationViewDisplayMode.Expanded && (_expandedPanePreference ?? true);
            UpdateFooterWidth();
        });
    }

    private void CloseOverlayNavigation()
    {
        if (LibraryNavigation.DisplayMode != NavigationViewDisplayMode.Expanded) LibraryNavigation.IsPaneOpen = false;
    }

    public async Task NavigateBackAsync()
    {
        if (IsPersonDialogOpen) return;
        await ViewModel.GoBackAsync();
        UpdateNavigation();
        FocusBrowseDestination();
    }

    private async Task OpenSearchAsync(FocusState focus)
    {
        await ViewModel.ShowSearchAsync();
        CloseOverlayNavigation();
        UpdateNavigation(false);
        FocusSearchInput(focus);
    }

    private void FocusSearchInput(FocusState focus)
    {
        var version = ++_searchFocusRequestVersion;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!CanFocusSearchInput(version)) return;
            SearchBox.ApplyTemplate();
            SearchBox.UpdateLayout();
            if (!CanFocusSearchInput(version) || !SearchBox.IsLoaded) return;
            // Focus the editable template part after navigation has finished moving focus.
            if (FindSearchTextBox(SearchBox) is { IsLoaded: true, IsEnabled: true } textBox
                && textBox.Focus(focus)) return;
            SearchBox.Focus(focus);
        });
    }

    private bool CanFocusSearchInput(int version) => _componentInitialized && version == _searchFocusRequestVersion
        && IsLoaded && Visibility == Visibility.Visible && ViewModel.IsSearch && !IsPersonDialogOpen
        && ViewModel.BrowseVisibility == Visibility.Visible && SearchBox.IsEnabled
        && SearchBox.Visibility == Visibility.Visible;

    private static TextBox? FindSearchTextBox(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is TextBox textBox) return textBox;
            if (FindSearchTextBox(child) is { } nested) return nested;
        }
        return null;
    }

    private async void ClearBrowseFilterClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.ClearBrowseFilterAsync();
        UpdateNavigation();
        if (ViewModel.BrowseOptionsVisibility != Visibility.Visible || !WatchStatusSelector.Focus(FocusState.Programmatic))
            FocusBrowseDestination();
    }

    private async void DetailBackClicked(object sender, RoutedEventArgs args)
    {
        if (IsPersonDialogOpen) return;
        await NavigateBackAsync();
    }

    private void UpdateDetailPresentation()
    {
        UpdateDetailBreadcrumb();
        OverviewText.MaxLines = 4;
        OverviewToggle.IsChecked = false;
        OverviewToggle.Content = "Read more";
        UpdateOverviewAffordance();
        UpdateDetailHeroLayout(DetailHero.ActualWidth);
    }

    private void UpdateDetailHeroLayout(double width)
    {
        if (!_componentInitialized) return;
        var landscape = ViewModel.Detail.Item.Type == "Episode";
        AutomationProperties.SetAutomationId(DetailPoster, landscape ? "LandscapeArtwork" : "PosterArtwork");
        if (width <= 0) return;
        var scale = Math.Max(1, _textScaleFactor);
        var narrow = width < (landscape ? 820 : 620) * Math.Min(1.5, scale);
        var artworkWidth = landscape ? narrow ? Math.Min(360, Math.Max(160, width - 64)) : 280 : narrow ? 112 : 160;
        DetailPosterColumn.Width = new GridLength(narrow ? 0 : artworkWidth);
        DetailPosterFrame.Width = artworkWidth;
        DetailPosterFrame.Height = landscape ? artworkWidth * 9 / 16 : artworkWidth * 1.5;
        Grid.SetColumnSpan(DetailPosterFrame, narrow ? 2 : 1);
        Grid.SetRow(DetailInformation, narrow ? 1 : 0);
        Grid.SetColumn(DetailInformation, narrow ? 0 : 1);
        Grid.SetColumnSpan(DetailInformation, narrow ? 2 : 1);
        DetailLayout.ColumnSpacing = narrow ? 0 : 24;
    }

    private async void ParentSeries_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.OpenParentSeriesAsync();
        UpdateNavigation();
        FocusCurrentDetails();
    }

    private async void NextEpisode_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.OpenNextEpisodeAsync();
        UpdateNavigation();
        FocusCurrentDetails();
    }

    private void CancelSeasonSwitch_Click(object sender, RoutedEventArgs args) => ViewModel.CancelSeasonSwitch();

    private void UpdateDetailBreadcrumb()
    {
        if (!_componentInitialized) return;
        DetailBreadcrumb.Text = "Back";
        AutomationProperties.SetName(DetailBackButton, "Back to previous page");
        DetailBackButton.IsEnabled = ViewModel.CanGoBack;
    }

    private void UpdateOverviewAffordance() => OverviewToggle.Visibility = OverviewToggle.IsChecked == true || OverviewText.IsTextTrimmed
        ? Visibility.Visible : Visibility.Collapsed;

    private void EpisodeLayoutClicked(object sender, RoutedEventArgs args)
    {
        UpdateDetailShelfSize();
        _detailItemsScroller?.ChangeView(0, 0, null, true);
        QueueViewportUpdate();
    }

    private void OverviewToggleClicked(object sender, RoutedEventArgs args)
    {
        var expanded = OverviewToggle.IsChecked == true;
        OverviewText.MaxLines = expanded ? 0 : 4;
        OverviewToggle.Content = expanded ? "Show less" : "Read more";
        UpdateOverviewAffordance();
    }

    private async void MarkPlayedMenuClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.TogglePlayedAsync();
        DetailMoreButton.Focus(FocusState.Programmatic);
    }

    private void LibraryPresentationKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key != Windows.System.VirtualKey.Escape || IsPersonDialogOpen) return;
        if (LibraryNavigation.IsPaneOpen && LibraryNavigation.DisplayMode != NavigationViewDisplayMode.Expanded)
        {
            LibraryNavigation.IsPaneOpen = false;
            args.Handled = true;
        }
    }
}
