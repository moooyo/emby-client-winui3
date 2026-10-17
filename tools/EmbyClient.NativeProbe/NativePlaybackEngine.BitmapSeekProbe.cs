using EmbyClient.NativeProbe;
using Windows.Media.Core;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private int _bitmapSeekTrackSnapshotRetries;
    internal int BitmapSeekTrackSnapshotRetries => Volatile.Read(ref _bitmapSeekTrackSnapshotRetries);

    internal async Task<BitmapSubtitleSeekTrackObservation> ObserveBitmapSeekTracksForProbeAsync(Guid playbackId,
        CancellationToken cancellationToken = default)
    {
        const int maximumRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await OnDispatcherAsync(() => CaptureBitmapSeekTracksForProbe(playbackId)); }
            catch (InvalidOperationException error) when (error.HResult == unchecked((int)0x8000000C) && attempt < maximumRetries)
            {
                // WinRT cue views can change while a native reference callback finishes.
                // Retry the complete snapshot; a failed read never means an empty track.
                Interlocked.Increment(ref _bitmapSeekTrackSnapshotRetries);
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private BitmapSubtitleSeekTrackObservation CaptureBitmapSeekTracksForProbe(Guid playbackId)
    {
        var session = Find(playbackId);
        if (session is not { NativeSession: not null, MediaSource: not null, NativeEmbeddedSubtitle: not null })
            return new BitmapSubtitleSeekTrackObservation();
        var tracks = new List<BitmapSubtitleSeekTrack>();
        if (session.NativeEmbeddedSubtitle.SubtitleTrack is { } image)
            tracks.Add(ReadTrack("SelectedNativeSubtitle", image));
        foreach (var track in session.MediaSource.ExternalTimedMetadataTracks)
            if (track.TimedMetadataKind == TimedMetadataKind.Custom && track.Id.StartsWith("ReferenceTrack_", StringComparison.Ordinal))
                tracks.Add(ReadTrack("PendingNativeReferenceTrack", track));
        return new BitmapSubtitleSeekTrackObservation
        {
            ActivePlaybackMatches = true, NativePositionTicks = session.NativeSession.Position.Ticks,
            NativeState = session.NativeSession.PlaybackState.ToString(), Tracks = [.. tracks]
        };
    }

    private static BitmapSubtitleSeekTrack ReadTrack(string origin, TimedMetadataTrack track) => new()
    {
        Origin = origin, Kind = track.TimedMetadataKind.ToString(), Id = track.Id, Label = track.Label,
        ActiveCueCount = track.ActiveCues.Count,
        Cues = track.Cues.Select(cue => new BitmapSubtitleSeekCue
        {
            StartTicks = cue.StartTime.Ticks, DurationTicks = cue.Duration.Ticks, IsImageCue = cue is ImageCue
        }).ToArray()
    };
}
