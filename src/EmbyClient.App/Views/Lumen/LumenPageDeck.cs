using EmbyClient.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

/// <summary>Native full-window chapters with a continuous-scroll option and bounded wheel gestures.</summary>
public sealed partial class LumenPageDeck : UserControl
{
    private readonly Grid _root = new();
    private readonly Grid _stage = new();
    private readonly StackPanel _continuousPages = new();
    private readonly ScrollViewer _continuousScroller = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        IsTabStop = false
    };
    private readonly StackPanel _ticks = new()
    {
        Width = 26, Padding = new Thickness(0, 8, 0, 8), Spacing = 2,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly Border _rail;
    private readonly Button _next;
    private readonly ToolTip _nextToolTip = RailToolTip(string.Empty);
    private readonly FrameworkElement _nextIcon = LumenUi.Icon("chevron-down", 14, true);
    private readonly Border _nextSeparator = new()
    {
        Width = 10, Height = 1, Margin = new Thickness(0, 4, 0, 4),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(51, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false
    };
    private readonly List<DeckPage> _pages = [];
    private bool _continuous;
    private bool _arranging;
    private bool _themeSubscribed;
    private long _lastNavigation;
    private long _lastWheel;
    private int _lastWheelMagnitude;
    private int _animationVersion;
    private double _lastLayoutHeight;
    private ulong _routedWheelTimestamp;
    private double? _pendingContinuousOffset;
    private bool _restoringScroll;
    private bool _nextPointsUp;

    public LumenPageDeck()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        _continuousScroller.Content = _continuousPages;
        _continuousScroller.Visibility = Visibility.Collapsed;
        _root.Children.Add(_stage);
        _root.Children.Add(_continuousScroller);
        _rail = new Border
        {
            Width = 26, CornerRadius = new CornerRadius(20),
            Background = new AcrylicBrush
            {
                TintColor = Windows.UI.Color.FromArgb(255, 20, 18, 16),
                TintOpacity = .36, TintLuminosityOpacity = .15,
                FallbackColor = Windows.UI.Color.FromArgb(220, 20, 18, 16)
            },
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _ticks
        };
        Canvas.SetZIndex(_rail, 20);
        _root.Children.Add(_rail);
        _next = RailButton(_nextIcon, 26);
        ToolTipService.SetToolTip(_next, _nextToolTip);
        ToolTipService.SetPlacement(_next, PlacementMode.Left);
        _nextIcon.Opacity = .72;
        _next.PointerEntered += (_, _) => _nextIcon.Opacity = 1;
        _next.PointerExited += (_, _) => _nextIcon.Opacity = .72;
        _next.Click += (_, _) => GoToPage(SelectedIndex >= _pages.Count - 1 ? 0 : SelectedIndex + 1);
        AutomationProperties.SetAutomationId(_next, "LumenNextPage");
        Content = _root;
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheelChanged), true);
        KeyDown += (_, args) => HandleNavigationKey(args);
        SizeChanged += (_, _) => RefreshLayout();
        _continuousScroller.ViewChanged += (_, _) => ContinuousViewChanged();
        _continuousScroller.LayoutUpdated += (_, _) => RestoreContinuousOffset();
        Loaded += (_, _) =>
        {
            if (!_themeSubscribed) { LumenTheme.Changed += ThemeChanged; _themeSubscribed = true; }
            RefreshLayout();
        };
        Unloaded += (_, _) =>
        {
            _animationVersion++;
            if (_themeSubscribed) LumenTheme.Changed -= ThemeChanged;
            _themeSubscribed = false;
        };
        AutomationProperties.SetName(_rail, LumenText.Get("Page navigation"));
        RefreshRail();
    }

    public bool ContinuousScroll
    {
        get => _continuous;
        set
        {
            if (_continuous == value) return;
            _continuous = value;
            MountPages();
        }
    }

    public int SelectedIndex { get; private set; }
    public bool LastPageChangeAnimated { get; private set; }
    public double ViewportHeight => Math.Max(1, ActualHeight);
    public double ContinuousOffset
    {
        get => _pendingContinuousOffset ?? _continuousScroller.VerticalOffset;
        set
        {
            _pendingContinuousOffset = double.IsFinite(value) ? Math.Max(0, value) : 0;
            RestoreContinuousOffset();
        }
    }
    public event EventHandler<int>? PageChanged;

    public void SetPages(IEnumerable<(string Title, FrameworkElement Content)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pendingContinuousOffset = _continuous ? ContinuousOffset : null;
        _animationVersion++;
        _stage.Children.Clear();
        _continuousPages.Children.Clear();
        foreach (var page in _pages) page.Scroller.Content = null;
        _ticks.Children.Clear();
        _pages.Clear();
        foreach (var (title, content) in pages)
        {
            var scroller = new ScrollViewer
            {
                Content = content, HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                IsTabStop = false
            };
            var mark = new Border
            {
                Width = 4, Height = 6, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var tick = RailButton(mark, 24);
            var index = _pages.Count;
            tick.Click += (_, _) => GoToPage(index);
            ToolTipService.SetToolTip(tick, RailToolTip(title));
            ToolTipService.SetPlacement(tick, PlacementMode.Left);
            AutomationProperties.SetName(tick, title);
            AutomationProperties.SetAutomationId(tick, $"LumenPage{index + 1}");
            _pages.Add(new DeckPage(title, content, scroller, tick, mark));
            _ticks.Children.Add(tick);
        }
        _ticks.Children.Add(_nextSeparator);
        _ticks.Children.Add(_next);
        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, _pages.Count - 1));
        MountPages();
    }

    public void RefreshLayout()
    {
        if (_arranging || ActualWidth <= 0 || ActualHeight <= 0) return;
        _arranging = true;
        try
        {
            _root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
            _rail.Margin = new Thickness(0, 0, Math.Clamp(ActualWidth * .014, 8, 24), 0);
            foreach (var page in _pages)
            {
                page.Scroller.Height = ActualHeight;
                page.Content.MinHeight = ActualHeight;
            }
            if (_continuous && Math.Abs(_lastLayoutHeight - ActualHeight) > .5)
                _pendingContinuousOffset ??= SelectedIndex * ActualHeight;
            _lastLayoutHeight = ActualHeight;
            RefreshRail();
        }
        finally { _arranging = false; }
        RestoreContinuousOffset();
    }

    public void GoToPage(int index, bool animate = true)
    {
        if (_pages.Count == 0) return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        var previous = SelectedIndex;
        if (previous == index)
        {
            if (_continuous) _continuousScroller.ChangeView(null, index * ViewportHeight, null, !animate);
            return;
        }
        SelectedIndex = index;
        LastPageChangeAnimated = animate && !_continuous && IsLoaded && AnimationsEnabled();
        _lastNavigation = Environment.TickCount64;
        if (_continuous)
            _continuousScroller.ChangeView(null, index * ViewportHeight, null, !animate || !AnimationsEnabled());
        else ShowPage(previous, index, animate);
        RefreshRail();
        PageChanged?.Invoke(this, index);
    }

    public bool HandleNavigationKey(KeyRoutedEventArgs args)
    {
        if (args.Handled || _pages.Count < 2 || HasModifier(VirtualKey.Control) || HasModifier(VirtualKey.Menu)
            || HasModifier(VirtualKey.Shift) || OwnsKeyboardInput(args.OriginalSource as DependencyObject, args.Key)) return false;
        if (_continuous)
        {
            var target = args.Key switch
            {
                VirtualKey.Down => _continuousScroller.VerticalOffset + 90,
                VirtualKey.Up => _continuousScroller.VerticalOffset - 90,
                VirtualKey.PageDown or VirtualKey.Space => _continuousScroller.VerticalOffset + .88 * ViewportHeight,
                VirtualKey.PageUp => _continuousScroller.VerticalOffset - .88 * ViewportHeight,
                VirtualKey.Home => 0,
                VirtualKey.End => _continuousScroller.ScrollableHeight,
                _ => double.NaN
            };
            if (double.IsNaN(target)) return false;
            if (args.Key is not (VirtualKey.Home or VirtualKey.End)
                && CanScrollInner(args.OriginalSource as DependencyObject, target > _continuousScroller.VerticalOffset ? 1 : -1)) return false;
            _continuousScroller.ChangeView(null, Math.Clamp(target, 0, _continuousScroller.ScrollableHeight), null, !AnimationsEnabled());
            args.Handled = true;
            return true;
        }
        var next = args.Key switch
        {
            VirtualKey.Down or VirtualKey.PageDown or VirtualKey.Space => SelectedIndex + 1,
            VirtualKey.Up or VirtualKey.PageUp => SelectedIndex - 1,
            VirtualKey.Home => 0,
            VirtualKey.End => _pages.Count - 1,
            _ => -1
        };
        if (next < 0 && args.Key is not (VirtualKey.Up or VirtualKey.PageUp)) return false;
        if (args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Space)
        {
            var delta = next > SelectedIndex ? 1 : -1;
            if (CanScrollInner(args.OriginalSource as DependencyObject, delta)) return false;
        }
        args.Handled = true;
        if (args.Key is VirtualKey.Home or VirtualKey.End || Environment.TickCount64 - _lastNavigation >= 1050) GoToPage(next);
        return true;
    }

    /// <summary>Routes a row's vertical wheel before its horizontal ScrollViewer can consume it.</summary>
    public bool HandleVerticalWheel(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (point.Properties.IsHorizontalMouseWheel || point.Properties.MouseWheelDelta == 0
            || HasModifier(VirtualKey.Shift) || HasModifier(VirtualKey.Control)) return false;
        var delta = point.Properties.MouseWheelDelta < 0 ? 1 : -1;
        var inner = FindScrollableInner(args.OriginalSource as DependencyObject, delta);
        if (inner is not null)
        {
            inner.ChangeView(null, Math.Clamp(inner.VerticalOffset - point.Properties.MouseWheelDelta, 0, inner.ScrollableHeight), null, !AnimationsEnabled());
            args.Handled = true;
        }
        else if (_continuous)
        {
            _continuousScroller.ChangeView(null,
                Math.Clamp(_continuousScroller.VerticalOffset - point.Properties.MouseWheelDelta, 0, _continuousScroller.ScrollableHeight),
                null, !AnimationsEnabled());
            args.Handled = true;
        }
        else OnWheelChanged(this, args);
        _routedWheelTimestamp = point.Timestamp;
        return args.Handled;
    }

    private void MountPages()
    {
        LastPageChangeAnimated = false;
        _animationVersion++;
        _stage.Children.Clear();
        _continuousPages.Children.Clear();
        _stage.Visibility = _continuous ? Visibility.Collapsed : Visibility.Visible;
        _continuousScroller.Visibility = _continuous ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            page.Scroller.Visibility = _continuous || index == SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            page.Scroller.IsHitTestVisible = _continuous || index == SelectedIndex;
            page.Scroller.IsEnabled = _continuous || index == SelectedIndex;
            var visual = ElementCompositionPreview.GetElementVisual(page.Scroller);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            if (_continuous) _continuousPages.Children.Add(page.Scroller);
            else _stage.Children.Add(page.Scroller);
        }
        RefreshLayout();
        if (_continuous) _pendingContinuousOffset ??= SelectedIndex * ViewportHeight;
        else _pendingContinuousOffset = null;
        RestoreContinuousOffset();
        RefreshRail();
    }

    private void ShowPage(int previous, int index, bool animate)
    {
        var version = ++_animationVersion;
        for (var pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            var page = _pages[pageIndex];
            page.Scroller.IsHitTestVisible = pageIndex == index;
            page.Scroller.IsEnabled = pageIndex == index;
            if (pageIndex != previous && pageIndex != index) page.Scroller.Visibility = Visibility.Collapsed;
        }
        var outgoing = _pages[previous].Scroller;
        var incoming = _pages[index].Scroller;
        incoming.Visibility = Visibility.Visible;
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);
        if (!animate || !IsLoaded || !AnimationsEnabled())
        {
            outgoing.Visibility = Visibility.Collapsed;
            ElementCompositionPreview.GetElementVisual(incoming).Opacity = 1;
            ElementCompositionPreview.GetElementVisual(incoming).Scale = Vector3.One;
            return;
        }
        var down = index > previous;
        AnimatePage(incoming, true, down);
        AnimatePage(outgoing, false, down);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version == _animationVersion) outgoing.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private static void AnimatePage(FrameworkElement page, bool entering, bool down)
    {
        var visual = ElementCompositionPreview.GetElementVisual(page);
        var compositor = visual.Compositor;
        visual.CenterPoint = new Vector3((float)page.ActualWidth / 2, (float)page.ActualHeight / 2, 0);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, entering ? 0 : 1);
        var opacityEase = entering
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(.42f, 0), new Vector2(1, 1));
        opacity.InsertKeyFrame(1, entering ? 1 : 0, opacityEase);
        opacity.Duration = TimeSpan.FromMilliseconds(entering ? 700 : 450);
        if (entering) opacity.DelayTime = TimeSpan.FromMilliseconds(180);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        var ease = entering
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(.45f, 0), new Vector2(.25f, 1));
        var start = entering ? down ? .94f : 1.06f : 1f;
        var end = entering ? 1f : down ? 1.06f : .94f;
        scale.InsertKeyFrame(0, new Vector3(start, start, 1));
        scale.InsertKeyFrame(1, new Vector3(end, end, 1), ease);
        scale.Duration = TimeSpan.FromMilliseconds(entering ? 1300 : 1000);
        if (entering) scale.DelayTime = TimeSpan.FromMilliseconds(50);
        visual.Opacity = entering ? 0 : 1;
        visual.Scale = new Vector3(start, start, 1);
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Scale", scale);
    }

    private void OnWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_continuous || _pages.Count < 2 || HasModifier(VirtualKey.Shift) || HasModifier(VirtualKey.Control)) return;
        var point = args.GetCurrentPoint(this);
        if (point.Timestamp == _routedWheelTimestamp) return;
        var properties = point.Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0) return;
        var delta = properties.MouseWheelDelta < 0 ? 1 : -1;
        if (CanScrollInner(args.OriginalSource as DependencyObject, delta)) return;
        var now = Environment.TickCount64;
        var magnitude = Math.Abs(properties.MouseWheelDelta);
        var freshGesture = now - _lastWheel > 160 || magnitude > _lastWheelMagnitude * 1.5 + 6;
        _lastWheel = now;
        _lastWheelMagnitude = magnitude;
        args.Handled = true;
        if (freshGesture && now - _lastNavigation >= 1050) GoToPage(SelectedIndex + delta);
    }

    private bool CanScrollInner(DependencyObject? source, int direction)
        => FindScrollableInner(source, direction) is not null;

    private ScrollViewer? FindScrollableInner(DependencyObject? source, int direction)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, this); current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ScrollViewer scroller || ReferenceEquals(scroller, _continuousScroller)) continue;
            if (scroller.ScrollableHeight > 1 && (direction > 0
                ? scroller.VerticalOffset < scroller.ScrollableHeight - 1
                : scroller.VerticalOffset > 1)) return scroller;
        }
        return null;
    }

    private void ContinuousViewChanged()
    {
        if (!_continuous || _arranging || _restoringScroll || _pendingContinuousOffset is not null
            || _pages.Count == 0 || ViewportHeight <= 1) return;
        var index = Math.Clamp((int)Math.Round(_continuousScroller.VerticalOffset / ViewportHeight), 0, _pages.Count - 1);
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        LastPageChangeAnimated = false;
        RefreshRail();
        PageChanged?.Invoke(this, index);
    }

    private void RestoreContinuousOffset()
    {
        if (!_continuous || _restoringScroll || _arranging || _pendingContinuousOffset is not { } offset
            || ActualHeight <= 0 || _continuousScroller.ViewportHeight <= 0
            || _continuousScroller.ExtentHeight < _pages.Count * ViewportHeight - 1) return;
        _restoringScroll = true;
        try
        {
            var target = Math.Clamp(offset, 0, _continuousScroller.ScrollableHeight);
            if (_continuousScroller.ChangeView(null, target, null, true)
                || Math.Abs(_continuousScroller.VerticalOffset - target) < 1) _pendingContinuousOffset = null;
        }
        finally { _restoringScroll = false; }
        if (_pendingContinuousOffset is null) ContinuousViewChanged();
    }

    private void RefreshRail()
    {
        _rail.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            SetRailMarkHeight(page.Mark, index == SelectedIndex ? 16 : 6);
            page.Mark.Background = index == SelectedIndex ? LumenTheme.Brush("ImageAccent")
                : new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255));
            AutomationProperties.SetItemStatus(page.Tick, index == SelectedIndex ? LumenText.Get("Current page") : string.Empty);
        }
        var last = SelectedIndex >= _pages.Count - 1;
        var label = last ? LumenText.Get("Back to top")
            : LumenText.Get("Next page: {0}", _pages[SelectedIndex + 1].Title);
        _next.IsEnabled = _pages.Count > 1;
        AutomationProperties.SetName(_next, label);
        _nextToolTip.Content = label;
        if (_nextPointsUp != last)
        {
            _nextPointsUp = last;
            var visual = ElementCompositionPreview.GetElementVisual(_nextIcon);
            var from = visual.RotationAngleInDegrees;
            visual.StopAnimation("RotationAngleInDegrees");
            visual.CenterPoint = new Vector3(7, 7, 0);
            visual.RotationAngleInDegrees = last ? 180 : 0;
            if (_nextIcon.IsLoaded && AnimationsEnabled())
            {
                var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
                rotation.InsertKeyFrame(0, from);
                rotation.InsertKeyFrame(1, visual.RotationAngleInDegrees,
                    visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
                rotation.Duration = TimeSpan.FromMilliseconds(450);
                visual.StartAnimation("RotationAngleInDegrees", rotation);
            }
        }
    }

    private static Button RailButton(FrameworkElement content, double height)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var button = new Button
        {
            Content = content, Width = 26, Height = height, MinWidth = 26, MinHeight = height,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(13),
            Background = transparent, UseSystemFocusVisuals = true
        };
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
            "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" }) button.Resources[key] = transparent;
        return button;
    }

    private static ToolTip RailToolTip(string label) => new()
    {
        Content = label, FontFamily = LumenTheme.SansFont, FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = LumenTheme.Brush("ImageInk"),
        Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(99), BorderThickness = new Thickness(0),
        Background = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 20, 18, 16), TintOpacity = .72,
            TintLuminosityOpacity = .15, FallbackColor = Windows.UI.Color.FromArgb(240, 20, 18, 16)
        }
    };

    private static void SetRailMarkHeight(Border mark, double height)
    {
        if (mark.Height == height) return;
        var previous = mark.Height;
        mark.Height = height;
        var visual = ElementCompositionPreview.GetElementVisual(mark);
        visual.StopAnimation("Scale");
        visual.CenterPoint = new Vector3(2, (float)height / 2, 0);
        visual.Scale = Vector3.One;
        if (!mark.IsLoaded || !AnimationsEnabled()) return;
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(1, (float)(previous / height), 1));
        scale.InsertKeyFrame(1, Vector3.One,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1)));
        scale.Duration = TimeSpan.FromMilliseconds(450);
        visual.StartAnimation("Scale", scale);
    }

    private void ThemeChanged(object? sender, EventArgs args) => RefreshRail();
    private static bool HasModifier(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private static bool AnimationsEnabled() => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private bool OwnsKeyboardInput(DependencyObject? source, VirtualKey key)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, this); current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBox or PasswordBox or ComboBox or Slider or ListViewBase or MenuFlyoutPresenter
                || current is ButtonBase && key == VirtualKey.Space)
                return true;
        }
        return false;
    }

    private sealed record DeckPage(string Title, FrameworkElement Content, ScrollViewer Scroller, Button Tick, Border Mark);
}
