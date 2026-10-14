using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private Task? _audioRecoveryTask;
    private Guid _audioRecoveryPlaybackId;
    private CancellationTokenSource? _audioRecoveryCancellation;

    /// <summary>Replaces the local output backend while preserving the coordinator's one server session.</summary>
    private async Task ReopenAudioAsPcmAsync(Session session, string fallbackReason,
        CancellationToken cancellationToken = default)
    {
        var recovery = await OnDispatcherAsync(() =>
        {
            if (_audioRecoveryPlaybackId == session.Request.PlaybackId && _audioRecoveryTask is { IsCompleted: false } running)
                return running;
            if (!IsActive(session)) return Task.CompletedTask;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(session.OwnerCancellationToken, cancellationToken);
            _audioRecoveryPlaybackId = session.Request.PlaybackId;
            _audioRecoveryCancellation = lifetime;
            return _audioRecoveryTask = RecoverAudioBackendAsync(session, fallbackReason, lifetime);
        }).ConfigureAwait(false);
        await recovery.ConfigureAwait(false);
    }

    private async Task RecoverAudioBackendAsync(Session previous, string fallbackReason,
        CancellationTokenSource lifetime)
    {
        // Register the task before retirement can cancel the original opening completion.
        await Task.Yield();
        Session? replacement = null;
        var transitionAcquired = false;
        try
        {
            await _openingTransition.WaitAsync(lifetime.Token).ConfigureAwait(false);
            transitionAcquired = true;
            var state = await OnDispatcherAsync(() =>
            {
                if (!IsActive(previous)) return null;
                lifetime.Token.ThrowIfCancellationRequested();
                var native = previous.NativeSession;
                var position = !previous.IsOpened || previous.InitialSeekPending
                    ? previous.Request.InitialPositionTicks
                    : Math.Max(0, native?.Position.Ticks ?? Snapshot?.PositionTicks ?? previous.Request.InitialPositionTicks);
                var paused = native?.PlaybackState == MediaPlaybackState.Paused || previous.StartPaused;
                var local = previous.LocalSubtitle?.RecoverySubtitle;
                SuspendNativeAudioOutput(previous);
                previous.Player?.Pause();
                Publish(previous, PlaybackEngineEventKind.StateChanged, PlaybackEngineState.Opening);
                return new AudioBackendRecovery(previous.Request with
                {
                    InitialPositionTicks = position,
                    AudioPassthroughMode = AudioPassthroughMode.Off
                }, previous.OwnerCancellationToken, paused, local, previous.RequestedDecoderApi,
                    previous.AudioOutputSnapshot?.RequestedMode ?? previous.Request.AudioPassthroughMode);
            }).ConfigureAwait(false);
            if (state is null) return;

            // Canceling this decoder's lifetime never cancels its parent coordinator lifetime.
            // Drain must not wait for previous.AudioFallbackTask, which is this recovery itself.
            await RetireAsync(previous, publishStopped: false).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!previous.NativeAudioCleanupSucceeded) throw new PlaybackException("AudioOutputCleanupPending");
            replacement = await OnDispatcherAsync(() => BeginSession(state.Request, state.OwnerToken,
                startPaused: true, audioFallbackReason: fallbackReason, preservedDecoderApi: state.DecoderApi,
                audioRecoveryRequestedMode: state.RequestedAudioMode)).ConfigureAwait(false);
            await replacement.Started.Task.WaitAsync(OpenTimeout, lifetime.Token).ConfigureAwait(false);
            if (state.LocalSubtitle is { } prepared)
                await LoadLocalSubtitleAsync(state.Request.PlaybackId, prepared, lifetime.Token).ConfigureAwait(false);

            await OnDispatcherAsync(() =>
            {
                EnsureActive(replacement);
                lifetime.Token.ThrowIfCancellationRequested();
                replacement.StartPaused = state.IsPaused;
                if (state.IsPaused)
                {
                    replacement.Player!.Pause();
                    Publish(replacement, PlaybackEngineEventKind.StateChanged, PlaybackEngineState.Paused);
                }
                else replacement.Player!.Play();
            }).ConfigureAwait(false);
            if (!state.IsPaused)
                await replacement.PlaybackConfirmed.Task.WaitAsync(OpenTimeout, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (replacement is not null)
            {
                await RetireAsync(replacement, publishStopped: false).ConfigureAwait(false);
                // Preserve a replacement failure that canceled an import or active check before playback confirmation.
                if (!lifetime.IsCancellationRequested && replacement.PlaybackConfirmed.Task.IsFaulted)
                    await replacement.PlaybackConfirmed.Task.ConfigureAwait(false);
            }
            throw;
        }
        catch
        {
            await OnDispatcherAsync(() =>
            {
                if (replacement is not null && IsActive(replacement)) Fail(replacement, "NativePlaybackFailure");
                else if (!_disposed && !previous.OwnerCancellationToken.IsCancellationRequested && _current is null)
                {
                    // Keep a failed current owner so the coordinator can finish its established server session.
                    var failed = new Session(previous.Request, previous.OwnerCancellationToken);
                    _current = failed;
                    Fail(failed, "NativePlaybackFailure");
                }
            }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transitionAcquired) _openingTransition.Release();
            await OnDispatcherAsync(() =>
            {
                if (ReferenceEquals(_audioRecoveryCancellation, lifetime)) _audioRecoveryCancellation = null;
                lifetime.Dispose();
            }).ConfigureAwait(false);
        }
    }

    private async Task WaitForAudioRecoveryAsync(Guid playbackId, CancellationToken cancellationToken)
    {
        var recovery = await OnDispatcherAsync(() => _audioRecoveryPlaybackId == playbackId
            ? _audioRecoveryTask : null).ConfigureAwait(false);
        if (recovery is { IsCompleted: false }) await recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelAudioRecoveryAsync(Guid? playbackId)
    {
        var recovery = await OnDispatcherAsync(() =>
        {
            if (playbackId is not null && playbackId != _audioRecoveryPlaybackId) return null;
            _audioRecoveryCancellation?.Cancel();
            return _audioRecoveryTask;
        }).ConfigureAwait(false);
        if (recovery is null) return;
        try { await recovery.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (PlaybackException) { }
        catch (TimeoutException) { }
        catch { }
    }

    private sealed record AudioBackendRecovery(PlaybackEngineRequest Request, CancellationToken OwnerToken,
        bool IsPaused, NativePreparedSubtitle? LocalSubtitle, string DecoderApi, AudioPassthroughMode RequestedAudioMode);

    private sealed partial class Session
    {
        public bool StartPaused { get; set; }
        public string? AudioRecoveryFallbackReason { get; init; }
        public AudioPassthroughMode? AudioRecoveryRequestedMode { get; init; }
        public TaskCompletionSource PlaybackConfirmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
