using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
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
    private readonly Dictionary<Image, PosterMetadataBinding> _posterMetadataSubscriptions = [];
    private readonly ConditionalWeakTable<GridViewItem, PosterContainer> _posterContainers = new();
    private readonly ConditionalWeakTable<Image, PosterLoadState<MediaCardViewModel>> _posterLoads = new();
    private ScrollViewer? _gridScroller;
    private bool _componentInitialized;
    private bool _updatingNavigation;
    private bool _updatingBrowseOptions;
    private string? _navigationLibraryId;
    private bool _hasSearchDraft;

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
        _componentInitialized = true;
        Loaded += LibraryAccessibility_Loaded;
        Unloaded += LibraryAccessibility_Unloaded;
        KeyDown += LibraryPresentationKeyDown;
        OverviewText.IsTextTrimmedChanged += (_, _) => UpdateOverviewAffordance();
        LibraryNavigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateFooterWidth());
        LibraryNavigation.Loaded += (_, _) => UpdateFooterWidth();
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ViewModel.Libraries.CollectionChanged += (_, _) => RebuildLibraries();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SessionExpired += ViewModel_SessionExpired;
        ViewModel.BrowseStateCapturing += CaptureBrowseViewport;
        ViewModel.BrowseStateRestored += RestoreBrowseViewport;
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
        if (!_componentInitialized) return;
        if (Footer is FrameworkElement footer)
            footer.Width = LibraryNavigation.IsPaneOpen ? LibraryNavigation.OpenPaneLength : LibraryNavigation.CompactPaneLength;
    }
    public event EventHandler<PlayItemRequestedEventArgs>? PlayRequested;
    public event EventHandler? SessionExpired;

    private void ViewModel_SessionExpired(object? sender, EventArgs args) => SessionExpired?.Invoke(this, args);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (ViewModel.DetailItemsAreEpisodes) UpdateDetailShelfSize();
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
        _searchFocusRequestVersion++;
        ObservationSessionStarted();
        ClosePersonDialog();
        MediaInformation.ClearDisclosureState();
        CancelPosterRequests();
        _hasSearchDraft = false;
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
        _searchFocusRequestVersion++;
        ClosePersonDialog();
        MediaInformation.ClearDisclosureState();
        CancelPosterRequests();
        ViewModel.ClearSession();
        _hasSearchDraft = false;
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
        if (!_componentInitialized) return;
        _updatingNavigation = true;
        try
        {
            while (LibraryNavigation.MenuItems.Count > 5) LibraryNavigation.MenuItems.RemoveAt(5);
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
        if (!_componentInitialized) return;
        if (!ViewModel.IsSearch) _searchFocusRequestVersion++;
        _updatingNavigation = true;
        _updatingBrowseOptions = true;
        try
        {
            if (!ViewModel.IsDetailLocation)
            {
                _navigationLibraryId = ViewModel.SelectedLibraryId;
                LibraryNavigation.SelectedItem = ViewModel.IsSearch ? SearchNavigationItem
                    : ViewModel.IsHome || ViewModel.IsHomeSection ? HomeNavigationItem
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
            WatchStatusSelector.SelectedIndex = ViewModel.BrowseWatchFilter switch
            {
                WatchStatusFilter.Unplayed => 1,
                WatchStatusFilter.Played => 2,
                _ => 0
            };
            if (updateSearch && !_hasSearchDraft && !ViewModel.HasPendingSearch && SearchBox.Text != ViewModel.SearchText)
                SearchBox.Text = ViewModel.SearchText;
            SearchBox.Visibility = ViewModel.IsSearch ? Visibility.Visible : Visibility.Collapsed;
            UpdateDetailBreadcrumb();
        }
        finally
        {
            _updatingNavigation = false;
            _updatingBrowseOptions = false;
        }
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void LibraryNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_componentInitialized || _updatingNavigation || args.SelectedItem is not NavigationViewItem item) return;
        if (item == SearchNavigationItem)
        {
            await OpenSearchAsync(FocusState.Programmatic);
            return;
        }
        CloseOverlayNavigation();
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
        if (!_componentInitialized || _updatingBrowseOptions || SortBox?.SelectedItem is not ComboBoxItem { Tag: string sort }
            || DescendingButton is null || WatchStatusSelector is null) return;
        var status = WatchStatusSelector.SelectedIndex switch
        {
            1 => WatchStatusFilter.Unplayed,
            2 => WatchStatusFilter.Played,
            _ => WatchStatusFilter.All
        };
        await ViewModel.SetBrowseOptionsAsync(sort, DescendingButton.IsChecked == true, status);
        UpdateNavigation();
    }

    private async void SeasonBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_componentInitialized) return;
        if (SeasonBox.SelectedItem is MediaCardViewModel season) await ViewModel.SelectSeasonAsync(season);
    }

    private void DetailHero_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_componentInitialized) return;
        UpdateDetailHeroLayout(args.NewSize.Width);
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T result) return result;
        return null;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) _hasSearchDraft = true;
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _hasSearchDraft = false;
        await ViewModel.SearchAsync(args.QueryText, false);
        UpdateNavigation(false);
    }

    private async void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Visibility != Visibility.Visible || !IsLoaded) return;
        args.Handled = true;
        await OpenSearchAsync(FocusState.Keyboard);
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Visibility != Visibility.Visible || !IsLoaded) return;
        args.Handled = true;
        await RefreshAsync();
    }

    private async void MediaGrid_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MediaCardViewModel item) return;
        _lastInvokedItemId = item.Id;
        await ViewModel.ShowItemAsync(item);
        UpdateNavigation();
        if (ViewModel.Detail.Id == item.Id) FocusCurrentDetails();
    }

    private void MediaGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is MediaCardViewModel item)
            args.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding
            {
                Source = item, Path = new PropertyPath(nameof(MediaCardViewModel.AutomationLabel)), Mode = BindingMode.OneWay
            });
        else args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
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
        var state = _posterContainers.GetValue(container, static current => new PosterContainer(current));
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
            || !ReferenceEquals(owner, current) || currentVersion != version) return;
        await LoadPosterAsync(image);
    }

    private void CancelContainerPoster(PosterContainer state, Image image)
    {
        if (_posterLoads.TryGetValue(image, out var load) && load.IsOwnedBy(state))
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
        if (ReferenceEquals(image, DetailPoster))
            return ViewModel.HasDetails && ReferenceEquals(item, ViewModel.Detail);
        if (FindPosterContainer(image) is not { } container || !_posterContainers.TryGetValue(container, out var state)
            || !state.Realization.TryGetVersion(item, out version)) return false;
        if (state.Poster is null || !state.Poster.TryGetTarget(out var previous) || !ReferenceEquals(previous, image))
            state.Poster = new WeakReference<Image>(image);
        owner = state;
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
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle)
        {
            toggle.IsChecked = ViewModel.Detail.IsFavorite;
            if (toggle.IsLoaded) toggle.Focus(FocusState.Programmatic);
        }
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
        if (!ViewModel.PlayableDetail.CanPlay || !ViewModel.CanUseSeasonActions) return;
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
        ReconnectMediaGridScroller();
        QueueViewportUpdate();
    }

    private void MediaGrid_Unloaded(object sender, RoutedEventArgs args)
    {
        DetachCollectionScroller(_gridScroller);
        _gridScroller = null;
        _wallMetrics = default;
        _wallPanelRoot = null;
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
        if (sender is Image { IsLoaded: true } image && !ReferenceEquals(image, DetailPoster)) await LoadPosterAsync(image);
    }

    private static ImageCache.ImageReference? GetPosterReference(MediaCardViewModel item, ArtworkKind kind)
    {
        var reference = ImageCache.SelectImage(item.Item, kind);
        return kind == ArtworkKind.Backdrop && reference?.Type != "Backdrop" ? null : reference;
    }

    private void ObservePosterMetadata(Image image, MediaCardViewModel item, object owner, long ownerVersion,
        ArtworkKind kind, ImageCache.ImageReference? reference)
    {
        if (_posterMetadataSubscriptions.TryGetValue(image, out var current)
            && ReferenceEquals(current.Item, item) && ReferenceEquals(current.Owner, owner)
            && current.OwnerVersion == ownerVersion && current.Kind == kind) return;
        DetachPosterMetadata(image);
        var binding = new PosterMetadataBinding(item, owner, ownerVersion, kind, reference);
        binding.Handler = async (_, args) => await PosterMetadataChangedAsync(image, binding, args);
        _posterMetadataSubscriptions.Add(image, binding);
        item.PropertyChanged += binding.Handler;
    }

    private async Task PosterMetadataChangedAsync(Image image, PosterMetadataBinding binding, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MediaCardViewModel.Item) or null or "")
            || !_posterMetadataSubscriptions.TryGetValue(image, out var current) || !ReferenceEquals(current, binding)) return;
        if (!ReferenceEquals(image.Tag, binding.Item)
            || !TryGetPosterBinding(image, binding.Item, out var owner, out var ownerVersion)
            || !ReferenceEquals(owner, binding.Owner) || ownerVersion != binding.OwnerVersion)
        {
            DetachPosterMetadata(image);
            return;
        }
        var reference = GetPosterReference(binding.Item, binding.Kind);
        if (reference == binding.Reference) return;
        // Reuse the cache's selected reference; unrelated user-data changes preserve the decoded image.
        CancelPosterRequest(image);
        await LoadPosterAsync(image);
    }

    private void DetachPosterMetadata(Image image)
    {
        if (_posterMetadataSubscriptions.Remove(image, out var binding))
            binding.Item.PropertyChanged -= binding.Handler;
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
        var reference = GetPosterReference(item, kind);
        ObservePosterMetadata(image, item, owner, ownerVersion, kind, reference);
        if (kind == ArtworkKind.Backdrop && reference is null)
        {
            ResetPosterRequest(image);
            return;
        }
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
        DetachPosterMetadata(image);
        ResetPosterRequest(image);
    }

    private void ResetPosterRequest(Image image)
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
        foreach (var image in _posterSubscriptions.Keys.Concat(_posterRequests.Keys).Concat(_posterMetadataSubscriptions.Keys).Distinct().ToArray()) CancelPosterRequest(image);
        CancelPosterRequest(DetailPoster);
    }

    private sealed class PosterContainer(GridViewItem container)
    {
        // Active image bindings keep the managed container projection alive until their existing cleanup runs.
        public GridViewItem Container { get; } = container;
        public PosterRealization<MediaCardViewModel> Realization { get; } = new();
        public WeakReference<Image>? Poster { get; set; }
    }

    private sealed class PosterMetadataBinding(MediaCardViewModel item, object owner, long ownerVersion,
        ArtworkKind kind, ImageCache.ImageReference? reference)
    {
        public MediaCardViewModel Item { get; } = item;
        public object Owner { get; } = owner;
        public long OwnerVersion { get; } = ownerVersion;
        public ArtworkKind Kind { get; } = kind;
        public ImageCache.ImageReference? Reference { get; } = reference;
        public PropertyChangedEventHandler? Handler { get; set; }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LibraryViewModel.ActivePerson) or nameof(LibraryViewModel.IsPerson)) UpdatePersonPage();
        if (args.PropertyName == nameof(LibraryViewModel.CollectionRevision)) ResetCollectionScroll();
        if (args.PropertyName is nameof(LibraryViewModel.IsBusy) or nameof(LibraryViewModel.HasMore)
            or nameof(LibraryViewModel.BrowseVisibility) or nameof(LibraryViewModel.DetailItemsVisibility))
            QueueViewportUpdate();
        if (args.PropertyName is nameof(LibraryViewModel.BrowseSortKey) or nameof(LibraryViewModel.BrowseSortDescending)
            or nameof(LibraryViewModel.BrowseWatchFilter)) UpdateNavigation(false);
        if (args.PropertyName == nameof(LibraryViewModel.Detail))
        {
            UpdateDetailPresentation();
            DetailScroller.ChangeView(null, 0, null, true);
            DetailPoster.Tag = ViewModel.Detail;
            CancelPosterRequest(DetailPoster);
            if (ViewModel.HasDetails) await LoadPosterAsync(DetailPoster);
        }
        else if (args.PropertyName == nameof(LibraryViewModel.HasDetails))
        {
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            if (ViewModel.HasDetails)
            {
                DetailPoster.Tag = ViewModel.Detail;
                await LoadPosterAsync(DetailPoster);
            }
            else
            {
                CancelPosterRequest(DetailPoster);
            }
        }
        else if (args.PropertyName == nameof(LibraryViewModel.DetailItemsAreEpisodes))
        {
            UpdateDetailShelfSize();
        }
        if (args.PropertyName == nameof(LibraryViewModel.CanGoBack)) NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
