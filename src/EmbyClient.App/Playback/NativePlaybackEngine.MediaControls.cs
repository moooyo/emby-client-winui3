using System.Diagnostics;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media;

namespace EmbyClient.App.Playback;

/// <summary>Commands requested through Windows media controls, routed by the UI to PlaybackCoordinator.</summary>
public enum NativeMediaCommand
{
    Play,
    Pause,
    Stop,
    Seek
}

/// <summary>PositionTicks, when present, is an absolute position in the complete media item.</summary>
public sealed class NativeMediaCommandEventArgs(Guid playbackId, NativeMediaCommand command, long? positionTicks = null) : EventArgs
{
    public Guid PlaybackId { get; } = playbackId;
    public NativeMediaCommand Command { get; } = command;
    public long? PositionTicks { get; } = positionTicks;
}

public sealed partial class NativePlaybackEngine
{
    private static readonly TimeSpan MediaControlsTimelineInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raised on the player UI dispatcher. The consumer must recheck PlaybackId against the current coordinator
    /// immediately before handling the command. Route commands to the coordinator, never directly to MediaPlayer;
    /// seeking a converted stream can require a new negotiation, and stopping must release server resources.
    /// </summary>
    public event EventHandler<NativeMediaCommandEventArgs>? MediaCommandRequested;

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
            if (session is null || !IsActive(session)) return;
            try { UpdateMediaControlsCore(session, context, status, title); }
            catch
            {
                session.MediaControlsUnavailable = true;
                RetireMediaControlsCore(session);
                throw;
            }
        });
    }

    /// <summary>Call after creating session.Player and disabling its automatic CommandManager, on the player dispatcher.</summary>
    private void InitializeMediaControls(Session session)
    {
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsInitialize", () =>
        {
            if (!IsActive(session) || session.Player is null || session.MediaControls is not null || session.MediaControlsUnavailable) return;
            try
            {
                session.Player.CommandManager.IsEnabled = false;
                var controls = session.Player.SystemMediaTransportControls;
                session.MediaControls = controls;
                controls.IsEnabled = false;
                controls.IsPlayEnabled = false;
                controls.IsPauseEnabled = false;
                controls.IsStopEnabled = false;
                DisableUnsupportedMediaCommands(controls);
                controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Type = MediaPlaybackType.Video;

                session.MediaControlsButtonPressed = (_, args) =>
                {
                    var command = args.Button switch
                    {
                        SystemMediaTransportControlsButton.Play => (NativeMediaCommand?)NativeMediaCommand.Play,
                        SystemMediaTransportControlsButton.Pause => NativeMediaCommand.Pause,
                        SystemMediaTransportControlsButton.Stop => NativeMediaCommand.Stop,
                        _ => null
                    };
                    if (command.HasValue) DispatchMediaCommand(session, controls, command.Value);
                };
                session.MediaControlsPositionRequested = (_, args) =>
                    DispatchMediaCommand(session, controls, NativeMediaCommand.Seek, args.RequestedPlaybackPosition.Ticks);
                controls.ButtonPressed += session.MediaControlsButtonPressed;
                controls.PlaybackPositionChangeRequested += session.MediaControlsPositionRequested;
            }
            catch
            {
                // System media controls are optional integration; their failure must not stop working playback.
                session.MediaControlsUnavailable = true;
                RetireMediaControlsCore(session);
                ReportMediaControlsFailure(session.Request.PlaybackId, "MediaControlsInitialize");
            }
        });
    }

    /// <summary>
    /// Call before disposing session.Player. This also works after session.Retired or engine._disposed is set.
    /// Only this session's handlers and controls are touched; a replacement player's controls are never cleared.
    /// </summary>
    private void RetireMediaControls(Session session) =>
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsRetire", () => RetireMediaControlsCore(session), allowDisposed: true);

    private void RetireMediaControlsCore(Session session)
    {
        var controls = session.MediaControls;
        session.MediaControls = null;
        session.MediaControlsCanSeek = false;
        session.MediaControlsDurationTicks = null;
        session.MediaControlsStatus = null;
        session.MediaControlsTitle = null;
        session.MediaControlsLastTimelineTimestamp = null;
        session.MediaControlsLastPositionTicks = null;
        if (controls is not null)
        {
            if (session.MediaControlsButtonPressed is { } buttonPressed)
                Release(session, "UnsubscribeSystemMediaButtons", () => controls.ButtonPressed -= buttonPressed);
            if (session.MediaControlsPositionRequested is { } positionRequested)
                Release(session, "UnsubscribeSystemMediaPosition", () => controls.PlaybackPositionChangeRequested -= positionRequested);
            Release(session, "DisableSystemMediaControls", () => controls.IsEnabled = false);
            Release(session, "CloseSystemMediaControls", () => controls.PlaybackStatus = MediaPlaybackStatus.Closed);
            Release(session, "ClearSystemMediaMetadata", () => controls.DisplayUpdater.ClearAll());
        }
        session.MediaControlsButtonPressed = null;
        session.MediaControlsPositionRequested = null;
    }

    private void UpdateMediaControlsCore(Session session, PlaybackContext context, PlaybackStatus status, string? title)
    {
        var controls = session.MediaControls;
        if (controls is null || session.MediaControlsUnavailable) return;
        var terminal = status is PlaybackStatus.Idle or PlaybackStatus.Stopping or PlaybackStatus.Ended or PlaybackStatus.Failed;
        if (terminal)
        {
            if (session.MediaControlsStatus != status)
            {
                controls.IsPlayEnabled = false;
                controls.IsPauseEnabled = false;
                controls.IsStopEnabled = false;
                controls.IsEnabled = false;
                controls.PlaybackStatus = status == PlaybackStatus.Idle ? MediaPlaybackStatus.Closed : MediaPlaybackStatus.Stopped;
                controls.DisplayUpdater.ClearAll();
                session.MediaControlsTitle = null;
            }
            session.MediaControlsCanSeek = false;
            session.MediaControlsStatus = status;
            return;
        }

        var durationTicks = context.Source.IsInfiniteStream == true || context.Source.RunTimeTicks is not > 0
            ? (long?)null : context.Source.RunTimeTicks.Value;
        var canSeek = context.CanSeek && durationTicks.HasValue
            && status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        var stateChanged = session.MediaControlsStatus != status;
        var rangeChanged = session.MediaControlsDurationTicks != durationTicks || session.MediaControlsCanSeek != canSeek;
        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? session.MediaControlsTitle ?? "Emby for Windows" : title.Trim();
        var titleChanged = !string.Equals(session.MediaControlsTitle, displayTitle, StringComparison.Ordinal);

        if (stateChanged)
        {
            controls.IsPlayEnabled = status == PlaybackStatus.Paused;
            controls.IsPauseEnabled = status == PlaybackStatus.Playing;
            controls.IsStopEnabled = true;
            controls.PlaybackStatus = status switch
            {
                PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Changing
            };
        }
        if (titleChanged)
        {
            controls.DisplayUpdater.Type = MediaPlaybackType.Video;
            controls.DisplayUpdater.VideoProperties.Title = displayTitle;
            controls.DisplayUpdater.Update();
            session.MediaControlsTitle = displayTitle;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = session.MediaControlsLastTimelineTimestamp.HasValue
            ? Stopwatch.GetElapsedTime(session.MediaControlsLastTimelineTimestamp.Value, now) : TimeSpan.Zero;
        var endTicks = durationTicks ?? 0;
        var positionTicks = durationTicks.HasValue ? Math.Clamp(context.PositionTicks, 0, endTicks) : 0;
        var positionJump = session.MediaControlsLastPositionTicks is { } previousPosition
            && (positionTicks < previousPosition
                || status == PlaybackStatus.Paused && positionTicks != previousPosition
                || status == PlaybackStatus.Playing && (double)positionTicks - previousPosition > elapsed.Ticks + (2 * TimeSpan.TicksPerSecond));
        if (stateChanged || rangeChanged || !session.MediaControlsLastTimelineTimestamp.HasValue
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
        if (stateChanged) controls.IsEnabled = true;
    }

    private void DispatchMediaCommand(Session session, SystemMediaTransportControls controls, NativeMediaCommand command, long? positionTicks = null)
    {
        DispatchMediaControls(session.Request.PlaybackId, "MediaControlsCommand", () =>
        {
            if (!IsActive(session) || !ReferenceEquals(session.MediaControls, controls) || !controls.IsEnabled) return;
            if (command == NativeMediaCommand.Play && !controls.IsPlayEnabled
                || command == NativeMediaCommand.Pause && !controls.IsPauseEnabled
                || command == NativeMediaCommand.Stop && !controls.IsStopEnabled) return;
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
        controls.IsNextEnabled = false;
        controls.IsPreviousEnabled = false;
        controls.IsFastForwardEnabled = false;
        controls.IsRewindEnabled = false;
        controls.IsRecordEnabled = false;
        controls.IsChannelUpEnabled = false;
        controls.IsChannelDownEnabled = false;
        // Queue navigation, playback rate, shuffle, and repeat are not advertised until explicitly routed by the product.
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

    private void ReportMediaControlsFailure(Guid playbackId, string operation)
    {
        try { Diagnostic?.Invoke(this, new(playbackId, operation, "SystemMediaControlsUnavailable")); }
        catch { }
    }

    private sealed partial class Session
    {
        public SystemMediaTransportControls? MediaControls { get; set; }
        public TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>? MediaControlsButtonPressed { get; set; }
        public TypedEventHandler<SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs>? MediaControlsPositionRequested { get; set; }
        public bool MediaControlsUnavailable { get; set; }
        public PlaybackStatus? MediaControlsStatus { get; set; }
        public string? MediaControlsTitle { get; set; }
        public long? MediaControlsDurationTicks { get; set; }
        public bool MediaControlsCanSeek { get; set; }
        public long? MediaControlsLastTimelineTimestamp { get; set; }
        public long? MediaControlsLastPositionTicks { get; set; }
    }
}
