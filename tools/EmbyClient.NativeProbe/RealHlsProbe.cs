using System.Text.Json;
using System.Globalization;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private async Task RunRealHlsAsync()
    {
        var report = new RealHlsReport
        {
            RequiredLoops = _realHlsDiagnostic ? 2 : 20,
            ExecutionMode = _realHlsDiagnostic ? "TwoLoopDiagnosticNotResourceAcceptance"
                : _sharedNativeHlsPlayerControl ? "SharedPlayerNativeHttpHlsControlNotProductAcceptance"
                : _nativeHlsHttpControl ? "NativeHttpHlsControlNotProductAcceptance" : "NormalRealHlsLifecycle",
            Scope = _sharedNativeHlsPlayerControl
                ? "Isolated shared-player native HTTP HLS control on the owned identity-checked official Emby 4.9.5.0 loopback server, fixed item 5. Same twenty complete cycles and forty source/session graphs; one MediaPlayer is reused, with per-session source/event/SMTC/HTTP retirement. Not product acceptance or concurrent cancellation/fallback isolation proof."
                : _nativeHlsHttpControl
                ? "Isolated native HTTP HLS control on the owned identity-checked official Emby 4.9.5.0 loopback server, fixed item 5. Same coordinator, forty native graphs, SMTC, and twenty complete cycles; only adaptive HTTP construction differs. Not product acceptance."
                : "Owned official Emby 4.9.5.0 loopback validation server, fixed item 5, forced HLS, Windows native engine and real PlaybackCoordinator. Separate from synthetic direct-stream evidence."
        };
        var nativeHttpControl = _nativeHlsHttpControl ? new NativeHlsHttpControlObservation() : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var observation = new HlsApiObservationHandler();
        using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
        EmbyApiClient? authenticated = null;
        PlaybackCoordinator? coordinator = null;
        NativePlaybackEngine? engine = null;
        try
        {
            SaveRealHls(report);
            Require(report.NativeAot, "NativeAotRequired");
            var credentialBytes = await File.ReadAllBytesAsync(_realHlsCredentialsPath!, deadline.Token);
            RealHlsCredentials? credentials;
            try { credentials = JsonSerializer.Deserialize(credentialBytes, RealHlsJsonContext.Default.RealHlsCredentials); }
            finally { Array.Clear(credentialBytes); }
            Require(credentials is not null && Uri.TryCreate(credentials.ServerUrl, UriKind.Absolute, out _)
                && !string.IsNullOrEmpty(credentials.Username) && credentials.Password is not null, "CredentialsFileInvalid");
            var server = new Uri(credentials!.ServerUrl!);
            Require(server.IsLoopback && server.Port == 19096 && server.Scheme == "http"
                && server.UserInfo.Length == 0 && server.Query.Length == 0 && server.Fragment.Length == 0,
                "OwnedValidationEndpointRequired");
            var api = new EmbyApiClient(http, server, new ClientIdentity("Native HLS Validation", "Windows native probe",
                "native-hls-probe-" + Guid.NewGuid().ToString("N"), "0.1.0"));
            var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(publicInfo.Id == report.ServerId && publicInfo.Version == "4.9.5.0", "OwnedValidationIdentityMismatch");
            report.ServerVersion = publicInfo.Version!;
            var authentication = await api.AuthenticateByNameAsync(credentials.Username!, credentials.Password!, deadline.Token);
            credentials = null;
            Require(authentication.User?.Id is not null && authentication.AccessToken is not null
                && authentication.ServerId == report.ServerId, "OwnedValidationAuthenticationFailed");
            authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
            var item = await authenticated.GetItemAsync(report.ItemId, deadline.Token);
            Require(item.Id == report.ItemId && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000,
                "OwnedValidationMediaMismatch");
            report.ItemDurationTicks = item.RunTimeTicks;
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true)
            {
                NativeHttpControlObservation = nativeHttpControl,
                ReuseNativeHttpControlPlayer = _sharedNativeHlsPlayerControl
            };
            engine.Diagnostic += (_, value) => { lock (report.Diagnostics) report.Diagnostics.Add("Native:" + value.Operation + ":" + value.ErrorCode); };
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(1), CleanupTimeout = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(5)
            });
            coordinator.Diagnostic += (_, value) => { lock (report.Diagnostics) report.Diagnostics.Add(value.Operation + ":" + value.ErrorCode); };
            coordinator.StatusChanged += (_, value) => engine.UpdateMediaControls(value.Context, value.Status, "Owned HLS validation media");
            for (var number = 1; number <= report.RequiredLoops; number++)
            {
                var loop = new RealHlsLoop { Number = number, InitialPositionTicks = number % 2 == 0 ? 170_000_000 : 0 };
                report.Loops.Add(loop);
                SaveRealHls(report);
                var firstApiEvent = observation.Events.Length;
                try
                {
                    await RunRealHlsLoopAsync(loop, coordinator, engine, observation, authenticated, http, nativeHttpControl, deadline.Token);
                    loop.Resources = await CaptureResourcesAsync(deadline.Token);
                    loop.Status = "Passed";
                    report.CompletedLoops++;
                }
                catch (Exception exception)
                {
                    loop.Status = "Failed"; loop.ErrorCode = ErrorCode(exception); loop.ErrorHResult = exception.HResult;
                    throw;
                }
                finally
                {
                    loop.ApiEvents = observation.Events.Skip(firstApiEvent).ToList();
                    report.Negotiations = observation.Negotiations.ToList();
                    report.NativeHttpControl = nativeHttpControl?.Capture();
                    report.SharedPlayerControl = _sharedNativeHlsPlayerControl ? engine.CaptureSharedNativePlayerControl() : null;
                    SaveRealHls(report);
                }
            }
            if (_realHlsDiagnostic) { report.Status = "DiagnosticCyclesCompleted"; return; }
            await coordinator.DisposeAsync(); coordinator = null;
            engine.DisposeSharedNativePlayerControl();
            if (_sharedNativeHlsPlayerControl)
            {
                report.SharedPlayerControl = engine.CaptureSharedNativePlayerControl();
                Require(report.SharedPlayerControl.PlayersCreated == 1 && report.SharedPlayerControl.PlayersDisposed == 1
                    && report.SharedPlayerControl.SessionAssignments == report.RequiredLoops * 2
                    && report.SharedPlayerControl.DistinctPlaybackIds == report.RequiredLoops * 2
                    && report.SharedPlayerControl.SourceClearChecks == report.RequiredLoops * 2, "SharedPlayerControlLifecycleInvalid");
            }
            await authenticated.LogoutAsync(deadline.Token); authenticated = null;
            var idleRequests = observation.CompletedRequests;
            var idleNativeRequests = nativeHttpControl?.Capture().RequestsAccepted;
            foreach (var seconds in new[] { 2, 8, 20 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
                report.FinalIdleSamples.Add(await CaptureResourcesAsync(deadline.Token));
                SaveRealHls(report);
            }
            Require(idleRequests == observation.CompletedRequests, "ApiRequestsContinuedDuringFinalIdle");
            if (nativeHttpControl is not null)
            {
                report.NativeHttpControl = nativeHttpControl.Capture();
                Require(report.NativeHttpControl.FiltersCreated == report.RequiredLoops * 2
                    && report.NativeHttpControl.FiltersDisposed == report.NativeHttpControl.FiltersCreated
                    && report.NativeHttpControl.RequestsRejected == 0
                    && report.NativeHttpControl.RequestsAccepted == idleNativeRequests, "NativeHttpControlLifecycleInvalid");
            }
            var samples = report.Loops.Select(loop => loop.Resources!).ToArray();
            report.PostWarmupPrivateBytesGrowth = Median(samples.TakeLast(5).Select(value => value.PrivateBytes))
                - Median(samples.Skip(4).Take(5).Select(value => value.PrivateBytes));
            report.PostWarmupHandleGrowth = checked((int)(Median(samples.TakeLast(5).Select(value => (long)value.HandleCount))
                - Median(samples.Skip(4).Take(5).Select(value => (long)value.HandleCount))));
            var sustainedHandles = samples.TakeLast(8).Zip(samples.TakeLast(7), (a, b) => b.HandleCount > a.HandleCount).All(value => value);
            var sustainedPrivate = samples.TakeLast(8).Zip(samples.TakeLast(7), (a, b) => b.PrivateBytes - a.PrivateBytes > 1_048_576).All(value => value);
            Require(report.PostWarmupPrivateBytesGrowth <= 64L * 1024 * 1024 && report.PostWarmupHandleGrowth <= 32
                && !sustainedHandles && !sustainedPrivate, "ResourceGrowthDetected");
            Require(report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
            report.Status = _nativeHlsHttpControl ? "ControlPassed" : "Passed";
        }
        catch (Exception exception) { report.Status = "Failed"; report.ErrorCode = ErrorCode(exception); report.ErrorHResult = exception.HResult; }
        finally
        {
            try
            {
                try { if (coordinator is not null) await coordinator.DisposeAsync(); }
                finally { engine?.DisposeSharedNativePlayerControl(); }
                if (authenticated is not null) await authenticated.LogoutAsync();
            }
            catch (Exception exception) { report.Status = "Failed"; report.ErrorCode ??= "FinalCleanup_" + ErrorCode(exception); }
            report.FinishedAt = DateTimeOffset.UtcNow;
            report.NativeHttpControl = nativeHttpControl?.Capture();
            report.SharedPlayerControl = _sharedNativeHlsPlayerControl ? engine?.CaptureSharedNativePlayerControl() : null;
            SaveRealHls(report);
            Environment.ExitCode = report.Status is "Passed" or "ControlPassed" or "DiagnosticCyclesCompleted" ? 0 : 1;
            _window!.Close(); Exit();
        }
    }

    private async Task RunRealHlsLoopAsync(RealHlsLoop loop, PlaybackCoordinator coordinator,
        NativePlaybackEngine engine, HlsApiObservationHandler observation, EmbyApiClient api, HttpClient http,
        NativeHlsHttpControlObservation? nativeHttpControl, CancellationToken ct)
    {
        var firstEvent = observation.Events.Length;
        await coordinator.PlayAsync(new PlaybackSelection
        {
            ItemId = "5", ForceTranscoding = true, StartPositionTicks = loop.InitialPositionTicks,
            SubtitleStreamIndex = -1, MaxStreamingBitrate = 2_000_000
        }, ct);
        var context = coordinator.ActiveContext;
        Require(context is { DeliveryMethod: PlaybackDeliveryMethod.Transcode, TimelineOffsetTicks: 0 }
            && context.Source.TranscodingSubProtocol?.Equals("hls", StringComparison.OrdinalIgnoreCase) == true, "FullSourceHlsRequired");
        var player = _element!.MediaPlayer;
        Require(player is not null && player.IsMuted, "MutedNativePlayerRequired");
        loop.TimelineOffsetTicks = context!.TimelineOffsetTicks;
        loop.OpenedNativePositionTicks = player!.PlaybackSession.Position.Ticks;
        loop.OpenedSnapshotPositionTicks = engine.Snapshot?.PositionTicks ?? -1;
        loop.OpenedNativeDurationTicks = player.PlaybackSession.NaturalDuration.Ticks;
        loop.AdaptiveCreationResponsePresentAfterOpen = engine.HasAdaptiveCreationResponseForProbe;
        if (_sharedNativeHlsPlayerControl)
        {
            loop.SharedPlayerSourceBoundAfterOpen = engine.SharedNativeControlSourceBoundForProbe;
            Require(loop.SharedPlayerSourceBoundAfterOpen == true, "SharedPlayerSourceMissingAfterOpen");
        }
        loop.SourceDurationTicks = context.Source.RunTimeTicks ?? 0;
        var diagnosticPlaylistUri = _realHlsDiagnostic ? api.ResolveMediaUri(context.Source.TranscodingUrl!) : null;
        if (diagnosticPlaylistUri is not null) await CapturePlaylistSummaryAsync(diagnosticPlaylistUri, api, http, loop, "AfterOpen", ct);
        Require(Math.Abs(loop.OpenedNativePositionTicks - loop.InitialPositionTicks) <= 15_000_000,
            "InitialNativeHlsPositionMismatch");
        loop.Stage = "NativeClock";
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > loop.InitialPositionTicks + 2_000_000
            && player.PlaybackSession.NaturalVideoWidth > 0 && player.PlaybackSession.NaturalVideoHeight > 0,
            "NativeHlsClockDidNotAdvance", ct);
        loop.VideoWidth = player.PlaybackSession.NaturalVideoWidth; loop.VideoHeight = player.PlaybackSession.NaturalVideoHeight;
        loop.Stage = "Pause";
        await coordinator.PauseAsync(ct);
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused, "NativeHlsPauseFailed", ct);
        await Task.Delay(150, ct);
        var pauseStart = player.PlaybackSession.Position.Ticks;
        await Task.Delay(650, ct);
        loop.PausedDriftTicks = Math.Abs(player.PlaybackSession.Position.Ticks - pauseStart);
        Require(loop.PausedDriftTicks <= 500_000, "PausedHlsClockAdvanced");
        loop.Stage = "Seek"; loop.SeekTargetTicks = 450_000_000;
        loop.SeekBeforeNativePositionTicks = player.PlaybackSession.Position.Ticks;
        loop.SeekBeforeNativeDurationTicks = player.PlaybackSession.NaturalDuration.Ticks;
        loop.SeekBeforeNativeState = player.PlaybackSession.PlaybackState.ToString();
        loop.SeekBeforeCanSeek = player.PlaybackSession.CanSeek;
        loop.SeekBeforeEngineCanSeekInPlace = engine.Snapshot?.CanSeek == true;
        var beforeSeekPlaybackId = coordinator.ActiveContext!.PlaybackId;
        loop.SeekBeforeSeekableRanges = player.PlaybackSession.GetSeekableRanges().Select(value => new NativeTimeRangeSummary
            { StartTicks = value.Start.Ticks, EndTicks = value.End.Ticks }).ToList();
        using var seekObservation = new HlsSeekObservation(engine, coordinator.ActiveContext!.PlaybackId,
            player, _window!.DispatcherQueue, loop);
        var seeking = coordinator.SeekAsync(loop.SeekTargetTicks, ct);
        var seekWatch = System.Diagnostics.Stopwatch.StartNew();
        var sampledPendingPlaylist = false;
        while (!seeking.IsCompleted)
        {
            seekObservation.Sample("WhileAwaitingSeekCompletion");
            if (!sampledPendingPlaylist && diagnosticPlaylistUri is not null && seekWatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                sampledPendingPlaylist = true;
                await CapturePlaylistSummaryAsync(diagnosticPlaylistUri, api, http, loop, "SeekStillPending", ct);
            }
            await Task.WhenAny(seeking, Task.Delay(100, ct));
        }
        seekObservation.Sample("SeekTaskCompleted");
        await seeking;
        player = _element.MediaPlayer;
        Require(player is not null && player.IsMuted && coordinator.ActiveContext?.TimelineOffsetTicks == 0, "HlsSeekContextLost");
        loop.PlaybackReplacedBySeek = coordinator.ActiveContext!.PlaybackId != beforeSeekPlaybackId;
        loop.AdaptiveCreationResponsePresentAfterSeek = engine.HasAdaptiveCreationResponseForProbe;
        if (_sharedNativeHlsPlayerControl)
        {
            loop.SharedPlayerSourceBoundAfterSeek = engine.SharedNativeControlSourceBoundForProbe;
            Require(loop.PlaybackReplacedBySeek && loop.SharedPlayerSourceBoundAfterSeek == true, "SharedPlayerSourceNotReplacedBySeek");
        }
        await WaitAsync(() => player!.PlaybackSession.PlaybackState == MediaPlaybackState.Paused, "HlsSeekDidNotPreservePause", ct);
        loop.PausePreservedAfterSeek = true;
        loop.SeekObservedNativeTicks = player!.PlaybackSession.Position.Ticks;
        loop.SeekObservedSnapshotTicks = engine.Snapshot?.PositionTicks ?? -1;
        Require(Math.Abs(loop.SeekObservedNativeTicks - loop.SeekTargetTicks) <= 7_500_000, "NativeHlsSeekPositionMismatch");
        loop.Stage = "Resume";
        await coordinator.ResumeAsync(ct);
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > loop.SeekObservedNativeTicks + 2_000_000, "NativeHlsResumeFailed", ct);
        loop.ResumeNativePositionTicks = player.PlaybackSession.Position.Ticks;
        await Task.Delay(300, ct);
        loop.Stage = "Stop";
        await coordinator.StopAsync(ct);
        loop.PlayerDetached = _element.MediaPlayer is null && coordinator.ActiveContext is null && coordinator.Status == PlaybackStatus.Idle;
        Require(loop.PlayerDetached, "NativeHlsStopDidNotDetach");
        if (_sharedNativeHlsPlayerControl)
        {
            loop.SharedPlayerSourceClearedAfterStop = engine.SharedNativeControlSourceClearedForProbe;
            Require(loop.SharedPlayerSourceClearedAfterStop == true, "SharedPlayerSourceNotClearedAfterStop");
        }
        player = null;
        var stoppedRequests = observation.CompletedRequests;
        var stoppedNativeRequests = nativeHttpControl?.Capture().RequestsAccepted;
        await Task.Delay(1300, ct);
        loop.NoApiRequestsAfterStop = stoppedRequests == observation.CompletedRequests;
        Require(loop.NoApiRequestsAfterStop, "HlsApiRequestsContinuedAfterStop");
        if (nativeHttpControl is not null)
        {
            loop.NativeHttpControl = nativeHttpControl.Capture();
            loop.NoNativeGuardRequestsAfterStop = stoppedNativeRequests == loop.NativeHttpControl.RequestsAccepted;
            Require(loop.NoNativeGuardRequestsAfterStop == true
                && loop.NativeHttpControl.FiltersCreated == loop.Number * 2
                && loop.NativeHttpControl.FiltersDisposed == loop.NativeHttpControl.FiltersCreated
                && loop.NativeHttpControl.RequestsRejected == 0, "NativeHttpControlStopInvalid");
        }
        loop.ApiEvents = observation.Events.Skip(firstEvent).ToList();
        var groups = loop.ApiEvents.Where(value => value.SessionHash is not null).GroupBy(value => value.SessionHash).ToArray();
        loop.ValidReportOrder = groups.Length > 0 && groups.All(group =>
        {
            var playEvents = group.Where(value => value.Operation is "Start" or "Progress" or "Stop").ToArray();
            return playEvents.Length >= 2 && playEvents[0].Operation == "Start" && playEvents[^1].Operation == "Stop"
                && playEvents.Count(value => value.Operation == "Start") == 1 && playEvents.Count(value => value.Operation == "Stop") == 1
                && group.All(value => value.StatusCode is >= 200 and < 300)
                && group.Any(value => value.Operation == "StopEncoding");
        });
        loop.SuccessfulEncodingCleanups = loop.ApiEvents.Count(value => value.Operation == "StopEncoding" && value.StatusCode is >= 200 and < 300);
        Require(loop.ValidReportOrder && loop.SuccessfulEncodingCleanups > 0, "HlsReportOrEncodingCleanupInvalid");
        loop.Stage = "Complete";
    }

    private void SaveRealHls(RealHlsReport report)
    {
        report.UpdatedAt = DateTimeOffset.UtcNow;
        lock (report.Diagnostics)
        {
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report, RealHlsJsonContext.Default.RealHlsReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
    }

    private static async Task CapturePlaylistSummaryAsync(Uri uri, EmbyApiClient api, HttpClient http,
        RealHlsLoop loop, string observationPoint, CancellationToken ct)
    {
        for (var depth = 0; depth < 2; depth++)
        {
            Require(uri.IsLoopback && uri.Port == 19096 && uri.Scheme == "http", "OwnedPlaylistEndpointRequired");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var header in api.GetMediaRequestHeaders(uri)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await http.SendAsync(request, ct);
            Require(response.IsSuccessStatusCode, "PlaylistDiagnosticRequestFailed");
            var text = await response.Content.ReadAsStringAsync(ct);
            Require(text.Length <= 1024 * 1024 && text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal), "PlaylistDiagnosticInvalid");
            var lines = text.Split('\n').Select(value => value.Trim()).ToArray();
            var durations = lines.Where(value => value.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .Select(value => double.TryParse(value[8..].Split(',')[0], CultureInfo.InvariantCulture, out var duration) ? duration : 0).ToArray();
            var sequence = lines.FirstOrDefault(value => value.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal));
            var start = lines.FirstOrDefault(value => value.StartsWith("#EXT-X-START:", StringComparison.Ordinal));
            var offset = start?[13..].Split(',').FirstOrDefault(value => value.StartsWith("TIME-OFFSET=", StringComparison.Ordinal));
            var playlistType = lines.FirstOrDefault(value => value.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal));
            var typeValue = playlistType is null ? null : playlistType[21..];
            loop.PlaylistSummaries.Add(new HlsPlaylistSummary
            {
                Kind = depth == 0 ? "NegotiatedPlaylist" : "MediaPlaylist", ObservationPoint = observationPoint,
                PlaylistType = typeValue is "VOD" or "EVENT" ? typeValue : typeValue is null ? null : "Other",
                SegmentCount = durations.Length,
                DurationSeconds = durations.Sum(), HasEndList = lines.Contains("#EXT-X-ENDLIST"),
                MediaSequence = sequence is not null && long.TryParse(sequence[22..], out var number) ? number : null,
                StartOffsetSeconds = offset is not null && double.TryParse(offset[12..], CultureInfo.InvariantCulture, out var seconds) ? seconds : null
            });
            if (durations.Length > 0) break;
            var child = lines.FirstOrDefault(value => value.Length > 0 && !value.StartsWith('#'));
            if (child is null) break;
            uri = new Uri(uri, child);
        }
    }
}
