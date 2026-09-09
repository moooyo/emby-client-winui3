using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using EmbyClient.Api;
using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using NativeHttpClient = Windows.Web.Http.HttpClient;
using NativeHttpResponse = Windows.Web.Http.HttpResponseMessage;

namespace EmbyClient.App.Playback;

/// <summary>
/// Keeps one Windows media player for the engine lifetime while each playback owns its sources, subscriptions,
/// and authenticated transports. Native objects are touched only on the UI dispatcher.
/// </summary>
public sealed partial class NativePlaybackEngine(
    DispatcherQueue dispatcher,
    MediaPlayerElement element,
    int initialVolumeLevel = 100,
    bool initialMuted = false) : IPlaybackEngine
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(45);
    private Session? _current;
    private PlaybackEngineSnapshot? _snapshot;
    private MediaPlayer? _playerOwner;
    private Guid _playerOwnerPlaybackId;
    private long _playerOwnerUseSequence;
    private Task? _disposalTask;
    private bool _disposed;
    private readonly SemaphoreSlim _openingTransition = new(1, 1);
    private int _volumeLevel = initialVolumeLevel is >= 0 and <= 100
        ? initialVolumeLevel : throw new ArgumentOutOfRangeException(nameof(initialVolumeLevel));
    private bool _isMuted = initialMuted;

    public event EventHandler<PlaybackEngineEventArgs>? EventReceived;
    public event EventHandler<PlaybackDiagnosticEventArgs>? Diagnostic;
    public PlaybackEngineSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public async Task OpenAsync(PlaybackEngineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.InitialPositionTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(request.TimelineOffsetTicks);
        if (request.PlaybackId == Guid.Empty || !IsHttp(request.MediaUri)) throw new PlaybackException("UnsupportedFormat");
        cancellationToken.ThrowIfCancellationRequested();

        Session session;
        await _openingTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await OnDispatcherAsync(() => _current).ConfigureAwait(false);
            if (previous is not null) await RetireAsync(previous).ConfigureAwait(false);
            session = await OnDispatcherAsync(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var created = new Session(request, cancellationToken);
                _current = created;
                created.Cancellation = cancellationToken.Register(() =>
                {
                    created.Started.TrySetCanceled(cancellationToken);
                    Queue(() => RetireNative(created));
                });
                created.LoadTask = LoadAsync(created);
                if (!created.Retired) Publish(created, PlaybackEngineEventKind.StateChanged, PlaybackEngineState.Opening);
                return created;
            }).ConfigureAwait(false);
        }
        finally { _openingTransition.Release(); }
        try
        {
            await session.Started.Task.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RetireAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            await RetireAsync(session).ConfigureAwait(false);
            throw new PlaybackException("PlaybackOpenTimeout");
        }
        catch (PlaybackException)
        {
            await RetireAsync(session).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RetireAsync(session).ConfigureAwait(false);
            throw new PlaybackException("NativePlaybackFailure");
        }
    }

    public Task PauseAsync(Guid playbackId, CancellationToken cancellationToken = default) =>
        ControlAsync(playbackId, session => session.Player!.Pause(), cancellationToken);

    public Task ResumeAsync(Guid playbackId, CancellationToken cancellationToken = default) =>
        ControlAsync(playbackId, session => session.Player!.Play(), cancellationToken);

    public async Task SeekAsync(Guid playbackId, long positionTicks, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(positionTicks);
        cancellationToken.ThrowIfCancellationRequested();
        Task? seek = await OnPlayerDispatcherAsync(() =>
        {
            var session = Find(playbackId);
            if (session?.Player is null) return null;
            if (!MediaDeliveryClassifier.CanSeekInPlace(session.Request, session.NativeSession!.CanSeek))
                throw new PlaybackException("SeekNotSupported");
            if (session.NativeSession.Position.Ticks == positionTicks) return Task.CompletedTask;
            session.SeekCompletion?.TrySetCanceled();
            session.SeekCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.UserSeeking = true;
            Publish(session, PlaybackEngineEventKind.StateChanged, PlaybackEngineState.Seeking);
            session.NativeSession.Position = TimeSpan.FromTicks(positionTicks);
            return session.SeekCompletion.Task;
        }).ConfigureAwait(false);
        if (seek is not null)
        {
            try { await seek.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new PlaybackException("SeekTimedOut"); }
        }
    }

    public Task SetVolumeAsync(Guid playbackId, int volumeLevel, bool isMuted, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumeLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volumeLevel, 100);
        return ControlAsync(playbackId, session =>
        {
            session.Player!.Volume = volumeLevel / 100d;
            session.Player.IsMuted = isMuted;
            _volumeLevel = volumeLevel;
            _isMuted = isMuted;
            Publish(session, PlaybackEngineEventKind.StateChanged);
        }, cancellationToken);
    }

    public async Task StopAsync(Guid playbackId, CancellationToken cancellationToken = default)
    {
        var session = await OnDispatcherAsync(() => _current?.Request.PlaybackId == playbackId ? _current : null).ConfigureAwait(false);
        if (session is not null) await RetireAsync(session).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var disposal = await OnDispatcherAsync(() =>
        {
            if (_disposalTask is not null) return _disposalTask;
            _disposed = true;
            return _disposalTask = DisposeCoreAsync(_current);
        }).ConfigureAwait(false);
        await disposal.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync(Session? session)
    {
        // Publish the shared completion before cancellation or native events can reenter DisposeAsync.
        await Task.Yield();
        try
        {
            if (session is not null) await RetireAsync(session).ConfigureAwait(false);
        }
        finally
        {
            await OnDispatcherAsync(ClosePlayerOwner).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync(Session session)
    {
        // Publish the tracked task and cancellation registration before Stop can drain this session.
        await Task.Yield();
        try
        {
            ValidateSubtitleDelivery(session.Request);
            if (session.Request.DeliveryMethod == PlaybackDeliveryMethod.Transcode && HasPreservedTimestamps(session.Request.MediaUri))
                throw new PlaybackException("UnsupportedFormat");

            if (MediaDeliveryClassifier.IsHls(session.Request))
            {
                var creationTask = await OnDispatcherAsync(() =>
                {
                    EnsureActive(session);
                    NativeHttpClient? adaptiveHttp = null;
                    IDisposable? filterOwner = null;
                    CreateAdaptiveHttpClient(session.Request, ref adaptiveHttp, ref filterOwner);
                    session.AdaptiveFilterOwner = filterOwner;
                    if (adaptiveHttp is null)
                    {
                        session.AdaptiveFilter = new ScopedAdaptiveHttpFilter(session.Transport,
                            code => Queue(() => Fail(session, code)));
                        adaptiveHttp = new NativeHttpClient(session.AdaptiveFilter);
                    }
                    session.AdaptiveHttp = adaptiveHttp;
                    return AdaptiveMediaSource.CreateFromUriAsync(session.Request.MediaUri, session.AdaptiveHttp)
                        .AsTask(session.Lifetime.Token);
                }).ConfigureAwait(false);
                var result = await creationTask.ConfigureAwait(false);
                await OnDispatcherAsync(() =>
                {
                    var adaptive = result.MediaSource;
                    var creationResponse = result.HttpResponseMessage;
                    if (!IsActive(session))
                    {
                        Release(session, "CloseLateAdaptiveSource", () => adaptive?.Dispose());
                        Release(session, "CloseLateAdaptiveResponse", () => creationResponse?.Dispose());
                        throw new OperationCanceledException(session.Lifetime.Token);
                    }
                    // The creation result is not closable, but its source and HTTP response both are.
                    // Keep the response alive until its adaptive consumer retires, then close it explicitly.
                    session.AdaptiveSource = adaptive;
                    session.AdaptiveCreationResponse = creationResponse;
                    if (result.Status != AdaptiveMediaSourceCreationStatus.Success || adaptive is null)
                        throw new PlaybackException("UnsupportedFormat");
                    session.MediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptive);
                }).ConfigureAwait(false);
            }
            else
            {
                var relay = await SessionHttpRelay.OpenAsync(session.Transport, session.Request.MediaUri,
                    session.Request.Source.Container, session.Lifetime.Token).ConfigureAwait(false);
                await OnDispatcherAsync(() =>
                {
                    // Retain ownership even when cancellation races the completed open; DrainAsync will close the relay.
                    session.DirectRelay = relay;
                    EnsureActive(session);
                    session.MediaSource = MediaSource.CreateFromUri(relay.LocalUri);
                }).ConfigureAwait(false);
            }

            if (session.Request.ExternalSubtitleUri is { } subtitleUri)
            {
                session.SubtitleTransport = new ScopedMediaTransport(subtitleUri,
                    session.Request.ExternalSubtitleHeaders, session.Lifetime.Token);
                var subtitle = await session.SubtitleTransport.DownloadAsync(subtitleUri,
                    maximumBytes: 4 * 1024 * 1024, ct: session.Lifetime.Token).ConfigureAwait(false);
                if (!Encoding.UTF8.GetString(subtitle.Data).TrimStart('\uFEFF').StartsWith("WEBVTT", StringComparison.Ordinal))
                    throw new PlaybackException("UnsupportedSubtitle");
                var subtitleWrite = await OnDispatcherAsync(() =>
                {
                    EnsureActive(session);
                    var stream = new InMemoryRandomAccessStream();
                    session.SubtitleStream = stream;
                    return stream.WriteAsync(subtitle.Data.AsBuffer()).AsTask(session.Lifetime.Token);
                }).ConfigureAwait(false);
                await subtitleWrite.ConfigureAwait(false);
                await OnDispatcherAsync(() =>
                {
                    EnsureActive(session);
                    session.SubtitleStream!.Seek(0);
                    session.TimedText = TimedTextSource.CreateFromStream(session.SubtitleStream);
                    session.TimedTextResolved = (_, args) => Queue(() => OnTimedTextResolved(session, args));
                    session.TimedText.Resolved += session.TimedTextResolved;
                    session.MediaSource!.ExternalTimedTextSources.Add(session.TimedText);
                }).ConfigureAwait(false);
            }

            await OnDispatcherAsync(() => BindPlayer(session)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.Started.TrySetCanceled(session.Lifetime.Token);
        }
        catch (PlaybackException exception)
        {
            await OnDispatcherAsync(() => Fail(session, exception.ErrorCode)).ConfigureAwait(false);
        }
        catch
        {
            await OnDispatcherAsync(() => Fail(session, "NativePlaybackFailure")).ConfigureAwait(false);
        }
    }

    private void BindPlayer(Session session)
    {
        EnsureActive(session);
        session.Item = new MediaPlaybackItem(session.MediaSource!);
        session.TracksChanged = (_, _) => Queue(() =>
        {
            if (!IsActive(session)) return;
            ApplySubtitles(session);
        });
        session.Item.TimedMetadataTracksChanged += session.TracksChanged;
        MediaPlayer? player = null;
        var ownsPlayer = true;
        CreateSessionPlayer(session.Request, ref player, ref ownsPlayer);
        EnsureActive(session);
        if (player is null)
        {
            player = GetPlayerOwner(session);
            ownsPlayer = false;
        }
        session.OwnsPlayer = ownsPlayer;
        session.Player = player;
        session.Player.AutoPlay = false;
        session.Player.Volume = _volumeLevel / 100d;
        session.Player.IsMuted = _isMuted;
        session.NativeSession = session.Player.PlaybackSession;
        session.Player.CommandManager.IsEnabled = false;
        InitializeMediaControls(session);
        session.Opened = (_, _) => Queue(() => OnOpened(session));
        session.Ended = (_, _) => Queue(() =>
        {
            if (!IsActive(session)) return;
            Publish(session, PlaybackEngineEventKind.Ended, PlaybackEngineState.Ended);
            session.Started.TrySetException(new PlaybackException("PlaybackEndedBeforeStart"));
        });
        session.Failed = (_, args) => Queue(() => Fail(session, args.Error switch
        {
            MediaPlayerError.SourceNotSupported or MediaPlayerError.DecodingError => "UnsupportedFormat",
            MediaPlayerError.NetworkError => "NetworkFailure",
            _ => "NativePlaybackFailure"
        }));
        session.StateChanged = (_, _) => Queue(() => OnStateChanged(session));
        session.PositionChanged = (_, _) => Queue(() =>
        {
            if (IsActive(session)) Publish(session, PlaybackEngineEventKind.PositionChanged);
        });
        session.SeekCompleted = (_, _) => Queue(() => OnSeekCompleted(session));
        session.DurationChanged = (_, _) => Queue(() =>
        {
            if (IsActive(session)) Publish(session, PlaybackEngineEventKind.StateChanged);
        });
        session.Player.MediaOpened += session.Opened;
        session.Player.MediaEnded += session.Ended;
        session.Player.MediaFailed += session.Failed;
        session.NativeSession.PlaybackStateChanged += session.StateChanged;
        session.NativeSession.PositionChanged += session.PositionChanged;
        session.NativeSession.SeekCompleted += session.SeekCompleted;
        session.NativeSession.NaturalDurationChanged += session.DurationChanged;
        element.AreTransportControlsEnabled = false;
        element.SetMediaPlayer(session.Player);
        session.Player.Source = session.Item;
    }

    private void OnOpened(Session session)
    {
        if (!IsActive(session) || session.IsOpened) return;
        try
        {
            ApplyAudioSelection(session);
            ApplySubtitles(session);
            session.IsOpened = true;
            if (session.Request.InitialPositionTicks > 0)
            {
                if (!session.NativeSession!.CanSeek) throw new PlaybackException("UnsupportedFormat");
                session.InitialSeekPending = true;
                session.NativeSession.Position = TimeSpan.FromTicks(session.Request.InitialPositionTicks);
            }
            else
            {
                session.Player!.Play();
            }
        }
        catch (PlaybackException exception) { Fail(session, exception.ErrorCode); }
        catch { Fail(session, "NativePlaybackFailure"); }
    }

    private void OnSeekCompleted(Session session)
    {
        if (!IsActive(session)) return;
        if (session.InitialSeekPending)
        {
            if (Math.Abs(session.NativeSession!.Position.Ticks - session.Request.InitialPositionTicks)
                > TimeSpan.FromMilliseconds(750).Ticks)
            {
                Fail(session, "InitialSeekFailed");
                return;
            }
            session.InitialSeekPending = false;
            session.Player!.Play();
        }
        session.UserSeeking = false;
        session.SeekCompletion?.TrySetResult();
        Publish(session, PlaybackEngineEventKind.StateChanged);
        TryCompleteOpen(session);
    }

    private void OnStateChanged(Session session)
    {
        if (!IsActive(session)) return;
        Publish(session, PlaybackEngineEventKind.StateChanged);
        TryCompleteOpen(session);
    }

    private void TryCompleteOpen(Session session)
    {
        if (session.IsOpened && !session.InitialSeekPending && session.ExternalTextReady
            && session.NativeSession!.PlaybackState == MediaPlaybackState.Playing)
            session.Started.TrySetResult();
    }

    private void OnTimedTextResolved(Session session, TimedTextSourceResolveResultEventArgs args)
    {
        if (!IsActive(session)) return;
        if (args.Error is not null || args.Tracks.Count == 0)
        {
            Fail(session, "UnsupportedSubtitle");
            return;
        }
        session.ExternalTracks = [.. args.Tracks];
        session.ExternalTextReady = true;
        ApplySubtitles(session);
        TryCompleteOpen(session);
    }

    private static void ApplyAudioSelection(Session session)
    {
        if (session.Request.DeliveryMethod == PlaybackDeliveryMethod.Transcode) return;
        var streams = (session.Request.Source.MediaStreams ?? [])
            .Where(stream => string.Equals(stream.Type, "Audio", StringComparison.OrdinalIgnoreCase)).ToArray();
        var index = session.Request.AudioStreamIndex ?? session.Request.Source.DefaultAudioStreamIndex;
        if (index is null) return;
        var selected = streams.FirstOrDefault(stream => stream.Index == index);
        if (selected is null) throw new PlaybackException("UnsupportedTrack");
        var tracks = session.Item!.AudioTracks;
        if (streams.Length == 1 && tracks.Count == 1)
        {
            tracks.SelectedIndex = 0;
            return;
        }

        // Native track IDs and Emby container indices are different namespaces.
        // A unique language/title match is safe; a matching ordinal alone is not.
        var matching = Enumerable.Range(0, tracks.Count).Where(i =>
            !string.IsNullOrWhiteSpace(selected.Language)
            && string.Equals(tracks[i].Language, selected.Language, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(selected.Title)
                || string.Equals(tracks[i].Label, selected.Title, StringComparison.Ordinal))).ToArray();
        var equivalentSourceCount = streams.Count(stream =>
            string.Equals(stream.Language, selected.Language, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(selected.Title) || string.Equals(stream.Title, selected.Title, StringComparison.Ordinal)));
        if (matching.Length != 1 || equivalentSourceCount != 1) throw new PlaybackException("UnsupportedTrack");
        tracks.SelectedIndex = matching[0];
    }

    private static void ApplySubtitles(Session session)
    {
        if (session.Item is null) return;
        var tracks = session.Item.TimedMetadataTracks;
        for (uint index = 0; index < tracks.Count; index++)
        {
            var track = tracks[(int)index];
            var enabled = session.Request.SubtitleStreamIndex != -1 && session.ExternalTextReady
                && session.ExternalTracks.Any(external => external == track);
            tracks.SetPresentationMode(index, enabled
                ? TimedMetadataTrackPresentationMode.PlatformPresented
                : TimedMetadataTrackPresentationMode.Disabled);
        }
    }

    private static void ValidateSubtitleDelivery(PlaybackEngineRequest request)
    {
        if (request.SubtitleStreamIndex is null or -1 || request.ExternalSubtitleUri is not null) return;
        var selected = (request.Source.MediaStreams ?? []).FirstOrDefault(stream =>
            string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase) && stream.Index == request.SubtitleStreamIndex);
        if (request.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && (string.Equals(selected?.DeliveryMethod, "Encode", StringComparison.OrdinalIgnoreCase)
                || QueryValue(request.MediaUri, "SubtitleMethod")?.Equals("Encode", StringComparison.OrdinalIgnoreCase) == true))
            return;
        throw new PlaybackException("UnsupportedSubtitle");
    }

    private async Task ControlAsync(Guid playbackId, Action<Session> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await OnDispatcherAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(playbackId) is { Player: not null } session) action(session);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("NativePlaybackFailure"); }
    }

    private void Fail(Session session, string errorCode)
    {
        if (!IsActive(session)) return;
        session.Started.TrySetException(new PlaybackException(errorCode));
        session.SeekCompletion?.TrySetException(new PlaybackException(errorCode));
        Publish(session, PlaybackEngineEventKind.Failed, PlaybackEngineState.Failed, errorCode);
        // Coordinator owns server cleanup. Stop native activity immediately while it handles the failure.
        RetireNative(session, publishStopped: false);
        _ = RetireAsync(session);
    }

    private async Task RetireAsync(Session session)
    {
        var retirement = await OnDispatcherAsync(() =>
        {
            RetireNative(session);
            return session.RetirementTask ??= DrainAsync(session);
        }).ConfigureAwait(false);
        await retirement.ConfigureAwait(false);
    }

    private async Task DrainAsync(Session session)
    {
        try { await session.LoadTask.ConfigureAwait(false); }
        catch { }
        await OnDispatcherAsync(() => ClearRetiredPlayerSource(session)).ConfigureAwait(false);
        if (session.AdaptiveFilter is not null) await session.AdaptiveFilter.DrainAsync().ConfigureAwait(false);
        if (session.DirectRelay is not null) await session.DirectRelay.DisposeAsync().ConfigureAwait(false);
        await session.Transport.DisposeAsync().ConfigureAwait(false);
        if (session.SubtitleTransport is not null) await session.SubtitleTransport.DisposeAsync().ConfigureAwait(false);
        session.Cancellation.Dispose();
        session.Lifetime.Dispose();
        await OnDispatcherAsync(() =>
        {
            session.AdaptiveFilter = null;
            session.DirectRelay = null;
            session.SubtitleTransport = null;
            session.LoadTask = Task.CompletedTask;
            session.SeekCompletion = null;
            if (ReferenceEquals(_current, session)) _current = null;
        }).ConfigureAwait(false);
    }

    private void RetireNative(Session session, bool publishStopped = true)
    {
        if (session.Retired) return;
        session.Retired = true;
        session.Lifetime.Cancel();
        session.Started.TrySetCanceled();
        session.SeekCompletion?.TrySetCanceled();
        RetireMediaControls(session);
        if (ReferenceEquals(_current, session))
        {
            if (publishStopped) Publish(session, PlaybackEngineEventKind.StateChanged, PlaybackEngineState.Stopped);
            Release(session, "DetachPlayerElement", () => element.SetMediaPlayer(null));
        }
        if (session.Player is { } player)
        {
            Release(session, "UnsubscribeOpened", () => player.MediaOpened -= session.Opened);
            Release(session, "UnsubscribeEnded", () => player.MediaEnded -= session.Ended);
            Release(session, "UnsubscribeFailed", () => player.MediaFailed -= session.Failed);
            if (session.NativeSession is { } native)
            {
                Release(session, "UnsubscribeState", () => native.PlaybackStateChanged -= session.StateChanged);
                Release(session, "UnsubscribePosition", () => native.PositionChanged -= session.PositionChanged);
                Release(session, "UnsubscribeSeek", () => native.SeekCompleted -= session.SeekCompleted);
                Release(session, "UnsubscribeDuration", () => native.NaturalDurationChanged -= session.DurationChanged);
            }
            Release(session, "PausePlayer", player.Pause);
            Release(session, "ClearPlayerSource", () => player.Source = null);
            if (session.OwnsPlayer) Release(session, "ClosePlayer", player.Dispose);
            session.Player = null;
        }
        if (session.Item is not null) Release(session, "UnsubscribeMetadataTracks", () => session.Item.TimedMetadataTracksChanged -= session.TracksChanged);
        if (session.TimedText is not null) Release(session, "UnsubscribeTimedText", () => session.TimedText.Resolved -= session.TimedTextResolved);
        Release(session, "CloseMediaSource", () => session.MediaSource?.Dispose());
        Release(session, "CloseAdaptiveSource", () => session.AdaptiveSource?.Dispose());
        Release(session, "CloseAdaptiveResponse", () => session.AdaptiveCreationResponse?.Dispose());
        Release(session, "CloseAdaptiveHttp", () => session.AdaptiveHttp?.Dispose());
        Release(session, "CloseAdaptiveFilterOwner", () => session.AdaptiveFilterOwner?.Dispose());
        session.AdaptiveFilter?.Dispose();
        Release(session, "CloseSubtitleStream", () => session.SubtitleStream?.Dispose());
        session.NativeSession = null;
        session.Item = null;
        session.MediaSource = null;
        session.AdaptiveSource = null;
        session.AdaptiveCreationResponse = null;
        session.AdaptiveHttp = null;
        session.AdaptiveFilterOwner = null;
        session.SubtitleStream = null;
        session.TimedText = null;
        session.ExternalTracks = [];
        session.Opened = null;
        session.Ended = null;
        session.Failed = null;
        session.StateChanged = null;
        session.PositionChanged = null;
        session.SeekCompleted = null;
        session.DurationChanged = null;
        session.TracksChanged = null;
        session.TimedTextResolved = null;
    }

    private void Publish(Session session, PlaybackEngineEventKind kind, PlaybackEngineState? state = null, string? errorCode = null)
    {
        var previous = Snapshot?.PlaybackId == session.Request.PlaybackId ? Snapshot : null;
        MediaPlaybackState? nativeState = null;
        var position = previous?.PositionTicks ?? 0;
        var duration = previous?.DurationTicks;
        var canSeek = false;
        var muted = previous?.IsMuted ?? _isMuted;
        var volume = previous?.VolumeLevel ?? _volumeLevel;
        var rate = previous?.PlaybackRate ?? 1;
        try
        {
            if (session.Player is { } player)
            {
                var native = session.NativeSession!;
                nativeState = native.PlaybackState;
                position = Math.Max(0, native.Position.Ticks);
                if (native.NaturalDuration.Ticks > 0) duration = native.NaturalDuration.Ticks;
                canSeek = MediaDeliveryClassifier.CanSeekInPlace(session.Request, native.CanSeek);
                muted = player.IsMuted;
                volume = (int)Math.Round(player.Volume * 100);
                rate = native.PlaybackRate;
            }
        }
        catch { }
        var snapshot = new PlaybackEngineSnapshot
        {
            PlaybackId = session.Request.PlaybackId,
            State = state ?? (session.UserSeeking ? PlaybackEngineState.Seeking : MapState(nativeState)),
            PositionTicks = position,
            DurationTicks = duration,
            CanSeek = canSeek,
            IsMuted = muted,
            VolumeLevel = volume,
            PlaybackRate = rate
        };
        Volatile.Write(ref _snapshot, snapshot);
        try { EventReceived?.Invoke(this, new PlaybackEngineEventArgs(kind, snapshot, errorCode)); }
        catch { }
    }

    private MediaPlayer GetPlayerOwner(Session session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _playerOwner ??= new MediaPlayer();
        // A new playback may borrow the player only after the previous source has been detached.
        // OpenAsync serializes that complete retirement before creating the replacement session.
        if (_playerOwner.Source is not null) throw new PlaybackException("PlayerSourceNotReleased");
        _playerOwnerPlaybackId = session.Request.PlaybackId;
        session.PlayerOwnerUseSequence = ++_playerOwnerUseSequence;
        return _playerOwner;
    }

    private void ClearRetiredPlayerSource(Session session)
    {
        if (!session.Retired || session.PlayerOwnerUseSequence == 0
            || session.PlayerOwnerUseSequence != _playerOwnerUseSequence || _playerOwner is not { } player) return;
        // Cancellation can reenter a Source setter through a native event. Recheck after LoadAsync has
        // unwound, while the serialized replacement open still waits for this retirement to finish.
        Release(session, "ClearRetiredPlayerSource", () =>
        {
            if (player.Source is not null) player.Source = null;
            if (player.Source is not null) throw new PlaybackException("PlayerSourceNotReleased");
        });
    }

    private void ClosePlayerOwner()
    {
        var player = _playerOwner;
        _playerOwner = null;
        if (player is null) return;
        try { player.Dispose(); }
        catch
        {
            try { Diagnostic?.Invoke(this, new(_playerOwnerPlaybackId, "ClosePlayerOwner", "NativeReleaseFailed")); }
            catch { }
            throw new PlaybackException("NativePlayerCloseFailed");
        }
    }

    private Session? Find(Guid id) => _current is { } session && session.Request.PlaybackId == id && IsActive(session) ? session : null;
    private void Release(Session session, string operation, Action action) =>
        Release(session.Request.PlaybackId, operation, action);

    private void Release(Guid playbackId, string operation, Action action)
    {
        try { action(); }
        catch
        {
            try { Diagnostic?.Invoke(this, new PlaybackDiagnosticEventArgs(playbackId, operation, "NativeReleaseFailed")); }
            catch { }
        }
    }
    private bool IsActive(Session session) => !_disposed && ReferenceEquals(_current, session) && !session.Retired && !session.Lifetime.IsCancellationRequested;
    private void EnsureActive(Session session)
    {
        if (!IsActive(session)) throw new OperationCanceledException(session.Lifetime.Token);
    }

    private void Queue(Action action)
    {
        void Run()
        {
            try { action(); }
            catch
            {
                if (_current is { Retired: false } session) Fail(session, "NativePlaybackFailure");
            }
        }
        if (dispatcher.HasThreadAccess) Run();
        else dispatcher.TryEnqueue(Run);
    }

    private Task OnDispatcherAsync(Action action) => OnDispatcherAsync(() => { action(); return true; });

    private async Task<T> OnPlayerDispatcherAsync<T>(Func<T> action)
    {
        try { return await OnDispatcherAsync(action).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (PlaybackException) { throw; }
        catch { throw new PlaybackException("NativePlaybackFailure"); }
    }

    private Task<T> OnDispatcherAsync<T>(Func<T> action)
    {
        if (dispatcher.HasThreadAccess)
        {
            try { return Task.FromResult(action()); }
            catch (Exception exception) { return Task.FromException<T>(exception); }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new PlaybackException("PlayerDispatcherUnavailable"));
        return completion.Task;
    }

    private static PlaybackEngineState MapState(MediaPlaybackState? state) => state switch
    {
        MediaPlaybackState.Playing => PlaybackEngineState.Playing,
        MediaPlaybackState.Paused => PlaybackEngineState.Paused,
        MediaPlaybackState.Buffering => PlaybackEngineState.Buffering,
        MediaPlaybackState.Opening => PlaybackEngineState.Opening,
        _ => PlaybackEngineState.Idle
    };

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;

    partial void CreateAdaptiveHttpClient(PlaybackEngineRequest request, ref NativeHttpClient? client,
        ref IDisposable? filterOwner);

    partial void CreateSessionPlayer(PlaybackEngineRequest request, ref MediaPlayer? player, ref bool ownsPlayer);
    private static bool HasPreservedTimestamps(Uri uri)
    {
        var value = QueryValue(uri, "CopyTimestamps");
        return value == "1" || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }
    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    private sealed partial class Session
    {
        public Session(PlaybackEngineRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Transport = new ScopedMediaTransport(request.MediaUri, request.Headers, Lifetime.Token);
            ExternalTextReady = request.ExternalSubtitleUri is null;
        }

        public PlaybackEngineRequest Request { get; }
        public CancellationTokenSource Lifetime { get; }
        public ScopedMediaTransport Transport { get; }
        public ScopedMediaTransport? SubtitleTransport { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? SeekCompletion { get; set; }
        public Task LoadTask { get; set; } = Task.CompletedTask;
        public Task? RetirementTask { get; set; }
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool Retired { get; set; }
        public bool IsOpened { get; set; }
        public bool InitialSeekPending { get; set; }
        public bool UserSeeking { get; set; }
        public bool ExternalTextReady { get; set; }
        public TimedMetadataTrack[] ExternalTracks { get; set; } = [];
        public MediaPlayer? Player { get; set; }
        public bool OwnsPlayer { get; set; } = true;
        public long PlayerOwnerUseSequence { get; set; }
        public MediaPlaybackSession? NativeSession { get; set; }
        public MediaSource? MediaSource { get; set; }
        public MediaPlaybackItem? Item { get; set; }
        public AdaptiveMediaSource? AdaptiveSource { get; set; }
        public NativeHttpResponse? AdaptiveCreationResponse { get; set; }
        public NativeHttpClient? AdaptiveHttp { get; set; }
        public IDisposable? AdaptiveFilterOwner { get; set; }
        public ScopedAdaptiveHttpFilter? AdaptiveFilter { get; set; }
        public SessionHttpRelay? DirectRelay { get; set; }
        public IRandomAccessStream? SubtitleStream { get; set; }
        public TimedTextSource? TimedText { get; set; }
        public TypedEventHandler<MediaPlayer, object>? Opened { get; set; }
        public TypedEventHandler<MediaPlayer, object>? Ended { get; set; }
        public TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? Failed { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? StateChanged { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? PositionChanged { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? SeekCompleted { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? DurationChanged { get; set; }
        public TypedEventHandler<MediaPlaybackItem, IVectorChangedEventArgs>? TracksChanged { get; set; }
        public TypedEventHandler<TimedTextSource, TimedTextSourceResolveResultEventArgs>? TimedTextResolved { get; set; }
    }
}
