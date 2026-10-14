using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EmbyClient.App.Playback;

/// <summary>Commands requested through Windows media controls, routed by the UI to PlaybackCoordinator.</summary>
public enum NativeMediaCommand
{
    Play,
    Pause,
    Stop,
    Seek,
    Previous,
    Next
}

/// <summary>PositionTicks, when present, is an absolute position in the complete media item.</summary>
public sealed class NativeMediaCommandEventArgs(Guid playbackId, NativeMediaCommand command, long? positionTicks = null) : EventArgs
{
    public Guid PlaybackId { get; } = playbackId;
    public NativeMediaCommand Command { get; } = command;
    public long? PositionTicks { get; } = positionTicks;
}

/// <summary>A source-free, account-scoped identity that the application must authenticate before resuming.</summary>
public sealed record NativeIdleMediaTarget(string AccountKey, string ItemId, string Title, string Subtitle)
{
    public bool IsValid => AccountKey is { Length: 64 } && AccountKey.All(char.IsAsciiHexDigit)
        && ItemId is { Length: > 0 and <= 128 }
        && ItemId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        && Title is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(Title) && !Title.Any(char.IsControl)
        && Subtitle is { Length: <= 256 } && !Subtitle.Any(char.IsControl);
}

public sealed partial class NativePlaybackEngine
{
    private static readonly TimeSpan MediaControlsTimelineInterval = TimeSpan.FromSeconds(5);
    private const int MaximumMediaControlsArtworkBytes = 16 * 1024 * 1024;
    private bool _systemMediaControlsEnabled = true;
    private NativeIdleMediaTarget? _idleMediaTarget;
    private SystemMediaTransportControls? _idleMediaControls;
    private TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>? _idleMediaButtonPressed;
    private long _idleMediaRegistration;
    private bool _idleMediaControlsUnavailable;

    /// <summary>Raised on the UI dispatcher. The receiver must authenticate and prepare the item before playback.</summary>
    public event EventHandler<NativeIdleMediaTarget>? IdleResumeRequested;

    /// <summary>Registers Play-only idle controls without retaining a source, authenticated URL, or resume position.</summary>
    public void SetIdleMediaControlsTarget(NativeIdleMediaTarget? target)
    {
        if (target is not null && !target.IsValid) throw new ArgumentException("The idle media identity is invalid.", nameof(target));
        DispatchMediaControls(Guid.Empty, "IdleMediaControlsTarget", () =>
        {
            RetireIdleMediaControlsCore();
            _idleMediaTarget = target;
            _idleMediaControlsUnavailable = false;
            UpdateIdleMediaControlsCore();
        });
    }

    /// <summary>
    /// Raised on the player UI dispatcher. The consumer must recheck PlaybackId against the current coordinator
    /// immediately before handling the command. Route commands to the coordinator, never directly to MediaPlayer;
    /// seeking a converted stream can require a new negotiation, and stopping must release server resources.
    /// </summary>
    public event EventHandler<NativeMediaCommandEventArgs>? MediaCommandRequested;

    /// <summary>
    /// Enables or unregisters this engine's system media integration. Disabling preserves active-session metadata
    /// so that enabling again can restore the same playing or paused item without waiting for a state transition.
    /// </summary>
    public void SetSystemMediaControlsEnabled(bool enabled)
    {
        DispatchMediaControls(Guid.Empty, "MediaControlsSettings", () =>
        {
            var wasEnabled = _systemMediaControlsEnabled;
            _systemMediaControlsEnabled = enabled;
            if (!enabled) RetireIdleMediaControlsCore();
            else if (!wasEnabled) _idleMediaControlsUnavailable = false;
            if (_current is not { } session || !IsActive(session))
            {
                UpdateIdleMediaControlsCore();
                return;
            }
            if (!enabled)
            {
                RetireMediaControlsCore(session, clearSessionMetadata: false);
                return;
            }
            if (!wasEnabled) session.MediaControlsUnavailable = false;
            InitializeMediaControls(session);
        });
    }

