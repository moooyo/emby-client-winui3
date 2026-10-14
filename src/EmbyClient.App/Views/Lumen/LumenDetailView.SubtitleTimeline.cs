using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private readonly Dictionary<int, SubtitleTimelineTrackState> _subtitleTimelineTracks = [];
    private SubtitleTimelineContext? _subtitleTimelineContext;
    private bool _subtitleTimelineLifetimeRegistered;
    private bool _subtitleTimelineReloadOnLoad;
    private static bool _subtitleTimelineTextRegistered;

    private void PrepareSubtitleTimelines(MediaCardViewModel media, MediaSourceInfo? source, MediaStream[] streams, long duration)
    {
        RegisterSubtitleTimelineText();
        if (!_subtitleTimelineLifetimeRegistered)
        {
            _subtitleTimelineLifetimeRegistered = true;
            Unloaded += (_, _) =>
            {
                ClearSubtitleTimelines();
                _subtitleTimelineReloadOnLoad = true;
            };
            Loaded += (_, _) =>
            {
                if (!_subtitleTimelineReloadOnLoad) return;
                _subtitleTimelineReloadOnLoad = false;
                _forceRender = true;
                Refresh();
            };
            _deck.PageChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var state in _subtitleTimelineTracks.Values.ToArray()) StartSubtitleTimelineLoad(state);
            });
        }
        var context = _session is { } session && _detailId is { } detailId && source is not null
            ? new SubtitleTimelineContext(detailId, _detailVersion, media.Id, source.Id, source.Path,
                duration, _selectedSubtitleStreamIndex, session.Api) : null;
        if (context != _subtitleTimelineContext)
        {
            ClearSubtitleTimelines();
            _subtitleTimelineContext = context;
        }
        foreach (var entry in _subtitleTimelineTracks.ToArray())
        {
            if (streams.FirstOrDefault(stream => stream.Index == entry.Key) == entry.Value.Stream) continue;
            entry.Value.Request?.Cancel();
            _subtitleTimelineTracks.Remove(entry.Key);
        }
    }

    private void ClearSubtitleTimelines()
    {
        foreach (var state in _subtitleTimelineTracks.Values) state.Request?.Cancel();
        _subtitleTimelineTracks.Clear();
        _subtitleTimelineContext = null;
    }

    private FrameworkElement CreateSubtitleTimelineLane(MediaSourceInfo? source, MediaStream stream, bool selected, long duration)
    {
        if (SubtitleTimelineService.IsGraphicalTrack(stream)) return CreateGraphicalSubtitleLane(stream);
        if (!SubtitleTimelineService.IsTextTrack(stream))
            return SubtitleTimelineMessage(stream, "Text cue timing is unavailable for this subtitle format.");
        if (duration <= 0) return SubtitleTimelineMessage(stream, "Subtitle cue positions need a known media duration.");
        if (_subtitleTimelineContext is not { } context || source is null || string.IsNullOrWhiteSpace(source.Id))
            return SubtitleTimelineMessage(stream, "The subtitle media source is unavailable.");
        if (source.IsInfiniteStream == true || source.RequiresOpening == true)
            return SubtitleTimelineMessage(stream, "Subtitle timelines are unavailable for live media.");
        if (!_subtitleTimelineTracks.TryGetValue(stream.Index, out var state))
        {
            state = new SubtitleTimelineTrackState(context, source, stream);
            _subtitleTimelineTracks.Add(stream.Index, state);
        }
        var host = new Border { CornerRadius = new CornerRadius(6) };
        void UpdateLane()
        {
            if (!OwnsSubtitleTimeline(state)) return;
            host.Child = state.Result?.Document is { } document
                ? CreateSubtitleCueChart(stream, document, selected, duration)
                : state.Result?.UnavailableReason is { } unavailable ? SubtitleTimelineMessage(stream, unavailable)
                : state.Failure is { } failure ? CreateSubtitleRetry(state, failure) : CreateSubtitleLoading(stream);
        }
        host.Loaded += (_, _) =>
        {
            state.Changed -= UpdateLane;
            state.Changed += UpdateLane;
            StartSubtitleTimelineLoad(state);
            UpdateLane();
        };
        // Rebuilding the surrounding media layout does not end this request. The detail lifetime,
        // source/track identity, and the view's Unloaded event own cancellation instead.
        host.Unloaded += (_, _) => state.Changed -= UpdateLane;
        UpdateLane();
        return host;
    }

    private static Border CreateGraphicalSubtitleLane(MediaStream stream)
    {
        var surface = new Grid { Background = ImageBrush(12) };
        var stripes = new Canvas { IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(stripes, AccessibilityView.Raw);
        void ArrangeStripes()
        {
            stripes.Children.Clear();
            var height = stripes.ActualHeight;
            var step = 12 * Math.Sqrt(2);
            var band = step / 2;
            for (var x = 0d; x < stripes.ActualWidth + height + band; x += step)
            {
                stripes.Children.Add(new Microsoft.UI.Xaml.Shapes.Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(x, 0), new Point(x + band, 0),
                        new Point(x + band - height, height), new Point(x - height, height)
                    },
                    Fill = ImageBrush(13), IsHitTestVisible = false
                });
            }
            stripes.Clip = new RectangleGeometry { Rect = new Rect(0, 0, stripes.ActualWidth, height) };
        }
        stripes.Loaded += (_, _) => ArrangeStripes();
        stripes.SizeChanged += (_, _) => ArrangeStripes();
        surface.Children.Add(stripes);
        var text = MediaMono(LumenText.Localize("Graphical subtitles \u00b7 no timeline data",
            "\u56fe\u5f62\u5b57\u5e55 \u00b7 \u65e0\u65f6\u95f4\u8f74\u6570\u636e"), 10.5, .55);
        text.Margin = new Thickness(10, 0, 10, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        surface.Children.Add(text);
        var lane = new Border { Child = surface, CornerRadius = new CornerRadius(6) };
        lane.Loaded += (_, _) => ApplyRoundedClip(lane, 6);
        lane.SizeChanged += (_, _) => ApplyRoundedClip(lane, 6);
        var description = MediaJoin(StreamTitle(stream), StreamSummary(stream), text.Text);
        ToolTipService.SetToolTip(lane, description);
        AutomationProperties.SetName(lane, description);
        return lane;
    }

    private bool OwnsSubtitleTimeline(SubtitleTimelineTrackState state) => ReferenceEquals(_subtitleTimelineContext, state.Context)
        && _subtitleTimelineTracks.TryGetValue(state.Stream.Index, out var current) && ReferenceEquals(current, state)
        && ReferenceEquals(_session?.Api, state.Context.Api) && IsCurrentDetail(state.Context.DetailId, state.Context.Version)
        && _selectionItemId == state.Context.ItemId && _library.PlayableDetail.Id == state.Context.ItemId
        && SelectedSource?.Id == state.Context.SourceId && SelectedSource?.Path == state.Context.SourcePath
        && _selectedSubtitleStreamIndex == state.Context.SelectedSubtitle
        && SourceStreams.FirstOrDefault(stream => stream.Index == state.Stream.Index) == state.Stream;

    private void StartSubtitleTimelineLoad(SubtitleTimelineTrackState state)
    {
        if (!IsLoaded || _deck.SelectedIndex != (_episodePageIndex >= 0 ? 3 : 2)
            || !OwnsSubtitleTimeline(state) || state.Loading || state.Result is not null || state.Failure is not null
            || _detailCancellation is not { IsCancellationRequested: false } lifetime) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        state.Request = request;
        state.Loading = true;
        state.NotifyChanged();
        _ = LoadSubtitleTimelineAsync(state, request);
    }

    private async Task LoadSubtitleTimelineAsync(SubtitleTimelineTrackState state, CancellationTokenSource request)
    {
        try
        {
            var result = await SubtitleTimelineService.LoadAsync(state.Context.Api, state.Context.ItemId,
                state.Source, state.Stream, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (OwnsSubtitleTimeline(state)) state.Result = result;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception) || exception is ArgumentException or FormatException or ObjectDisposedException)
        {
            if (OwnsSubtitleTimeline(state)) state.Failure = exception is ArgumentException or FormatException
                ? LumenText.Get("The subtitle data could not be read. Try again or choose another track.") : DescribeRequestFailure(exception);
        }
        finally
        {
            state.Loading = false;
            if (ReferenceEquals(state.Request, request)) state.Request = null;
            request.Dispose();
            if (OwnsSubtitleTimeline(state)) state.NotifyChanged();
        }
    }

    private static Border CreateSubtitleLoading(MediaStream stream)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0) };
        content.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14, Foreground = LumenTheme.Brush("ImageAccent") });
        content.Children.Add(MediaMono(LumenText.Get("Loading subtitle cues..."), 10.5, .6));
        var lane = new Border { Child = content, CornerRadius = new CornerRadius(6), Background = ImageBrush(12) };
        AutomationProperties.SetName(lane, MediaJoin(StreamTitle(stream), LumenText.Get("Loading subtitle cues...")));
        AutomationProperties.SetLiveSetting(lane, AutomationLiveSetting.Polite);
        return lane;
    }

    private static Border SubtitleTimelineMessage(MediaStream stream, string message)
    {
        var text = MediaMono(LumenText.Get(message), 10.5, .55);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(10, 0, 10, 0);
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var lane = new Border { Child = text, Background = ImageBrush(12), CornerRadius = new CornerRadius(6) };
        var description = MediaJoin(StreamTitle(stream), StreamSummary(stream), text.Text);
        ToolTipService.SetToolTip(lane, description);
        AutomationProperties.SetName(lane, description);
        return lane;
    }

    private Button CreateSubtitleRetry(SubtitleTimelineTrackState state, string failure)
    {
        var retry = LumenUi.Button(string.Empty, onImage: true, height: 24);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(LumenUi.Icon("refresh", 12, true));
        content.Children.Add(MediaMono(LumenText.Get("Retry subtitle cues"), 10.5, .75));
        retry.Content = content;
        retry.HorizontalAlignment = HorizontalAlignment.Stretch;
        retry.HorizontalContentAlignment = HorizontalAlignment.Left;
        retry.Padding = new Thickness(10, 0, 10, 0);
        retry.CornerRadius = new CornerRadius(6);
        retry.BorderThickness = new Thickness(0);
        retry.Background = ImageBrush(12);
        LumenUi.SetResource(retry.Resources, "ButtonBackground", retry.Background);
        LumenUi.SetResource(retry.Resources, "ButtonBackgroundPointerOver", ImageBrush(20));
        LumenUi.SetResource(retry.Resources, "ButtonBackgroundPressed", ImageBrush(30));
        ToolTipService.SetToolTip(retry, failure);
        AutomationProperties.SetName(retry, MediaJoin(StreamTitle(state.Stream), LumenText.Get("Subtitle cues could not be loaded."),
            failure, LumenText.Get("Retry subtitle cues")));
        retry.Click += (_, _) =>
        {
            if (!OwnsSubtitleTimeline(state) || state.Loading) return;
            state.Failure = null;
            StartSubtitleTimelineLoad(state);
        };
        return retry;
    }

    private Button CreateSubtitleCueChart(MediaStream stream, SubtitleTimelineDocument document, bool selected, long duration)
    {
        var intervals = SubtitleTimelineService.Project(document, duration);
        var canvas = new Canvas { Height = 24, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(canvas, AccessibilityView.Raw);
        void ArrangeCues()
        {
            canvas.Children.Clear();
            foreach (var interval in intervals)
            {
                var marker = new Border { Height = 8, Width = Math.Max(0, canvas.ActualWidth * (interval.EndFraction - interval.StartFraction)),
                    Background = selected ? LumenTheme.Brush("ImageAccent") : ImageBrush(82), CornerRadius = new CornerRadius(2) };
                Canvas.SetLeft(marker, canvas.ActualWidth * interval.StartFraction);
                Canvas.SetTop(marker, 8);
                canvas.Children.Add(marker);
            }
            canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, canvas.ActualWidth, canvas.Height) };
        }
        canvas.SizeChanged += (_, _) => ArrangeCues();
        canvas.Loaded += (_, _) => ArrangeCues();
        var summary = intervals.Count == 0 ? LumenText.Get("Subtitle cues fall outside the media duration.")
            : document.IsSampled ? Format("Showing {0} of {1} real subtitle cues", document.Cues.Count, document.CueCount)
                : Format("{0} real subtitle cues", document.CueCount);
        var button = LumenUi.Button(string.Empty, onImage: true, height: 24);
        button.Content = canvas;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(6);
        button.BorderThickness = new Thickness(0);
        button.Background = ImageBrush(12);
        LumenUi.SetResource(button.Resources, "ButtonBackground", button.Background);
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", ImageBrush(20));
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPressed", ImageBrush(30));
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTipService.SetToolTip(button, MediaJoin(summary, LumenText.Get("Open subtitle cue intervals")));
        AutomationProperties.SetName(button, MediaJoin(StreamTitle(stream), summary, LumenText.Get("Open subtitle cue intervals")));
        AutomationProperties.SetHelpText(button, LumenText.Get("Cue positions come from the downloaded subtitle file."));
        button.Flyout = CreateSubtitleCueFlyout(stream, document, summary);
        return button;
    }

    private Flyout CreateSubtitleCueFlyout(MediaStream stream, SubtitleTimelineDocument document, string summary)
    {
        var content = new StackPanel { Spacing = 10, MinWidth = 240, MaxWidth = 480 };
        content.Children.Add(ImageText(StreamTitle(stream), 15, weight: true));
        var description = ImageText(summary, 12, opacity: .85);
        description.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(description);
        if (document.IsSampled) content.Children.Add(ImageText(LumenText.Get("Only a sample of cues is shown."), 12, opacity: .85));
        var cues = new ListView { MaxHeight = 320, SelectionMode = ListViewSelectionMode.Single,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsTabStop = true };
        AutomationProperties.SetName(cues, LumenText.Get("Subtitle cue intervals"));
        foreach (var cue in document.Cues)
        {
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(MediaMono(SubtitleCueTime(cue.StartTicks) + " \u2013 " + SubtitleCueTime(cue.EndTicks), 12, .95));
            var preview = ImageText(cue.Preview, 13, opacity: .95);
            preview.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(preview);
            var item = new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(item, SubtitleCueTime(cue.StartTicks) + " \u2013 " + SubtitleCueTime(cue.EndTicks) + ". " + cue.Preview);
            cues.Items.Add(item);
        }
        content.Children.Add(cues);
        var popup = new Flyout { Content = content, Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
            FlyoutPresenterStyle = PopupStyle(440) };
        popup.Opening += (_, _) => { _hasOpenDetailFlyout = true; UpdateStageInteraction(); };
        popup.Opened += (_, _) => cues.Focus(FocusState.Programmatic);
        popup.Closed += (_, _) => { _hasOpenDetailFlyout = false; UpdateStageInteraction(); };
        return popup;
    }

    private static string SubtitleCueTime(long ticks) => (ticks / TimeSpan.TicksPerHour).ToString("00", CultureInfo.InvariantCulture)
        + ":" + (ticks / TimeSpan.TicksPerMinute % 60).ToString("00", CultureInfo.InvariantCulture)
        + ":" + (ticks / TimeSpan.TicksPerSecond % 60).ToString("00", CultureInfo.InvariantCulture)
        + "." + (ticks / TimeSpan.TicksPerMillisecond % 1000).ToString("000", CultureInfo.InvariantCulture);

    private static void RegisterSubtitleTimelineText()
    {
        if (_subtitleTimelineTextRegistered) return;
        _subtitleTimelineTextRegistered = true;
        LumenText.Register(new Dictionary<string, string>
        {
            ["Loading subtitle cues..."] = "\u6b63\u5728\u52a0\u8f7d\u5b57\u5e55\u65f6\u95f4\u8f74...",
            ["Retry subtitle cues"] = "\u91cd\u8bd5\u52a0\u8f7d\u5b57\u5e55\u65f6\u95f4\u8f74",
            ["Subtitle cues could not be loaded."] = "\u5b57\u5e55\u65f6\u95f4\u8f74\u52a0\u8f7d\u5931\u8d25\u3002",
            ["The subtitle data could not be read. Try again or choose another track."] = "\u65e0\u6cd5\u8bfb\u53d6\u5b57\u5e55\u6570\u636e\u3002\u8bf7\u91cd\u8bd5\u6216\u9009\u62e9\u5176\u4ed6\u8f68\u9053\u3002",
            ["Text cue timing is unavailable for this subtitle format."] = "\u8be5\u5b57\u5e55\u683c\u5f0f\u65e0\u53ef\u7528\u7684\u6587\u672c\u65f6\u95f4\u8f74\u3002",
            ["Subtitle cue positions need a known media duration."] = "\u83b7\u53d6\u5a92\u4f53\u65f6\u957f\u540e\u624d\u80fd\u663e\u793a\u5b57\u5e55\u65f6\u95f4\u8f74\u3002",
            ["The subtitle media source is unavailable."] = "\u5b57\u5e55\u6240\u5c5e\u7684\u5a92\u4f53\u6e90\u6682\u65f6\u4e0d\u53ef\u7528\u3002",
            ["Subtitle timelines are unavailable for live media."] = "\u76f4\u64ad\u5a92\u4f53\u6682\u4e0d\u652f\u6301\u5b57\u5e55\u65f6\u95f4\u8f74\u3002",
            ["The server did not provide downloadable text subtitles."] = "\u670d\u52a1\u5668\u672a\u63d0\u4f9b\u53ef\u4e0b\u8f7d\u7684\u6587\u672c\u5b57\u5e55\u3002",
            ["Subtitle timelines are unavailable for external download hosts."] = "\u5916\u90e8\u4e0b\u8f7d\u4e3b\u673a\u7684\u5b57\u5e55\u6682\u4e0d\u652f\u6301\u65f6\u95f4\u8f74\u3002",
            ["The subtitle time reference could not be confirmed."] = "\u65e0\u6cd5\u786e\u8ba4\u5b57\u5e55\u4e0e\u5a92\u4f53\u7684\u65f6\u95f4\u5bf9\u9f50\u3002",
            ["The server did not provide SRT or WebVTT subtitles."] = "\u670d\u52a1\u5668\u672a\u63d0\u4f9b SRT \u6216 WebVTT \u5b57\u5e55\u3002",
            ["Showing {0} of {1} real subtitle cues"] = "\u663e\u793a {0} / {1} \u6761\u5b9e\u9645\u5b57\u5e55",
            ["{0} real subtitle cues"] = "{0} \u6761\u5b9e\u9645\u5b57\u5e55",
            ["Subtitle cues fall outside the media duration."] = "\u5b57\u5e55\u65f6\u95f4\u8d85\u51fa\u5a92\u4f53\u65f6\u957f\u3002",
            ["Open subtitle cue intervals"] = "\u67e5\u770b\u5b57\u5e55\u8d77\u6b62\u65f6\u95f4",
            ["Subtitle cue intervals"] = "\u5b57\u5e55\u8d77\u6b62\u65f6\u95f4",
            ["Cue positions come from the downloaded subtitle file."] = "\u5b57\u5e55\u4f4d\u7f6e\u6765\u81ea\u5b9e\u9645\u4e0b\u8f7d\u7684\u5b57\u5e55\u6587\u4ef6\u3002",
            ["Only a sample of cues is shown."] = "\u5f53\u524d\u663e\u793a\u90e8\u5206\u5b57\u5e55\u6837\u672c\u3002"
        });
    }

    private sealed record SubtitleTimelineContext(string DetailId, int Version, string ItemId, string? SourceId,
        string? SourcePath, long Duration, int? SelectedSubtitle, EmbyApiClient Api);

    private sealed class SubtitleTimelineTrackState(SubtitleTimelineContext context, MediaSourceInfo source, MediaStream stream)
    {
        public SubtitleTimelineContext Context { get; } = context;
        public MediaSourceInfo Source { get; } = source;
        public MediaStream Stream { get; } = stream;
        public bool Loading { get; set; }
        public SubtitleTimelineResult? Result { get; set; }
        public string? Failure { get; set; }
        public CancellationTokenSource? Request { get; set; }
        public event Action? Changed;
        public void NotifyChanged() => Changed?.Invoke();
    }
}
