using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private sealed class HomeShelfState(string key, string title, MediaCardViewModel[] items)
    {
        public string Key { get; } = key;
        public string Title { get; } = title;
        public MediaCardViewModel[] Items { get; } = items;
        public Grid Page { get; } = new();
        public Grid Layout { get; } = new();
        public ScrollViewer Viewport { get; } = new()
        {
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false
        };
        public StackPanel Block { get; } = new();
        public Border InfoHost { get; } = new() { Opacity = .66 };
        public StackPanel Info { get; } = new() { MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
        public TextBlock Eyebrow { get; } = LumenUi.Text(string.Empty, 12.5);
        public TextBlock Heading { get; } = LumenUi.Text(string.Empty, 36, true);
        public TextBlock Metadata { get; } = LumenUi.Text(string.Empty, 13.5);
        public TextBlock Overview { get; } = LumenUi.Text(string.Empty, 14);
        public Grid RowHost { get; } = new() { Height = 80, Opacity = .8 };
        public Grid Row { get; } = new() { VerticalAlignment = VerticalAlignment.Top };
        public Grid Header { get; } = new() { Height = 36 };
        public Grid Lane { get; } = new();
        public StackPanel Cards { get; } = new() { Orientation = Orientation.Horizontal };
        public ScrollViewer Scroller { get; } = new()
        {
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false
        };
        public Button Previous { get; } = LumenUi.IconButton("chevron-left", "Previous", 46, true);
        public Button Next { get; } = LumenUi.IconButton("chevron-right", "Next", 46, true);
        public Border Fade { get; } = new() { Height = 44, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        public DispatcherTimer HoverTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(160) };
        public DispatcherTimer ExpandTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(1050) };
        public DispatcherTimer TextTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(240) };
        public DispatcherTimer CollapseTimer { get; } = new() { Interval = TimeSpan.FromMilliseconds(280) };
        public Storyboard? RowAnimation { get; set; }
        public int SelectedIndex { get; set; }
        public int DisplayIndex { get; set; }
        public int HoverIndex { get; set; } = -1;
        public bool Expanded { get; set; }
        public bool PointerWithin { get; set; }
        public double CardWidth { get; set; } = 200;
        public void Stop()
        {
            HoverTimer.Stop();
            ExpandTimer.Stop();
            TextTimer.Stop();
            CollapseTimer.Stop();
            RowAnimation?.Stop();
            RowAnimation = null;
        }
    }

    private void RebuildHomePages(MediaCardViewModel[] resume, MediaCardViewModel[] movies, MediaCardViewModel[] shows)
    {
        var selectedPage = _homeDeck.SelectedIndex;
        var selections = _homeShelves.ToDictionary(shelf => shelf.Key,
            shelf => shelf.Items.ElementAtOrDefault(shelf.SelectedIndex)?.Id, StringComparer.Ordinal);
        foreach (var shelf in _homeShelves) shelf.Stop();
        _homeShelves.Clear();
        _homeShelves.Add(BuildHomeShelf("continue", "Continue watching", resume, null));
        _homeShelves.Add(BuildHomeShelf("movies", "Latest movies", movies, () => NavigateHomeLibraryAsync("movies")));
        _homeShelves.Add(BuildHomeShelf("series", "Latest series", shows, () => NavigateHomeLibraryAsync("series")));
        foreach (var shelf in _homeShelves)
        {
            shelf.SelectedIndex = Math.Max(0, Array.FindIndex(shelf.Items, item => item.Id == selections.GetValueOrDefault(shelf.Key)));
            shelf.DisplayIndex = shelf.SelectedIndex;
            RenderHomeShelfText(shelf);
            RefreshHomeShelfSelection(shelf);
        }
        _homeDeck.SetPages(new[] { (LumenText.Get("Featured"), (FrameworkElement)_hero) }
            .Concat(_homeShelves.Select(shelf => (LumenText.Get(shelf.Title), (FrameworkElement)shelf.Page))));
        _homeDeck.GoToPage(selectedPage, false);
        UpdateHomeLayout(Math.Max(640, ActualWidth));
    }

    private HomeShelfState BuildHomeShelf(string key, string title, MediaCardViewModel[] items, Func<Task>? seeAll)
    {
        var state = new HomeShelfState(key, title, items);
        AutomationProperties.SetAutomationId(state.Page, "LumenHome" + key + "Page");
        AutomationProperties.SetAutomationId(state.Scroller, "LumenHome" + key + "Scroller");
        state.Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        state.Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(state.Block, 1);
        state.Layout.Children.Add(state.Block);
        state.Viewport.Content = state.Layout;
        state.Page.Children.Add(state.Viewport);
        state.InfoHost.Child = state.Info;
        state.Info.Loaded += (_, _) =>
        {
            state.DisplayIndex = state.SelectedIndex;
            RenderHomeShelfText(state);
            ResetHomeShelfInformationAnimation(state);
        };
        state.Eyebrow.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        state.Eyebrow.CharacterSpacing = 140;
        state.Eyebrow.Opacity = .62;
        state.Eyebrow.TextWrapping = TextWrapping.NoWrap;
        state.Eyebrow.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Heading.Margin = new Thickness(0, 8, 0, 0);
        state.Heading.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        state.Heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        state.Heading.MaxLines = 2;
        state.Heading.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Metadata.Margin = new Thickness(0, 8, 0, 0);
        state.Metadata.Opacity = .65;
        state.Overview.Margin = new Thickness(0, 12, 0, 0);
        state.Overview.MaxWidth = 540;
        state.Overview.MaxLines = 2;
        state.Overview.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Overview.LineHeight = 24.5;
        state.Overview.Opacity = .72;
        foreach (var text in new[] { state.Eyebrow, state.Heading, state.Metadata, state.Overview })
        {
            text.Foreground = LumenTheme.Brush("ImageInk");
            state.Info.Children.Add(text);
        }
        state.Block.Children.Add(state.InfoHost);

        state.Row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        state.Row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        state.Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        state.Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        var label = Label(title, 15);
        label.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        label.Foreground = LumenTheme.Brush("ImageInk");
        labels.Children.Add(label);
        var count = Label(key == "continue" ? "{0} entries" : "{0} items", 13);
        count.Text = LumenText.Get(key == "continue" ? "{0} entries" : "{0} items", items.Length);
        count.Foreground = LumenTheme.Brush("ImageInk");
        count.Opacity = .5;
        labels.Children.Add(count);
        state.Header.Children.Add(labels);
        if (seeAll is not null)
        {
            var more = new HyperlinkButton
            {
                Content = LumenText.Get("View all") + "  ›", FontFamily = LumenTheme.SansFont,
                Foreground = LumenTheme.Brush("ImageInk"), FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Padding = new Thickness(0),
                Opacity = .7, VerticalAlignment = VerticalAlignment.Center
            };
            more.Click += async (_, _) => await seeAll();
            Grid.SetColumn(more, 1);
            state.Header.Children.Add(more);
        }
        state.Row.Children.Add(state.Header);
        Grid.SetRow(state.Lane, 1);
        state.Row.Children.Add(state.Lane);
        state.Scroller.Content = state.Cards;
        state.Lane.Children.Add(state.Scroller);
        state.Cards.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler((_, args) => _homeDeck.HandleVerticalWheel(args)), true);
        LumenMediaCard.ConfigureNavigationGroup(state.Cards, true);
        for (var index = 0; index < items.Length; index++)
        {
            var selectedIndex = index;
            var item = items[index];
            var card = MakeCard(item, true, state.CardWidth);
            card.SetHomeStripStyle();
            card.SetPrimaryActionToPlay(key == "continue");
            card.SetContinueWatchingContext(key == "continue");
            card.SetSubtitleOverride(HomeShelfSubtitle(item, key));
            card.PointerEntered += (_, _) =>
            {
                state.HoverIndex = selectedIndex;
                state.HoverTimer.Stop();
                state.HoverTimer.Start();
            };
            card.PointerExited += (_, _) => { state.HoverIndex = -1; state.HoverTimer.Stop(); };
            state.Cards.Children.Add(card);
        }
        state.Previous.HorizontalAlignment = HorizontalAlignment.Left;
        state.Next.HorizontalAlignment = HorizontalAlignment.Right;
        state.Previous.VerticalAlignment = state.Next.VerticalAlignment = VerticalAlignment.Top;
        state.Previous.Background = state.Next.Background = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 20, 18, 16), TintOpacity = .62,
            TintLuminosityOpacity = .15, FallbackColor = Windows.UI.Color.FromArgb(224, 20, 18, 16)
        };
        state.Lane.Children.Add(state.Previous);
        state.Lane.Children.Add(state.Next);
        state.Previous.Click += (_, _) => ScrollHomeShelf(state, -1);
        state.Next.Click += (_, _) => ScrollHomeShelf(state, 1);
        state.Scroller.ViewChanged += (_, _) => RefreshHomeShelfArrows(state);
        state.Scroller.SizeChanged += (_, _) => RefreshHomeShelfArrows(state);
        state.Scroller.PointerWheelChanged += (_, args) =>
        {
            var properties = args.GetCurrentPoint(state.Scroller).Properties;
            if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0
                || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == 0) return;
            state.Scroller.ChangeView(Math.Clamp(state.Scroller.HorizontalOffset - properties.MouseWheelDelta,
                0, state.Scroller.ScrollableWidth), null, null, true);
            args.Handled = true;
        };
        state.Cards.AddHandler(KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
            SetHomeShelfExpanded(state, true);
            state.CollapseTimer.Interval = TimeSpan.FromMilliseconds(2600);
            state.CollapseTimer.Stop();
            state.CollapseTimer.Start();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (XamlRoot is null || !state.Page.IsLoaded) return;
                var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                for (var index = 0; index < state.Cards.Children.Count; index++)
                    if (IsDescendantOf(focused, state.Cards.Children[index]))
                    {
                        SelectHomeShelf(state, index);
                        break;
                    }
            });
        }), true);
        state.RowHost.Children.Add(state.Row);
        state.Fade.Background = HomeGradient(false, (0, 0, 8, 8, 9), (1, 235, 8, 8, 9));
        state.RowHost.Children.Add(state.Fade);
        state.RowHost.SizeChanged += (_, _) =>
            state.RowHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, state.RowHost.ActualWidth, state.RowHost.ActualHeight) };
        state.RowHost.PointerEntered += (_, _) =>
        {
            state.PointerWithin = true;
            state.CollapseTimer.Stop();
            state.ExpandTimer.Stop();
            var remaining = 1050 - (Environment.TickCount64 - _homeFocusResetAt);
            if (remaining > 0)
            {
                state.ExpandTimer.Interval = TimeSpan.FromMilliseconds(remaining + 20);
                state.ExpandTimer.Start();
            }
            else SetHomeShelfExpanded(state, true);
        };
        state.RowHost.PointerExited += (_, _) =>
        {
            state.PointerWithin = false;
            state.ExpandTimer.Stop();
            state.CollapseTimer.Interval = TimeSpan.FromMilliseconds(280);
            state.CollapseTimer.Stop();
            state.CollapseTimer.Start();
        };
        state.HoverTimer.Tick += (_, _) =>
        {
            state.HoverTimer.Stop();
            if (state.HoverIndex >= 0) SelectHomeShelf(state, state.HoverIndex);
        };
        state.ExpandTimer.Tick += (_, _) =>
        {
            state.ExpandTimer.Stop();
            if (state.PointerWithin && state.Page.IsLoaded && _homeShelves.IndexOf(state) + 1 == _homeDeck.SelectedIndex)
                SetHomeShelfExpanded(state, true);
        };
        state.TextTimer.Tick += (_, _) =>
        {
            state.TextTimer.Stop();
            state.DisplayIndex = state.SelectedIndex;
            RenderHomeShelfText(state);
            AnimateHomeShelfInformation(state, false);
        };
        state.CollapseTimer.Tick += (_, _) =>
        {
            state.CollapseTimer.Stop();
            if (!state.PointerWithin) SetHomeShelfExpanded(state, false);
        };
        state.Page.Unloaded += (_, _) => state.Stop();
        state.RowHost.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        state.Block.Children.Add(state.RowHost);
        return state;
    }

    private void SelectHomeShelf(HomeShelfState shelf, int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, shelf.Items.Length - 1));
        if (index == shelf.SelectedIndex) return;
        shelf.SelectedIndex = index;
        RefreshHomeShelfSelection(shelf);
        shelf.TextTimer.Stop();
        if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            AnimateHomeShelfInformation(shelf, true);
            shelf.TextTimer.Start();
        }
        else
        {
            shelf.DisplayIndex = index;
            RenderHomeShelfText(shelf);
        }
        if (_homeShelves.IndexOf(shelf) + 1 == _homeDeck.SelectedIndex) UpdateHomeStage();
    }

    private void RefreshHomeShelfSelection(HomeShelfState shelf)
    {
        for (var index = 0; index < shelf.Cards.Children.Count; index++)
            if (shelf.Cards.Children[index] is LumenMediaCard card) card.SetSelected(index == shelf.SelectedIndex);
    }

    private void RenderHomeShelfText(HomeShelfState shelf)
    {
        if (shelf.Items.ElementAtOrDefault(shelf.DisplayIndex) is not { } item)
        {
            shelf.Eyebrow.Text = LumenText.Get(shelf.Title);
            shelf.Heading.Text = LumenText.Get(shelf.Key == "continue"
                ? "Your unfinished movies and episodes will appear here."
                : "New additions to this library will appear here.");
            shelf.Metadata.Text = string.Empty;
            shelf.Overview.Text = string.Empty;
            return;
        }
        shelf.Heading.Text = item.DisplayTitle;
        var episode = item.Item.Type == "Episode";
        shelf.Eyebrow.Text = shelf.Key == "continue"
            ? LumenText.Get("Continue watching") + " · " + (episode ? item.EpisodeNumber : LumenText.Get("Movie"))
            : string.Join(" · ", new[] { LumenText.Get(shelf.Title), item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
                item.Item.Genres is { Length: > 0 } genres ? string.Join(" / ", genres) : null }.Where(value => !string.IsNullOrWhiteSpace(value)));
        shelf.Metadata.Text = shelf.Key == "continue"
            ? string.Join(" · ", new[] { episode ? item.Title : item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
                episode ? null : item.Item.Genres is { Length: > 0 } metadataGenres ? string.Join(" / ", metadataGenres) : null,
                RemainingHomeTime(item) }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Join(" · ", new[] { item.Item.CommunityRating is { } rating ? "★ " + rating.ToString("0.0", CultureInfo.InvariantCulture) : null,
                item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons ? LumenText.Get("{0} seasons", seasons) : RuntimeText(item.Item.RunTimeTicks),
                HeroTechnicalTags(item.Item).FirstOrDefault() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        shelf.Overview.Text = item.Item.Overview ?? string.Empty;
        shelf.Overview.Visibility = ActualHeight >= 620 && !string.IsNullOrWhiteSpace(shelf.Overview.Text) ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < shelf.Cards.Children.Count; index++)
            if (shelf.Cards.Children[index] is LumenMediaCard card) card.SetSubtitleOverride(HomeShelfSubtitle(card.Item, shelf.Key));
    }

    private void SetHomeShelfExpanded(HomeShelfState shelf, bool expanded)
    {
        if (shelf.Expanded == expanded) return;
        if (expanded)
            foreach (var other in _homeShelves.Where(other => !ReferenceEquals(other, shelf))) SetHomeShelfExpanded(other, false);
        shelf.Expanded = expanded;
        var from = shelf.RowHost.ActualHeight > 0 ? shelf.RowHost.ActualHeight : shelf.RowHost.Height;
        var target = expanded ? shelf.CardWidth * 9 / 16 + 109 : 80;
        shelf.RowAnimation?.Stop();
        shelf.RowHost.Height = target;
        SetHeroOpacity(shelf.RowHost, expanded ? 1 : .8, 400);
        SetHeroOpacity(shelf.Fade, expanded ? 0 : 1, 400);
        if (shelf.RowHost.IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(550), EnableDependentAnimation = true
            };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame { Value = from, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                Value = target, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(550)),
                KeySpline = new KeySpline { ControlPoint1 = new Point(.2, .8), ControlPoint2 = new Point(.2, 1) }
            });
            Storyboard.SetTarget(animation, shelf.RowHost);
            Storyboard.SetTargetProperty(animation, "Height");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            shelf.RowAnimation = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(shelf.RowAnimation, storyboard)) return;
                storyboard.Stop();
                shelf.RowAnimation = null;
            };
            storyboard.Begin();
        }
        UpdateHomeStageShades();
    }

    private static void AnimateHomeShelfInformation(HomeShelfState shelf, bool leaving)
    {
        if (!shelf.Info.IsLoaded) return;
        ElementCompositionPreview.SetIsTranslationEnabled(shelf.Info, true);
        var visual = ElementCompositionPreview.GetElementVisual(shelf.Info);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, leaving ? 1 : 0);
        opacity.InsertKeyFrame(1, leaving ? 0 : 1,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1)));
        opacity.Duration = TimeSpan.FromMilliseconds(300);
        visual.StartAnimation("Opacity", opacity);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, leaving ? Vector3.Zero : new Vector3(0, 10, 0));
        translation.InsertKeyFrame(1, leaving ? new Vector3(0, 10, 0) : Vector3.Zero,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
        translation.Duration = TimeSpan.FromMilliseconds(450);
        visual.Properties.StartAnimation("Translation", translation);
    }

    private static void ResetHomeShelfInformationAnimation(HomeShelfState shelf)
    {
        if (!shelf.Info.IsLoaded) return;
        ElementCompositionPreview.SetIsTranslationEnabled(shelf.Info, true);
        var visual = ElementCompositionPreview.GetElementVisual(shelf.Info);
        // Custom composition properties must exist before their animations can be stopped.
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Properties.StopAnimation("Translation");
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
    }

    private static void RefreshHomeShelfArrows(HomeShelfState shelf)
    {
        var previous = shelf.Scroller.HorizontalOffset > 4;
        var next = shelf.Scroller.HorizontalOffset < shelf.Scroller.ScrollableWidth - 4;
        shelf.Previous.IsEnabled = previous;
        shelf.Next.IsEnabled = next;
        shelf.Previous.Opacity = previous ? 1 : 0;
        shelf.Next.Opacity = next ? 1 : 0;
    }

    private static void ScrollHomeShelf(HomeShelfState shelf, int direction) =>
        shelf.Scroller.ChangeView(Math.Clamp(shelf.Scroller.HorizontalOffset + direction * Math.Max(320, shelf.Scroller.ViewportWidth * .8),
            0, shelf.Scroller.ScrollableWidth), null, null);

    private Task NavigateHomeLibraryAsync(string type)
        => NavigateLatestMediaAsync(type == "series" ? "Series" : "Movie");

    private static string HomeShelfSubtitle(MediaCardViewModel item, string key) => key == "continue"
        ? item.Item.Type == "Episode" ? item.CardSubtitle : LumenText.Get("Movie") + " · " + RemainingHomeTime(item)
        : string.Join(" · ", new[] { item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture),
            item.Item.Genres?.FirstOrDefault(), item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons
                ? LumenText.Get("{0} seasons", seasons) : null }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string RemainingHomeTime(MediaCardViewModel item) => item.Item.RunTimeTicks is > 0
        ? LumenText.Get("{0} remaining", RuntimeText(Math.Max(0, item.Item.RunTimeTicks.Value - item.ResumeTicks))) : string.Empty;

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
        var minutes = Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(ticks.Value).TotalMinutes));
        return minutes >= 60 ? LumenText.Get("{0} h {1} min", minutes / 60, minutes % 60) : LumenText.Get("{0} min", minutes);
    }

    private static string MediaMetadata(MediaCardViewModel item) => string.Join("  ·  ", new[]
    {
        item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture), item.Item.Genres?.FirstOrDefault(),
        item.Item.Type == "Series" && item.Item.SeasonCount is { } seasons ? LumenText.Get("{0} seasons", seasons) : RuntimeText(item.Item.RunTimeTicks),
        item.Item.CommunityRating is { } rating ? "★ " + rating.ToString("0.0", CultureInfo.InvariantCulture) : null,
        item.MediaFormat.Split(" · ", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
