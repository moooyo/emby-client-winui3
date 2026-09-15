using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class LibraryView
{
    private ScrollViewer? _detailItemsScroller;
    private bool _resetDetailItemsScroll;
    private PosterWallMetrics _wallMetrics;
    private Panel? _wallPanelRoot;
    private int _collectionLayoutVersion;
    private bool _viewportUpdateQueued;
    private int? _loadingMoreVersion;
    private bool _autoLoadStalled;
    private int _autoLoadGeneration;

    private void ResetCollectionScroll()
    {
        var version = ++_collectionLayoutVersion;
        _autoLoadStalled = false;
        // Reposition only after a successful replacement or an actual navigation.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _collectionLayoutVersion) return;
            _gridScroller?.ChangeView(null, 0, null, true);
            _detailItemsScroller?.ChangeView(0, 0, null, true);
            QueueViewportUpdate();
        });
    }

    private void MediaPosterCard_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Grid card) return;
        var isWall = ReferenceEquals(FindAncestor<GridView>(card), MediaGrid);
        card.Width = isWall ? double.NaN : 156;
        if (!isWall) card.RowDefinitions[0].Height = new GridLength(234);
    }

    private async void MediaPosterCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        RefreshTextScaleIfNeeded();
        if (sender is not Grid card || !ReferenceEquals(FindAncestor<GridView>(card), MediaGrid)
            || args.NewSize.Width <= 0) return;
        var height = args.NewSize.Width * 1.5;
        if (Math.Abs(card.RowDefinitions[0].Height.Value - height) > 0.1)
            card.RowDefinitions[0].Height = new GridLength(height);
        if (FindPoster(card) is { IsLoaded: true } image) await LoadPosterAsync(image);
    }

    private void DetailItemsGrid_Loaded(object sender, RoutedEventArgs args)
    {
        UpdateDetailShelfSize();
        ReconnectDetailItemsScroller();
        QueueViewportUpdate();
    }

    private void ReconnectDetailItemsScroller()
    {
        var scroller = FindScrollViewer(DetailItemsGrid);
        if (ReferenceEquals(_detailItemsScroller, scroller)) return;
        DetachCollectionScroller(_detailItemsScroller);
        _detailItemsScroller = scroller;
        AttachCollectionScroller(_detailItemsScroller);
    }

    private void ReconnectMediaGridScroller()
    {
        var scroller = FindScrollViewer(MediaGrid);
        if (ReferenceEquals(_gridScroller, scroller)) return;
        DetachCollectionScroller(_gridScroller);
        _gridScroller = scroller;
        _wallMetrics = default;
        _wallPanelRoot = null;
        AttachCollectionScroller(_gridScroller);
    }

    private void DetailItemsGrid_Unloaded(object sender, RoutedEventArgs args)
    {
        DetachCollectionScroller(_detailItemsScroller);
        _detailItemsScroller = null;
    }

    private void AttachCollectionScroller(ScrollViewer? scroller)
    {
        if (scroller is null) return;
        scroller.ViewChanged += GridScroller_ViewChanged;
        scroller.SizeChanged += CollectionViewport_SizeChanged;
    }

    private void DetachCollectionScroller(ScrollViewer? scroller)
    {
        if (scroller is null) return;
        scroller.ViewChanged -= GridScroller_ViewChanged;
        scroller.SizeChanged -= CollectionViewport_SizeChanged;
    }

    private void CollectionViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        RefreshTextScaleIfNeeded();
        QueueViewportUpdate();
    }

    private void QueueViewportUpdate()
    {
        if (_viewportUpdateQueued || !IsLoaded || Visibility != Visibility.Visible) return;
        _viewportUpdateQueued = true;
        var version = _collectionLayoutVersion;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (version != _collectionLayoutVersion)
            {
                _viewportUpdateQueued = false;
                QueueViewportUpdate();
                return;
            }
            try
            {
                if (ViewModel.BrowseVisibility == Visibility.Visible)
                {
                    MediaGrid.UpdateLayout();
                    // A hidden grid can load before its template creates the scroll viewer.
                    ReconnectMediaGridScroller();
                    UpdatePosterWallLayout();
                    MediaGrid.UpdateLayout();
                }
                else if (ViewModel.DetailItemsVisibility == Visibility.Visible)
                {
                    DetailItemsGrid.UpdateLayout();
                    ReconnectDetailItemsScroller();
                    if (_resetDetailItemsScroll && _detailItemsScroller is { IsLoaded: true } scroller)
                    {
                        _resetDetailItemsScroll = false;
                        scroller.ChangeView(0, 0, null, true);
                    }
                }
            }
            finally { _viewportUpdateQueued = false; }
            await LoadNextPageIfNeededAsync(version);
        }))
        {
            _viewportUpdateQueued = false;
        }
    }

    private void UpdatePosterWallLayout()
    {
        if (_gridScroller is null || MediaGrid.ItemsPanelRoot is not { } panelRoot)
        {
            _wallMetrics = default;
            _wallPanelRoot = null;
            return;
        }
        if (!ReferenceEquals(_wallPanelRoot, panelRoot))
        {
            _wallPanelRoot = panelRoot;
            _wallMetrics = default;
        }
        // Native AOT can return the base Panel projection for the known wrap-grid template.
        var panel = panelRoot.As<ItemsWrapGrid>();
        var measuredWidth = LayoutInformation.GetAvailableSize(panelRoot).Width;
        var viewportWidth = _gridScroller.ViewportWidth;
        if (!double.IsFinite(measuredWidth) || measuredWidth <= 0
            || !double.IsFinite(viewportWidth) || viewportWidth <= 0) return;
        // The wrap grid uses its measure constraint, which can be narrower than the arranged viewport.
        var availableWidth = Math.Min(measuredWidth, viewportWidth) - panel.GroupPadding.Left - panel.GroupPadding.Right;
        var metrics = PosterWallLayout.Calculate(availableWidth, XamlRoot?.RasterizationScale ?? 1, _textScaleFactor);
        if (metrics.Columns == 0 || metrics == _wallMetrics) return;
        _wallMetrics = metrics;
        panel.MaximumRowsOrColumns = metrics.Columns;
        panel.ItemWidth = metrics.ItemWidth;
        panel.ItemHeight = metrics.ItemHeight;
    }

    private async Task LoadNextPageIfNeededAsync(int version)
    {
        if (_autoLoadGeneration != ViewModel.NavigationRevision)
        {
            _autoLoadGeneration = ViewModel.NavigationRevision;
            _autoLoadStalled = false;
        }
        if (version != _collectionLayoutVersion || _loadingMoreVersion == version || _autoLoadStalled || !IsLoaded
            || Visibility != Visibility.Visible || ViewModel.HasError || !ViewModel.CanLoadMore || ViewModel.Items.Count == 0) return;
        var isWall = ViewModel.BrowseVisibility == Visibility.Visible;
        var scroller = isWall ? _gridScroller
            : ViewModel.DetailItemsVisibility == Visibility.Visible ? _detailItemsScroller : null;
        if (scroller is null || !scroller.IsLoaded) return;
        var vertical = isWall || UseEpisodeList;
        var viewport = vertical ? scroller.ViewportHeight : scroller.ViewportWidth;
        var extent = vertical ? scroller.ScrollableHeight : scroller.ScrollableWidth;
        var offset = vertical ? scroller.VerticalOffset : scroller.HorizontalOffset;
        if (viewport <= 0 || extent - offset > Math.Max(360, viewport * 0.75)) return;

        _loadingMoreVersion = version;
        var itemCount = ViewModel.Items.Count;
        var outcome = PageLoadMoreOutcome.NotStarted;
        try
        {
            outcome = await ViewModel.LoadMoreAsync();
            // An unchanged page must not create an unbounded automatic request loop.
            if (version == _collectionLayoutVersion && outcome == PageLoadMoreOutcome.Repeated)
                _autoLoadStalled = true;
        }
        finally
        {
            if (_loadingMoreVersion == version) _loadingMoreVersion = null;
            if (version == _collectionLayoutVersion && (ViewModel.Items.Count > itemCount
                || outcome == PageLoadMoreOutcome.Canceled)) QueueViewportUpdate();
        }
    }
}
