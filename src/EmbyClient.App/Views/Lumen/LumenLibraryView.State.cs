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
    private sealed record SearchPresentationState(string Tab);
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
        _scroll.Visibility = mode == "detail" ? Visibility.Collapsed : Visibility.Visible;
        _ambience.Visibility = mode is "wall" or "search" or "person" ? Visibility.Visible : Visibility.Collapsed;
        _alphabet.Visibility = mode == "wall" && ViewModel.BrowseSortKey == "SortName" ? Visibility.Visible : Visibility.Collapsed;
        if (mode != "person") DetachPerson();
        if (mode != "search") DetachSearchCards();
        if (changed)
        {
            _renderedPage = mode;
            _page.Children.Clear();
            if (mode == "home") _page.Children.Add(_home);
            else if (mode == "wall") _page.Children.Add(_wall);
            else if (mode == "search") _page.Children.Add(_search);
            else if (mode == "person") _page.Children.Add(BuildPerson());
            if (!_restorePending) _scroll.ChangeView(null, 0, null, true);
            if (mode != "detail") LumenUi.AnimateEntrance(_page);
            else LumenUi.AnimateEntrance(_detail);
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
        _empty.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
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
            if (ViewModel.Detail.Item.Type is "Series" or "Season" or "Episode") ActiveNavigation = "series";
            else if (ViewModel.Detail.Item.Type == "Movie") ActiveNavigation = "movies";
        }
        else if (ViewModel.IsHome || ViewModel.IsHomeSection) ActiveNavigation = "home";
        else if (ViewModel.IsSearch) ActiveNavigation = "search";
        else if (ViewModel.IsFavorites) ActiveNavigation = "favs";
        else if (ViewModel.BrowseMediaType is "Movie") ActiveNavigation = "movies";
        else if (ViewModel.BrowseMediaType is "Series") ActiveNavigation = "series";
        if (previous != ActiveNavigation) NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

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
        card.HoverRequested += (_, selected) => SetAmbience(selected);
        card.Loaded += (_, _) => _realizedCards.Add(card);
        card.Unloaded += (_, _) => _realizedCards.Remove(card);
        return card;
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
        ViewModel.ViewportState = new(VerticalOffset: _scroll.VerticalOffset, DetailOffset: _detail.ScrollOffset,
            FocusedItemId: _lastInvokedId, CollectionWidth: _scroll.ViewportWidth);
        if (ViewModel.IsSearch) _searchViewports.Add(ViewModel.ViewportState, new(_searchTab));
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
                : ViewModel.BrowseSearchType switch { "Movie" => "movies", "Series" => "series", "BoxSet" => "collections", _ => "all" };
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
            _scroll.ChangeView(null, ViewModel.IsPerson ? ViewModel.ActivePerson?.ScrollOffset ?? 0 : ViewModel.ViewportState.VerticalOffset, null, true);
            if (ViewModel.HasDetails) _detail.ScrollOffset = ViewModel.ViewportState.DetailOffset;
            else
            {
                _scroll.UpdateLayout();
                if (ViewModel.ViewportState.FocusedItemId is { } focusedId) FindItemButton(_page, focusedId)?.Focus(FocusState.Programmatic);
            }
            _restorePending = false;
        }) && epoch == _renderEpoch) _restorePending = false;
    }

    private async void ScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
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
        _pageMargin = width < 1000 ? 32 : 56;
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
