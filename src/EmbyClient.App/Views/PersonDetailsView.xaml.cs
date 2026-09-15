using EmbyClient.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views;

public sealed partial class PersonDetailsView : UserControl
{
    private readonly UISettings _displaySettings = new();
    private CancellationTokenRegistration _sessionRegistration;
    private ScrollViewer? _worksScroller;
    private bool _textScaleSubscribed;
    private bool _detached;
    private bool _restoreFocus;
    private double? _pendingScrollOffset;
    private int _restoreVersion;

    public PersonDetailsView(PersonDetailsViewModel viewModel)
    {
        ViewModel = viewModel;
        _pendingScrollOffset = viewModel.ScrollOffset;
        InitializeComponent();
        AutomationProperties.SetName(this, $"About {ViewModel.Person.Name}");
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        UpdateBiographyExpansion();
    }

    public PersonDetailsViewModel ViewModel { get; }
    public event EventHandler<MediaCardViewModel>? WorkInvoked;

    public double ScrollOffset
    {
        get => _pendingScrollOffset ?? _worksScroller?.VerticalOffset ?? ViewModel.ScrollOffset;
        set
        {
            ViewModel.ScrollOffset = double.IsFinite(value) ? Math.Max(0, value) : 0;
            _pendingScrollOffset = ViewModel.ScrollOffset;
            QueueRestoreState();
        }
    }

    public void CaptureState()
    {
        ViewModel.ScrollOffset = ScrollOffset;
        if (XamlRoot is not null) RememberFocusedWork(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);
    }

    public void FocusDestination()
    {
        _restoreFocus = true;
        QueueRestoreState();
    }

