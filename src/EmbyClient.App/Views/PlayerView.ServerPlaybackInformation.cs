using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private Guid? _serverPlaybackInformationId;
    private TranscodingInfo? _serverTranscodingInformation;
    private string? _serverPlaybackSessionId;
    private CancellationTokenSource? _serverPlaybackInformationRequest;
    private long _serverPlaybackInformationRevision;
    private long _nextServerPlaybackInformationRefresh;

    private TranscodingInfo? CurrentServerTranscodingInformation(PlaybackContext context) =>
        context.DeliveryMethod == PlaybackDeliveryMethod.Transcode && _serverPlaybackInformationId == context.PlaybackId
            ? _serverTranscodingInformation : null;

    private void ResetServerPlaybackInformation()
    {
        _serverPlaybackInformationRevision++;
        _serverPlaybackInformationRequest?.Cancel();
        _serverPlaybackInformationRequest?.Dispose();
        _serverPlaybackInformationRequest = null;
        _serverPlaybackInformationId = null;
        _serverTranscodingInformation = null;
        _serverPlaybackSessionId = null;
        _nextServerPlaybackInformationRefresh = 0;
    }

    private async void RefreshServerPlaybackInformation(PlaybackContext context)
    {
        if (!_playbackInfoOpen || context.DeliveryMethod != PlaybackDeliveryMethod.Transcode
            || _coordinator?.ActiveContext?.PlaybackId != context.PlaybackId
            || _coordinator.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Buffering or PlaybackStatus.Seeking)
            || _session is not { } session || _engine is not { } engine) return;
        if (_serverPlaybackInformationId != context.PlaybackId)
        {
            ResetServerPlaybackInformation();
            _serverPlaybackInformationId = context.PlaybackId;
        }
        if (_serverPlaybackInformationRequest is not null || Environment.TickCount64 < _nextServerPlaybackInformationRefresh) return;
        var intent = _playIntent;
        var revision = _serverPlaybackInformationRevision;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_playRequest?.Token ?? CancellationToken.None);
        _serverPlaybackInformationRequest = request;
        _nextServerPlaybackInformationRefresh = Environment.TickCount64 + 5_000;
        bool OwnsRequest() => _playbackInfoOpen && !request.IsCancellationRequested && intent == _playIntent
            && revision == _serverPlaybackInformationRevision && ReferenceEquals(session, _session)
            && ReferenceEquals(engine, _engine) && _coordinator?.ActiveContext?.PlaybackId == context.PlaybackId;
        try
        {
            // Read server scalars only. No packet sampling or client streaming cap is used as a transcode target.
            var result = await session.Api.GetCurrentPlaybackSessionAsync(context.Selection.ItemId,
                playSessionId: context.PlaySessionId, sessionId: _serverPlaybackSessionId, cancellationToken: request.Token);
            if (!OwnsRequest()) return;
            var reportedSource = result?.PlayState?.MediaSourceId;
            var matchingSource = string.IsNullOrWhiteSpace(reportedSource)
                || string.Equals(reportedSource, context.Source.Id, StringComparison.Ordinal);
            _serverTranscodingInformation = matchingSource ? result?.TranscodingInfo : null;
            _serverPlaybackSessionId = matchingSource ? result?.Id : null;
            UpdatePlaybackInformation();
            UpdateStreamPresentation(context);
        }
        catch (OperationCanceledException) { }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure && exception.ApplicationErrorCode != "ParentalControl")
        {
            if (OwnsRequest()) ReportExpiredSession();
        }
        catch (Exception)
        {
            // Session inspection is optional. Keep the source metadata label when the response is unavailable.
        }
        finally
        {
            if (ReferenceEquals(request, _serverPlaybackInformationRequest)) _serverPlaybackInformationRequest = null;
            request.Dispose();
        }
    }
}
