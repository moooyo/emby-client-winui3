using System.Globalization;
using System.Threading.Channels;
using EmbyClient.Api;

namespace EmbyClient.Playback;

/// <summary>
/// Owns the engine and exactly one active Emby playback context. Transitions and reports are serialized.
/// Cancellation retires playback; cleanup uses independent bounded tokens and never reuses a retired session ID.
/// Events can arrive on any thread. Subscribers must marshal UI updates themselves.
/// </summary>
public sealed class PlaybackCoordinator : IAsyncDisposable
{
    private readonly EmbyApiClient _api;
    private readonly IPlaybackEngine _engine;
    private readonly PlaybackCoordinatorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly DeviceProfile? _deviceProfile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<WorkItem> _events = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly HashSet<Guid> _pendingEngineStops = [];
    private readonly Task _eventLoop;
    private readonly Task _timerLoop;
    private Session? _current;
    private PlaybackSelection? _lastSelection;
    private long _intent;
    private int _status;
    private int _disposed;
    private int _tickPending;

    public PlaybackCoordinator(EmbyApiClient api, IPlaybackEngine engine,
        PlaybackCoordinatorOptions? options = null, TimeProvider? timeProvider = null, DeviceProfile? deviceProfile = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(engine);
        _api = api;
        _engine = engine;
        _options = options ?? new();
        ValidateInterval(_options.ProgressInterval, nameof(_options.ProgressInterval));
        ValidateInterval(_options.CleanupTimeout, nameof(_options.CleanupTimeout));
        ValidateInterval(_options.ReportTimeout, nameof(_options.ReportTimeout));
        ValidateInterval(_options.StateChangeTimeout, nameof(_options.StateChangeTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deviceProfile = deviceProfile;
        _engine.EventReceived += OnEngineEvent;
        _eventLoop = ProcessEventsAsync();
        _timerLoop = RunTimerAsync();
    }

    public event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<PlaybackDiagnosticEventArgs>? Diagnostic;
    public PlaybackStatus Status => (PlaybackStatus)Volatile.Read(ref _status);
    public PlaybackContext? ActiveContext => CreateContext(Volatile.Read(ref _current));

    public async Task PlayAsync(PlaybackSelection selection, CancellationToken cancellationToken = default)
    {
        ValidateSelection(selection);
        ThrowIfDisposed();
        var intent = BeginIntent();
        // Once retirement has been requested, cancellation must not abandon its cleanup while waiting.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureIntent(intent, cancellationToken);
            await RetireCurrentAsync(false).ConfigureAwait(false);
            EnsureIntent(intent, cancellationToken);
            await StartCoreAsync(selection, intent, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ChangeSelectionAsync(PlaybackSelectionChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ThrowIfDisposed();
        // Capture before cancellation: the worker may retire the old session before this call gets the gate.
        var observed = Volatile.Read(ref _current) ?? throw new InvalidOperationException("There is no active playback to change.");
        CaptureEngineSnapshot(observed);
        var restorePaused = ShouldPauseAfterRestart(observed);
        var currentSourceId = observed.Source?.Id ?? observed.Selection.MediaSourceId;
        var changesSource = change.MediaSourceId is not null
            && !string.Equals(change.MediaSourceId, currentSourceId, StringComparison.Ordinal);
        var selection = observed.Selection with
        {
            MediaSourceId = change.MediaSourceId ?? currentSourceId,
            StartPositionTicks = AbsolutePosition(observed),
            // Stream indexes belong to one source. Null asks Emby to select the new source's defaults.
            AudioStreamIndex = change.AudioStreamIndex ?? (changesSource ? null : observed.Selection.AudioStreamIndex),
            SubtitleStreamIndex = change.SubtitleStreamIndex ?? (changesSource ? null : observed.Selection.SubtitleStreamIndex),
            MaxStreamingBitrate = change.MaxStreamingBitrate ?? observed.Selection.MaxStreamingBitrate,
            ForceTranscoding = change.ForceTranscoding ?? observed.Selection.ForceTranscoding
        };
        ValidateSelection(selection);
        var ownerToken = cancellationToken.CanBeCanceled ? cancellationToken : observed.OwnerCancellationToken;
        var intent = BeginIntent();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureIntent(intent, cancellationToken);
            var reportEvent = change.AudioStreamIndex.HasValue ? "AudioTrackChange"
                : change.SubtitleStreamIndex.HasValue ? "SubtitleTrackChange" : "QualityChange";
            await RetireCurrentAsync(false).ConfigureAwait(false);
            EnsureIntent(intent, cancellationToken);
            await StartCoreAsync(selection, intent, ownerToken, reportEvent, restorePaused).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        var selection = Volatile.Read(ref _lastSelection)
            ?? throw new InvalidOperationException("There is no playback to replay.");
        return PlayAsync(selection with { StartPositionTicks = 0 }, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ControlAsync((id, token) => _engine.PauseAsync(id, token), "Pause", cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        ControlAsync((id, token) => _engine.ResumeAsync(id, token), "Unpause", cancellationToken);

    public Task SetVolumeAsync(int volumeLevel, bool isMuted, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumeLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volumeLevel, 100);
        return ControlAsync((id, token) => _engine.SetVolumeAsync(id, volumeLevel, isMuted, token), "VolumeChange", cancellationToken);
    }

    public async Task SeekAsync(long absolutePositionTicks, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absolutePositionTicks);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequirePlayingSession();
            CaptureEngineSnapshot(session);
            var target = ClampPosition(absolutePositionTicks, session.Source?.RunTimeTicks);
            if (session.Request!.TimelineKind == PlaybackTimelineKind.FullSource && session.LatestSnapshot?.CanSeek == true)
            {
                PublishStatus(PlaybackStatus.Seeking, session);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
                try
                {
                    await _engine.SeekAsync(session.Id, target, operation.Token).ConfigureAwait(false);
                    CaptureEngineSnapshot(session);
                    await ReportProgressAsync(session, "TimeUpdate").ConfigureAwait(false);
                    PublishEngineStatus(session);
                }
                catch (Exception exception)
                {
                    await HandleControlFailureAsync(session, exception).ConfigureAwait(false);
                    throw;
                }
            }
            else
            {
                if (session.Source?.IsInfiniteStream == true) throw new PlaybackException("UnsupportedSeek");
                // A restart is valid for both progressive conversion and HLS with an incomplete seek window.
                var restorePaused = ShouldPauseAfterRestart(session);
                var intent = BeginIntent();
                var selection = session.Selection with { StartPositionTicks = target, ForceTranscoding = true };
                await RetireAsync(session, false).ConfigureAwait(false);
                EnsureIntent(intent, cancellationToken);
                var ownerToken = cancellationToken.CanBeCanceled ? cancellationToken : session.OwnerCancellationToken;
                await StartCoreAsync(selection, intent, ownerToken, "TimeUpdate", restorePaused).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var intent = BeginIntent();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (intent != Volatile.Read(ref _intent)) return;
            var retired = _current;
            await RetireCurrentAsync(false).ConfigureAwait(false);
            PublishStatus(_pendingEngineStops.Count == 0 ? PlaybackStatus.Idle : PlaybackStatus.Failed,
                retired, _pendingEngineStops.Count == 0 ? null : "EngineStopFailed");
        }
        finally { _gate.Release(); }
        // The parameter cancels neither stop reporting nor native/server resource cleanup.
    }

    private async Task StartCoreAsync(PlaybackSelection selection, long intent, CancellationToken cancellationToken,
        string? reportEvent = null, bool restorePaused = false)
    {
        EnsureIntent(intent, cancellationToken);
        await RetryPendingEngineStopsAsync().ConfigureAwait(false);
        EnsureIntent(intent, cancellationToken);
        var session = new Session(selection, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token), cancellationToken);
        session.RestorePaused = restorePaused;
        Volatile.Write(ref _current, session);
        Volatile.Write(ref _lastSelection, selection);
        session.CancellationRegistration = session.Lifetime.Token.Register(() => _events.Writer.TryWrite(new(session.Id, null, false)));
        PublishStatus(PlaybackStatus.Negotiating, session);
        try
        {
            EnsureIntent(intent, session.Lifetime.Token);
            var profile = BuildProfile(selection);
            var request = new PlaybackInfoRequest
            {
                MediaSourceId = selection.MediaSourceId,
                MaxStreamingBitrate = selection.MaxStreamingBitrate,
                StartTimeTicks = selection.StartPositionTicks,
                AudioStreamIndex = selection.AudioStreamIndex,
                SubtitleStreamIndex = selection.SubtitleStreamIndex,
                MaxAudioChannels = 2,
                DeviceProfile = profile,
                EnableDirectPlay = false,
                EnableDirectStream = !selection.ForceTranscoding && !selection.AudioStreamIndex.HasValue,
                EnableTranscoding = true,
                AllowVideoStreamCopy = !selection.ForceTranscoding,
                AllowAudioStreamCopy = !selection.ForceTranscoding,
                AllowInterlacedVideoStreamCopy = false,
                IsPlayback = true,
                AutoOpenLiveStream = false
            };
            var response = await _api.GetPlaybackInfoAsync(selection.ItemId, request, session.Lifetime.Token).ConfigureAwait(false);
            session.PlaySessionId = response.PlaySessionId;
            var source = PlaybackRequestFactory.SelectSource(response, selection);
            session.Source = source;
            TrackLiveStream(session, source, false);
            if (source.RequiresOpening == true)
            {
                if (!long.TryParse(selection.ItemId, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
                    throw new PlaybackException("LiveStreamItemIdOutOfRange");
                if (string.IsNullOrWhiteSpace(source.OpenToken)) throw new PlaybackException("MissingOpenToken");
                var opened = await _api.OpenLiveStreamAsync(new LiveStreamRequest
                {
                    ItemId = itemId,
                    OpenToken = source.OpenToken,
                    PlaySessionId = session.PlaySessionId,
                    DeviceProfile = profile,
                    MaxStreamingBitrate = selection.MaxStreamingBitrate,
                    StartTimeTicks = selection.StartPositionTicks,
                    AudioStreamIndex = selection.AudioStreamIndex,
                    SubtitleStreamIndex = selection.SubtitleStreamIndex,
                    MaxAudioChannels = 2,
                    EnableDirectPlay = false,
                    EnableDirectStream = request.EnableDirectStream,
                    EnableTranscoding = true,
                    AllowVideoStreamCopy = request.AllowVideoStreamCopy,
                    AllowAudioStreamCopy = request.AllowAudioStreamCopy
                }, session.Lifetime.Token).ConfigureAwait(false);
                source = opened.MediaSource ?? throw new PlaybackException("MissingOpenedSource");
                session.Source = source;
                TrackLiveStream(session, source, true);
            }
            session.Lifetime.Token.ThrowIfCancellationRequested();
            if (source.RunTimeTicks is > 0 && selection.StartPositionTicks >= source.RunTimeTicks)
            {
                // Resume from another edition cannot safely address beyond this source's duration.
                throw new PlaybackException("ResumePositionOutsideSource");
            }
            var engineRequest = PlaybackRequestFactory.Create(_api, session.Id, session.PlaySessionId!, source, selection);
            session.Selection = selection with
            {
                MediaSourceId = source.Id
            };
            Volatile.Write(ref session.Request, engineRequest);
            Volatile.Write(ref _lastSelection, session.Selection);
            PublishStatus(PlaybackStatus.Opening, session);
            session.EngineOpenAttempted = true;
            await _engine.OpenAsync(engineRequest, session.Lifetime.Token).WaitAsync(session.Lifetime.Token).ConfigureAwait(false);
            session.OpenCompleted = true;
            session.ActuallyStarted = true;
            CaptureEngineSnapshot(session);
            session.Lifetime.Token.ThrowIfCancellationRequested();
            if (session.LatestSnapshot is null || session.LatestSnapshot.State is PlaybackEngineState.Idle
                or PlaybackEngineState.Opening or PlaybackEngineState.Stopped or PlaybackEngineState.Failed)
                throw new PlaybackException("EngineDidNotStart");
            session.StartReportAttempted = true;
            var startSnapshot = session.LatestSnapshot;
            using (var timeout = new CancellationTokenSource(_options.ReportTimeout, _timeProvider))
                await _api.ReportPlaybackStartAsync(CreateStartReport(session, startSnapshot), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            session.StartReported = true;
            session.LastReportedSnapshot = startSnapshot;
            session.Lifetime.Token.ThrowIfCancellationRequested();
            if (restorePaused)
            {
                await RestorePauseAsync(session).ConfigureAwait(false);
                Volatile.Write(ref session.RestorePaused, false);
            }
            PublishEngineStatus(session);
            if (reportEvent is not null) await ReportProgressAsync(session, reportEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var code = ErrorCode(exception);
            var retry = !selection.ForceTranscoding && IsFallbackCode(code)
                && !session.Lifetime.IsCancellationRequested && intent == Volatile.Read(ref _intent);
            CaptureEngineSnapshot(session);
            var fallback = session.Selection with { StartPositionTicks = AbsolutePosition(session), ForceTranscoding = true };
            var fallbackPaused = restorePaused || ShouldPauseAfterRestart(session);
            await RetireAsync(session, exception is not OperationCanceledException).ConfigureAwait(false);
            if (retry)
            {
                EmitDiagnostic(session.Id, "Fallback", code);
                await StartCoreAsync(fallback, intent, cancellationToken, reportEvent, fallbackPaused).ConfigureAwait(false);
                return;
            }
            PublishStatus(exception is OperationCanceledException ? PlaybackStatus.Idle : PlaybackStatus.Failed, session, code);
            if (exception is OperationCanceledException) throw;
            throw new PlaybackException(code);
        }
    }

    private async Task RestorePauseAsync(Session session)
    {
        CaptureEngineSnapshot(session);
        if (session.LatestSnapshot is { State: PlaybackEngineState.Paused } alreadyPaused)
        {
            if (session.LastReportedSnapshot?.State != PlaybackEngineState.Paused)
                await ReportProgressAsync(session, "Pause", alreadyPaused).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource<PlaybackEngineSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref session.PauseCompletion, completion);
        using var timeout = new CancellationTokenSource(_options.StateChangeTimeout, _timeProvider);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token, timeout.Token);
        try
        {
            // Call the adapter directly: public PauseAsync would try to acquire the transition gate again.
            await _engine.PauseAsync(session.Id, operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
            CaptureEngineSnapshot(session);
            if (session.LatestSnapshot is { State: PlaybackEngineState.Paused } snapshot) completion.TrySetResult(snapshot);
            // The adapter can acknowledge a command before the native playback state has changed.
            // OnEngineEvent completes this signal directly, without waiting for the serialized worker.
            var pausedSnapshot = await completion.Task.WaitAsync(operation.Token).ConfigureAwait(false);
            await ReportProgressAsync(session, "Pause", pausedSnapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !session.Lifetime.IsCancellationRequested)
        {
            throw new PlaybackException("PauseNotConfirmed");
        }
        finally { Interlocked.CompareExchange(ref session.PauseCompletion, null, completion); }
    }

    private DeviceProfile BuildProfile(PlaybackSelection selection)
    {
        var profile = _deviceProfile ?? ConservativeDeviceProfile.Create(selection.MaxStreamingBitrate, _options.EnableExternalWebVtt);
        profile = profile with
        {
            MaxStreamingBitrate = selection.MaxStreamingBitrate,
            MaxStaticBitrate = selection.MaxStreamingBitrate,
            TranscodingProfiles = profile.TranscodingProfiles?.Select(value => value with { CopyTimestamps = false }).ToArray()
        };
        if (selection.ForceTranscoding)
        {
            profile = profile with
            {
                SubtitleProfiles = ConservativeDeviceProfile.Create(selection.MaxStreamingBitrate).SubtitleProfiles
            };
        }
        return profile;
    }

    private static void TrackLiveStream(Session session, MediaSourceInfo source, bool explicitlyOpened)
    {
        var mustClose = source.RequiresClosing == true || explicitlyOpened && source.RequiresClosing is null;
        if (!mustClose) return;
        if (string.IsNullOrWhiteSpace(source.LiveStreamId))
        {
            if (source.RequiresOpening == true) return;
            throw new PlaybackException("MissingLiveStreamId");
        }
        session.LiveStreamIds.Add(source.LiveStreamId);
    }

    private async Task ControlAsync(Func<Guid, CancellationToken, Task> action, string eventName, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequirePlayingSession();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
            try
            {
                await action(session.Id, operation.Token).ConfigureAwait(false);
                CaptureEngineSnapshot(session);
                await ReportProgressAsync(session, eventName).ConfigureAwait(false);
                PublishEngineStatus(session);
            }
            catch (Exception exception)
            {
                await HandleControlFailureAsync(session, exception).ConfigureAwait(false);
                if (exception is OperationCanceledException) throw;
                throw new PlaybackException(ErrorCode(exception));
            }
        }
        finally { _gate.Release(); }
    }

    private async Task HandleControlFailureAsync(Session session, Exception exception)
    {
        await RetireAsync(session, exception is not OperationCanceledException).ConfigureAwait(false);
        PublishStatus(exception is OperationCanceledException ? PlaybackStatus.Idle : PlaybackStatus.Failed, session, ErrorCode(exception));
    }

    private void OnEngineEvent(object? sender, PlaybackEngineEventArgs args)
    {
        var session = Volatile.Read(ref _current);
        if (session is null || session.Id != args.Snapshot.PlaybackId || Volatile.Read(ref _disposed) != 0) return;
        AcceptSnapshot(session, args.Snapshot);
        if (args.Snapshot.State == PlaybackEngineState.Playing) Volatile.Write(ref session.ActuallyStarted, true);
        var pauseCompletion = Volatile.Read(ref session.PauseCompletion);
        if (args.Snapshot.State == PlaybackEngineState.Paused) pauseCompletion?.TrySetResult(args.Snapshot);
        else if (args.Kind == PlaybackEngineEventKind.Failed)
            pauseCompletion?.TrySetException(new PlaybackException(SafeCode(args.ErrorCode ?? "EngineFailed")));
        else if (args.Kind == PlaybackEngineEventKind.Ended)
            pauseCompletion?.TrySetException(new PlaybackException("PlaybackEndedBeforePause"));
        // Opening/initial-seek transitions precede the Start report and must not be replayed as later progress.
        // A paused restart reports its actual Start and Pause explicitly; do not replay intermediate Playing events afterward.
        if (args.Kind == PlaybackEngineEventKind.StateChanged
            && (!Volatile.Read(ref session.OpenCompleted) || Volatile.Read(ref session.RestorePaused))) return;
        // Frame-rate position updates are sampled by the timer instead of accumulating in a channel.
        if (args.Kind != PlaybackEngineEventKind.PositionChanged)
            _events.Writer.TryWrite(new(session.Id, args, false));
    }

    private async Task RunTimerAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.ProgressInterval, _timeProvider, _shutdown.Token).ConfigureAwait(false);
                var session = Volatile.Read(ref _current);
                if (session is not null && Interlocked.Exchange(ref _tickPending, 1) == 0)
                {
                    if (!_events.Writer.TryWrite(new(session.Id, null, true))) Interlocked.Exchange(ref _tickPending, 0);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (item.IsTimer) Interlocked.Exchange(ref _tickPending, 0);
                await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                try
                {
                    var session = _current;
                    if (session is null || session.Id != item.PlaybackId) continue;
                    if (session.Lifetime.IsCancellationRequested)
                    {
                        await RetireAsync(session, false).ConfigureAwait(false);
                        PublishStatus(PlaybackStatus.Idle, session);
                        continue;
                    }
                    if (item.EngineEvent?.Kind == PlaybackEngineEventKind.Ended)
                    {
                        await RetireAsync(session, false).ConfigureAwait(false);
                        PublishStatus(PlaybackStatus.Ended, session);
                    }
                    else if (item.EngineEvent?.Kind == PlaybackEngineEventKind.Failed)
                    {
                        var code = SafeCode(item.EngineEvent.ErrorCode ?? "EngineFailed");
                        var intent = Volatile.Read(ref _intent);
                        var fallback = session.Selection with { StartPositionTicks = AbsolutePosition(session), ForceTranscoding = true };
                        var restorePaused = ShouldPauseAfterRestart(session);
                        var retry = !session.Selection.ForceTranscoding && IsFallbackCode(code);
                        await RetireAsync(session, true).ConfigureAwait(false);
                        if (retry && intent == Volatile.Read(ref _intent))
                        {
                            EmitDiagnostic(session.Id, "Fallback", code);
                            await StartCoreAsync(fallback, intent, session.OwnerCancellationToken, restorePaused: restorePaused).ConfigureAwait(false);
                        }
                        else PublishStatus(PlaybackStatus.Failed, session, code);
                    }
                    else if (session.StartReported)
                    {
                        CaptureEngineSnapshot(session);
                        if (item.IsTimer) await ReportProgressAsync(session, "TimeUpdate").ConfigureAwait(false);
                        else if (HasStateChange(session, item.EngineEvent?.Snapshot))
                            await ReportProgressAsync(session, StateEventName(session, item.EngineEvent?.Snapshot), item.EngineEvent?.Snapshot).ConfigureAwait(false);
                        PublishEngineStatus(session);
                    }
                }
                catch (Exception exception)
                {
                    EmitDiagnostic(item.PlaybackId, "EventProcessing", ErrorCode(exception));
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ReportProgressAsync(Session session, string eventName, PlaybackEngineSnapshot? reportedSnapshot = null)
    {
        if (_current != session || !session.StartReported || session.Retired || session.Lifetime.IsCancellationRequested) return;
        reportedSnapshot ??= session.LatestSnapshot;
        var start = CreateStartReport(session, reportedSnapshot);
        var report = new PlaybackProgressInfo
        {
            ItemId = start.ItemId, MediaSourceId = start.MediaSourceId, PlaySessionId = start.PlaySessionId,
            LiveStreamId = start.LiveStreamId, PositionTicks = start.PositionTicks, RunTimeTicks = start.RunTimeTicks,
            CanSeek = start.CanSeek, IsPaused = start.IsPaused, IsMuted = start.IsMuted, VolumeLevel = start.VolumeLevel,
            AudioStreamIndex = start.AudioStreamIndex, SubtitleStreamIndex = start.SubtitleStreamIndex,
            PlayMethod = start.PlayMethod, PlaybackRate = start.PlaybackRate, EventName = eventName
        };
        try
        {
            // Do not cancel an in-flight report on retirement: finish its bounded send before posting Stopped.
            using var timeout = new CancellationTokenSource(_options.ReportTimeout, _timeProvider);
            await _api.ReportPlaybackProgressAsync(report, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            session.LastReportedSnapshot = reportedSnapshot;
        }
        catch (Exception exception) { EmitDiagnostic(session.Id, "Progress", ErrorCode(exception)); }
    }

    private PlaybackStartInfo CreateStartReport(Session session, PlaybackEngineSnapshot? snapshot = null)
    {
        snapshot ??= session.LatestSnapshot;
        return new PlaybackStartInfo
        {
            ItemId = session.Selection.ItemId,
            MediaSourceId = session.Source?.Id,
            PlaySessionId = session.PlaySessionId,
            LiveStreamId = session.Source?.LiveStreamId,
            PositionTicks = AbsolutePosition(session, snapshot),
            RunTimeTicks = session.Request?.ItemRunTimeTicks,
            CanSeek = snapshot?.CanSeek == true || session.Source?.IsInfiniteStream != true && session.Request?.DeliveryMethod == PlaybackDeliveryMethod.Transcode,
            IsPaused = snapshot?.State == PlaybackEngineState.Paused,
            IsMuted = snapshot?.IsMuted ?? false,
            VolumeLevel = Math.Clamp(snapshot?.VolumeLevel ?? 100, 0, 100),
            AudioStreamIndex = session.Request?.AudioStreamIndex,
            SubtitleStreamIndex = session.Request?.SubtitleStreamIndex,
            PlayMethod = session.Request?.DeliveryMethod == PlaybackDeliveryMethod.Transcode ? "Transcode" : "DirectStream",
            PlaybackRate = snapshot?.PlaybackRate is > 0 and < 16 ? snapshot.PlaybackRate : 1
        };
    }

    private Task RetireCurrentAsync(bool failed) => _current is { } session ? RetireAsync(session, failed) : Task.CompletedTask;

    private async Task RetireAsync(Session session, bool failed)
    {
        if (session.Retired) return;
        CaptureEngineSnapshot(session);
        session.FinalPositionTicks = AbsolutePosition(session);
        session.Retired = true;
        PublishStatus(PlaybackStatus.Stopping, session);
        if (_current == session) Volatile.Write(ref _current, null);
        CancelSession(session);
        if (session.EngineOpenAttempted)
        {
            _pendingEngineStops.Add(session.Id);
            if (await CleanupStepAsync(session.Id, "EngineStop", token => _engine.StopAsync(session.Id, token)).ConfigureAwait(false))
                _pendingEngineStops.Remove(session.Id);
        }
        if ((session.ActuallyStarted || session.StartReportAttempted) && !string.IsNullOrWhiteSpace(session.PlaySessionId))
        {
            await CleanupStepAsync(session.Id, "StopReport", token => _api.ReportPlaybackStoppedAsync(new PlaybackStopInfo
            {
                ItemId = session.Selection.ItemId,
                MediaSourceId = session.Source?.Id,
                PlaySessionId = session.PlaySessionId,
                LiveStreamId = session.Source?.LiveStreamId,
                PositionTicks = session.FinalPositionTicks,
                Failed = failed
            }, token)).ConfigureAwait(false);
        }
        if (session.Request?.DeliveryMethod == PlaybackDeliveryMethod.Transcode && !string.IsNullOrWhiteSpace(session.PlaySessionId))
            await CleanupStepAsync(session.Id, "StopEncoding", token => _api.StopActiveEncodingsAsync(session.PlaySessionId, token)).ConfigureAwait(false);
        foreach (var liveStreamId in session.LiveStreamIds)
            await CleanupStepAsync(session.Id, "CloseLiveStream", token => _api.CloseLiveStreamAsync(liveStreamId, token)).ConfigureAwait(false);
        session.CancellationRegistration.Dispose();
        session.Lifetime.Dispose();
    }

    private async Task<bool> CleanupStepAsync(Guid id, string operation, Func<CancellationToken, Task> cleanup)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.CleanupTimeout, _timeProvider);
            await cleanup(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            EmitDiagnostic(id, operation, ErrorCode(exception));
            return false;
        }
    }

    private async Task RetryPendingEngineStopsAsync()
    {
        foreach (var id in _pendingEngineStops.ToArray())
            if (await CleanupStepAsync(id, "EngineStopRetry", token => _engine.StopAsync(id, token)).ConfigureAwait(false))
                _pendingEngineStops.Remove(id);
        if (_pendingEngineStops.Count != 0)
        {
            PublishStatus(PlaybackStatus.Failed, null, "EngineStopFailed");
            throw new PlaybackException("EngineStopFailed");
        }
    }

    private void CaptureEngineSnapshot(Session session)
    {
        if (session.Retired) return;
        var snapshot = _engine.Snapshot;
        if (snapshot?.PlaybackId == session.Id) AcceptSnapshot(session, snapshot);
    }

    private static void AcceptSnapshot(Session session, PlaybackEngineSnapshot snapshot)
    {
        var previous = Volatile.Read(ref session.LatestSnapshot);
        if (snapshot.State is PlaybackEngineState.Stopped or PlaybackEngineState.Failed
            && snapshot.PositionTicks == 0 && previous is not null)
            snapshot = snapshot with { PositionTicks = previous.PositionTicks };
        Volatile.Write(ref session.LatestSnapshot, snapshot);
        if (snapshot.State is PlaybackEngineState.Playing or PlaybackEngineState.Paused)
            Volatile.Write(ref session.LastPlaybackWasPaused, snapshot.State == PlaybackEngineState.Paused);
    }

    // AutoPlay=false can produce a Paused snapshot during initialization. It is not a user's pause intent.
    // A requested paused restart remains authoritative even before its replacement source starts playing.
    private static bool ShouldPauseAfterRestart(Session session) => Volatile.Read(ref session.RestorePaused)
        || Volatile.Read(ref session.ActuallyStarted) && Volatile.Read(ref session.LastPlaybackWasPaused);

    private static long AbsolutePosition(Session session, PlaybackEngineSnapshot? snapshot = null)
    {
        if (session.Retired) return session.FinalPositionTicks;
        snapshot ??= Volatile.Read(ref session.LatestSnapshot);
        var request = Volatile.Read(ref session.Request);
        if (!session.OpenCompleted || snapshot is null || request is null) return session.Selection.StartPositionTicks;
        var relative = Math.Max(0, snapshot.PositionTicks);
        var offset = request.TimelineOffsetTicks;
        var absolute = relative > long.MaxValue - offset ? long.MaxValue : offset + relative;
        return ClampPosition(absolute, request.ItemRunTimeTicks);
    }

    private static long ClampPosition(long ticks, long? duration) => duration is > 0 ? Math.Min(ticks, duration.Value) : ticks;

    private static PlaybackContext? CreateContext(Session? session)
    {
        if (session is null || Volatile.Read(ref session.Request) is not { } request || session.Source is null
            || string.IsNullOrWhiteSpace(session.PlaySessionId)) return null;
        return new PlaybackContext
        {
            PlaybackId = session.Id,
            Selection = session.Selection,
            Source = session.Source,
            PlaySessionId = session.PlaySessionId,
            DeliveryMethod = request.DeliveryMethod,
            TimelineKind = request.TimelineKind,
            TimelineOffsetTicks = request.TimelineOffsetTicks,
            PositionTicks = AbsolutePosition(session),
            CanSeek = session.LatestSnapshot?.CanSeek == true || session.Source.IsInfiniteStream != true
                && request.DeliveryMethod == PlaybackDeliveryMethod.Transcode
        };
    }

    private void PublishEngineStatus(Session session)
    {
        if (_current != session || session.Retired) return;
        var status = session.LatestSnapshot?.State switch
        {
            PlaybackEngineState.Paused => PlaybackStatus.Paused,
            PlaybackEngineState.Buffering => PlaybackStatus.Buffering,
            PlaybackEngineState.Seeking => PlaybackStatus.Seeking,
            PlaybackEngineState.Ended => PlaybackStatus.Ended,
            PlaybackEngineState.Failed => PlaybackStatus.Failed,
            _ => PlaybackStatus.Playing
        };
        PublishStatus(status, session);
    }

    private void PublishStatus(PlaybackStatus status, Session? session, string? code = null)
    {
        Volatile.Write(ref _status, (int)status);
        try { StatusChanged?.Invoke(this, new(status, CreateContext(session), code)); }
        catch { /* Consumer UI failures must not interrupt server resource cleanup. */ }
    }

    private void EmitDiagnostic(Guid id, string operation, string code)
    {
        try { Diagnostic?.Invoke(this, new(id, operation, SafeCode(code))); }
        catch { /* Diagnostic subscribers do not control the playback lifecycle. */ }
    }

    private static bool HasStateChange(Session session, PlaybackEngineSnapshot? current = null)
    {
        current ??= session.LatestSnapshot;
        var previous = session.LastReportedSnapshot;
        return current?.State != previous?.State || current?.VolumeLevel != previous?.VolumeLevel
            || current?.IsMuted != previous?.IsMuted || current?.PlaybackRate != previous?.PlaybackRate;
    }

    private static string StateEventName(Session session, PlaybackEngineSnapshot? current = null)
    {
        current ??= session.LatestSnapshot;
        var previous = session.LastReportedSnapshot;
        if (current?.State == PlaybackEngineState.Paused && previous?.State != PlaybackEngineState.Paused) return "Pause";
        if (current?.State == PlaybackEngineState.Playing && previous?.State == PlaybackEngineState.Paused) return "Unpause";
        if (current?.VolumeLevel != previous?.VolumeLevel || current?.IsMuted != previous?.IsMuted) return "VolumeChange";
        if (current?.PlaybackRate != previous?.PlaybackRate) return "PlaybackRateChange";
        return "StateChange";
    }

    private Session RequirePlayingSession() => _current is { StartReported: true, Retired: false } session
        && !session.Lifetime.IsCancellationRequested ? session : throw new InvalidOperationException("There is no active playback.");

    private long BeginIntent()
    {
        var intent = Interlocked.Increment(ref _intent);
        if (Volatile.Read(ref _current) is { } session) CancelSession(session);
        return intent;
    }

    private static void CancelSession(Session session)
    {
        try { session.Lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void EnsureIntent(long intent, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (intent != Volatile.Read(ref _intent)) throw new OperationCanceledException("Playback was superseded by a newer request.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static void ValidateSelection(PlaybackSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ItemId);
        ArgumentOutOfRangeException.ThrowIfNegative(selection.StartPositionTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selection.MaxStreamingBitrate);
        if (selection.AudioStreamIndex is < 0 || selection.SubtitleStreamIndex is < -1)
            throw new ArgumentOutOfRangeException(nameof(selection), "Stream indexes are invalid.");
        if (selection.MediaSourceId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(selection.MediaSourceId);
    }

    private static void ValidateInterval(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(name);
    }

    private static bool IsFallbackCode(string code) => code is "UnsupportedTrack" or "UnsupportedFormat" or "UnsupportedSubtitle";
    private static string SafeCode(string code) => code.Length is > 0 and <= 64 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        ? code : "PlaybackFailed";
    private static string ErrorCode(Exception exception) => exception switch
    {
        PlaybackException playback => SafeCode(playback.ErrorCode),
        OperationCanceledException => "Cancelled",
        TimeoutException => "Timeout",
        EmbyApiException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "AccessRestricted",
        EmbyApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized, ApplicationErrorCode: "ParentalControl" } => "AccessRestricted",
        EmbyApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "AuthenticationExpired",
        EmbyApiException => "ServerRejected",
        EmbyProtocolException => "InvalidServerResponse",
        EmbyTransportException => "ServerUnavailable",
        _ => "PlaybackFailed"
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BeginIntent();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetireCurrentAsync(false).ConfigureAwait(false);
            _engine.EventReceived -= OnEngineEvent;
            await CleanupStepAsync(Guid.Empty, "EngineDispose", _ => _engine.DisposeAsync().AsTask()).ConfigureAwait(false);
            _pendingEngineStops.Clear();
            PublishStatus(PlaybackStatus.Idle, null);
        }
        finally
        {
            _shutdown.Cancel();
            _events.Writer.TryComplete();
            _gate.Release();
        }
        await Task.WhenAll(_eventLoop, _timerLoop).ConfigureAwait(false);
        _shutdown.Dispose();
        // Do not dispose the gate while an already-issued public call may still be awaiting it.
    }

    private sealed class Session(PlaybackSelection selection, CancellationTokenSource lifetime, CancellationToken ownerCancellationToken)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal PlaybackSelection Selection = selection;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal CancellationToken OwnerCancellationToken { get; } = ownerCancellationToken;
        internal CancellationTokenRegistration CancellationRegistration;
        internal string? PlaySessionId;
        internal MediaSourceInfo? Source;
        internal PlaybackEngineRequest? Request;
        internal PlaybackEngineSnapshot? LatestSnapshot;
        internal PlaybackEngineSnapshot? LastReportedSnapshot;
        internal TaskCompletionSource<PlaybackEngineSnapshot>? PauseCompletion;
        internal HashSet<string> LiveStreamIds { get; } = new(StringComparer.Ordinal);
        internal bool EngineOpenAttempted;
        internal bool OpenCompleted;
        internal bool ActuallyStarted;
        internal bool StartReportAttempted;
        internal bool StartReported;
        internal bool RestorePaused;
        internal bool LastPlaybackWasPaused;
        internal bool Retired;
        internal long FinalPositionTicks;
    }

    private readonly record struct WorkItem(Guid PlaybackId, PlaybackEngineEventArgs? EngineEvent, bool IsTimer);
}
