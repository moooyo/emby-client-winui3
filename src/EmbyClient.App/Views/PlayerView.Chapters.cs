using System.Diagnostics;
using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private const int ChapterPreviewCacheEntries = 16;
    private const long ChapterPreviewCacheBytes = 8L * 1024 * 1024;
    private readonly Dictionary<ChapterImageKey, LinkedListNode<ChapterImageEntry>> _chapterImageCache = [];
    private readonly LinkedList<ChapterImageEntry> _chapterImageRecency = [];
    private readonly DispatcherTimer _nextChapterTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _chapterPreviewCancellation;
    private ChapterHoverOwner? _chapterHoverOwner;
    private ChapterCommandOwner? _introSkipRequest;
    private ChapterNextPrompt? _chapterNextPrompt;
    private long _chapterImageBytes;
    private long? _introSkipHandledIntent;
    private long? _chapterNextSuppressedIntent;
    private Guid? _drawnChapterPlayback;
    private ChapterInfo[]? _drawnChapters;
    private long _drawnChapterDuration;
    private double _drawnChapterWidth;
    private readonly TranslateTransform _chapterPreviewLift = new();

    private void InitializeChapters()
    {
        ChapterPreview.RenderTransform = _chapterPreviewLift;
        ChapterPreview.SizeChanged += (_, _) => UpdateChapterPreviewClearance();
        SubtitleOverlay.SizeChanged += (_, _) => UpdateChapterPreviewClearance();
        _nextChapterTimer.Tick += NextChapterTimerTick;
        TimelineHost.SizeChanged += (_, _) => RebuildChapterTicks();
        Loaded += (_, _) => RefreshChapterControls();
        Unloaded += (_, _) =>
        {
            HideChapterPreview();
            _nextChapterTimer.Stop();
            if (_chapterNextPrompt is { } prompt) prompt.LastTimestamp = 0;
            SkipIntroButton.Visibility = NextEpisodeCountdown.Visibility = Visibility.Collapsed;
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => RefreshChapterControls());
        AutomationProperties.SetLiveSetting(NextCountdownText,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        ResetChapters();
    }

    private void ResetChapters()
    {
        HideChapterPreview();
        _chapterImageCache.Clear();
        _chapterImageRecency.Clear();
        _chapterImageBytes = 0;
        _introSkipRequest = null;
        _introSkipHandledIntent = null;
        _chapterNextSuppressedIntent = null;
        ClearChapterNextPrompt();
        ChapterTicks.Children.Clear();
        _drawnChapterPlayback = null;
        _drawnChapters = null;
        _drawnChapterDuration = 0;
        _drawnChapterWidth = 0;
        SkipIntroButton.Visibility = Visibility.Collapsed;
    }

    private void RebuildChapterTicks()
    {
        if (ChapterTicks is null) return;
        ChapterTicks.Children.Clear();
        var context = _coordinator?.ActiveContext;
        if (context?.Selection.ItemId != _item?.Id || _preparationIntent == _playIntent) context = null;
        var duration = context?.Source.RunTimeTicks.GetValueOrDefault() ?? 0;
        var width = ChapterTrackWidth();
        _drawnChapterPlayback = context?.PlaybackId;
        _drawnChapterDuration = duration;
        _drawnChapterWidth = width;
        _drawnChapters = context is null ? null : PlaybackChapters(context);
        if (_drawnChapters is null || duration <= 0 || width < 3) return;
        foreach (var chapter in _drawnChapters.Where(chapter => ValidChapterStart(chapter, duration))
            .DistinctBy(chapter => chapter.StartPositionTicks))
        {
            var tick = new Rectangle
            {
                Width = 3, Height = 4, IsHitTestVisible = false,
                Fill = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0))
            };
            Canvas.SetLeft(tick, Math.Clamp(chapter.StartPositionTicks / (double)duration * width - 1.5, 0, width - 3));
            Canvas.SetTop(tick, 0);
            ChapterTicks.Children.Add(tick);
        }
    }

    private double ChapterTrackWidth() => ChapterTicks.ActualWidth > 0 ? ChapterTicks.ActualWidth
        : Math.Max(0, TimelineHost.ActualWidth - 16);

    private ChapterInfo[] PlaybackChapters(PlaybackContext context) =>
        context.Source.Chapters ?? _item?.Chapters ?? Array.Empty<ChapterInfo>();

    private static bool ValidChapterStart(ChapterInfo chapter, long duration) =>
        chapter.StartPositionTicks >= 0 && chapter.StartPositionTicks < duration;

    private async void TimelinePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        var session = _session;
        var item = _item;
        var coordinator = _coordinator;
        var context = coordinator?.ActiveContext;
        var duration = context?.Source.RunTimeTicks.GetValueOrDefault() ?? 0;
        var width = ChapterTrackWidth();
        if (!IsLoaded || Visibility != Visibility.Visible || !Timeline.IsEnabled || session is null
            || _compactOverlay || item?.Id is null || context is null || context.Selection.ItemId != item.Id || duration <= 0 || width <= 0)
        { HideChapterPreview(); return; }
        var point = args.GetCurrentPoint(ChapterTicks).Position;
        var position = Math.Min(duration - 1, (long)(duration * Math.Clamp(point.X / width, 0, 1)));
        var candidate = PlaybackChapters(context).Select((chapter, index) => (Chapter: chapter, Index: index))
            .Where(entry => ValidChapterStart(entry.Chapter, duration) && entry.Chapter.StartPositionTicks <= position
                && (!IsChapterBoundaryMarker(entry.Chapter) || !string.IsNullOrWhiteSpace(entry.Chapter.ImageTag)))
            .OrderByDescending(entry => entry.Chapter.StartPositionTicks).FirstOrDefault();
        if (candidate.Chapter is null || string.IsNullOrWhiteSpace(candidate.Chapter.ImageTag)
            || (candidate.Chapter.ChapterIndex ?? candidate.Index) < 0)
        { HideChapterPreview(); return; }
        ChapterPreviewText.Text = string.Join(LumenText.Get(" \u00b7 "),
            new[] { FormatTime(position), candidate.Chapter.Name }.Where(value => !string.IsNullOrWhiteSpace(value)));
        PositionChapterPreview(point.X);
        var key = new ChapterImageKey(session.AccountKey, session.Api.ApiRoot.AbsoluteUri, item.Id,
            candidate.Chapter.ChapterIndex ?? candidate.Index, candidate.Chapter.ImageTag);
        if (_chapterHoverOwner is { } current && current.Key == key && OwnsChapterHover(current)) return;
        HideChapterPreview(clearCaption: false);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_playRequest?.Token ?? CancellationToken.None);
        _chapterPreviewCancellation = cancellation;
        var owner = new ChapterHoverOwner(session, coordinator!, item, _playIntent, context.PlaybackId, key, cancellation.Token);
        _chapterHoverOwner = owner;
        try
        {
            var bytes = GetCachedChapterImage(key)
                ?? await session.Api.GetItemImageAsync(item.Id, "Chapter", key.Index,
                    new ImageOptions { Tag = key.Tag, MaxWidth = 416, MaxHeight = 234 }, owner.Token);
            if (!OwnsChapterHover(owner) || bytes.Length == 0) return;
            CacheChapterImage(key, bytes);
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 416, 234, owner.Token, bitmap =>
            {
                if (!OwnsChapterHover(owner)) return;
                ChapterPreviewImage.Source = bitmap;
                ChapterPreview.Visibility = Visibility.Visible;
                UpdateChapterPreviewClearance();
            });
        }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure
            && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (OwnsChapterHover(owner)) ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A missing chapter image never substitutes artwork or changes playback state.
        }
    }

    private void PositionChapterPreview(double trackX)
    {
        var point = ChapterTicks.TransformToVisual(ControlDock).TransformPoint(new Point(trackX, 0));
        var available = Math.Max(0, ControlDock.ActualWidth - ControlDock.Padding.Left - ControlDock.Padding.Right);
        var left = Math.Clamp(point.X - ControlDock.Padding.Left - ChapterPreview.Width / 2, 0,
            Math.Max(0, available - ChapterPreview.Width));
        ChapterPreview.Margin = new Thickness(left, ChapterPreview.Margin.Top, 0, ChapterPreview.Margin.Bottom);
        UpdateChapterPreviewClearance();
    }

    private void UpdateChapterPreviewClearance()
    {
        var lift = 0d;
        var opacity = 1d;
        if (IsLoaded && !_compactOverlay && ChapterPreview.Visibility == Visibility.Visible
            && ChapterPreview.ActualHeight > 0 && SubtitleOverlay.Visibility == Visibility.Visible
            && _subtitleText.Length > 0 && SubtitleOverlay.ActualHeight > 0)
        {
            // A render-only lift leaves the fixed dock's measurement slot and the preview's size unchanged.
            var naturalBottom = PlayerRoot.ActualHeight - ControlDock.Padding.Bottom - ChapterPreview.Margin.Bottom;
            var subtitleTop = PlayerRoot.ActualHeight - SubtitleOverlay.Margin.Bottom - SubtitleOverlay.ActualHeight;
            lift = Math.Min(0, subtitleTop - 12 - naturalBottom);
            var headerBottom = AreControlsVisible && PlayerHeader.Visibility == Visibility.Visible ? 64d : 0d;
            var previewTop = naturalBottom - ChapterPreview.ActualHeight + lift;
            // Keep the hover owner and image ready when a very small viewport has no room above the caption.
            if (previewTop < headerBottom + 12) opacity = 0;
        }
        if (IsLoaded && UsesNarrowCaptionBand && ChapterPreview.Visibility == Visibility.Visible
            && ChapterPreview.ActualHeight > 0 && SidePanel.Visibility == Visibility.Visible && SidePanel.ActualHeight > 0)
        {
            var previewLeft = ControlDock.Padding.Left + ChapterPreview.Margin.Left;
            var panelLeft = PlayerRoot.ActualWidth - SidePanel.Margin.Right - SidePanel.ActualWidth;
            var naturalBottom = PlayerRoot.ActualHeight - ControlDock.Padding.Bottom - ChapterPreview.Margin.Bottom;
            var panelBottom = PlayerRoot.ActualHeight - SidePanel.Margin.Bottom;
            var panelTop = panelBottom - SidePanel.ActualHeight;
            var previewBottom = naturalBottom + lift;
            if (previewLeft + ChapterPreview.ActualWidth > panelLeft
                && previewLeft < PlayerRoot.ActualWidth - SidePanel.Margin.Right
                && previewBottom > panelTop && previewBottom - ChapterPreview.ActualHeight < panelBottom)
            {
                lift = Math.Min(lift, panelTop - 12 - naturalBottom);
                var headerBottom = AreControlsVisible && PlayerHeader.Visibility == Visibility.Visible ? 64d : 0d;
                if (naturalBottom + lift - ChapterPreview.ActualHeight < headerBottom + 12) opacity = 0;
            }
        }
        if (Math.Abs(_chapterPreviewLift.Y - lift) > .1) _chapterPreviewLift.Y = lift;
        if (Math.Abs(ChapterPreview.Opacity - opacity) > .01) ChapterPreview.Opacity = opacity;
    }

    private void TimelinePointerExited(object sender, PointerRoutedEventArgs args) => HideChapterPreview();

    private void HideChapterPreview(bool clearCaption = true)
    {
        _chapterHoverOwner = null;
        var cancellation = _chapterPreviewCancellation;
        _chapterPreviewCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        ChapterPreview.Visibility = Visibility.Collapsed;
        ChapterPreviewImage.Source = null;
        if (clearCaption) ChapterPreviewText.Text = string.Empty;
        UpdateChapterPreviewClearance();
    }

    private bool OwnsChapterHover(ChapterHoverOwner owner) => ReferenceEquals(owner, _chapterHoverOwner)
        && !owner.Token.IsCancellationRequested && owner.Intent == _playIntent
        && ReferenceEquals(owner.Session, _session) && ReferenceEquals(owner.Coordinator, _coordinator)
        && ReferenceEquals(owner.Item, _item) && _coordinator?.ActiveContext?.PlaybackId == owner.PlaybackId
        && IsLoaded && Visibility == Visibility.Visible && Timeline.IsEnabled && !_compactOverlay;

    private byte[]? GetCachedChapterImage(ChapterImageKey key)
    {
        if (!_chapterImageCache.TryGetValue(key, out var entry)) return null;
        _chapterImageRecency.Remove(entry);
        _chapterImageRecency.AddFirst(entry);
        return entry.Value.Bytes;
    }

    private void CacheChapterImage(ChapterImageKey key, byte[] bytes)
    {
        if (_chapterImageCache.ContainsKey(key) || bytes.LongLength > ChapterPreviewCacheBytes) return;
        while (_chapterImageCache.Count >= ChapterPreviewCacheEntries || _chapterImageBytes + bytes.LongLength > ChapterPreviewCacheBytes)
        {
            var oldest = _chapterImageRecency.Last!;
            _chapterImageCache.Remove(oldest.Value.Key);
            _chapterImageBytes -= oldest.Value.Bytes.LongLength;
            _chapterImageRecency.RemoveLast();
        }
        var entry = _chapterImageRecency.AddFirst(new ChapterImageEntry(key, bytes));
        _chapterImageCache.Add(key, entry);
        _chapterImageBytes += bytes.LongLength;
    }

    private void RefreshChapterControls()
    {
        if (SkipIntroButton is null) return;
        if (_coordinator?.ActiveContext is { } context) UpdateChapterPosition(context);
        else
        {
            HideChapterPreview();
            SkipIntroButton.Visibility = Visibility.Collapsed;
            RefreshChapterNextPrompt();
        }
    }

    private void UpdateChapterPosition(PlaybackContext context)
    {
        var chapters = PlaybackChapters(context);
        var duration = context.Source.RunTimeTicks.GetValueOrDefault();
        if (_drawnChapterPlayback != context.PlaybackId || _drawnChapterDuration != duration
            || !ReferenceEquals(chapters, _drawnChapters) || Math.Abs(ChapterTrackWidth() - _drawnChapterWidth) > 0.5)
            RebuildChapterTicks();
        if (_chapterHoverOwner is { } hover && !OwnsChapterHover(hover)) HideChapterPreview();
        SkipIntroText.Text = LumenText.Get("Skip intro");
        SetControlLabel(SkipIntroButton, SkipIntroText.Text);
        if (_session is null || _item?.Id != context.Selection.ItemId || _preparationIntent == _playIntent || duration <= 0)
        {
            SkipIntroButton.Visibility = Visibility.Collapsed;
            ClearChapterNextPrompt();
            return;
        }
        var markers = ReadChapterMarkers(chapters, duration);
        var inIntro = markers.IntroStart is { } start && markers.IntroEnd is { } end
            && context.PositionTicks >= start && context.PositionTicks < end;
        var canSeek = CanUseChapterCommand(context);
        if (_preferences.IntroSkipMode == "Automatic" && inIntro && canSeek
            && _coordinator?.Status == PlaybackStatus.Playing && _introSkipHandledIntent != _playIntent
            && _introSkipRequest is null && !_timelineDrag.IsActive && _keyboardTimelinePlaybackId is null)
            _ = SkipChapterIntroAsync(context, automatic: true);
        UpdateCreditsPrompt(context, markers.CreditsStart);
        SkipIntroButton.IsEnabled = canSeek && _introSkipRequest is null;
        SkipIntroButton.Visibility = !_compactOverlay && _preferences.IntroSkipMode == "ShowButton" && inIntro && canSeek
            && CanShowChapterPrompt() && NextEpisodeCountdown.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ChapterMarkers ReadChapterMarkers(ChapterInfo[] chapters, long duration)
    {
        var valid = chapters.Where(chapter => ValidChapterStart(chapter, duration)).ToArray();
        var start = valid.Where(chapter => string.Equals(chapter.MarkerType, "IntroStart", StringComparison.OrdinalIgnoreCase))
            .OrderBy(chapter => chapter.StartPositionTicks).FirstOrDefault();
        var end = start is null ? null : valid
            .Where(chapter => string.Equals(chapter.MarkerType, "IntroEnd", StringComparison.OrdinalIgnoreCase)
                && chapter.StartPositionTicks > start.StartPositionTicks)
            .OrderBy(chapter => chapter.StartPositionTicks).FirstOrDefault();
        var credits = valid.Where(chapter => string.Equals(chapter.MarkerType, "CreditsStart", StringComparison.OrdinalIgnoreCase))
            .OrderBy(chapter => chapter.StartPositionTicks).FirstOrDefault();
        return new ChapterMarkers(start?.StartPositionTicks, end?.StartPositionTicks, credits?.StartPositionTicks);
    }

    private static bool IsChapterBoundaryMarker(ChapterInfo chapter) =>
        string.Equals(chapter.MarkerType, "IntroStart", StringComparison.OrdinalIgnoreCase)
        || string.Equals(chapter.MarkerType, "IntroEnd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(chapter.MarkerType, "CreditsStart", StringComparison.OrdinalIgnoreCase);

    private bool CanShowChapterPrompt() => IsLoaded && Visibility == Visibility.Visible
        && _sidePanelMode == PlayerSidePanel.None && !IsModalOpen;

    private bool CanUseChapterCommand(PlaybackContext context) => _session is not null && !_advancing
        && !_retryInProgress && !IsModalOpen && _preparationIntent != _playIntent && context.CanSeek
        && context.Selection.ItemId == _item?.Id
        && _coordinator?.ActiveContext?.PlaybackId == context.PlaybackId
        && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;

    private async void SkipIntroClicked(object sender, RoutedEventArgs args)
    {
        if (_preferences.IntroSkipMode == "Off" || _coordinator?.ActiveContext is not { } context) return;
        await SkipChapterIntroAsync(context, automatic: false);
    }

    private async Task SkipChapterIntroAsync(PlaybackContext context, bool automatic)
    {
        var session = _session;
        var coordinator = _coordinator;
        var item = _item;
        if (session is null || coordinator is null || item is null || !CanUseChapterCommand(context)
            || _introSkipRequest is not null || automatic && _introSkipHandledIntent == _playIntent) return;
        var markers = ReadChapterMarkers(PlaybackChapters(context), context.Source.RunTimeTicks.GetValueOrDefault());
        if (markers.IntroStart is not { } start || markers.IntroEnd is not { } end
            || context.PositionTicks < start || context.PositionTicks >= end) return;
        var owner = new ChapterCommandOwner(session, coordinator, item, _playIntent, context.PlaybackId,
            _playRequest?.Token ?? CancellationToken.None);
        _introSkipRequest = owner;
        _introSkipHandledIntent = owner.Intent;
        SkipIntroButton.IsEnabled = false;
        try
        {
            await RunAsync(async () =>
            {
                if (!OwnsChapterCommand(owner) || coordinator.ActiveContext is not { } current
                    || !CanUseChapterCommand(current) || current.PositionTicks < start || current.PositionTicks >= end
                    || automatic && (_preferences.IntroSkipMode != "Automatic" || coordinator.Status != PlaybackStatus.Playing)) return;
                try { await coordinator.SeekAsync(owner.PlaybackId, end, owner.Token); }
                catch (Exception) when (!OwnsChapterCommand(owner)) { }
            });
        }
        finally
        {
            if (ReferenceEquals(owner, _introSkipRequest))
            {
                _introSkipRequest = null;
                RefreshChapterControls();
            }
        }
    }

    private bool OwnsChapterCommand(ChapterCommandOwner owner) => ReferenceEquals(owner, _introSkipRequest)
        && !owner.Token.IsCancellationRequested && owner.Intent == _playIntent
        && ReferenceEquals(owner.Session, _session) && ReferenceEquals(owner.Coordinator, _coordinator)
        && ReferenceEquals(owner.Item, _item) && _coordinator?.ActiveContext?.PlaybackId == owner.PlaybackId;

    private BaseItemDto? ChapterNextTarget()
    {
        var target = _queue.Next?.Item
            ?? (_episodeNeighborsItemId == _item?.Id ? _episodeNeighbors?.Next : null);
        return string.IsNullOrWhiteSpace(target?.Id) ? null : target;
    }

    private void UpdateCreditsPrompt(PlaybackContext context, long? creditsStart)
    {
        if (_chapterNextPrompt is { } current && !OwnsChapterNextPrompt(current)) ClearChapterNextPrompt();
        if (_coordinator?.Status == PlaybackStatus.Ended)
        { RefreshChapterNextPrompt(); return; }
        if (creditsStart is null || context.PositionTicks < creditsStart.Value
            || _chapterNextSuppressedIntent == _playIntent || ChapterNextTarget() is null)
        { ClearChapterNextPrompt(); return; }
        if (_chapterNextPrompt is null && _session is { } session && _coordinator is { } coordinator && _item is { } item
            && _notificationOwner.Capture(coordinator, context.PlaybackId, context.PlaybackId) is { } ticket)
            _chapterNextPrompt = new ChapterNextPrompt(session, coordinator, item, _playIntent, ticket);
        RefreshChapterNextPrompt();
    }

    private bool TryScheduleEndedCountdown(PlaybackNotificationOwner.Ticket ticket)
    {
        if (ticket.Intent != _playIntent || !ticket.PlaybackId.HasValue || !_notificationOwner.IsCurrent(ticket)
            || _session is not { } session || _coordinator is not { } coordinator || _item is not { } item) return true;
        HideChapterPreview();
        SkipIntroButton.Visibility = Visibility.Collapsed;
        if (_chapterNextSuppressedIntent == _playIntent || _advancing || !_preferences.AutoPlayNext)
        { ClearChapterNextPrompt(); return true; }
        if (_preferences.NextEpisodeCountdownSeconds <= 0 || ChapterNextTarget() is null)
        { ClearChapterNextPrompt(); return false; }
        var prompt = _chapterNextPrompt;
        if (prompt is null || !OwnsChapterNextPrompt(prompt))
            _chapterNextPrompt = prompt = new ChapterNextPrompt(session, coordinator, item, _playIntent, ticket);
        // Ended retires ActiveContext, but its notification ticket still owns the deferred advance.
        prompt.Ticket = ticket;
        prompt.Completion = ticket;
        RefreshChapterNextPrompt();
        return true;
    }

    private bool OwnsChapterNextPrompt(ChapterNextPrompt prompt) => ReferenceEquals(prompt, _chapterNextPrompt)
        && prompt.Intent == _playIntent && ReferenceEquals(prompt.Session, _session)
        && ReferenceEquals(prompt.Coordinator, _coordinator) && ReferenceEquals(prompt.Item, _item)
        && _notificationOwner.IsCurrent(prompt.Ticket)
        && (_coordinator?.Status is PlaybackStatus.Stopping or PlaybackStatus.Ended
            || _coordinator?.ActiveContext?.PlaybackId == prompt.Ticket.PlaybackId);

    private bool ChapterNextAutomatic(ChapterNextPrompt prompt) => _preferences.AutoPlayNext
        && (prompt.Completion.HasValue || _preferences.IntroSkipMode == "Automatic");

    private void RefreshChapterNextPrompt()
    {
        var prompt = _chapterNextPrompt;
        if (prompt is null) { _nextChapterTimer.Stop(); NextEpisodeCountdown.Visibility = Visibility.Collapsed; return; }
        var target = ChapterNextTarget();
        if (!OwnsChapterNextPrompt(prompt) || target is null || _chapterNextSuppressedIntent == prompt.Intent)
        { ClearChapterNextPrompt(); return; }
        if (ChapterNextAutomatic(prompt))
        {
            var seconds = Math.Clamp(_preferences.NextEpisodeCountdownSeconds, 0, 15);
            if (!prompt.RemainingSeconds.HasValue || prompt.ConfiguredSeconds != seconds)
            {
                prompt.RemainingSeconds = seconds;
                prompt.ConfiguredSeconds = seconds;
                prompt.LastTimestamp = Stopwatch.GetTimestamp();
            }
        }
        else
        {
            prompt.RemainingSeconds = null;
            prompt.ConfiguredSeconds = -1;
            prompt.LastTimestamp = 0;
        }
        RenderChapterNextPrompt(prompt, target);
        var shouldRun = prompt.RemainingSeconds.HasValue && !prompt.Advancing && IsLoaded
            && Visibility == Visibility.Visible && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Ended;
        if (shouldRun)
        {
            if (!_nextChapterTimer.IsEnabled)
            {
                prompt.LastTimestamp = Stopwatch.GetTimestamp();
                _nextChapterTimer.Start();
            }
            if (prompt.RemainingSeconds <= 0 && CanConsumeChapterCountdown())
                _ = AdvanceChapterNextAsync(prompt, automatic: true);
        }
        else
        {
            _nextChapterTimer.Stop();
            prompt.LastTimestamp = 0;
        }
    }

    private void RenderChapterNextPrompt(ChapterNextPrompt prompt, BaseItemDto target)
    {
        var title = PlaybackTitle(target, target.Name);
        var episode = string.Equals(target.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        NextCountdownText.Text = prompt.RemainingSeconds is { } remaining
            ? string.Format(LumenText.Get(episode ? "Next episode in {0} seconds: {1}" : "Up next in {0} seconds: {1}"),
                (int)Math.Ceiling(remaining), title)
            : string.Format(LumenText.Get(episode ? "Next episode: {0}" : "Up next: {0}"), title);
        NextEpisodeCountdown.Visibility = CanShowChapterPrompt()
            && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(NextCountdownText, NextCountdownText.Text);
        UpdateSubtitleOverlayLayout();
    }

    private bool CanConsumeChapterCountdown() => CanShowChapterPrompt() && !_advancing && !_retryInProgress
        && _preparationIntent != _playIntent && !_timelineDrag.IsActive && _keyboardTimelinePlaybackId is null
        && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Ended;

    private async void NextChapterTimerTick(object? sender, object args)
    {
        var prompt = _chapterNextPrompt;
        RefreshChapterNextPrompt();
        if (prompt is null || !ReferenceEquals(prompt, _chapterNextPrompt) || !OwnsChapterNextPrompt(prompt)
            || prompt.RemainingSeconds is not { } remaining || prompt.Advancing) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = prompt.LastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(prompt.LastTimestamp, now).TotalSeconds;
        prompt.LastTimestamp = now;
        if (!CanConsumeChapterCountdown()) return;
        prompt.RemainingSeconds = Math.Max(0, remaining - elapsed);
        if (ChapterNextTarget() is { } target) RenderChapterNextPrompt(prompt, target);
        if (prompt.RemainingSeconds <= 0) await AdvanceChapterNextAsync(prompt, automatic: true);
    }

    private async void CountdownPlayNextClicked(object sender, RoutedEventArgs args)
    {
        if (_chapterNextPrompt is { } prompt && CanShowChapterPrompt())
            await AdvanceChapterNextAsync(prompt, automatic: false);
    }

    private void CountdownCancelClicked(object sender, RoutedEventArgs args)
    {
        if (_chapterNextPrompt is not { } prompt || !OwnsChapterNextPrompt(prompt)) return;
        _chapterNextSuppressedIntent = prompt.Intent;
        ClearChapterNextPrompt();
    }

    private async Task AdvanceChapterNextAsync(ChapterNextPrompt prompt, bool automatic)
    {
        if (!OwnsChapterNextPrompt(prompt) || prompt.Advancing || _advancing || _retryInProgress || IsModalOpen
            || _preparationIntent == _playIntent || ChapterNextTarget() is null
            || _coordinator?.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended)
            || automatic && (!ChapterNextAutomatic(prompt) || !CanConsumeChapterCountdown())) return;
        prompt.Advancing = true;
        _chapterNextSuppressedIntent = prompt.Intent;
        ClearChapterNextPrompt();
        await RunAsync(() => AdvanceAsync(prompt.Completion ?? prompt.Ticket));
    }

    private void ClearChapterNextPrompt()
    {
        _chapterNextPrompt = null;
        _nextChapterTimer.Stop();
        NextEpisodeCountdown.Visibility = Visibility.Collapsed;
        NextCountdownText.Text = string.Empty;
        UpdateSubtitleOverlayLayout();
    }

    private sealed class ChapterNextPrompt(ConnectedSession session, PlaybackCoordinator coordinator, BaseItemDto item,
        long intent, PlaybackNotificationOwner.Ticket ticket)
    {
        public ConnectedSession Session { get; } = session;
        public PlaybackCoordinator Coordinator { get; } = coordinator;
        public BaseItemDto Item { get; } = item;
        public long Intent { get; } = intent;
        public PlaybackNotificationOwner.Ticket Ticket { get; set; } = ticket;
        public PlaybackNotificationOwner.Ticket? Completion { get; set; }
        public double? RemainingSeconds { get; set; }
        public int ConfiguredSeconds { get; set; } = -1;
        public long LastTimestamp { get; set; }
        public bool Advancing { get; set; }
    }

    private readonly record struct ChapterMarkers(long? IntroStart, long? IntroEnd, long? CreditsStart);
    private sealed record ChapterCommandOwner(ConnectedSession Session, PlaybackCoordinator Coordinator, BaseItemDto Item,
        long Intent, Guid PlaybackId, CancellationToken Token);
    private readonly record struct ChapterImageKey(string AccountKey, string ApiRoot, string ItemId, int Index, string Tag);
    private sealed record ChapterImageEntry(ChapterImageKey Key, byte[] Bytes);
    private sealed record ChapterHoverOwner(ConnectedSession Session, PlaybackCoordinator Coordinator, BaseItemDto Item,
        long Intent, Guid PlaybackId, ChapterImageKey Key, CancellationToken Token);
}
