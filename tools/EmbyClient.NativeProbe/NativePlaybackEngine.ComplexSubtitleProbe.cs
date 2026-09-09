using EmbyClient.NativeProbe;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    /// <summary>Reads the active product request and native session without changing subtitle capabilities or presentation.</summary>
    internal Task<ComplexSubtitleNativeObservation> ObserveComplexSubtitleForProbeAsync(Guid playbackId) => OnDispatcherAsync(() =>
    {
        var value = new ComplexSubtitleNativeObservation();
        var session = _current;
        if (session is null || session.Retired || session.Request.PlaybackId != playbackId) return value;
        var request = session.Request;
        value.ActivePlaybackMatches = true;
        value.DefaultPlayerOwnerPresent = _playerOwner is not null && ReferenceEquals(session.Player, _playerOwner)
            && !ReuseNativeHttpControlPlayer && NativeHttpControlObservation is null;
        value.DeliveryMethod = request.DeliveryMethod.ToString();
        value.SelectedSubtitleStreamIndex = request.SubtitleStreamIndex;
        var delivery = request.Source.MediaStreams.FirstOrDefault(stream => stream.Index == request.SubtitleStreamIndex)?.DeliveryMethod;
        value.SelectedSubtitleDeliveryMethod = delivery?.Equals("Encode", StringComparison.OrdinalIgnoreCase) == true ? "Encode"
            : delivery?.Equals("External", StringComparison.OrdinalIgnoreCase) == true ? "External" : delivery is null ? "Missing" : "Other";
        var uri = request.MediaUri;
        value.MediaOriginIsOwnedLoopback = uri is { Scheme: "http", Host: "127.0.0.1", Port: 19096 } && uri.UserInfo.Length == 0;
        value.MediaRouteKind = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "HlsPlaylist"
            : uri.AbsolutePath.Contains("/videos/", StringComparison.OrdinalIgnoreCase) ? "ProgressiveVideo" : "Other";
        foreach (var entry in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length != 2) continue;
            var name = Uri.UnescapeDataString(pair[0]);
            if (name.Equals("SubtitleMethod", StringComparison.OrdinalIgnoreCase))
                value.RequestSubtitleMethod = Uri.UnescapeDataString(pair[1]).Equals("Encode", StringComparison.OrdinalIgnoreCase) ? "Encode" : "Other";
            if (name.Equals("SubtitleStreamIndex", StringComparison.OrdinalIgnoreCase) && int.TryParse(Uri.UnescapeDataString(pair[1]), out var index))
                value.RequestSubtitleStreamIndex = index;
        }
        value.HasExternalSubtitleUri = request.ExternalSubtitleUri is not null;
        value.HasTimedTextSource = session.TimedText is not null;
        value.HasProgressiveRelay = session.ProgressiveRelay is not null;
        value.HasFullSourceRangeRelay = session.DirectRelay is not null;
        value.TranscodingContainer = request.Source.TranscodingContainer?.ToLowerInvariant() is "mp4" ? "mp4"
            : request.Source.TranscodingContainer is null ? "Missing" : "Other";
        value.TimelineKind = request.TimelineKind.ToString();
        value.TimelineOffsetTicks = request.TimelineOffsetTicks;
        value.InitialPositionTicks = request.InitialPositionTicks;
        value.State = session.NativeSession?.PlaybackState.ToString() ?? "Missing";
        value.PositionTicks = session.NativeSession?.Position.Ticks ?? -1;
        value.SourcePositionTicks = value.PositionTicks >= 0 && request.TimelineOffsetTicks <= long.MaxValue - value.PositionTicks
            ? request.TimelineOffsetTicks + value.PositionTicks : null;
        value.NaturalDurationTicks = session.NativeSession?.NaturalDuration.Ticks;
        value.VideoWidth = session.NativeSession?.NaturalVideoWidth ?? 0;
        value.VideoHeight = session.NativeSession?.NaturalVideoHeight ?? 0;
        return value;
    });

    internal Task<bool> ComplexSubtitleOwnerClearedForProbeAsync() => OnDispatcherAsync(() =>
        _current is null && _playerOwner is null && _disposalTask?.IsCompleted == true);

    internal Task<bool> ComplexSubtitleSessionDrainedForProbeAsync() => OnDispatcherAsync(() =>
        _current is null && _playerOwner?.Source is null && element.MediaPlayer is null);
}
