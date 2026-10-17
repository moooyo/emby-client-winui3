using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private NativeImageFrameRequest? _pendingNativeImageFrame;
    private NativePlaybackEngine? _nativeImageOwner;
    private Guid? _nativeImagePlaybackId;
    private long _nativeImagePresentationEpoch;
    private long _nativeImageViewEpoch;
    private ulong _nativeImageTrackId;
    private ulong _nativeImageGeneration;
    private ulong _latestNativeImageSequence;
    private ulong _committedNativeImageSequence;
    private bool _nativeImagePumpRunning;
    private IAsyncAction? _nativeImageUploadAction;
    private SoftwareBitmapSource? _nativeImageSource;
    private AssSubtitleGeometry? _nativeImageGeometry;
    private bool _nativeImageLayoutQueued;
    private bool _nativeImageControlsVisible = true;
    private readonly DispatcherTimer _nativeImageControlFadeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void InitializeNativeSubtitleLayout()
    {
        _nativeImageControlFadeTimer.Tick += (_, _) =>
        {
            _nativeImageControlFadeTimer.Stop();
            _nativeImageControlsVisible = AreControlsVisible;
            QueueNativeSubtitleImageLayout();
        };
        Unloaded += (_, _) =>
        {
            _nativeImageControlFadeTimer.Stop();
            _nativeImageControlsVisible = AreControlsVisible;
        };
    }

    private void NativeSubtitleImageFrameChanged(object? sender, NativeAssSubtitleFrameChangedEventArgs args)
    {
        if (sender is not NativePlaybackEngine engine || !ReferenceEquals(engine, _engine)) return;
        if (!args.HasImage)
        {
            if ((ReferenceEquals(_nativeImageOwner, engine) && _nativeImagePlaybackId == args.PlaybackId
                && args.PresentationEpoch >= _nativeImagePresentationEpoch)
                || engine.IsAssSubtitleFrameCurrent(args.PlaybackId, args.PresentationEpoch))
                ClearNativeSubtitleImage();
            return;
        }
        if (!engine.IsAssSubtitleFrameCurrent(args.PlaybackId, args.PresentationEpoch)) return;
        if (!ReferenceEquals(_nativeImageOwner, engine) || _nativeImagePlaybackId != args.PlaybackId
            || _nativeImagePresentationEpoch != args.PresentationEpoch || _nativeImageTrackId != args.TrackId
            || _nativeImageGeneration != args.Generation)
        {
            ClearNativeSubtitleImage();
            _nativeImageOwner = engine;
            _nativeImagePlaybackId = args.PlaybackId;
            _nativeImagePresentationEpoch = args.PresentationEpoch;
            _nativeImageTrackId = args.TrackId;
            _nativeImageGeneration = args.Generation;
        }
        if (args.Sequence <= _latestNativeImageSequence) return;
        var frame = args.TakeFrame();
        if (frame is null) return;
        _latestNativeImageSequence = args.Sequence;
        ReleaseNativeImageFrame(_pendingNativeImageFrame);
        _pendingNativeImageFrame = new(engine, args.PlaybackId, args.PresentationEpoch, _nativeImageViewEpoch, frame);
        if (!_nativeImagePumpRunning) _ = PresentNativeSubtitleImagesAsync();
    }

    private async Task PresentNativeSubtitleImagesAsync()
    {
        if (_nativeImagePumpRunning) return;
        _nativeImagePumpRunning = true;
        try
        {
            // At most one upload and one replaceable pending snapshot retain bitmap pixels.
            while (_pendingNativeImageFrame is { } request)
            {
                _pendingNativeImageFrame = null;
                SoftwareBitmapSource? candidate = null;
                IAsyncAction? action = null;
                var assigned = false;
                try
                {
                    if (!OwnsNativeImageRequest(request)) continue;
                    candidate = new SoftwareBitmapSource();
                    action = candidate.SetBitmapAsync(request.Frame.Bitmap);
                    _nativeImageUploadAction = action;
                    await action;
                    if (!OwnsNativeImageRequest(request)
                        || request.Frame.Metadata.Sequence <= _committedNativeImageSequence) continue;

                    // A newer pending frame does not invalidate a completed frame in this generation.
                    // Otherwise a busy animation could prevent every upload from becoming visible.
                    var previous = _nativeImageSource;
                    NativeSubtitleImage.Source = candidate;
                    _nativeImageSource = candidate;
                    _nativeImageGeometry = request.Frame.Metadata.Geometry;
                    _committedNativeImageSequence = request.Frame.Metadata.Sequence;
                    assigned = true;
                    UpdateNativeSubtitleImageLayout();
                    ReleaseNativeImageSource(previous);
                    request.Engine.RecordAssSubtitleImageUpload(request.PlaybackId,
                        request.PresentationEpoch, request.Frame.Metadata);
                }
                catch (OperationCanceledException) when (!OwnsNativeImageRequest(request)) { }
                catch (Exception exception)
                {
                    AcceptanceDiagnostics.Record(exception, "NativeImage/Upload");
                    if (OwnsNativeImageRequest(request))
                    {
                        request.Engine.ReportAssSubtitlePresentationFailure(request.PlaybackId, request.PresentationEpoch, exception);
                        ClearNativeSubtitleImage();
                    }
                }
                finally
                {
                    if (ReferenceEquals(_nativeImageUploadAction, action)) _nativeImageUploadAction = null;
                    if (!assigned) ReleaseNativeImageSource(candidate);
                    // The source owns an independent snapshot; close it only after SetBitmapAsync finishes.
                    ReleaseNativeImageFrame(request);
                }
            }
        }
        finally { _nativeImagePumpRunning = false; }
    }

    private bool OwnsNativeImageRequest(NativeImageFrameRequest request) =>
        ReferenceEquals(request.Engine, _engine) && ReferenceEquals(request.Engine, _nativeImageOwner)
        && request.PlaybackId == _nativeImagePlaybackId && request.PresentationEpoch == _nativeImagePresentationEpoch
        && request.ViewEpoch == _nativeImageViewEpoch && request.Frame.Metadata.TrackId == _nativeImageTrackId
        && request.Frame.Metadata.Generation == _nativeImageGeneration
        && request.Engine.IsAssSubtitleFrameCurrent(request.PlaybackId, request.PresentationEpoch);

    private void NativeSubtitleViewportChanged(object sender, SizeChangedEventArgs args) => QueueNativeSubtitleImageLayout();

    private void QueueNativeSubtitleImageLayout()
    {
        if (!_subtitleLayoutLoaded || _nativeImageSource is null || _nativeImageLayoutQueued) return;
        var epoch = _nativeImageViewEpoch;
        _nativeImageLayoutQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (epoch != _nativeImageViewEpoch) return;
            _nativeImageLayoutQueued = false;
            if (!_subtitleLayoutLoaded) return;
            UpdateNativeSubtitleImageLayout();
        })) _nativeImageLayoutQueued = false;
    }

    private void NativeSubtitleControlsVisibilityChanged(bool visible, bool animated)
    {
        _nativeImageControlFadeTimer.Stop();
        // Keep the clearance through the existing HUD fade, with no per-animation layout work.
        if (!visible && animated) _nativeImageControlFadeTimer.Start();
        else _nativeImageControlsVisible = visible;
        QueueNativeSubtitleImageLayout();
    }

    private void UpdateNativeSubtitleImageLayout()
    {
        if (_nativeImageSource is null || _nativeImageGeometry is not { } geometry
            || !AssSubtitleLayoutPolicy.TryPlace(VideoSurface.ActualWidth, VideoSurface.ActualHeight, geometry, out var placement))
        {
            NativeSubtitleLayer.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var subtitle = _nativeImageOwner?.SubtitlePlayback;
            if (_nativeImageControlsVisible && subtitle is { IsBitmapSource: true, PreservesSourceStyles: false }
                && subtitle.PlaybackId == _nativeImagePlaybackId)
                placement = AssSubtitleLayoutPolicy.AvoidBottomControls(placement, NativeSubtitleBottomControls(), 12);

            // The player may retain a caption-band margin or move during resize and compact overlay transitions.
            var origin = VideoSurface.TransformToVisual(NativeSubtitleLayer).TransformPoint(new Point(0, 0));
            if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y))
            {
                NativeSubtitleLayer.Visibility = Visibility.Collapsed;
                return;
            }
            NativeSubtitleLayer.Clip = new RectangleGeometry
            {
                Rect = new Rect(origin.X + placement.Video.X, origin.Y + placement.Video.Y,
                    placement.Video.Width, placement.Video.Height)
            };
            Canvas.SetLeft(NativeSubtitleImage, origin.X + placement.Image.X);
            Canvas.SetTop(NativeSubtitleImage, origin.Y + placement.Image.Y);
            NativeSubtitleImage.Width = placement.Image.Width;
            NativeSubtitleImage.Height = placement.Image.Height;
            NativeSubtitleLayer.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            NativeSubtitleLayer.Visibility = Visibility.Collapsed;
            AcceptanceDiagnostics.Record(exception, "NativeImage/Layout");
        }
    }

    private AssSubtitleRectangle? NativeSubtitleBottomControls()
    {
        Rect? bounds = null;
        var controls = _compactOverlay
            ? new FrameworkElement[] { TimelineHost, CompactElapsedHost, CompactDurationHost }
            : new FrameworkElement[] { TimelineHost, TransportRow };
        foreach (var element in controls)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            // All geometry is measured in the current video viewport's DIPs, including letterboxing.
            var current = element.TransformToVisual(VideoSurface)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (!double.IsFinite(current.X) || !double.IsFinite(current.Y)
                || !double.IsFinite(current.Width) || !double.IsFinite(current.Height)) continue;
            if (bounds is { } previous)
            {
                var left = Math.Min(previous.X, current.X);
                var top = Math.Min(previous.Y, current.Y);
                bounds = new Rect(left, top,
                    Math.Max(previous.X + previous.Width, current.X + current.Width) - left,
                    Math.Max(previous.Y + previous.Height, current.Y + current.Height) - top);
            }
            else bounds = current;
        }
        return bounds is { } occupied
            ? new AssSubtitleRectangle(occupied.X, occupied.Y, occupied.Width, occupied.Height) : null;
    }

    private void ClearNativeSubtitleImage()
    {
        ++_nativeImageViewEpoch;
        _nativeImageLayoutQueued = false;
        ReleaseNativeImageFrame(_pendingNativeImageFrame);
        _pendingNativeImageFrame = null;
        try { _nativeImageUploadAction?.Cancel(); }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "NativeImage/Cancel"); }
        _nativeImageOwner = null;
        _nativeImagePlaybackId = null;
        _nativeImagePresentationEpoch = 0;
        _nativeImageTrackId = _nativeImageGeneration = _latestNativeImageSequence = _committedNativeImageSequence = 0;
        _nativeImageGeometry = null;
        NativeSubtitleImage.Source = null;
        NativeSubtitleLayer.Visibility = Visibility.Collapsed;
        NativeSubtitleLayer.Clip = null;
        var previous = _nativeImageSource;
        _nativeImageSource = null;
        ReleaseNativeImageSource(previous);
    }

    private static void ReleaseNativeImageFrame(NativeImageFrameRequest? request)
    {
        try { request?.Frame.Dispose(); }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "NativeImage/CloseFrame"); }
    }

    private static void ReleaseNativeImageSource(SoftwareBitmapSource? source)
    {
        try { if ((object?)source is IDisposable owner) owner.Dispose(); }
        catch (Exception exception) { AcceptanceDiagnostics.Record(exception, "NativeImage/CloseSource"); }
    }

    private sealed record NativeImageFrameRequest(NativePlaybackEngine Engine, Guid PlaybackId,
        long PresentationEpoch, long ViewEpoch, NativeAssFrameLease Frame);
}
