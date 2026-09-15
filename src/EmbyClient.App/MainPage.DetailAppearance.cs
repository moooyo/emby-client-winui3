using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;

namespace EmbyClient.App;

public sealed partial class MainPage
{
    private const int DetailBackdropRequestWidth = 1600;
    private const int DetailBackdropRequestHeight = 1000;
    private readonly UISettings _detailDisplaySettings = new();
    private readonly AccessibilitySettings _detailAccessibilitySettings = new();
    private DetailAppearanceResources? _detailAppearanceResources;
    private Grid? _detailBackdropLayer;
    private Image? _detailBackdropImage;
    private FrameworkElement? _detailAppearanceRoot;
    private MediaCardViewModel? _detailAppearanceItem;
    private ConnectedSession? _detailAppearanceSession;
    private CancellationTokenSource? _detailBackdropRequest;
    private DetailBackdropKey? _detailBackdropKey;
    private Storyboard? _detailBackdropArrival;
    private long _detailSessionGeneration;
    private long _detailLibraryVisibilityToken;
    private long _detailPlayerVisibilityToken;
    private long _detailConnectedVisibilityToken;
    private bool _detailAppearanceInitialized;
    private bool _detailAppearanceSuspended;
    private bool _detailHighContrastSubscribed;
    private bool _detailEffectsSubscribed;
    private bool _detailAnimationsSubscribed;

    internal void InitializeDetailAppearance(Grid backdropLayer, Image backdropImage, FrameworkElement windowRoot, TitleBar titleBar)
    {
        if (_detailAppearanceInitialized) return;
        _detailAppearanceInitialized = true;
        _detailBackdropLayer = backdropLayer;
        _detailBackdropImage = backdropImage;
        _detailAppearanceRoot = windowRoot;
        _detailAppearanceResources = new();
        Library.ViewModel.PropertyChanged += DetailViewModelPropertyChanged;
        ChromeStateChanged += DetailChromeStateChanged;
        windowRoot.ActualThemeChanged += DetailAppearanceThemeChanged;
        Loaded += DetailAppearanceLoaded;
        Unloaded += DetailAppearanceUnloaded;
        _detailLibraryVisibilityToken = Library.RegisterPropertyChangedCallback(VisibilityProperty, DetailVisibilityChanged);
        _detailPlayerVisibilityToken = Player.RegisterPropertyChangedCallback(VisibilityProperty, DetailVisibilityChanged);
        _detailConnectedVisibilityToken = ConnectedPane.RegisterPropertyChangedCallback(VisibilityProperty, DetailVisibilityChanged);
        SubscribeDetailSystemPreferences();
        RefreshDetailAppearance();
    }

    internal void DisposeDetailAppearance()
    {
        if (!_detailAppearanceInitialized) return;
        _detailAppearanceInitialized = false;
        CancelDetailBackdrop();
        UnsubscribeDetailSystemPreferences();
        Library.ViewModel.PropertyChanged -= DetailViewModelPropertyChanged;
        ChromeStateChanged -= DetailChromeStateChanged;
        if (_detailAppearanceRoot is not null) _detailAppearanceRoot.ActualThemeChanged -= DetailAppearanceThemeChanged;
        Loaded -= DetailAppearanceLoaded;
        Unloaded -= DetailAppearanceUnloaded;
        Library.UnregisterPropertyChangedCallback(VisibilityProperty, _detailLibraryVisibilityToken);
        Player.UnregisterPropertyChangedCallback(VisibilityProperty, _detailPlayerVisibilityToken);
        ConnectedPane.UnregisterPropertyChangedCallback(VisibilityProperty, _detailConnectedVisibilityToken);
        if (_detailAppearanceItem is not null) _detailAppearanceItem.PropertyChanged -= DetailItemPropertyChanged;
        _detailAppearanceItem = null;
        _detailAppearanceSession = null;
        ClearDetailBackdrop();
    }

