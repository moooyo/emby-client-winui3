using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private static bool _mediaVisualizationTextRegistered;
    private MediaMetadataState? _mediaMetadataState;
    private string? _pendingMediaSourceFocusKey;

    private void RenderMediaVisualization(MediaCardViewModel detail)
    {
        RegisterMediaVisualizationText();
        _mediaSection.Children.Clear();
        _mediaSection.Visibility = Visibility.Visible;
        _mediaSection.Spacing = 0;

        var media = detail.IsFolder ? _library.PlayableDetail : detail;
        var hasMedia = !media.IsFolder && !string.IsNullOrWhiteSpace(media.Id);
        var metadataPending = detail.IsFolder && hasMedia && !EnsureMediaMetadata(media);
        var item = media.Item;
        var source = hasMedia ? item.Id == _selectionItemId ? SelectedSource : item.MediaSources?.FirstOrDefault() : null;
        MediaStream[] streams = !hasMedia ? [] : item.Id == _selectionItemId ? SourceStreams
            : source?.MediaStreams ?? item.MediaStreams ?? [];
        var video = streams.FirstOrDefault(stream => stream.Type == "Video");
        var audio = streams.Where(stream => stream.Type == "Audio").ToArray();
        var subtitles = streams.Where(stream => stream.Type == "Subtitle").ToArray();
        var selectedAudio = audio.FirstOrDefault(stream => stream.Index == _selectedAudioStreamIndex)
            ?? audio.FirstOrDefault(stream => stream.IsDefault == true) ?? audio.FirstOrDefault();
        var duration = source?.RunTimeTicks is > 0 ? source.RunTimeTicks.Value : item.RunTimeTicks.GetValueOrDefault();
        var position = duration > 0 ? Math.Clamp(media.ResumeTicks, 0, duration) : 0;
        PrepareSubtitleTimelines(media, source, subtitles, duration);

        var header = new Grid { ColumnSpacing = 24, RowSpacing = 10, Margin = new Thickness(0, 0, 0, 22) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = ImageText(LumenText.Get("Media information"), 30, serif: true);
        heading.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        heading.VerticalAlignment = VerticalAlignment.Bottom;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        header.Children.Add(heading);
        var versionWidth = 0d;
        var versions = hasMedia && media.Id == _selectionItemId && _sources.Length > 1 ? CreateMediaVersionSelector(out versionWidth) : null;
        if (versions is not null)
        {
            versions.VerticalAlignment = VerticalAlignment.Bottom;
            versions.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(versions, 1);
            header.Children.Add(versions);
        }
        _mediaSection.Children.Add(header);

        if (detail.IsFolder && hasMedia)
        {
            var target = ImageText(Format("Media for {0}", media.SceneIdentity), 13, opacity: .8);
            target.Margin = new Thickness(0, -8, 0, 18);
            _mediaSection.Children.Add(target);
        }
        if (!hasMedia)
        {
            _mediaSection.Children.Add(ImageText(LumenText.Get("No playable media is available."), 14, opacity: .75));
            return;
        }
        if (metadataPending)
        {
            RenderMediaMetadataStatus();
            return;
        }

        var overview = new Grid { ColumnSpacing = 48, RowSpacing = 24 };
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(480) });
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var monitor = CreateMediaMonitor(media, source, video, position);
        overview.Children.Add(monitor);
        var marks = new StackPanel { Spacing = 22, VerticalAlignment = VerticalAlignment.Center };
        var largeLabels = new List<TextBlock>();
        AddMediaSpecification(marks, largeLabels, MediaResolutionTitle(video), MediaResolutionSummary(video));
        var range = VideoRange(video);
        AddMediaSpecification(marks, largeLabels, range.Length > 0 ? range : "\u2014", MediaColorSummary(video));
        AddMediaSpecification(marks, largeLabels, MediaAudioTitle(selectedAudio), selectedAudio is null
            ? LumenText.Get("No audio tracks") : MediaJoin(LanguageName(selectedAudio),
                selectedAudio.BitRate is > 0 ? Bitrate(selectedAudio.BitRate.Value) : null,
                selectedAudio.SampleRate is > 0 ? MediaSampleRate(selectedAudio.SampleRate.Value) : null));
        Grid.SetColumn(marks, 1);
        overview.Children.Add(marks);
        _mediaSection.Children.Add(overview);

        var visibleAudio = MediaVisibleTracks(audio, selectedAudio?.Index, 2);
        var visibleSubtitles = MediaVisibleTracks(subtitles, _selectedSubtitleStreamIndex, 3);
        var timeline = CreateMediaTimeline(media, source, streams, video, visibleAudio, visibleSubtitles,
            selectedAudio?.Index, duration, position);
        timeline.Margin = new Thickness(0, 28, 0, 0);
        _mediaSection.Children.Add(timeline);

        if (visibleAudio.Length < audio.Length || visibleSubtitles.Length < subtitles.Length)
        {
            var count = ImageText(Format("{0} of {1} tracks shown", visibleAudio.Length + visibleSubtitles.Length,
                audio.Length + subtitles.Length), 12, opacity: .6);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            count.Margin = new Thickness(0, 12, 0, 0);
            _mediaSection.Children.Add(count);
        }

        void ArrangeMediaOverview()
        {
            var viewportWidth = ActualWidth > 0 ? ActualWidth : 1280;
            var viewportHeight = ActualHeight > 0 ? ActualHeight : 800;
            var available = overview.ActualWidth > 0 ? overview.ActualWidth : Math.Max(240, viewportWidth - 112);
            var monitorWidth = Math.Min(560, Math.Min(available, viewportHeight * .34 * 16 / 9));
            var gap = Math.Clamp(viewportWidth * .04, 28, 64);
            var stacked = available < monitorWidth + gap + 240;
            overview.ColumnSpacing = stacked ? 0 : gap;
            overview.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(monitorWidth);
            overview.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(marks, stacked ? 0 : 1);
            Grid.SetRow(marks, stacked ? 1 : 0);
            monitor.Width = monitorWidth;
            monitor.Height = monitorWidth * 9 / 16;
            marks.Spacing = Math.Clamp(viewportHeight * .026, 14, 28);
            foreach (var label in largeLabels)
            {
                label.FontSize = Math.Clamp(viewportWidth * .036, 36, 62);
                label.LineHeight = label.FontSize * .9;
            }
            heading.FontSize = Math.Clamp(viewportWidth * .023, 24, 36);
            heading.LineHeight = heading.FontSize * 1.15;
            header.Margin = new Thickness(0, 0, 0, Math.Clamp(viewportHeight * .028, 16, 26));
            timeline.Margin = new Thickness(0, Math.Clamp(viewportHeight * .036, 20, 36), 0, 0);
            if (versions is not null)
            {
                heading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var headingWidth = heading.DesiredSize.Width;
                var narrowHeader = available < headingWidth + versionWidth + 24;
                header.RowSpacing = narrowHeader ? 10 : 0;
                Grid.SetColumn(versions, narrowHeader ? 0 : 1);
                Grid.SetRow(versions, narrowHeader ? 1 : 0);
                Grid.SetColumnSpan(versions, narrowHeader ? 2 : 1);
                versions.HorizontalAlignment = narrowHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                versions.MaxWidth = narrowHeader ? available : Math.Max(120, available - headingWidth - 24);
            }
        }
        void ViewportChanged(object sender, SizeChangedEventArgs args) => ArrangeMediaOverview();
        overview.SizeChanged += (_, _) => ArrangeMediaOverview();
        overview.Loaded += (_, _) =>
        {
            SizeChanged -= ViewportChanged;
            SizeChanged += ViewportChanged;
            ArrangeMediaOverview();
        };
        overview.Unloaded += (_, _) => SizeChanged -= ViewportChanged;
        ArrangeMediaOverview();
    }

    private FrameworkElement CreateMediaVersionSelector(out double naturalWidth)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (source, index) in _sources.Select((source, index) => (source, index)))
        {
            var key = SourceKey(source, index);
            var selected = key == _selectedSourceKey;
            var label = ImageText(MediaVersionLabel(source), 12.5, opacity: selected ? 1 : .72);
            label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
            label.Foreground = selected ? new SolidColorBrush(Color.FromArgb(255, 22, 19, 14)) : ImageBrush(255);
            label.TextWrapping = TextWrapping.NoWrap;
            var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = label, IsChecked = selected, Height = 28, MinHeight = 28, MinWidth = 28,
                Padding = new Thickness(13, 0, 13, 0), CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(0), UseSystemFocusVisuals = true,
                IsEnabled = !_actionBusy && !string.IsNullOrWhiteSpace(source.Id),
                Background = selected ? new SolidColorBrush(Color.FromArgb(255, 243, 242, 240)) : ImageBrush(0)
            };
            LumenUi.SetResource(button.Resources, "ToggleButtonBackground", button.Background);
            LumenUi.SetResource(button.Resources, "ToggleButtonBackgroundPointerOver", selected ? button.Background : ImageBrush(20));
            LumenUi.SetResource(button.Resources, "ToggleButtonBackgroundPressed", selected ? button.Background : ImageBrush(30));
            LumenUi.SetResource(button.Resources, "ToggleButtonBackgroundChecked", new SolidColorBrush(Color.FromArgb(255, 243, 242, 240)));
            LumenUi.SetResource(button.Resources, "ToggleButtonBackgroundCheckedPointerOver", new SolidColorBrush(Color.FromArgb(255, 243, 242, 240)));
            LumenUi.SetResource(button.Resources, "ToggleButtonBackgroundCheckedPressed", new SolidColorBrush(Color.FromArgb(255, 243, 242, 240)));
            var profileFive = (source.MediaStreams ?? []).Any(stream => stream.Type == "Video"
                && string.Equals(stream.ExtendedVideoSubType, "DoviProfile50", StringComparison.OrdinalIgnoreCase));
            var description = MediaJoin(VersionTitle(source), SourceSummary(source), profileFive
                ? LumenText.Localize("Dolby Vision Profile 5 \u00b7 requires server transcoding",
                    "\u675c\u6bd4\u89c6\u754c Profile 5 \u00b7 \u9700\u670d\u52a1\u5668\u8f6c\u7801") : null);
            ToolTipService.SetToolTip(button, description);
            AutomationProperties.SetName(button, LumenText.Get("Version") + ": " + label.Text);
            AutomationProperties.SetHelpText(button, description);
            button.Click += (_, _) =>
            {
                button.IsChecked = true;
                if (_selectedSourceKey == key || _actionBusy) return;
                _selectedSourceKey = key;
                SelectDefaultStreams();
                _pendingMediaSourceFocusKey = key;
                _forceRender = true;
                Refresh();
            };
            button.Loaded += (_, _) =>
            {
                if (_pendingMediaSourceFocusKey != key) return;
                _pendingMediaSourceFocusKey = null;
                button.Focus(FocusState.Programmatic);
            };
            row.Children.Add(button);
        }
        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        naturalWidth = row.DesiredSize.Width + 8;
        var scroll = new ScrollViewer
        {
            Content = row, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false
        };
        var group = new Border
        {
            Child = scroll, Padding = new Thickness(3), CornerRadius = new CornerRadius(18),
            Background = ImageBrush(18), BorderBrush = ImageBrush(26), BorderThickness = new Thickness(1)
        };
        AutomationProperties.SetName(group, LumenText.Get("Version"));
        return group;
    }

    private static string MediaVersionLabel(MediaSourceInfo source)
    {
        var video = (source.MediaStreams ?? []).FirstOrDefault(stream => stream.Type == "Video");
        var resolution = video?.Width is >= 3840 || video?.Height is >= 2160 ? "4K"
            : video?.Height is > 0 ? video.Height.Value.ToString(CultureInfo.InvariantCulture) + "p" : null;
        var range = VideoRange(video);
        return MediaJoin(resolution, range.Length > 0 && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase)
            ? range : video?.Codec?.ToUpperInvariant()) is { Length: > 0 } title ? title
                : source.Name ?? source.Container?.ToUpperInvariant() ?? LumenText.Get("Not available");
    }

    private bool EnsureMediaMetadata(MediaCardViewModel media)
    {
        if (_session is not { } session || _detailCancellation is null || _detailId is null) return false;
        var state = _mediaMetadataState;
        if (state is null || state.ItemId != media.Id || state.DetailId != _detailId || state.Version != _detailVersion
            || !ReferenceEquals(state.Session.Api, session.Api))
        {
            state?.Request?.Cancel();
            state = new MediaMetadataState(media.Id, _detailId, _detailVersion, session);
            _mediaMetadataState = state;
        }
        if (state.Loaded && state.Snapshot is { } snapshot
            && (!ReferenceEquals(media.Item.MediaSources, snapshot.MediaSources)
                || !ReferenceEquals(media.Item.MediaStreams, snapshot.MediaStreams)
                || !ReferenceEquals(media.Item.Chapters, snapshot.Chapters)))
        {
            // A sparse list refresh must not turn missing chapter or source fields into confirmed absence.
            state.Loaded = false;
            state.Failure = null;
        }
        if (!state.Loaded && !state.Loading && state.Failure is null)
        {
            state.Loading = true;
            state.Request = CancellationTokenSource.CreateLinkedTokenSource(_detailCancellation.Token);
            _ = LoadMediaMetadataAsync(state, state.Request);
        }
        return state.Loaded;
    }

    private async Task LoadMediaMetadataAsync(MediaMetadataState state, CancellationTokenSource request)
    {
        try
        {
            var item = await state.Session.Api.GetItemAsync(state.ItemId, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (!OwnsMediaMetadata(state)) return;
            var current = _library.PlayableDetail;
            var updated = item with { UserData = current.Item.UserData ?? item.UserData };
            current.ApplyItem(updated);
            UpdateStreamSelection(updated);
            state.Snapshot = updated;
            state.Loaded = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception) || exception is ArgumentException)
        {
            if (OwnsMediaMetadata(state)) state.Failure = DescribeRequestFailure(exception);
        }
        finally
        {
            state.Loading = false;
            if (ReferenceEquals(state.Request, request)) state.Request = null;
            request.Dispose();
            if (OwnsMediaMetadata(state))
            {
                _forceRender = true;
                Refresh();
            }
        }
    }

    private bool OwnsMediaMetadata(MediaMetadataState state) => ReferenceEquals(_mediaMetadataState, state)
        && ReferenceEquals(_session?.Api, state.Session.Api) && _detailCancellation?.IsCancellationRequested == false
        && IsCurrentDetail(state.DetailId, state.Version) && _library.PlayableDetail.Id == state.ItemId;

    private void RenderMediaMetadataStatus()
    {
        var status = new StackPanel { Spacing = 12, MinHeight = 180, Margin = new Thickness(0, 20, 0, 0) };
        if (_mediaMetadataState?.Failure is { } failure)
        {
            status.Children.Add(ImageText(LumenText.Get("Media information could not be loaded."), 15, weight: true));
            status.Children.Add(ImageText(failure, 13, opacity: .75));
            var retry = LumenUi.Button("Try again", "refresh", onImage: true, height: 36);
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Click += (_, _) =>
            {
                if (_mediaMetadataState is { Loading: false } current) current.Failure = null;
                _forceRender = true;
                Refresh();
            };
            status.Children.Add(retry);
        }
        else
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            loading.Children.Add(new ProgressRing { Width = 20, Height = 20, IsActive = true, Foreground = LumenTheme.Brush("ImageAccent") });
            loading.Children.Add(ImageText(LumenText.Get("Loading media information..."), 14, opacity: .75));
            status.Children.Add(loading);
        }
        _mediaSection.Children.Add(status);
    }

    private Grid CreateMediaMonitor(MediaCardViewModel media, MediaSourceInfo? source, MediaStream? video, long position)
    {
        var surface = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        var artwork = new LumenArtwork { CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
        artwork.Set(media, _library, ArtworkKind.Landscape, 1120);
        surface.Children.Add(artwork);
        surface.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(153, 0, 0, 0)), (.26, Color.FromArgb(0, 0, 0, 0)),
                (.7, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(168, 0, 0, 0)))
        });
        foreach (var (horizontal, vertical, thickness) in new[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(2, 2, 0, 0)),
            (HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 2, 2, 0)),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(2, 0, 0, 2)),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 2, 2))
        }) surface.Children.Add(new Border
        {
            Width = 20, Height = 20, Margin = new Thickness(12), BorderBrush = ImageBrush(230),
            BorderThickness = thickness, HorizontalAlignment = horizontal, VerticalAlignment = vertical, IsHitTestVisible = false
        });
        surface.Children.Add(new Border { Width = 22, Height = 2, Background = ImageBrush(153),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        surface.Children.Add(new Border { Width = 2, Height = 22, Background = ImageBrush(153),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });

        var duration = source?.RunTimeTicks is > 0 ? source.RunTimeTicks.Value : media.Item.RunTimeTicks.GetValueOrDefault();
        var timeReadout = position > 0 ? LumenText.Get("Saved position") + " " + MediaCardViewModel.FormatTime(position)
            : duration > 0 ? LumenText.Get("Duration") + " " + MediaCardViewModel.FormatTime(duration)
            : LumenText.Get("Duration unavailable");
        var top = MediaMonitorReadout(LumenText.Get("Artwork preview"), timeReadout, true);
        top.VerticalAlignment = VerticalAlignment.Top;
        top.Margin = new Thickness(42, 15, 42, 0);
        surface.Children.Add(top);
        var frameRate = video?.AverageFrameRate is > 0 ? video.AverageFrameRate : video?.RealFrameRate;
        var bottom = MediaMonitorReadout(MediaJoin(video?.Codec?.ToUpperInvariant(),
                frameRate is > 0 ? frameRate.Value.ToString("0.###", CultureInfo.InvariantCulture) + " FPS" : null),
            video?.BitRate is > 0 ? Bitrate(video.BitRate.Value).ToUpperInvariant()
                : source?.Bitrate is > 0 ? Bitrate(source.Bitrate.Value).ToUpperInvariant() : string.Empty, false);
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        bottom.Margin = new Thickness(42, 0, 42, 15);
        surface.Children.Add(bottom);
        AutomationProperties.SetName(surface, Format("Artwork preview for {0}", media.SceneIdentity));
        var monitor = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Width = 480, Height = 270 };
        var shadowHost = new Grid { IsHitTestVisible = false };
        monitor.Children.Add(shadowHost);
        monitor.Children.Add(new Border
        {
            Child = surface, CornerRadius = new CornerRadius(4), BorderBrush = ImageBrush(26), BorderThickness = new Thickness(1)
        });
        surface.Loaded += (_, _) => ApplyRoundedClip(surface, 4);
        surface.SizeChanged += (_, _) => ApplyRoundedClip(surface, 4);
        ConfigureDetailShadow(monitor, shadowHost, 4, 90, 34, .55f, 90, 34, .55f);
        return monitor;
    }

    private static Grid MediaMonitorReadout(string first, string second, bool status)
    {
        var row = new Grid { ColumnSpacing = 16, IsHitTestVisible = false };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new Grid { ColumnSpacing = 8 };
        if (status)
        {
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
                Background = LumenTheme.Brush("ImageAccent"), VerticalAlignment = VerticalAlignment.Center });
        }
        var label = MediaMono(first, 11, .92);
        Grid.SetColumn(label, status ? 1 : 0);
        left.Children.Add(label);
        row.Children.Add(left);
        var right = MediaMono(second, 11, .92);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    private static void AddMediaSpecification(StackPanel parent, ICollection<TextBlock> largeLabels, string value, string description)
    {
        var block = new StackPanel { Spacing = 8 };
        var main = ImageText(value.ToUpperInvariant(), 46);
        main.FontFamily = LumenTheme.DisplayFont;
        main.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        main.CharacterSpacing = 15;
        main.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        main.LineHeight = main.FontSize * .9;
        main.TextWrapping = TextWrapping.NoWrap;
        main.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(main, value);
        block.Children.Add(main);
        block.Children.Add(MediaMono(description, 12, .6));
        largeLabels.Add(main);
        parent.Children.Add(block);
    }

    private Border CreateMediaTimeline(MediaCardViewModel media, MediaSourceInfo? source, MediaStream[] streams, MediaStream? video,
        MediaStream[] audio, MediaStream[] subtitles, int? selectedAudio, long duration, long position)
    {
        var content = new StackPanel { Spacing = 12 };
        var file = MediaMono(MediaJoin(SafeFileName(source?.Path) ?? source?.Name ?? media.Title,
            source?.Size is > 0 ? FileSize(source.Size.Value) : null), 11.5, .6);
        ToolTipService.SetToolTip(file, file.Text);
        content.Children.Add(file);
        var rows = new Grid { ColumnSpacing = 18, RowSpacing = 6 };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(142) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMediaTimelineRow(rows, new Border(), CreateMediaRuler(duration), 22);
        var chapters = MediaTimelineChapters(source, media.Item, duration);
        var videoDescription = MediaJoin(video?.Codec?.ToUpperInvariant(), video?.BitRate is > 0 ? Bitrate(video.BitRate.Value) : null);
        AddMediaTimelineRow(rows, MediaLaneLabel("V1", LumenText.Get("Video"), videoDescription, false),
            CreateMediaVideoLane(media, source, chapters, duration, video is not null), 46);
        foreach (var stream in audio)
        {
            var ordinal = Array.FindIndex(streams.Where(value => value.Type == "Audio").ToArray(), value => value.Index == stream.Index) + 1;
            var selected = stream.Index == selectedAudio;
            var specification = MediaJoin(stream.Codec?.ToUpperInvariant(), ChannelName(stream));
            AddMediaTimelineRow(rows, MediaLaneLabel("A" + Math.Max(1, ordinal), LanguageName(stream), specification, selected),
                MediaAudioLane(stream, selected), 36);
        }
        foreach (var stream in subtitles)
        {
            var ordinal = Array.FindIndex(streams.Where(value => value.Type == "Subtitle").ToArray(), value => value.Index == stream.Index) + 1;
            var selected = stream.Index == _selectedSubtitleStreamIndex;
            AddMediaTimelineRow(rows, MediaLaneLabel("S" + Math.Max(1, ordinal), LanguageName(stream), stream.Codec?.ToUpperInvariant(), selected, true),
                CreateSubtitleTimelineLane(source, stream, selected, duration), 24);
        }
        if (duration > 0 && position > 0 && position < duration)
        {
            var playhead = CreateMediaPlayhead(position, duration);
            Grid.SetColumn(playhead, 1);
            Grid.SetRowSpan(playhead, rows.RowDefinitions.Count);
            rows.Children.Add(playhead);
        }
        rows.SizeChanged += (_, _) =>
        {
            var viewport = ActualWidth > 0 ? ActualWidth : 1280;
            rows.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(viewport * .11, 104, 168));
            rows.ColumnSpacing = Math.Clamp(viewport * .014, 10, 18);
        };
        content.Children.Add(rows);
        var panel = new Border
        {
            Child = content, CornerRadius = new CornerRadius(16), BorderBrush = ImageBrush(20), BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 16),
            Background = new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 10, 10, 11), TintOpacity = .5, TintLuminosityOpacity = .25,
                FallbackColor = Color.FromArgb(240, 10, 10, 11), TintTransitionDuration = TimeSpan.Zero
            }
        };
        panel.SizeChanged += (_, _) =>
        {
            var inset = Math.Clamp((ActualWidth > 0 ? ActualWidth : 1280) * .016, 14, 20);
            panel.Padding = new Thickness(inset, 14, inset, 16);
        };
        AutomationProperties.SetName(panel, LumenText.Get("Media track overview"));
        return panel;
    }

    private static void AddMediaTimelineRow(Grid grid, FrameworkElement label, FrameworkElement track, double height)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        Grid.SetRow(label, row);
        Grid.SetRow(track, row);
        Grid.SetColumn(track, 1);
        grid.Children.Add(label);
        grid.Children.Add(track);
    }

    private static FrameworkElement MediaLaneLabel(string code, string name, string? description, bool selected, bool compact = false)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = compact ? 0 : 2 };
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var index = MediaMono(code, 11, selected ? 1 : .45);
        if (selected) index.Foreground = LumenTheme.Brush("ImageAccent");
        heading.Children.Add(index);
        var title = ImageText(name, compact ? 13 : 13.5, weight: true);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, 1);
        heading.Children.Add(title);
        if (compact && !string.IsNullOrWhiteSpace(description))
        {
            heading.ColumnDefinitions[1].Width = GridLength.Auto;
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var specification = MediaMono(description, 10.5, .5);
            specification.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(specification, 2);
            heading.Children.Add(specification);
        }
        content.Children.Add(heading);
        if (!compact && !string.IsNullOrWhiteSpace(description)) content.Children.Add(MediaMono(description, 10.5, .5));
        ToolTipService.SetToolTip(content, MediaJoin(code, name, description, selected ? LumenText.Get("Selected") : null));
        return content;
    }

    private static Border MediaAudioLane(MediaStream stream, bool selected)
    {
        var tint = selected ? LumenTheme.Brush("ImageAccent") : ImageBrush(82);
        var bar = new Border
        {
            Height = 10, CornerRadius = new CornerRadius(2), Background = tint,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center
        };
        var lane = new Border
        {
            Child = bar, CornerRadius = new CornerRadius(6), Background = ImageBrush(12), Padding = new Thickness(3, 0, 3, 0)
        };
        var description = MediaJoin(StreamTitle(stream), StreamSummary(stream),
            stream.BitRate is > 0 ? Bitrate(stream.BitRate.Value) : null,
            selected ? LumenText.Get("Selected") : null);
        ToolTipService.SetToolTip(lane, description);
        AutomationProperties.SetName(lane, description);
        return lane;
    }

    private FrameworkElement CreateMediaChapterLane(MediaCardViewModel media, MediaVisualizationChapter[] chapters, long duration, bool hasVideo)
    {
        var lane = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), ColumnSpacing = 2, CornerRadius = new CornerRadius(6) };
        if (chapters.Length == 0)
        {
            if (hasVideo)
            {
                var artwork = new LumenArtwork { CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
                artwork.Set(media, _library, ArtworkKind.Landscape, 720);
                artwork.Opacity = .45;
                lane.Children.Add(artwork);
            }
            var unavailable = MediaMono(LumenText.Get(hasVideo ? "Chapter previews are unavailable." : "No video track"), 11, .75);
            unavailable.Margin = new Thickness(12, 0, 12, 0);
            unavailable.VerticalAlignment = VerticalAlignment.Center;
            lane.Children.Add(unavailable);
            return lane;
        }

        var count = Math.Min(12, chapters.Length);
        var previews = Enumerable.Range(0, count).Select(index => chapters[count == 1 ? 0
            : (int)Math.Round(index * (chapters.Length - 1d) / (count - 1))]).ToArray();
        if (previews[0].Chapter.StartPositionTicks > 0)
            lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(previews[0].Chapter.StartPositionTicks / (double)duration, GridUnitType.Star) });
        for (var index = 0; index < previews.Length; index++)
        {
            var preview = previews[index];
            var start = preview.Chapter.StartPositionTicks;
            var end = index + 1 < previews.Length ? previews[index + 1].Chapter.StartPositionTicks : duration;
            var column = lane.ColumnDefinitions.Count;
            lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, end - start) / (double)duration, GridUnitType.Star) });
            var tile = new Grid { Background = ImageBrush(18) };
            if (!string.IsNullOrWhiteSpace(preview.Chapter.ImageTag)) tile.Children.Add(CreateMediaChapterImage(media, preview));
            tile.Children.Add(new Border { Background = Gradient(new Point(0, 0), new Point(0, 1),
                (0, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(130, 0, 0, 0))), IsHitTestVisible = false });
            var title = MediaJoin(preview.Chapter.Name ?? Format("Chapter {0}", preview.Index + 1), MediaCardViewModel.FormatTime(start));
            ToolTipService.SetToolTip(tile, title);
            AutomationProperties.SetName(tile, title);
            Grid.SetColumn(tile, column);
            lane.Children.Add(tile);
        }
        lane.SizeChanged += (_, _) => lane.Clip = new RectangleGeometry { Rect = new Rect(0, 0, lane.ActualWidth, lane.ActualHeight) };
        return lane;
    }

    private Image CreateMediaChapterImage(MediaCardViewModel media, MediaVisualizationChapter preview)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, Opacity = .92 };
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        CancellationTokenSource? pending = null;
        image.Loaded += async (_, _) =>
        {
            if (pending is not null || image.Source is not null || _session is not { } session) return;
            var index = preview.Chapter.ChapterIndex ?? preview.Index;
            if (index < 0 || string.IsNullOrWhiteSpace(preview.Chapter.ImageTag)) return;
            var request = CancellationTokenSource.CreateLinkedTokenSource(_detailCancellation?.Token ?? CancellationToken.None);
            pending = request;
            try
            {
                var bytes = await session.Api.GetItemImageAsync(media.Id, "Chapter", index,
                    new ImageOptions { Tag = preview.Chapter.ImageTag, MaxWidth = 240, MaxHeight = 135 }, request.Token);
                if (bytes.Length == 0 || !image.IsLoaded || request.IsCancellationRequested || !ReferenceEquals(pending, request)) return;
                await NativePosterDecoder.DecodeAndApplyAsync(bytes, 240, 135, request.Token, bitmap =>
                {
                    if (image.IsLoaded && !request.IsCancellationRequested && ReferenceEquals(pending, request)
                        && ReferenceEquals(_session?.Api, session.Api)) image.Source = bitmap;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // A missing chapter image leaves its real timestamp and metadata in place.
            }
            finally
            {
                if (ReferenceEquals(pending, request)) pending = null;
                request.Dispose();
            }
        };
        image.Unloaded += (_, _) => { pending?.Cancel(); pending = null; image.Source = null; };
        return image;
    }

    private static Canvas CreateMediaRuler(long duration)
    {
        var ruler = new Canvas { Height = 22, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(ruler, AccessibilityView.Raw);
        void ArrangeRuler()
        {
            if (ruler.ActualWidth <= 0) return;
            ruler.Children.Clear();
            if (duration <= 0)
            {
                ruler.Children.Add(MediaMono(LumenText.Get("Duration unavailable"), 10.5, .55));
                return;
            }
            for (var tick = 0; tick <= 40; tick++)
            {
                var line = new Border { Width = 1, Height = tick % 10 == 0 ? 5 : 3, Background = ImageBrush(77) };
                Canvas.SetLeft(line, Math.Min(ruler.ActualWidth - 1, ruler.ActualWidth * tick / 40));
                Canvas.SetTop(line, 22 - line.Height);
                ruler.Children.Add(line);
            }
            var divisions = ruler.ActualWidth < 320 ? 2 : 4;
            for (var index = 0; index <= divisions; index++)
            {
                var label = MediaMono(MediaCardViewModel.FormatTime((long)(duration * (index / (double)divisions))), 10.5, .55);
                label.Measure(new Size(double.PositiveInfinity, 22));
                var left = ruler.ActualWidth * index / divisions - (index == 0 ? 0 : index == divisions ? label.DesiredSize.Width : label.DesiredSize.Width / 2);
                Canvas.SetLeft(label, Math.Max(0, left));
                ruler.Children.Add(label);
            }
        }
        ruler.SizeChanged += (_, _) => ArrangeRuler();
        ruler.Loaded += (_, _) => ArrangeRuler();
        return ruler;
    }

    private static Canvas CreateMediaPlayhead(long position, long duration)
    {
        var overlay = new Canvas { IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(overlay, AccessibilityView.Raw);
        var line = new Border { Width = 2, Background = LumenTheme.Brush("ImageAccent") };
        var label = MediaMono(MediaCardViewModel.FormatTime(position), 10.5);
        label.Foreground = LumenTheme.Brush("ImageAccentInk");
        var badge = new Border { Child = label, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(10),
            Background = LumenTheme.Brush("ImageAccent") };
        overlay.Children.Add(line);
        overlay.Children.Add(badge);
        void ArrangePlayhead()
        {
            var x = overlay.ActualWidth * Math.Clamp(position / (double)duration, 0, 1);
            line.Height = overlay.ActualHeight + 4;
            Canvas.SetLeft(line, x - 1);
            badge.Measure(new Size(double.PositiveInfinity, 30));
            Canvas.SetLeft(badge, Math.Clamp(x - badge.DesiredSize.Width / 2, 0, Math.Max(0, overlay.ActualWidth - badge.DesiredSize.Width)));
            Canvas.SetTop(badge, -4);
        }
        overlay.SizeChanged += (_, _) => ArrangePlayhead();
        overlay.Loaded += (_, _) => ArrangePlayhead();
        return overlay;
    }

    private MediaVisualizationChapter[] MediaTimelineChapters(MediaSourceInfo? source, BaseItemDto item, long duration)
    {
        if (duration <= 0) return [];
        var chapters = source?.Chapters ?? (MediaUsesItemTimeline(source, item) ? item.Chapters : null) ?? [];
        return chapters.Select((chapter, index) => new MediaVisualizationChapter(chapter, index))
            .Where(entry => entry.Chapter.StartPositionTicks >= 0 && entry.Chapter.StartPositionTicks < duration
                && (entry.Chapter.MarkerType is not ("IntroStart" or "IntroEnd" or "CreditsStart") || !string.IsNullOrWhiteSpace(entry.Chapter.ImageTag)))
            .OrderBy(entry => entry.Chapter.StartPositionTicks)
            .GroupBy(entry => entry.Chapter.StartPositionTicks)
            .Select(group => group.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Chapter.ImageTag)) ?? group.First()).ToArray();
    }

    private bool MediaUsesItemTimeline(MediaSourceInfo? source, BaseItemDto item)
    {
        var primary = item.MediaSources?.FirstOrDefault();
        return source is null || primary is null || ReferenceEquals(source, primary)
            || _selectionItemId == item.Id && ReferenceEquals(source, _sources.FirstOrDefault())
            || !string.IsNullOrWhiteSpace(source.Id) && source.Id == primary.Id;
    }

    private static MediaStream[] MediaVisibleTracks(MediaStream[] streams, int? selectedIndex, int limit)
    {
        var visible = streams.Take(limit).ToArray();
        if (streams.FirstOrDefault(stream => stream.Index == selectedIndex) is { } selected
            && !visible.Any(stream => stream.Index == selected.Index) && visible.Length > 0) visible[^1] = selected;
        return visible;
    }

    private static string MediaResolutionTitle(MediaStream? video) => video?.Width is >= 7680 || video?.Height is >= 4320 ? "8K Ultra HD"
        : video?.Width is >= 3840 || video?.Height is >= 2160 ? "4K Ultra HD"
        : video?.Width is >= 1920 || video?.Height is >= 1080 ? "Full HD"
        : video?.Width is >= 1280 || video?.Height is >= 720 ? "HD 720p"
        : video?.Height is > 0 ? video.Height.Value.ToString(CultureInfo.InvariantCulture) + "p" : "\u2014";

    private static string MediaResolutionSummary(MediaStream? video) => MediaJoin(
        video?.Width is > 0 && video.Height is > 0 ? $"{video.Width} \u00d7 {video.Height}" : LumenText.Get("Resolution unavailable"),
        video?.AspectRatio);

    private static string MediaColorSummary(MediaStream? video)
    {
        var summary = MediaJoin(video?.ColorPrimaries?.ToUpperInvariant(), video?.ColorTransfer?.ToUpperInvariant(),
            video?.BitDepth is > 0 ? video.BitDepth.Value.ToString(CultureInfo.InvariantCulture) + "-bit" : null);
        return summary.Length > 0 ? summary : LumenText.Get("Color information unavailable");
    }

    private static string MediaAudioTitle(MediaStream? audio)
    {
        if (audio is null) return "\u2014";
        if (new[] { audio.DisplayTitle, audio.Title, audio.Profile }.Any(value => value?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true))
            return "Dolby Atmos";
        var channels = !string.IsNullOrWhiteSpace(audio.ChannelLayout) ? audio.ChannelLayout
            : audio.Channels == 2 ? "Stereo" : audio.Channels is > 0 ? audio.Channels.Value.ToString(CultureInfo.InvariantCulture) + " CH" : null;
        var result = string.Join(" ", new[] { audio.Codec?.ToUpperInvariant(), channels }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return result.Length > 0 ? result : "\u2014";
    }

    private static string MediaSampleRate(int sampleRate) => (sampleRate / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + " kHz";
    private static string MediaJoin(params string?[] values) => string.Join(" \u00b7 ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static TextBlock MediaMono(string text, double size, double opacity = 1)
    {
        var label = ImageText(text, size, opacity: opacity);
        label.FontFamily = LumenTheme.MonoFont;
        label.CharacterSpacing = 40;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        return label;
    }

    private static void RegisterMediaVisualizationText()
    {
        if (_mediaVisualizationTextRegistered) return;
        _mediaVisualizationTextRegistered = true;
        LumenText.Register(new Dictionary<string, string>
        {
            ["Version {0}"] = "\u7248\u672c {0}",
            ["Container"] = "\u5bb9\u5668",
            ["File size"] = "\u6587\u4ef6\u5927\u5c0f",
            ["Duration"] = "\u65f6\u957f",
            ["Path"] = "\u8def\u5f84",
            ["Loading media information..."] = "\u6b63\u5728\u52a0\u8f7d\u5a92\u4f53\u4fe1\u606f...",
            ["Media information could not be loaded."] = "\u5a92\u4f53\u4fe1\u606f\u52a0\u8f7d\u5931\u8d25\u3002",
            ["Media for {0}"] = "\u5f53\u524d\u5355\u96c6\uff1a{0}",
            ["Artwork preview"] = "\u5c01\u9762\u9884\u89c8",
            ["Saved position"] = "\u4e0a\u6b21\u770b\u5230",
            ["Artwork preview for {0}"] = "{0} \u7684\u5c01\u9762\u9884\u89c8",
            ["Media track overview"] = "\u5a92\u4f53\u8f68\u9053\u6982\u89c8",
            ["{0} of {1} tracks shown"] = "\u663e\u793a {0} / {1} \u6761\u8f68\u9053",
            ["Chapter previews are unavailable."] = "\u6682\u65e0\u7ae0\u8282\u9884\u89c8",
            ["Chapter {0}"] = "\u7b2c {0} \u7ae0",
            ["No video track"] = "\u65e0\u89c6\u9891\u8f68\u9053",
            ["Duration unavailable"] = "\u6682\u65e0\u65f6\u957f\u4fe1\u606f",
            ["Resolution unavailable"] = "\u6682\u65e0\u5206\u8fa8\u7387\u4fe1\u606f",
            ["Color information unavailable"] = "\u6682\u65e0\u8272\u5f69\u4fe1\u606f"
        });
    }

    private sealed record MediaVisualizationChapter(ChapterInfo Chapter, int Index);

    private sealed class MediaMetadataState(string itemId, string detailId, int version, ConnectedSession session)
    {
        public string ItemId { get; } = itemId;
        public string DetailId { get; } = detailId;
        public int Version { get; } = version;
        public ConnectedSession Session { get; } = session;
        public bool Loading { get; set; }
        public bool Loaded { get; set; }
        public string? Failure { get; set; }
        public BaseItemDto? Snapshot { get; set; }
        public CancellationTokenSource? Request { get; set; }
    }
}