    /// <summary>
    /// Updates the active item's title, subtitle, and queue navigation capabilities. A null artwork URI preserves
    /// the existing artwork. Only local file, ms-appx, and ms-appdata URIs are accepted; authenticated remote images
    /// must arrive as cached bytes through SetMediaControlsArtworkAsync. Call again when queue capabilities change.
    /// </summary>
    public void SetMediaControlsMetadata(Guid playbackId, string? title, string? subtitle,
        Uri? artworkUri = null, bool previousEnabled = false, bool nextEnabled = false)
    {
        DispatchMediaControls(playbackId, "MediaControlsMetadata", () =>
        {
            var session = Find(playbackId);
            if (session is null) return;
            SetMediaControlsTitle(session, string.IsNullOrWhiteSpace(title) ? "Lumen" : title.Trim());
            var displaySubtitle = subtitle?.Trim() ?? string.Empty;
            if (!string.Equals(session.MediaControlsSubtitle, displaySubtitle, StringComparison.Ordinal))
            {
                session.MediaControlsSubtitle = displaySubtitle;
                session.MediaControlsMetadataDirty = true;
            }
            session.MediaControlsPreviousEnabled = previousEnabled;
            session.MediaControlsNextEnabled = nextEnabled;
            var currentArtworkUri = session.MediaControlsPendingArtwork is { } pendingArtwork
                ? pendingArtwork.ArtworkUri : session.MediaControlsArtworkUri;
            if (artworkUri is not null && !Equals(currentArtworkUri, artworkUri))
            {
                if (IsLocalMediaControlsArtworkUri(artworkUri))
                    _ = SetMediaControlsArtworkCoreAsync(playbackId, null, artworkUri, CancellationToken.None);
                else ReportMediaControlsFailure(playbackId, "MediaControlsArtwork", "SystemMediaArtworkUnavailable");
            }
            PublishMediaControlsMetadata(session);
            if (session.MediaControlsRequestedStatus is { } status)
                UpdateMediaControlsButtons(session, status);
        });
    }

    /// <summary>
    /// Supplies already authenticated or cached image bytes without exposing a server URL or credentials to SMTC.
    /// The caller must not modify the array until this task completes. Null or empty bytes clear the artwork.
    /// Stale completions cannot replace artwork belonging to a newer request or playback session.
    /// </summary>
    public Task SetMediaControlsArtworkAsync(Guid playbackId, byte[]? imageBytes,
        CancellationToken cancellationToken = default)
    {
        if (imageBytes is { Length: > MaximumMediaControlsArtworkBytes })
            throw new ArgumentOutOfRangeException(nameof(imageBytes), "System media artwork cannot exceed 16 MiB.");
        return SetMediaControlsArtworkCoreAsync(playbackId,
            imageBytes is { Length: > 0 } ? imageBytes : null, null, cancellationToken);
    }

    /// <summary>
    /// Updates SMTC from a coordinator snapshot. Call on status changes and from the existing position refresh loop.
    /// Position-only updates are throttled; state, duration, seek-capability changes, and position jumps update immediately.
    /// A null or retired context is ignored; RetireMediaControls owns clearing the retired player's controls.
    /// </summary>
    public void UpdateMediaControls(PlaybackContext? context, PlaybackStatus status, string? title = null)
    {
        if (context is null) return;
        DispatchMediaControls(context.PlaybackId, "MediaControlsUpdate", () =>
        {
            var session = Find(context.PlaybackId);
            if (session is null) return;
            session.MediaControlsContext = context;
            session.MediaControlsRequestedStatus = status;
            if (!string.IsNullOrWhiteSpace(title)) SetMediaControlsTitle(session, title.Trim());
            try
            {
                InitializeMediaControls(session);
                UpdateMediaControlsCore(session, context, status);
            }
            catch
            {
                session.MediaControlsUnavailable = true;
                RetireMediaControlsCore(session, clearSessionMetadata: false);
                throw;
            }
        });
    }

