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
    private (double Width, int Columns, string Page, string Tab, int Revision, int People)? _searchLayoutKey;

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
        var layoutKey = (width, _columns, _renderedPage, _searchTab, _searchLayoutRevision, _searchPeoplePreviewCount);
        if (_searchLayoutKey is { } applied && applied.Equals(layoutKey)) return;
        _searchLayoutKey = layoutKey;
        var margin = new Thickness(_pageMargin, 116, _pageMargin, 60);
        if (!_search.Margin.Equals(margin)) _search.Margin = margin;
        var usable = Math.Max(1, width - _pageMargin * 2);
        _searchHint.Visibility = Visibility.Visible;
        _searchHint.MaxWidth = usable * .4;
        if (_search.Width != usable) _search.Width = usable;
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        var hasBest = _searchFeatured.Children.Any(child => !ReferenceEquals(child, _searchSecondary));
        var compact = usable < 848 || !hasBest;
        var bestWidth = Math.Clamp(usable * .58, 520, 680);
        _searchFeatured.ColumnSpacing = compact ? 0 : 28;
        _searchFeatured.RowSpacing = compact && hasBest && _searchSecondary.Children.Count > 0 ? 28 : 0;
        SetSearchTrackWidth(_searchFeatured.ColumnDefinitions[0], compact ? new GridLength(1, GridUnitType.Star) : new GridLength(bestWidth));
        SetSearchTrackWidth(_searchFeatured.ColumnDefinitions[1], compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star));
        foreach (var match in _searchFeatured.Children.OfType<Grid>().Where(child => !ReferenceEquals(child, _searchSecondary)))
        {
            match.MaxWidth = 680;
            match.HorizontalAlignment = HorizontalAlignment.Left;
            match.Width = compact ? Math.Min(680, usable) : bestWidth;
        }
        SetSearchGridPosition(_searchSecondary, compact ? 0 : 1, compact && hasBest ? 1 : 0);
        _searchSecondary.ColumnSpacing = 0;
        _searchSecondary.RowSpacing = 0;
        SetSearchTrackWidth(_searchSecondary.ColumnDefinitions[1], new GridLength(0));
        if (_searchPeopleSection is not null) SetSearchGridPosition(_searchPeopleSection, 0, 0);
        var columns = Math.Max(1, (int)Math.Floor((usable + 20) / 160));
        _searchCardWidth = Math.Max(80, Math.Floor((usable - 20 * (columns - 1)) / columns * 4) / 4);
        foreach (var repeater in new[] { _searchRepeater, _searchSkeletons })
        {
            repeater.Width = usable;
            repeater.HorizontalAlignment = HorizontalAlignment.Left;
        }
        foreach (var layout in new[] { _searchGridLayout, _searchSkeletonLayout })
        {
            layout.MaximumRowsOrColumns = columns;
            layout.MinItemWidth = _searchCardWidth;
            layout.MinItemHeight = _searchCardWidth * 1.5 + 48;
        }
        for (var index = 0; index < _searchMedia.Count; index++)
            if (LumenMediaCard.UnwrapNavigationElement(_searchRepeater.TryGetElement(index)) is LumenMediaCard card) card.SetCardWidth(_searchCardWidth);
        for (var index = 0; index < 16; index++)
            if (_searchSkeletons.TryGetElement(index) is CatalogSkeletonCard skeleton) skeleton.SetWidth(_searchCardWidth);
        var peopleColumns = Math.Max(1, (int)Math.Floor((usable + 10) / 290));
        _searchPeopleRepeater.Width = usable;
        _searchPeopleGridLayout.MaximumRowsOrColumns = peopleColumns;
        _searchPeopleGridLayout.MinItemWidth = Math.Floor((usable - 10 * (peopleColumns - 1)) / peopleColumns);
        UpdateSearchDiscoveryLayout();
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
