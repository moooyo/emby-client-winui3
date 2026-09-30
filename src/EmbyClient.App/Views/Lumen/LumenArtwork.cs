using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Numerics;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenArtwork : UserControl
{
    private readonly Border _frame = new();
    private readonly Grid _body = new();
    private readonly FrameworkElement _placeholder;
    private MediaCardViewModel? _item;
    private LibraryViewModel? _library;
    private ArtworkKind _kind;
    private int _decodeWidth = 480;
    private CancellationTokenSource? _request;
    private ImageCache.ImageReference? _reference;
    private long _generation;
    private bool _subscribed;

    public Image Image { get; } = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };

    public LumenArtwork()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        CornerRadius = new CornerRadius(12);
        Background = LumenTheme.Brush("Control");
        _placeholder = LumenUi.Icon("film", 32);
        _placeholder.Opacity = .25;
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _body.Children.Add(_placeholder);
        _body.Children.Add(Image);
        _frame.Child = _body;
        Content = _frame;
        void SynchronizeFrame()
        {
            _frame.CornerRadius = CornerRadius;
            _frame.Background = Background;
            _frame.BorderBrush = BorderBrush;
            _frame.BorderThickness = BorderThickness;
            UpdateClip();
        }
        SynchronizeFrame();
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => SynchronizeFrame());
        RegisterPropertyChangedCallback(BackgroundProperty, (_, _) => SynchronizeFrame());
        RegisterPropertyChangedCallback(BorderBrushProperty, (_, _) => SynchronizeFrame());
        RegisterPropertyChangedCallback(BorderThicknessProperty, (_, _) => SynchronizeFrame());
        AutomationProperties.SetAccessibilityView(Image, AccessibilityView.Raw);
        Loaded += ArtworkLoaded;
        Unloaded += ArtworkUnloaded;
        SizeChanged += (_, _) => { UpdateClip(); UpdateCoverTransform(); };
        _body.SizeChanged += (_, _) =>
        {
            if (!_coverFocalPoint.HasValue) return;
            UpdateClip();
            UpdateCoverTransform();
        };
        Image.ImageOpened += (_, _) => UpdateCoverTransform();
    }

    public void Set(MediaCardViewModel item, LibraryViewModel library, ArtworkKind kind = ArtworkKind.Poster, int decodeWidth = 480)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(library);
        Unsubscribe();
        CancelRequest(true);
        _item = item;
        _library = library;
        _kind = kind;
        _decodeWidth = Math.Clamp(decodeWidth, 64, 1920);
        _reference = ImageCache.SelectImage(item.Item, kind);
        AutomationProperties.SetName(this, item.SceneIdentity);
        if (IsLoaded)
        {
            Subscribe();
            _ = LoadAsync();
        }
    }

    private void ArtworkLoaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null) _reference = ImageCache.SelectImage(_item.Item, _kind);
        Subscribe();
        UpdateClip();
        UpdateCoverTransform();
        if (Image.Source is null) _ = LoadAsync();
    }

    private void ArtworkUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        CancelRequest(true);
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

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaCardViewModel.Item) || _item is null) return;
        var reference = ImageCache.SelectImage(_item.Item, _kind);
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
        var source = new CancellationTokenSource();
        var token = source.Token;
        _request = source;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = (int)Math.Clamp(_decodeWidth * scale, 64, 1920);
        var height = _kind == ArtworkKind.Poster ? width * 3 / 2 : width * 9 / 16;
        var decodeHeight = _coverFocalPoint.HasValue ? 0 : height;
        try
        {
            var bytes = await library.LoadPosterAsync(item, width, height, _kind, token);
            if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, decodeHeight, token, bitmap =>
            {
                if (generation != _generation || token.IsCancellationRequested || !IsLoaded
                    || !ReferenceEquals(_item, item) || !ReferenceEquals(_library, library)
                    || !ReferenceEquals(_request, source)) return;
                Image.Source = bitmap;
                UpdateCoverTransform();
                _placeholder.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException
            or TimeoutException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Missing or undecodable artwork keeps the native placeholder.
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
        if (!clear) return;
        Image.Source = null;
        UpdateCoverTransform();
        _placeholder.Visibility = Visibility.Visible;
    }

    private void UpdateClip()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(_body);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)_body.ActualWidth, (float)_body.ActualHeight);
        geometry.CornerRadius = new Vector2((float)CornerRadius.TopLeft);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }
}
