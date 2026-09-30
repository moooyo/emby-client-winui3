using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private (double Width, int Columns, string Page, string Tab, int Revision, int People, int Collections)? _searchLayoutKey;

    private async Task LoadCollectionPreviewAsync(Grid preview, MediaCardViewModel collection, CancellationToken cancellationToken)
    {
        var session = _session;
        var generation = _sessionGeneration;
        if (session is null) return;
        try
        {
            if (!_collectionPreviews.TryGetValue(collection.Id, out var items))
            {
                var result = await session.Api.GetItemsAsync(new ItemQuery
                {
                    ParentId = collection.Id, Limit = 3, Recursive = false, Fields = ["PrimaryImageAspectRatio"],
                    EnableImageTypes = ["Primary"], ImageTypeLimit = 1
                }, cancellationToken);
                if (generation != _sessionGeneration || cancellationToken.IsCancellationRequested) return;
                items = result.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Select(item => new MediaCardViewModel(item)).ToArray();
                _collectionPreviews[collection.Id] = items;
            }
            if (cancellationToken.IsCancellationRequested || !preview.IsLoaded || generation != _sessionGeneration || items.Length == 0) return;
            preview.Children.Clear();
            for (var index = 0; index < items.Length; index++)
            {
                var artwork = new LumenArtwork { Width = 36, Height = 54, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(index * 20, 0, 0, 0) };
                artwork.Set(items[index], ViewModel, ArtworkKind.Poster, 160);
                preview.Children.Add(artwork);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
        {
            if (generation == _sessionGeneration && !cancellationToken.IsCancellationRequested) SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException)
        {
            // A collection's own poster remains available when child artwork cannot be loaded.
        }
    }

    private void UpdateSearchLayout(double width)
    {
        var layoutKey = (width, _columns, _renderedPage, _searchTab, _searchLayoutRevision, _searchPeoplePreviewCount, _searchCollectionPreviewCount);
        if (_searchLayoutKey is { } applied && applied.Equals(layoutKey)) return;
        _searchLayoutKey = layoutKey;
        var margin = new Thickness(_pageMargin, 96, _pageMargin + 8, 64);
        if (!_search.Margin.Equals(margin)) _search.Margin = margin;
        var hintVisibility = width < 1120 ? Visibility.Collapsed : Visibility.Visible;
        if (_searchHint.Visibility != hintVisibility) _searchHint.Visibility = hintVisibility;
        var hasBest = _searchFeatured.Children.Any(child => !ReferenceEquals(child, _searchSecondary));
        var compact = width < 1120 || !hasBest;
        var featuredColumnSpacing = compact ? 0d : 28;
        var featuredRowSpacing = compact && hasBest && _searchSecondary.Children.Count > 0 ? 28d : 0;
        if (_searchFeatured.ColumnSpacing != featuredColumnSpacing) _searchFeatured.ColumnSpacing = featuredColumnSpacing;
        if (_searchFeatured.RowSpacing != featuredRowSpacing) _searchFeatured.RowSpacing = featuredRowSpacing;
        SetSearchTrackWidth(_searchFeatured.ColumnDefinitions[0], compact ? new GridLength(1, GridUnitType.Star) : new GridLength(600));
        SetSearchTrackWidth(_searchFeatured.ColumnDefinitions[1], compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star));
        SetSearchGridPosition(_searchSecondary, compact ? 0 : 1, compact && hasBest ? 1 : 0);
        var usable = Math.Max(1, width - _pageMargin * 2 - 8);
        if (_search.Width != usable) _search.Width = usable;
        if (_search.HorizontalAlignment != HorizontalAlignment.Left) _search.HorizontalAlignment = HorizontalAlignment.Left;
        var secondaryWidth = compact ? usable : usable - 600 - _searchFeatured.ColumnSpacing;
        var combinedHeight = 88 + _searchPeoplePreviewCount * 64 + _searchCollectionPreviewCount * 72
            + Math.Max(0, _searchPeoplePreviewCount - 1) * 8 + Math.Max(0, _searchCollectionPreviewCount - 1) * 8;
        var splitSecondary = _searchPeopleSection is not null && _searchCollectionsSection is not null
            && _searchCollectionPreviewCount > 1 && combinedHeight > 338 && secondaryWidth >= 600;
        var secondaryColumnSpacing = splitSecondary ? 20d : 0;
        var secondaryRowSpacing = !splitSecondary && _searchPeopleSection is not null && _searchCollectionsSection is not null ? 20d : 0;
        if (_searchSecondary.ColumnSpacing != secondaryColumnSpacing) _searchSecondary.ColumnSpacing = secondaryColumnSpacing;
        if (_searchSecondary.RowSpacing != secondaryRowSpacing) _searchSecondary.RowSpacing = secondaryRowSpacing;
        SetSearchTrackWidth(_searchSecondary.ColumnDefinitions[1], splitSecondary ? new GridLength(1, GridUnitType.Star) : new GridLength(0));
        if (_searchPeopleSection is not null) SetSearchGridPosition(_searchPeopleSection, 0, 0);
        if (_searchCollectionsSection is not null)
            SetSearchGridPosition(_searchCollectionsSection, splitSecondary ? 1 : 0, !splitSecondary && _searchPeopleSection is not null ? 1 : 0);
        var columns = Math.Clamp(Math.Min(_columns, (int)(usable / 115)), 2, 10);
        var cardWidth = Math.Max(80, Math.Floor((usable - 20 * (columns - 1)) / columns * 4) / 4);
        _searchCardWidth = cardWidth;
        if (_searchRepeater.Width != usable) _searchRepeater.Width = usable;
        if (_searchRepeater.HorizontalAlignment != HorizontalAlignment.Left) _searchRepeater.HorizontalAlignment = HorizontalAlignment.Left;
        if (_searchGridLayout.MaximumRowsOrColumns != columns) _searchGridLayout.MaximumRowsOrColumns = columns;
        if (_searchGridLayout.MinItemWidth != cardWidth) _searchGridLayout.MinItemWidth = cardWidth;
        var cardHeight = cardWidth * 1.5 + 48;
        if (_searchGridLayout.MinItemHeight != cardHeight) _searchGridLayout.MinItemHeight = cardHeight;
        if (_renderedPage == "search")
            foreach (var card in _realizedCards)
                if (card.Width != cardWidth) card.SetCardWidth(cardWidth);
    }

    private static void SetSearchTrackWidth(ColumnDefinition track, GridLength width)
    {
        if (!track.Width.Equals(width)) track.Width = width;
    }

    private static void SetSearchGridPosition(FrameworkElement element, int column, int row)
    {
        if (Grid.GetColumn(element) != column) Grid.SetColumn(element, column);
        if (Grid.GetRow(element) != row) Grid.SetRow(element, row);
    }

    private static void HideNativeSearchClearButton(DependencyObject element)
    {
        if (element is Button { Name: "DeleteButton" } button)
        {
            if (button.MaxWidth == 0) return;
            // The native ButtonVisible state animates Visibility, so retain a zero-width style in every state.
            var style = new Style(typeof(Button)) { BasedOn = button.Style };
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 0d));
            style.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
            style.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
            style.Setters.Add(new Setter(Control.IsEnabledProperty, false));
            style.Setters.Add(new Setter(Control.IsTabStopProperty, false));
            button.Style = style;
            button.Width = 0;
            button.Visibility = Visibility.Collapsed;
            return;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            HideNativeSearchClearButton(VisualTreeHelper.GetChild(element, index));
    }

    private void ApplySearchTheme()
    {
        _searchBar.Background = LumenTheme.Brush("Control");
        _searchBar.BorderBrush = LumenTheme.Brush("Accent");
        _searchInput.Foreground = LumenTheme.Brush("Ink");
        _searchHint.Foreground = LumenTheme.Brush("Muted");
        _searchSummarySignature = string.Empty;
    }

    private static SolidColorBrush AvatarBrush(string seed)
    {
        var value = seed.Aggregate(0, (total, character) => (total + character) % 3);
        return new SolidColorBrush(value switch
        {
            0 => Windows.UI.Color.FromArgb(255, 104, 61, 65),
            1 => Windows.UI.Color.FromArgb(255, 24, 94, 100),
            _ => Windows.UI.Color.FromArgb(255, 80, 64, 112)
        });
    }

    private static LinearGradientBrush ImageBottomFade() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0, 0, 0, 0) },
            new GradientStop { Offset = 0.28, Color = Windows.UI.Color.FromArgb(20, 0, 0, 0) },
            new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(225, 0, 0, 0) }
        }
    };

    private static void SetHighlight(TextBlock target, string title, string? query, Brush ink, Brush? highlight = null)
    {
        target.Text = string.Empty;
        target.Inlines.Clear();
        target.Foreground = ink;
        if (string.IsNullOrWhiteSpace(query)) { target.Inlines.Add(new Run { Text = title }); return; }
        var start = 0;
        while (start < title.Length)
        {
            var match = title.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0) { target.Inlines.Add(new Run { Text = title[start..] }); break; }
            if (match > start) target.Inlines.Add(new Run { Text = title[start..match] });
            target.Inlines.Add(new Run { Text = title.Substring(match, query.Length), Foreground = highlight ?? LumenTheme.Brush("Accent") });
            start = match + query.Length;
        }
    }

    private static void ReconcileMedia(ObservableCollection<MediaCardViewModel> target, IReadOnlyList<MediaCardViewModel> incoming)
    {
        var ids = incoming.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = target.Count - 1; index >= 0; index--)
            if (!ids.Contains(target[index].Id)) target.RemoveAt(index);
        for (var index = 0; index < incoming.Count; index++)
        {
            var existing = target.FirstOrDefault(item => item.Id == incoming[index].Id);
            if (existing is null) target.Insert(index, incoming[index]);
            else
            {
                var previous = target.IndexOf(existing);
                if (previous != index) target.Move(previous, index);
                if (!ReferenceEquals(existing, incoming[index])) target[index] = incoming[index];
            }
        }
    }
}
