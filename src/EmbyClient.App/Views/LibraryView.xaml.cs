using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EmbyClient.App.Views;

public sealed partial class PlayItemRequestedEventArgs(BaseItemDto item, long startPositionTicks, bool addToQueue = false) : EventArgs
{
    public BaseItemDto Item { get; } = item;
    public long StartPositionTicks { get; } = startPositionTicks;
    public bool AddToQueue { get; } = addToQueue;
}

public sealed partial class LibraryView : UserControl
{
    private readonly Dictionary<Image, CancellationTokenSource> _posterRequests = [];
    private readonly Dictionary<Image, long> _posterSubscriptions = [];
    private readonly ConditionalWeakTable<GridViewItem, PosterContainer> _posterContainers = new();
    private readonly ConditionalWeakTable<Image, PosterLoadState<MediaCardViewModel>> _posterLoads = new();
    private ScrollViewer? _gridScroller;
    private bool _updatingNavigation;
    private bool _updatingBrowseOptions;
    private string? _navigationLibraryId;

    partial void ObservationSessionStarted();
    partial void ObservationPosterLoaded(Image image);
    partial void ObservationPosterUnloaded(Image image);
    partial void ObservationPosterTagChanged(DependencyObject sender);
    partial void ObservationBitmapDecoded(BitmapImage bitmap);
    partial void ObservationPosterAssigned(Image image);
    partial void ObservationPosterCleared(Image image);
    partial void ObservationPosterLoadRequested();
    partial void ObservationPosterLoadRejected();
    partial void ObservationPosterLoadDeduplicated();

