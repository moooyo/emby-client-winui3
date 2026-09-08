using System.Collections.Concurrent;

namespace EmbyClient.Playback.Tests;

internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    private readonly ConcurrentQueue<PlaybackEngineRequest> opened = new();
    private readonly ConcurrentQueue<Guid> stopped = new();
    private readonly ConcurrentQueue<Guid> paused = new();
    private readonly ConcurrentQueue<(Guid PlaybackId, long PositionTicks)> sought = new();
    private PlaybackEngineSnapshot? snapshot;

    public event EventHandler<PlaybackEngineEventArgs>? EventReceived;

    public Func<PlaybackEngineRequest, CancellationToken, Task>? OnOpenAsync { get; set; }
    public Func<Guid, CancellationToken, Task>? OnStopAsync { get; set; }
    public Func<Guid, CancellationToken, Task>? OnPauseAsync { get; set; }
    public PlaybackEngineRequest[] Opened => opened.ToArray();
    public Guid[] Stopped => stopped.ToArray();
    public Guid[] Paused => paused.ToArray();
    public (Guid PlaybackId, long PositionTicks)[] Sought => sought.ToArray();
    public PlaybackEngineSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public bool IsDisposed { get; private set; }

    public async Task OpenAsync(PlaybackEngineRequest request, CancellationToken cancellationToken = default)
    {
        opened.Enqueue(request);
        Volatile.Write(ref snapshot, new PlaybackEngineSnapshot
        {
            PlaybackId = request.PlaybackId,
            State = PlaybackEngineState.Opening,
            PositionTicks = 0,
            DurationTicks = request.ItemRunTimeTicks,
            CanSeek = true
        });
        if (OnOpenAsync is not null) await OnOpenAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref snapshot, Snapshot! with
        {
            PlaybackId = request.PlaybackId,
            State = PlaybackEngineState.Playing,
            PositionTicks = request.InitialPositionTicks
        });
    }

    public async Task PauseAsync(Guid playbackId, CancellationToken cancellationToken = default)
    {
        paused.Enqueue(playbackId);
        if (OnPauseAsync is not null)
        {
            await OnPauseAsync(playbackId, cancellationToken);
            return;
        }
        ChangeState(playbackId, PlaybackEngineState.Paused);
    }

    public Task ResumeAsync(Guid playbackId, CancellationToken cancellationToken = default)
    {
        ChangeState(playbackId, PlaybackEngineState.Playing);
        return Task.CompletedTask;
    }

    public Task SeekAsync(Guid playbackId, long positionTicks, CancellationToken cancellationToken = default)
    {
        sought.Enqueue((playbackId, positionTicks));
        if (Snapshot is { } current && current.PlaybackId == playbackId)
            Volatile.Write(ref snapshot, current with { PositionTicks = positionTicks });
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(Guid playbackId, int volumeLevel, bool isMuted, CancellationToken cancellationToken = default)
    {
        if (Snapshot is { } current && current.PlaybackId == playbackId)
            Volatile.Write(ref snapshot, current with { VolumeLevel = volumeLevel, IsMuted = isMuted });
        return Task.CompletedTask;
    }

    public async Task StopAsync(Guid playbackId, CancellationToken cancellationToken = default)
    {
        stopped.Enqueue(playbackId);
        if (OnStopAsync is not null) await OnStopAsync(playbackId, cancellationToken);
        ChangeState(playbackId, PlaybackEngineState.Stopped);
    }

    public void SetPosition(long positionTicks)
    {
        if (Snapshot is not { } current) throw new InvalidOperationException("The engine is not open.");
        Volatile.Write(ref snapshot, current with { PositionTicks = positionTicks });
    }

    public void Emit(PlaybackEngineEventKind kind, PlaybackEngineSnapshot state, string? errorCode = null)
    {
        if (Snapshot?.PlaybackId == state.PlaybackId) Volatile.Write(ref snapshot, state);
        EventReceived?.Invoke(this, new PlaybackEngineEventArgs(kind, state, errorCode));
    }

    private void ChangeState(Guid playbackId, PlaybackEngineState state)
    {
        if (Snapshot is { } current && current.PlaybackId == playbackId)
            Volatile.Write(ref snapshot, current with { State = state });
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
