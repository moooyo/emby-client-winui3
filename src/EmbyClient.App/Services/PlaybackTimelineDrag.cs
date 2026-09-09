namespace EmbyClient.App.Services;

/// <summary>
/// Binds a timeline drag to one pointer and playback, including deferred capture-loss cleanup.
/// </summary>
internal sealed class PlaybackTimelineDrag
{
    private Ticket? _active;
    private long _generation;

    public bool IsActive => _active.HasValue;

    public void Begin(uint pointerId, Guid playbackId)
    {
        if (_active.HasValue) return;
        _active = new Ticket(pointerId, playbackId, ++_generation);
    }

    public Guid? Complete(uint pointerId, Guid? currentPlaybackId, bool canSeek)
    {
        if (_active is not { } ticket || ticket.PointerId != pointerId) return null;
        _active = null;
        return canSeek && ticket.PlaybackId == currentPlaybackId ? ticket.PlaybackId : null;
    }

    public void Reconcile(Guid? currentPlaybackId, bool canSeek)
    {
        if (_active is { } ticket && (!canSeek || ticket.PlaybackId != currentPlaybackId)) Clear();
    }

    public void Clear() => _active = null;

    public Ticket? Capture(uint pointerId) =>
        _active is { } ticket && ticket.PointerId == pointerId ? ticket : null;

    public void Cancel(Ticket ticket)
    {
        if (_active == ticket) Clear();
    }

    internal readonly record struct Ticket(uint PointerId, Guid PlaybackId, long Generation);
}
