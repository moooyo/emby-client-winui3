using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private readonly List<EpisodeCardVisual> _episodeCards = [];
    private MediaCardViewModel? _focusedEpisode;
    private MediaCardViewModel? _pendingEpisodeHover;
    private string? _episodeContextKey;
    private StackPanel? _episodeFocusInfo;
    private StackPanel? _episodeFocusBody;
    private TextBlock? _episodeFocusNumber;
    private TextBlock? _episodeFocusKicker;
    private TextBlock? _episodeFocusTitle;
    private TextBlock? _episodeFocusMetadata;
    private TextBlock? _episodeFocusOverview;
    private StackPanel? _episodeRow;
    private Grid? _episodeHeader;
    private Grid? _episodeStrip;
    private Grid? _episodeRowViewport;
    private Grid? _episodePreviousPad;
    private Grid? _episodeNextPad;
    private Button? _episodePreviousButton;
    private Button? _episodeNextButton;
    private Border? _episodeStripFade;
    private DispatcherTimer? _episodeHoverTimer;
    private DispatcherTimer? _episodeCollapseTimer;
    private DispatcherTimer? _episodeFocusTextTimer;
    private Storyboard? _episodeStripAnimation;
    private Flyout? _episodeContextFlyout;
    private bool _episodeStripExpanded;
    private bool _episodePointerWithin;
    private bool _episodeCenterPending;
    private int _episodeInitialSelectionRevision = -1;
    private int _episodeRestoredSelectionRevision = -1;
    private int _episodeUserSelectionRevision = -1;
    private string? _episodeInitialFocusId;

    private double EpisodeViewportWidth => ActualWidth > 0 ? ActualWidth : 1280;
    private double EpisodeViewportHeight => ActualHeight > 0 ? ActualHeight : 800;
    private double EpisodeInset => Math.Clamp(EpisodeViewportWidth * .04, 24, 56);
    private double EpisodeCardWidth => Math.Clamp(EpisodeViewportWidth * .14, 168, 250);

    private void RestoreEpisodeSelectionIntent()
    {
        _episodeRestoredSelectionRevision = _library.NavigationRevision;
        _episodeInitialFocusId = null;
    }

    private void RecordExplicitEpisodeSelection()
    {
        _episodeUserSelectionRevision = _library.NavigationRevision;
        _episodeInitialFocusId = null;
    }

    private void EnsureInitialEpisodeSelection()
    {
        var revision = _library.NavigationRevision;
        if (_deck.SelectedIndex != _episodePageIndex || _library.Detail.Item.Type != "Series"
            || _detailId is null || _detailCancellation is null || !_library.CanUseSeasonActions || _library.IsBusy
            || _episodeInitialSelectionRevision == revision || _episodeRestoredSelectionRevision == revision
            || _episodeUserSelectionRevision == revision) return;
        _episodeInitialSelectionRevision = revision;
        _ = SelectInitialEpisodeAsync(_detailId, _detailVersion, revision, _detailCancellation.Token);
    }

    private async Task SelectInitialEpisodeAsync(string detailId, int detailVersion, int navigationRevision, CancellationToken token)
    {
        bool CanApply() => IsCurrentDetail(detailId, detailVersion) && _library.NavigationRevision == navigationRevision
            && _episodeRestoredSelectionRevision != navigationRevision && _episodeUserSelectionRevision != navigationRevision;
        try
        {
            // The overview recommendation may settle after the initial season's episode list.
            var recommendation = await _library.WaitForPlaybackRecommendationAsync(detailId, navigationRevision, token);
            token.ThrowIfCancellationRequested();
            if (!CanApply() || !_library.CanUseSeasonActions) return;
            recommendation ??= _library.PlayableDetail;
            if (recommendation.Item.Type != "Episode" || string.IsNullOrWhiteSpace(recommendation.Id)
                || recommendation.Item.SeriesId is { Length: > 0 } seriesId && seriesId != detailId) return;
            var targetSeason = _library.Seasons.FirstOrDefault(season => season.Id == recommendation.Item.SeasonId)
                ?? (recommendation.Item.ParentIndexNumber is { } seasonNumber
                    ? _library.Seasons.FirstOrDefault(season => season.Item.IndexNumber == seasonNumber) : null);
            if (targetSeason is null && !_library.Items.Any(episode => episode.Id == recommendation.Id)) return;
            _episodeInitialFocusId = recommendation.Id;
            if (targetSeason is not null && targetSeason.Id != _library.SelectedSeason?.Id)
                await _library.SelectSeasonAsync(targetSeason, token);
            token.ThrowIfCancellationRequested();
            if (!CanApply() || _library.IsSeasonSwitchPending) return;
            if (!_library.Items.Any(episode => episode.Id == recommendation.Id))
            {
                _episodeInitialFocusId = null;
                return;
            }
            _episodeInitialFocusId = recommendation.Id;
            _episodeCenterPending = true;
            _forceRender = true;
            Refresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ExpectedRequestFailure(exception))
        {
            if (CanApply())
            {
                _episodeInitialFocusId = null;
                SetNotice(DescribeRequestFailure(exception), true);
            }
        }
    }

    private void RenderEpisodeScreen(MediaCardViewModel detail, MediaCardViewModel[] episodes, string? previousFocusId, double previousOffset)
    {
        var context = detail.Id + ":" + (_library.SelectedSeason?.Id ?? detail.Item.SeasonId ?? string.Empty);
        var changedSeason = !_library.IsSeasonSwitchPending && context != _episodeContextKey;
        if (!_library.IsSeasonSwitchPending) _episodeContextKey = context;
        var initialSelection = episodes.FirstOrDefault(episode => episode.Id == _episodeInitialFocusId);
        var selection = initialSelection ?? (!changedSeason ? episodes.FirstOrDefault(episode => episode.Id == previousFocusId) : null);
        selection ??= episodes.FirstOrDefault(episode => episode.CanResume && !episode.IsPlayed)
            ?? episodes.FirstOrDefault(episode => episode.Id == _library.PlayableDetail.Id)
            ?? episodes.FirstOrDefault(episode => episode.CanPlay && !episode.IsPlayed)
            ?? episodes.FirstOrDefault();
        _focusedEpisode = selection;
        var shouldCenter = initialSelection is not null || _episodeCenterPending || changedSeason
            || previousFocusId is null || !episodes.Any(episode => episode.Id == previousFocusId);
        if (initialSelection is not null) _episodeInitialFocusId = null;
        _episodeCenterPending = selection is not null && shouldCenter;

        _episodeFocusInfo = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Opacity = .66 };
        _episodeFocusBody = new StackPanel { MaxWidth = 780 };
        var identity = new Grid { ColumnSpacing = 18 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _episodeFocusNumber = ImageText(string.Empty, 72, opacity: .8);
        _episodeFocusNumber.FontFamily = LumenTheme.DisplayFont;
        _episodeFocusNumber.VerticalAlignment = VerticalAlignment.Bottom;
        _episodeFocusNumber.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        identity.Children.Add(_episodeFocusNumber);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _episodeFocusKicker = ImageText(string.Empty, 12, opacity: .65, weight: true);
        _episodeFocusKicker.CharacterSpacing = 100;
        _episodeFocusKicker.TextWrapping = TextWrapping.NoWrap;
        _episodeFocusKicker.TextTrimming = TextTrimming.CharacterEllipsis;
        _episodeFocusTitle = ImageText(string.Empty, 28, true);
        _episodeFocusTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        _episodeFocusTitle.Margin = new Thickness(0, 4, 0, 0);
        _episodeFocusTitle.MaxLines = 2;
        _episodeFocusTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _episodeFocusMetadata = ImageText(string.Empty, 13, opacity: .65);
        _episodeFocusMetadata.Margin = new Thickness(0, 4, 0, 0);
        titles.Children.Add(_episodeFocusKicker);
        titles.Children.Add(_episodeFocusTitle);
        titles.Children.Add(_episodeFocusMetadata);
        Grid.SetColumn(titles, 1);
        identity.Children.Add(titles);
        _episodeFocusBody.Children.Add(identity);
        _episodeFocusOverview = ImageText(string.Empty, 14, opacity: .72);
        _episodeFocusOverview.MaxWidth = 560;
        _episodeFocusOverview.HorizontalAlignment = HorizontalAlignment.Left;
        _episodeFocusOverview.LineHeight = 24.5;
        _episodeFocusOverview.MaxLines = 2;
        _episodeFocusOverview.TextTrimming = TextTrimming.CharacterEllipsis;
        _episodeFocusOverview.Margin = new Thickness(0, 14, 0, 0);
        _episodeFocusBody.Children.Add(_episodeFocusOverview);
        _episodeFocusInfo.Children.Add(_episodeFocusBody);
        _childrenSection.Children.Add(_episodeFocusInfo);

        _episodeStrip = new Grid { VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var strip = _episodeStrip;
        var stripBody = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        _episodeHeader = new Grid { ColumnSpacing = 18, Height = 36 };
        _episodeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _episodeHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var seasons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        if (detail.Item.Type != "Episode" && _library.Seasons.Count > 0)
            foreach (var season in _library.Seasons) seasons.Children.Add(CreateSeasonTab(season));
        else seasons.Children.Add(ImageText(LumenText.Get(detail.Item.Type == "Episode" ? "Next episode" : "Episodes"), 15, weight: true));
        var seasonScroll = HorizontalScroll(seasons);
        seasonScroll.VerticalAlignment = VerticalAlignment.Center;
        seasonScroll.MaxWidth = Math.Max(140, EpisodeViewportWidth - EpisodeInset * 2 - 100);
        _episodeHeader.Children.Add(seasonScroll);
        var count = ImageText(_library.IsSeasonSwitchPending ? LumenText.Get("Loading episodes...") : Format("{0} episodes", episodes.Length), 13, opacity: .5);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.TextWrapping = TextWrapping.NoWrap;
        count.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(count, 1);
        _episodeHeader.Children.Add(count);
        stripBody.Children.Add(_episodeHeader);

        _episodeRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var episode in episodes) _episodeRow.Children.Add(CreateEpisodeCard(episode));
        _episodeScroll = HorizontalScroll(_episodeRow);
        var scroller = _episodeScroll;
        scroller.ViewChanged += (_, _) => UpdateEpisodePaging();
        scroller.SizeChanged += (_, _) =>
        {
            if (!ReferenceEquals(_episodeScroll, scroller)) return;
            if (_episodeCenterPending) CenterCurrentEpisode();
            UpdateEpisodePaging();
        };
        scroller.Loaded += (_, _) =>
        {
            if (!ReferenceEquals(_episodeScroll, scroller)) return;
            if (shouldCenter) CenterCurrentEpisode();
            else scroller.ChangeView(Math.Min(previousOffset, scroller.ScrollableWidth), null, null, true);
            UpdateEpisodePaging();
        };
        _episodeRowViewport = new Grid();
        _episodeRowViewport.Children.Add(scroller);
        _episodePreviousPad = CreateEpisodePagingPad(-1, out var previous);
        _episodePreviousButton = previous;
        _episodeNextPad = CreateEpisodePagingPad(1, out var following);
        _episodeNextButton = following;
        _episodeRowViewport.Children.Add(_episodePreviousPad);
        _episodeRowViewport.Children.Add(_episodeNextPad);
        stripBody.Children.Add(_episodeRowViewport);
        strip.Children.Add(stripBody);
        _episodeStripFade = new Border
        {
            Height = 44, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Background = Gradient(new Point(0, 0), new Point(0, 1), (0, Color.FromArgb(0, 8, 8, 9)), (1, Color.FromArgb(235, 8, 8, 9)))
        };
        strip.Children.Add(_episodeStripFade);
        strip.PointerEntered += (_, _) => { _episodePointerWithin = true; SetEpisodeStripExpanded(true); };
        strip.PointerExited += (_, _) => { _episodePointerWithin = false; ScheduleEpisodeStripCollapse(); };
        strip.GotFocus += (_, _) => SetEpisodeStripExpanded(true);
        strip.LostFocus += (_, _) => ScheduleEpisodeStripCollapse();
        strip.SizeChanged += (_, _) => strip.Clip = new RectangleGeometry { Rect = new Rect(0, 0, strip.ActualWidth, strip.ActualHeight) };
        strip.Unloaded += (_, _) =>
        {
            if (!ReferenceEquals(_episodeStrip, strip)) return;
            _episodeHoverTimer?.Stop();
            _episodeCollapseTimer?.Stop();
        };
        _childrenSection.Children.Add(strip);
        UpdateFocusedEpisodeText();
        UpdateEpisodeCardSelection();
        if (selection is not null && _library.CanUseSeasonActions) OnFocusedEpisodeChanged(selection);
    }

    private Button CreateSeasonTab(MediaCardViewModel season)
    {
        var selected = season.Id == _library.SelectedSeason?.Id;
        var label = season.Item.IndexNumber == 0 ? LumenText.Get("Specials")
            : season.Item.IndexNumber is { } number ? Format("Season {0}", number) : season.Title;
        var button = EpisodePlainButton();
        button.Height = 36;
        button.MinWidth = 44;
        button.Tag = season.Id;
        button.IsEnabled = _library.CanSelectSeason && !_actionBusy;
        var body = new Grid { Height = 30 };
        var text = ImageText(label, 15, opacity: selected ? 1 : .55);
        text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextWrapping = TextWrapping.NoWrap;
        body.Children.Add(text);
        body.Children.Add(new Border
        {
            Height = 2, CornerRadius = new CornerRadius(2), Background = LumenTheme.Brush("ImageAccent"),
            VerticalAlignment = VerticalAlignment.Bottom, Visibility = selected ? Visibility.Visible : Visibility.Collapsed
        });
        button.Content = body;
        button.PointerEntered += (_, _) => text.Opacity = 1;
        button.PointerExited += (_, _) => text.Opacity = selected ? 1 : .55;
        AutomationProperties.SetName(button, label + (selected ? ", " + LumenText.Get("Selected") : string.Empty));
        button.Click += async (_, _) =>
        {
            RecordExplicitEpisodeSelection();
            var restoreFocus = button.FocusState == FocusState.Keyboard;
            var detailId = _library.Detail.Id;
            await _library.SelectSeasonAsync(season);
            if (!restoreFocus) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_library.Detail.Id != detailId || _episodeHeader?.Children.FirstOrDefault() is not ScrollViewer { Content: StackPanel tabs }) return;
                tabs.Children.OfType<Button>().FirstOrDefault(tab => tab.Tag as string == season.Id)?.Focus(FocusState.Keyboard);
            });
        };
        return button;
    }

    private FrameworkElement CreateEpisodeCard(MediaCardViewModel episode)
    {
        var root = new Grid { Width = EpisodeCardWidth, VerticalAlignment = VerticalAlignment.Top };
        var body = new StackPanel();
        var imageFrame = new Grid { Height = EpisodeCardWidth * 9 / 16, RenderTransform = new TranslateTransform() };
        var shadowHost = new Grid { IsHitTestVisible = false };
        imageFrame.Children.Add(shadowHost);
        var image = new Grid();
        imageFrame.Children.Add(image);
        var artwork = new LumenArtwork();
        artwork.Set(episode, _library, ArtworkKind.Landscape, 640);
        image.Children.Add(artwork);
        image.Children.Add(new Border
        {
            IsHitTestVisible = false,
            Background = Gradient(new Point(0, 0), new Point(0, 1), (0, Color.FromArgb(0, 0, 0, 0)), (.45, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(153, 0, 0, 0)))
        });
        var number = ImageText(EpisodeNumberText(episode, false), Math.Clamp(EpisodeViewportWidth * .026, 30, 42));
        number.FontFamily = LumenTheme.DisplayFont;
        number.LineHeight = number.FontSize;
        number.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        number.VerticalAlignment = VerticalAlignment.Bottom;
        number.HorizontalAlignment = HorizontalAlignment.Left;
        number.Margin = new Thickness(10, 0, 0, 6);
        number.IsHitTestVisible = false;
        image.Children.Add(number);
        var progress = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = ImageBrush(51), IsHitTestVisible = false };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, episode.Progress), GridUnitType.Star) });
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.001, 100 - episode.Progress), GridUnitType.Star) });
        progress.Children.Add(new Border { Background = LumenTheme.Brush("ImageAccent") });
        progress.Visibility = episode.CanResume && !episode.IsPlayed ? Visibility.Visible : Visibility.Collapsed;
        image.Children.Add(progress);
        var ring = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12), IsHitTestVisible = false };
        image.Children.Add(ring);
        image.Loaded += (_, _) => ApplyRoundedClip(image, 12);
        image.SizeChanged += (_, _) => ApplyRoundedClip(image, 12);
        body.Children.Add(imageFrame);
        var title = ImageText(episode.Title, 14, weight: true);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        title.Margin = new Thickness(0, 10, 0, 0);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(title);
        var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3, 0, 0) };
        var runtime = ImageText(episode.Item.RunTimeTicks is > 0 ? Runtime(episode.Item.RunTimeTicks.Value) : string.Empty, 12.5, opacity: .5);
        runtime.TextWrapping = TextWrapping.NoWrap;
        var status = ImageText(EpisodeWatchState(episode, false), 12.5);
        status.Foreground = episode.IsPlayed ? ImageBrush(128) : LumenTheme.Brush("ImageAccent");
        status.TextWrapping = TextWrapping.NoWrap;
        metadata.Children.Add(runtime);
        metadata.Children.Add(status);
        body.Children.Add(metadata);

        var open = EpisodePlainButton();
        open.Content = body;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.VerticalContentAlignment = VerticalAlignment.Top;
        open.IsEnabled = _library.CanUseSeasonActions && !_actionBusy && !_library.IsMutating;
        open.Click += (_, _) => { FocusEpisode(episode); RequestEpisodePlayback(episode); };
        ToolTipService.SetToolTip(open, episode.SceneIdentity);
        AutomationProperties.SetName(open, LumenText.Get("Play") + ": " + EpisodeCode(episode.Item) + " " + episode.Title);
        AutomationProperties.SetHelpText(open, episode.AutomationLabel);
        root.Children.Add(open);

        var mark = EpisodePlainButton();
        mark.Width = mark.Height = 44;
        mark.HorizontalAlignment = HorizontalAlignment.Right;
        mark.VerticalAlignment = VerticalAlignment.Top;
        mark.RenderTransform = new TranslateTransform();
        var markCircle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5) };
        var markIcon = new Image { Width = 14, Height = 14, IsHitTestVisible = false };
        markCircle.Child = markIcon;
        mark.Content = markCircle;
        mark.Click += async (_, _) => await ToggleEpisodeWatchedAsync(episode);
        root.Children.Add(mark);
        var visual = new EpisodeCardVisual(episode, root, open, imageFrame, number, title, ring, mark, markCircle, markIcon)
        {
            UpdateShadow = ConfigureEpisodeCardShadow(imageFrame, shadowHost),
            UpdateOffset = ConfigureEpisodeCardOffset(imageFrame, mark)
        };
        _episodeCards.Add(visual);

        var popup = CreateEpisodeContextFlyout(episode);
        open.ContextFlyout = popup;
        mark.ContextFlyout = popup;
        root.PointerEntered += (_, _) => QueueEpisodeFocus(episode);
        root.PointerExited += (_, _) =>
        {
            if (_pendingEpisodeHover?.Id != episode.Id) return;
            _episodeHoverTimer?.Stop();
            _pendingEpisodeHover = null;
        };
        root.GotFocus += (_, _) =>
        {
            FocusEpisode(episode);
            SetEpisodeStripExpanded(true);
            if (open.FocusState == FocusState.Keyboard || mark.FocusState == FocusState.Keyboard) CenterEpisode(episode, false);
        };
        root.KeyDown += (_, args) => HandleEpisodeCardKey(episode, args);
        return root;
    }

    private static Button EpisodePlainButton()
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var button = new Button
        {
            MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = clear, BorderBrush = clear, CornerRadius = new CornerRadius(12), UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
            "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" }) LumenUi.SetResource(button.Resources, key, clear);
        return button;
    }

    private Flyout CreateEpisodeContextFlyout(MediaCardViewModel episode)
    {
        var popup = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterStyle = PopupStyle(230) };
        popup.Opening += (_, _) =>
        {
            _episodeContextFlyout = popup;
            _hasOpenDetailFlyout = true;
            FocusEpisode(episode);
            SetEpisodeStripExpanded(true);
            UpdateStageInteraction();
            var rows = new StackPanel { Spacing = 2 };
            var available = _session is not null && _library.CanUseSeasonActions && !_actionBusy && !_library.IsMutating;
            void Add(string key, string icon, Action action, bool enabled = true)
            {
                var row = PopupAction(LumenText.Get(key), icon);
                row.IsEnabled = available && enabled;
                row.Click += (_, _) => { popup.Hide(); action(); };
                rows.Children.Add(row);
            }
            Add(episode.CanResume && !episode.IsPlayed ? "Resume" : "Play", "play", () => RequestEpisodePlayback(episode), episode.CanPlay && _library.IsPlaybackAllowed);
            Add("Play from beginning", "rotate-ccw", () => RequestEpisodePlayback(episode, true), episode.CanPlay && _library.IsPlaybackAllowed);
            Add(episode.IsPlayed ? "Mark unwatched" : "Mark watched", "check", () => _ = ToggleEpisodeWatchedAsync(episode));
            rows.Children.Add(new Border { Height = 1, Background = LumenTheme.Brush("Line"), Margin = new Thickness(10, 5, 10, 5) });
            Add("Media information", "info", () => _ = ShowMediaInformationAsync(episode));
            popup.Content = rows;
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_episodeContextFlyout, popup))
            {
                _episodeContextFlyout = null;
                _hasOpenDetailFlyout = false;
                UpdateStageInteraction();
            }
            ScheduleEpisodeStripCollapse();
        };
        return popup;
    }

    private async Task ToggleEpisodeWatchedAsync(MediaCardViewModel episode)
    {
        if (!_library.CanUseSeasonActions || _library.IsMutating) return;
        var restoreFocus = _episodeCards.FirstOrDefault(card => card.Episode.Id == episode.Id)?.Mark.FocusState == FocusState.Keyboard;
        var detailId = _library.Detail.Id;
        await RunDetailActionAsync(async (_, token) =>
        {
            await _library.ToggleItemPlayedAsync(episode, token);
            token.ThrowIfCancellationRequested();
            SetNotice(_library.HasError ? LumenText.Get(_library.ErrorMessage) : string.Empty, _library.HasError);
        });
        if (!restoreFocus) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_library.Detail.Id == detailId)
                _episodeCards.FirstOrDefault(card => card.Episode.Id == episode.Id)?.Mark.Focus(FocusState.Keyboard);
        });
    }

    private void QueueEpisodeFocus(MediaCardViewModel episode)
    {
        _pendingEpisodeHover = episode;
        _episodeHoverTimer ??= CreateEpisodeTimer(160, () =>
        {
            if (_pendingEpisodeHover is { } target) FocusEpisode(target);
            _pendingEpisodeHover = null;
        });
        _episodeHoverTimer.Stop();
        _episodeHoverTimer.Start();
    }

    private void FocusEpisode(MediaCardViewModel episode)
    {
        if (!_library.CanUseSeasonActions || !_episodeCards.Any(card => card.Episode.Id == episode.Id)) return;
        RecordExplicitEpisodeSelection();
        var changed = _focusedEpisode?.Id != episode.Id;
        _focusedEpisode = episode;
        if (!changed && _episodeFocusTextTimer?.IsEnabled == true)
        {
            UpdateEpisodeCardSelection();
            OnFocusedEpisodeChanged(episode);
            return;
        }
        if (changed && _episodeFocusBody is { IsLoaded: true } && new UISettings().AnimationsEnabled)
        {
            AnimateEpisodeFocusText(true);
            _episodeFocusTextTimer ??= CreateEpisodeTimer(240, () =>
            {
                UpdateFocusedEpisodeText();
                AnimateEpisodeFocusText(false);
            });
            _episodeFocusTextTimer.Stop();
            _episodeFocusTextTimer.Start();
        }
        else
        {
            _episodeFocusTextTimer?.Stop();
            UpdateFocusedEpisodeText();
            AnimateEpisodeFocusText(false, immediate: true);
        }
        UpdateEpisodeCardSelection();
        OnFocusedEpisodeChanged(episode);
    }

    private void AnimateEpisodeFocusText(bool hidden, bool immediate = false)
    {
        if (_episodeFocusBody is not { } body) return;
        ElementCompositionPreview.SetIsTranslationEnabled(body, true);
        var visual = ElementCompositionPreview.GetElementVisual(body);
        visual.StopAnimation("Opacity");
        visual.Properties.StopAnimation("Translation");
        var targetOpacity = hidden ? 0f : 1f;
        var targetTranslation = new Vector3(0, hidden ? 10 : 0, 0);
        if (immediate || !body.IsLoaded || !new UISettings().AnimationsEnabled)
        {
            visual.Opacity = targetOpacity;
            visual.Properties.InsertVector3("Translation", targetTranslation);
            return;
        }
        var compositor = visual.Compositor;
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, targetOpacity);
        opacity.Duration = TimeSpan.FromMilliseconds(300);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1));
        translation.InsertKeyFrame(1, targetTranslation, ease);
        translation.Duration = TimeSpan.FromMilliseconds(450);
        visual.StartAnimation("Opacity", opacity);
        visual.Properties.StartAnimation("Translation", translation);
    }

    private void UpdateFocusedEpisodeText()
    {
        if (_episodeFocusInfo is null || _episodeFocusNumber is null || _episodeFocusKicker is null || _episodeFocusTitle is null
            || _episodeFocusMetadata is null || _episodeFocusOverview is null) return;
        if (_focusedEpisode is not { } episode)
        {
            _episodeFocusInfo.Visibility = Visibility.Collapsed;
            return;
        }
        _episodeFocusInfo.Visibility = Visibility.Visible;
        _episodeFocusNumber.Text = EpisodeNumberText(episode, true);
        var series = episode.Item.SeriesName ?? _library.Detail.DisplayTitle;
        var season = episode.Item.ParentIndexNumber is { } number ? Format("Season {0}", number) : _library.SelectedSeason?.Title;
        _episodeFocusKicker.Text = string.Join(" \u00b7 ", new[] { series, season }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _episodeFocusTitle.Text = episode.Title;
        _episodeFocusMetadata.Text = string.Join(" \u00b7 ", new[]
        {
            episode.Item.RunTimeTicks is > 0 ? Runtime(episode.Item.RunTimeTicks.Value) : null,
            EpisodeWatchState(episode, true), episode.Item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _episodeFocusOverview.Text = string.IsNullOrWhiteSpace(episode.Item.Overview) ? LumenText.Get("No description is available.") : episode.Item.Overview;
        AutomationProperties.SetHeadingLevel(_episodeFocusTitle, AutomationHeadingLevel.Level2);
    }

    private static string EpisodeNumberText(MediaCardViewModel episode, bool padded) => episode.Item.IndexNumber is { } number
        ? number.ToString(padded ? "00" : "0", CultureInfo.InvariantCulture) : string.Empty;

    private static string EpisodeWatchState(MediaCardViewModel episode, bool includeUnwatched) => episode.IsPlayed
        ? LumenText.Get("Watched") : episode.CanResume ? Format("Watched {0}%", Math.Round(episode.Progress))
        : includeUnwatched ? LumenText.Get("Unwatched") : string.Empty;

    private void UpdateEpisodeCardSelection()
    {
        foreach (var card in _episodeCards)
        {
            var selected = card.Episode.Id == _focusedEpisode?.Id;
            card.Root.Opacity = (selected ? 1 : .6) * (_library.IsSeasonSwitchPending ? .45 : 1);
            card.Title.Opacity = selected ? 1 : .78;
            card.Ring.BorderBrush = selected ? ImageBrush(243) : ImageBrush(0);
            card.UpdateShadow?.Invoke(selected);
            card.UpdateOffset?.Invoke(selected);
            card.Mark.Visibility = selected || _showWatchedMarks && card.Episode.IsPlayed ? Visibility.Visible : Visibility.Collapsed;
            card.Mark.IsEnabled = _session is not null && _library.CanUseSeasonActions && !_actionBusy && !_library.IsMutating;
            card.MarkCircle.Background = card.Episode.IsPlayed ? LumenTheme.Brush("ImageAccent") : new SolidColorBrush(Color.FromArgb(89, 0, 0, 0));
            card.MarkCircle.BorderBrush = card.Episode.IsPlayed ? LumenTheme.Brush("ImageAccent") : ImageBrush(179);
            card.MarkIcon.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/{(card.Episode.IsPlayed ? "d" : "w")}/checkmark_20_regular.svg"));
            var label = LumenText.Get(card.Episode.IsPlayed ? "Mark unwatched" : "Mark watched");
            AutomationProperties.SetName(card.Mark, label + ": " + card.Episode.SceneIdentity);
            ToolTipService.SetToolTip(card.Mark, label);
        }
    }

    private Grid CreateEpisodePagingPad(int direction, out Button button)
    {
        var pad = new Grid { HorizontalAlignment = direction < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        button = LumenUi.IconButton(direction < 0 ? "chevron-left" : "chevron-right", LumenText.Get(direction < 0 ? "Previous episodes" : "Next episodes"), 46, true);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Background = new AcrylicBrush
        {
            TintColor = Color.FromArgb(255, 20, 18, 16), TintOpacity = .62,
            TintLuminosityOpacity = .15, FallbackColor = Color.FromArgb(224, 20, 18, 16)
        };
        button.BorderBrush = ImageBrush(41);
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", new SolidColorBrush(Color.FromArgb(191, 80, 76, 70)));
        button.Click += (_, _) =>
        {
            if (_episodeScroll is not { } scroller) return;
            var step = Math.Max(EpisodeCardWidth + 18, scroller.ViewportWidth * .82);
            scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset + direction * step, 0, scroller.ScrollableWidth), null, null);
        };
        pad.Children.Add(button);
        return pad;
    }

    private void UpdateEpisodePaging()
    {
        if (_episodeScroll is not { } scroller) return;
        SetEpisodePagingState(_episodePreviousButton, scroller.HorizontalOffset > 1);
        SetEpisodePagingState(_episodeNextButton, scroller.HorizontalOffset + 1 < scroller.ScrollableWidth);
    }

    private void SetEpisodePagingState(Button? button, bool available)
    {
        if (button is null) return;
        button.IsEnabled = available && _library.CanUseSeasonActions && !_actionBusy;
        button.Opacity = available ? 1 : 0;
        button.IsHitTestVisible = available;
    }

    private void HandleEpisodeCardKey(MediaCardViewModel episode, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key is not (VirtualKey.Left or VirtualKey.Right) || !_library.CanUseSeasonActions) return;
        var index = _episodeCards.FindIndex(card => card.Episode.Id == episode.Id);
        if (index < 0) return;
        var next = Math.Clamp(index + (args.Key == VirtualKey.Right ? 1 : -1), 0, _episodeCards.Count - 1);
        args.Handled = true;
        _episodeCards[next].Open.Focus(FocusState.Keyboard);
        FocusEpisode(_episodeCards[next].Episode);
        CenterEpisode(_episodeCards[next].Episode, false);
    }

    private bool HandleEpisodeNavigationKey(KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key is not (VirtualKey.Left or VirtualKey.Right) || !_library.CanUseSeasonActions
            || _actionBusy || _library.IsMutating || _activeDialog is not null || _episodeContextFlyout is not null || _episodeCards.Count == 0) return false;
        var index = _episodeCards.FindIndex(card => card.Episode.Id == _focusedEpisode?.Id);
        var next = Math.Clamp(index + (args.Key == VirtualKey.Right ? 1 : -1), 0, _episodeCards.Count - 1);
        args.Handled = true;
        SetEpisodeStripExpanded(true);
        _episodeCards[next].Open.Focus(FocusState.Keyboard);
        FocusEpisode(_episodeCards[next].Episode);
        CenterEpisode(_episodeCards[next].Episode, false);
        return true;
    }

    private void CenterCurrentEpisode()
    {
        if (_focusedEpisode is not { } episode) return;
        _episodeCenterPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_focusedEpisode?.Id == episode.Id) CenterEpisode(episode, true);
        });
    }

    private void CenterEpisode(MediaCardViewModel episode, bool immediate)
    {
        if (_episodeScroll is not { } scroller || _episodeRow is not { } row || scroller.ViewportWidth <= 0) return;
        var index = _episodeCards.FindIndex(card => card.Episode.Id == episode.Id);
        if (index < 0) return;
        _episodeCenterPending = false;
        var left = row.Padding.Left + index * (EpisodeCardWidth + row.Spacing) - (scroller.ViewportWidth - EpisodeCardWidth) / 2;
        scroller.ChangeView(Math.Clamp(left, 0, scroller.ScrollableWidth), null, null, immediate);
        UpdateEpisodePaging();
    }

    private void UpdateEpisodeResponsiveLayout()
    {
        if (_episodeStrip is null)
        {
            foreach (var child in _childrenSection.Children.OfType<FrameworkElement>())
                child.Margin = new Thickness(EpisodeInset, child.Margin.Top, EpisodeInset, child.Margin.Bottom);
            return;
        }
        var width = EpisodeViewportWidth;
        var height = EpisodeViewportHeight;
        var inset = EpisodeInset;
        var cardWidth = EpisodeCardWidth;
        if (_episodeFocusInfo is not null) _episodeFocusInfo.Margin = new Thickness(inset, 0, inset, 0);
        if (_episodeFocusBody is not null) _episodeFocusBody.MaxWidth = Math.Max(1, Math.Min(780, width - inset * 2));
        if (_episodeFocusNumber is not null)
        {
            _episodeFocusNumber.FontSize = Math.Clamp(Math.Min(width * .056, height * .095), 54, 104);
            _episodeFocusNumber.LineHeight = _episodeFocusNumber.FontSize * .8;
        }
        if (_episodeFocusTitle is not null)
        {
            _episodeFocusTitle.FontSize = Math.Clamp(Math.Min(width * .022, height * .04), 20, 34);
            _episodeFocusTitle.LineHeight = _episodeFocusTitle.FontSize * 1.15;
        }
        if (_episodeFocusOverview is not null) _episodeFocusOverview.Visibility = height >= 620 ? Visibility.Visible : Visibility.Collapsed;
        _episodeStrip.Margin = new Thickness(0, Math.Clamp(height * .034, 18, 36), 0, 0);
        if (_episodeHeader is not null)
        {
            _episodeHeader.Margin = new Thickness(inset, 0, inset, 0);
            if (_episodeHeader.Children.FirstOrDefault() is ScrollViewer seasons) seasons.MaxWidth = Math.Max(140, width - inset * 2 - 100);
        }
        if (_episodeRow is not null)
        {
            _episodeRow.Spacing = Math.Clamp(width * .012, 12, 18);
            _episodeRow.Padding = new Thickness(inset, 14, inset, 6);
        }
        if (_episodeRowViewport is not null) _episodeRowViewport.Height = cardWidth * 9 / 16 + 73;
        foreach (var card in _episodeCards)
        {
            card.Root.Width = cardWidth;
            card.Image.Height = cardWidth * 9 / 16;
            card.Number.FontSize = Math.Clamp(width * .026, 30, 42);
            card.Number.LineHeight = card.Number.FontSize;
            card.Mark.Margin = new Thickness(0, card.Image.Height - 44, 0, 0);
        }
        foreach (var pad in new[] { _episodePreviousPad, _episodeNextPad })
        {
            if (pad is null) continue;
            pad.Width = inset;
            pad.Height = cardWidth * 9 / 16;
            pad.Margin = new Thickness(0, 14, 0, 0);
        }
        if (_episodeStripAnimation is null) _episodeStrip.Height = _episodeStripExpanded ? cardWidth * 9 / 16 + 109 : 80;
        _episodeStrip.Opacity = _episodeStripExpanded ? 1 : .8;
        if (_episodeStripFade is not null) _episodeStripFade.Opacity = _episodeStripExpanded ? 0 : 1;
        UpdateEpisodePaging();
    }

    private void SetEpisodeStripExpanded(bool expanded)
    {
        _episodeCollapseTimer?.Stop();
        if (_episodeStrip is not { } strip || _episodeStripExpanded == expanded) return;
        _episodeStripExpanded = expanded;
        UpdateBackdropFade();
        UpdateStageInteraction();
        var from = strip.ActualHeight;
        _episodeStripAnimation?.Stop();
        _episodeStripAnimation = null;
        var target = expanded ? EpisodeCardWidth * 9 / 16 + 109 : 80;
        strip.Height = target;
        strip.Opacity = expanded ? 1 : .8;
        if (_episodeStripFade is not null) _episodeStripFade.Opacity = expanded ? 0 : 1;
        if (!strip.IsLoaded || from <= 0 || !new UISettings().AnimationsEnabled) return;
        var animation = new DoubleAnimation
        {
            From = from, To = target, Duration = TimeSpan.FromMilliseconds(550), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, strip);
        Storyboard.SetTargetProperty(animation, "Height");
        storyboard.Children.Add(animation);
        _episodeStripAnimation = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_episodeStripAnimation, storyboard)) return;
            _episodeStripAnimation = null;
            UpdateEpisodeResponsiveLayout();
        };
        storyboard.Begin();
    }

    private void ScheduleEpisodeStripCollapse()
    {
        _episodeCollapseTimer ??= CreateEpisodeTimer(280, () =>
        {
            if (!_episodePointerWithin && _episodeContextFlyout is null && _episodeStrip is { } strip && !HasFocusWithin(strip))
                SetEpisodeStripExpanded(false);
        });
        _episodeCollapseTimer.Stop();
        _episodeCollapseTimer.Start();
    }

    private static DispatcherTimer CreateEpisodeTimer(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        return timer;
    }

    private static Action<bool> ConfigureEpisodeCardOffset(Grid frame, Button mark)
    {
        bool? selected = null;
        Storyboard? pending = null;
        void Update(bool value)
        {
            if (selected == value) return;
            selected = value;
            var target = value ? -6d : 0d;
            var transforms = new[] { (TranslateTransform)frame.RenderTransform, (TranslateTransform)mark.RenderTransform };
            var starts = transforms.Select(transform => transform.Y).ToArray();
            pending?.Stop();
            foreach (var transform in transforms) transform.Y = target;
            if (!frame.IsLoaded || !new UISettings().AnimationsEnabled) return;
            var storyboard = new Storyboard();
            for (var index = 0; index < transforms.Length; index++)
            {
                var animation = new DoubleAnimation
                {
                    From = starts[index], To = target, Duration = TimeSpan.FromMilliseconds(450), EnableDependentAnimation = true,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                };
                Storyboard.SetTarget(animation, transforms[index]);
                Storyboard.SetTargetProperty(animation, "Y");
                storyboard.Children.Add(animation);
            }
            pending = storyboard;
            storyboard.Completed += (_, _) => { if (ReferenceEquals(pending, storyboard)) pending = null; };
            storyboard.Begin();
        }
        frame.Unloaded += (_, _) => { pending?.Stop(); pending = null; };
        return Update;
    }

    private static Action<bool> ConfigureEpisodeCardShadow(Grid frame, Grid host) =>
        ConfigureDetailShadow(frame, host, 12, 22, 8, .35f, 48, 22, .55f);

    private static Action<bool> ConfigureDetailShadow(Grid frame, Grid host, float cornerRadius, float blurRadius,
        float offset, float opacity, float selectedBlurRadius, float selectedOffset, float selectedOpacity)
    {
        CompositionRoundedRectangleGeometry? geometry = null;
        CompositionColorBrush? fill = null;
        CompositionSpriteShape? shape = null;
        ShapeVisual? caster = null;
        CompositionVisualSurface? surface = null;
        CompositionSurfaceBrush? mask = null;
        DropShadow? shadow = null;
        SpriteVisual? sprite = null;
        var selected = false;
        void Update(bool value)
        {
            selected = value;
            if (shadow is null) return;
            shadow.BlurRadius = selected ? selectedBlurRadius : blurRadius;
            shadow.Offset = new Vector3(0, selected ? selectedOffset : offset, 0);
            shadow.Opacity = selected ? selectedOpacity : opacity;
        }
        void Resize()
        {
            if (sprite is null || geometry is null || caster is null || surface is null || frame.ActualWidth <= 0 || frame.ActualHeight <= 0) return;
            var size = new Vector2((float)frame.ActualWidth, (float)frame.ActualHeight);
            geometry.Size = caster.Size = surface.SourceSize = size;
        }
        frame.Loaded += (_, _) =>
        {
            if (sprite is not null) return;
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(cornerRadius);
            fill = compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
            shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = fill;
            caster = compositor.CreateShapeVisual();
            caster.Shapes.Add(shape);
            surface = compositor.CreateVisualSurface();
            surface.SourceVisual = caster;
            mask = compositor.CreateSurfaceBrush(surface);
            shadow = compositor.CreateDropShadow();
            shadow.Color = Microsoft.UI.Colors.Black;
            shadow.Mask = mask;
            sprite = compositor.CreateSpriteVisual();
            sprite.RelativeSizeAdjustment = Vector2.One;
            sprite.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(host, sprite);
            Resize();
            Update(selected);
        };
        frame.SizeChanged += (_, _) => Resize();
        frame.Unloaded += (_, _) =>
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
            sprite?.Dispose();
            shadow?.Dispose();
            mask?.Dispose();
            surface?.Dispose();
            caster?.Dispose();
            shape?.Dispose();
            fill?.Dispose();
            geometry?.Dispose();
            sprite = null;
            shadow = null;
        };
        return Update;
    }

    private void ClearEpisodeElements()
    {
        _episodeHoverTimer?.Stop();
        _episodeCollapseTimer?.Stop();
        _episodeFocusTextTimer?.Stop();
        _episodeStripAnimation?.Stop();
        _episodeStripAnimation = null;
        if (_episodeContextFlyout is not null)
        {
            _hasOpenDetailFlyout = false;
            _episodeContextFlyout.Hide();
            UpdateStageInteraction();
        }
        _episodeContextFlyout = null;
        _episodeCollapseTimer?.Stop();
        _pendingEpisodeHover = null;
        _episodeFocusInfo = _episodeFocusBody = _episodeRow = null;
        _episodeFocusNumber = _episodeFocusKicker = _episodeFocusTitle = _episodeFocusMetadata = _episodeFocusOverview = null;
        _episodeHeader = _episodeStrip = _episodeRowViewport = _episodePreviousPad = _episodeNextPad = null;
        _episodePreviousButton = _episodeNextButton = null;
        _episodeStripFade = null;
        _episodeScroll = null;
        _episodeCards.Clear();
    }

    private void ResetEpisodeScreen()
    {
        ClearEpisodeElements();
        _focusedEpisode = null;
        _episodeContextKey = null;
        _episodeStripExpanded = false;
        _episodePointerWithin = false;
        _episodeCenterPending = false;
    }

    private sealed record EpisodeCardVisual(MediaCardViewModel Episode, Grid Root, Button Open, Grid Image, TextBlock Number,
        TextBlock Title, Border Ring, Button Mark, Border MarkCircle, Image MarkIcon)
    {
        public Action<bool>? UpdateShadow { get; init; }
        public Action<bool>? UpdateOffset { get; init; }
    }
}
