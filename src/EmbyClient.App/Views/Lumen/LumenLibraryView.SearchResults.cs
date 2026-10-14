using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Dictionary<string, int?> _searchTypeTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaCardViewModel[]> _collectionPreviews = new(StringComparer.Ordinal);
    private string _searchCountQuery = string.Empty;
    private string _searchTotalsRequestQuery = string.Empty;
    private CancellationTokenSource? _searchTotalsLifetime;

    private void RenderSearchTabs()
    {
        if (_searchCountQuery != ViewModel.SearchText)
        {
            _searchCountQuery = ViewModel.SearchText;
            _searchTypeTotals.Clear();
            _searchTotalsRequestQuery = string.Empty;
        }
        EnsureSearchTypeTotals();
        var currentType = ViewModel.BrowseSearchType ?? "all";
        var currentCount = LumenSearchCount.From(ViewModel.TotalItemsCount, ViewModel.Items.Count, ViewModel.HasMore, SearchCountLoading);
        if (!SearchCountLoading && currentCount.IsExact)
            _searchTypeTotals[currentType] = checked((int)currentCount.Value);
        foreach (var entry in _searchTabControls)
        {
            var (button, label, count, dot) = entry.Value;
            var active = entry.Key == _searchTab;
            label.Foreground = active ? LumenTheme.Brush("Ink") : LumenTheme.Brush("Sub");
            label.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            dot.Opacity = active ? 1 : 0;
            var loaded = entry.Key switch
            {
                "people" => ViewModel.SearchPeople.Count,
                "movies" => ViewModel.Items.Count(item => item.Item.Type == "Movie"),
                "series" => ViewModel.Items.Count(item => item.Item.Type == "Series"),
                _ => 0
            };
            var estimate = entry.Key switch
            {
                "all" => LumenSearchCount.Sum(SearchMediaCount(currentType == "BoxSet" ? "BoxSet" : "all", ViewModel.Items.Count), SearchPeopleCount()),
                "people" => SearchPeopleCount(),
                "movies" => SearchMediaCount("Movie", loaded),
                "series" => SearchMediaCount("Series", loaded),
                _ => LumenSearchCount.From(null, 0, false, false, scopeComplete: false)
            };
            count.Text = ViewModel.HasSearchQuery ? estimate.Format(CultureInfo.CurrentCulture, hideZeroLowerBound: true) : string.Empty;
            AutomationProperties.SetName(button, label.Text + (count.Text.Length > 0 ? " " + count.Text : string.Empty));
        }
    }

    private bool SearchCountLoading => ViewModel.IsBusy || ViewModel.HasPendingSearch || ViewModel.HasError
        || ViewModel.LoadOutcome != PageLoadOutcome.Succeeded;

    private LumenSearchCount SearchMediaCount(string scope, int loaded)
    {
        var currentScope = ViewModel.BrowseSearchType ?? "all";
        var cached = _searchTypeTotals.GetValueOrDefault(scope);
        var total = scope == currentScope ? ViewModel.TotalItemsCount ?? cached : cached;
        var completeScope = scope == currentScope || currentScope == "all";
        return LumenSearchCount.From(total, loaded, ViewModel.HasMore, SearchCountLoading, completeScope);
    }

    private LumenSearchCount SearchPeopleCount() => LumenSearchCount.From(ViewModel.SearchPeopleTotal,
        ViewModel.SearchPeople.Count, ViewModel.SearchPeopleHasMore,
        SearchCountLoading || ViewModel.SearchPeopleIsBusy || ViewModel.SearchPeopleError.Length > 0);

    private void EnsureSearchTypeTotals()
    {
        if (_session is null || !ViewModel.HasSearchQuery || ViewModel.HasPendingSearch || _searchTotalsRequestQuery == ViewModel.SearchText) return;
        _searchTotalsLifetime?.Cancel();
        _searchTotalsLifetime?.Dispose();
        _searchTotalsLifetime = new CancellationTokenSource();
        _searchTotalsRequestQuery = ViewModel.SearchText;
        _ = LoadSearchTypeTotalsAsync(_session, ViewModel.SearchText, _sessionGeneration, _searchTotalsLifetime.Token);
    }

    private async Task LoadSearchTypeTotalsAsync(ConnectedSession session, string query, int generation, CancellationToken cancellationToken)
    {
        var requests = new[] { "Movie", "Series" }.Select(async type =>
        {
            int? count = null;
            try
            {
                var response = await session.Api.GetItemsAsync(new ItemQuery
                {
                    SearchTerm = query, IncludeItemTypes = [type], Recursive = true, Limit = 1,
                    EnableImages = false, EnableUserData = false
                }, cancellationToken);
                count = response.TotalRecordCount is >= 0 ? response.TotalRecordCount : response.Items.Length == 0 ? 0 : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (EmbyApiException exception) when (exception.IsAuthenticationFailure)
            {
                if (generation == _sessionGeneration && !cancellationToken.IsCancellationRequested) SessionExpired?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception) when (exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException)
            {
                // A missing aggregate retains an explicitly inexact count based on loaded items.
            }
            return (Type: type, Count: count);
        });
        var results = await Task.WhenAll(requests);
        if (cancellationToken.IsCancellationRequested || generation != _sessionGeneration || query != _searchCountQuery) return;
        foreach (var result in results) if (result.Count is not null) _searchTypeTotals[result.Type] = result.Count;
        if (results.All(result => result.Count is not null))
            _searchTypeTotals["all"] = results.Sum(result => result.Count!.Value);
        QueueRender();
    }

    private Grid BestMatch(MediaCardViewModel item)
    {
        var surface = new Grid { Height = 338, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top };
        AttachHomeShadow(surface, 18, 40, 16, .3f);
        var backdropLayer = new Grid();
        var hasBackdrop = ImageCache.SelectImage(item.Item, ArtworkKind.Backdrop) is not null;
        var artwork = new LumenArtwork { CornerRadius = new CornerRadius(0) };
        artwork.SetCoverFocalPoint(.5, .5);
        artwork.Set(item, ViewModel, hasBackdrop ? ArtworkKind.Backdrop : ArtworkKind.Poster, hasBackdrop ? 1200 : 128);
        backdropLayer.Children.Add(artwork);
        surface.Children.Add(backdropLayer);
        if (!hasBackdrop)
        {
            backdropLayer.Children.Add(new Border
            {
                Background = new AcrylicBrush
                {
                    TintColor = Colors.Transparent, TintOpacity = 0, TintLuminosityOpacity = 0,
                    FallbackColor = Windows.UI.Color.FromArgb(255, 26, 24, 21)
                }
            });
            var posterFrame = new Grid
            {
                Width = 150, Height = 225,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 28, 28, 0)
            };
            AttachHomeShadow(posterFrame, 10, 40, 16, .45f);
            var poster = new LumenArtwork { CornerRadius = new CornerRadius(10) };
            poster.Set(item, ViewModel, ArtworkKind.Poster, 480);
            posterFrame.Children.Add(poster);
            surface.Children.Add(posterFrame);
        }
        void UpdateBackdropBounds()
        {
            if (backdropLayer.ActualWidth <= 0 || backdropLayer.ActualHeight <= 0) return;
            var visual = ElementCompositionPreview.GetElementVisual(backdropLayer);
            var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2((float)backdropLayer.ActualWidth, (float)backdropLayer.ActualHeight);
            geometry.CornerRadius = new Vector2(18);
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
            if (!hasBackdrop) artwork.Margin = new Thickness(-backdropLayer.ActualWidth * .2, -backdropLayer.ActualHeight * .2,
                -backdropLayer.ActualWidth * .2, -backdropLayer.ActualHeight * .2);
        }
        backdropLayer.Loaded += (_, _) => UpdateBackdropBounds();
        backdropLayer.SizeChanged += (_, _) => UpdateBackdropBounds();
        surface.Children.Add(new Border
        {
            Background = HomeGradient(true, (0, 219, 0, 0, 0), (.6, 102, 0, 0, 0), (1, 26, 0, 0, 0)),
            CornerRadius = new CornerRadius(18)
        });
        // The action buttons are siblings of the native detail button, so they cannot activate it.
        var open = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(0), CornerRadius = new CornerRadius(18), UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Tag = item
        };
        open.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        open.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(open, LumenText.Get("Details") + ": " + item.AutomationLabel);
        AutomationProperties.SetAutomationId(open, "LumenBestMatchDetails");
        var query = ViewModel.SearchText;
        open.Click += async (_, _) => { RememberSearchQuery(query); await OpenItemAsync(item); };
        var information = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(28, 20, 28, 26), MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left };
        var typeLabel = item.Item.Type == "BoxSet" ? "Collection" : item.Item.Type ?? "Not available";
        var eyebrow = LumenUi.Text(LumenText.Get("Best match") + " \u00B7 " + LumenText.Get(typeLabel), 12);
        eyebrow.Foreground = LumenTheme.Brush("ImageInk");
        eyebrow.CharacterSpacing = 60;
        eyebrow.Opacity = .8;
        information.Children.Add(eyebrow);
        var title = LumenUi.Text(string.Empty, 40, true);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        title.Margin = new Thickness(0, 8, 0, 0);
        title.LineHeight = 44;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        SetHighlight(title, item.DisplayTitle, ViewModel.SearchText, LumenTheme.Brush("ImageInk"), LumenTheme.Brush("ImageAccent"));
        information.Children.Add(title);
        information.Children.Add(SearchMatchMetadata(item));
        if (!string.IsNullOrWhiteSpace(item.Item.Overview))
        {
            var overview = LumenUi.Text(item.Item.Overview, 13);
            overview.Foreground = LumenTheme.Brush("ImageInk");
            overview.Opacity = .82;
            overview.Margin = new Thickness(0, 10, 0, 0);
            overview.MaxWidth = 440;
            overview.MaxLines = 2;
            overview.TextTrimming = TextTrimming.CharacterEllipsis;
            overview.LineHeight = 21;
            information.Children.Add(overview);
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 0, 28, 26),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
        };
        var play = LumenUi.Button(item.CanResume ? "Resume" : "Play", "play", true, true, 38);
        play.Padding = new Thickness(14, 0, 18, 0);
        if (play.Content is StackPanel playContent)
            foreach (var icon in playContent.Children.OfType<Image>()) icon.Width = icon.Height = 16;
        play.IsEnabled = ViewModel.IsPlaybackAllowed;
        play.Click += (_, _) => { RememberSearchQuery(query); ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0)); };
        if (item.CanPlay || item.Item.Type is "Series" or "Season") buttons.Children.Add(play);
        var details = LumenUi.Button("Details", onImage: true, height: 38);
        details.Padding = new Thickness(16, 0, 16, 0);
        details.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(41, 255, 255, 255));
        details.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255));
        details.Resources["ButtonBackground"] = details.Background;
        details.Click += async (_, _) => { RememberSearchQuery(query); await OpenItemAsync(item); };
        buttons.Children.Add(details);
        information.Children.Add(new Border { Height = 54 });
        open.Content = information;
        surface.Children.Add(open);
        surface.Children.Add(buttons);
        AutomationProperties.SetName(surface, LumenText.Get("Best match") + ", " + item.AutomationLabel);
        return surface;
    }

    private static RichTextBlock SearchMatchMetadata(MediaCardViewModel item)
    {
        var metadata = new RichTextBlock
        {
            FontFamily = LumenTheme.SansFont, FontSize = 13, Foreground = LumenTheme.Brush("ImageInk"),
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false,
            Margin = new Thickness(0, 8, 0, 0), Opacity = .9
        };
        var line = new Paragraph();
        line.Inlines.Add(new Run
        {
            Text = string.Join(" \u00b7 ", new[]
            {
                item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
                item.Item.Genres is { Length: > 0 } genres ? string.Join(" / ", genres) : null,
                item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons
                    ? LumenText.Get("{0} seasons", seasons) : RuntimeText(item.Item.RunTimeTicks)
            }.Where(value => !string.IsNullOrWhiteSpace(value)))
        });
        if (item.Item.CommunityRating is { } rating)
        {
            line.Inlines.Add(new Run { Text = "  " });
            var score = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            score.Children.Add(LumenUi.Icon("star", 13, true));
            var value = LumenUi.Text(rating.ToString("0.0", CultureInfo.InvariantCulture), 13);
            value.Foreground = LumenTheme.Brush("ImageInk");
            score.Children.Add(value);
            line.Inlines.Add(new InlineUIContainer { Child = score });
        }
        metadata.Blocks.Add(line);
        return metadata;
    }

    private StackPanel SearchResultSection(string title, double spacing = 12)
    {
        var section = new StackPanel { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        var heading = Label(title, 16);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        section.Children.Add(heading);
        return section;
    }

    private Button PersonResult(MediaCardViewModel person, bool tile = false)
    {
        var size = tile ? 48 : 44;
        var row = new Grid { ColumnSpacing = 14, Height = tile ? 72 : 64, Padding = new Thickness(tile ? 16 : 14, 0, tile ? 16 : 14, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var portrait = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = AvatarBrush(person.Id) };
        if (person.Item.ImageTags?.ContainsKey("Primary") == true)
        {
            var image = new LumenArtwork { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2) };
            image.Set(person, ViewModel, ArtworkKind.Poster, 160);
            portrait.Child = image;
        }
        else
        {
            var initial = LumenUi.Text(string.IsNullOrWhiteSpace(person.Title) ? "?" : StringInfo.GetNextTextElement(person.Title), 18);
            initial.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            initial.Foreground = LumenTheme.Brush("ImageInk");
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            portrait.Child = initial;
        }
        row.Children.Add(portrait);
        var information = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = LumenUi.Text(string.Empty, tile ? 15 : 14);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        SetHighlight(title, person.Title, ViewModel.SearchText, LumenTheme.Brush("Ink"));
        information.Children.Add(title);
        var works = (person.Item.MovieCount ?? 0) + (person.Item.SeriesCount ?? 0);
        var subtitle = LumenUi.Text((string.IsNullOrWhiteSpace(person.PersonRole) ? LumenText.Get("Person") : LumenText.Get(person.PersonRole))
            + (works > 0 ? " \u00B7 " + LumenText.Get("{0} works", works) : string.Empty), 12);
        subtitle.Foreground = LumenTheme.Brush("Muted");
        subtitle.TextWrapping = TextWrapping.NoWrap;
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        information.Children.Add(subtitle);
        Grid.SetColumn(information, 1);
        row.Children.Add(information);
        var arrow = LumenUi.Icon("chevron-right", 16);
        arrow.Opacity = 0.55;
        Grid.SetColumn(arrow, 2);
        row.Children.Add(arrow);
        var button = new Button { Content = row, Padding = new Thickness(0), Height = tile ? 72 : 64, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1), BorderBrush = LumenTheme.Brush("Line"), Background = LumenTheme.Brush("Card"), CornerRadius = new CornerRadius(12), UseSystemFocusVisuals = true };
        button.Tag = person;
        AutomationProperties.SetName(button, person.Title);
        var query = ViewModel.SearchText;
        button.Click += async (_, _) => { RememberSearchQuery(query); await OpenPersonAsync(person); };
        return button;
    }

    private Button CollectionResult(MediaCardViewModel collection)
    {
        var row = new Grid { Height = 72, ColumnSpacing = 14, Padding = new Thickness(14, 0, 14, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var preview = new Grid { Width = 78, Height = 54, HorizontalAlignment = HorizontalAlignment.Left };
        var poster = new LumenArtwork { Width = 36, Height = 54, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        poster.Set(collection, ViewModel, ArtworkKind.Poster, 160);
        preview.Children.Add(poster);
        CancellationTokenSource? lifetime = null;
        preview.Loaded += async (_, _) =>
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            await LoadCollectionPreviewAsync(preview, collection, lifetime.Token);
        };
        preview.Unloaded += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
        row.Children.Add(preview);
        var information = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = LumenUi.Text(string.Empty, 14);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        SetHighlight(title, collection.Title, ViewModel.SearchText, LumenTheme.Brush("Ink"));
        information.Children.Add(title);
        var count = collection.Item.ChildCount ?? collection.Item.RecursiveItemCount;
        var subtitle = LumenUi.Text(count is { } total ? LumenText.Get("{0} items", total) : LumenText.Get("Collection"), 12);
        subtitle.Foreground = LumenTheme.Brush("Muted");
        subtitle.TextWrapping = TextWrapping.NoWrap;
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        information.Children.Add(subtitle);
        Grid.SetColumn(information, 1);
        row.Children.Add(information);
        var arrow = LumenUi.Icon("chevron-right", 16);
        arrow.Opacity = 0.55;
        Grid.SetColumn(arrow, 2);
        row.Children.Add(arrow);
        var button = new Button { Content = row, Height = 72, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(12), Background = LumenTheme.Brush("Card"), BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(1) };
        button.Tag = collection;
        AutomationProperties.SetName(button, collection.Title);
        var query = ViewModel.SearchText;
        button.Click += async (_, _) => { RememberSearchQuery(query); await OpenItemAsync(collection); };
        return button;
    }
}
