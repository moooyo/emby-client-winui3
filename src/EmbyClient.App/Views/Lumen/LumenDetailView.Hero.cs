using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private MediaCardViewModel ActivePlaybackTarget => _deck.SelectedIndex == _episodePageIndex && _focusedEpisode is not null
        ? _focusedEpisode : _library.PlayableDetail;

    private void RenderHero(MediaCardViewModel detail, bool series)
    {
        var focusedAction = _favoriteButton is not null && HasFocusWithin(_favoriteButton) ? "favorite"
            : _watchedButton is not null && HasFocusWithin(_watchedButton) ? "watched"
            : _trailerButton is not null && HasFocusWithin(_trailerButton) ? "trailer"
            : _moreButton is not null && HasFocusWithin(_moreButton) ? "more" : null;
        _hero.Children.Clear();
        var kind = detail.Item.Type switch
        {
            "Movie" => "Movie", "Series" => "Series", "Season" => "Season", "Episode" => "Episode",
            "BoxSet" => "Collection", _ => detail.IsFolder ? "Collection" : "Video"
        };
        var eyebrow = new FlowPanel { Spacing = 10, RowSpacing = 6 };
        var category = ImageText(LumenText.Get(kind), 15, weight: true);
        category.Foreground = LumenTheme.Brush("ImageAccent");
        category.CharacterSpacing = 60;
        eyebrow.Children.Add(category);
        if (detail.Item.Type == "Episode") eyebrow.Children.Add(ImageText(EpisodeCode(detail.Item), 15, opacity: .8));
        if (detail.Item.Genres is { Length: > 0 })
        {
            eyebrow.Children.Add(ImageText("\u00b7", 15, opacity: .4));
            eyebrow.Children.Add(ImageText(string.Join(" / ", detail.Item.Genres.Take(4)), 15, opacity: .8));
        }
        _hero.Children.Add(eyebrow);
        _heroTitle = ImageText(detail.Title, 64, true);
        _heroTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        _heroTitle.CharacterSpacing = 30;
        _heroTitle.LineHeight = _heroTitle.FontSize * 1.06;
        _heroTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _heroTitle.TextWrapping = TextWrapping.Wrap;
        _heroTitle.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetHeadingLevel(_heroTitle, AutomationHeadingLevel.Level1);
        _hero.Children.Add(_heroTitle);
        var metadata = new FlowPanel { Spacing = 14, RowSpacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        if (detail.Item.CommunityRating is { } rating)
        {
            var rated = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            rated.Children.Add(LumenUi.Icon("star", 15, true));
            rated.Children.Add(ImageText(rating.ToString("0.0", CultureInfo.InvariantCulture), 15));
            metadata.Children.Add(rated);
        }
        if (detail.Item.ProductionYear is { } year) metadata.Children.Add(ImageText(year.ToString(CultureInfo.InvariantCulture), 15));
        if (series)
        {
            var count = detail.Item.SeasonCount ?? _library.Seasons.Count;
            var episodeCount = detail.Item.RecursiveItemCount ?? detail.Item.ChildCount;
            var seriesParts = new List<string>();
            if (count > 0) seriesParts.Add(Format("{0} seasons", count));
            if (episodeCount is > 0) seriesParts.Add(Format("{0} episodes", episodeCount.Value));
            if (seriesParts.Count > 0) metadata.Children.Add(ImageText(string.Join(" \u00b7 ", seriesParts), 15));
        }
        else if (detail.Item.RunTimeTicks is > 0) metadata.Children.Add(ImageText(Runtime(detail.Item.RunTimeTicks.Value), 15));
        else if (detail.IsFolder && (detail.Item.RecursiveItemCount ?? detail.Item.ChildCount) is { } childCount)
            metadata.Children.Add(ImageText(Format("{0} items", childCount), 15));
        if (!string.IsNullOrWhiteSpace(detail.Item.OfficialRating)) metadata.Children.Add(OutlineBadge(detail.Item.OfficialRating));
        _estimatedFinish = ImageText(string.Empty, 15, opacity: .62);
        var playable = _library.PlayableDetail;
        if (playable.Item.RunTimeTicks is > 0)
        {
            var remaining = Math.Max(0, playable.Item.RunTimeTicks.Value - (playable.CanResume ? playable.ResumeTicks : 0));
            _estimatedFinish.Text = Format("Ends around {0}", DateTime.Now.AddTicks(remaining).ToString("HH:mm", CultureInfo.CurrentCulture));
            metadata.Children.Add(_estimatedFinish);
        }
        if (metadata.Children.Count > 0) _hero.Children.Add(metadata);
        _heroDescription = ImageText(string.IsNullOrWhiteSpace(detail.Item.Overview)
            ? LumenText.Get("No description is available.") : detail.Item.Overview, 15, opacity: .88);
        _heroDescription.TextWrapping = TextWrapping.Wrap;
        _heroDescription.LineHeight = 27;
        _heroDescription.MaxLines = _descriptionExpanded ? 0 : 3;
        _heroDescription.TextTrimming = _descriptionExpanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        _heroDescription.MaxWidth = 640;
        _heroDescription.HorizontalAlignment = HorizontalAlignment.Left;
        _heroDescription.Margin = new Thickness(0, 14, 0, 0);
        _hero.Children.Add(_heroDescription);
        if (detail.Item.Overview?.Length > 80)
        {
            var expand = new HyperlinkButton
            {
                Content = LumenText.Get(_descriptionExpanded ? "Collapse description" : "Expand description"),
                FontFamily = LumenTheme.SansFont, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = LumenTheme.Brush("ImageAccent"), Padding = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 28, UseSystemFocusVisuals = true
            };
            expand.Click += (_, _) =>
            {
                _descriptionExpanded = !_descriptionExpanded;
                _heroDescription.MaxLines = _descriptionExpanded ? 0 : 3;
                _heroDescription.TextTrimming = _descriptionExpanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                expand.Content = LumenText.Get(_descriptionExpanded ? "Collapse description" : "Expand description");
                UpdateResponsiveLayout();
            };
            _hero.Children.Add(expand);
        }
        AddPeopleSummary(detail);
        if (detail.Item.Type is "Episode" or "Season" && detail.Item.SeriesId is { Length: > 0 })
        {
            var parent = new HyperlinkButton
            {
                Content = detail.Item.SeriesName ?? LumenText.Get("Open series"), Foreground = LumenTheme.Brush("ImageAccent"),
                FontFamily = LumenTheme.SansFont, FontSize = 13, Padding = new Thickness(0),
                Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left
            };
            parent.Click += async (_, _) => await _library.OpenParentSeriesAsync();
            _hero.Children.Add(parent);
        }
        RenderHeroActions(detail, series);
        RenderFloatingPlayback();
        if (focusedAction is not null) DispatcherQueue.TryEnqueue(() =>
        {
            var button = focusedAction switch { "favorite" => _favoriteButton, "watched" => _watchedButton,
                "trailer" => _trailerButton, _ => _moreButton };
            button?.Focus(FocusState.Programmatic);
        });
    }

    private void AddPeopleSummary(MediaCardViewModel detail)
    {
        var people = detail.Item.People ?? [];
        var directors = people.Where(person => person.Type == "Director" && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => person.Name!).Take(3).ToArray();
        var cast = people.Where(person => person.Type is "Actor" or "GuestStar" && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => person.Name!).Take(3).ToArray();
        var parts = new List<string>();
        if (directors.Length > 0) parts.Add(LumenText.Get("Director") + " " + string.Join(" / ", directors));
        if (cast.Length > 0) parts.Add(LumenText.Get("Cast") + " " + string.Join(" / ", cast));
        if (parts.Count == 0) return;
        var text = ImageText(string.Join("  \u00b7  ", parts), 14, opacity: .6);
        text.Margin = new Thickness(0, 12, 0, 0);
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxWidth = 920;
        _hero.Children.Add(text);
    }

    private void RenderHeroActions(MediaCardViewModel detail, bool series)
    {
        _heroActionDivider = null;
        _detailStreamSelectors.Clear();
        var actions = new FlowPanel { Spacing = 6, RowSpacing = 2, Margin = new Thickness(-14, 20, 0, 0) };
        if (SelectedSource is not null)
        {
            var selectors = CreateStreamSelectors();
            while (selectors.Children.Count > 0)
            {
                var selector = selectors.Children[0];
                selectors.Children.RemoveAt(0);
                actions.Children.Add(selector);
            }
            var divider = new Grid
            {
                Width = 17, Height = 60, Visibility = ActualWidth >= 720 ? Visibility.Visible : Visibility.Collapsed
            };
            divider.Children.Add(new Border
            {
                Width = 1, Height = 30, Background = ImageBrush(46),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            _heroActionDivider = divider;
            actions.Children.Add(divider);
        }
        _favoriteButton = CreateDetailAction("Favorite", detail.IsFavorite ? "heart-filled" : "heart", detail.IsFavorite);
        AutomationProperties.SetName(_favoriteButton, LumenText.Get(detail.IsFavorite ? "Remove favorite" : "Add favorite"));
        _favoriteButton.Click += async (_, _) => await MutateDetailUserDataAsync(true);
        actions.Children.Add(_favoriteButton);
        _watchedButton = CreateDetailAction("Watched action", "check", detail.IsPlayed);
        AutomationProperties.SetName(_watchedButton, LumenText.Get(detail.IsPlayed ? "Mark unwatched" : "Mark watched"));
        _watchedButton.Click += async (_, _) => await MutateDetailUserDataAsync(false);
        actions.Children.Add(_watchedButton);
        _trailerButton = CreateDetailAction("Trailer", "clapperboard");
        _trailerButton.Click += async (_, _) => await PlayTrailerAsync();
        actions.Children.Add(_trailerButton);
        _moreButton = CreateMoreButton();
        StyleDetailAction(_moreButton, "More", "ellipsis");
        actions.Children.Add(_moreButton);
        _hero.Children.Add(actions);
        if (!_library.IsPlaybackAllowed || !detail.IsFolder && !_library.PlayableDetail.CanPlay)
        {
            var unavailable = ImageText(LumenText.Get(!_library.IsPlaybackAllowed ? "Playback is disabled for this account." : "No playable media is available."), 13, opacity: .8);
            unavailable.Margin = new Thickness(0, 12, 0, 0);
            unavailable.TextWrapping = TextWrapping.Wrap;
            _hero.Children.Add(unavailable);
        }
    }

    private static Button CreateDetailAction(string label, string icon, bool selected = false)
    {
        var button = new Button();
        StyleDetailAction(button, label, icon, selected);
        return button;
    }

    private static void StyleDetailAction(Button button, string label, string icon, bool selected = false)
    {
        button.Height = 60;
        button.MinHeight = 60;
        button.Width = double.NaN;
        button.MinWidth = 58;
        button.Padding = new Thickness(14, 8, 14, 8);
        button.CornerRadius = new CornerRadius(14);
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.UseSystemFocusVisuals = true;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        LumenUi.SetResource(button.Resources, "ButtonBackground", button.Background);
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", ImageBrush(20));
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPressed", ImageBrush(30));
        var body = new StackPanel { Spacing = 4 };
        var heading = ImageText(LumenText.Get(label), 12, opacity: .55, weight: true);
        heading.CharacterSpacing = 160;
        heading.TextWrapping = TextWrapping.NoWrap;
        body.Children.Add(heading);
        var symbol = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = selected ? LumenTheme.Brush("ImageAccent") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = selected ? new Image
            {
                Width = 16, Height = 16,
                Source = new SvgImageSource(new Uri("ms-appx:///Assets/Lumen/Icons/d/" + (icon == "heart-filled" ? "heart_20_filled" : "checkmark_20_regular") + ".svg"))
            } : LumenUi.Icon(icon, icon is "ellipsis" or "clapperboard" ? 20 : 16, true)
        };
        body.Children.Add(symbol);
        button.Content = body;
        AutomationProperties.SetName(button, LumenText.Get(label));
        ToolTipService.SetToolTip(button, LumenText.Get(label));
    }

    private void RenderFloatingPlayback()
    {
        var restoreFocus = _playButton is not null && HasFocusWithin(_playButton);
        _floatingStage.Children.Clear();
        var playable = ActivePlaybackTarget;
        var episode = playable.Item.Type == "Episode";
        var label = playable.CanResume ? episode ? Format("Resume {0}", EpisodeCode(playable.Item)) : LumenText.Get("Resume")
            : episode ? Format("Play {0}", EpisodeCode(playable.Item)) : LumenText.Get(playable.IsPlayed ? "Watch again" : "Play");
        var column = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var disc = new Grid { Width = 108, Height = 108 };
        _playButton = LumenUi.IconButton("play", label, 92, true);
        _playButton.TabIndex = 0;
        _playButton.HorizontalAlignment = HorizontalAlignment.Center;
        _playButton.VerticalAlignment = VerticalAlignment.Center;
        _playButton.BorderBrush = ImageBrush(77);
        _playButton.Background = new AcrylicBrush
        {
            TintColor = Microsoft.UI.Colors.White, TintOpacity = .06, TintLuminosityOpacity = .1,
            FallbackColor = Color.FromArgb(56, 255, 255, 255)
        };
        LumenUi.SetResource(_playButton.Resources, "ButtonBackground", _playButton.Background);
        LumenUi.SetResource(_playButton.Resources, "ButtonBackgroundPointerOver", ImageBrush(240));
        LumenUi.SetResource(_playButton.Resources, "ButtonBackgroundPressed", ImageBrush(255));
        var icon = new Image { Width = 30, Height = 30, Margin = new Thickness(4, 0, 0, 0), IsHitTestVisible = false };
        void UpdateIcon(bool hovered) => icon.Source = new SvgImageSource(new Uri("ms-appx:///Assets/Lumen/Icons/" + (hovered ? "d" : "w") + "/play_24_filled.svg"));
        UpdateIcon(false);
        _playButton.PointerEntered += (_, _) => UpdateIcon(true);
        _playButton.PointerExited += (_, _) => UpdateIcon(false);
        _playButton.Content = icon;
        _playButton.Click += (_, _) => PlayActiveTarget(false);
        disc.Children.Add(_playButton);
        if (playable.CanResume)
        {
            disc.Children.Add(new Ellipse { Width = 103, Height = 103, Stroke = ImageBrush(46), StrokeThickness = 3, IsHitTestVisible = false });
            var progress = Math.Clamp(playable.Progress / 100, 0, 1);
            if (progress >= .999)
                disc.Children.Add(new Ellipse { Width = 103, Height = 103, Stroke = LumenTheme.Brush("ImageAccent"), StrokeThickness = 3, IsHitTestVisible = false });
            else if (progress > 0)
            {
                var angle = progress * Math.PI * 2 - Math.PI / 2;
                var figure = new PathFigure { StartPoint = new Point(54, 4), IsClosed = false };
                figure.Segments.Add(new ArcSegment
                {
                    Point = new Point(54 + 50 * Math.Cos(angle), 54 + 50 * Math.Sin(angle)),
                    Size = new Size(50, 50), IsLargeArc = progress > .5, SweepDirection = SweepDirection.Clockwise
                });
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                disc.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geometry, Stroke = LumenTheme.Brush("ImageAccent"), StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
                });
            }
        }
        column.Children.Add(disc);
        _floatingLabels = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0 };
        var title = ImageText(label, 16, weight: true);
        title.TextAlignment = TextAlignment.Center;
        _floatingLabels.Children.Add(title);
        var summaryParts = new List<string>();
        if (episode) summaryParts.Add(playable.Title);
        if (playable.CanResume)
        {
            summaryParts.Add(Format("Watched {0}%", Math.Round(playable.Progress)));
            if (playable.Item.RunTimeTicks is > 0) summaryParts.Add(Format("Remaining {0}", Runtime(Math.Max(0, playable.Item.RunTimeTicks.Value - playable.ResumeTicks))));
        }
        else if (playable.Item.RunTimeTicks is > 0) summaryParts.Add(Runtime(playable.Item.RunTimeTicks.Value));
        var summary = ImageText(string.Join(" \u00b7 ", summaryParts), 12.5, opacity: .75);
        summary.TextAlignment = TextAlignment.Center;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.MaxLines = 2;
        summary.MaxWidth = 340;
        _floatingLabels.Children.Add(summary);
        if (playable.CanResume)
        {
            var restart = LumenUi.Button(string.Empty, onImage: true, height: 28);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(LumenUi.Icon("rotate-ccw", 13, true));
            content.Children.Add(ImageText(LumenText.Get("Play from beginning"), 12, weight: true));
            restart.Content = content;
            restart.Padding = new Thickness(12, 0, 12, 0);
            restart.Background = new AcrylicBrush
            {
                TintColor = Color.FromArgb(255, 20, 18, 16), TintOpacity = .5, TintLuminosityOpacity = .1,
                FallbackColor = Color.FromArgb(180, 20, 18, 16)
            };
            restart.BorderBrush = ImageBrush(51);
            LumenUi.SetResource(restart.Resources, "ButtonBackground", restart.Background);
            LumenUi.SetResource(restart.Resources, "ButtonBackgroundPointerOver", ImageBrush(46));
            LumenUi.SetResource(restart.Resources, "ButtonBackgroundPressed", ImageBrush(61));
            AutomationProperties.SetName(restart, LumenText.Get("Play from beginning"));
            restart.HorizontalAlignment = HorizontalAlignment.Center;
            restart.Click += (_, _) => PlayActiveTarget(true);
            _floatingLabels.Children.Add(restart);
        }
        column.Children.Add(_floatingLabels);
        _floatingStage.Children.Add(column);
        UpdateActionState();
        UpdateStageInteraction();
        if (restoreFocus) DispatcherQueue.TryEnqueue(() => _playButton?.Focus(FocusState.Programmatic));
    }

    private void PlayActiveTarget(bool fromStart)
    {
        if (_activeDialog is not null || !_library.IsPlaybackAllowed || _actionBusy || !_library.CanUseSeasonActions) return;
        var playable = ActivePlaybackTarget;
        if (!playable.CanPlay) return;
        var ticks = fromStart ? 0 : playable.CanResume ? playable.ResumeTicks : 0;
        if (_deck.SelectedIndex != _episodePageIndex) RaisePlay(ticks);
        else PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(playable.Item, ticks));
    }

    private void RaisePlay(long startTicks)
    {
        var playable = _library.PlayableDetail;
        if (!_library.HasDetails || !_library.IsPlaybackAllowed || !playable.CanPlay || _actionBusy) return;
        var selectedForPlayable = _selectionItemId == playable.Id;
        PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(playable.Item, Math.Max(0, startTicks),
            mediaSourceId: selectedForPlayable ? SelectedSource?.Id : null,
            audioStreamIndex: selectedForPlayable ? _audioSelectionIntent.RequestIndex : null,
            subtitleStreamIndex: selectedForPlayable ? _selectedSubtitleStreamIndex : null));
    }

    private void UpdateActionState()
    {
        var enabled = _session is not null && !_actionBusy && !_library.IsMutating;
        if (_playButton is not null) _playButton.IsEnabled = enabled && _library.IsPlaybackAllowed && ActivePlaybackTarget.CanPlay && _library.CanUseSeasonActions;
        if (_favoriteButton is not null) _favoriteButton.IsEnabled = enabled;
        if (_watchedButton is not null) _watchedButton.IsEnabled = enabled;
        if (_moreButton is not null) _moreButton.IsEnabled = enabled;
        if (_trailerButton is not null)
        {
            _trailerButton.IsEnabled = enabled && _library.IsPlaybackAllowed
                && (_localTrailers.Any(item => new MediaCardViewModel(item).CanPlay) || RemoteTrailerUris().Any() || _localTrailerFailed);
            ToolTipService.SetToolTip(_trailerButton, LumenText.Get(_supplementaryLoading ? "Loading trailers..."
                : _localTrailerFailed ? "Trailers could not be loaded." : _trailerButton.IsEnabled ? "Trailer" : "No trailer is available."));
        }
    }
}