    public bool TryToggleFullscreen()
    {
        if (!IsPlayerVisible || _shuttingDown || _sessionTransition || _returningToLibrary || _accountActionPending
            || Player.IsModalOpen || Library.IsPersonDialogOpen) return false;
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void DetailAppearanceLoaded(object sender, RoutedEventArgs args)
    {
        _detailAppearanceSuspended = false;
        SubscribeDetailSystemPreferences();
        RefreshDetailAppearance();
    }

    private void DetailAppearanceUnloaded(object sender, RoutedEventArgs args)
    {
        _detailAppearanceSuspended = true;
        UnsubscribeDetailSystemPreferences();
        CancelDetailBackdrop();
        ClearDetailBackdrop();
    }

    private void DetailViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LibraryViewModel.HasDetails) or nameof(LibraryViewModel.Detail)
            or nameof(LibraryViewModel.IsPerson) or nameof(LibraryViewModel.IsHome) or nameof(LibraryViewModel.IsSearch)
            or nameof(LibraryViewModel.CanGoBack)) QueueDetailAppearanceRefresh();
    }

    private void DetailItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaCardViewModel.Item) or null or "") QueueDetailAppearanceRefresh();
    }

    private void DetailChromeStateChanged(object? sender, EventArgs args) => QueueDetailAppearanceRefresh();
    private void DetailAppearanceThemeChanged(FrameworkElement sender, object args) => QueueDetailAppearanceRefresh();
    private void DetailVisibilityChanged(DependencyObject sender, DependencyProperty property) => QueueDetailAppearanceRefresh();
    private void DetailHighContrastChanged(AccessibilitySettings sender, object args) => QueueDetailAppearanceRefresh();
    private void DetailAdvancedEffectsChanged(UISettings sender, object args) => QueueDetailAppearanceRefresh();
    private void DetailAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => QueueDetailAppearanceRefresh();

    private void QueueDetailAppearanceRefresh()
    {
        if (!_detailAppearanceInitialized) return;
        if (DispatcherQueue.HasThreadAccess) RefreshDetailAppearance();
        else DispatcherQueue.TryEnqueue(RefreshDetailAppearance);
    }

    private void RefreshDetailAppearance()
    {
        if (!_detailAppearanceInitialized || _detailAppearanceSuspended || _detailBackdropImage is null) return;
        if (!ReferenceEquals(_detailAppearanceSession, _session))
        {
            _detailAppearanceSession = _session;
            _detailSessionGeneration++;
            CancelDetailBackdrop();
            ClearDetailBackdrop();
        }
        var item = Library.ViewModel.Detail;
        if (!ReferenceEquals(_detailAppearanceItem, item))
        {
            if (_detailAppearanceItem is not null) _detailAppearanceItem.PropertyChanged -= DetailItemPropertyChanged;
            _detailAppearanceItem = item;
            item.PropertyChanged += DetailItemPropertyChanged;
        }

        var highContrast = ReadDetailHighContrast();
        var reference = CanShowDetailBackdrop(highContrast) ? ImageCache.SelectImage(item.Item, ArtworkKind.Backdrop) : null;
        DetailBackdropKey? key = reference is { } artwork ? new(_detailSessionGeneration, item.Id, artwork) : null;
        if (_detailBackdropKey != key)
        {
            CancelDetailBackdrop();
            ClearDetailBackdrop();
            _detailBackdropKey = key;
            if (key is { } requestedKey && _session is { } session)
            {
                var request = new CancellationTokenSource();
                _detailBackdropRequest = request;
                _ = LoadDetailBackdropAsync(item, session, requestedKey, request);
            }
        }
        if (!ReadDetailAnimationsEnabled()) StopDetailBackdropArrival();
        ApplyDetailAppearanceResources(highContrast);
    }

    private bool CanShowDetailBackdrop(bool highContrast) => !highContrast && !_shuttingDown && !_sessionTransition
        && !_detailAppearanceSuspended && IsLibraryVisible && !IsPlayerVisible && Library.ViewModel.HasDetails
        && !Library.ViewModel.IsPerson && !Library.ViewModel.IsHome && !Library.ViewModel.IsSearch
        && Library.ViewModel.Detail.Item.Type is "Movie" or "Episode" or "Series" or "Season";

    private async Task LoadDetailBackdropAsync(MediaCardViewModel item, ConnectedSession session,
        DetailBackdropKey key, CancellationTokenSource request)
    {
        var token = request.Token;
        try
        {
            // The shared cache owns authenticated bytes; no credential-bearing URI reaches XAML.
            var bytes = await Library.ViewModel.LoadPosterAsync(item, DetailBackdropRequestWidth, DetailBackdropRequestHeight,
                ArtworkKind.Backdrop, token);
            if (bytes is not { Length: > 0 } || !IsCurrentDetailBackdrop(session, key, request)) return;

            // This method remains on the XAML context. A single decode dimension preserves the source aspect ratio.
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, DetailBackdropRequestWidth, 0, token, bitmap =>
            {
                if (!IsCurrentDetailBackdrop(session, key, request) || _detailBackdropImage is null || _detailBackdropLayer is null) return;
                _detailBackdropImage.Source = bitmap;
                _detailBackdropLayer.Visibility = Visibility.Visible;
                ApplyDetailAppearanceResources(highContrast: false);
                StartDetailBackdropArrival();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Missing, unavailable, and malformed artwork all leave the ordinary window surface intact.
            if (IsCurrentDetailBackdrop(session, key, request)) ClearDetailBackdrop();
        }
        finally
        {
            if (ReferenceEquals(_detailBackdropRequest, request)) _detailBackdropRequest = null;
            request.Dispose();
        }
    }

    private bool IsCurrentDetailBackdrop(ConnectedSession session, DetailBackdropKey key, CancellationTokenSource request) =>
        _detailAppearanceInitialized && !request.IsCancellationRequested && ReferenceEquals(_detailBackdropRequest, request)
        && ReferenceEquals(session, _session) && _detailBackdropKey == key && CanShowDetailBackdrop(ReadDetailHighContrast())
        && Library.ViewModel.Detail.Id == key.MediaId
        && ImageCache.SelectImage(Library.ViewModel.Detail.Item, ArtworkKind.Backdrop) == key.Artwork;

    private void CancelDetailBackdrop()
    {
        var request = _detailBackdropRequest;
        _detailBackdropRequest = null;
        _detailBackdropKey = null;
        request?.Cancel();
    }

    private void ClearDetailBackdrop()
    {
        StopDetailBackdropArrival();
        if (_detailBackdropLayer is not null) _detailBackdropLayer.Visibility = Visibility.Collapsed;
        if (_detailBackdropImage is not null) _detailBackdropImage.Source = null;
        ApplyDetailAppearanceResources(ReadDetailHighContrast());
    }

    private void ApplyDetailAppearanceResources(bool highContrast)
    {
        if (_detailAppearanceRoot is null) return;
        _detailAppearanceResources?.Apply(_detailAppearanceRoot.ActualTheme,
            _detailBackdropImage?.Source is not null && !highContrast, ReadDetailAdvancedEffects(), highContrast, _detailDisplaySettings);
    }

    private void StartDetailBackdropArrival()
    {
        StopDetailBackdropArrival();
        if (_detailBackdropLayer is null || !ReadDetailAnimationsEnabled()) return;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, _detailBackdropLayer);
        Storyboard.SetTargetProperty(animation, "Opacity");
        _detailBackdropArrival = new Storyboard();
        _detailBackdropArrival.Children.Add(animation);
        _detailBackdropArrival.Begin();
    }

    private void StopDetailBackdropArrival()
    {
        _detailBackdropArrival?.Stop();
        _detailBackdropArrival = null;
        if (_detailBackdropLayer is not null) _detailBackdropLayer.Opacity = 1;
    }

    private void SubscribeDetailSystemPreferences()
    {
        if (!_detailHighContrastSubscribed)
        {
            try { _detailAccessibilitySettings.HighContrastChanged += DetailHighContrastChanged; _detailHighContrastSubscribed = true; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (!_detailEffectsSubscribed)
        {
            try { _detailDisplaySettings.AdvancedEffectsEnabledChanged += DetailAdvancedEffectsChanged; _detailEffectsSubscribed = true; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (!_detailAnimationsSubscribed)
        {
            try { _detailDisplaySettings.AnimationsEnabledChanged += DetailAnimationsChanged; _detailAnimationsSubscribed = true; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
    }

    private void UnsubscribeDetailSystemPreferences()
    {
        if (_detailHighContrastSubscribed)
        {
            try { _detailAccessibilitySettings.HighContrastChanged -= DetailHighContrastChanged; _detailHighContrastSubscribed = false; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (_detailEffectsSubscribed)
        {
            try { _detailDisplaySettings.AdvancedEffectsEnabledChanged -= DetailAdvancedEffectsChanged; _detailEffectsSubscribed = false; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (_detailAnimationsSubscribed)
        {
            try { _detailDisplaySettings.AnimationsEnabledChanged -= DetailAnimationsChanged; _detailAnimationsSubscribed = false; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
    }

    private bool ReadDetailHighContrast()
    {
        try { return _detailAccessibilitySettings.HighContrast; }
        catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { return false; }
    }

    private bool ReadDetailAdvancedEffects()
    {
        try { return _detailDisplaySettings.AdvancedEffectsEnabled; }
        catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { return false; }
    }

    private bool ReadDetailAnimationsEnabled()
    {
        try { return _detailDisplaySettings.AnimationsEnabled; }
        catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { return false; }
    }

    private static bool IsOptionalPreferenceUnavailable(Exception exception) => exception is COMException or InvalidOperationException;
    private readonly record struct DetailBackdropKey(long SessionGeneration, string MediaId, ImageCache.ImageReference Artwork);
}
