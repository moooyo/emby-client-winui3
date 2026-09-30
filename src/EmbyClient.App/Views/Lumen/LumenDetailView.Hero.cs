using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RenderHero(MediaCardViewModel detail, bool series)
    {
        _hero.Children.Clear();
        var type = detail.Item.Type switch
        {
            "Movie" => "Movie", "Series" => "Series", "Season" => "Season", "Episode" => "Episode",
            "BoxSet" => "Collection", _ => detail.IsFolder ? "Collection" : "Video"
        };
        var eyebrow = ImageText(LumenText.Get(type), 13, opacity: .82);
        if (detail.Item.Type == "Episode") eyebrow.Text += "  " + EpisodeCode(detail.Item);
        _hero.Children.Add(eyebrow);
        var title = ImageText(detail.Title, series ? 62 : 64, true);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        title.LineHeight = title.FontSize * 1.08;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 3;
        title.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        _hero.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(detail.Item.OriginalTitle)
            && !string.Equals(detail.Item.OriginalTitle, detail.Item.Name, StringComparison.OrdinalIgnoreCase))
        {
            var original = ImageText(detail.Item.OriginalTitle.ToUpperInvariant(), 12, opacity: .7, weight: true);
            original.Margin = new Thickness(0, 6, 0, 0);
            original.TextWrapping = TextWrapping.Wrap;
            _hero.Children.Add(original);
        }
        var metadata = new FlowPanel { Spacing = 10, RowSpacing = 6, Margin = new Thickness(0, 16, 0, 0) };
        var values = new List<string>();
        if (detail.Item.ProductionYear is { } year) values.Add(year.ToString(CultureInfo.InvariantCulture));
        var seasonCount = detail.Item.SeasonCount ?? _library.Seasons.Count;
        if (series && seasonCount > 0) values.Add(Format("{0} seasons", seasonCount));
        else if (!series && detail.Item.RunTimeTicks is > 0) values.Add(Runtime(detail.Item.RunTimeTicks.Value));
        else if (detail.IsFolder && (detail.Item.RecursiveItemCount ?? detail.Item.ChildCount) is { } childCount)
            values.Add(Format("{0} items", childCount));
        if (detail.Item.Genres is { Length: > 0 }) values.Add(string.Join(" / ", detail.Item.Genres.Take(3)));
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) metadata.Children.Add(ImageText("\u00b7", 14, opacity: .45));
            metadata.Children.Add(ImageText(values[index], 14));
        }
        if (detail.Item.CommunityRating is { } rating)
        {
            var rated = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            rated.Children.Add(LumenUi.Icon("star", 14, true));
            rated.Children.Add(ImageText(rating.ToString("0.0", CultureInfo.InvariantCulture), 14));
            metadata.Children.Add(rated);
        }
        if (!string.IsNullOrWhiteSpace(detail.Item.OfficialRating)) metadata.Children.Add(OutlineBadge(detail.Item.OfficialRating));
        if (series)
            foreach (var tag in TechnicalTags(detail.Item).Take(3)) metadata.Children.Add(OutlineBadge(tag));
        if (metadata.Children.Count > 0) _hero.Children.Add(metadata);
        if (!series)
        {
            var tags = new FlowPanel { Spacing = 6, RowSpacing = 6, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var tag in TechnicalTags(detail.Item))
                tags.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12), Background = ImageBrush(36),
                    Padding = new Thickness(11, 4, 11, 4), Child = ImageText(tag, 11, weight: true)
                });
            if (tags.Children.Count > 0) _hero.Children.Add(tags);
        }
        var overview = ImageText(string.IsNullOrWhiteSpace(detail.Item.Overview)
            ? LumenText.Get("No description is available.") : detail.Item.Overview, 15, opacity: .9);
        overview.LineHeight = 25.5;
        overview.TextWrapping = TextWrapping.Wrap;
        overview.MaxLines = series ? 2 : 3;
        overview.TextTrimming = TextTrimming.CharacterEllipsis;
        overview.MaxWidth = 600;
        overview.Margin = new Thickness(0, series ? 14 : 16, 0, 0);
        _hero.Children.Add(overview);
        if (!series) AddPeopleSummary(detail);
        if (detail.Item.Type is "Episode" or "Season" && detail.Item.SeriesId is { Length: > 0 })
        {
            var parent = LumenUi.Button(detail.Item.SeriesName ?? LumenText.Get("Open series"), "tv", onImage: true, height: 38);
            parent.Margin = new Thickness(0, 14, 0, 0);
            parent.HorizontalAlignment = HorizontalAlignment.Left;
            parent.Click += async (_, _) => await _library.OpenParentSeriesAsync();
            _hero.Children.Add(parent);
        }
        RenderHeroActions(detail, series);
        if (!detail.IsFolder && SelectedSource is not null)
        {
            var selectors = CreateStreamSelectors();
            selectors.Margin = new Thickness(0, 16, 0, 0);
            _hero.Children.Add(selectors);
        }
    }

    private void AddPeopleSummary(MediaCardViewModel detail)
    {
        var people = detail.Item.People ?? [];
        var directors = people.Where(person => person.Type == "Director" && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => person.Name!).Take(3).ToArray();
        var cast = people.Where(person => person.Type is "Actor" or "GuestStar" && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => person.Name!).Take(4).ToArray();
        if (directors.Length == 0 && cast.Length == 0) return;
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6, Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (key, names) in new[] { ("Director", directors), ("Cast", cast) })
        {
            if (names.Length == 0) continue;
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = ImageText(LumenText.Get(key), 13, opacity: .6);
            Grid.SetRow(label, row);
            grid.Children.Add(label);
            var value = ImageText(string.Join(" \u00b7 ", names), 13);
            value.TextWrapping = TextWrapping.Wrap;
            value.MaxLines = 2;
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        _hero.Children.Add(grid);
    }

    private void RenderHeroActions(MediaCardViewModel detail, bool series)
    {
        var actions = new FlowPanel { Spacing = 10, RowSpacing = 12, Margin = new Thickness(0, 22, 0, 0) };
        var playable = _library.PlayableDetail;
        var label = playable.CanResume ? series ? Format("Resume {0}", EpisodeCode(playable.Item)) : LumenText.Get("Resume")
            : series && playable.Item.Type == "Episode" ? Format("Play {0}", EpisodeCode(playable.Item)) : LumenText.Get("Play");
        _playButton = LumenUi.Button(label, "play", true, true, 46);
        _playButton.MinWidth = 126;
        _playButton.Click += (_, _) => RaisePlay(_library.PlayableDetail.CanResume ? _library.PlayableDetail.ResumeTicks : 0);
        if (playable.CanPlay || !detail.IsFolder) actions.Children.Add(_playButton);
        if (playable.CanResume && playable.Item.RunTimeTicks is > 0)
        {
            var hint = Format("Remaining {0}", Runtime(Math.Max(0, playable.Item.RunTimeTicks.Value - playable.ResumeTicks)));
            if (series) hint = playable.Title + " \u00b7 " + hint;
            var resume = new StackPanel { Width = 120, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 10, 0) };
            var resumeText = ImageText(hint, 12, opacity: .8);
            resumeText.MaxLines = 1;
            resumeText.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(resumeText, hint);
            resume.Children.Add(resumeText);
            var track = new Grid { Height = 3, Background = ImageBrush(71), CornerRadius = new CornerRadius(2) };
            track.Children.Add(new Border
            {
                Width = 120 * playable.Progress / 100, HorizontalAlignment = HorizontalAlignment.Left,
                Background = LumenTheme.Brush("ImageAccent"), CornerRadius = new CornerRadius(2)
            });
            resume.Children.Add(track);
            actions.Children.Add(resume);
        }
        _trailerButton = LumenUi.Button(LumenText.Get("Trailer"), "clapperboard", onImage: true, height: 46);
        _trailerButton.Click += async (_, _) => await PlayTrailerAsync();
        actions.Children.Add(_trailerButton);
        if (!series)
        {
            _watchedButton = LumenUi.IconButton("check", LumenText.Get(detail.IsPlayed ? "Mark unwatched" : "Mark watched"), 46, true);
            _watchedButton.Opacity = detail.IsPlayed ? 1 : .74;
            _watchedButton.Click += async (_, _) => await MutateDetailUserDataAsync(false);
            actions.Children.Add(_watchedButton);
        }
        else _watchedButton = null;
        _favoriteButton = LumenUi.IconButton(detail.IsFavorite ? "heart_20_filled" : "heart", LumenText.Get(detail.IsFavorite ? "Remove favorite" : "Add favorite"), 46, true);
        _favoriteButton.Click += async (_, _) => await MutateDetailUserDataAsync(true);
        actions.Children.Add(_favoriteButton);
        _moreButton = CreateMoreButton();
        actions.Children.Add(_moreButton);
        _hero.Children.Add(actions);
        if (!_library.IsPlaybackAllowed || !detail.IsFolder && !playable.CanPlay)
        {
            var unavailable = ImageText(LumenText.Get(!_library.IsPlaybackAllowed ? "Playback is disabled for this account." : "No playable media is available."), 13, opacity: .8);
            unavailable.Margin = new Thickness(0, 12, 0, 0);
            unavailable.TextWrapping = TextWrapping.Wrap;
            _hero.Children.Add(unavailable);
        }
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
        if (_playButton is not null) _playButton.IsEnabled = enabled && _library.IsPlaybackAllowed && _library.PlayableDetail.CanPlay && _library.CanUseSeasonActions;
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
