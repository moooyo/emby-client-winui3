using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Dictionary<string, int?> _searchTypeTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaCardViewModel[]> _collectionPreviews = new(StringComparer.Ordinal);
    private string _searchCountQuery = string.Empty;

    private void RenderSearchTabs()
    {
        if (_searchCountQuery != ViewModel.SearchText)
        {
            _searchCountQuery = ViewModel.SearchText;
            _searchTypeTotals.Clear();
        }
        var currentType = ViewModel.BrowseSearchType ?? "all";
        var currentCount = LumenSearchCount.From(ViewModel.TotalItemsCount, ViewModel.Items.Count, ViewModel.HasMore, SearchCountLoading);
        if (!SearchCountLoading && currentCount.IsExact)
            _searchTypeTotals[currentType] = checked((int)currentCount.Value);
        foreach (var entry in _searchTabControls)
        {
            var (button, label, count, dot) = entry.Value;
            var active = entry.Key == _searchTab;
            label.Foreground = active ? LumenTheme.Brush("Ink") : LumenTheme.Brush("Sub");
            label.FontWeight = active ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            dot.Opacity = active ? 1 : 0;
            var loaded = entry.Key switch
            {
                "people" => ViewModel.SearchPeople.Count,
                "movies" => ViewModel.Items.Count(item => item.Item.Type == "Movie"),
                "series" => ViewModel.Items.Count(item => item.Item.Type == "Series"),
                "collections" => ViewModel.Items.Count(item => item.Item.Type == "BoxSet"),
                _ => 0
            };
            var estimate = entry.Key switch
            {
                "all" => LumenSearchCount.Sum(SearchMediaCount("all", ViewModel.Items.Count), SearchPeopleCount()),
                "people" => SearchPeopleCount(),
                "movies" => SearchMediaCount("Movie", loaded),
                "series" => SearchMediaCount("Series", loaded),
                "collections" => SearchMediaCount("BoxSet", loaded),
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

    private Grid BestMatch(MediaCardViewModel item)
    {
        var surface = new Grid { Height = 338, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top };
        var artwork = new LumenArtwork { CornerRadius = new CornerRadius(18) };
        artwork.SetCoverFocalPoint(.5, .5);
        artwork.Set(item, ViewModel, ArtworkKind.Backdrop, 1200);
        surface.Children.Add(artwork);
        surface.Children.Add(new Border { Background = LumenTheme.ImageFade(horizontal: true), CornerRadius = new CornerRadius(18) });
        surface.Children.Add(new Border { Background = ImageBottomFade(), CornerRadius = new CornerRadius(18) });
        // The action buttons are siblings of the native detail button, so they cannot activate it.
        var open = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(0), CornerRadius = new CornerRadius(18), UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
        };
        open.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        open.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(open, LumenText.Get("Details") + ": " + item.AutomationLabel);
        AutomationProperties.SetAutomationId(open, "LumenBestMatchDetails");
        open.Click += async (_, _) => await OpenItemAsync(item);
        var information = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(28, 20, 28, 26), MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left };
        var typeLabel = item.Item.Type == "BoxSet" ? "Collection" : item.Item.Type ?? "Not available";
        var eyebrow = LumenUi.Text(LumenText.Get("Best match") + " \u00B7 " + LumenText.Get(typeLabel), 12);
        eyebrow.Foreground = LumenTheme.Brush("ImageSub");
        information.Children.Add(eyebrow);
        var title = LumenUi.Text(string.Empty, 40, true);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        title.LineHeight = 44;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        SetHighlight(title, item.DisplayTitle, ViewModel.SearchText, LumenTheme.Brush("ImageInk"), LumenTheme.Brush("ImageAccent"));
        information.Children.Add(title);
        var metadata = LumenUi.Text(MediaMetadata(item), 13);
        metadata.Foreground = LumenTheme.Brush("ImageSub");
        information.Children.Add(metadata);
        if (!string.IsNullOrWhiteSpace(item.Item.Overview))
        {
            var overview = LumenUi.Text(item.Item.Overview, 13);
            overview.Foreground = LumenTheme.Brush("ImageInk");
            overview.MaxLines = 2;
            overview.TextTrimming = TextTrimming.CharacterEllipsis;
            overview.LineHeight = 21;
            information.Children.Add(overview);
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(28, 0, 28, 26),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
        };
        var play = LumenUi.Button(item.CanResume ? "Resume" : "Play", "play", true, true, 38);
        play.IsEnabled = ViewModel.IsPlaybackAllowed;
        play.Click += (_, _) => ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
        if (item.CanPlay || item.Item.Type is "Series" or "Season") buttons.Children.Add(play);
        var details = LumenUi.Button("Details", "info", false, true, 38);
        details.Click += async (_, _) => await OpenItemAsync(item);
        buttons.Children.Add(details);
        information.Children.Add(new Border { Height = 42 });
        open.Content = information;
        surface.Children.Add(open);
        surface.Children.Add(buttons);
        AutomationProperties.SetName(surface, LumenText.Get("Best match") + ", " + item.AutomationLabel);
        return surface;
    }

    private StackPanel SearchResultSection(string title, double spacing = 12)
    {
        var section = new StackPanel { Spacing = spacing, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        var heading = Label(title, 16);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        section.Children.Add(heading);
        return section;
    }

    private Button PersonResult(MediaCardViewModel person)
    {
        var row = new Grid { ColumnSpacing = 14, Height = 64, Padding = new Thickness(14, 0, 14, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var portrait = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Background = AvatarBrush(person.Id) };
        if (person.Item.ImageTags?.ContainsKey("Primary") == true)
        {
            var image = new LumenArtwork { Width = 44, Height = 44, CornerRadius = new CornerRadius(22) };
            image.Set(person, ViewModel, ArtworkKind.Poster, 160);
            portrait.Child = image;
        }
        else
        {
            var initial = LumenUi.Text(person.Title[..1], 18);
            initial.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            initial.Foreground = LumenTheme.Brush("ImageInk");
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            portrait.Child = initial;
        }
        row.Children.Add(portrait);
        var information = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = LumenUi.Text(string.Empty, 14);
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
        var button = new Button { Content = row, Padding = new Thickness(0), Height = 64, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1), BorderBrush = LumenTheme.Brush("Line"), Background = LumenTheme.Brush("Card"), CornerRadius = new CornerRadius(12) };
        button.Tag = person;
        AutomationProperties.SetName(button, person.Title);
        button.Click += async (_, _) => await OpenPersonAsync(person);
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
        button.Click += async (_, _) => await OpenItemAsync(collection);
        return button;
    }
}
