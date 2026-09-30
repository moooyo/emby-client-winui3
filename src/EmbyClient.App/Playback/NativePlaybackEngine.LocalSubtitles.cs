using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private const int MaximumLocalSubtitleBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _localSubtitleTransition = new(1, 1);

    /// <summary>Clears a local import even when the server selection was already off.</summary>
    public Task ClearLocalSubtitleAsync(Guid playbackId, CancellationToken cancellationToken = default) =>
        ControlAsync(playbackId, session =>
        {
            if (session.LocalSubtitle is null && session.PendingLocalSubtitle is null or { Detached: true }) return;
            RetireSubtitlePresentation(session);
            var current = session.LocalSubtitle;
            session.LocalSubtitle = null;
            session.ExternalTracks = [];
            if (session.PendingLocalSubtitle is { } pending)
            {
                pending.Completion.TrySetCanceled();
                ReleaseLocalSubtitle(session, pending, closeStream: false);
            }
            if (current is not null) ReleaseLocalSubtitle(session, current, closeStream: true);
            if (!IsActive(session)) return;
            ApplySubtitles(session);
            RefreshSubtitlePresentation(session, force: true);
            if (IsActive(session)) Publish(session, PlaybackEngineEventKind.StateChanged);
        }, cancellationToken);

    /// <summary>
    /// Loads local WebVTT without uploading it to the server. Select subtitles off through the coordinator first;
    /// server-burned text cannot be removed from an already negotiated video stream. The import belongs to one ID.
    /// </summary>
    public async Task LoadLocalSubtitleAsync(Guid playbackId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(text) > MaximumLocalSubtitleBytes)
            throw new PlaybackException("UnsupportedSubtitle");
        cancellationToken.ThrowIfCancellationRequested();
        var data = Encoding.UTF8.GetBytes(text);
        await _localSubtitleTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var load = await OnPlayerDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId);
                if (session is null) return Task.CompletedTask;
                if (session.Request.SubtitleStreamIndex is not (null or -1))
                    throw new PlaybackException("SubtitleSourceMustBeDisabled");
                if (session.MediaSource is null || session.Player is null || SubtitleTextChanged is null)
                    throw new PlaybackException("SubtitlePreferencesNotSupported");
                var import = new LocalSubtitleImport();
                session.PendingLocalSubtitle = import;
                return session.LocalSubtitleLoadTask = LoadLocalSubtitleCoreAsync(session, import, data, cancellationToken);
            }).ConfigureAwait(false);
            await load.ConfigureAwait(false);
        }
        finally { _localSubtitleTransition.Release(); }
    }

    private async Task LoadLocalSubtitleCoreAsync(Session session, LocalSubtitleImport import, byte[] data,
        CancellationToken cancellationToken)
    {
        // Track the operation before a synchronous native event or retirement can observe it.
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        try
        {
            var writing = await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                import.Stream = new InMemoryRandomAccessStream();
                return import.Stream.WriteAsync(data.AsBuffer()).AsTask(lifetime.Token);
            }).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                import.Stream!.Seek(0);
                import.Source = TimedTextSource.CreateFromStream(import.Stream);
                import.Resolved = (_, args) => Queue(() =>
                {
                    if (!IsActive(session) || import.Detached)
                    {
                        import.Completion.TrySetCanceled();
                        return;
                    }
                    if (args.Error is not null || args.Tracks.Count == 0)
                        import.Completion.TrySetException(new PlaybackException("UnsupportedSubtitle"));
                    else import.Completion.TrySetResult([.. args.Tracks]);
                });
                import.Source.Resolved += import.Resolved;
                import.Attached = true;
                session.MediaSource!.ExternalTimedTextSources.Add(import.Source);
                // Insertion can reenter through native events; retain the late attachment until cleanup.
                import.Attached = true;
                EnsureLocalSubtitleActive(session, import);
                return true;
            }).ConfigureAwait(false);
            TimedMetadataTrack[] tracks;
            try
            {
                tracks = await import.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            }
            catch (TimeoutException) { throw new PlaybackException("UnsupportedSubtitle"); }
            await OnPlayerDispatcherAsync(() =>
            {
                EnsureLocalSubtitleActive(session, import);
                lifetime.Token.ThrowIfCancellationRequested();
                CommitLocalSubtitle(session, import, tracks);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("UnsupportedSubtitle"); }
        finally
        {
            await OnDispatcherAsync(() =>
            {
                if (!import.Committed) ReleaseLocalSubtitle(session, import, closeStream: true);
                if (ReferenceEquals(session.PendingLocalSubtitle, import)) session.PendingLocalSubtitle = null;
            }).ConfigureAwait(false);
        }
    }

    private void EnsureLocalSubtitleActive(Session session, LocalSubtitleImport import)
    {
        EnsureActive(session);
        if (import.Detached) throw new OperationCanceledException();
    }

    private void CommitLocalSubtitle(Session session, LocalSubtitleImport import, TimedMetadataTrack[] tracks)
    {
        // Validate native cues while the previous subtitle is still owned and recoverable.
        var previousTracks = session.ExternalTracks;
        session.ExternalTracks = tracks;
        try { CaptureSubtitleCues(session); }
        catch
        {
            session.ExternalTracks = previousTracks;
            CaptureSubtitleCues(session);
            throw;
        }
        RetireSubtitlePresentation(session);
        var previous = session.LocalSubtitle;
        session.LocalSubtitle = import;
        session.ExternalTextReady = true;
        try
        {
            InitializeSubtitlePresentation(session);
            EnsureLocalSubtitleActive(session, import);
            ApplySubtitles(session);
            EnsureLocalSubtitleActive(session, import);
            import.Committed = true;
            session.PendingLocalSubtitle = null;
            if (previous is not null) ReleaseLocalSubtitle(session, previous, closeStream: true);
            EnsureLocalSubtitleActive(session, import);
            Publish(session, PlaybackEngineEventKind.StateChanged);
            EnsureLocalSubtitleActive(session, import);
        }
        catch
        {
            if (!IsActive(session) || import.Detached)
            {
                if (previous is not null) ReleaseLocalSubtitle(session, previous, closeStream: true);
                throw;
            }
            RetireSubtitlePresentation(session);
            session.LocalSubtitle = previous;
            session.ExternalTracks = previousTracks;
            if (previousTracks.Length > 0) InitializeSubtitlePresentation(session);
            ApplySubtitles(session);
            throw;
        }
    }

    private void RetireLocalSubtitles(Session session)
    {
        if (session.PendingLocalSubtitle is { } pending)
        {
            pending.Completion.TrySetCanceled();
            // Drain waits for a pending stream write before the import's finally closes that stream.
            ReleaseLocalSubtitle(session, pending, closeStream: false);
        }
        if (session.LocalSubtitle is { } current)
            ReleaseLocalSubtitle(session, current, closeStream: true);
        session.LocalSubtitle = null;
    }

    private async Task DrainLocalSubtitlesAsync(Session session)
    {
        try { await session.LocalSubtitleLoadTask.ConfigureAwait(false); }
        catch { }
        await OnDispatcherAsync(() => session.LocalSubtitleLoadTask = Task.CompletedTask).ConfigureAwait(false);
    }

    private void ReleaseLocalSubtitle(Session session, LocalSubtitleImport import, bool closeStream)
    {
        if (!import.Detached)
        {
            import.Detached = true;
            if (import.Source is { } source)
            {
                if (import.Resolved is { } resolved)
                    Release(session, "UnsubscribeLocalSubtitle", () => source.Resolved -= resolved);
            }
            import.Resolved = null;
        }
        var detach = import.Attached;
        import.Attached = false;
        if (detach && import.Source is { } attached && session.MediaSource is { } media)
            Release(session, "DetachLocalSubtitle", () => media.ExternalTimedTextSources.Remove(attached));
        if (!closeStream) return;
        Release(session, "CloseLocalSubtitleStream", () => import.Stream?.Dispose());
        import.Stream = null;
        import.Source = null;
    }

    private sealed class LocalSubtitleImport
    {
        public TaskCompletionSource<TimedMetadataTrack[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InMemoryRandomAccessStream? Stream { get; set; }
        public TimedTextSource? Source { get; set; }
        public TypedEventHandler<TimedTextSource, TimedTextSourceResolveResultEventArgs>? Resolved { get; set; }
        public bool Attached { get; set; }
        public bool Detached { get; set; }
        public bool Committed { get; set; }
    }

    private sealed partial class Session
    {
        public LocalSubtitleImport? LocalSubtitle { get; set; }
        public LocalSubtitleImport? PendingLocalSubtitle { get; set; }
        public Task LocalSubtitleLoadTask { get; set; } = Task.CompletedTask;
    }
}
