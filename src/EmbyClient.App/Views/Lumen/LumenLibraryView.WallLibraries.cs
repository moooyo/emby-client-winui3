using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private sealed record WallLibraryOption(SelectorBarItem Item, string? LibraryId, TextBlock Title,
        TextBlock Count, Border Dot, SolidColorBrush Hover, SolidColorBrush Pressed);

    private readonly SelectorBar _wallLibrarySelector = new() { Padding = new Thickness(0) };
    private readonly ScrollViewer _wallLibraryStrip = new()
    {
        Height = 40, Margin = new Thickness(-14, 14, 0, 0),
        HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Left, IsTabStop = false, Visibility = Visibility.Collapsed
    };
    private readonly List<WallLibraryOption> _wallLibraryOptions = [];
    private string _wallLibrarySignature = string.Empty;
    private bool _updatingWallLibraries;
    private int _wallLibraryInputVersion;

    public void RestoreMediaLibrarySelection(string type, string? libraryId) =>
        ViewModel.RestoreMediaLibrarySelection(type, libraryId);

    private void BuildWallLibrarySelector()
    {
        _wallLibraryStrip.Content = _wallLibrarySelector;
        AutomationProperties.SetName(_wallLibrarySelector, LumenText.Get("Media library"));
        AutomationProperties.SetAutomationId(_wallLibrarySelector, "LumenMediaLibrarySelector");
        _wallLibrarySelector.SelectionChanged += async (_, _) =>
        {
            var type = ViewModel.BrowseMediaType;
            if (_updatingWallLibraries || type is not ("Movie" or "Series")
                || _wallLibrarySelector.SelectedItem is not { } selected) return;
            var option = _wallLibraryOptions.FirstOrDefault(option => ReferenceEquals(option.Item, selected));
            if (option is not null) await SwitchWallLibraryAsync(type, option.LibraryId);
        };
        _wall.Children.Add(_wallLibraryStrip);
    }

    private void RenderWallLibrarySelector()
    {
        var type = ViewModel.BrowseMediaType;
        IReadOnlyList<MediaCardViewModel> libraries = type is "Movie" or "Series" ? ViewModel.GetMediaLibraries(type) : [];
        _wallLibraryStrip.Visibility = !ViewModel.IsFavorites && libraries.Count > 1
            ? Visibility.Visible : Visibility.Collapsed;
        if (_wallLibraryStrip.Visibility != Visibility.Visible || type is null) return;
        var signature = $"{_sessionGeneration}:{_libraryCountRevision}:{type}:{LumenText.Get("All")}:"
            + string.Join('|', libraries.Select(library => library.Id + ":" + library.Title));
        if (_wallLibrarySignature != signature)
        {
            var focused = _wallLibraryOptions.FirstOrDefault(option => option.Item.FocusState != FocusState.Unfocused);
            var focusState = focused?.Item.FocusState ?? FocusState.Unfocused;
            _wallLibrarySignature = signature;
            _updatingWallLibraries = true;
            try
            {
                _wallLibrarySelector.Items.Clear();
                _wallLibraryOptions.Clear();
                AddWallLibraryOption(type, null, LumenText.Get("All"));
                foreach (var library in libraries) AddWallLibraryOption(type, library.Id, library.Title);
            }
            finally { _updatingWallLibraries = false; }
            if (focused is not null)
            {
                var replacement = _wallLibraryOptions.FirstOrDefault(option => option.LibraryId == focused.LibraryId)
                    ?? _wallLibraryOptions.FirstOrDefault();
                var generation = _sessionGeneration;
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (generation != _sessionGeneration || !_renderEnabled || !_renderLoaded || _renderedPage != "wall"
                        || replacement is null || !replacement.Item.IsLoaded
                        || !_wallLibraryOptions.Contains(replacement) || _wallLibraryStrip.Visibility != Visibility.Visible) return;
                    var current = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
                    if (current is Control { IsLoaded: true } && !IsWallLibraryFocus(current as DependencyObject)) return;
                    replacement.Item.Focus(focusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
                });
            }
        }
        _updatingWallLibraries = true;
        try
        {
            var selectedId = ViewModel.SelectedLibraryId;
            _wallLibrarySelector.SelectedItem = _wallLibraryOptions.FirstOrDefault(option => option.LibraryId == selectedId)?.Item
                ?? _wallLibraryOptions[0].Item;
            foreach (var option in _wallLibraryOptions)
            {
                var selected = ReferenceEquals(option.Item, _wallLibrarySelector.SelectedItem);
                option.Title.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                option.Title.Foreground = LumenTheme.Brush(selected ? "Ink" : "Sub");
                option.Count.Foreground = LumenTheme.Brush("Muted");
                option.Dot.Background = LumenTheme.Brush("Accent");
                option.Dot.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                option.Hover.Color = LumenTheme.Color("Hover");
                option.Pressed.Color = LumenTheme.Color("ControlStrong");
            }
        }
        finally { _updatingWallLibraries = false; }
    }

    private void AddWallLibraryOption(string type, string? libraryId, string label)
    {
        var title = LumenUi.Text(label, 15);
        title.TextWrapping = TextWrapping.NoWrap;
        var count = MediaLibraryCountLabel(type, libraryId);
        var text = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(title);
        text.Children.Add(count);
        var content = new Grid { Height = 40 };
        content.Children.Add(text);
        var dot = new Border
        {
            Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false
        };
        content.Children.Add(dot);
        Grid.SetColumnSpan(content, 2);
        Grid.SetRowSpan(content, 2);
        var item = new SelectorBarItem
        {
            Child = content, Height = 40, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseSystemFocusVisuals = true
        };
        var hover = new SolidColorBrush(LumenTheme.Color("Hover"));
        var pressed = new SolidColorBrush(LumenTheme.Color("ControlStrong"));
        var transparent = new SolidColorBrush(Colors.Transparent);
        item.Resources["SelectorBarItemBackgroundPointerOver"] = hover;
        item.Resources["SelectorBarItemBackgroundPressed"] = pressed;
        item.Resources["SelectorBarItemBackgroundSelected"] = transparent;
        item.Resources["SelectorBarItemBorderBrush"] = transparent;
        item.Resources["SelectorBarItemBorderBrushSelected"] = transparent;
        AutomationProperties.SetName(item, count.Text.Length > 0
            ? LumenText.Get("{0}, {1} titles", label, count.Text) : label);
        AutomationProperties.SetAutomationId(item, "LumenMediaLibrary_" + type + "_" + (libraryId ?? "All"));
        AutomationProperties.SetAccessibilityView(content, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(title, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(count, AccessibilityView.Raw);
        ToolTipService.SetToolTip(item, label);
        item.Loaded += (_, _) =>
        {
            item.ApplyTemplate();
            HideDefaultWallLibraryIndicator(item);
        };
        count.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            AutomationProperties.SetName(item, count.Text.Length > 0
                ? LumenText.Get("{0}, {1} titles", label, count.Text) : label));
        _wallLibrarySelector.Items.Add(item);
        _wallLibraryOptions.Add(new(item, libraryId, title, count, dot, hover, pressed));
    }

    private static void HideDefaultWallLibraryIndicator(DependencyObject element)
    {
        if (element is FrameworkElement { Name: "PART_SelectionVisual" } indicator)
        {
            // Keep native selection and keyboard behavior while the content supplies the V2 dot.
            indicator.Height = 0;
            indicator.Visibility = Visibility.Collapsed;
            return;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            HideDefaultWallLibraryIndicator(VisualTreeHelper.GetChild(element, index));
    }

    private bool IsWallLibraryFocus(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _wallLibrarySelector)) return true;
        return false;
    }

    private async Task SwitchWallLibraryAsync(string type, string? libraryId)
    {
        var generation = _sessionGeneration;
        var version = ++_wallLibraryInputVersion;
        CloseBrowseFlyouts();
        _autoPageBlocked = false;
        _scroll.ChangeView(null, 0, null, false);
        var navigation = ViewModel.SelectMediaLibraryAsync(type, libraryId);
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
        await navigation;
        if (generation != _sessionGeneration || version != _wallLibraryInputVersion) return;
        QueueRender();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
