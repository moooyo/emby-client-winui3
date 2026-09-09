using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

/// <summary>Injects one explicitly synthetic invalid-media URI without forwarding credentials to it.</summary>
internal sealed class IsolationFailureEngine(IPlaybackEngine inner) : IPlaybackEngine
{
    internal Uri? NextInvalidMedia { get; set; }
    public PlaybackEngineSnapshot? Snapshot => inner.Snapshot;
    public event EventHandler<PlaybackEngineEventArgs>? EventReceived
    {
        add => inner.EventReceived += value;
        remove => inner.EventReceived -= value;
    }
    public Task OpenAsync(PlaybackEngineRequest request, CancellationToken cancellationToken = default)
    {
        if (NextInvalidMedia is { } invalid)
        {
            NextInvalidMedia = null;
            if (!invalid.IsLoopback || invalid.Scheme != "http" || invalid.UserInfo.Length != 0 || invalid.Query.Length != 0)
                throw new PlaybackException("SyntheticFailureEndpointRequired");
            request = request with { MediaUri = invalid, Headers = new Dictionary<string, string>(),
                DeliveryMethod = PlaybackDeliveryMethod.DirectStream, ExternalSubtitleUri = null,
                ExternalSubtitleHeaders = new Dictionary<string, string>() };
        }
        return inner.OpenAsync(request, cancellationToken);
    }
    public Task PauseAsync(Guid id, CancellationToken ct = default) => inner.PauseAsync(id, ct);
    public Task ResumeAsync(Guid id, CancellationToken ct = default) => inner.ResumeAsync(id, ct);
    public Task SeekAsync(Guid id, long ticks, CancellationToken ct = default) => inner.SeekAsync(id, ticks, ct);
    public Task SetVolumeAsync(Guid id, int volume, bool muted, CancellationToken ct = default) => inner.SetVolumeAsync(id, volume, muted, ct);
    public Task StopAsync(Guid id, CancellationToken ct = default) => inner.StopAsync(id, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
