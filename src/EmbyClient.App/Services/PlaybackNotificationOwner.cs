namespace EmbyClient.App.Services;

/// <summary>
/// Binds dispatched notifications to one user intent and its current coordinator playback.
/// The coordinator may publish on a worker thread; admission and dispatch validation share this lock.
/// </summary>
internal sealed class PlaybackNotificationOwner
{
    private readonly object _sync = new();
    private object? _owner;
    private long _intent;
    private long _generation;
    private Guid? _playbackId;
    private Guid? _retiredPlaybackId;
    private Guid? _excludedPlaybackId;
    private bool _armed;
    private bool _allowNewPlayback;
    private bool _advanceStarted;

    public void BeginPlay(object owner, long intent, Guid? previousPlaybackId)
    {
        lock (_sync)
        {
            _retiredPlaybackId = ReferenceEquals(owner, _owner) ? _playbackId ?? _retiredPlaybackId : null;
            _excludedPlaybackId = previousPlaybackId;
            _owner = owner;
            _intent = intent;
            _generation++;
            _playbackId = null;
            _armed = false;
            _allowNewPlayback = true;
            _advanceStarted = false;
        }
    }

    public void Arm(long intent)
    {
        lock (_sync) { if (_intent == intent && _owner is not null) _armed = true; }
    }

    public void BeginStop(object owner, long intent, Guid? playbackId)
    {
        lock (_sync)
        {
            _playbackId = playbackId ?? (ReferenceEquals(owner, _owner) ? _playbackId : null);
            _owner = owner;
            _intent = intent;
            _generation++;
            _armed = true;
            _allowNewPlayback = false;
            _advanceStarted = false;
        }
    }

    public void Invalidate(long intent)
    {
        lock (_sync)
        {
            _owner = null;
            _intent = intent;
            _generation++;
            _playbackId = null;
            _armed = false;
        }
    }

    public Ticket? Capture(object owner, Guid? playbackId, Guid? activePlaybackId,
        bool isOpening = false, bool isNegotiating = false, bool isEnded = false)
    {
        lock (_sync)
        {
            if (!_armed || !ReferenceEquals(owner, _owner) || isEnded && !_allowNewPlayback) return null;
            if (isNegotiating)
            {
                if (!_allowNewPlayback || playbackId.HasValue || activePlaybackId.HasValue) return null;
                _retiredPlaybackId = _playbackId ?? _retiredPlaybackId;
                _playbackId = null;
                _generation++;
                _advanceStarted = false;
            }
            else if (playbackId is { } id)
            {
                if (isOpening && _playbackId != id)
                {
                    // Only the coordinator's actual new source may establish ownership, never a terminal event.
                    if (!_allowNewPlayback || activePlaybackId != id || id == _retiredPlaybackId || id == _excludedPlaybackId) return null;
                    _retiredPlaybackId = _playbackId ?? _retiredPlaybackId;
                    _playbackId = id;
                    _generation++;
                    _advanceStarted = false;
                }
                if (_playbackId != id || activePlaybackId.HasValue && activePlaybackId != id) return null;
            }
            else if (isEnded || _allowNewPlayback && _playbackId.HasValue) return null;

            return new Ticket(_intent, _generation, _playbackId);
        }
    }

    public bool IsCurrent(Ticket ticket)
    {
        lock (_sync) return IsCurrentCore(ticket);
    }

    public bool TryBeginAdvance(Ticket ticket)
    {
        lock (_sync)
        {
            if (!IsCurrentCore(ticket) || !_allowNewPlayback || !ticket.PlaybackId.HasValue || _advanceStarted) return false;
            _advanceStarted = true;
            return true;
        }
    }

    private bool IsCurrentCore(Ticket ticket) => _armed && ticket.Intent == _intent
        && ticket.Generation == _generation && ticket.PlaybackId == _playbackId;

    internal readonly record struct Ticket(long Intent, long Generation, Guid? PlaybackId);
}
