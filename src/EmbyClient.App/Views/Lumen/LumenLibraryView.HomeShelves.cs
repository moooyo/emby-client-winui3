using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private StackPanel HomeShelf(string title, IEnumerable<MediaCardViewModel> items, bool landscape, Func<Task> seeAll)
    {
        // The scroller's seven-pixel hover clearance completes the twelve-pixel visual gap.
        var section = new StackPanel { Spacing = 5 };
        var header = new Grid { Margin = new Thickness(_pageMargin, 0, _pageMargin, 0), Height = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var heading = Label(title, 22, true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(heading);
        var more = LumenUi.IconButton("chevron-right", "View all", 28);
        more.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        more.BorderThickness = new Thickness(0);
        more.Click += async (_, _) => await seeAll();
        label.Children.Add(more);
        header.Children.Add(label);
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var previous = LumenUi.IconButton("chevron-left", "Previous", 32);
        var next = LumenUi.IconButton("chevron-right", "Next", 32);
        arrows.Children.Add(previous);
        arrows.Children.Add(next);
        Grid.SetColumn(arrows, 1);
        header.Children.Add(arrows);
        section.Children.Add(header);
        _homeHeaders.Add(header);
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 7, 0, 12) };
        foreach (var item in items) cards.Children.Add(MakeCard(item, landscape, landscape ? 320 : 160));
        var scroller = new ScrollViewer
        {
            Content = cards, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(_pageMargin, 0, _pageMargin, 0)
        };
        previous.Click += (_, _) => scroller.ChangeView(Math.Max(0, scroller.HorizontalOffset - scroller.ViewportWidth * 0.9), null, null);
        next.Click += (_, _) => scroller.ChangeView(Math.Min(scroller.ScrollableWidth, scroller.HorizontalOffset + scroller.ViewportWidth * 0.9), null, null);
        scroller.ViewChanged += (_, _) =>
        {
            previous.IsEnabled = scroller.HorizontalOffset > 1;
            next.IsEnabled = scroller.HorizontalOffset < scroller.ScrollableWidth - 1;
        };
        previous.IsEnabled = false;
        _homeScrollers.Add(scroller);
        section.Children.Add(scroller);
        return section;
    }

    private StackPanel BuildMediaTiles()
    {
        var section = new StackPanel { Spacing = 16 };
        var title = Label("My media", 22, true);
        title.Margin = new Thickness(_pageMargin, 0, _pageMargin, 0);
        section.Children.Add(title);
        _homeHeaders.Add(title);
        _mediaTiles = new Grid { ColumnSpacing = 16, RowSpacing = 16, Margin = new Thickness(_pageMargin, 0, _pageMargin, 0) };
        foreach (var library in ViewModel.Libraries)
        {
            var surface = new Grid { Height = 112, CornerRadius = new CornerRadius(12) };
            var backdrop = new LumenArtwork { CornerRadius = new CornerRadius(12) };
            var latest = ViewModel.HomeRows.FirstOrDefault(row => row.LibraryId == library.Id)?.Items.FirstOrDefault();
            backdrop.Set(latest ?? library, ViewModel, ArtworkKind.Landscape, 640);
            surface.Children.Add(backdrop);
            surface.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = LumenTheme.ImageFade(horizontal: true) });
            var icon = LumenUi.Icon(library.Item.CollectionType == "tvshows" ? "tv" : library.Item.CollectionType == "movies" ? "film" : "library", 20, true);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(18, 16, 0, 0);
            surface.Children.Add(icon);
            var text = new StackPanel { Spacing = 4, Margin = new Thickness(18, 0, 18, 15), VerticalAlignment = VerticalAlignment.Bottom };
            var name = LumenUi.Text(library.Title, 20, true);
            name.FontWeight = Microsoft.UI.Text.FontWeights.Black;
            name.Text = library.Title;
            name.Foreground = LumenTheme.Brush("ImageInk");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(name);
            text.Children.Add(LibraryCountLabel(library));
            surface.Children.Add(text);
            var button = new Button { Content = surface, Height = 112, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(1), BorderBrush = LumenTheme.Brush("ImageLine"), CornerRadius = new CornerRadius(12) };
            AutomationProperties.SetName(button, library.Title);
            button.Click += async (_, _) => { _lastInvokedId = library.Id; await ViewModel.ShowLibraryAsync(library); QueueRender(); };
            button.PointerEntered += (_, _) => button.RenderTransform = new TranslateTransform { Y = -3 };
            button.PointerExited += (_, _) => button.RenderTransform = null;
            _mediaTiles.Children.Add(button);
        }
        section.Children.Add(_mediaTiles);
        LayoutMediaTiles();
        return section;
    }

    private void LayoutMediaTiles()
    {
        if (_mediaTiles is null) return;
        var usableWidth = Math.Max(1, ActualWidth - _pageMargin * 2);
        var columns = Math.Clamp((int)(usableWidth / 200), 1, 5);
        _mediaTiles.ColumnDefinitions.Clear();
        _mediaTiles.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++) _mediaTiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (int)Math.Ceiling(_mediaTiles.Children.Count / (double)columns); row++) _mediaTiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(112) });
        for (var index = 0; index < _mediaTiles.Children.Count; index++)
        {
            if (_mediaTiles.Children[index] is not FrameworkElement tile) continue;
            Grid.SetColumn(tile, index % columns);
            Grid.SetRow(tile, index / columns);
        }
    }

    private void UpdateHomeLayout(double width)
    {
        _hero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(1, _hero.ActualWidth > 0 ? _hero.ActualWidth : width), 660) };
        _heroInfo.Width = Math.Max(240, Math.Min(580, width - _pageMargin * 2));
        _heroInfo.Margin = new Thickness(_pageMargin, 0, 0, width < 1280 ? 174 : 112);
        _heroThumbScroll.HorizontalAlignment = width < 1280 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _heroThumbScroll.Margin = new Thickness(width < 1280 ? _pageMargin : 0, 0, _pageMargin, width < 1280 ? 80 : 112);
        _heroThumbScroll.MaxWidth = Math.Max(1, width - _pageMargin * 2);
        foreach (var header in _homeHeaders) header.Margin = new Thickness(_pageMargin, 0, _pageMargin, 0);
        foreach (var scroller in _homeScrollers) scroller.Padding = new Thickness(_pageMargin, 0, _pageMargin, 0);
        if (_mediaTiles is not null) _mediaTiles.Margin = new Thickness(_pageMargin, 0, _pageMargin, 0);
        LayoutMediaTiles();
    }

    private void ApplyHomeTheme()
    {
        _heroBottom.Background = LumenTheme.ImageFade(pageBottom: true);
        foreach (var text in new[] { _heroTitle, _heroOriginal, _heroOverview, _heroType }) text.Foreground = LumenTheme.Brush("ImageInk");
        _heroMetadata.Foreground = LumenTheme.Brush("ImageInk");
        _homeSignature = string.Empty;
    }

    private static StackPanel ActionContent(string label, string icon, bool onImage = false)
    {
        if (onImage)
        {
            var primary = LumenUi.Button(label, icon, true, true);
            var content = (StackPanel)primary.Content;
            primary.Content = null;
            return content;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(LumenUi.Icon(icon, 18, onImage));
        var text = Label(label, 14);
        text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        text.Foreground = onImage ? new SolidColorBrush(Microsoft.UI.Colors.Black) : LumenTheme.Brush("Ink");
        panel.Children.Add(text);
        return panel;
    }

    private static string RuntimeText(long? ticks)
    {
        if (ticks is not > 0) return string.Empty;
        var duration = TimeSpan.FromTicks(ticks.Value);
        var minutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        return minutes >= 60 ? LumenText.Get("{0} h {1} min", minutes / 60, minutes % 60) : LumenText.Get("{0} min", minutes);
    }

    private static string MediaMetadata(MediaCardViewModel item) => string.Join("  \u00B7  ", new[]
    {
        item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
        item.Item.Genres?.FirstOrDefault(),
        item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons ? LumenText.Get("{0} seasons", seasons) : RuntimeText(item.Item.RunTimeTicks),
        item.Item.CommunityRating is { } rating ? "\u2605 " + rating.ToString("0.0", CultureInfo.InvariantCulture) : null,
        item.MediaFormat.Split(" \u00B7 ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