    /// <summary>Call after creating session.Player and disabling its automatic CommandManager, on the player dispatcher.</summary>
    private void InitializeMediaControls(Session session)
    {
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsInitialize", () =>
        {
            if (!_systemMediaControlsEnabled || !IsActive(session) || session.Player is null
                || session.MediaControls is not null || session.MediaControlsUnavailable) return;
            if (session.MediaControlsRequestedStatus is { } requested && IsTerminalMediaControlsStatus(requested))
            {
                UpdateIdleMediaControlsCore();
                return;
            }
            try
            {
                RetireIdleMediaControlsCore();
                session.Player.CommandManager.IsEnabled = false;
                var controls = session.Player.SystemMediaTransportControls;
                var registration = ++session.MediaControlsRegistrationGeneration;
                session.MediaControls = controls;
                controls.IsEnabled = false;
                controls.IsPlayEnabled = false;
                controls.IsPauseEnabled = false;
                controls.IsStopEnabled = false;
                controls.IsPreviousEnabled = false;
                controls.IsNextEnabled = false;
                DisableUnsupportedMediaCommands(controls);
                controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Type = MediaPlaybackType.Video;
                session.MediaControlsMetadataDirty = true;

                session.MediaControlsButtonPressed = (_, args) =>
                {
                    var command = args.Button switch
                    {
                        SystemMediaTransportControlsButton.Play => (NativeMediaCommand?)NativeMediaCommand.Play,
                        SystemMediaTransportControlsButton.Pause => NativeMediaCommand.Pause,
                        SystemMediaTransportControlsButton.Stop => NativeMediaCommand.Stop,
                        SystemMediaTransportControlsButton.Previous => NativeMediaCommand.Previous,
                        SystemMediaTransportControlsButton.Next => NativeMediaCommand.Next,
                        _ => null
                    };
                    if (command.HasValue) DispatchMediaCommand(session, controls, registration, command.Value);
                };
                session.MediaControlsPositionRequested = (_, args) =>
                    DispatchMediaCommand(session, controls, registration, NativeMediaCommand.Seek, args.RequestedPlaybackPosition.Ticks);
                controls.ButtonPressed += session.MediaControlsButtonPressed;
                controls.PlaybackPositionChangeRequested += session.MediaControlsPositionRequested;
                if (session.MediaControlsContext is { } context && session.MediaControlsRequestedStatus is { } status)
                    UpdateMediaControlsCore(session, context, status);
                else PublishMediaControlsMetadata(session);
            }
            catch
            {
                // System media controls are optional integration; their failure must not stop working playback.
                session.MediaControlsUnavailable = true;
                RetireMediaControlsCore(session, clearSessionMetadata: false);
                ReportMediaControlsFailure(session.Request.PlaybackId, "MediaControlsInitialize");
            }
        });
    }

    /// <summary>
    /// Call before disposing session.Player. This also works after session.Retired or engine._disposed is set.
    /// Only this session's handlers and controls are touched; a replacement player's controls are never cleared.
    /// </summary>
    private void RetireMediaControls(Session session) =>
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsRetire", () =>
        {
            RetireMediaControlsCore(session);
            if (_disposed) { _idleMediaTarget = null; RetireIdleMediaControlsCore(); }
            else UpdateIdleMediaControlsCore();
        }, allowDisposed: true);

    private void RetireMediaControlsCore(Session session, bool clearSessionMetadata = true)
    {
        session.MediaControlsRegistrationGeneration++;
        var controls = session.MediaControls;
        session.MediaControls = null;
        session.MediaControlsCanSeek = false;
        session.MediaControlsDurationTicks = null;
        session.MediaControlsStatus = null;
        session.MediaControlsLastTimelineTimestamp = null;
        session.MediaControlsLastPositionTicks = null;
        session.MediaControlsPlaybackRate = null;
        session.MediaControlsMetadataDirty = true;
        if (controls is not null)
        {
            if (session.MediaControlsButtonPressed is { } buttonPressed)
                Release(session, "UnsubscribeSystemMediaButtons", () => controls.ButtonPressed -= buttonPressed);
            if (session.MediaControlsPositionRequested is { } positionRequested)
                Release(session, "UnsubscribeSystemMediaPosition", () => controls.PlaybackPositionChangeRequested -= positionRequested);
            Release(session, "DisableSystemMediaControls", () => controls.IsEnabled = false);
            Release(session, "DisableSystemMediaButtons", () =>
            {
                controls.IsPlayEnabled = false;
                controls.IsPauseEnabled = false;
                controls.IsStopEnabled = false;
                controls.IsPreviousEnabled = false;
                controls.IsNextEnabled = false;
            });
            Release(session, "CloseSystemMediaControls", () => controls.PlaybackStatus = MediaPlaybackStatus.Closed);
            Release(session, "ClearSystemMediaMetadata", () =>
            {
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Update();
            });
        }
        session.MediaControlsButtonPressed = null;
        session.MediaControlsPositionRequested = null;
        if (!clearSessionMetadata) return;
        session.MediaControlsArtworkGeneration++;
        session.MediaControlsPendingArtwork?.Lifetime.Cancel();
        session.MediaControlsPendingArtwork = null;
        session.MediaControlsContext = null;
        session.MediaControlsRequestedStatus = null;
        session.MediaControlsTitle = null;
        session.MediaControlsSubtitle = null;
        session.MediaControlsPreviousEnabled = false;
        session.MediaControlsNextEnabled = false;
        session.MediaControlsArtwork = null;
        session.MediaControlsArtworkUri = null;
        var artworkStream = session.MediaControlsArtworkStream;
        session.MediaControlsArtworkStream = null;
        if (artworkStream is not null) Release(session, "CloseSystemMediaArtwork", artworkStream.Dispose);
    }

    private void UpdateMediaControlsCore(Session session, PlaybackContext context, PlaybackStatus status)
    {
        var controls = session.MediaControls;
        if (controls is null || !_systemMediaControlsEnabled || session.MediaControlsUnavailable) return;
        if (IsTerminalMediaControlsStatus(status))
        {
            if (session.MediaControlsStatus != status)
            {
                UpdateMediaControlsButtons(session, status);
                controls.IsEnabled = false;
                controls.PlaybackStatus = status == PlaybackStatus.Idle ? MediaPlaybackStatus.Closed : MediaPlaybackStatus.Stopped;
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Update();
                session.MediaControlsMetadataDirty = true;
            }
            session.MediaControlsCanSeek = false;
            session.MediaControlsStatus = status;
            session.MediaControlsLastTimelineTimestamp = null;
            session.MediaControlsLastPositionTicks = null;
            return;
        }

        var durationTicks = context.Source.IsInfiniteStream == true || context.Source.RunTimeTicks is not > 0
            ? (long?)null : context.Source.RunTimeTicks.Value;
        var canSeek = context.CanSeek && durationTicks.HasValue
            && status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        var stateChanged = session.MediaControlsStatus != status;
        var rangeChanged = session.MediaControlsDurationTicks != durationTicks || session.MediaControlsCanSeek != canSeek;
        var playbackRate = session.NativeSession?.PlaybackRate ?? 1d;
        if (!double.IsFinite(playbackRate) || playbackRate <= 0) playbackRate = 1d;
        var rateChanged = session.MediaControlsPlaybackRate != playbackRate;

        UpdateMediaControlsButtons(session, status);
        if (stateChanged)
        {
            controls.PlaybackStatus = status switch
            {
                PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Changing
            };
        }
        if (rateChanged) controls.PlaybackRate = playbackRate;
        PublishMediaControlsMetadata(session);

        var now = Stopwatch.GetTimestamp();
        var elapsed = session.MediaControlsLastTimelineTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(session.MediaControlsLastTimelineTimestamp.Value, now) : TimeSpan.Zero;
        var endTicks = durationTicks ?? 0;
        var positionTicks = durationTicks.HasValue ? Math.Clamp(context.PositionTicks, 0, endTicks) : 0;
        var positionJump = session.MediaControlsLastPositionTicks is { } previousPosition
            && (positionTicks < previousPosition
                || status == PlaybackStatus.Paused && positionTicks != previousPosition
                || status == PlaybackStatus.Playing && (double)positionTicks - previousPosition > elapsed.Ticks * playbackRate + (2 * TimeSpan.TicksPerSecond));
        if (stateChanged || rangeChanged || rateChanged || !session.MediaControlsLastTimelineTimestamp.HasValue
            || positionJump || elapsed >= MediaControlsTimelineInterval)
        {
            // SMTC shows the complete source timeline. Never use the engine's URL-relative position here.
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromTicks(endTicks),
                Position = TimeSpan.FromTicks(positionTicks),
                MinSeekTime = TimeSpan.FromTicks(canSeek ? 0 : positionTicks),
                MaxSeekTime = TimeSpan.FromTicks(canSeek ? endTicks : positionTicks)
            });
            session.MediaControlsLastTimelineTimestamp = now;
            session.MediaControlsLastPositionTicks = positionTicks;
        }

        session.MediaControlsDurationTicks = durationTicks;
        session.MediaControlsCanSeek = canSeek;
        session.MediaControlsStatus = status;
        session.MediaControlsPlaybackRate = playbackRate;
        if (!controls.IsEnabled) controls.IsEnabled = true;
    }

    private static bool IsTerminalMediaControlsStatus(PlaybackStatus status) =>
        status is PlaybackStatus.Idle or PlaybackStatus.Stopping or PlaybackStatus.Ended or PlaybackStatus.Failed;

    private static void SetMediaControlsTitle(Session session, string title)
    {
        if (string.Equals(session.MediaControlsTitle, title, StringComparison.Ordinal)) return;
        session.MediaControlsTitle = title;
        session.MediaControlsMetadataDirty = true;
    }

    private void UpdateMediaControlsButtons(Session session, PlaybackStatus status)
    {
        if (session.MediaControls is not { } controls) return;
        var enabled = _systemMediaControlsEnabled && !IsTerminalMediaControlsStatus(status);
        var canNavigate = enabled && status is PlaybackStatus.Playing or PlaybackStatus.Paused
            or PlaybackStatus.Buffering or PlaybackStatus.Seeking;
        controls.IsPlayEnabled = enabled && status == PlaybackStatus.Paused;
        controls.IsPauseEnabled = enabled && status == PlaybackStatus.Playing;
        controls.IsStopEnabled = enabled;
        controls.IsPreviousEnabled = canNavigate && session.MediaControlsPreviousEnabled;
        controls.IsNextEnabled = canNavigate && session.MediaControlsNextEnabled;
    }

    private void PublishMediaControlsMetadata(Session session)
    {
        if (!IsActive(session) || !_systemMediaControlsEnabled || session.MediaControlsUnavailable
            || !session.MediaControlsMetadataDirty || session.MediaControls is not { } controls
            || session.MediaControlsRequestedStatus is { } status && IsTerminalMediaControlsStatus(status)) return;
        var updater = controls.DisplayUpdater;
        updater.Type = MediaPlaybackType.Video;
        updater.VideoProperties.Title = session.MediaControlsTitle ?? "Lumen";
        updater.VideoProperties.Subtitle = session.MediaControlsSubtitle ?? string.Empty;
        var artwork = session.MediaControlsArtwork;
        try
        {
            updater.Thumbnail = artwork;
            updater.Update();
        }
        catch when (artwork is not null)
        {
            // An inaccessible image must not disable playback buttons or otherwise valid item metadata.
            updater.Thumbnail = null;
            updater.Update();
            if (ReferenceEquals(session.MediaControlsArtwork, artwork))
            {
                session.MediaControlsArtwork = null;
                session.MediaControlsArtworkUri = null;
                var rejectedStream = session.MediaControlsArtworkStream;
                session.MediaControlsArtworkStream = null;
                if (rejectedStream is not null) Release(session, "CloseUnavailableSystemMediaArtwork", rejectedStream.Dispose);
            }
            ReportMediaControlsFailure(session.Request.PlaybackId, "MediaControlsArtwork", "SystemMediaArtworkUnavailable");
        }
        session.MediaControlsMetadataDirty = false;
    }

    private static bool IsLocalMediaControlsArtworkUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Host.Length == 0 && (uri.IsFile && !uri.IsUnc || uri.Scheme is "ms-appx" or "ms-appdata");

    private async Task SetMediaControlsArtworkCoreAsync(Guid playbackId, byte[]? imageBytes,
        Uri? artworkUri, CancellationToken cancellationToken)
    {
        MediaControlsArtworkLoad? load = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            load = await OnDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Find(playbackId);
                if (session is null) return null;
                session.MediaControlsPendingArtwork?.Lifetime.Cancel();
                if (!IsActive(session)) return null;
                var created = new MediaControlsArtworkLoad(session, ++session.MediaControlsArtworkGeneration,
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token), artworkUri);
                session.MediaControlsPendingArtwork = created;
                session.MediaControlsArtworkLoads.Add(created);
                return created;
            }).ConfigureAwait(false);
            if (load is null) return;
            var pending = load;
            if (imageBytes is not null)
            {
                var writing = await OnDispatcherAsync(() =>
                {
                    pending.Lifetime.Token.ThrowIfCancellationRequested();
                    pending.Stream = new InMemoryRandomAccessStream();
                    return pending.Stream.WriteAsync(imageBytes.AsBuffer()).AsTask(pending.Lifetime.Token);
                }).ConfigureAwait(false);
                await writing.ConfigureAwait(false);
                await OnDispatcherAsync(() =>
                {
                    pending.Lifetime.Token.ThrowIfCancellationRequested();
                    pending.Stream!.Seek(0);
                    pending.Artwork = RandomAccessStreamReference.CreateFromStream(pending.Stream);
                }).ConfigureAwait(false);
            }
            else if (artworkUri is { IsFile: true })
            {
                var opening = await OnDispatcherAsync(() =>
                    StorageFile.GetFileFromPathAsync(artworkUri.LocalPath).AsTask(pending.Lifetime.Token)).ConfigureAwait(false);
                var file = await opening.ConfigureAwait(false);
                await OnDispatcherAsync(() =>
                {
                    pending.Lifetime.Token.ThrowIfCancellationRequested();
                    pending.Artwork = RandomAccessStreamReference.CreateFromFile(file);
                }).ConfigureAwait(false);
            }
            else if (artworkUri is not null)
            {
                await OnDispatcherAsync(() =>
                {
                    pending.Lifetime.Token.ThrowIfCancellationRequested();
                    pending.Artwork = RandomAccessStreamReference.CreateFromUri(artworkUri);
                }).ConfigureAwait(false);
            }
            await OnDispatcherAsync(() =>
            {
                pending.Lifetime.Token.ThrowIfCancellationRequested();
                var session = pending.Session;
                if (!IsActive(session) || session.MediaControlsArtworkGeneration != pending.Generation
                    || !ReferenceEquals(session.MediaControlsPendingArtwork, pending)) return;
                var previousStream = session.MediaControlsArtworkStream;
                session.MediaControlsArtwork = pending.Artwork;
                session.MediaControlsArtworkStream = pending.Stream;
                session.MediaControlsArtworkUri = pending.ArtworkUri;
                pending.Stream = null;
                session.MediaControlsMetadataDirty = true;
                try { PublishMediaControlsMetadata(session); }
                finally
                {
                    if (previousStream is not null) Release(session, "CloseSystemMediaArtwork", previousStream.Dispose);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch
        {
            DispatchMediaControls(playbackId, "MediaControlsArtwork", () =>
                ReportMediaControlsFailure(playbackId, "MediaControlsArtwork", "SystemMediaArtworkUnavailable"));
        }
        finally
        {
            if (load is not null)
            {
                var pending = load;
                try
                {
                    await OnDispatcherAsync(() => ReleaseMediaControlsArtworkLoad(pending)).ConfigureAwait(false);
                }
                catch
                {
                    ReportMediaControlsFailure(playbackId, "ClosePendingSystemMediaArtwork", "SystemMediaArtworkUnavailable");
                    pending.Completion.TrySetException(new PlaybackException("PlayerDispatcherUnavailable"));
                }
            }
        }
    }

    /// <summary>Called by the session drain while its dispatcher remains available.</summary>
    private async Task DrainMediaControlsAsync(Session session)
    {
        // A retired session cannot accept another artwork load. Superseded requests remain tracked until their
        // native writes have finished and their dispatcher cleanup has released the corresponding streams.
        var pending = await OnDispatcherAsync(() => session.MediaControlsArtworkLoads
            .Select(load => load.Completion.Task).ToArray()).ConfigureAwait(false);
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { }
        await OnDispatcherAsync(() =>
        {
            foreach (var load in session.MediaControlsArtworkLoads.ToArray()) ReleaseMediaControlsArtworkLoad(load);
        }).ConfigureAwait(false);
    }

    private void ReleaseMediaControlsArtworkLoad(MediaControlsArtworkLoad pending)
    {
        var session = pending.Session;
        if (ReferenceEquals(session.MediaControlsPendingArtwork, pending)) session.MediaControlsPendingArtwork = null;
        if (pending.Stream is { } stream) Release(session, "ClosePendingSystemMediaArtwork", stream.Dispose);
        pending.Stream = null;
        pending.Lifetime.Dispose();
        session.MediaControlsArtworkLoads.Remove(pending);
        pending.Completion.TrySetResult();
    }

    private void DispatchMediaCommand(Session session, SystemMediaTransportControls controls, long registration,
        NativeMediaCommand command, long? positionTicks = null)
    {
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsCommand", () =>
        {
            if (!IsActive(session) || session.MediaControlsRegistrationGeneration != registration
                || !ReferenceEquals(session.MediaControls, controls) || !controls.IsEnabled) return;
            if (command == NativeMediaCommand.Play && !controls.IsPlayEnabled
                || command == NativeMediaCommand.Pause && !controls.IsPauseEnabled
                || command == NativeMediaCommand.Stop && !controls.IsStopEnabled
                || command == NativeMediaCommand.Previous && !controls.IsPreviousEnabled
                || command == NativeMediaCommand.Next && !controls.IsNextEnabled) return;
            if (command == NativeMediaCommand.Seek)
            {
                if (!session.MediaControlsCanSeek || !positionTicks.HasValue || session.MediaControlsDurationTicks is not > 0) return;
                positionTicks = Math.Clamp(positionTicks.Value, 0, session.MediaControlsDurationTicks.Value);
            }
            MediaCommandRequested?.Invoke(this, new(session.Request.PlaybackId, command, positionTicks));
        });
    }

    private static void DisableUnsupportedMediaCommands(SystemMediaTransportControls controls)
    {
        controls.IsFastForwardEnabled = false;
        controls.IsRewindEnabled = false;
        controls.IsRecordEnabled = false;
        controls.IsChannelUpEnabled = false;
        controls.IsChannelDownEnabled = false;
        // Playback-rate requests, shuffle, and repeat are not advertised until explicitly routed by the product.
    }

    private void UpdateIdleMediaControlsCore()
    {
        if (_disposed || !_systemMediaControlsEnabled || _idleMediaTarget is not { } target
            || _idleMediaControlsUnavailable || _idleMediaControls is not null
            || HasActiveMediaControlsSession()) return;
        try
        {
            if (_current is { } completed && IsActive(completed))
                RetireMediaControlsCore(completed, clearSessionMetadata: false);
            // The same native player is reused by active playback. No source is assigned for an idle card.
            _playerOwner ??= new Windows.Media.Playback.MediaPlayer();
            _playerOwner.CommandManager.IsEnabled = false;
            var controls = _playerOwner.SystemMediaTransportControls;
            var registration = ++_idleMediaRegistration;
            _idleMediaControls = controls;
            controls.IsEnabled = false;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = false;
            controls.IsStopEnabled = false;
            controls.IsPreviousEnabled = false;
            controls.IsNextEnabled = false;
            DisableUnsupportedMediaCommands(controls);
            controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Type = MediaPlaybackType.Video;
            controls.DisplayUpdater.VideoProperties.Title = target.Title;
            controls.DisplayUpdater.VideoProperties.Subtitle = target.Subtitle;
            controls.DisplayUpdater.Update();
            _idleMediaButtonPressed = (_, args) =>
            {
                if (args.Button != SystemMediaTransportControlsButton.Play) return;
                DispatchMediaControls(Guid.Empty, "IdleMediaControlsPlay", () =>
                {
                    if (_disposed || !_systemMediaControlsEnabled || _idleMediaRegistration != registration
                        || !ReferenceEquals(_idleMediaControls, controls) || !ReferenceEquals(_idleMediaTarget, target)
                        || !controls.IsEnabled || !controls.IsPlayEnabled
                        || HasActiveMediaControlsSession()) return;
                    IdleResumeRequested?.Invoke(this, target);
                });
            };
            controls.ButtonPressed += _idleMediaButtonPressed;
            controls.IsEnabled = true;
        }
        catch
        {
            _idleMediaControlsUnavailable = true;
            RetireIdleMediaControlsCore();
            ReportMediaControlsFailure(Guid.Empty, "IdleMediaControlsInitialize");
        }
    }

    private bool HasActiveMediaControlsSession() => _current is { } current && IsActive(current)
        && (current.MediaControlsRequestedStatus is not { } status || !IsTerminalMediaControlsStatus(status));

    private void ClearIdleMediaControlsForDisposal()
    {
        _idleMediaTarget = null;
        RetireIdleMediaControlsCore();
    }

    private void RetireIdleMediaControlsCore()
    {
        _idleMediaRegistration++;
        var controls = _idleMediaControls;
        _idleMediaControls = null;
        var pressed = _idleMediaButtonPressed;
        _idleMediaButtonPressed = null;
        if (controls is null) return;
        if (pressed is not null) Release(Guid.Empty, "UnsubscribeIdleSystemMediaButtons", () => controls.ButtonPressed -= pressed);
        Release(Guid.Empty, "DisableIdleSystemMediaControls", () =>
        {
            controls.IsEnabled = false;
            controls.IsPlayEnabled = false;
            controls.IsPauseEnabled = false;
            controls.IsStopEnabled = false;
            controls.IsPreviousEnabled = false;
            controls.IsNextEnabled = false;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
        });
    }

    private void DispatchMediaControls(Guid playbackId, string operation, Action action, bool allowDisposed = false)
    {
        void Run()
        {
            if (_disposed && !allowDisposed) return;
            try { action(); }
            catch { ReportMediaControlsFailure(playbackId, operation); }
        }
        if (dispatcher.HasThreadAccess) Run();
        else if (!dispatcher.TryEnqueue(Run)) ReportMediaControlsFailure(playbackId, operation);
    }

    private void ReportMediaControlsFailure(Guid playbackId, string operation, string errorCode = "SystemMediaControlsUnavailable")
    {
        try { Diagnostic?.Invoke(this, new(playbackId, operation, errorCode)); }
        catch { }
    }

    private sealed class MediaControlsArtworkLoad(Session session, long generation, CancellationTokenSource lifetime, Uri? artworkUri)
    {
        public Session Session { get; } = session;
        public long Generation { get; } = generation;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? ArtworkUri { get; } = artworkUri;
        public IRandomAccessStream? Stream { get; set; }
        public RandomAccessStreamReference? Artwork { get; set; }
    }

    private sealed partial class Session
    {
        public SystemMediaTransportControls? MediaControls { get; set; }
        public long MediaControlsRegistrationGeneration { get; set; }
        public TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>? MediaControlsButtonPressed { get; set; }
        public TypedEventHandler<SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs>? MediaControlsPositionRequested { get; set; }
        public bool MediaControlsUnavailable { get; set; }
        public PlaybackContext? MediaControlsContext { get; set; }
        public PlaybackStatus? MediaControlsRequestedStatus { get; set; }
        public PlaybackStatus? MediaControlsStatus { get; set; }
        public string? MediaControlsTitle { get; set; }
        public string? MediaControlsSubtitle { get; set; }
        public bool MediaControlsMetadataDirty { get; set; } = true;
        public bool MediaControlsPreviousEnabled { get; set; }
        public bool MediaControlsNextEnabled { get; set; }
        public Uri? MediaControlsArtworkUri { get; set; }
        public RandomAccessStreamReference? MediaControlsArtwork { get; set; }
        public IRandomAccessStream? MediaControlsArtworkStream { get; set; }
        public long MediaControlsArtworkGeneration { get; set; }
        public MediaControlsArtworkLoad? MediaControlsPendingArtwork { get; set; }
        public HashSet<MediaControlsArtworkLoad> MediaControlsArtworkLoads { get; } = [];
        public long? MediaControlsDurationTicks { get; set; }
        public bool MediaControlsCanSeek { get; set; }
        public double? MediaControlsPlaybackRate { get; set; }
        public long? MediaControlsLastTimelineTimestamp { get; set; }
        public long? MediaControlsLastPositionTicks { get; set; }
    }
}
