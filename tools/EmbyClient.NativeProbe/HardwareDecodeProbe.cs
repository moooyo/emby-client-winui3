using System.Diagnostics;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private bool _hardwareDecodeMode;
    private bool _hardwareDecodeRequested = true;
    private bool _requireHardwareDecode;
    private string? _requireHardwareVendor;
    private string _decoderApiRequested = "Auto";
    private bool _expectDecoderApiFallback;

    private void ConfigureHardwareDecodeMode(string[] arguments)
    {
        _hardwareDecodeMode = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var softwareControl = false;
        string? explicitApi = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!seen.Add(arguments[index])) throw new ArgumentException("Repeated hardware decoding option.");
            switch (arguments[index])
            {
                case "--software-decode":
                    softwareControl = true;
                    break;
                case "--decoder-api" when index + 1 < arguments.Length:
                    explicitApi = arguments[++index].ToLowerInvariant() switch
                    {
                        "auto" => "Auto", "d3d11" => "D3D11", "intelqsv" => "IntelQsv",
                        "amdamf" => "AmdAmf", "nvidianvdec" => "NvidiaNvdec", "software" => "Software",
                        _ => throw new ArgumentException("The decoder API must be Auto, D3D11, IntelQsv, AmdAmf, NvidiaNvdec, or Software.")
                    };
                    break;
                case "--expect-api-fallback":
                    _expectDecoderApiFallback = true;
                    break;
                case "--require-hardware":
                    _requireHardwareDecode = true;
                    break;
                case "--require-vendor" when index + 1 < arguments.Length:
                    _requireHardwareVendor = arguments[++index].ToLowerInvariant() switch
                    {
                        "nvidia" => "Nvidia", "intel" => "Intel", "amd" => "Amd",
                        _ => throw new ArgumentException("The required graphics vendor must be Nvidia, Intel, or Amd.")
                    };
                    _requireHardwareDecode = true;
                    break;
                default:
                    throw new ArgumentException("Usage: --hardware-decode [--decoder-api Auto|D3D11|IntelQsv|AmdAmf|NvidiaNvdec|Software] [--software-decode | --require-hardware] [--require-vendor Nvidia|Intel|Amd] [--expect-api-fallback].");
            }
        }
        if (softwareControl && explicitApi is not null and not "Software")
            throw new ArgumentException("The software decoding control cannot select a hardware decoder API.");
        _decoderApiRequested = explicitApi ?? (softwareControl ? "Software" : "Auto");
        _hardwareDecodeRequested = _decoderApiRequested != "Software";
        if (!_hardwareDecodeRequested && _requireHardwareDecode)
            throw new ArgumentException("A software decoding control cannot require a hardware decoder.");
        if (_expectDecoderApiFallback && (!IsVendorDecoderApi(_decoderApiRequested) || _requireHardwareDecode))
            throw new ArgumentException("Expected API fallback requires an explicit vendor API without a hardware/vendor requirement.");
        if (IsVendorDecoderApi(_decoderApiRequested) && _requireHardwareVendor is not null
            && VendorForDecoderApi(_decoderApiRequested) != _requireHardwareVendor)
            throw new ArgumentException("The required graphics vendor must match the selected decoder API.");
    }

    private async Task RunHardwareDecodeAsync()
    {
        var report = new HardwareDecodeReport
        {
            HardwareRequested = _hardwareDecodeRequested,
            HardwareRequired = _requireHardwareDecode,
            RequiredVendor = _requireHardwareVendor,
            RequestedApi = _decoderApiRequested,
            ExpectedApiFallback = _expectDecoderApiFallback,
            ExecutionMode = _hardwareDecodeRequested ? "ProductHardwareDecoding" : "ProductSoftwareDecodingControl"
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var http = EmbyApiClient.CreateHttpClient();
        EmbyApiClient? authenticated = null;
        PlaybackCoordinator? coordinator = null;
        NativePlaybackEngine? engine = null;
        try
        {
            SaveHardwareDecode(report);
            Require(report.NativeAot, "NativeAotRequired");
            var before = await GetStatsAsync(http, timeout.Token);
            var api = new EmbyApiClient(http, FixtureRoot, new ClientIdentity("SYNTHETIC Hardware Decode Probe",
                "Windows native probe", "synthetic-hardware-decoding-probe", "0.1.0"));
            var info = await api.GetPublicSystemInfoAsync(timeout.Token);
            Require(info.Id == "synthetic-server-0001" && info.ServerName == "SYNTHETIC Emby Client Fixture"
                && info.Version == "synthetic-1.0", "SyntheticServerRequired");
            var authentication = await api.AuthenticateByNameAsync("demo", "demo", timeout.Token);
            Require(authentication.AccessToken is not null && authentication.User?.Id is not null,
                "SyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", timeout.Token);
            Require(item.Name?.StartsWith("Synthetic", StringComparison.Ordinal) == true
                && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000, "ExpectedSixtySecondSyntheticMedia");
            report.FixtureDurationTicks = item.RunTimeTicks!.Value;
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true,
                initialHardwareDecoding: _hardwareDecodeRequested, initialDecoderApi: _decoderApiRequested);
            engine.Diagnostic += (_, diagnostic) =>
            {
                lock (report.Diagnostics) report.Diagnostics.Add("Native:" + diagnostic.Operation + ":" + diagnostic.ErrorCode);
            };
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(1), ReportTimeout = TimeSpan.FromSeconds(5),
                CleanupTimeout = TimeSpan.FromSeconds(10)
            });
            coordinator.Diagnostic += (_, diagnostic) =>
            {
                lock (report.Diagnostics) report.Diagnostics.Add(diagnostic.Operation + ":" + diagnostic.ErrorCode);
            };
            coordinator.StatusChanged += (_, status) =>
                engine.UpdateMediaControls(status.Context, status.Status, "Synthetic hardware decoding media");

            report.Stage = "Open";
            SaveHardwareDecode(report);
            var watch = Stopwatch.StartNew();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = "1001", MediaSourceId = "synthetic-mp4", SubtitleStreamIndex = -1, StartPositionTicks = 0
            }, timeout.Token);
            report.OpenMilliseconds = watch.Elapsed.TotalMilliseconds;
            var original = coordinator.ActiveContext;
            Require(original is not null && original.TimelineOffsetTicks == 0, "UnexpectedSourceTimeline");
            Require(original!.DeliveryMethod is PlaybackDeliveryMethod.DirectStream or PlaybackDeliveryMethod.Transcode,
                "SyntheticPlaybackDeliveryRequired");
            report.DeliveryMethod = original.DeliveryMethod.ToString();
            var playSessionIds = new HashSet<string>(StringComparer.Ordinal) { original.PlaySessionId };

            report.Stage = "ObservePlayingDecoder";
            SaveHardwareDecode(report);
            await WaitForDecoderAsync(engine, timeout.Token);
            var player = _element!.MediaPlayer!;
            report.InitialMuted = player.IsMuted;
            Require(report.InitialMuted, "InitialMuteNotPreserved");
            AddHardwareDecodeObservation(report, engine, coordinator, "Playing");
            // Keep the product graph active for external process-scoped GPU and CPU sampling.
            var previousPosition = player.PlaybackSession.Position.Ticks;
            for (var second = 1; second <= 15; second++)
            {
                await Task.Delay(1000, timeout.Token);
                Require(coordinator.Status == PlaybackStatus.Playing
                    && player.PlaybackSession.Position.Ticks > previousPosition, "DecoderObservationClockStopped");
                previousPosition = player.PlaybackSession.Position.Ticks;
                AddHardwareDecodeObservation(report, engine, coordinator, "PlayingObservation");
                SaveHardwareDecode(report);
            }

            report.Stage = "Pause";
            SaveHardwareDecode(report);
            await coordinator.PauseAsync(timeout.Token);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused,
                "NativePauseDidNotComplete", timeout.Token);
            await Task.Delay(150, timeout.Token);
            report.PauseStartTicks = player.PlaybackSession.Position.Ticks;
            await Task.Delay(650, timeout.Token);
            report.PauseEndTicks = player.PlaybackSession.Position.Ticks;
            Require(Math.Abs(report.PauseEndTicks - report.PauseStartTicks) <= 500_000, "PausedClockAdvanced");

            report.Stage = "Seek";
            SaveHardwareDecode(report);
            await coordinator.SeekAsync(report.SeekTargetTicks, timeout.Token);
            player = _element.MediaPlayer ?? throw new ProbeFailure("NativePlayerMissingAfterSeek");
            report.SeekObservedTicks = player.PlaybackSession.Position.Ticks;
            Require(Math.Abs(report.SeekObservedTicks - report.SeekTargetTicks) <= 7_500_000,
                "NativeSeekPositionMismatch");
            report.SourceTimelinePreserved = coordinator.ActiveContext?.TimelineOffsetTicks == 0;
            Require(report.SourceTimelinePreserved, "SourceTimelineChanged");
            if (coordinator.ActiveContext is { } afterSeek) playSessionIds.Add(afterSeek.PlaySessionId);

            report.Stage = "Resume";
            SaveHardwareDecode(report);
            await coordinator.ResumeAsync(timeout.Token);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                && player.PlaybackSession.Position.Ticks > report.SeekObservedTicks + 2_000_000,
                "NativeResumeClockDidNotAdvance", timeout.Token);
            await WaitForDecoderAsync(engine, timeout.Token);
            report.ResumePositionTicks = player.PlaybackSession.Position.Ticks;
            Require(player.IsMuted, "ResumeMuteNotPreserved");
            AddHardwareDecodeObservation(report, engine, coordinator, "AfterResume");
            await Task.Delay(300, timeout.Token);

            report.Stage = "Stop";
            SaveHardwareDecode(report);
            await coordinator.StopAsync(timeout.Token);
            report.PlayerDetachedAfterStop = _element.MediaPlayer is null && coordinator.ActiveContext is null
                && coordinator.Status == PlaybackStatus.Idle;
            report.StoppedSnapshotObserved = engine.Snapshot is { State: PlaybackEngineState.Stopped };
            report.LastDecoderSnapshotRetainedAfterStop = IsObservedDecoder(engine.VideoDecoding);
            Require(report.PlayerDetachedAfterStop && report.StoppedSnapshotObserved, "NativeStopDidNotDetach");
            Require(report.LastDecoderSnapshotRetainedAfterStop, "LastDecoderSnapshotMissingAfterStop");
            player = null;
            var stopped = await GetStatsAsync(http, timeout.Token);
            await Task.Delay(1300, timeout.Token);
            var settled = await GetStatsAsync(http, timeout.Token);
            report.NoNetworkAfterStop = stopped.MediaRequests == settled.MediaRequests
                && stopped.RangeRequests == settled.RangeRequests && stopped.PlaybackInfoCount == settled.PlaybackInfoCount
                && stopped.Events.Count(value => playSessionIds.Contains(value.PlaySessionId))
                    == settled.Events.Count(value => playSessionIds.Contains(value.PlaySessionId));
            Require(report.NoNetworkAfterStop, "PlaybackNetworkContinuedAfterStop");
            var events = settled.Events.Where(value => playSessionIds.Contains(value.PlaySessionId)).ToArray();
            report.StartReports = events.Count(value => value.Kind == "Start");
            report.ProgressReports = events.Count(value => value.Kind == "Progress");
            report.StopReports = events.Count(value => value.Kind == "Stop");
            report.ReportOrderValid = events.Length >= 4 && events[0].Kind == "Start" && events[^1].Kind == "Stop"
                && report.StartReports == report.StopReports && report.StartReports >= 1
                && events.All(value => value.Failed != true) && events.Any(value => value.EventName == "Pause")
                && events.Any(value => value.EventName == "Unpause");
            Require(report.ReportOrderValid, "PlaybackReportOrderInvalid");
            report.StopReportPositionTicks = events[^1].PositionTicks;
            Require(report.StopReportPositionTicks >= report.SeekTargetTicks - 7_500_000
                && report.StopReportPositionTicks <= report.SeekTargetTicks + 30_000_000, "StopReportSourcePositionMismatch");
            report.MediaRequests = settled.MediaRequests - before.MediaRequests;
            report.PartialResponses = settled.PartialResponses - before.PartialResponses;
            Require(report.MediaRequests > 0 && before.AuthenticationFailures == settled.AuthenticationFailures,
                "AuthenticatedMediaEvidenceMissing");
            await coordinator.DisposeAsync();
            coordinator = null;
            engine = null;
            await authenticated.LogoutAsync(timeout.Token);
            authenticated = null;
            report.ResourcesAfterCleanup = await CaptureResourcesAsync(timeout.Token);
            lock (report.Diagnostics) Require(report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
            report.Stage = "Complete";
            report.Status = "Passed";
        }
        catch (Exception exception)
        {
            if (engine?.VideoDecoding is not null && coordinator is not null && _element?.MediaPlayer is not null)
            {
                try { AddHardwareDecodeObservation(report, engine, coordinator, "Failure", validate: false); }
                catch { }
            }
            report.Status = "Failed";
            report.ErrorCode = ErrorCode(exception);
            report.ErrorHResult = exception.HResult;
        }
        finally
        {
            try
            {
                if (coordinator is not null) await coordinator.DisposeAsync();
                else if (engine is not null) await engine.DisposeAsync();
                if (authenticated is not null) await authenticated.LogoutAsync();
            }
            catch (Exception exception)
            {
                report.Status = "Failed";
                report.ErrorCode ??= "FinalCleanup_" + ErrorCode(exception);
                report.ErrorHResult ??= exception.HResult;
            }
            report.FinishedAt = DateTimeOffset.UtcNow;
            SaveHardwareDecode(report);
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private async Task WaitForDecoderAsync(NativePlaybackEngine engine, CancellationToken token, string? requestedApi = null)
    {
        var expectedApi = requestedApi ?? _decoderApiRequested;
        await WaitAsync(() => _element!.MediaPlayer is { } player
            && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > 2_000_000
            && player.PlaybackSession.NaturalVideoWidth > 0 && player.PlaybackSession.NaturalVideoHeight > 0
            && engine.VideoDecoding is { } decoding && IsObservedDecoder(decoding)
            && (expectedApi == "Software" || !_requireHardwareDecode
                || decoding.ActualApi == (IsVendorDecoderApi(expectedApi) ? expectedApi : "D3D11"))
            && (expectedApi == "Software" || _requireHardwareVendor is null
                || GraphicsVendor(decoding.GpuVendorId) == _requireHardwareVendor),
            "ActualVideoDecoderNotObserved", token);
    }

    private void AddHardwareDecodeObservation(HardwareDecodeReport report, NativePlaybackEngine engine,
        PlaybackCoordinator coordinator, string stage, bool validate = true)
    {
        var decoding = engine.VideoDecoding ?? throw new ProbeFailure("ActualVideoDecoderSnapshotMissing");
        var player = _element!.MediaPlayer ?? throw new ProbeFailure("NativePlayerMissing");
        var vendor = GraphicsVendor(decoding.GpuVendorId);
        report.DecodingSamples.Add(new HardwareDecodeObservation
        {
            Stage = stage, HardwareRequested = decoding.HardwareRequested, Decoder = decoding.Decoder,
            RequestedApi = decoding.RequestedApi, ActualApi = decoding.ActualApi,
            FallbackReason = decoding.FallbackReason, NativeDecoderName = decoding.NativeDecoderName,
            DecodedFrames = decoding.DecodedFrames, HardwareDecodedFrames = decoding.HardwareDecodedFrames,
            HardwareFallback = decoding.HardwareFallback, Codec = decoding.Codec, GpuName = decoding.GpuName,
            GpuVendorId = decoding.GpuVendorId, GpuVendor = vendor, AdapterLuid = decoding.AdapterLuid,
            NativePositionTicks = player.PlaybackSession.Position.Ticks,
            EnginePositionTicks = engine.Snapshot?.PositionTicks ?? 0,
            TimelineOffsetTicks = coordinator.ActiveContext?.TimelineOffsetTicks ?? -1,
            VideoWidth = player.PlaybackSession.NaturalVideoWidth, VideoHeight = player.PlaybackSession.NaturalVideoHeight
        });
        if (!validate) return;
        ValidateDecoderSelection(decoding, _decoderApiRequested);
    }

    private static bool IsVendorDecoderApi(string api) => api is "IntelQsv" or "AmdAmf" or "NvidiaNvdec";

    private static bool IsObservedDecoder(VideoDecodingSnapshot? decoding) =>
        decoding is { ActualApi: "D3D11" or "IntelQsv" or "AmdAmf" or "NvidiaNvdec" or "Software" }
        && (!IsVendorDecoderApi(decoding.ActualApi) || decoding.HardwareDecodedFrames > 0);

    private static string? VendorForDecoderApi(string api) => api switch
    {
        "IntelQsv" => "Intel", "AmdAmf" => "Amd", "NvidiaNvdec" => "Nvidia", _ => null
    };

    private static bool IsAllowedApiFallbackReason(string? reason) => reason is "DecoderApiUnavailable"
        or "CodecUnsupported" or "DeviceUnavailable" or "DecoderInitializationFailed" or "DecoderRuntimeFailed";

    private void ValidateDecoderSelection(VideoDecodingSnapshot decoding, string requestedApi)
    {
        var hardwareRequested = requestedApi != "Software";
        Require(IsObservedDecoder(decoding), "ActualVideoDecoderNotObserved");
        Require(decoding.HardwareRequested == hardwareRequested, "HardwareDecodingConfigurationMismatch");
        Require(decoding.RequestedApi == requestedApi, "RequestedDecoderApiNotRetained");
        if (!hardwareRequested)
        {
            Require(decoding.ActualApi == "Software" && decoding.Decoder == "Software"
                && !decoding.HardwareFallback, "SoftwareDecoderRequired");
            return;
        }
        if (_expectDecoderApiFallback)
        {
            Require(IsVendorDecoderApi(requestedApi) && decoding.ActualApi == "Software" && decoding.Decoder == "Software"
                && decoding.HardwareFallback && IsAllowedApiFallbackReason(decoding.FallbackReason), "ExpectedDecoderApiFallbackRequired");
            return;
        }
        if (IsVendorDecoderApi(requestedApi))
        {
            Require(decoding.ActualApi == requestedApi && !decoding.HardwareFallback, "SelectedVendorDecoderApiRequired");
            var suffix = requestedApi switch { "IntelQsv" => "_qsv", "AmdAmf" => "_amf", _ => "_cuvid" };
            Require(decoding.NativeDecoderName?.EndsWith(suffix, StringComparison.Ordinal) == true
                && decoding.HardwareDecodedFrames > 0, "VendorHardwareFrameEvidenceRequired");
        }
        else if (_requireHardwareDecode)
            Require(decoding.ActualApi == "D3D11" && decoding.Decoder == "D3D11"
                && !decoding.HardwareFallback, "HardwareDecoderRequired");
        else
            Require(decoding.ActualApi is "D3D11" or "Software"
                && decoding.HardwareFallback == (decoding.ActualApi == "Software"), "AutomaticDecoderEvidenceInvalid");
        if (_requireHardwareVendor is not null)
            Require(GraphicsVendor(decoding.GpuVendorId) == _requireHardwareVendor, "RequiredGraphicsVendorNotSelected");
    }

    private static string? GraphicsVendor(uint? vendorId) => vendorId switch
    {
        0x10DE => "Nvidia", 0x8086 => "Intel", 0x1002 => "Amd", _ => null
    };

    private void SaveHardwareDecode(HardwareDecodeReport report)
    {
        report.UpdatedAt = DateTimeOffset.UtcNow;
        lock (report.Diagnostics)
        {
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report,
                HardwareDecodeJsonContext.Default.HardwareDecodeReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
    }
}