    public void Detach()
    {
        if (_detached) return;
        CaptureState();
        _detached = true;
        _restoreVersion++;
        DetachNotifications();
        Bindings.StopTracking();
        WorksGrid.ItemsSource = null;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_detached) return;
        _sessionRegistration.Dispose();
        _sessionRegistration = ViewModel.LifetimeToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_detached) IsEnabled = false;
        }));
        if (!_textScaleSubscribed)
        {
            try
            {
                _displaySettings.TextScaleFactorChanged += TextScaleFactorChanged;
                _textScaleSubscribed = true;
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        UpdateLayoutMetrics();
        QueueRestoreState();
        await ViewModel.EnsureLoadedAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (!_detached) CaptureState();
        _restoreVersion++;
        DetachNotifications();
    }

    private void DetachNotifications()
    {
        if (_worksScroller is { } scroller) scroller.ViewChanged -= WorksScroller_ViewChanged;
        _worksScroller = null;
        if (_textScaleSubscribed)
        {
            try { _displaySettings.TextScaleFactorChanged -= TextScaleFactorChanged; }
            catch (System.Runtime.InteropServices.COMException) { }
            _textScaleSubscribed = false;
        }
        _sessionRegistration.Dispose();
    }

    private void WorksGrid_Loaded(object sender, RoutedEventArgs args)
    {
        if (_detached) return;
        ConnectScroller();
        UpdateLayoutMetrics();
        QueueRestoreState();
    }

    private void WorksGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutMetrics();

    private void TextScaleFactorChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (_textScaleSubscribed && !_detached) UpdateLayoutMetrics(); });

    private void UpdateLayoutMetrics()
    {
        if (_detached) return;
        var width = Math.Max(0, WorksGrid.ActualWidth - WorksGrid.Padding.Left - WorksGrid.Padding.Right - 20);
        if (width > 0)
        {
            PageHeader.Width = width;
            PageFooter.Width = width;
        }
        var compact = width < 520;
        Grid.SetRow(PersonIdentity, compact ? 1 : 0);
        Grid.SetColumn(PersonIdentity, compact ? 0 : 1);
        Grid.SetColumnSpan(PersonIdentity, compact ? 2 : 1);
        var textScale = 1d;
        try { textScale = Math.Max(1, _displaySettings.TextScaleFactor); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (WorksGrid.ItemsPanelRoot is ItemsWrapGrid panel)
            panel.ItemHeight = 320 + 92 * (textScale - 1);
    }

    private void ConnectScroller()
    {
        var scroller = FindScroller(WorksGrid);
        if (ReferenceEquals(scroller, _worksScroller)) return;
        if (_worksScroller is { } previous) previous.ViewChanged -= WorksScroller_ViewChanged;
        _worksScroller = scroller;
        if (scroller is not null) scroller.ViewChanged += WorksScroller_ViewChanged;
    }

    private void WorksScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!_detached && _pendingScrollOffset is null && sender is ScrollViewer scroller)
            ViewModel.ScrollOffset = scroller.VerticalOffset;
    }

    private void QueueRestoreState()
    {
        if (_detached || !IsLoaded) return;
        var version = ++_restoreVersion;
        var offset = _pendingScrollOffset ?? ViewModel.ScrollOffset;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_detached || !IsLoaded || version != _restoreVersion) return;
            WorksGrid.UpdateLayout();
            ConnectScroller();
            _worksScroller?.ChangeView(null, offset, null, true);
            if (!_restoreFocus)
            {
                _pendingScrollOffset = null;
                return;
            }
            _restoreFocus = false;
            var work = ViewModel.Works.FirstOrDefault(item => item.Id == ViewModel.FocusedWorkId);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_detached || !IsLoaded || version != _restoreVersion) return;
                if (work is not null && WorksGrid.ContainerFromItem(work) is Control container && IsInViewport(container))
                    container.Focus(FocusState.Programmatic);
                else Focus(FocusState.Programmatic);
                _worksScroller?.ChangeView(null, offset, null, true);
                _pendingScrollOffset = null;
            });
        });
    }

    private bool IsInViewport(FrameworkElement element)
    {
        var bounds = element.TransformToVisual(WorksGrid)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < WorksGrid.ActualHeight;
    }

    private void WorksGrid_GotFocus(object sender, RoutedEventArgs args) => RememberFocusedWork(args.OriginalSource as DependencyObject);

    private void RememberFocusedWork(DependencyObject? element)
    {
        MediaCardViewModel? focusedWork = null;
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, WorksGrid))
            {
                if (focusedWork is not null) ViewModel.FocusedWorkId = focusedWork.Id;
                return;
            }
            if (current is GridViewItem { Content: MediaCardViewModel work }) focusedWork = work;
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private async void RetryBiography_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadBiographyAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadMoreAsync();

    private void Work_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_detached || ViewModel.LifetimeToken.IsCancellationRequested || args.ClickedItem is not MediaCardViewModel work) return;
        CaptureState();
        ViewModel.FocusedWorkId = work.Id;
        WorkInvoked?.Invoke(this, work);
    }

    private void BiographyText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateBiographyToggle();

    private void BiographyToggle_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.IsBiographyExpanded = !ViewModel.IsBiographyExpanded;
        UpdateBiographyExpansion();
    }

    private void UpdateBiographyExpansion()
    {
        BiographyText.MaxLines = ViewModel.IsBiographyExpanded ? 0 : 5;
        BiographyToggle.Content = ViewModel.IsBiographyExpanded ? "Show less" : "Read more";
        AutomationProperties.SetName(BiographyToggle, ViewModel.IsBiographyExpanded ? "Show less about this person" : "Read more about this person");
        UpdateBiographyToggle();
    }

    private void UpdateBiographyToggle() => BiographyToggle.Visibility = ViewModel.IsBiographyExpanded || BiographyText.IsTextTrimmed
        ? Visibility.Visible : Visibility.Collapsed;

    private void Artwork_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_detached && sender is PersonArtwork artwork) artwork.SetImageLoader(ViewModel.LoadArtworkAsync);
    }

    private void Work_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var card = args.InRecycleQueue ? null : args.Item as MediaCardViewModel;
        AutomationProperties.SetName(args.ItemContainer, card is null ? string.Empty : $"{card.Title}, {card.CardSubtitle}. Open details.");
        ToolTipService.SetToolTip(args.ItemContainer, card?.Title);
    }
}
