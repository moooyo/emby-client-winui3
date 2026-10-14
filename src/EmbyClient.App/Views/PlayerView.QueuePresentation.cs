using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly Dictionary<Guid, ImageSource> _queueArtwork = [];
    private readonly HashSet<Guid> _pendingQueueArtwork = [];

    private async void QueueItemClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not PlaybackQueueEntry entry || !CanStartQueuedItem() || _queue.IndexOf(entry.EntryId) < 0) return;
        var session = _session;
        var coordinator = _coordinator;
        var previousPlaybackId = coordinator?.ActiveContext?.PlaybackId;
        _advancing = true;
        UpdateQueueControls();
        CloseSidePanel(restoreFocus: false);
        try
        {
            await PlayItemAsync(entry.Item);
            if (ReferenceEquals(session, _session) && ReferenceEquals(coordinator, _coordinator)
                && coordinator is { ActiveContext: { } active, Status: PlaybackStatus.Playing or PlaybackStatus.Paused }
                && active.PlaybackId != previousPlaybackId && active.Selection.ItemId == entry.Item.Id)
                _queue.Remove(entry.EntryId);
        }
        finally
        {
            _advancing = false;
            UpdateQueueControls();
        }
    }

    private void QueueEntryRemoveClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: Guid entryId }) return;
        _queue.Remove(entryId);
        _queueArtwork.Remove(entryId);
        UpdateQueuePanel();
    }

    private void UpdateQueueArtwork(ContentControl container, PlaybackQueueEntry? entry, FrameworkElement template)
    {
        if (template.FindName("QueueArtwork") is not { } imageElement) return;
        var image = imageElement.As<Image>();
        if (entry is null)
        {
            image.Source = null;
            return;
        }
        if (_queueArtwork.TryGetValue(entry.EntryId, out var artwork)) image.Source = artwork;
        else
        {
            image.Source = null;
            if (_pendingQueueArtwork.Add(entry.EntryId)) _ = LoadQueueArtworkAsync(container, image, entry);
        }
    }

    private async Task LoadQueueArtworkAsync(ContentControl container, Image image, PlaybackQueueEntry entry)
    {
        var session = _session;
        var intent = _playIntent;
        var token = _playRequest?.Token ?? CancellationToken.None;
        try
        {
            if (session is null || string.IsNullOrWhiteSpace(session.Server.Id) || string.IsNullOrWhiteSpace(session.User.Id)) return;
            bool OwnsRequest() => ReferenceEquals(session, _session) && intent == _playIntent && _queue.IndexOf(entry.EntryId) >= 0;
            var bytes = await _playerArtworkCache.GetAsync(session.Api, session.Server.Id, session.User.Id,
                entry.Item, 256, 144, ArtworkKind.Landscape, token);
            if (bytes is not { Length: > 0 } || !OwnsRequest()) return;
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 256, 144, token, bitmap =>
            {
                if (!OwnsRequest()) return;
                _queueArtwork[entry.EntryId] = bitmap;
                if (ReferenceEquals(container.Content, entry) && container.IsLoaded) image.Source = bitmap;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Optional queue artwork cannot interfere with playback or queue ordering.
        }
        finally { _pendingQueueArtwork.Remove(entry.EntryId); }
    }
}