    public LibraryView()
    {
        InitializeComponent();
        Loaded += LibraryAccessibility_Loaded;
        Unloaded += LibraryAccessibility_Unloaded;
        LibraryNavigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateFooterWidth());
        LibraryNavigation.Loaded += (_, _) => UpdateFooterWidth();
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ViewModel.Libraries.CollectionChanged += (_, _) => RebuildLibraries();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SessionExpired += ViewModel_SessionExpired;
    }

    public LibraryViewModel ViewModel { get; } = new();
    public UIElement? Footer
    {
        get => LibraryNavigation.PaneFooter as UIElement;
        set
        {
            LibraryNavigation.PaneFooter = value;
            UpdateFooterWidth();
        }
    }

    private void UpdateFooterWidth()
    {
        if (Footer is FrameworkElement footer)
            footer.Width = LibraryNavigation.IsPaneOpen ? LibraryNavigation.OpenPaneLength : LibraryNavigation.CompactPaneLength;
    }
    public event EventHandler<PlayItemRequestedEventArgs>? PlayRequested;
    public event EventHandler? SessionExpired;

    private void ViewModel_SessionExpired(object? sender, EventArgs args) => SessionExpired?.Invoke(this, args);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // Cached containers can stay loaded after navigation removes their items.
        if (args.Action != NotifyCollectionChangedAction.Reset) return;
        ResetCollectionScroll();
        foreach (var image in _posterSubscriptions.Keys.ToArray())
        {
            var grid = FindAncestor<GridView>(image);
            if (!ReferenceEquals(grid, MediaGrid) && !ReferenceEquals(grid, DetailItemsGrid)) continue;
            if (FindPosterContainer(image) is { } container)
            {
                if (_posterContainers.TryGetValue(container, out var state)) state.Realization.Retire();
                _posterContainers.Remove(container);
            }
            CancelPosterRequest(image);
        }
    }

    public async Task SetSessionAsync(EmbyApiClient api, string serverId, UserDto user, CancellationToken cancellationToken = default)
    {
        ObservationSessionStarted();
        CancelPosterRequests();
        SearchBox.Text = string.Empty;
        await ViewModel.SetSessionAsync(api, serverId, user, cancellationToken);
        RebuildLibraries();
        UpdateNavigation();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await ViewModel.RefreshAsync(cancellationToken);
        RebuildLibraries();
        UpdateNavigation();
    }

    public void ClearSession()
    {
        CancelPosterRequests();
        ViewModel.ClearSession();
        SearchBox.Text = string.Empty;
        RebuildLibraries();
        UpdateNavigation();
    }

    public void FocusCurrentDetails()
    {
        if (!ViewModel.HasDetails || Visibility != Visibility.Visible) return;
        if (ViewModel.PrimaryPlayVisibility == Visibility.Visible) PrimaryPlayButton.Focus(FocusState.Programmatic);
        else DetailScroller.Focus(FocusState.Programmatic);
    }

    private void RebuildLibraries()
    {
        _updatingNavigation = true;
        try
        {
            while (LibraryNavigation.MenuItems.Count > 4) LibraryNavigation.MenuItems.RemoveAt(4);
            foreach (var library in ViewModel.Libraries)
            {
                LibraryNavigation.MenuItems.Add(new NavigationViewItem
                {
                    Content = library.Title,
                    Tag = library,
                    Icon = new SymbolIcon(library.Item.CollectionType == "tvshows" ? Symbol.Video : Symbol.Library)
                });
            }
            if (ViewModel.IsDetailLocation && _navigationLibraryId is not null)
                LibraryNavigation.SelectedItem = LibraryNavigation.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(item => item.Tag is MediaCardViewModel card && card.Id == _navigationLibraryId);
        }
        finally { _updatingNavigation = false; }
    }

    private void UpdateNavigation(bool updateSearch = true)
    {
        _updatingNavigation = true;
        _updatingBrowseOptions = true;
        try
        {
            if (!ViewModel.IsDetailLocation)
            {
                _navigationLibraryId = ViewModel.SelectedLibraryId;
                LibraryNavigation.SelectedItem = ViewModel.IsHome || ViewModel.IsHomeSection ? HomeNavigationItem
                    : ViewModel.IsFavorites ? FavoritesNavigationItem
                    : LibraryNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag is MediaCardViewModel card && card.Id == ViewModel.SelectedLibraryId);
            }
            SortBox.SelectedIndex = ViewModel.BrowseSortKey switch
            {
                "ProductionYear" => 1,
                "DateCreated" => 2,
                _ => 0
            };
            DescendingButton.IsChecked = ViewModel.BrowseSortDescending;
            SortDirectionIcon.Glyph = ViewModel.BrowseSortDescending ? "\uE74B" : "\uE74A";
            var direction = ViewModel.BrowseSortDescending ? "Descending" : "Ascending";
            var nextDirection = ViewModel.BrowseSortDescending ? "ascending" : "descending";
            ToolTipService.SetToolTip(DescendingButton, $"{direction}. Click to sort {nextDirection}.");
            AutomationProperties.SetName(DescendingButton, $"{direction}. Activate to sort {nextDirection}.");
            UnplayedFilter.IsChecked = ViewModel.BrowseUnplayedOnly;
            if (updateSearch && !ViewModel.HasPendingSearch && SearchBox.Text != ViewModel.SearchText)
                SearchBox.Text = ViewModel.SearchText;
        }
        finally
        {
            _updatingNavigation = false;
            _updatingBrowseOptions = false;
        }
    }

    private async void LibraryNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_updatingNavigation || args.SelectedItem is not NavigationViewItem item) return;
        if (item == HomeNavigationItem) await ViewModel.ShowHomeAsync();
        else if (item == FavoritesNavigationItem) await ViewModel.ShowFavoritesAsync();
        else if (item.Tag is MediaCardViewModel library) await ViewModel.ShowLibraryAsync(library);
        UpdateNavigation();
    }

    private async void LibraryNavigation_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        await ViewModel.GoBackAsync();
        UpdateNavigation();
        FocusBrowseDestination();
    }

    private async void LibraryCard_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MediaCardViewModel library) return;
        await ViewModel.ShowLibraryAsync(library);
        UpdateNavigation();
    }

    private async void ShelfSeeAll_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: MediaShelfViewModel shelf }) return;
        await ViewModel.ShowShelfAsync(shelf);
        UpdateNavigation();
    }

    private void HomeShelf_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not GridView { Tag: MediaShelfViewModel shelf } grid) return;
        grid.ItemTemplate = (DataTemplate)Resources[shelf.IsLandscape ? "LandscapeCardTemplate" : "MediaCardTemplate"];
        if (_loadedShelves.Add(grid)) grid.Unloaded += HomeShelf_Unloaded;
        SizeHomeShelf(grid, shelf.IsLandscape);
    }

    private async void BrowseOptions_Changed(object sender, SelectionChangedEventArgs args) => await ApplyBrowseOptionsAsync();
    private async void BrowseDirection_Click(object sender, RoutedEventArgs args) => await ApplyBrowseOptionsAsync();

    private async Task ApplyBrowseOptionsAsync()
    {
        if (_updatingBrowseOptions || SortBox?.SelectedItem is not ComboBoxItem { Tag: string sort }
            || DescendingButton is null || UnplayedFilter is null) return;
        await ViewModel.SetBrowseOptionsAsync(sort, DescendingButton.IsChecked == true, UnplayedFilter.IsChecked == true);
        UpdateNavigation();
    }

    private async void SeasonBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SeasonBox.SelectedItem is MediaCardViewModel season) await ViewModel.SelectSeasonAsync(season);
    }

    private void DetailHero_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        DetailBackdrop.Width = args.NewSize.Width;
        DetailBackdrop.Height = args.NewSize.Height;
        DetailHero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        var narrow = args.NewSize.Width < 760;
        DetailPosterColumn.Width = new GridLength(narrow ? 0 : 216);
        DetailPosterFrame.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(DetailInformation, narrow ? 0 : 1);
        Grid.SetColumnSpan(DetailInformation, narrow ? 2 : 1);
        DetailLayout.ColumnSpacing = narrow ? 0 : 28;
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        return null;
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        await ViewModel.SearchAsync(sender.Text);
        UpdateNavigation(false);
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await ViewModel.SearchAsync(args.QueryText, false);
        UpdateNavigation(false);
    }

    private async void MediaGrid_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MediaCardViewModel item) return;
        await ViewModel.ShowItemAsync(item);
        UpdateNavigation();
        if (ViewModel.Detail.Id == item.Id) FocusCurrentDetails();
    }

    private void MediaGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var name = !args.InRecycleQueue && args.Item is MediaCardViewModel item
            ? string.Join(", ", new[] { item.DisplayTitle, item.CardSubtitle, item.StateLabel }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Empty;
        AutomationProperties.SetName(args.ItemContainer, name);
        if (args.ItemContainer is not GridViewItem container) return;
        if (args.InRecycleQueue)
        {
            if (_posterContainers.TryGetValue(container, out var retired))
            {
                retired.Realization.Retire();
                if (retired.Poster?.TryGetTarget(out var previous) == true) CancelContainerPoster(retired, previous);
            }
            if (FindPoster(container.ContentTemplateRoot) is { } poster && ReferenceEquals(FindPosterContainer(poster), container))
                CancelPosterRequest(poster);
            _posterContainers.Remove(container);
            return;
        }
        if (args.Phase != 0 || args.Item is not MediaCardViewModel card) return;
        var state = _posterContainers.GetValue(container, static _ => new PosterContainer());
        var sameItem = state.Realization.TryGetVersion(card, out _);
        var version = state.Realization.Activate(card);
        if (!sameItem && state.Poster?.TryGetTarget(out var oldPoster) == true) CancelContainerPoster(state, oldPoster);
        // Let phase-zero x:Bind set Tag. The later callback still belongs to this realization,
        // even if the native container has been recycled again before it is dispatched.
        args.RegisterUpdateCallback((_, update) => ContainerPosterReady(update, state, version));
    }

    private async void ContainerPosterReady(ContainerContentChangingEventArgs args, PosterContainer expected, long version)
    {
        if (args.InRecycleQueue || args.ItemContainer is not GridViewItem container
            || args.Item is not MediaCardViewModel item || !_posterContainers.TryGetValue(container, out var current)
            || !ReferenceEquals(current, expected) || !current.Realization.Owns(version, item)) return;
        if (FindPoster(container.ContentTemplateRoot) is not { } image) return;
        if (!ReferenceEquals(image.Tag, item) || !TryGetPosterBinding(image, item, out var owner, out var currentVersion)
            || !ReferenceEquals(owner, current.Realization) || currentVersion != version) return;
        await LoadPosterAsync(image);
    }

    private void CancelContainerPoster(PosterContainer state, Image image)
    {
        if (_posterLoads.TryGetValue(image, out var load) && load.IsOwnedBy(state.Realization))
            CancelPosterRequest(image);
    }

    private static Image? FindPoster(DependencyObject? element)
    {
        if (element is Image image) return image;
        if (element is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (FindPoster(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }

    private bool TryGetPosterBinding(Image image, MediaCardViewModel item, out object owner, out long version)
    {
        owner = this;
        version = 0;
        if (!image.IsLoaded) return false;
        if (ReferenceEquals(image, DetailPoster) || ReferenceEquals(image, DetailBackdrop))
            return ViewModel.HasDetails && ReferenceEquals(item, ViewModel.Detail);
        if (FindPosterContainer(image) is not { } container || !_posterContainers.TryGetValue(container, out var state)
            || !state.Realization.TryGetVersion(item, out version)) return false;
        if (state.Poster is null || !state.Poster.TryGetTarget(out var previous) || !ReferenceEquals(previous, image))
            state.Poster = new WeakReference<Image>(image);
        owner = state.Realization;
        return true;
    }

    private static GridViewItem? FindPosterContainer(Image image)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(image);
        while (parent is not null && parent is not GridViewItem) parent = VisualTreeHelper.GetParent(parent);
        return parent as GridViewItem;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RefreshAsync();
    private async void Favorite_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.ToggleFavoriteAsync();
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle) toggle.IsChecked = ViewModel.Detail.IsFavorite;
    }

    private async void Played_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.TogglePlayedAsync();
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle) toggle.IsChecked = ViewModel.Detail.IsPlayed;
    }
    private void Play_Click(object sender, RoutedEventArgs args) => RequestPlay(0, false);
    private void PrimaryPlay_Click(object sender, RoutedEventArgs args) => RequestPlay(
        ViewModel.PlayableDetail.CanResume ? ViewModel.PlayableDetail.ResumeTicks : 0, false);
    private void Queue_Click(object sender, RoutedEventArgs args) => RequestPlay(0, true);

    private void RequestPlay(long position, bool addToQueue)
    {
        if (!ViewModel.PlayableDetail.CanPlay) return;
        if (!ViewModel.IsPlaybackAllowed)
        {
            ViewModel.ErrorMessage = "Playback is disabled for this account. Contact your server administrator.";
            ViewModel.HasError = true;
            return;
        }
        PlayRequested?.Invoke(this, new(ViewModel.PlayableDetail.Item, position, addToQueue));
    }

    private void MediaGrid_Loaded(object sender, RoutedEventArgs args)
    {
        DetachCollectionScroller(_gridScroller);
        _gridScroller = FindScrollViewer(MediaGrid);
        AttachCollectionScroller(_gridScroller);
        QueueViewportUpdate();
    }

    private void MediaGrid_Unloaded(object sender, RoutedEventArgs args)
    {
        DetachCollectionScroller(_gridScroller);
        _gridScroller = null;
    }

    private void GridScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) => QueueViewportUpdate();

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private async void Poster_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image) return;
        ObservationPosterLoaded(image);
        if (!_posterSubscriptions.ContainsKey(image))
            _posterSubscriptions.Add(image, image.RegisterPropertyChangedCallback(FrameworkElement.TagProperty, PosterTagChanged));
        await LoadPosterAsync(image);
    }

    private void Poster_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image) return;
        ObservationPosterUnloaded(image);
        if (_posterSubscriptions.Remove(image, out var registration))
            image.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, registration);
        CancelPosterRequest(image);
        _posterLoads.Remove(image);
    }

    private async void PosterTagChanged(DependencyObject sender, DependencyProperty property)
    {
        ObservationPosterTagChanged(sender);
        if (sender is Image { IsLoaded: true } image && !ReferenceEquals(image, DetailPoster)
            && !ReferenceEquals(image, DetailBackdrop)) await LoadPosterAsync(image);
    }

    private async Task LoadPosterAsync(Image image)
    {
        ObservationPosterLoadRequested();
        if (image.Tag is not MediaCardViewModel item || !TryGetPosterBinding(image, item, out var owner, out var ownerVersion))
        {
            ObservationPosterLoadRejected();
            CancelPosterRequest(image);
            return;
        }
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var kind = AutomationProperties.GetAutomationId(image) switch
        {
            "BackdropArtwork" => ArtworkKind.Backdrop,
            "LandscapeArtwork" => ArtworkKind.Landscape,
            _ => ArtworkKind.Poster
        };
        var logicalWidth = kind == ArtworkKind.Backdrop ? 1280 : kind == ArtworkKind.Landscape ? 272
            : ReferenceEquals(image, DetailPoster) ? 216 : 156;
        if (kind == ArtworkKind.Poster && ReferenceEquals(FindAncestor<GridView>(image), MediaGrid) && _wallMetrics.PosterWidth > 0)
            logicalWidth = (int)(Math.Ceiling(_wallMetrics.PosterWidth / 32) * 32);
        var width = (int)Math.Clamp(logicalWidth * scale, logicalWidth, kind == ArtworkKind.Backdrop ? 1920 : 864);
        var height = kind == ArtworkKind.Poster ? width * 3 / 2 : width * 9 / 16;
        var load = _posterLoads.GetValue(image, static _ => new PosterLoadState<MediaCardViewModel>());
        if (!load.TryBegin(item, owner, ownerVersion, width, height, image.Source is not null, out var version))
        {
            ObservationPosterLoadDeduplicated();
            return;
        }
        CancelPosterDownload(image);
        image.Source = null;
        ObservationPosterCleared(image);
        var source = new CancellationTokenSource();
        var token = source.Token;
        _posterRequests[image] = source;
        var assigned = false;
        try
        {
            var bytes = await ViewModel.LoadPosterAsync(item, width, height, kind, token);
            if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
            void ApplyPoster(BitmapImage bitmap)
            {
                ObservationBitmapDecoded(bitmap);
                if (!token.IsCancellationRequested && load.Owns(version) && ReferenceEquals(image.Tag, item)
                    && TryGetPosterBinding(image, item, out var currentOwner, out var currentVersion)
                    && ReferenceEquals(owner, currentOwner) && ownerVersion == currentVersion
                    && _posterRequests.TryGetValue(image, out var current) && ReferenceEquals(current, source))
                {
                    image.Source = bitmap;
                    assigned = true;
                    ObservationPosterAssigned(image);
                }
            }
#if LIBRARY_OBSERVATION
            await ObservePosterDecodeAsync(bytes, width, height, token, ApplyPoster);
#else
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, token, ApplyPoster);
#endif
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException
            or TimeoutException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Keep the native placeholder when artwork is missing or cannot be decoded.
        }
        finally
        {
            load.Complete(version, assigned);
            if (_posterRequests.TryGetValue(image, out var current) && ReferenceEquals(current, source))
            {
                _posterRequests.Remove(image);
                source.Dispose();
            }
        }
    }

    private void CancelPosterRequest(Image image)
    {
        if (_posterLoads.TryGetValue(image, out var load)) load.Reset();
        CancelPosterDownload(image);
        image.Source = null;
        ObservationPosterCleared(image);
    }

    private void CancelPosterDownload(Image image)
    {
        if (_posterRequests.Remove(image, out var source))
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private void CancelPosterRequests()
    {
        // Invalidate every deferred container callback without changing any x:Bind-owned Tag.
        _posterContainers.Clear();
        foreach (var image in _posterSubscriptions.Keys.Concat(_posterRequests.Keys).Distinct().ToArray()) CancelPosterRequest(image);
        CancelPosterRequest(DetailPoster);
        CancelPosterRequest(DetailBackdrop);
    }

    private sealed class PosterContainer
    {
        public PosterRealization<MediaCardViewModel> Realization { get; } = new();
        public WeakReference<Image>? Poster { get; set; }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LibraryViewModel.CollectionRevision)) ResetCollectionScroll();
        if (args.PropertyName is nameof(LibraryViewModel.IsBusy) or nameof(LibraryViewModel.HasMore)
            or nameof(LibraryViewModel.BrowseVisibility) or nameof(LibraryViewModel.DetailItemsVisibility))
            QueueViewportUpdate();
        if (args.PropertyName is nameof(LibraryViewModel.BrowseSortKey) or nameof(LibraryViewModel.BrowseSortDescending)
            or nameof(LibraryViewModel.BrowseUnplayedOnly)) UpdateNavigation(false);
        if (args.PropertyName == nameof(LibraryViewModel.Detail))
        {
            DetailScroller.ChangeView(null, 0, null, true);
            DetailPoster.Tag = ViewModel.Detail;
            DetailBackdrop.Tag = ViewModel.Detail;
            CancelPosterRequest(DetailPoster);
            CancelPosterRequest(DetailBackdrop);
            if (ViewModel.HasDetails) await Task.WhenAll(LoadPosterAsync(DetailPoster), LoadPosterAsync(DetailBackdrop));
        }
        else if (args.PropertyName == nameof(LibraryViewModel.HasDetails))
        {
            if (ViewModel.HasDetails)
            {
                DetailPoster.Tag = ViewModel.Detail;
                DetailBackdrop.Tag = ViewModel.Detail;
                await Task.WhenAll(LoadPosterAsync(DetailPoster), LoadPosterAsync(DetailBackdrop));
            }
            else
            {
                CancelPosterRequest(DetailPoster);
                CancelPosterRequest(DetailBackdrop);
            }
        }
        else if (args.PropertyName == nameof(LibraryViewModel.DetailItemsAreEpisodes))
        {
            DetailItemsGrid.ItemTemplate = (DataTemplate)Resources[ViewModel.DetailItemsAreEpisodes ? "EpisodeCardTemplate" : "MediaCardTemplate"];
            UpdateDetailShelfSize();
        }
    }
}
