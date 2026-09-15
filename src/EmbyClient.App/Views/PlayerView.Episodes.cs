using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private PlaybackEpisodeNeighbors? _episodeNeighbors;
    private string? _episodeNeighborsItemId;
    private bool _resolvingEpisodeNeighbors;

    private void ResetEpisodeNeighbors()
    {
        _episodeNeighbors = null;
        _episodeNeighborsItemId = null;
        _resolvingEpisodeNeighbors = false;
        UpdateEpisodeControls();
    }

    private async Task ResolveEpisodeNeighborsAsync(BaseItemDto item, ConnectedSession session, long intent, CancellationToken token)
    {
        if (!string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(item.SeriesId)) return;
        _episodeNeighborsItemId = item.Id;
        _resolvingEpisodeNeighbors = true;
        UpdateEpisodeControls();
        bool OwnsRequest() => ReferenceEquals(session, _session) && intent == _playIntent && _item?.Id == item.Id;
        try
        {
            var episodes = await session.Api.GetEpisodesAsync(item.SeriesId, cancellationToken: token);
            if (!OwnsRequest() || token.IsCancellationRequested) return;
            _episodeNeighbors = PlaybackEpisodeNeighbors.Resolve(item, episodes.Items);
        }
        catch (EmbyApiException ex) when (ex.IsAuthenticationFailure && ex.ApplicationErrorCode != "ParentalControl")
        {
            if (OwnsRequest()) ReportExpiredSession();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Episode navigation is optional metadata. Its failure must not replace a playback error.
            if (OwnsRequest()) _episodeNeighbors = null;
        }
        finally
        {
            if (OwnsRequest())
            {
                _resolvingEpisodeNeighbors = false;
                UpdateEpisodeControls();
            }
        }
    }

    private bool CanChangeEpisode() => _session is not null && !_advancing && !_retryInProgress && !IsModalOpen
        && _preparationIntent != _playIntent && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;

    private bool CanStartQueuedItem() => _session is not null && !_advancing && !_retryInProgress && !IsModalOpen
        && _preparationIntent != _playIntent && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused
            or PlaybackStatus.Ended or PlaybackStatus.Idle;

    private void UpdateEpisodeControls()
    {
        if (PreviousEpisodeButton is null) return;
        var isEpisode = string.Equals(_item?.Type, "Episode", StringComparison.OrdinalIgnoreCase);
        PreviousEpisodeButton.Visibility = NextEpisodeButton.Visibility = isEpisode ? Visibility.Visible : Visibility.Collapsed;
        var neighbors = _episodeNeighborsItemId == _item?.Id ? _episodeNeighbors : null;
        var enabled = CanChangeEpisode();
        PreviousEpisodeButton.IsEnabled = enabled && neighbors?.Previous is not null;
        NextEpisodeButton.IsEnabled = QueueNextEpisodeButton.IsEnabled = enabled && neighbors?.Next is not null;
        SetEpisodeLabel(PreviousEpisodeButton, "Previous episode", neighbors?.Previous, enabled);
        SetEpisodeLabel(NextEpisodeButton, "Next episode", neighbors?.Next, enabled);
        NextEpisodeSection.Visibility = isEpisode && _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NextEpisodeText.Text = neighbors?.Next is { } next ? PlaybackTitle(next, next.Name)
            : _resolvingEpisodeNeighbors ? "Finding the next episode…"
            : neighbors is null ? "Episode order is unavailable." : "No next episode is available.";
        QueueAutoPlayDescription.Text = _queue.Count > 0 ? "Your queue plays first. The next episode is resolved when the queue is empty."
            : isEpisode ? "Starts the next available episode when this one ends." : "Starts items you add to your queue when this movie ends.";
    }

    private void SetEpisodeLabel(Control button, string label, BaseItemDto? target, bool enabled)
    {
        var detail = target is not null ? $"{label}: {PlaybackTitle(target, target.Name)}"
            : _resolvingEpisodeNeighbors ? $"{label}: Finding episode order" : $"{label}: No episode available";
        if (!enabled && target is not null) detail += ". Available while playing or paused.";
        SetControlLabel(button, detail);
    }

    private async void PreviousEpisodeClicked(object sender, RoutedEventArgs args) => await ChangeEpisodeAsync(previous: true);
    private async void NextEpisodeClicked(object sender, RoutedEventArgs args) => await ChangeEpisodeAsync(previous: false);

    private async Task ChangeEpisodeAsync(bool previous)
    {
        if (!CanChangeEpisode() || _episodeNeighborsItemId != _item?.Id) return;
        var target = previous ? _episodeNeighbors?.Previous : _episodeNeighbors?.Next;
        if (target is null) return;
        _advancing = true;
        UpdateQueueControls();
        try
        {
            // This direct episode command deliberately leaves every manually queued entry untouched.
            await PlayItemAsync(target);
        }
        finally
        {
            _advancing = false;
            UpdateQueueControls();
        }
    }
}
