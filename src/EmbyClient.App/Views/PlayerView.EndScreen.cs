using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly StackPanel _endRecommendations = new() { Spacing = 14, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 40, 0, 0) };
    private readonly StackPanel _endRecommendationRow = new() { Orientation = Orientation.Horizontal, Spacing = 16,
        HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScrollViewer _endRecommendationScroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false
    };
    private readonly List<EndRecommendationCard> _endRecommendationCards = [];
    private long? _endRecommendationsIntent;
    private EndedClockPresentation? _endedClockPresentation;

    private sealed record EndedClockPresentation(PlaybackNotificationOwner.Ticket Ticket,
        string ItemId, long PositionTicks, long? DurationTicks);

    public event EventHandler<BaseItemDto>? ItemDetailsRequested;

    private void InitializeEndRecommendations()
    {
        _endRecommendationScroll.Content = _endRecommendationRow;
        if (PlaybackEndedTitle.Parent is not StackPanel content) return;
        content.Children.Add(_endRecommendations);
        if (!ReferenceEquals(content.Parent, PlaybackEndedOverlay)) return;
        // A short window keeps every end action and recommendation reachable through native scrolling.
        PlaybackEndedOverlay.Children.Remove(content);
        PlaybackEndedOverlay.Children.Add(new ScrollViewer
        {
            Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, IsTabStop = false
        });
    }

    private void ResetEndRecommendations()
    {
        if (_endedClockPresentation is not null)
        {
            _endedClockPresentation = null;
            Timeline.Value = 0;
            PositionText.Text = DurationText.Text = FormatTime(0);
        }
        _endRecommendationsIntent = null;
        _endRecommendations.Children.Clear();
        _endRecommendationRow.Children.Clear();
        _endRecommendationCards.Clear();
        _endRecommendations.Visibility = Visibility.Collapsed;
    }

    private void CaptureEndedClockPresentation(PlaybackStatusChangedEventArgs args, PlaybackNotificationOwner.Ticket ticket)
    {
        _endedClockPresentation = args.Status == PlaybackStatus.Ended && args.Context is { } context
            && ticket.PlaybackId == context.PlaybackId && context.Selection.ItemId == _item?.Id
            ? new(ticket, context.Selection.ItemId, Math.Max(0, context.PositionTicks), context.Source.RunTimeTicks)
            : null;
    }

    private bool TryPresentEndedClock()
    {
        if (_endedClockPresentation is not { } ended || _coordinator?.Status != PlaybackStatus.Ended
            || ended.Ticket.Intent != _playIntent || !_notificationOwner.IsCurrent(ended.Ticket)
            || ended.Ticket.PlaybackId != _displayedPlayback || ended.ItemId != _item?.Id)
        {
            _endedClockPresentation = null;
            return false;
        }
        var duration = ended.DurationTicks;
        var position = duration is > 0 ? Math.Min(ended.PositionTicks, duration.Value) : ended.PositionTicks;
        PositionText.Text = _showRemainingTime && duration is > 0
            ? "-" + FormatTime(Math.Max(0, duration.Value - position)) : FormatTime(position);
        DurationText.Text = duration.HasValue ? FormatTime(duration.Value) : LumenText.Get("Live");
        Timeline.Maximum = Math.Max(1, duration.GetValueOrDefault() / (double)TimeSpan.TicksPerSecond);
        Timeline.Value = Math.Clamp(position / (double)TimeSpan.TicksPerSecond, 0, Timeline.Maximum);
        return true;
    }

    private void UpdateEndRecommendationsLayout()
    {
        if (PlayerRoot.ActualWidth > 0)
            PlaybackEndedTitle.FontSize = Math.Clamp(PlayerRoot.ActualWidth * .03, 26, 38);
        _endRecommendations.Visibility = !_compactOverlay && _endRecommendationCards.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (_endRecommendationCards.Count == 0 || PlayerRoot.ActualWidth <= 0 || PlayerRoot.ActualHeight <= 0) return;
        var padding = PlaybackEndedOverlay.Padding;
        var availableWidth = Math.Max(0, Math.Min(640, PlayerRoot.ActualWidth - padding.Left - padding.Right));
        var availableHeight = Math.Max(0, PlayerRoot.ActualHeight - padding.Top - padding.Bottom);
        // Reserve the end heading, actions, recommendation label, and scroll bar before sizing the posters.
        var heightWidth = Math.Clamp((availableHeight - 250) / 1.5, 80, 120);
        var widthWidth = Math.Clamp((availableWidth - (_endRecommendationCards.Count - 1) * 16)
            / _endRecommendationCards.Count, 80, 120);
        var cardWidth = Math.Min(heightWidth, widthWidth);
        foreach (var card in _endRecommendationCards)
        {
            card.Button.Width = card.Content.Width = card.Artwork.Width = cardWidth;
            card.Artwork.Height = cardWidth * 1.5;
        }
        _endRecommendations.MaxWidth = availableWidth;
        _endRecommendationScroll.Width = Math.Min(availableWidth,
            _endRecommendationCards.Count * cardWidth + (_endRecommendationCards.Count - 1) * 16);
    }

    private void UpdatePlaybackEndedPresentation()
    {
        if (_coordinator?.Status != PlaybackStatus.Ended) return;
        var item = _item;
        var episode = string.Equals(item?.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        PlaybackEndedEyebrow.Text = LumenText.Get("Playback finished");
        PlaybackEndedTitle.Text = item is null || string.IsNullOrWhiteSpace(item.Name)
            ? LumenText.Get("Playback finished")
            : string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase)
                ? LumenText.Get("Finished watching {0}", item.Name) : PlaybackTitle(item, item.Name);
        PlaybackEndedTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        var replay = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        replay.Children.Add(PlayerIcon("play_24_filled", dark: true, size: 16));
        replay.Children.Add(new TextBlock { Text = LumenText.Get("Play again"), FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        PlaybackEndedPrimaryButton.Content = replay;
        SetControlLabel(PlaybackEndedPrimaryButton, LumenText.Get("Play again"));
        var details = PlaybackEndedDetailsTarget();
        var returnLabel = LumenText.Get(episode && string.Equals(details?.Type, "Series", StringComparison.OrdinalIgnoreCase)
            ? "Return to series" : "Return to details");
        PlaybackEndedSecondaryButton.Content = returnLabel;
        PlaybackEndedSecondaryButton.IsEnabled = details is not null && _session is not null && !_expiredReported
            && !_advancing && !_retryInProgress && _preparationIntent != _playIntent;
        SetControlLabel(PlaybackEndedSecondaryButton, returnLabel);
        UpdateEndRecommendationsLayout();
    }

    private BaseItemDto? PlaybackEndedDetailsTarget()
    {
        var item = _item;
        if (item is null) return null;
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.SeriesId))
            return new BaseItemDto { Id = item.SeriesId, Name = item.SeriesName, Type = "Series" };
        return string.IsNullOrWhiteSpace(item.Id) ? null : item;
    }

    private void PlaybackEndedSecondaryClicked(object sender, RoutedEventArgs args)
    {
        if (_coordinator?.Status != PlaybackStatus.Ended || _session is null || _expiredReported || _advancing
            || _retryInProgress || _preparationIntent == _playIntent || IsModalOpen) return;
        if (PlaybackEndedDetailsTarget() is { } details) ItemDetailsRequested?.Invoke(this, details);
    }

    private async void LoadEndRecommendations()
    {
        var session = _session;
        var item = _item;
        var intent = _playIntent;
        var token = _playRequest?.Token ?? CancellationToken.None;
        if (item is null) return;
        var relatedId = string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) ? item.SeriesId : item.Id;
        if (session is null || string.IsNullOrWhiteSpace(relatedId) || _endRecommendationsIntent == intent) return;
        _endRecommendationsIntent = intent;
        bool OwnsRequest() => ReferenceEquals(session, _session) && intent == _playIntent
            && _coordinator?.Status == PlaybackStatus.Ended && ReferenceEquals(item, _item);
        try
        {
            var result = await session.Api.GetSimilarItemsAsync(relatedId, 4, token);
            if (!OwnsRequest()) return;
            _endRecommendations.Children.Clear();
            _endRecommendationRow.Children.Clear();
            _endRecommendationCards.Clear();
            var items = result.Items.Where(value => !string.IsNullOrWhiteSpace(value.Id)).Take(4).ToArray();
            if (items.Length == 0) return;
            _endRecommendations.Children.Add(new TextBlock { Text = LumenText.Get("Watch next"), FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(178, 255, 255, 255)), TextAlignment = TextAlignment.Center });
            _endRecommendations.Children.Add(_endRecommendationScroll);
            foreach (var recommendation in items)
            {
                var artwork = new Image { Width = 120, Height = 180, Stretch = Stretch.UniformToFill };
                var card = new StackPanel { Width = 120 };
                card.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 24, 21)), Child = artwork });
                var caption = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
                caption.Children.Add(new TextBlock { Text = recommendation.Name ?? string.Empty, FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Colors.White) });
                caption.Children.Add(new TextBlock { Text = string.Join(" · ", new[]
                    {
                        recommendation.ProductionYear?.ToString(), recommendation.Genres?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    FontSize = 11, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 255, 255, 255)),
                    TextTrimming = TextTrimming.CharacterEllipsis });
                card.Children.Add(caption);
                var button = new Button { Content = card, Style = (Style)Resources["PlayerTrackRowStyle"], Width = 120,
                    Height = double.NaN, Padding = new Thickness(0), CornerRadius = new CornerRadius(10) };
                SetControlLabel(button, recommendation.Name ?? LumenText.Get("Open details"));
                button.Click += (_, _) =>
                {
                    if (OwnsRequest() && !IsModalOpen) ItemDetailsRequested?.Invoke(this, recommendation);
                };
                _endRecommendationRow.Children.Add(button);
                _endRecommendationCards.Add(new EndRecommendationCard(button, card, artwork));
                _ = LoadEndRecommendationArtworkAsync(recommendation, artwork, session, intent, token);
            }
            UpdateEndRecommendationsLayout();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A missing recommendation response does not change the completed item's state or controls.
        }
    }

    private async Task LoadEndRecommendationArtworkAsync(BaseItemDto item, Image artwork, ConnectedSession session,
        long intent, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(session.Server.Id) || string.IsNullOrWhiteSpace(session.User.Id)) return;
        bool OwnsRequest() => ReferenceEquals(session, _session) && intent == _playIntent && _coordinator?.Status == PlaybackStatus.Ended;
        try
        {
            var bytes = await _playerArtworkCache.GetAsync(session.Api, session.Server.Id, session.User.Id,
                item, 240, 360, ArtworkKind.Poster, token);
            if (bytes is not { Length: > 0 } || !OwnsRequest()) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 240, 360, token,
                bitmap => { if (OwnsRequest()) artwork.Source = bitmap; });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private sealed record EndRecommendationCard(Button Button, StackPanel Content, Image Artwork);
}
