using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly ImageCache _episodeDrawerImages = new();
    private readonly List<EpisodeDrawerRow> _episodeDrawerRows = [];
    private readonly Dictionary<string, BaseItemDto> _episodeDrawerSeasons = new(StringComparer.Ordinal);
    private CancellationTokenSource? _episodeDrawerCancellation;
    private EpisodeDrawerRequest? _episodeDrawerOwner;
    private bool _updatingEpisodeSeasons;

    private void InitializeEpisodeDrawer()
    {
        SetControlLabel(EpisodeSeasonSelector, LumenText.Get("Season"));
        AutomationProperties.SetLiveSetting(EpisodeDrawerStatus,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        ResetEpisodeDrawer();
    }

    private void ResetEpisodeDrawer()
    {
        CancelEpisodeDrawerRequest();
        _episodeDrawerImages.Clear();
        _episodeDrawerSeasons.Clear();
        _updatingEpisodeSeasons = true;
        try
        {
            EpisodeSeasonSelector.Items.Clear();
            EpisodeSeasonSelector.IsEnabled = false;
        }
        finally { _updatingEpisodeSeasons = false; }
        ClearEpisodeDrawerRows();
        SetEpisodeDrawerStatus(string.Empty);
    }

    private async Task PopulateEpisodeDrawerAsync()
    {
        var session = _session;
        var item = _item;
        if (session is null || item is null || string.IsNullOrWhiteSpace(item.SeriesId)
            || !string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            ResetEpisodeDrawer();
            SetEpisodeDrawerStatus(LumenText.Get("No episodes available."));
            return;
        }

        var owner = BeginEpisodeDrawerRequest(session, item, _playIntent);
        _episodeDrawerSeasons.Clear();
        _updatingEpisodeSeasons = true;
        try
        {
            EpisodeSeasonSelector.Items.Clear();
            EpisodeSeasonSelector.IsEnabled = false;
        }
        finally { _updatingEpisodeSeasons = false; }
        ClearEpisodeDrawerRows();
        SetEpisodeDrawerStatus(LumenText.Get("Loading seasons..."));
        try
        {
            var result = await session.Api.GetSeasonsAsync(item.SeriesId, owner.Token);
            if (!OwnsEpisodeDrawerRequest(owner)) return;
            var seasons = result.Items.Where(season => !string.IsNullOrWhiteSpace(season.Id))
                .OrderBy(season => season.IndexNumber ?? int.MaxValue).ToArray();
            var selected = seasons.FirstOrDefault(season => season.Id == item.SeasonId)
                ?? seasons.FirstOrDefault(season => season.IndexNumber == item.ParentIndexNumber)
                ?? seasons.FirstOrDefault();
            _updatingEpisodeSeasons = true;
            try
            {
                foreach (var season in seasons)
                {
                    _episodeDrawerSeasons[season.Id!] = season;
                    var entry = new ComboBoxItem
                    {
                        Content = season.IndexNumber is { } number
                            ? string.Format(LumenText.Get("Season {0}"), number)
                            : season.Name ?? LumenText.Get("Season"),
                        Tag = season.Id
                    };
                    EpisodeSeasonSelector.Items.Add(entry);
                    if (ReferenceEquals(season, selected)) EpisodeSeasonSelector.SelectedItem = entry;
                }
                EpisodeSeasonSelector.IsEnabled = seasons.Length > 1;
            }
            finally { _updatingEpisodeSeasons = false; }
            await LoadEpisodeDrawerSeasonAsync(owner, selected?.Id ?? item.SeasonId);
        }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure
            && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (!OwnsEpisodeDrawerRequest(owner)) return;
            SetEpisodeDrawerStatus(LumenText.Get("Unable to load episodes."));
            ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (OwnsEpisodeDrawerRequest(owner))
                SetEpisodeDrawerStatus(LumenText.Get("Unable to load episodes."));
        }
    }

    private async void EpisodeSeasonChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingEpisodeSeasons || _sidePanelMode != PlayerSidePanel.Episodes
            || _episodeDrawerOwner is not { } previous || !OwnsEpisodeDrawerRequest(previous)
            || EpisodeSeasonSelector.SelectedItem is not ComboBoxItem { Tag: string seasonId }) return;
        var owner = BeginEpisodeDrawerRequest(previous.Session, previous.Item, previous.Intent);
        await LoadEpisodeDrawerSeasonAsync(owner, seasonId);
    }

    private async Task LoadEpisodeDrawerSeasonAsync(EpisodeDrawerRequest owner, string? seasonId)
    {
        if (!OwnsEpisodeDrawerRequest(owner)) return;
        ClearEpisodeDrawerRows();
        SetEpisodeDrawerStatus(LumenText.Get("Loading episodes..."));
        try
        {
            var result = await owner.Session.Api.GetEpisodesAsync(owner.Item.SeriesId!, seasonId, owner.Token);
            if (!OwnsEpisodeDrawerRequest(owner)) return;
            var episodes = result.Items.OrderBy(episode => episode.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(episode => episode.IndexNumber ?? int.MaxValue).ToArray();
            foreach (var episode in episodes)
            {
                var row = CreateEpisodeDrawerRow(episode, owner);
                _episodeDrawerRows.Add(row);
                EpisodeDrawerItems.Children.Add(row.Button);
            }
            EpisodeCountText.Text = string.Format(LumenText.Get("{0} episodes"), episodes.Length);
            SetEpisodeDrawerStatus(episodes.Length == 0 ? LumenText.Get("No episodes available.") : string.Empty);
            UpdateEpisodeDrawerControls();
            EpisodeDrawerScroll.ChangeView(null, 0, null, disableAnimation: true);
            await Task.WhenAll(_episodeDrawerRows.Select(row => LoadEpisodeDrawerImageAsync(row, owner)).ToArray());
        }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure
            && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (!OwnsEpisodeDrawerRequest(owner)) return;
            SetEpisodeDrawerStatus(LumenText.Get("Unable to load episodes."));
            ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (OwnsEpisodeDrawerRequest(owner))
                SetEpisodeDrawerStatus(LumenText.Get("Unable to load episodes."));
        }
    }

    private EpisodeDrawerRequest BeginEpisodeDrawerRequest(ConnectedSession session, BaseItemDto item, long intent)
    {
        CancelEpisodeDrawerRequest();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_playRequest?.Token ?? CancellationToken.None);
        _episodeDrawerCancellation = cancellation;
        var owner = new EpisodeDrawerRequest(session, item, intent, cancellation.Token);
        _episodeDrawerOwner = owner;
        return owner;
    }

    private void CancelEpisodeDrawerRequest()
    {
        _episodeDrawerOwner = null;
        var cancellation = _episodeDrawerCancellation;
        _episodeDrawerCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private bool OwnsEpisodeDrawerRequest(EpisodeDrawerRequest owner) =>
        ReferenceEquals(owner, _episodeDrawerOwner) && !owner.Token.IsCancellationRequested
        && ReferenceEquals(owner.Session, _session) && owner.Intent == _playIntent
        && ReferenceEquals(owner.Item, _item);

    private void ClearEpisodeDrawerRows()
    {
        _episodeDrawerRows.Clear();
        EpisodeDrawerItems.Children.Clear();
        EpisodeCountText.Text = string.Format(LumenText.Get("{0} episodes"), 0);
    }

    private void SetEpisodeDrawerStatus(string text)
    {
        EpisodeDrawerStatus.Text = text;
        EpisodeDrawerStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private EpisodeDrawerRow CreateEpisodeDrawerRow(BaseItemDto episode, EpisodeDrawerRequest owner)
    {
        var artwork = new Image { Width = 128, Height = 72, Stretch = Stretch.UniformToFill };
        var imageGrid = new Grid { Width = 128, Height = 72 };
        imageGrid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = artwork });
        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 3, MinHeight = 3,
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Foreground = Resources["PlayerAccentBrush"] as Brush,
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0))
        };
        imageGrid.Children.Add(progress);
        var watched = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)),
            Child = PlayerIcon("checkmark_16_filled", size: 12)
        };
        imageGrid.Children.Add(watched);
        var title = new TextBlock
        {
            Text = EpisodeDrawerTitle(episode), FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        var detail = new TextBlock
        {
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        var overview = new TextBlock
        {
            Text = episode.Overview ?? string.Empty, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
            Visibility = string.IsNullOrWhiteSpace(episode.Overview) ? Visibility.Collapsed : Visibility.Visible
        };
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);
        text.Children.Add(overview);
        var content = new Grid { ColumnSpacing = 12, MinHeight = 72 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(imageGrid);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var button = new Button
        {
            Content = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, Style = Resources["PlayerEpisodeRowStyle"] as Style
        };
        button.Click += async (_, _) => await RunAsync(() => PlayEpisodeDrawerItemAsync(episode, owner));
        return new EpisodeDrawerRow(episode, button, artwork, title, detail, watched, progress);
    }

    private void UpdateEpisodeDrawerControls()
    {
        if (EpisodeDrawerItems is null) return;
        SetControlLabel(EpisodeSeasonSelector, LumenText.Get("Season"));
        foreach (var entry in EpisodeSeasonSelector.Items.OfType<ComboBoxItem>())
        {
            if (entry.Tag is not string id || !_episodeDrawerSeasons.TryGetValue(id, out var season)) continue;
            entry.Content = season.IndexNumber is { } number ? string.Format(LumenText.Get("Season {0}"), number)
                : season.Name ?? LumenText.Get("Season");
        }
        EpisodeCountText.Text = string.Format(LumenText.Get("{0} episodes"), _episodeDrawerRows.Count);
        var available = CanChangeEpisode();
        foreach (var row in _episodeDrawerRows)
        {
            var current = row.Episode.Id == _item?.Id;
            var watched = row.Episode.UserData?.Played == true;
            row.Button.IsEnabled = available && !string.IsNullOrWhiteSpace(row.Episode.Id);
            row.Button.Background = new SolidColorBrush(Color.FromArgb(current ? (byte)26 : (byte)0, 255, 255, 255));
            row.Watched.Visibility = watched && _preferences.ShowWatchedMarks ? Visibility.Visible : Visibility.Collapsed;
            var progress = EpisodeDrawerProgress(row.Episode);
            row.Progress.Value = progress * 100;
            row.Progress.Visibility = progress > 0 ? Visibility.Visible : Visibility.Collapsed;
            var details = new List<string>();
            if (row.Episode.IndexNumber is { } number) details.Add(string.Format(LumenText.Get("Episode {0}"), number));
            if (row.Episode.RunTimeTicks is > 0) details.Add(FormatTime(row.Episode.RunTimeTicks.Value));
            if (current) details.Add(LumenText.Get("Now playing"));
            else if (watched) details.Add(LumenText.Get("Watched"));
            row.Detail.Text = string.Join(LumenText.Get(" \u00b7 "), details);
            var title = EpisodeDrawerTitle(row.Episode);
            row.Title.Text = title;
            var position = Math.Max(0, row.Episode.UserData?.PlaybackPositionTicks ?? 0);
            var label = current ? string.Format(LumenText.Get("Now playing: {0}"), title)
                : position > 0 ? string.Format(LumenText.Get("Continue {0} from {1}"), title, FormatTime(position))
                : string.Format(LumenText.Get("Play {0}"), title);
            SetControlLabel(row.Button, label);
        }
    }

    private async Task PlayEpisodeDrawerItemAsync(BaseItemDto episode, EpisodeDrawerRequest owner)
    {
        if (!OwnsEpisodeDrawerRequest(owner) || _sidePanelMode != PlayerSidePanel.Episodes
            || !CanChangeEpisode() || string.IsNullOrWhiteSpace(episode.Id)) return;
        if (episode.Id == _item?.Id) { CloseSidePanel(); return; }
        var position = Math.Max(0, episode.UserData?.PlaybackPositionTicks ?? 0);
        if (ItemPlaybackRequested is not null)
        {
            CloseSidePanel();
            ItemPlaybackRequested.Invoke(this, new Lumen.LumenPlayRequestEventArgs(episode, position));
            return;
        }
        _advancing = true;
        UpdateQueueControls();
        CloseSidePanel();
        try { await PlayItemAsync(episode, _preferences.ResumeMode == "Restart" ? 0 : position); }
        finally
        {
            _advancing = false;
            UpdateQueueControls();
        }
    }

    private async Task LoadEpisodeDrawerImageAsync(EpisodeDrawerRow row, EpisodeDrawerRequest owner)
    {
        var session = owner.Session;
        if (!OwnsEpisodeDrawerRequest(owner) || string.IsNullOrWhiteSpace(session.Server.Id)
            || string.IsNullOrWhiteSpace(session.User.Id)) return;
        try
        {
            var bytes = await _episodeDrawerImages.GetAsync(session.Api, session.Server.Id, session.User.Id,
                row.Episode, 256, 144, ArtworkKind.Landscape, owner.Token);
            if (bytes is not { Length: > 0 } || !OwnsEpisodeDrawerRequest(owner)) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 256, 144, owner.Token, bitmap =>
            {
                // A new season or playback owner must never receive retired artwork.
                if (OwnsEpisodeDrawerRequest(owner) && _episodeDrawerRows.Contains(row)) row.Artwork.Source = bitmap;
            });
        }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure
            && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (OwnsEpisodeDrawerRequest(owner)) ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Artwork is optional and cannot interrupt playback or replace episode metadata.
        }
    }

    private static string EpisodeDrawerTitle(BaseItemDto episode) => !string.IsNullOrWhiteSpace(episode.Name)
        ? episode.Name : episode.IndexNumber is { } number
            ? string.Format(LumenText.Get("Episode {0}"), number) : LumenText.Get("Untitled episode");

    private static double EpisodeDrawerProgress(BaseItemDto episode)
    {
        if (episode.UserData?.Played == true) return 1;
        if (episode.RunTimeTicks is > 0 && episode.UserData?.PlaybackPositionTicks is > 0)
            return Math.Clamp((double)episode.UserData.PlaybackPositionTicks.Value / episode.RunTimeTicks.Value, 0, 1);
        return Math.Clamp(episode.UserData?.PlayedPercentage ?? 0, 0, 100) / 100;
    }

    private sealed record EpisodeDrawerRequest(ConnectedSession Session, BaseItemDto Item, long Intent, CancellationToken Token);

    private sealed record EpisodeDrawerRow(BaseItemDto Episode, Button Button, Image Artwork,
        TextBlock Title, TextBlock Detail, Border Watched, ProgressBar Progress);
}
