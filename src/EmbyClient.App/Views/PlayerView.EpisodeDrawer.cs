using System.Globalization;
using System.Numerics;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
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
        EpisodeDrawerItems.Spacing = 2;
        EpisodeDrawerScroll.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => SuppressEpisodeDrawerAutoCenter()), true);
        EpisodeDrawerScroll.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => SuppressEpisodeDrawerAutoCenter()), true);
        EpisodeDrawerScroll.AddHandler(KeyDownEvent,
            new KeyEventHandler((_, _) => SuppressEpisodeDrawerAutoCenter()), true);
        _clock.Tick += (_, _) => RefreshCurrentEpisodeDrawerProgress();
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
                        Tag = season.Id
                    };
                    UpdateEpisodeSeasonLabel(entry, season);
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
        if (EpisodeSeasonSelector.SelectedItem is ComboBoxItem selectedSeason)
            ToolTipService.SetToolTip(EpisodeSeasonSelector, ToolTipService.GetToolTip(selectedSeason));
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
                if (episode.Id == owner.Item.Id) ArrangeCurrentEpisodeIntoView(row, owner);
            }
            EpisodeCountText.Text = string.Format(LumenText.Get("{0} episodes"), episodes.Length);
            SetEpisodeDrawerStatus(episodes.Length == 0 ? LumenText.Get("No episodes available.") : string.Empty);
            UpdateEpisodeDrawerControls();
            if (!_episodeDrawerRows.Any(row => row.Episode.Id == owner.Item.Id))
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
        foreach (var row in _episodeDrawerRows) RemoveEpisodeDrawerLoadedHandler(row);
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
        foreach (var row in _episodeDrawerRows) RemoveEpisodeDrawerLoadedHandler(row);
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
        AutomationProperties.SetAccessibilityView(artwork, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var imageGrid = new Grid
        {
            Width = 128, Height = 72, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(255, 26, 24, 21))
        };
        imageGrid.Children.Add(artwork);
        imageGrid.Loaded += (_, _) =>
        {
            if (!OwnsEpisodeDrawerRequest(owner) || imageGrid.ActualWidth <= 0 || imageGrid.ActualHeight <= 0) return;
            var visual = ElementCompositionPreview.GetElementVisual(imageGrid);
            var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2((float)imageGrid.ActualWidth, (float)imageGrid.ActualHeight);
            geometry.CornerRadius = new Vector2(6);
            visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
        };
        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 3, MinHeight = 3,
            VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
            Foreground = Resources["PlayerAccentBrush"] as Brush,
            Background = new SolidColorBrush(Color.FromArgb(77, 255, 255, 255))
        };
        AutomationProperties.SetAccessibilityView(progress, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        imageGrid.Children.Add(progress);
        var watched = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 5, 5, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            Child = PlayerIcon("checkmark_16_filled", size: 10)
        };
        imageGrid.Children.Add(watched);
        var title = new TextBlock
        {
            Text = EpisodeDrawerTitle(episode), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center
        };
        var nowPlaying = new Border
        {
            Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 0, 7, 0),
            Background = Resources["PlayerAccentBrush"] as Brush, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = LumenText.Get("Now playing"), FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 31, 22, 6)),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
            }
        };
        AutomationProperties.SetAccessibilityView(nowPlaying, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(title);
        Grid.SetColumn(nowPlaying, 1);
        heading.Children.Add(nowPlaying);
        var detail = new TextBlock
        {
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255)),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var overview = new TextBlock
        {
            Text = episode.Overview ?? string.Empty, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(184, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
            LineHeight = 17.4, Margin = new Thickness(0, 4, 0, 0),
            Visibility = string.IsNullOrWhiteSpace(episode.Overview) ? Visibility.Collapsed : Visibility.Visible
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        text.Children.Add(heading);
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
            Content = content, Padding = new Thickness(8), CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center, Style = Resources["PlayerEpisodeRowStyle"] as Style
        };
        button.Click += async (_, _) => await RunAsync(() => PlayEpisodeDrawerItemAsync(episode, owner));
        return new EpisodeDrawerRow(episode, button, artwork, title, detail, nowPlaying, watched, progress);
    }

    private void UpdateEpisodeDrawerControls()
    {
        if (EpisodeDrawerItems is null) return;
        SetControlLabel(EpisodeSeasonSelector, LumenText.Get("Season"));
        foreach (var entry in EpisodeSeasonSelector.Items.OfType<ComboBoxItem>())
        {
            if (entry.Tag is not string id || !_episodeDrawerSeasons.TryGetValue(id, out var season)) continue;
            UpdateEpisodeSeasonLabel(entry, season);
        }
        if (EpisodeSeasonSelector.SelectedItem is ComboBoxItem selectedSeason)
            ToolTipService.SetToolTip(EpisodeSeasonSelector, ToolTipService.GetToolTip(selectedSeason));
        EpisodeCountText.Text = string.Format(LumenText.Get("{0} episodes"), _episodeDrawerRows.Count);
        var available = CanChangeEpisode();
        foreach (var row in _episodeDrawerRows)
        {
            var current = row.Episode.Id == _item?.Id;
            var watched = row.Episode.UserData?.Played == true;
            row.Button.IsEnabled = available && !string.IsNullOrWhiteSpace(row.Episode.Id);
            row.Button.Background = new SolidColorBrush(Color.FromArgb(current ? (byte)26 : (byte)0, 255, 255, 255));
            row.Watched.Visibility = !current && watched && _preferences.ShowWatchedMarks ? Visibility.Visible : Visibility.Collapsed;
            row.NowPlaying.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            if (row.NowPlaying.Child is TextBlock badge) badge.Text = LumenText.Get("Now playing");
            UpdateEpisodeDrawerRowProgress(row, current);
            row.Detail.Text = row.Episode.RunTimeTicks is > 0
                ? LumenText.Get("{0} min", Math.Max(1, Math.Round(TimeSpan.FromTicks(row.Episode.RunTimeTicks.Value).TotalMinutes,
                    MidpointRounding.AwayFromZero))) : LumenText.Get("Unavailable");
            var title = EpisodeDrawerTitle(row.Episode);
            row.Title.Text = title;
            var position = Math.Max(0, row.Episode.UserData?.PlaybackPositionTicks ?? 0);
            var label = current ? string.Format(LumenText.Get("Now playing: {0}"), title)
                : position > 0 ? string.Format(LumenText.Get("Continue {0} from {1}"), title, FormatTime(position))
                : string.Format(LumenText.Get("Play {0}"), title);
            SetControlLabel(row.Button, label);
            AutomationProperties.SetHelpText(row.Button, string.Join(" · ", new[] { row.Detail.Text,
                !current && watched ? LumenText.Get("Watched") : null }.Where(value => !string.IsNullOrEmpty(value))));
        }
    }

    private static void UpdateEpisodeSeasonLabel(ComboBoxItem entry, BaseItemDto season)
    {
        var label = season.IndexNumber is { } number ? LumenText.Get("Season {0}", number)
            : season.Name ?? LumenText.Get("Season");
        entry.Content = season.IndexNumber is { } index ? "S" + index.ToString(CultureInfo.InvariantCulture) : label;
        var description = string.IsNullOrWhiteSpace(season.Name) || string.Equals(season.Name, label, StringComparison.Ordinal)
            ? label : label + " · " + season.Name;
        AutomationProperties.SetName(entry, description);
        ToolTipService.SetToolTip(entry, description);
    }

    private void RefreshCurrentEpisodeDrawerProgress()
    {
        if (_sidePanelMode != PlayerSidePanel.Episodes || !EpisodeDrawerScroll.IsLoaded
            || _episodeDrawerOwner is not { } owner
            || !OwnsEpisodeDrawerRequest(owner)) return;
        var current = _episodeDrawerRows.FirstOrDefault(row => row.Episode.Id == _item?.Id);
        if (current is not null) UpdateEpisodeDrawerRowProgress(current, current: true);
    }

    private void UpdateEpisodeDrawerRowProgress(EpisodeDrawerRow row, bool current)
    {
        var progress = current ? CurrentEpisodeDrawerProgress(row.Episode) : EpisodeDrawerProgress(row.Episode);
        row.Progress.Value = progress * 100;
        row.Progress.Visibility = progress > .01 && (current || row.Episode.UserData?.Played != true)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private double CurrentEpisodeDrawerProgress(BaseItemDto episode)
    {
        var context = _coordinator?.ActiveContext;
        if (context is null || context.Selection.ItemId != episode.Id) return 0;
        var duration = context.Source.RunTimeTicks ?? episode.RunTimeTicks;
        return duration is > 0 ? Math.Clamp((double)context.PositionTicks / duration.Value, 0, 1) : 0;
    }

    private void SuppressEpisodeDrawerAutoCenter()
    {
        if (_episodeDrawerOwner is { } owner) owner.AutoCenterSuppressed = true;
    }

    private void ArrangeCurrentEpisodeIntoView(EpisodeDrawerRow row, EpisodeDrawerRequest owner)
    {
        if (row.Button.IsLoaded) { QueueEpisodeDrawerCenter(row, owner); return; }
        row.PendingLoaded = (_, _) =>
        {
            RemoveEpisodeDrawerLoadedHandler(row);
            QueueEpisodeDrawerCenter(row, owner);
        };
        row.Button.Loaded += row.PendingLoaded;
    }

    private void QueueEpisodeDrawerCenter(EpisodeDrawerRow row, EpisodeDrawerRequest owner)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!OwnsEpisodeDrawerRequest(owner) || owner.AutoCenterSuppressed || owner.CurrentItemCentered
                || _sidePanelMode != PlayerSidePanel.Episodes || SidePanel.Visibility != Visibility.Visible
                || EpisodesPanel.Visibility != Visibility.Visible || !_episodeDrawerRows.Contains(row)
                || row.Episode.Id != _item?.Id || !row.Button.IsLoaded || row.Button.ActualHeight <= 0
                || EpisodeDrawerScroll.ViewportHeight <= 0) return;
            owner.CurrentItemCentered = true;
            row.Button.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = .5,
                AnimationDesired = false
            });
        });
    }

    private static void RemoveEpisodeDrawerLoadedHandler(EpisodeDrawerRow row)
    {
        if (row.PendingLoaded is not { } handler) return;
        row.Button.Loaded -= handler;
        row.PendingLoaded = null;
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
        ? episode.IndexNumber is { } number ? number.ToString(CultureInfo.CurrentCulture) + ". " + episode.Name : episode.Name
        : episode.IndexNumber is { } missingTitleNumber ? LumenText.Get("Episode {0}", missingTitleNumber)
        : LumenText.Get("Untitled episode");

    private static double EpisodeDrawerProgress(BaseItemDto episode)
    {
        if (episode.UserData?.Played == true) return 1;
        if (episode.RunTimeTicks is > 0 && episode.UserData?.PlaybackPositionTicks is > 0)
            return Math.Clamp((double)episode.UserData.PlaybackPositionTicks.Value / episode.RunTimeTicks.Value, 0, 1);
        return Math.Clamp(episode.UserData?.PlayedPercentage ?? 0, 0, 100) / 100;
    }

    private sealed record EpisodeDrawerRequest(ConnectedSession Session, BaseItemDto Item, long Intent, CancellationToken Token)
    {
        public bool AutoCenterSuppressed { get; set; }
        public bool CurrentItemCentered { get; set; }
    }

    private sealed record EpisodeDrawerRow(BaseItemDto Episode, Button Button, Image Artwork,
        TextBlock Title, TextBlock Detail, Border NowPlaying, Border Watched, ProgressBar Progress)
    {
        public RoutedEventHandler? PendingLoaded { get; set; }
    }
}
