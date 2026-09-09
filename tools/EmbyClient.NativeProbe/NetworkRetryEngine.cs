using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

/// <summary>Changes only the media route to the controlled loopback proxy; engine events are never synthesized.</summary>
internal sealed class NetworkRetryEngine(IPlaybackEngine inner, ColdRangeProxy proxy) : IPlaybackEngine
{
    public PlaybackEngineSnapshot? Snapshot => inner.Snapshot;
    public event EventHandler<PlaybackEngineEventArgs>? EventReceived
    {
        add => inner.EventReceived += value;
        remove => inner.EventReceived -= value;
    }
    public Task OpenAsync(PlaybackEngineRequest request, CancellationToken ct = default) =>
        inner.OpenAsync(request with { MediaUri = proxy.Register(request), Headers = new Dictionary<string, string>() }, ct);
    public Task PauseAsync(Guid id, CancellationToken ct = default) => inner.PauseAsync(id, ct);
    public Task ResumeAsync(Guid id, CancellationToken ct = default) => inner.ResumeAsync(id, ct);
    public Task SeekAsync(Guid id, long ticks, CancellationToken ct = default) => inner.SeekAsync(id, ticks, ct);
    public Task SetVolumeAsync(Guid id, int volume, bool muted, CancellationToken ct = default) => inner.SetVolumeAsync(id, volume, muted, ct);
    public Task StopAsync(Guid id, CancellationToken ct = default) => inner.StopAsync(id, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
