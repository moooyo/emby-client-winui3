using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private static readonly ConditionalWeakTable<Button, SolidColorBrush> SegmentHoverBrushes = new();
    private bool _wallSliderResourcesInstalled;
    private int _wallNavigationVersion;
    private readonly TextBlock _wallNavigationStatus = LumenUi.Text(string.Empty, 12);
    private void BuildSortFlyout()
    {
        var options = new StackPanel { Spacing = 2, Width = 192 };
        foreach (var entry in new[]
        {
            (Label: "Name A-Z", Sort: "SortName", Descending: false),
            (Label: "Recently added", Sort: "DateCreated", Descending: true),
            (Label: "Release date", Sort: "ProductionYear", Descending: true),
            (Label: "Rating", Sort: "CommunityRating", Descending: true),
            (Label: "Random", Sort: "Random", Descending: false)
        })
        {
            var button = LumenUi.Button(entry.Label, "check", height: 38);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(10);
            button.Click += async (_, _) =>
            {
                _sortFlyout.Hide();
                await ViewModel.SetBrowseOptionsAsync(entry.Sort, entry.Descending, ViewModel.BrowseWatchFilter);
            };
            options.Children.Add(button);
            _sortButtons.Add((button, entry.Sort, entry.Descending));
        }
        _sortFlyout.Content = options;
        _sortFlyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        _sortFlyout.FlyoutPresenterStyle = PopupStyle();
        _sortFlyout.Opened += (_, _) =>
            (_sortButtons.FirstOrDefault(entry => entry.Sort == ViewModel.BrowseSortKey && entry.Descending == ViewModel.BrowseSortDescending).Button
                ?? _sortButtons.FirstOrDefault().Button)?.Focus(FocusState.Keyboard);
        options.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; _sortFlyout.Hide(); return; }
            if (args.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End)) return;
            var buttons = options.Children.OfType<Button>().ToArray();
            var current = Array.IndexOf(buttons, XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as Button);
            var next = args.Key switch
            {
                Windows.System.VirtualKey.Home => 0, Windows.System.VirtualKey.End => buttons.Length - 1,
                Windows.System.VirtualKey.Up => (current + buttons.Length - 1) % buttons.Length,
                _ => (current + 1) % buttons.Length
            };
            args.Handled = true;
            buttons[next].Focus(FocusState.Keyboard);
        };
    }

    private void BuildFilterFlyout()
    {
        var content = new StackPanel { Width = 268 };
        content.Children.Add(FilterHeading("Watch status"));
        var watch = new Grid { ColumnSpacing = 2 };
        foreach (var entry in new[]
        {
            (Label: "All", Filter: WatchStatusFilter.All),
            (Label: "Unwatched", Filter: WatchStatusFilter.Unplayed),
            (Label: "Watched", Filter: WatchStatusFilter.Played)
        })
        {
            watch.ColumnDefinitions.Add(new ColumnDefinition());
            var button = SegmentButton(entry.Label);
            Grid.SetColumn(button, watch.Children.Count);
            button.Click += async (_, _) => await ViewModel.SetLumenBrowseFiltersAsync(ViewModel.BrowseGenre, entry.Filter);
            watch.Children.Add(button);
            _watchButtons.Add((button, entry.Filter));
        }
        content.Children.Add(SegmentSurface(watch));
        var qualityHeading = FilterHeading("Video quality");
        qualityHeading.Margin = new Thickness(0, 14, 0, 0);
        content.Children.Add(qualityHeading);
        var quality = new Grid { ColumnSpacing = 2 };
        foreach (var label in new[] { "All", "4K", "1080p" })
        {
            var value = label == "All" ? null : label;
            quality.ColumnDefinitions.Add(new ColumnDefinition());
            var button = SegmentButton(label);
            Grid.SetColumn(button, quality.Children.Count);
            button.Click += async (_, _) => await ViewModel.SetLumenQualityFilterAsync(value);
            quality.Children.Add(button);
            _qualityButtons.Add((button, value));
        }
        content.Children.Add(SegmentSurface(quality));
        var clear = LumenUi.Button("Clear all filters", height: 32);
        clear.Margin = new Thickness(0, 16, 0, 0);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.Click += async (_, _) => { _filterFlyout.Hide(); await ClearWallFiltersAsync(); };
        content.Children.Add(clear);
        _filterFlyout.Content = content;
        _filterFlyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        _filterFlyout.FlyoutPresenterStyle = PopupStyle(16);
    }

    private static TextBlock FilterHeading(string key)
    {
        var heading = Label(key, 12);
        heading.Foreground = LumenTheme.Brush("Muted");
        return heading;
    }

    private static Border SegmentSurface(Grid content) => new()
    {
        Child = content, Padding = new Thickness(3), Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(19),
        Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(1)
    };

    private static Button SegmentButton(string label)
    {
        var button = new Button
        {
            Height = 30, MinHeight = 30, MinWidth = 0, Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Colors.Transparent),
            Foreground = LumenTheme.Brush("Sub"), Content = Label(label, 13), UseSystemFocusVisuals = true
        };
        var hover = new SolidColorBrush(LumenTheme.Color("Hover"));
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = hover;
        SegmentHoverBrushes.Add(button, hover);
        AutomationProperties.SetName(button, LumenText.Get(label));
        return button;
    }

    private static void SetSegmentAppearance(Button button, bool selected)
    {
        button.Background = selected ? LumenTheme.Brush("Ink") : new SolidColorBrush(Colors.Transparent);
        button.Foreground = selected ? LumenTheme.Brush("Background") : LumenTheme.Brush("Sub");
        if (SegmentHoverBrushes.TryGetValue(button, out var hover)) hover.Color = LumenTheme.Color(selected ? "Ink" : "Hover");
        if (button.Content is TextBlock label)
        {
            label.Foreground = button.Foreground;
            label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
        }
        AutomationProperties.SetItemStatus(button, selected ? LumenText.Get("Selected") : string.Empty);
    }

    private static StackPanel WallToolContent(string key, string icon, bool chevron = false, int count = 0)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(LumenUi.Icon(icon, 16));
        var label = Label(key, 13);
        label.TextWrapping = TextWrapping.NoWrap;
        content.Children.Add(label);
        if (count > 0)
        {
            var number = LumenUi.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11);
            number.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            number.Foreground = LumenTheme.Brush("AccentInk");
            number.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(new Border { Child = number, Background = LumenTheme.Brush("Accent"), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(9) });
        }
        if (chevron) content.Children.Add(LumenUi.Icon("chevron-down", 12));
        return content;
    }

    private static Style PopupStyle(double padding = 6)
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Pop")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("Ink")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LumenTheme.Brush("LineStrong")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(padding)));
        return style;
    }

    private void CloseBrowseFlyouts()
    {
        _sortFlyout.Hide();
        _filterFlyout.Hide();
    }

    private Task ClearWallFiltersAsync() => ViewModel.ClearLumenBrowseFiltersAsync();

    private async Task LoadWallGenresAsync(int revision, int generation)
    {
        await ViewModel.LoadLumenGenresAsync();
        if (generation == _sessionGeneration && revision == ViewModel.NavigationRevision) QueueRender();
    }

    private void UpdateAlphabet(MediaCardViewModel? hovered = null)
    {
        _alphabet.Visibility = _renderedPage == "wall" && ViewModel.SupportsAlphabetNavigation
            && !_listView && ActualWidth >= 900 && ViewModel.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var letters = ViewModel.AvailableBrowseInitials?.ToHashSet(StringComparer.Ordinal);
        var selected = ViewModel.PendingBrowseInitial ?? (hovered is not null ? AlphabetKey(hovered.Item.SortName ?? hovered.Title) : null);
        foreach (var button in _alphabet.Children.OfType<Button>())
        {
            if (button.Tag is not string letter) continue;
            var active = selected == letter;
            var pending = ViewModel.IsBrowseNavigationPending && ViewModel.PendingBrowseInitial == letter;
            // Unknown prefixes stay reachable while the server-wide prefix query is pending or unsupported.
            button.IsEnabled = ViewModel.SupportsAlphabetNavigation && !pending && (letter == "#" || letters is null || letters.Contains(letter));
            button.Background = active ? LumenTheme.Brush("Accent") : new SolidColorBrush(Colors.Transparent);
            button.Opacity = active || button.IsEnabled ? 1 : .3;
            if (button.Content is TextBlock text) text.Foreground = active ? LumenTheme.Brush("AccentInk") : LumenTheme.Brush("Sub");
            AutomationProperties.SetItemStatus(button, pending ? LumenText.Get("Loading") : active ? LumenText.Get("Selected") : string.Empty);
        }
        UpdateWallNavigationStatus();
        if (_renderedPage == "wall" && ViewModel.SupportsAlphabetNavigation && !ViewModel.IsBusy)
            _ = ViewModel.LoadBrowseAlphabetAsync();
    }

    private void ConfigureWallNavigation()
    {
        _wall.PreviewKeyDown += OnWallNavigationKeyDown;
        _alphabet.PreviewKeyDown += OnWallNavigationKeyDown;
        _wallNavigationStatus.Margin = new Thickness(0, 12, 0, 0);
        _wallNavigationStatus.Visibility = Visibility.Collapsed;
        _wallNavigationStatus.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(_wallNavigationStatus, "LumenLibraryPosition");
        AutomationProperties.SetLiveSetting(_wallNavigationStatus, AutomationLiveSetting.Polite);
    }

    private async void OnWallNavigationKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || !ViewModel.SupportsBrowseBoundaryNavigation
            || !IsWallNavigationSource(args.OriginalSource as DependencyObject)
            || WallNavigationModifierDown(Windows.System.VirtualKey.Control)
            || WallNavigationModifierDown(Windows.System.VirtualKey.Menu)) return;
        string? initial = null;
        var end = args.Key == Windows.System.VirtualKey.End;
        if (args.Key is not (Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End))
        {
            if (!ViewModel.SupportsAlphabetNavigation || args.Key < Windows.System.VirtualKey.A || args.Key > Windows.System.VirtualKey.Z) return;
            initial = ((char)('A' + (int)args.Key - (int)Windows.System.VirtualKey.A)).ToString();
        }
        args.Handled = true;
        await NavigateWallWindowAsync(initial, end);
    }

    private static bool WallNavigationModifierDown(Windows.System.VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private bool IsWallNavigationSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _wallRepeater) || ReferenceEquals(current, _alphabet)) return true;
        return false;
    }

    private Task JumpToWallInitialAsync(string initial) => NavigateWallWindowAsync(initial, false);

    private async Task NavigateWallWindowAsync(string? initial, bool end)
    {
        var inputVersion = ++_wallNavigationVersion;
        var generation = _sessionGeneration;
        var epoch = _renderEpoch;
        var origin = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        var navigation = initial is not null ? ViewModel.JumpToBrowseInitialAsync(initial) : ViewModel.JumpToBrowseBoundaryAsync(end);
        var revision = ViewModel.NavigationRevision;
        var target = await navigation;
        if (!CanApply()) return;
        _autoPageBlocked = false;
        RenderPage();
        if (target is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!CanApply()) return;
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
            if (focused is not null && !ReferenceEquals(focused, origin) && !IsWallNavigationSource(focused as DependencyObject)) return;
            var index = ViewModel.Items.IndexOf(target);
            if (index >= 0) LumenMediaCard.FocusNavigationItem(_wallRepeater, index);
        });

        bool CanApply() => inputVersion == _wallNavigationVersion && generation == _sessionGeneration && epoch == _renderEpoch
            && revision == ViewModel.NavigationRevision && _renderEnabled && _renderLoaded && _renderedPage == "wall";
    }

    private void UpdateWallNavigationStatus()
    {
        _wallNavigationStatus.Foreground = LumenTheme.Brush("Sub");
        _wallNavigationStatus.Text = ViewModel.IsBrowseNavigationPending ? LumenText.Get("Loading library position...")
            : ViewModel.BrowseWindowStartIndex > 0 && ViewModel.Items.Count > 0
                ? ViewModel.TotalItemsCount is { } total
                    ? LumenText.Get("Showing titles {0}–{1} of {2}. Press Home to return to the start.",
                        ViewModel.BrowseWindowStartIndex + 1, ViewModel.BrowseWindowStartIndex + ViewModel.Items.Count, total)
                    : LumenText.Get("Showing titles from {0}. Press Home to return to the start.", ViewModel.BrowseWindowStartIndex + 1)
                : string.Empty;
        _wallNavigationStatus.Visibility = _wallNavigationStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string AlphabetKey(string title)
    {
        var first = title.TrimStart().FirstOrDefault();
        return first is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? char.ToUpperInvariant(first).ToString() : "#";
    }

    private void UpdateWallLayout(double width)
    {
        _wall.Margin = new Thickness(_pageMargin, 116, _pageMargin + 22, 64);
        var compact = width < 1040;
        _wallHeader.RowSpacing = compact ? 14 : 0;
        Grid.SetRow(_wallTools, compact ? 1 : 0);
        Grid.SetColumn(_wallTools, compact ? 0 : 1);
        Grid.SetColumnSpan(_wallTools, compact ? 2 : 1);
        _wallTools.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var usable = Math.Max(1, width - _pageMargin * 2 - 22);
        var minimum = Math.Clamp(_preferences.PosterWidth, 110, 220);
        var columns = Math.Max(1, (int)Math.Floor((usable + 20) / (minimum + 20)));
        _wallCardWidth = Math.Max(80, Math.Floor((usable - 20 * (columns - 1)) / columns * 4) / 4);
        _wallRepeater.Width = _wallSkeletons.Width = usable;
        _wallRepeater.HorizontalAlignment = _wallSkeletons.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var layout in new[] { _wallGridLayout, _wallSkeletonLayout })
        {
            layout.MaximumRowsOrColumns = columns;
            layout.MinItemWidth = _wallCardWidth;
            layout.MinItemHeight = _wallCardWidth * 1.5 + 48;
        }
        for (var index = 0; index < 16; index++)
            if (_wallSkeletons.TryGetElement(index) is CatalogSkeletonCard skeleton) skeleton.SetWidth(_wallCardWidth);
        if (_renderedPage == "wall") foreach (var card in _realizedCards) card.SetCardWidth(_wallCardWidth);
        _alphabet.Margin = new Thickness(0, 170, Math.Max(8, _pageMargin - 34), 0);
        UpdateAlphabet(ViewModel.Items.FirstOrDefault(item => item.Id == _ambienceId));
        ToolTipService.SetToolTip(_posterSize, LumenText.Get("Poster width: {0} px", minimum));
    }

    private void ApplyWallTheme()
    {
        _wallTitle.Foreground = LumenTheme.Brush("Ink");
        _wallCount.Foreground = LumenTheme.Brush("Sub");
        _posterSize.Foreground = LumenTheme.Brush("Accent");
        _posterSize.Background = LumenTheme.Brush("Track");
        if (!_wallSliderResourcesInstalled)
        {
            foreach (var state in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
            {
                var disabled = state == "Disabled";
                _posterSize.Resources["SliderThumbBackground" + state] = LumenTheme.Brush(disabled ? "Muted" : "Accent");
                _posterSize.Resources["SliderTrackValueFill" + state] = LumenTheme.Brush(disabled ? "Muted" : "Accent");
                _posterSize.Resources["SliderTrackFill" + state] = LumenTheme.Brush("Track");
            }
            _posterSize.Resources["SliderOuterThumbBackground"] = LumenTheme.Brush("Thumb");
            _posterSize.Resources["SliderThumbBorderBrush"] = LumenTheme.Brush("LineStrong");
            _wallSliderResourcesInstalled = true;
        }
        _updatingWallControls = true;
        _posterSize.Value = Math.Clamp(_preferences.PosterWidth, 110, 220);
        _updatingWallControls = false;
        _genreSignature = _wallEmptySignature = string.Empty;
        _sortFlyout.FlyoutPresenterStyle = PopupStyle();
        _filterFlyout.FlyoutPresenterStyle = PopupStyle(16);
    }

    private void RenderWallEmpty()
    {
        var visible = !ViewModel.IsBusy && ViewModel.Items.Count == 0;
        _wallEmpty.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var filtered = ViewModel.BrowseGenre is not null || ViewModel.BrowseWatchFilter != WatchStatusFilter.All
            || ViewModel.BrowseQualityFilter is not null || ViewModel.BrowseNameStartsWith is not null;
        var signature = $"{ViewModel.IsFavorites}:{filtered}:{ViewModel.HasError}:{LumenTheme.IsDark}:{LumenTheme.AccentName}";
        if (_wallEmptySignature == signature) return;
        _wallEmptySignature = signature;
        _wallEmpty.Children.Clear();
        var favorites = ViewModel.IsFavorites && !filtered && !ViewModel.HasError;
        _wallEmpty.Children.Add(EmptyStateIcon(favorites ? "heart" : filtered ? "filter" : "library", favorites ? 88 : 80));
        var title = Label(favorites ? "No favorites yet" : filtered ? "No items match your filters" : "Your library is empty", favorites ? 26 : 24, true);
        title.Margin = new Thickness(0, 10, 0, 0);
        title.TextAlignment = TextAlignment.Center;
        _wallEmpty.Children.Add(title);
        var message = Label(favorites ? "Favorite a movie or series to find it here." : filtered ? "Try a different genre or clear your filters." : ViewModel.HasError ? ViewModel.ErrorMessage : "Refresh your library or browse another collection.", 14);
        message.Foreground = LumenTheme.Brush("Sub");
        message.TextAlignment = TextAlignment.Center;
        message.LineHeight = 24;
        _wallEmpty.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var primary = LumenUi.Button(filtered ? "Clear filters" : favorites ? "Browse movies" : "Refresh", primary: true, height: 40);
        primary.Click += async (_, _) =>
        {
            if (filtered) await ClearWallFiltersAsync();
            else if (favorites) await NavigateAsync("movies");
            else await RefreshAsync();
        };
        actions.Children.Add(primary);
        if (favorites)
        {
            var home = LumenUi.Button("Back to home", height: 40);
            home.Click += async (_, _) => await NavigateAsync("home");
            actions.Children.Add(home);
        }
        _wallEmpty.Children.Add(actions);
    }

    private static Border EmptyStateIcon(string icon, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = LumenTheme.Brush("Chip"),
        BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(1), Child = LumenUi.Icon(icon, size == 88 ? 34 : 30)
    };

    private sealed partial class LibraryCardFactory(LumenLibraryView owner, Func<double> width, bool list = false, Func<string?>? query = null) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not MediaCardViewModel item) return new Border();
            if (list) return owner.MakeListRow(item);
            var search = query?.Invoke();
            var card = owner.MakeCard(item, width: width(), query: search);
            if (query is null) card.SetLibraryWallStyle();
            else card.SetSearchPosterStyle();
            if (!string.IsNullOrWhiteSpace(search))
            {
                card.OpenRequested += (_, _) => owner.RememberSearchQuery(card.SearchContext ?? search);
                card.PlayRequested += (_, _) => owner.RememberSearchQuery(card.SearchContext ?? search);
            }
            return card;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            // Artwork cancels when the element unloads; detached controls are not pooled.
        }
    }

    private sealed partial class CatalogWrapPanel : Panel
    {
        public double HorizontalSpacing { get; init; } = 8;
        public double VerticalSpacing { get; init; } = 8;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : double.PositiveInfinity;
            var x = 0d;
            var y = 0d;
            var lineHeight = 0d;
            var desiredWidth = 0d;
            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(width, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > width) { desiredWidth = Math.Max(desiredWidth, x - HorizontalSpacing); x = 0; y += lineHeight + VerticalSpacing; lineHeight = 0; }
                x += size.Width + HorizontalSpacing;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return new Size(Math.Min(width, Math.Max(desiredWidth, Math.Max(0, x - HorizontalSpacing))), y + lineHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var x = 0d;
            var y = 0d;
            var lineHeight = 0d;
            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width) { x = 0; y += lineHeight + VerticalSpacing; lineHeight = 0; }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width + HorizontalSpacing;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
            return finalSize;
        }
    }
}
