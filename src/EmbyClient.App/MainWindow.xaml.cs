using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.App.Views.Lumen;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;
using Color = Windows.UI.Color;

namespace EmbyClient.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly LumenShellPage _page;
    private readonly DispatcherQueue _chromeDispatcher;
    private readonly TaskbarPlaybackService? _taskbarPlayback;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly Dictionary<NonClientRegionKind, RectInt32[]> _regionRects = [];
    private readonly object _captionInputGate = new();
    private bool _pendingCaptionInput;
    private bool _captionInputQueued;
    private bool _chromeUpdateQueued;
    private bool _regionUpdateQueued;
    private bool _updatingDragRegions;
    private bool _dragRegionsRequested;
    private bool? _forwardedCaptionActive;
    private bool _pointerInCaption;
    private bool _captionPointerActive;
    private bool _closing;
    private bool _allowClose;
    private bool _compactOverlay;
    private bool _windowActive;
    private bool _contentFocusQueued;

    public MainWindow()
    {
        InitializeComponent();
        _chromeDispatcher = DispatcherQueue;
        WindowRoot.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;
        Width = 1440;
        Height = 900;
        MinWidth = 640;
        MinHeight = 600;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _page = new LumenShellPage();
        RootFrame.Content = _page;
        Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (_windowActive) QueueContentKeyboardFocus();
            else _page.ResetGlobalShortcutPressState();
        };
        WindowRoot.Loaded += (_, _) => QueueContentKeyboardFocus();
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientPointerSource.PointerEntered += CaptionPointerMoved;
        _nonClientPointerSource.PointerMoved += CaptionPointerMoved;
        _nonClientPointerSource.PointerExited += CaptionPointerExited;
        _page.FullscreenRequested += (_, _) =>
        {
            var fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            SetPresenter(fullscreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
        };
        _page.ExitFullscreenRequested += (_, _) =>
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped) SetPresenter(AppWindowPresenterKind.Default);
        };
        _page.CompactOverlayRequested += (_, _) =>
        {
            SetPresenter(_compactOverlay ? AppWindowPresenterKind.Default : AppWindowPresenterKind.CompactOverlay);
        };
        _page.ChromeChanged += (_, _) => QueueChromeUpdate();
        WindowRoot.SizeChanged += (_, _) => QueueDragRegions();
        WindowRoot.LayoutUpdated += WindowRootLayoutUpdated;
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange) UpdatePresentation();
            else if (args.DidSizeChange) QueueChromeUpdate();
            if (args.DidPositionChange || args.DidSizeChange) QueueContentRefreshRateUpdate();
        };
        try
        {
            // Install the message listener before Activate creates this window's taskbar button.
            _taskbarPlayback = new TaskbarPlaybackService(this);
            _taskbarPlayback.CommandRequested += TaskbarCommandRequested;
            _page.TaskbarStateChanged += TaskbarStateChanged;
            _taskbarPlayback.Update(_page.TaskbarState);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Taskbar initialization failed (0x{0:X8}).", exception.HResult);
        }
        InitializeWindowsActivation();
        InitializeContentRefreshRate();
        PersistenceId = "MainWindow";
        AppWindow.Closing += OnClosing;
        Closed += MainWindowClosed;
        UpdatePresentation();
    }

    private void QueueContentKeyboardFocus()
    {
        if (_closing || !_windowActive || _contentFocusQueued) return;
        _contentFocusQueued = true;
        if (_chromeDispatcher.TryEnqueue(() =>
        {
            try
            {
                if (_closing || !_windowActive) return;
                var rootWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var nativeFocus = GetFocus();
                var foregroundRoot = rootWindow != 0 && GetForegroundWindow() == rootWindow;
                var nativeRoot = rootWindow != 0 && nativeFocus == rootWindow;
                // Island focus can remain reported while activation leaves native focus on the host.
                var reassertNativeFocus = foregroundRoot && nativeRoot;
                if (_acceptanceWindowInputTraceEnabled)
                {
                    var nativeChild = rootWindow != 0 && nativeFocus != 0 && IsChild(rootWindow, nativeFocus);
                    AcceptanceDiagnostics.Trace($"window-content-focus foregroundRoot={foregroundRoot} nativeRoot={nativeRoot} nativeChild={nativeChild} reassert={reassertNativeFocus}");
                }
                _page.EnsureContentKeyboardFocus(reassertNativeFocus);
            }
            finally
            {
                // Keep focus messages raised by this attempt coalesced until the handoff returns.
                _contentFocusQueued = false;
            }
        })) return;
        _contentFocusQueued = false;
    }

    private void UpdateChrome()
    {
        if (_closing) return;
        WindowRoot.RequestedTheme = _page.PreferredTheme;
        var player = _page.IsPlayerVisible;
        var windowed = AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped;
        var visible = windowed && (!player || _page.ArePlayerControlsVisible || _captionPointerActive);
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = visible ? TitleBarHeightOption.Tall : TitleBarHeightOption.Collapsed;
        var ink = _page.IsImagePage ? Colors.White : LumenTheme.Color("Ink");
        var hoverAlpha = player ? 26 : _page.IsImagePage ? 31 : LumenTheme.IsDark ? 20 : 13;
        var hoverValue = _page.IsImagePage || LumenTheme.IsDark ? (byte)255 : (byte)0;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ink;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(153, ink.R, ink.G, ink.B);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb((byte)hoverAlpha, hoverValue, hoverValue, hoverValue);
        titleBar.ButtonHoverForegroundColor = ink;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb((byte)(hoverAlpha + 15), hoverValue, hoverValue, hoverValue);
        titleBar.ButtonPressedForegroundColor = ink;
        Title = _page.WindowTitle;
        if (!windowed)
        {
            if (!_compactOverlay) _pointerInCaption = false;
        }
        UpdateCaptionActivity();
        QueueDragRegions();
    }

    private void QueueChromeUpdate()
    {
        if (_closing || _chromeUpdateQueued) return;
        _chromeUpdateQueued = true;
        if (_chromeDispatcher.TryEnqueue(() =>
        {
            _chromeUpdateQueued = false;
            if (_closing) return;
            try { UpdateChrome(); }
            catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "MainWindow.ChromeUpdate"); }
        })) return;
        _chromeUpdateQueued = false;
        if (!_closing) AcceptanceDiagnostics.Record(new InvalidOperationException("The chrome dispatcher rejected an update."), "MainWindow.ChromeDispatch");
    }

    private void CaptionPointerMoved(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
        => CaptureCaptionInput(args, exiting: false);

    private void CaptionPointerExited(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
        => CaptureCaptionInput(args, exiting: true);

    private void CaptureCaptionInput(NonClientPointerEventArgs args, bool exiting)
    {
        if (_closing) return;
        try
        {
            // Read the event before returning to native input. Never retain WinRT event arguments or mutate XAML here.
            var kind = args.RegionKind;
            var inRegion = !exiting && args.IsPointInRegion;
            var inCaption = inRegion && (kind == NonClientRegionKind.Caption || IsCaptionButton(kind));
            lock (_captionInputGate)
            {
                _pendingCaptionInput = inCaption;
                if (_captionInputQueued) return;
                _captionInputQueued = true;
            }
            if (_chromeDispatcher.TryEnqueue(ApplyCaptionInput)) return;
            lock (_captionInputGate) _captionInputQueued = false;
            if (!_closing) AcceptanceDiagnostics.Record(new InvalidOperationException("The caption dispatcher rejected an update."), "MainWindow.CaptionDispatch");
        }
        catch (Exception exception)
        {
            lock (_captionInputGate) _captionInputQueued = false;
            AcceptanceDiagnostics.Record(exception, "MainWindow.CaptionInputCapture");
        }
    }

    private void ApplyCaptionInput()
    {
        bool inCaption;
        lock (_captionInputGate)
        {
            inCaption = _pendingCaptionInput;
            _captionInputQueued = false;
        }
        if (_closing) return;
        try
        {
            _pointerInCaption = inCaption;
            UpdateCaptionActivity();
        }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "MainWindow.CaptionInputApply"); }
    }

    private static bool IsCaptionButton(NonClientRegionKind kind) =>
        kind is NonClientRegionKind.Minimize or NonClientRegionKind.Maximize or NonClientRegionKind.Close;

    private void UpdateCaptionActivity()
    {
        var active = _pointerInCaption;
        _captionPointerActive = active;
        if (!_page.IsPlayerVisible) { _forwardedCaptionActive = null; return; }
        if (_forwardedCaptionActive == active) return;
        _page.SetCaptionPointerActive(active);
        _forwardedCaptionActive = active;
    }

    private void WindowRootLayoutUpdated(object? sender, object args) => QueueDragRegions();

    private void QueueDragRegions()
    {
        if (_closing) return;
        _dragRegionsRequested = true;
        if (_regionUpdateQueued || _updatingDragRegions) return;
        _regionUpdateQueued = true;
        if (_chromeDispatcher.TryEnqueue(DispatcherQueuePriority.Low, ApplyDragRegions)) return;
        _regionUpdateQueued = false;
        if (!_closing) AcceptanceDiagnostics.Record(new InvalidOperationException("The region dispatcher rejected an update."), "MainWindow.RegionDispatch");
    }

    private void ApplyDragRegions()
    {
        _regionUpdateQueued = false;
        if (_closing || _updatingDragRegions) return;
        _dragRegionsRequested = false;
        _updatingDragRegions = true;
        try { UpdateDragRegions(); }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "MainWindow.RegionUpdate"); }
        finally
        {
            _updatingDragRegions = false;
            if (_dragRegionsRequested && !_closing) QueueDragRegions();
        }
    }

    private void UpdateDragRegions()
    {
        if (_closing || _page is null) return;
        var scale = _page.XamlRoot?.RasterizationScale ?? 1;
        var width = WindowRoot.ActualWidth;
        if (width <= 0) return;
        var fullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        var headerHeight = _page.IsPlayerVisible ? 48 : 84;
        var caption = new List<Rect>();
        var passthrough = new List<Rect>();
        if (!fullscreen && (!_page.IsPlayerVisible || _page.ArePlayerControlsVisible || _compactOverlay))
            caption.Add(new Rect(0, 0, width, headerHeight));

        foreach (var element in _page.CaptionInteractiveElements)
        {
            if (!TryGetElementBounds(element, out var bounds)) continue;
            passthrough.Add(bounds);
            ExcludeRegion(caption, bounds);
        }
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped
            && AppWindow.TitleBar.PreferredHeightOption != TitleBarHeightOption.Collapsed)
        {
            // Insets are physical pixels. Native caption buttons retain their own input regions.
            var reservedWidth = AppWindow.TitleBar.RightInset / scale;
            ExcludeRegion(caption, new Rect(Math.Max(0, width - reservedWidth), 0, reservedWidth, 48));
            var reservedLeft = AppWindow.TitleBar.LeftInset / scale;
            if (reservedLeft > 0) ExcludeRegion(caption, new Rect(0, 0, reservedLeft, 48));
        }

        SetRegions(NonClientRegionKind.Caption, caption.Select(rect => ToPixels(rect, scale)).ToArray());
        SetRegions(NonClientRegionKind.Passthrough, fullscreen ? [] : passthrough.Select(rect => ToPixels(rect, scale)).ToArray());
    }

    private bool TryGetElementBounds(FrameworkElement element, out Rect bounds)
    {
        bounds = default;
        if (!element.IsLoaded || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        for (DependencyObject? ancestor = element; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(ancestor, WindowRoot))
            {
                bounds = element.TransformToVisual(WindowRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return true;
            }
        }
        return false;
    }

    private static RectInt32 ToPixels(Rect bounds, double scale)
    {
        var left = (int)Math.Floor(bounds.Left * scale);
        var top = (int)Math.Floor(bounds.Top * scale);
        var right = (int)Math.Ceiling(bounds.Right * scale);
        var bottom = (int)Math.Ceiling(bounds.Bottom * scale);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    private void SetRegions(NonClientRegionKind kind, RectInt32[] rectangles)
    {
        if (_regionRects.TryGetValue(kind, out var previous) && previous.SequenceEqual(rectangles)) return;
        if (rectangles.Length == 0) _nonClientPointerSource.ClearRegionRects(kind);
        else _nonClientPointerSource.SetRegionRects(kind, rectangles);
        _regionRects[kind] = rectangles;
    }

    private static void ExcludeRegion(List<Rect> regions, Rect exclusion)
    {
        for (var index = regions.Count - 1; index >= 0; index--)
        {
            var region = regions[index];
            var left = Math.Max(region.Left, exclusion.Left);
            var top = Math.Max(region.Top, exclusion.Top);
            var right = Math.Min(region.Right, exclusion.Right);
            var bottom = Math.Min(region.Bottom, exclusion.Bottom);
            if (left >= right || top >= bottom) continue;
            regions.RemoveAt(index);
            if (top > region.Top) regions.Add(new Rect(region.Left, region.Top, region.Width, top - region.Top));
            if (bottom < region.Bottom) regions.Add(new Rect(region.Left, bottom, region.Width, region.Bottom - bottom));
            if (left > region.Left) regions.Add(new Rect(region.Left, top, left - region.Left, bottom - top));
            if (right < region.Right) regions.Add(new Rect(right, top, region.Right - right, bottom - top));
        }
    }

    private void UpdatePresentation()
    {
        if (_closing) return;
        _compactOverlay = AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
        _page.SetCompactOverlayState(_compactOverlay);
        _page.SetFullscreenState(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        _page.SetWindowTitleBarIntegrated(AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped);
        UpdateChrome();
        QueueContentRefreshRateUpdate();
    }

    private void SetPresenter(AppWindowPresenterKind kind)
    {
        // Apply limits before the native presenter chooses its size. Normal-page limits distort a video overlay.
        var compact = kind == AppWindowPresenterKind.CompactOverlay;
        MinWidth = compact ? 320 : 640;
        MinHeight = compact ? 180 : 600;
        AppWindow.SetPresenter(kind);
        UpdatePresentation();
    }

    private void TaskbarStateChanged(object? sender, EventArgs args)
    {
        if (!_closing) _taskbarPlayback?.Update(_page.TaskbarState);
    }

    private void TaskbarCommandRequested(object? sender, NativeMediaCommandEventArgs args)
    {
        if (!_closing) _page.RequestTaskbarCommand(args);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        _taskbarPlayback?.Update(new TaskbarPlaybackState(null, false, false, false, false, false));
        try { await _page.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* Closing remains bounded when the server is unavailable. */ }
        try { await RetireContentRefreshRateAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "MainWindow.DisplayRetirement"); }
        _allowClose = true;
        Close();
    }

    private void MainWindowClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _ = RetireContentRefreshRateAsync();
        DisposeWindowsActivation();
        _page.TaskbarStateChanged -= TaskbarStateChanged;
        if (_taskbarPlayback is not null)
        {
            _taskbarPlayback.CommandRequested -= TaskbarCommandRequested;
            _taskbarPlayback.Dispose();
        }
        WindowRoot.LayoutUpdated -= WindowRootLayoutUpdated;
        _nonClientPointerSource.PointerEntered -= CaptionPointerMoved;
        _nonClientPointerSource.PointerMoved -= CaptionPointerMoved;
        _nonClientPointerSource.PointerExited -= CaptionPointerExited;
    }
}
