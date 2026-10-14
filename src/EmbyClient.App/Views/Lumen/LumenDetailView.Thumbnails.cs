using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private MediaThumbnailState? _mediaThumbnailState;
    private bool _mediaThumbnailLifetimeRegistered;
    private bool _mediaThumbnailReloadOnLoad;

    private Grid CreateMediaVideoLane(MediaCardViewModel media, MediaSourceInfo? source,
        MediaVisualizationChapter[] chapters, long duration, bool hasVideo)
    {
        var lane = new Grid { CornerRadius = new CornerRadius(6) };
        var chapterLane = CreateMediaChapterLane(media, chapters, duration, hasVideo);
        lane.Children.Add(chapterLane);
        if (!hasVideo) ClearMediaThumbnailMetadata();
        var state = hasVideo ? PrepareMediaThumbnailMetadata(media, source, duration) : null;
        if (state is null) return lane;
        var metadata = new Grid();
        lane.Children.Add(metadata);
        void UpdateMetadata()
        {
            if (!OwnsMediaThumbnailMetadata(state)) return;
            metadata.Children.Clear();
            chapterLane.Visibility = state.Images.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (state.Images.Length > 0)
            {
                metadata.Children.Add(CreateBifThumbnailLane(state, duration));
                return;
            }
            if (state.Positions.Length == 0) return;
            var ticks = new Canvas { IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(ticks, AccessibilityView.Raw);
            void ArrangeTicks()
            {
                ticks.Children.Clear();
                if (duration <= 0 || ticks.ActualWidth <= 0) return;
                foreach (var position in state.Positions)
                {
                    var mark = new Border { Width = 1, Height = 8, Background = ImageBrush(179) };
                    Canvas.SetLeft(mark, Math.Clamp(ticks.ActualWidth * position / duration, 0, Math.Max(0, ticks.ActualWidth - 1)));
                    Canvas.SetTop(mark, 0);
                    ticks.Children.Add(mark);
                }
            }
            ticks.SizeChanged += (_, _) => ArrangeTicks();
            ticks.Loaded += (_, _) => ArrangeTicks();
            metadata.Children.Add(ticks);
            var label = MediaMono(LumenText.Localize("Thumbnail positions \u00b7 metadata only",
                "\u7f29\u7565\u56fe\u65f6\u95f4\u70b9 \u00b7 \u4ec5\u5143\u6570\u636e"), 10.5, .8);
            var summary = string.Format(CultureInfo.CurrentCulture, LumenText.Localize("{0} of {1} server thumbnail positions shown",
                "\u663e\u793a {0} / {1} \u4e2a\u670d\u52a1\u5668\u7f29\u7565\u56fe\u65f6\u95f4\u70b9"), state.Positions.Length, state.TotalPositions);
            var positions = string.Join(", ", state.Positions.Select(MediaCardViewModel.FormatTime));
            var banner = new Border
            {
                Child = label, Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4),
                Margin = new Thickness(4, 0, 4, 3), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = Math.Max(0, lane.ActualWidth - 8),
                Background = new SolidColorBrush(Color.FromArgb(166, 0, 0, 0))
            };
            lane.SizeChanged += (_, _) => banner.MaxWidth = Math.Max(0, lane.ActualWidth - 8);
            var description = MediaJoin(label.Text, summary, positions);
            ToolTipService.SetToolTip(banner, description);
            AutomationProperties.SetName(banner, description);
            metadata.Children.Add(banner);
        }
        lane.Loaded += (_, _) =>
        {
            state.Changed -= UpdateMetadata;
            state.Changed += UpdateMetadata;
            StartMediaThumbnailLoad(state);
            UpdateMetadata();
        };
        lane.Unloaded += (_, _) => state.Changed -= UpdateMetadata;
        UpdateMetadata();
        return lane;
    }

    private Grid CreateBifThumbnailLane(MediaThumbnailState state, long duration)
    {
        var lane = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            ColumnSpacing = 2, CornerRadius = new CornerRadius(6) };
        var samples = state.Images;
        if (samples[0].PositionTicks > 0)
            lane.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(samples[0].PositionTicks / (double)duration, GridUnitType.Star)
            });
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var end = index + 1 < samples.Length ? samples[index + 1].PositionTicks : duration;
            var column = lane.ColumnDefinitions.Count;
            lane.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, end - sample.PositionTicks) / (double)duration, GridUnitType.Star)
            });
            var tile = new Grid { Background = ImageBrush(18) };
            var samplePosition = sample.PositionTicks;
            var image = new Image { Source = sample.Bitmap, Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false, Opacity = .92 };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            image.Loaded += (_, _) =>
            {
                if (OwnsMediaThumbnailMetadata(state)
                    && state.Images.FirstOrDefault(entry => entry.PositionTicks == samplePosition) is { } cached)
                    image.Source = cached.Bitmap;
            };
            image.Unloaded += (_, _) => image.Source = null;
            tile.Children.Add(image);
            tile.Children.Add(new Border
            {
                Background = Gradient(new Point(0, 0), new Point(0, 1),
                    (0, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(130, 0, 0, 0))), IsHitTestVisible = false
            });
            var time = MediaCardViewModel.FormatTime(sample.PositionTicks);
            var label = MediaMono(time, 10, .9);
            label.Margin = new Thickness(4, 0, 4, 3);
            label.VerticalAlignment = VerticalAlignment.Bottom;
            label.IsHitTestVisible = false;
            tile.Children.Add(label);
            var description = string.Format(CultureInfo.CurrentCulture,
                LumenText.Localize("Preview at {0}", "{0} \u7684\u9884\u89c8\u56fe"), time);
            ToolTipService.SetToolTip(tile, description);
            AutomationProperties.SetName(tile, description);
            Grid.SetColumn(tile, column);
            lane.Children.Add(tile);
        }
        lane.SizeChanged += (_, _) => lane.Clip = new RectangleGeometry { Rect = new Rect(0, 0, lane.ActualWidth, lane.ActualHeight) };
        return lane;
    }

    private MediaThumbnailState? PrepareMediaThumbnailMetadata(MediaCardViewModel media, MediaSourceInfo? source, long duration)
    {
        if (!_mediaThumbnailLifetimeRegistered)
        {
            _mediaThumbnailLifetimeRegistered = true;
            Unloaded += (_, _) =>
            {
                ClearMediaThumbnailMetadata();
                _mediaThumbnailReloadOnLoad = true;
            };
            Loaded += (_, _) =>
            {
                if (!_mediaThumbnailReloadOnLoad) return;
                _mediaThumbnailReloadOnLoad = false;
                _forceRender = true;
                Refresh();
            };
            _deck.PageChanged += (_, _) =>
            {
                if (_mediaThumbnailState is not { } current) return;
                if (_deck.SelectedIndex == (_episodePageIndex >= 0 ? 3 : 2)) StartMediaThumbnailLoad(current);
                else current.Request?.Cancel();
            };
        }
        // The current ThumbnailSet and BIF operations have no MediaSourceId contract. Never reuse item
        // thumbnails for an alternate version, an opening live stream, or a timeline with unknown duration.
        var context = _session is { } session && _detailId is { } detailId && _selectionItemId == media.Id
            && duration > 0 && source?.IsInfiniteStream != true && source?.RequiresOpening != true
            && IsPrimaryThumbnailSource(source, media.Item)
            ? new MediaThumbnailContext(detailId, _detailVersion, media.Id, _selectedSourceKey,
                source?.Id, source?.Path, duration, session.Api) : null;
        if (context != _mediaThumbnailState?.Context)
        {
            ClearMediaThumbnailMetadata();
            if (context is not null) _mediaThumbnailState = new MediaThumbnailState(context);
        }
        return _mediaThumbnailState;
    }

    private bool IsPrimaryThumbnailSource(MediaSourceInfo? source, BaseItemDto item)
    {
        var primary = item.MediaSources?.FirstOrDefault() ?? _sources.FirstOrDefault();
        return source is not null && primary is not null && (ReferenceEquals(source, primary)
            || !string.IsNullOrWhiteSpace(source.Id) && source.Id == primary.Id);
    }

    private bool OwnsMediaThumbnailMetadata(MediaThumbnailState state) => ReferenceEquals(_mediaThumbnailState, state)
        && ReferenceEquals(_session?.Api, state.Context.Api) && IsCurrentDetail(state.Context.DetailId, state.Context.Version)
        && _selectionItemId == state.Context.ItemId && _selectedSourceKey == state.Context.SourceKey
        && SelectedSource?.Id == state.Context.SourceId && SelectedSource?.Path == state.Context.SourcePath;

    private void StartMediaThumbnailLoad(MediaThumbnailState state)
    {
        if (!IsLoaded || _deck.SelectedIndex != (_episodePageIndex >= 0 ? 3 : 2) || !OwnsMediaThumbnailMetadata(state)
            || state.Loading || state.Loaded || _detailCancellation is not { IsCancellationRequested: false } lifetime) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        state.Request = request;
        state.Loading = true;
        _ = LoadMediaThumbnailMetadataAsync(state, request);
    }

    private async Task LoadMediaThumbnailMetadataAsync(MediaThumbnailState state, CancellationTokenSource request)
    {
        try
        {
            try
            {
                var thumbnailSet = await state.Context.Api.GetThumbnailSetAsync(state.Context.ItemId, 240, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (!OwnsMediaThumbnailMetadata(state)) return;
                var positions = (thumbnailSet?.Thumbnails ?? []).Where(thumbnail => thumbnail.PositionTicks is >= 0)
                    .Select(thumbnail => thumbnail.PositionTicks!.Value)
                    .Where(position => position <= state.Context.Duration)
                    .Distinct().OrderBy(position => position).ToArray();
                state.TotalPositions = positions.Length;
                var count = Math.Min(BifThumbnailReader.MaximumSamples, positions.Length);
                state.Positions = count == 0 ? [] : Enumerable.Range(0, count)
                    .Select(index => positions[count == 1 ? 0 : (int)Math.Round(index * (positions.Length - 1d) / (count - 1))]).ToArray();
                state.NotifyChanged();
            }
            catch (Exception exception) when (ExpectedRequestFailure(exception)
                && exception is not EmbyApiException { IsAuthenticationFailure: true })
            {
                // BIF delivery is independent of ThumbnailSet metadata availability.
            }
            request.Token.ThrowIfCancellationRequested();
            if (!OwnsMediaThumbnailMetadata(state)) return;
            var archive = await state.Context.Api.GetBifThumbnailsAsync(state.Context.ItemId, 240,
                state.Context.Duration, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!OwnsMediaThumbnailMetadata(state)) return;
            var images = new List<MediaThumbnailImage>(BifThumbnailReader.MaximumSamples);
            foreach (var sample in archive?.Thumbnails ?? [])
            {
                request.Token.ThrowIfCancellationRequested();
                if (!OwnsMediaThumbnailMetadata(state)) return;
                try
                {
                    var scale = Math.Min(1, Math.Min(240d / sample.PixelWidth, 135d / sample.PixelHeight));
                    var decodeWidth = Math.Max(1, (int)Math.Round(sample.PixelWidth * scale));
                    var decodeHeight = Math.Max(1, (int)Math.Round(sample.PixelHeight * scale));
                    await NativePosterDecoder.DecodeAndApplyAsync(sample.JpegBytes, decodeWidth, decodeHeight, request.Token, bitmap =>
                    {
                        if (!request.IsCancellationRequested && OwnsMediaThumbnailMetadata(state))
                            images.Add(new MediaThumbnailImage(sample.PositionTicks, bitmap));
                    });
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                    or System.IO.IOException or System.Runtime.InteropServices.COMException or NotSupportedException)
                {
                    // Native JPEG decoding is the final image check. A failed sample is never replaced with artwork.
                }
            }
            request.Token.ThrowIfCancellationRequested();
            if (!OwnsMediaThumbnailMetadata(state)) return;
            state.Images = images.ToArray();
            state.Loaded = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception) || exception is ArgumentException)
        {
            if (OwnsMediaThumbnailMetadata(state))
            {
                // Missing, empty or unsupported archives retain real chapter images and any available metadata.
                state.Loaded = true;
            }
        }
        finally
        {
            state.Loading = false;
            if (ReferenceEquals(state.Request, request)) state.Request = null;
            request.Dispose();
            if (OwnsMediaThumbnailMetadata(state))
            {
                state.NotifyChanged();
                if (!state.Loaded) StartMediaThumbnailLoad(state);
            }
        }
    }

    private void ClearMediaThumbnailMetadata()
    {
        var previous = _mediaThumbnailState;
        _mediaThumbnailState = null;
        previous?.Request?.Cancel();
        previous?.Release();
    }

    private sealed record MediaThumbnailContext(string DetailId, int Version, string ItemId, string? SourceKey,
        string? SourceId, string? SourcePath, long Duration, EmbyApiClient Api);

    private sealed record MediaThumbnailImage(long PositionTicks, BitmapImage Bitmap);

    private sealed class MediaThumbnailState(MediaThumbnailContext context)
    {
        public MediaThumbnailContext Context { get; } = context;
        public long[] Positions { get; set; } = [];
        public MediaThumbnailImage[] Images { get; set; } = [];
        public int TotalPositions { get; set; }
        public bool Loading { get; set; }
        public bool Loaded { get; set; }
        public CancellationTokenSource? Request { get; set; }
        public event Action? Changed;
        public void NotifyChanged() => Changed?.Invoke();
        public void Release()
        {
            Positions = [];
            Images = [];
            Changed = null;
        }
    }
}
