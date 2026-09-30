using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

/// <summary>Turns artwork into a sparse color field with a native in-app blur.</summary>
public sealed partial class LumenAmbientArtwork : UserControl
{
    private readonly Grid _body = new();
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly CompositeTransform _overscan = new() { ScaleX = 1.2, ScaleY = 1.7 };
    private readonly AcrylicBrush _blur = new()
    {
        TintColor = Microsoft.UI.Colors.Transparent, TintOpacity = 0, TintLuminosityOpacity = 0,
        TintTransitionDuration = TimeSpan.Zero
    };
    private readonly Border _dimming = new() { IsHitTestVisible = false };
    private MediaCardViewModel? _item;
    private LibraryViewModel? _library;
    private ImageCache.ImageReference? _reference;
    private CancellationTokenSource? _request;
    private long _generation;
    private bool _subscribed;

    public LumenAmbientArtwork()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = false;
        _image.RenderTransform = _overscan;
        _image.RenderTransformOrigin = new Point(.5, 0);
        _body.Children.Add(_image);
        _body.Children.Add(new Border { Background = _blur, IsHitTestVisible = false });
        _body.Children.Add(_dimming);
        Content = _body;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        Loaded += (_, _) => { Subscribe(); ApplyTheme(); if (_image.Source is null) _ = LoadAsync(); };
        Unloaded += (_, _) => { Unsubscribe(); CancelRequest(false); };
        SizeChanged += (_, _) =>
        {
            _overscan.TranslateY = -ActualHeight * .4;
            _body.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        };
        ApplyTheme();
    }

    internal void Set(MediaCardViewModel item, LibraryViewModel library)
    {
        Unsubscribe();
        CancelRequest(true);
        _item = item;
        _library = library;
        _reference = ImageCache.SelectImage(item.Item, ArtworkKind.Poster);
        if (!IsLoaded) return;
        Subscribe();
        _ = LoadAsync();
    }

    internal void Clear()
    {
        Unsubscribe();
        CancelRequest(true);
        _item = null;
        _library = null;
        _reference = null;
    }

    internal void ApplyTheme()
    {
        var background = LumenTheme.Color("Background");
        _blur.FallbackColor = background;
        _dimming.Background = LumenTheme.Brush("Background");
        // An opaque blur followed by a scrim preserves the design alpha without exposing sharp source pixels.
        _dimming.Opacity = LumenTheme.IsDark ? .38 : .6;
    }

    private void Subscribe()
    {
        if (_subscribed || _item is null) return;
        _item.PropertyChanged += ItemChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (_subscribed && _item is not null) _item.PropertyChanged -= ItemChanged;
        _subscribed = false;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MediaCardViewModel.Item) || _item is null) return;
        var reference = ImageCache.SelectImage(_item.Item, ArtworkKind.Poster);
        if (reference == _reference) return;
        _reference = reference;
        CancelRequest(true);
        if (IsLoaded) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (!IsLoaded || _item is null || _library is null || _request is not null) return;
        var item = _item;
        var library = _library;
        var generation = _generation;
        var source = _request = new CancellationTokenSource();
        var token = source.Token;
        try
        {
            var bytes = await library.LoadPosterAsync(item, 128, 192, ArtworkKind.Poster, token);
            if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
            // The dedicated decoder is intentionally not routed through LumenArtwork's 64-pixel minimum.
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 8, 12, token, bitmap =>
            {
                if (generation != _generation || token.IsCancellationRequested || !IsLoaded
                    || !ReferenceEquals(item, _item) || !ReferenceEquals(library, _library)
                    || !ReferenceEquals(source, _request)) return;
                _image.Source = bitmap;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException
            or TimeoutException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Missing artwork leaves the native background fallback in place.
        }
        finally
        {
            if (ReferenceEquals(_request, source)) _request = null;
            source.Dispose();
        }
    }

    private void CancelRequest(bool clear)
    {
        _generation++;
        var request = _request;
        _request = null;
        request?.Cancel();
        if (clear) _image.Source = null;
    }
}
