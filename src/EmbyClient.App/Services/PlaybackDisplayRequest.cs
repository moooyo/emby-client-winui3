using Windows.System.Display;

namespace EmbyClient.App.Services;

/// <summary>
/// Owns at most one successful display request. The view calls this object on its UI thread.
/// A suspended or disposed owner cannot be revived by a delayed playback notification.
/// </summary>
internal sealed partial class PlaybackDisplayRequest : IDisposable
{
    private readonly Func<IPlaybackDisplayRequest> _createRequest;
    private IPlaybackDisplayRequest? _request;
    private Guid? _playbackId;
    private bool _active;
    private bool _releasePending;
    private bool _suspended = true;
    private bool _disposed;

    public PlaybackDisplayRequest(Func<IPlaybackDisplayRequest>? createRequest = null) =>
        _createRequest = createRequest ?? (() => new WindowsDisplayRequest());

    internal bool NeedsReleaseRetry => _releasePending;

    public void ResumeTracking()
    {
        if (!_disposed) _suspended = false;
    }

    public void Update(Guid? currentPlaybackId, Guid? notificationPlaybackId, bool isPlayingVideo)
    {
        if (_suspended || _disposed)
        {
            TryRelease();
            return;
        }
        if (currentPlaybackId != notificationPlaybackId) return;
        if (_releasePending && !TryRelease()) return;
        if (_playbackId != currentPlaybackId)
        {
            if (!TryRelease()) return;
            _playbackId = currentPlaybackId;
        }
        if (!currentPlaybackId.HasValue || !isPlayingVideo)
        {
            TryRelease();
            return;
        }
        if (_active) return;
        try
        {
            _request ??= _createRequest();
            _request.RequestActive();
            _active = true;
        }
        catch (Exception)
        {
            // Display inhibition is optional. A platform failure must not stop playback.
        }
    }

    public void Suspend()
    {
        _suspended = true;
        TryRelease();
    }

    public void Dispose()
    {
        _disposed = true;
        _suspended = true;
        // Retain failed ownership so a repeated cleanup call can retry without another RequestActive.
        TryRelease();
    }

    private bool TryRelease()
    {
        if (!_active) return true;
        try
        {
            _request!.RequestRelease();
            _active = false;
            _releasePending = false;
            return true;
        }
        catch (Exception)
        {
            _releasePending = true;
            return false;
        }
    }

    private sealed class WindowsDisplayRequest : IPlaybackDisplayRequest
    {
        private readonly DisplayRequest _request = new();
        public void RequestActive() => _request.RequestActive();
        public void RequestRelease() => _request.RequestRelease();
    }
}

internal interface IPlaybackDisplayRequest
{
    void RequestActive();
    void RequestRelease();
}
