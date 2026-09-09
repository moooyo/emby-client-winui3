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
    private bool _updatingHomeSection;

    partial void ObservationInitialize();
    partial void ObservationPosterLoaded(Image image);
    partial void ObservationPosterUnloaded(Image image);
    partial void ObservationPosterTagChanged(DependencyObject sender);
    partial void ObservationBitmapDecoded(BitmapImage bitmap);
    partial void ObservationPosterAssigned(Image image);
    partial void ObservationPosterCleared(Image image);

    public LibraryView()
    {
        InitializeComponent();
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SessionExpired += ViewModel_SessionExpired;
        ObservationInitialize();
    }

    public LibraryViewModel ViewModel { get; } = new();
    public event EventHandler<PlayItemRequestedEventArgs>? PlayRequested;
    public event EventHandler? SessionExpired;

    private void ViewModel_SessionExpired(object? sender, EventArgs args) => SessionExpired?.Invoke(this, args);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        // Cached containers can stay loaded after navigation removes their items.
        if (args.Action == NotifyCollectionChangedAction.Reset) CancelPosterRequests();
    }

    public async Task SetSessionAsync(EmbyApiClient api, string serverId, UserDto user, CancellationToken cancellationToken = default)
    {
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
        }
        finally { _updatingNavigation = false; }
    }

    private void UpdateNavigation(bool updateSearch = true)
    {
        _updatingNavigation = true;
        _updatingHomeSection = true;
        try
        {
            LibraryNavigation.SelectedItem = ViewModel.IsHome ? HomeNavigationItem
                : ViewModel.IsFavorites ? FavoritesNavigationItem
                : LibraryNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag is MediaCardViewModel card && card.Id == ViewModel.SelectedLibraryId);
            HomeSections.SelectedIndex = (int)ViewModel.SelectedHomeSection;
            if (updateSearch && !ViewModel.HasPendingSearch && SearchBox.Text != ViewModel.SearchText)
                SearchBox.Text = ViewModel.SearchText;
        }
        finally
        {
            _updatingNavigation = false;
            _updatingHomeSection = false;
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
    }

    private async void HomeSections_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingHomeSection || HomeSections.SelectedIndex is < 0 or > 2 || !ViewModel.IsHome) return;
        var section = (HomeSection)HomeSections.SelectedIndex;
        if (section != ViewModel.SelectedHomeSection) await ViewModel.ShowHomeAsync(section);
        UpdateNavigation();
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
    }

    private void MediaGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var name = !args.InRecycleQueue && args.Item is MediaCardViewModel item ? item.Title : string.Empty;
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
        if (ReferenceEquals(image, DetailPoster))
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
    private async void LoadMore_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadMoreAsync();
    private async void Favorite_Click(object sender, RoutedEventArgs args) => await ViewModel.ToggleFavoriteAsync();
    private async void Played_Click(object sender, RoutedEventArgs args) => await ViewModel.TogglePlayedAsync();
    private void Play_Click(object sender, RoutedEventArgs args) => RequestPlay(0, false);
    private void Resume_Click(object sender, RoutedEventArgs args) => RequestPlay(ViewModel.Detail.ResumeTicks, false);
    private void Queue_Click(object sender, RoutedEventArgs args) => RequestPlay(0, true);

    private void RequestPlay(long position, bool addToQueue)
    {
        if (!ViewModel.Detail.CanPlay) return;
        if (!ViewModel.IsPlaybackAllowed)
        {
            ViewModel.ErrorMessage = "Playback is disabled for this account. Contact your server administrator.";
            ViewModel.HasError = true;
            return;
        }
        PlayRequested?.Invoke(this, new(ViewModel.Detail.Item, position, addToQueue));
    }

    private void MediaGrid_Loaded(object sender, RoutedEventArgs args)
    {
        _gridScroller = FindScrollViewer(MediaGrid);
        if (_gridScroller is not null) _gridScroller.ViewChanged += GridScroller_ViewChanged;
    }

    private void MediaGrid_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_gridScroller is not null) _gridScroller.ViewChanged -= GridScroller_ViewChanged;
        _gridScroller = null;
    }

    private async void GridScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate && _gridScroller is { ScrollableHeight: > 0 } scroller
            && scroller.VerticalOffset >= scroller.ScrollableHeight - 500 && ViewModel.CanLoadMore)
            await ViewModel.LoadMoreAsync();
    }

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

    private async Task LoadPosterAsync(Image image)
    {
        if (image.Tag is not MediaCardViewModel item || !TryGetPosterBinding(image, item, out var owner, out var ownerVersion))
        {
            CancelPosterRequest(image);
            return;
        }
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Clamp(176 * scale, 176, 704);
        var height = width * 3 / 2;
        var load = _posterLoads.GetValue(image, static _ => new PosterLoadState<MediaCardViewModel>());
        if (!load.TryBegin(item, owner, ownerVersion, width, height, image.Source is not null, out var version)) return;
        CancelPosterDownload(image);
        image.Source = null;
        ObservationPosterCleared(image);
        var source = new CancellationTokenSource();
        var token = source.Token;
        _posterRequests[image] = source;
        var assigned = false;
        try
        {
            var bytes = await ViewModel.LoadPosterAsync(item, width, height, token);
            if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, token, bitmap =>
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
            });
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
    }

    private sealed class PosterContainer
    {
        public PosterRealization<MediaCardViewModel> Realization { get; } = new();
        public WeakReference<Image>? Poster { get; set; }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LibraryViewModel.Detail))
        {
            DetailPoster.Tag = ViewModel.Detail;
            CancelPosterRequest(DetailPoster);
            if (ViewModel.HasDetails) await LoadPosterAsync(DetailPoster);
        }
        else if (args.PropertyName == nameof(LibraryViewModel.HasDetails))
        {
            if (ViewModel.HasDetails)
            {
                DetailPoster.Tag = ViewModel.Detail;
                await LoadPosterAsync(DetailPoster);
            }
            else CancelPosterRequest(DetailPoster);
        }
    }
}
