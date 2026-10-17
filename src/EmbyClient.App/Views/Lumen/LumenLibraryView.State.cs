using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly ConditionalWeakTable<BrowseViewportState, SearchPresentationState> _searchViewports = new();
    private bool _responsiveLayoutQueued;
    private bool _wallViewportRefreshQueued;
    private bool _wallViewportIsIntermediate;
    private WallViewportRefresh? _wallViewportPendingRefresh;
    private WallViewportRefresh? _wallViewportLastRefresh;
    private WallViewportRefresh? _wallViewportObservation;
    private readonly bool _wallViewportTraceEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable));
    private int _wallViewportTraceRemaining = 128;
    private sealed record SearchPresentationState(string Tab);
    private sealed record WallViewportRefresh(ConnectedSession Session, int SessionGeneration, long RenderEpoch,
        int NavigationRevision, double Offset, double Width, double Height, int ItemCount);
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LibraryViewModel.CollectionRevision))
        {
            _autoPageBlocked = false;
            if (_renderEnabled && _renderLoaded && !_restorePending) _scroll.ChangeView(null, 0, null, true);
        }
        QueueRender();
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRender();

    private void HomeRowsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        foreach (var shelf in _observedShelves.Where(shelf => !ViewModel.HomeRows.Contains(shelf)).ToArray())
        {
            shelf.Items.CollectionChanged -= ItemsChanged;
            _observedShelves.Remove(shelf);
        }
        foreach (var shelf in ViewModel.HomeRows)
            if (_observedShelves.Add(shelf)) shelf.Items.CollectionChanged += ItemsChanged;
        QueueRender();
    }

    private void ActivateRenderLifecycle()
    {
        if (_renderEnabled || !_renderLoaded || _session is null) return;
        _renderEpoch++;
        _renderQueued = false;
        _renderEnabled = true;
    }

    private void InvalidateRenderLifecycle()
    {
        _renderEnabled = false;
        _renderEpoch++;
        _renderQueued = false;
        _responsiveLayoutQueued = false;
        _restorePending = false;
        _wallViewportRefreshQueued = false;
        _wallViewportIsIntermediate = false;
        _wallViewportPendingRefresh = _wallViewportLastRefresh = null;
        if (_wallViewportObservation is not null) _wallRepeater.LayoutUpdated -= ObserveWallViewportRefresh;
        _wallViewportObservation = null;
    }

    private void QueueRender()
    {
        // Managed ownership must be checked before a shutdown drain can touch native UI.
        if (!_renderEnabled || !_renderLoaded || _renderQueued || _session is not { } session) return;
        var epoch = _renderEpoch;
        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // An old epoch cannot release a replacement session's coalescing flag.
            if (epoch != _renderEpoch) return;
            _renderQueued = false;
            if (!_renderEnabled || !_renderLoaded || !ReferenceEquals(session, _session)) return;
            RenderPage();
        }) && epoch == _renderEpoch) _renderQueued = false;
    }

    private void RenderPage()
    {
        var mode = ViewModel.HasDetails ? "detail" : ViewModel.IsPerson ? "person"
            : ViewModel.IsHome ? "home" : ViewModel.IsSearch ? "search" : "wall";
        var changed = mode != _renderedPage;
        _detail.Visibility = mode == "detail" ? Visibility.Visible : Visibility.Collapsed;
        _home.Visibility = mode == "home" ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = mode is "detail" or "home" ? Visibility.Collapsed : Visibility.Visible;
        _ambience.Visibility = mode is "wall" or "search" or "person" ? Visibility.Visible : Visibility.Collapsed;
        _ambience.Height = mode == "search" ? 560 : mode == "person" ? 620 : 640;
        if (mode != "wall") _alphabet.Visibility = Visibility.Collapsed;
        if (mode != "person") DetachPerson();
        if (mode != "search") DetachSearchCards();
        if (changed)
        {
            _renderedPage = mode;
            _page.Children.Clear();
            if (mode == "wall") _page.Children.Add(_wall);
            else if (mode == "search") _page.Children.Add(_search);
            else if (mode == "person") _page.Children.Add(BuildPerson());
            if (!_restorePending) _scroll.ChangeView(null, 0, null, true);
            if (mode == "home") ResetHomePointerFocus();
            if (mode == "home" && !_restorePending) _homeDeck.GoToPage(0, false);
            if (mode is not ("detail" or "home")) LumenUi.AnimateEntrance(_page);
            else if (mode == "detail") LumenUi.AnimateEntrance(_detail);
        }
        if (mode == "home") RenderHome();
        else if (mode == "wall")
        {
            RenderWall();
            if (!ViewModel.IsBusy && _session is not null && _genreNavigationRevision != ViewModel.NavigationRevision)
            {
                _genreNavigationRevision = ViewModel.NavigationRevision;
                _ = LoadWallGenresAsync(_genreNavigationRevision, _sessionGeneration);
            }
        }
        else if (mode == "search") RenderSearch();
        else if (mode == "detail") _detail.Refresh();
        else RenderPerson();
        UpdateActiveNavigation();
        _loading.Visibility = ViewModel.IsBusy || ViewModel.SearchPeopleIsBusy || mode == "person" && _personModel is { } person && (person.IsLoadingBiography || person.IsLoadingWorks) ? Visibility.Visible : Visibility.Collapsed;
        _notice.Visibility = ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
        _noticeText.Text = LumenText.Get(ViewModel.ErrorMessage);
        var emptySearch = ViewModel.IsSearch && ViewModel.HasSearchQuery && !ViewModel.SearchPeopleIsBusy
            && (_searchTab == "people" ? ViewModel.SearchPeople.Count == 0 && ViewModel.SearchPeopleError.Length == 0
                : ViewModel.Items.Count == 0 && (_searchTab != "all" || ViewModel.SearchPeople.Count == 0));
        var isEmpty = !ViewModel.IsBusy && !ViewModel.HasError && !IsDetail
            && (ViewModel.IsSearch ? emptySearch : !ViewModel.HasItems);
        _empty.Visibility = isEmpty && mode is not ("home" or "wall" or "search") ? Visibility.Visible : Visibility.Collapsed;
        _emptyTitle.Text = LumenText.Get(ViewModel.IsSearch ? "No results" : ViewModel.IsFavorites ? "No favorites yet" : "Your library is empty");
        _emptyMessage.Text = LumenText.Get(ViewModel.EmptyMessage);
        UpdateHeroTimer();
        UpdateResponsiveLayout();
        if (changed) NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateActiveNavigation()
    {
        var previous = ActiveNavigation;
        if (ViewModel.HasDetails)
        {
            ActiveNavigation = NavigationKeyForMediaType(ViewModel.Detail.Item.Type is "Series" or "Season" or "Episode" ? "Series" : "Movie");
        }
        else if (ViewModel.IsPerson) ActiveNavigation = "person";
        else if (ViewModel.IsHome || ViewModel.IsHomeSection) ActiveNavigation = "home";
        else if (ViewModel.IsSearch) ActiveNavigation = "search";
        else if (ViewModel.IsFavorites) ActiveNavigation = "favs";
        else if (ViewModel.BrowseMediaType is "Movie") ActiveNavigation = NavigationKeyForMediaType("Movie");
        else if (ViewModel.BrowseMediaType is "Series") ActiveNavigation = NavigationKeyForMediaType("Series");
        else if (ViewModel.SelectedLibraryId is { Length: > 0 } selectedLibrary)
        {
            var library = ViewModel.Libraries.FirstOrDefault(item => item.Id == selectedLibrary);
            ActiveNavigation = library is not null ? LibraryViewModel.PrimaryNavigationForLibrary(library.Item.CollectionType)
                ?? "library:" + selectedLibrary : "library:" + selectedLibrary;
        }
        if (previous != ActiveNavigation) NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NavigationKeyForMediaType(string mediaType) => mediaType == "Series" ? "series" : "movies";

    private async Task OpenItemAsync(MediaCardViewModel item)
    {
        _lastInvokedId = item.Id;
        CloseBrowseFlyouts();
        await ViewModel.ShowItemAsync(item);
        QueueRender();
        DispatcherQueue.TryEnqueue(FocusCurrentDetails);
    }

    private async Task OpenPersonAsync(MediaCardViewModel person)
    {
        _lastInvokedId = person.Id;
        await ViewModel.ShowPersonAsync(person);
        QueueRender();
    }

    private async void ForwardPlay(LumenPlayRequestEventArgs args)
    {
        if (!ViewModel.IsPlaybackAllowed) return;
        var item = new MediaCardViewModel(args.Item);
        if (!item.CanPlay && item.Item.Type is "Series" or "Season")
        {
            var generation = _sessionGeneration;
            var opening = OpenItemAsync(item);
            var navigationRevision = ViewModel.NavigationRevision;
            await opening;
            if (generation != _sessionGeneration || navigationRevision != ViewModel.NavigationRevision) return;
            var playable = await ViewModel.WaitForPlaybackRecommendationAsync(item.Id, navigationRevision);
            if (playable is null || generation != _sessionGeneration || navigationRevision != ViewModel.NavigationRevision) return;
            args = new(playable.Item, playable.CanResume ? playable.ResumeTicks : args.StartPositionTicks, args.AddToQueue,
                args.MediaSourceId, args.AudioStreamIndex, args.SubtitleStreamIndex);
        }
        else if (!item.CanPlay) return;
        _lastInvokedId = args.Item.Id;
        _heroTimer.Stop();
        PlayRequested?.Invoke(this, args);
    }

    private LumenMediaCard MakeCard(MediaCardViewModel item, bool landscape = false, double width = 160, string? query = null)
    {
        var card = new LumenMediaCard(item, ViewModel, landscape);
        card.Tag = item;
        card.SetCardWidth(width);
        card.SetShowWatched(_preferences.ShowWatchedMarks);
        card.SetTitleHighlight(query);
        card.OpenRequested += async (_, selected) => await OpenItemAsync(selected);
        card.PlayRequested += (_, args) => ForwardPlay(args);
        card.FavoriteRequested += async (_, selected) => await ViewModel.ToggleItemFavoriteAsync(selected);
        card.WatchedRequested += async (_, selected) => await ViewModel.ToggleItemPlayedAsync(selected);
        card.MediaInformationRequested += async (_, selected) => await OpenItemMediaInformationAsync(selected);
        card.CollectionRequested += async (_, selected) => await _detail.AddItemToCollectionAsync(selected);
        card.RemoveFromContinueWatchingRequested += async (_, selected) => await ViewModel.RemoveFromContinueWatchingAsync(selected);
        card.HoverRequested += (_, selected) => SetAmbience(selected);
        card.Loaded += (_, _) => _realizedCards.Add(card);
        card.Unloaded += (_, _) => _realizedCards.Remove(card);
        return card;
    }

    private Task OpenItemMediaInformationAsync(MediaCardViewModel item)
    {
        _lastInvokedId = item.Id;
        CloseBrowseFlyouts();
        return _detail.ShowItemMediaInformationAsync(item);
    }

    private void SetAmbience(MediaCardViewModel? item)
    {
        if (item is null || _renderedPage is "home" or "detail") return;
        UpdateAlphabet(item);
        if (item.Id == _ambienceId) return;
        _ambienceId = item.Id;
        _ambienceArtwork.Set(item, ViewModel);
    }

    private static LinearGradientBrush BackgroundFade() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = WithAlpha(LumenTheme.Brush("Background").Color, LumenTheme.IsDark ? (byte)51 : (byte)64) },
            new GradientStop { Offset = 0.55, Color = WithAlpha(LumenTheme.Brush("Background").Color, LumenTheme.IsDark ? (byte)184 : (byte)191) },
            new GradientStop { Offset = 1, Color = LumenTheme.Brush("Background").Color }
        }
    };

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private async void LibraryKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled) return;
        if (HandleHomeNavigationKey(args)) return;
        if (args.Key == Windows.System.VirtualKey.Escape && CanGoBack)
        {
            args.Handled = true;
            await NavigateBackAsync();
        }
    }

    private void CaptureViewport(object? sender, EventArgs args)
    {
        if (!_renderEnabled || !_renderLoaded || _session is null) return;
        if (ViewModel.ActivePerson is { } person) person.ScrollOffset = _scroll.VerticalOffset;
        ViewModel.ViewportState = new(VerticalOffset: ViewModel.IsHome ? HomeVerticalOffset : _scroll.VerticalOffset, DetailOffset: _detail.ScrollOffset,
            FocusedItemId: _lastInvokedId, CollectionWidth: ViewModel.IsHome ? ActualWidth : _scroll.ViewportWidth,
            HomePageIndex: _homeDeck.SelectedIndex, DetailPageIndex: _detail.SelectedPageIndex);
        if (ViewModel.IsSearch) _searchViewports.Add(ViewModel.ViewportState, new(_searchTab));
        if (ViewModel.IsHome) CaptureHomeViewport(ViewModel.ViewportState);
        _lastInvokedId = null;
    }

    private void RestoreViewport(object? sender, EventArgs args)
    {
        if (!_renderEnabled || !_renderLoaded || _session is not { } session) return;
        var epoch = _renderEpoch;
        _restorePending = true;
        _autoPageBlocked = false;
        if (ViewModel.IsSearch)
        {
            _searchInputVersion++;
            _searchTabVersion++;
            _searchTypeOperation = Task.CompletedTask;
            _searchTab = _searchViewports.TryGetValue(ViewModel.ViewportState, out var presentation) ? presentation.Tab
                : ViewModel.BrowseSearchType switch { "Movie" => "movies", "Series" => "series", _ => "all" };
            SetSearchDraft(ViewModel.SearchText);
        }
        var revision = ViewModel.NavigationRevision;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (epoch != _renderEpoch) return;
            if (!_renderEnabled || !_renderLoaded || !ReferenceEquals(session, _session)
                || revision != ViewModel.NavigationRevision) { _restorePending = false; return; }
            RenderPage();
            ApplyResponsiveLayout();
            _scroll.UpdateLayout();
            if (ViewModel.IsHome) RestoreHomeViewport();
            else _scroll.ChangeView(null, ViewModel.IsPerson ? ViewModel.ActivePerson?.ScrollOffset ?? 0 : ViewModel.ViewportState.VerticalOffset, null, true);
            if (ViewModel.HasDetails) _detail.SelectedPageIndex = ViewModel.ViewportState.DetailPageIndex;
            else
            {
                _scroll.UpdateLayout();
                if (ViewModel.ViewportState.FocusedItemId is { } focusedId) FindItemButton(ViewModel.IsHome ? _home : _page, focusedId)?.Focus(FocusState.Programmatic);
            }
            _restorePending = false;
        }) && epoch == _renderEpoch) _restorePending = false;
    }

    private async void ScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        _wallViewportIsIntermediate = args.IsIntermediate;
        if (_lastHeaderScrolled != IsHeaderScrolled)
        {
            _lastHeaderScrolled = IsHeaderScrolled;
            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        }
        if (!args.IsIntermediate) QueueWallViewportRefresh();
        if (!_renderEnabled || !_renderLoaded || args.IsIntermediate || _restorePending || _autoPageBlocked
            || _scroll.ScrollableHeight - _scroll.VerticalOffset > 700) return;
        if (ViewModel.IsPerson && ViewModel.ActivePerson is { CanLoadMore: true } person)
        {
            await person.LoadMoreAsync();
            return;
        }
        if (_renderedPage is not ("wall" or "search") || !ViewModel.CanLoadMore || _renderedPage == "search" && _searchTab == "people") return;
        var outcome = await ViewModel.LoadMoreAsync();
        _autoPageBlocked = outcome is PageLoadMoreOutcome.Failed or PageLoadMoreOutcome.Repeated;
    }

    private void QueueWallViewportRefresh()
    {
        if (!_renderEnabled || !_renderLoaded || _restorePending || _renderedPage != "wall" || _session is not { } session) return;
        var request = new WallViewportRefresh(session, _sessionGeneration, _renderEpoch, ViewModel.NavigationRevision,
            _scroll.VerticalOffset, _scroll.ViewportWidth, _scroll.ViewportHeight, ViewModel.Items.Count);
        if (!CanApplyWallViewportRefresh(request) || !double.IsFinite(request.Offset)
            || !double.IsFinite(request.Width) || !double.IsFinite(request.Height) || request.Width <= 0 || request.Height <= 0) return;
        if (_wallViewportRefreshQueued) { _wallViewportPendingRefresh = request; return; }
        // A measure pass can report the same final position again; do not create a layout loop.
        if (_wallViewportLastRefresh == request) return;
        _wallViewportPendingRefresh = request;
        _wallViewportRefreshQueued = true;
        TraceWallViewportRefresh("queued", request, true);
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // An expired callback must not consume a replacement epoch's pending request.
            if (request.RenderEpoch != _renderEpoch) { TraceWallViewportRefresh("skipped-epoch", request, false); return; }
            _wallViewportRefreshQueued = false;
            var latest = _wallViewportPendingRefresh;
            _wallViewportPendingRefresh = null;
            if (latest is null) return;
            if (!CanApplyWallViewportRefresh(latest)) { TraceWallViewportRefresh("skipped-current", latest, false); return; }
            if (_wallViewportLastRefresh == latest) { TraceWallViewportRefresh("skipped-duplicate", latest, false); return; }
            _wallViewportLastRefresh = latest;
            if (_wallViewportTraceEnabled && _wallViewportTraceRemaining > 0)
            {
                if (_wallViewportObservation is null) _wallRepeater.LayoutUpdated += ObserveWallViewportRefresh;
                _wallViewportObservation = latest;
            }
            // Let the next normal layout pass realize the settled viewport; never force nested layout.
            _wallRepeater.InvalidateMeasure();
            TraceWallViewportRefresh("applied", latest, true);
        }))
        {
            _wallViewportRefreshQueued = false;
            _wallViewportPendingRefresh = null;
            TraceWallViewportRefresh("skipped-dispatch", request, false);
        }
    }

    private bool CanApplyWallViewportRefresh(WallViewportRefresh request) => _renderEnabled && _renderLoaded && !_restorePending
        && !_wallViewportIsIntermediate && ReferenceEquals(request.Session, _session) && request.SessionGeneration == _sessionGeneration
        && request.RenderEpoch == _renderEpoch && request.NavigationRevision == ViewModel.NavigationRevision
        && _renderedPage == "wall" && Visibility == Visibility.Visible && _wallRepeater.IsLoaded && _wallRepeater.Visibility == Visibility.Visible
        && request.Offset == _scroll.VerticalOffset && request.Width == _scroll.ViewportWidth && request.Height == _scroll.ViewportHeight
        && request.ItemCount == ViewModel.Items.Count;

    private void ObserveWallViewportRefresh(object? sender, object args)
    {
        var request = _wallViewportObservation;
        if (request is null) return;
        _wallViewportObservation = null;
        _wallRepeater.LayoutUpdated -= ObserveWallViewportRefresh;
        TraceWallViewportRefresh(CanApplyWallViewportRefresh(request) ? "arranged" : "skipped-arrange", request, true);
    }

    private void TraceWallViewportRefresh(string phase, WallViewportRefresh request, bool sampleRealized)
    {
        if (!_wallViewportTraceEnabled || _wallViewportTraceRemaining <= 0) return;
        _wallViewportTraceRemaining--;
        try
        {
            var sampled = sampleRealized && CanApplyWallViewportRefresh(request);
            var firstIndex = -1;
            FrameworkElement? first = null;
            if (sampled)
            {
                for (var childIndex = 0; childIndex < VisualTreeHelper.GetChildrenCount(_wallRepeater); childIndex++)
                {
                    if (VisualTreeHelper.GetChild(_wallRepeater, childIndex) is not FrameworkElement element) continue;
                    var index = _wallRepeater.GetElementIndex(element);
                    if (index < 0 || firstIndex >= 0 && index >= firstIndex) continue;
                    firstIndex = index;
                    first = element;
                }
            }
            var boundsElement = LumenMediaCard.UnwrapNavigationElement(first);
            var bounds = boundsElement is null ? default : boundsElement.TransformToVisual(_scroll)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, boundsElement.ActualWidth, boundsElement.ActualHeight));
            AcceptanceDiagnostics.Trace(FormattableString.Invariant($"wall-viewport phase={phase} epoch={request.RenderEpoch} generation={request.SessionGeneration} revision={request.NavigationRevision} offset={request.Offset:0.###} viewport={request.Width:0.###}x{request.Height:0.###} items={request.ItemCount} sampled={sampled} firstIndex={firstIndex} firstBounds={bounds.X:0.###},{bounds.Y:0.###},{bounds.Width:0.###},{bounds.Height:0.###}"));
        }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "LibraryWall/ViewportTrace"); }
    }

    private void UpdateResponsiveLayout()
    {
        if (!_renderEnabled || !_renderLoaded || _responsiveLayoutQueued || _session is not { } session) return;
        var epoch = _renderEpoch;
        _responsiveLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (epoch != _renderEpoch) return;
            _responsiveLayoutQueued = false;
            if (!_renderEnabled || !_renderLoaded || !ReferenceEquals(session, _session)) return;
            ApplyResponsiveLayout();
        }) && epoch == _renderEpoch) _responsiveLayoutQueued = false;
    }

    private void ApplyResponsiveLayout()
    {
        // Content and scrollbar measurement must not feed back into the outer viewport's card widths.
        var width = ActualWidth;
        if (!double.IsFinite(width) || width <= 0) return;
        _pageMargin = Math.Clamp(width * .04, 24, 56);
        _notice.Margin = new Thickness(_pageMargin, 96, _pageMargin, 0);
        if (_renderedPage == "search")
        {
            UpdateSearchLayout(width);
            return;
        }
        UpdateHomeLayout(width);
        UpdateWallLayout(width);
        UpdateSearchLayout(width);
        UpdatePersonLayout(width);
    }

    private static Button? FindItemButton(DependencyObject root, string itemId)
    {
        if (root is Button { Tag: MediaCardViewModel tagged } button && tagged.Id == itemId) return button;
        if (root is LumenMediaCard card && card.Item.Id == itemId) return FirstButton(card);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindItemButton(VisualTreeHelper.GetChild(root, index), itemId) is { } child) return child;
        return null;
    }

    private static Button? FirstButton(DependencyObject root)
    {
        if (root is Button { IsEnabled: true, Visibility: Visibility.Visible } button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FirstButton(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }
}
