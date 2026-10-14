using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private long _dolbyVisionNoticeIntent = -1;
    private string? _dolbyVisionNoticeSourceId;
    private string? _requestedPlaybackSourceId;

    private static bool IsDolbyVisionProfile5(MediaSourceInfo? source) => (source?.MediaStreams ?? []).Any(stream =>
        string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase)
        && string.Equals(stream.ExtendedVideoSubType, "DoviProfile50", StringComparison.OrdinalIgnoreCase));

    private async void PresentDolbyVisionTranscode(PlaybackContext context)
    {
        if (context.DeliveryMethod != PlaybackDeliveryMethod.Transcode || !IsDolbyVisionProfile5(context.Source)
            || _coordinator?.ActiveContext?.PlaybackId != context.PlaybackId
            || _coordinator.Status != PlaybackStatus.Playing || _session is not { } session
            || _item is not { } item || item.Id != context.Selection.ItemId || _engine is not { } engine
            || _dolbyVisionNoticeIntent == _playIntent && _dolbyVisionNoticeSourceId == context.Source.Id) return;
        var intent = _playIntent;
        var coordinator = _coordinator;
        var token = _playRequest?.Token ?? CancellationToken.None;
        _dolbyVisionNoticeIntent = intent;
        _dolbyVisionNoticeSourceId = context.Source.Id;
        ShowPlayerToast("Dolby Vision P5 is playing through server transcoding", "Use 1080p direct play");
        SetPlayerToastAction(null, "Checking compatible versions...");
        bool OwnsRequest() => !token.IsCancellationRequested && ReferenceEquals(session, _session)
            && ReferenceEquals(coordinator, _coordinator) && ReferenceEquals(engine, _engine) && intent == _playIntent
            && coordinator.ActiveContext?.PlaybackId == context.PlaybackId;
        try
        {
            foreach (var candidate in (item.MediaSources ?? []).Where(source => source.Id != context.Source.Id && Is1080pDirectCandidate(source)))
            {
                if (!OwnsRequest() || PlayerToast.Visibility != Visibility.Visible) return;
                var subtitleIndex = _selectedLocalSubtitle is not null ? -1 : PreferredSubtitleIndex(item, candidate.Id, null);
                var response = await session.Api.GetPlaybackInfoAsync(context.Selection.ItemId, new PlaybackInfoRequest
                {
                    MediaSourceId = candidate.Id, UserId = session.User.Id, MaxStreamingBitrate = _bitrate,
                    StartTimeTicks = context.PositionTicks, SubtitleStreamIndex = subtitleIndex, MaxAudioChannels = 2,
                    DeviceProfile = DeviceProfileFactory.Create(WithCurrentNativeCapabilities(new PlaybackSelection
                    {
                        ItemId = context.Selection.ItemId, MediaSourceId = candidate.Id,
                        MaxStreamingBitrate = _bitrate, SubtitleStreamIndex = subtitleIndex
                    }), enableExternalWebVtt: true),
                    EnableDirectPlay = false, EnableDirectStream = true, EnableTranscoding = false,
                    AllowVideoStreamCopy = true, AllowAudioStreamCopy = true, AllowInterlacedVideoStreamCopy = false,
                    IsPlayback = false, AutoOpenLiveStream = false
                }, token);
                if (!OwnsRequest() || PlayerToast.Visibility != Visibility.Visible) return;
                var available = string.IsNullOrWhiteSpace(response.ErrorCode)
                    ? (response.MediaSources ?? []).FirstOrDefault(source => source.Id == candidate.Id && source.SupportsDirectStream == true
                        && source.RequiresOpening != true && Is1080pDirectCandidate(source)
                        && source.RunTimeTicks > (coordinator.ActiveContext?.PositionTicks ?? context.PositionTicks)
                        && HasIndependentOrDisabledSubtitle(source, subtitleIndex)) : null;
                if (available is null) continue;
                SetPlayerToastAction(async () =>
                {
                    if (!OwnsRequest() || IsModalOpen || _advancing || _retryInProgress || _preparationIntent == _playIntent
                        || coordinator.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused)) return;
                    if (available.RunTimeTicks <= coordinator.ActiveContext?.PositionTicks)
                    {
                        ShowPlayerToast("This version is shorter than the current playback position");
                        return;
                    }
                    await coordinator.ChangeSelectionAsync(WithCurrentNativeCapabilities(new PlaybackSelectionChange
                    {
                        MediaSourceId = available.Id, SubtitleStreamIndex = subtitleIndex, ForceTranscoding = false
                    }), token);
                }, "Switch version and keep the current position");
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (OwnsRequest()) ReportExpiredSession();
        }
        catch (Exception)
        {
            // A missing alternative never changes the successfully negotiated transcode.
        }
        if (OwnsRequest() && PlayerToast.Visibility == Visibility.Visible)
            SetPlayerToastAction(null, "No compatible 1080p direct version is available");
    }

    private static bool Is1080pDirectCandidate(MediaSourceInfo source)
    {
        var video = (source.MediaStreams ?? []).FirstOrDefault(stream => string.Equals(stream.Type, "Video", StringComparison.OrdinalIgnoreCase));
        return !string.IsNullOrWhiteSpace(source.Id) && source.IsInfiniteStream != true && source.RunTimeTicks is > 0
            && video?.Width == 1920 && video.Height is > 0 and <= 1080 && !IsDolbyVisionProfile5(source)
            && (string.IsNullOrWhiteSpace(video.VideoRange) || string.Equals(video.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase))
            && !Hdr10PlaybackPolicy.RequiresServerConversion(video, hdr10OutputEligible: false);
    }

    private static bool HasIndependentOrDisabledSubtitle(MediaSourceInfo source, int? requestedIndex)
    {
        var index = requestedIndex ?? source.DefaultSubtitleStreamIndex;
        if (index is null or -1) return true;
        var subtitle = (source.MediaStreams ?? []).FirstOrDefault(stream => stream.Index == index
            && string.Equals(stream.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
        return subtitle is not null && subtitle.IsTextSubtitleStream == true
            && string.Equals(subtitle.DeliveryMethod, "External", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUnavailableDolbyVisionTranscode()
    {
        if (_session?.User.Policy?.EnableVideoPlaybackTranscoding != false) return false;
        var requestedSourceId = _coordinator?.Recovery?.Selection.MediaSourceId ?? _requestedPlaybackSourceId;
        var source = (_item?.MediaSources ?? []).FirstOrDefault(value => requestedSourceId is not null && value.Id == requestedSourceId);
        if (source is null && _lastPlaybackContext is { } last && last.Selection.ItemId == _item?.Id
            && (requestedSourceId is null || last.Source.Id == requestedSourceId)) source = last.Source;
        return IsDolbyVisionProfile5(source);
    }
}
