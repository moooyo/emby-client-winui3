using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.Storage;

namespace EmbyClient.NativeProbe;

public sealed partial class App : Application
{
    private static readonly Uri FixtureRoot = new("http://127.0.0.1:18961/emby/");
    private readonly ProbeReport _report = new();
    private Window? _window;
    private MediaPlayerElement? _element;
    private string? _resultPath;
    private string? _controlMode;
    private string? _controlMediaDirectory;
    private string? _realHlsCredentialsPath;
    private bool _realHlsDiagnostic;
    private bool _nativeHlsHttpControl;
    private bool _sharedNativeHlsPlayerControl;
    private bool _lifecycleIsolation;
    private string? _networkRetryMediaDirectory;
    private bool _noInFlightGc;
    private bool _noGcRegionActive;
    private const long NoGcRegionBudget = 64L * 1024 * 1024;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string directory;
        if (commandLine is ["--output-dir", var normalDirectory]) directory = normalDirectory;
        else if (commandLine is ["--output-dir", var isolationDirectory, "--lifecycle-isolation"])
        {
            directory = isolationDirectory;
            _lifecycleIsolation = true;
        }
        else if (commandLine is ["--output-dir", var networkDirectory, "--network-retry", "--media-dir", var networkMediaDirectory])
        {
            directory = networkDirectory;
            _networkRetryMediaDirectory = Path.GetFullPath(networkMediaDirectory);
        }
        else if (commandLine is ["--output-dir", var subtitleDirectory, "--external-subtitle", "--credentials-file", var subtitleCredentials])
        {
            directory = subtitleDirectory;
            _externalSubtitleCredentialsPath = Path.GetFullPath(subtitleCredentials);
        }
        else if (commandLine is ["--output-dir", var smokeDirectory, "--instrumentation-smoke"])
        {
            directory = smokeDirectory;
            _report.ExecutionMode = "InstrumentationSmoke";
            _report.RequiredLoops = 1;
            _report.Scope = "One synthetic product lifecycle to validate probe instrumentation. Not a 20-loop resource regression result.";
        }
        else if (commandLine is ["--output-dir", var realDirectory, "--real-hls", "--credentials-file", var credentialPath])
        {
            directory = realDirectory;
            _realHlsCredentialsPath = Path.GetFullPath(credentialPath);
        }
        else if (commandLine is ["--output-dir", var diagnosticDirectory, "--real-hls", "--credentials-file", var diagnosticCredentialPath, "--diagnostic-two-loops"])
        {
            directory = diagnosticDirectory;
            _realHlsCredentialsPath = Path.GetFullPath(diagnosticCredentialPath);
            _realHlsDiagnostic = true;
        }
        else if (commandLine is ["--output-dir", var nativeHlsDirectory, "--real-hls", "--credentials-file", var nativeHlsCredentialPath, "--native-http-control"])
        {
            directory = nativeHlsDirectory;
            _realHlsCredentialsPath = Path.GetFullPath(nativeHlsCredentialPath);
            _nativeHlsHttpControl = true;
        }
        else if (commandLine is ["--output-dir", var sharedHlsDirectory, "--real-hls", "--credentials-file", var sharedHlsCredentialPath, "--native-http-shared-player-control"])
        {
            directory = sharedHlsDirectory;
            _realHlsCredentialsPath = Path.GetFullPath(sharedHlsCredentialPath);
            _nativeHlsHttpControl = true;
            _sharedNativeHlsPlayerControl = true;
        }
        else if (commandLine is ["--output-dir", var managedSharedDirectory, "--real-hls", "--credentials-file", var managedSharedCredentialPath, "--shared-player-control"])
        {
            directory = managedSharedDirectory;
            _realHlsCredentialsPath = Path.GetFullPath(managedSharedCredentialPath);
            _sharedNativeHlsPlayerControl = true;
        }
        else if (commandLine is ["--output-dir", var experimentDirectory, "--no-inflight-gc"])
        {
            directory = experimentDirectory;
            _noInFlightGc = true;
            _report.ExecutionMode = "NoInFlightGcExperiment";
            _report.NoGcRegionBudgetBytes = NoGcRegionBudget;
            _report.Scope = "SYNTHETIC loopback product lifecycle with a bounded no-GC region from each Open to Stop. Instrumented causality experiment; not normal-runtime regression success or real Emby compatibility.";
        }
        else if (commandLine is ["--output-dir", var fileDirectory, "--control", var fileMode, "--media-dir", var mediaDirectory]
            && fileMode is "file-playback" or "file-stream-playback" or "file-managed-stream-playback")
        {
            directory = fileDirectory;
            _controlMode = fileMode;
            _controlMediaDirectory = Path.GetFullPath(mediaDirectory);
        }
        else if (commandLine is ["--output-dir", var controlDirectory, "--control", var mode]
            && mode is "media-player" or "media-player-configured" or "http-range" or "native-http-playback")
        {
            directory = controlDirectory;
            _controlMode = mode;
        }
        else throw new ArgumentException("Usage: EmbyClient.NativeProbe --output-dir <directory> [--no-inflight-gc | --control <mode> [--media-dir <synthetic-directory>]]. See README for supported modes.");
        Directory.CreateDirectory(Path.GetFullPath(directory));
        _resultPath = Path.Combine(Path.GetFullPath(directory), "result.json");
        if (File.Exists(_resultPath))
            throw new ArgumentException("The output directory already has a result. Select a new run directory.");
        if (_controlMode is null && _realHlsCredentialsPath is null && !_lifecycleIsolation && _networkRetryMediaDirectory is null
            && _externalSubtitleCredentialsPath is null) Save();
        _element = new MediaPlayerElement { AreTransportControlsEnabled = false };
        _window = new Window
        {
            Title = "SYNTHETIC native playback probe",
            Content = _controlMode is null or "file-playback" or "file-stream-playback" or "file-managed-stream-playback" or "native-http-playback"
                ? _element : new TextBlock { Text = "Isolated native resource control: " + _controlMode }
        };
        _window.AppWindow.Resize(new SizeInt32(480, 300));
        _window.AppWindow.Show(activateWindow: false);
        _ = _externalSubtitleCredentialsPath is not null ? RunExternalSubtitleModeAsync()
            : _networkRetryMediaDirectory is not null ? RunNetworkRetryAsync()
            : _lifecycleIsolation ? RunIsolationAsync()
            : _realHlsCredentialsPath is not null ? RunRealHlsAsync()
            : _controlMode is not null ? RunControlAsync(_controlMode) : RunAsync();
    }

    private async Task RunControlAsync(string mode)
    {
        var report = new ControlReport
        {
            Mode = mode,
            Scope = mode == "native-http-playback"
                ? "Isolated actual Windows HTTP playback control: verified SYNTHETIC loopback media negotiated in-process, with a synthetic fixture token attached in-memory to the URI accepted by MediaSource.CreateFromUri. Same native cycle; no product managed stream bridge. URI credentials are never written to reports, command lines, or application logs. Not the product coordinator lifecycle result."
                : mode == "file-managed-stream-playback"
                ? "Isolated actual managed file stream playback control: verified SYNTHETIC local H.264/AAC MP4 via new System.IO.FileStream with asynchronous options, Microsoft AsRandomAccessStream adapter, and MediaSource.CreateFromStream. Same file playback cycle; no HTTP or product Cloneable bridge. Not the product coordinator lifecycle result."
                : mode == "file-stream-playback"
                ? "Isolated actual native file stream playback control: verified SYNTHETIC local H.264/AAC MP4 via StorageFile.OpenReadAsync and MediaSource.CreateFromStream using a Windows-native random-access stream. Same cycle harness as file-playback. No HTTP or managed custom WinRT stream. Not the product coordinator lifecycle result."
                : mode == "file-playback"
                ? "Isolated actual native file playback control: verified SYNTHETIC local H.264/AAC MP4 via MediaSource.CreateFromStorageFile, native player and MediaPlayerElement with corresponding native events. No HTTP or custom WinRT stream bridge. Not the product coordinator lifecycle result."
                : mode == "http-range"
                ? "Isolated product ScopedMediaTransport/HttpRangeStream control against the SYNTHETIC loopback fixture. No MediaPlayer, MediaSource, WinRT stream bridge, rendering, or playback reports. Not the 20-loop product lifecycle result."
                : "Isolated Windows MediaPlayer allocation control. No source, no MediaPlayerElement binding, no transport, no server access, no media playback. Not the 20-loop product lifecycle result.",
            PlayerConfiguration = mode is "media-player-configured" or "file-playback" or "file-stream-playback" or "file-managed-stream-playback" or "native-http-playback"
                ? ["AutoPlay=false", "IsMuted=true", "Volume=1", "CommandManager.IsEnabled=false"] : []
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var http = mode is "http-range" or "native-http-playback" ? EmbyApiClient.CreateHttpClient() : null;
        EmbyApiClient? authenticated = null;
        string? syntheticToken = null;
        StorageFile? syntheticFile = null;
        try
        {
            Require(report.NativeAot, "NativeAotRequired");
            SaveControl(report);
            await Task.Delay(TimeSpan.FromSeconds(10), timeout.Token);
            if (mode is "file-playback" or "file-stream-playback" or "file-managed-stream-playback")
                syntheticFile = await LoadSyntheticFileAsync(timeout.Token);
            if (http is not null)
            {
                await GetStatsAsync(http, timeout.Token);
                var api = new EmbyApiClient(http, FixtureRoot,
                    new ClientIdentity("SYNTHETIC RangeControl", "Windows native probe", "synthetic-range-control", "0.1.0"));
                var info = await api.GetPublicSystemInfoAsync(timeout.Token);
                Require(info.ServerName == "SYNTHETIC Emby Client Fixture" && info.Version == "synthetic-1.0",
                    "SyntheticServerRequired");
                var authentication = await api.AuthenticateByNameAsync("demo", "demo", timeout.Token);
                Require(authentication.AccessToken is not null && authentication.User?.Id is not null,
                    "SyntheticAuthenticationFailed");
                authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
                syntheticToken = authentication.AccessToken;
                var item = await authenticated.GetItemAsync("1001", timeout.Token);
                Require(item.Name?.StartsWith("Synthetic", StringComparison.Ordinal) == true
                    && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000, "ExpectedSixtySecondSyntheticMedia");
            }
            report.Baseline = await CaptureResourcesAsync(timeout.Token);
            SaveControl(report);
            for (var index = 0; index < report.RequiredCycles; index++)
            {
                FixtureStats? before = null;
                FixtureStats? stopped = null;
                if (authenticated is not null)
                {
                    before = await GetStatsAsync(http!, timeout.Token);
                    if (mode == "native-http-playback")
                    {
                        var uri = await GetSyntheticStreamUriAsync(authenticated, timeout.Token);
                        Require(uri.IsLoopback && uri.Host == FixtureRoot.Host && uri.Port == FixtureRoot.Port
                            && uri.Scheme == FixtureRoot.Scheme && uri.UserInfo.Length == 0, "SyntheticLoopbackMediaRequired");
                        var builder = new UriBuilder(uri)
                        {
                            Query = uri.Query.TrimStart('?') + "&api_key=" + Uri.EscapeDataString(syntheticToken!)
                        };
                        report.NativeCycles.Add(await RunFileCycleAsync(null, index + 1, mode,
                            timeout.Token, builder.Uri));
                    }
                    else await ReadAndDisposeRangeAsync(authenticated, timeout.Token);
                    stopped = await GetStatsAsync(http!, timeout.Token);
                }
                else if (syntheticFile is not null)
                    report.NativeCycles.Add(await RunFileCycleAsync(syntheticFile, index + 1, mode, timeout.Token));
                else await CreateAndDisposePlayerAsync(mode == "media-player-configured", timeout.Token);
                await Task.Delay(1300, timeout.Token);
                if (before is not null && stopped is not null)
                {
                    var settled = await GetStatsAsync(http!, timeout.Token);
                    Require(stopped.MediaRequests == settled.MediaRequests && stopped.RangeRequests == settled.RangeRequests,
                        "RangeNetworkContinuedAfterDispose");
                    Require(settled.MediaRequests > before.MediaRequests
                        && (mode == "native-http-playback" || settled.PartialResponses > before.PartialResponses)
                        && settled.AuthenticationFailures == before.AuthenticationFailures, "AuthenticatedRangeEvidenceMissing");
                    report.MediaRequests += settled.MediaRequests - before.MediaRequests;
                    report.PartialResponses += settled.PartialResponses - before.PartialResponses;
                }
                report.CycleSamples.Add(await CaptureResourcesAsync(timeout.Token));
                report.CompletedCycles++;
                SaveControl(report);
            }
            if (authenticated is not null)
            {
                await authenticated.LogoutAsync(timeout.Token);
                authenticated = null;
                syntheticToken = null;
            }
            foreach (var seconds in new[] { 2, 8, 20 })
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
                report.FinalIdleSamples.Add(await CaptureResourcesAsync(timeout.Token));
                SaveControl(report);
            }
            var samples = report.CycleSamples;
            var earlyPrivate = Median(samples.Skip(4).Take(5).Select(sample => sample.PrivateBytes));
            var latePrivate = Median(samples.TakeLast(5).Select(sample => sample.PrivateBytes));
            var earlyHandles = Median(samples.Skip(4).Take(5).Select(sample => (long)sample.HandleCount));
            var lateHandles = Median(samples.TakeLast(5).Select(sample => (long)sample.HandleCount));
            report.PostWarmupPrivateBytesGrowth = latePrivate - earlyPrivate;
            report.PostWarmupHandleGrowth = checked((int)(lateHandles - earlyHandles));
            var sustainedHandles = samples.TakeLast(8).Zip(samples.TakeLast(7),
                (previous, next) => next.HandleCount > previous.HandleCount).All(increased => increased);
            var sustainedPrivate = samples.TakeLast(8).Zip(samples.TakeLast(7),
                (previous, next) => next.PrivateBytes - previous.PrivateBytes > 1_048_576).All(increased => increased);
            Require(latePrivate - earlyPrivate <= 64L * 1024 * 1024 && lateHandles - earlyHandles <= 32
                && !sustainedHandles && !sustainedPrivate,
                "ResourceGrowthDetected");
            report.Status = "CompletedWithinBoundedResourceLimits";
        }
        catch (Exception exception)
        {
            report.Status = "Failed";
            report.ErrorCode = ErrorCode(exception);
        }
        finally
        {
            if (authenticated is not null)
            {
                try { await authenticated.LogoutAsync(); }
                catch { report.Status = "Failed"; report.ErrorCode ??= "SyntheticLogoutFailed"; }
            }
            report.FinishedAt = DateTimeOffset.UtcNow;
            SaveControl(report);
            Environment.ExitCode = report.ErrorCode is null ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private static async Task CreateAndDisposePlayerAsync(bool configured, CancellationToken ct)
    {
        using var player = new MediaPlayer();
        if (configured)
        {
            player.AutoPlay = false;
            player.IsMuted = true;
            player.Volume = 1;
            player.CommandManager.IsEnabled = false;
        }
        await Task.Delay(50, ct);
    }

    private static async Task ReadAndDisposeRangeAsync(EmbyApiClient api, CancellationToken ct)
    {
        var uri = await GetSyntheticStreamUriAsync(api, ct);
        await using var transport = new ScopedMediaTransport(uri, api.GetMediaRequestHeaders(uri), ct);
        await using var stream = await HttpRangeStream.OpenAsync(transport, uri, ct);
        var buffer = new byte[128 * 1024];
        foreach (var offset in new[] { 0, stream.Length / 2, Math.Max(0, stream.Length - buffer.Length) })
        {
            stream.Seek(offset, SeekOrigin.Begin);
            Require(await stream.ReadAsync(buffer, ct) > 0, "SyntheticRangeWasEmpty");
        }
    }

    private static async Task<Uri> GetSyntheticStreamUriAsync(EmbyApiClient api, CancellationToken ct)
    {
        var response = await api.GetPlaybackInfoAsync("1001", new PlaybackInfoRequest
        {
            UserId = api.UserId, EnableDirectStream = true, EnableTranscoding = false
        }, ct);
        var source = response.MediaSources?.SingleOrDefault();
        Require(source?.Id == "synthetic-mp4" && response.PlaySessionId is not null, "SyntheticRangeSourceRequired");
        return api.BuildVideoStreamUri("1001", source!.Id!, response.PlaySessionId!);
    }

    private void SaveControl(ControlReport report)
    {
        report.UpdatedAt = DateTimeOffset.UtcNow;
        var temporary = _resultPath! + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report, ProbeJsonContext.Default.ControlReport));
        File.Move(temporary, _resultPath!, overwrite: true);
    }

    private async Task RunAsync()
    {
        PlaybackCoordinator? coordinator = null;
        EmbyApiClient? authenticated = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var http = EmbyApiClient.CreateHttpClient();
        try
        {
            Require(_report.NativeAot, "NativeAotRequired");
            Require(FixtureRoot.IsLoopback && FixtureRoot.Host == "127.0.0.1" && FixtureRoot.Port == 18961,
                "SyntheticLoopbackRequired");
            await GetStatsAsync(http, timeout.Token);
            var api = new EmbyApiClient(http, FixtureRoot,
                new ClientIdentity("SYNTHETIC NativeProbe", "Windows native probe", "synthetic-native-probe", "0.1.0"));
            var info = await api.GetPublicSystemInfoAsync(timeout.Token);
            Require(info.ServerName == "SYNTHETIC Emby Client Fixture" && info.Version == "synthetic-1.0",
                "SyntheticServerRequired");
            var authentication = await api.AuthenticateByNameAsync("demo", "demo", timeout.Token);
            Require(authentication.AccessToken is not null && authentication.User?.Id is not null,
                "SyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", timeout.Token);
            _report.FixtureDurationTicks = item.RunTimeTicks;
            Require(item.Name?.StartsWith("Synthetic", StringComparison.Ordinal) == true
                && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000,
                "ExpectedSixtySecondSyntheticMedia");
            var engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
            engine.Diagnostic += (_, diagnostic) =>
            {
                lock (_report.Diagnostics)
                    _report.Diagnostics.Add("Native:" + diagnostic.Operation + ":" + diagnostic.ErrorCode);
            };
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(1),
                ReportTimeout = TimeSpan.FromSeconds(5),
                CleanupTimeout = TimeSpan.FromSeconds(10)
            });
            coordinator.StatusChanged += (_, status) =>
                engine.UpdateMediaControls(status.Context, status.Status, "Synthetic Color Study (NativeProbe)");
            coordinator.Diagnostic += (_, diagnostic) =>
            {
                lock (_report.Diagnostics)
                    _report.Diagnostics.Add(diagnostic.Operation + ":" + diagnostic.ErrorCode);
            };

            for (var number = 1; number <= _report.RequiredLoops; number++)
            {
                var loop = new LoopResult { Number = number, Stage = "Open" };
                _report.Loops.Add(loop);
                Save();
                try
                {
                    try { await RunLoopAsync(loop, coordinator, engine, http, timeout.Token); }
                    finally { if (_noGcRegionActive) EndNoGcRegion(loop); }
                    loop.Resources = await CaptureResourcesAsync(timeout.Token);
                    loop.Status = "Passed";
                    _report.CompletedLoops++;
                }
                catch (Exception exception)
                {
                    loop.Status = "Failed";
                    loop.ErrorCode = ErrorCode(exception);
                    loop.ErrorHResult = exception.HResult;
                    throw;
                }
                finally { Save(); }
            }

            if (_report.ExecutionMode == "InstrumentationSmoke")
            {
                Require(_report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
                _report.ResourceObservation = "Not evaluated: this one-loop instrumentation check is not a resource regression run.";
                _report.Status = "InstrumentationSmokeCompleted";
                return;
            }
            var samples = _report.Loops.Select(loop => loop.Resources!).ToArray();
            var earlyPrivate = Median(samples.Skip(4).Take(5).Select(sample => sample.PrivateBytes));
            var latePrivate = Median(samples.TakeLast(5).Select(sample => sample.PrivateBytes));
            var earlyHandles = Median(samples.Skip(4).Take(5).Select(sample => (long)sample.HandleCount));
            var lateHandles = Median(samples.TakeLast(5).Select(sample => (long)sample.HandleCount));
            _report.PostWarmupPrivateBytesGrowth = latePrivate - earlyPrivate;
            _report.PostWarmupHandleGrowth = checked((int)(lateHandles - earlyHandles));
            var sustainedHandles = samples.TakeLast(8).Zip(samples.TakeLast(7),
                (previous, next) => next.HandleCount > previous.HandleCount).All(increased => increased);
            var sustainedPrivate = samples.TakeLast(8).Zip(samples.TakeLast(7),
                (previous, next) => next.PrivateBytes - previous.PrivateBytes > 1_048_576).All(increased => increased);
            _report.ResourceObservation = "Collecting 30-second final idle evidence after disposing the coordinator and logging out. Per-loop bounded resource criteria are retained.";
            await coordinator.DisposeAsync();
            coordinator = null;
            await authenticated.LogoutAsync(timeout.Token);
            authenticated = null;
            var finalStopped = await GetStatsAsync(http, timeout.Token);
            foreach (var delaySeconds in new[] { 2, 8, 20 })
            {
                Save();
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), timeout.Token);
                _report.FinalIdleSamples.Add(await CaptureResourcesAsync(timeout.Token));
            }
            var finalSettled = await GetStatsAsync(http, timeout.Token);
            Require(finalStopped.MediaRequests == finalSettled.MediaRequests
                && finalStopped.RangeRequests == finalSettled.RangeRequests
                && finalStopped.PlaybackInfoCount == finalSettled.PlaybackInfoCount
                && finalStopped.Events.Length == finalSettled.Events.Length, "PlaybackNetworkContinuedDuringFinalIdle");
            _report.ResourceObservation = "Measured 20 lifecycle loops and a further 30 seconds idle. Per-loop criteria: late five-loop median versus loops 5-9 must stay within 64 MiB private bytes and 32 handles; final eight loop samples must not all grow in handles or by more than 1 MiB per loop. Final idle samples diagnose delayed release and do not override per-loop failures.";
            Require(latePrivate - earlyPrivate <= 64L * 1024 * 1024
                && lateHandles - earlyHandles <= 32 && !sustainedHandles && !sustainedPrivate,
                "ResourceGrowthDetected");
            Require(_report.Diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
            _report.Status = _noInFlightGc ? "ExperimentCompletedWithinBoundedResourceLimits" : "Passed";
        }
        catch (Exception exception)
        {
            _report.Status = "Failed";
            _report.ErrorCode = ErrorCode(exception);
        }
        finally
        {
            try
            {
                if (coordinator is not null) await coordinator.DisposeAsync();
                if (authenticated is not null) await authenticated.LogoutAsync();
            }
            catch (Exception exception)
            {
                _report.Status = "Failed";
                _report.ErrorCode ??= "FinalCleanup_" + ErrorCode(exception);
            }
            _report.FinishedAt = DateTimeOffset.UtcNow;
            Save();
            Environment.ExitCode = _report.Status is "Passed" or "ExperimentCompletedWithinBoundedResourceLimits" or "InstrumentationSmokeCompleted" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private async Task RunLoopAsync(LoopResult loop, PlaybackCoordinator coordinator,
        NativePlaybackEngine engine, HttpClient http, CancellationToken ct)
    {
        var before = await GetStatsAsync(http, ct);
        var stopwatch = Stopwatch.StartNew();
        if (_noInFlightGc)
        {
            Require(GC.TryStartNoGCRegion(NoGcRegionBudget, disallowFullBlockingGC: true), "NoGcRegionUnavailable");
            _noGcRegionActive = true;
            loop.NoGcRegionStarted = true;
        }
        await coordinator.PlayAsync(new PlaybackSelection
        {
            ItemId = "1001", MediaSourceId = "synthetic-mp4", SubtitleStreamIndex = -1, StartPositionTicks = 0
        }, ct);
        loop.OpenMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var context = coordinator.ActiveContext;
        Require(context is { DeliveryMethod: PlaybackDeliveryMethod.DirectStream }, "DirectStreamRequired");
        Require(context!.TimelineOffsetTicks == 0, "UnexpectedSourceTimeline");
        loop.PlaySessionId = context.PlaySessionId;
        var player = _element!.MediaPlayer;
        Require(player is not null, "NativePlayerMissing");
        loop.Stage = "NativeClock";
        await WaitAsync(() => player!.PlaybackSession.Position.Ticks > 2_000_000
            && player.PlaybackSession.NaturalVideoWidth > 0 && player.PlaybackSession.NaturalVideoHeight > 0,
            "NativePlaybackClockDidNotAdvance", ct);
        loop.PlayingPositionTicks = player!.PlaybackSession.Position.Ticks;
        loop.VideoWidth = player.PlaybackSession.NaturalVideoWidth;
        loop.VideoHeight = player.PlaybackSession.NaturalVideoHeight;
        loop.Muted = player.IsMuted;
        Require(loop.Muted, "InitialMuteNotPreserved");
        engine.UpdateMediaControls(coordinator.ActiveContext, coordinator.Status, "Synthetic Color Study (NativeProbe)");
        var controls = player.SystemMediaTransportControls;
        using var controlsObservation = new MediaControlsRetirementObservation(engine, context.PlaybackId, controls, loop);
        await WaitAsync(() => controls.IsEnabled && controls.PlaybackStatus == Windows.Media.MediaPlaybackStatus.Playing,
            "SystemMediaControlsDidNotPlay", ct);
        loop.MediaControlsPlaying = controls.IsPauseEnabled && controls.IsStopEnabled
            && controls.DisplayUpdater.VideoProperties.Title == "Synthetic Color Study (NativeProbe)";
        Require(loop.MediaControlsPlaying, "SystemMediaControlsMetadataMissing");

        loop.Stage = "Pause";
        await coordinator.PauseAsync(ct);
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused,
            "NativePauseDidNotComplete", ct);
        await Task.Delay(150, ct);
        loop.PauseStartTicks = player.PlaybackSession.Position.Ticks;
        await Task.Delay(650, ct);
        loop.PauseEndTicks = player.PlaybackSession.Position.Ticks;
        Require(Math.Abs(loop.PauseEndTicks - loop.PauseStartTicks) <= 500_000,
            "PausedClockAdvanced");
        engine.UpdateMediaControls(coordinator.ActiveContext, PlaybackStatus.Paused, "Synthetic Color Study (NativeProbe)");
        loop.MediaControlsPaused = controls.IsEnabled && controls.IsPlayEnabled
            && controls.PlaybackStatus == Windows.Media.MediaPlaybackStatus.Paused;
        Require(loop.MediaControlsPaused, "SystemMediaControlsDidNotPause");

        loop.Stage = "Seek";
        loop.SeekTargetTicks = TimeSpan.FromSeconds(8 + loop.Number % 10).Ticks;
        await coordinator.SeekAsync(loop.SeekTargetTicks, ct);
        loop.SeekObservedTicks = player.PlaybackSession.Position.Ticks;
        Require(Math.Abs(loop.SeekObservedTicks - loop.SeekTargetTicks) <= 7_500_000,
            "NativeSeekPositionMismatch");
        Require(coordinator.ActiveContext?.TimelineOffsetTicks == 0, "SourceTimelineChanged");

        loop.Stage = "Resume";
        await coordinator.ResumeAsync(ct);
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > loop.SeekObservedTicks + 2_000_000,
            "NativeResumeClockDidNotAdvance", ct);
        loop.ResumePositionTicks = player.PlaybackSession.Position.Ticks;
        Require(player.IsMuted, "ResumeMuteNotPreserved");
        engine.UpdateMediaControls(coordinator.ActiveContext, PlaybackStatus.Playing, "Synthetic Color Study (NativeProbe)");
        await Task.Delay(300, ct);

        loop.Stage = "Stop";
        await coordinator.StopAsync(ct);
        if (_noGcRegionActive) EndNoGcRegion(loop);
        Require(loop.MediaControlsRetirementObserved && loop.MediaControlsRetired, "SystemMediaControlsDidNotRetire");
        controls = null;
        loop.PlayerDetachedAfterStop = _element.MediaPlayer is null;
        Require(loop.PlayerDetachedAfterStop && coordinator.ActiveContext is null
            && coordinator.Status == PlaybackStatus.Idle, "NativeStopDidNotDetach");
        Require(engine.Snapshot is { State: PlaybackEngineState.Stopped }, "NativeStoppedSnapshotMissing");
        player = null;
        var stopped = await GetStatsAsync(http, ct);
        await Task.Delay(1300, ct);
        var settled = await GetStatsAsync(http, ct);
        var stoppedEvents = stopped.Events.Where(item => item.PlaySessionId == loop.PlaySessionId).ToArray();
        var events = settled.Events.Where(item => item.PlaySessionId == loop.PlaySessionId).ToArray();
        loop.NoNetworkAfterStop = stopped.MediaRequests == settled.MediaRequests
            && stopped.RangeRequests == settled.RangeRequests
            && stopped.PlaybackInfoCount == settled.PlaybackInfoCount
            && stoppedEvents.Length == events.Length;
        Require(loop.NoNetworkAfterStop, "PlaybackNetworkContinuedAfterStop");
        loop.StartReports = events.Count(item => item.Kind == "Start");
        loop.ProgressReports = events.Count(item => item.Kind == "Progress");
        loop.StopReports = events.Count(item => item.Kind == "Stop");
        loop.ValidReportOrder = events.Length >= 4 && events[0].Kind == "Start"
            && events[^1].Kind == "Stop" && loop.StartReports == 1 && loop.StopReports == 1
            && events.All(item => item.Failed != true)
            && events.Any(item => item.EventName == "Pause")
            && events.Any(item => item.EventName == "Unpause");
        Require(loop.ValidReportOrder, "PlaybackReportOrderInvalid");
        loop.StopReportPositionTicks = events[^1].PositionTicks;
        Require(loop.StopReportPositionTicks >= loop.SeekTargetTicks - 7_500_000
            && loop.StopReportPositionTicks <= loop.SeekTargetTicks + 30_000_000,
            "StopReportSourcePositionMismatch");
        loop.MediaRequests = settled.MediaRequests - before.MediaRequests;
        loop.PartialResponses = settled.PartialResponses - before.PartialResponses;
        Require(loop.MediaRequests > 0 && loop.PartialResponses > 0
            && before.AuthenticationFailures == settled.AuthenticationFailures,
            "AuthenticatedMediaRangeEvidenceMissing");

        loop.Stage = "Complete";
    }

    private static async Task<ResourceSample> CaptureResourcesAsync(CancellationToken ct)
    {
        // Release delayed RCW finalizers at the same point on every loop without blocking the UI dispatcher.
        await Task.Run(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }, ct);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceSample
        {
            PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
            ManagedBytes = GC.GetTotalMemory(false), HandleCount = process.HandleCount,
            ThreadCount = process.Threads.Count
        };
    }

    private void EndNoGcRegion(LoopResult loop)
    {
        _noGcRegionActive = false;
        try
        {
            GC.EndNoGCRegion();
            loop.NoGcRegionEnded = true;
        }
        catch (InvalidOperationException) { throw new ProbeFailure("NoGcRegionBudgetExceededOrInterrupted"); }
    }

    private static async Task<FixtureStats> GetStatsAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(FixtureRoot, "/_fixture/stats"), ct);
        Require(response.IsSuccessStatusCode && response.Headers.TryGetValues("X-Synthetic-Fixture", out var values)
            && values.Contains("EmbyClient development data; not Emby Server"), "SyntheticFixtureHeaderRequired");
        var stats = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(ct),
            ProbeJsonContext.Default.FixtureStats);
        Require(stats?.Synthetic == true, "SyntheticFixtureRequired");
        return stats!;
    }

    private static async Task WaitAsync(Func<bool> condition, string failure, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new ProbeFailure(failure);
            await Task.Delay(50, ct);
        }
    }

    private void Save()
    {
        _report.UpdatedAt = DateTimeOffset.UtcNow;
        lock (_report.Diagnostics)
        {
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_report, ProbeJsonContext.Default.ProbeReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new ProbeFailure(code);
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        ProbeFailure failure => failure.Code,
        PlaybackException playback => playback.ErrorCode,
        OperationCanceledException => "CancelledOrDeadlineExpired",
        _ => exception.GetType().Name
    };

    private sealed class ProbeFailure(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }
}
