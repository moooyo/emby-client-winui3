using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenArtwork
{
    private Point? _coverFocalPoint;
    private CompositeTransform? _coverTransform;

    public void SetCoverFocalPoint(double x, double y)
    {
        if (!double.IsFinite(x) || x is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y) || y is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(y));
        var decodeModeChanged = !_coverFocalPoint.HasValue;
        _coverFocalPoint = new Point(x, y);
        _coverTransform ??= new CompositeTransform();
        Image.Stretch = Stretch.Fill;
        Image.RenderTransformOrigin = new Point(0, 0);
        Image.RenderTransform = _coverTransform;
        if (decodeModeChanged && (_request is not null || Image.Source is not null))
        {
            CancelRequest(true);
            if (IsLoaded) _ = LoadAsync();
        }
        UpdateCoverTransform();
    }

    private void UpdateCoverTransform()
    {
        if (_coverFocalPoint is not { } focal || _coverTransform is null) return;
        var viewWidth = _body.ActualWidth;
        var viewHeight = _body.ActualHeight;
        if (!IsLoaded || Image.Source is not BitmapImage bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0
            || !double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0)
        {
            _coverTransform.ScaleX = _coverTransform.ScaleY = 1;
            _coverTransform.TranslateX = _coverTransform.TranslateY = 0;
            return;
        }
        var scale = Math.Max(viewWidth / bitmap.PixelWidth, viewHeight / bitmap.PixelHeight);
        var coverWidth = bitmap.PixelWidth * scale;
        var coverHeight = bitmap.PixelHeight * scale;
        // Keep layout at the viewport size; only the rendered image is enlarged and cropped.
        _coverTransform.ScaleX = coverWidth / viewWidth;
        _coverTransform.ScaleY = coverHeight / viewHeight;
        _coverTransform.TranslateX = (viewWidth - coverWidth) * focal.X;
        _coverTransform.TranslateY = (viewHeight - coverHeight) * focal.Y;
    }
}
