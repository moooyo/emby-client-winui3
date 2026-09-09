using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace EmbyClient.NativeProbe
{
    /// <summary>Runs one bounded external-caption observation without altering the product engine.</summary>
    internal static class ExternalSubtitleProbe
    {
        internal static async Task<ExternalSubtitleReport> RunAsync(DispatcherQueue dispatcher,
            MediaPlayerElement element, string credentialsPath, string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(element);
            var output = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(output);
            var reportPath = Path.Combine(output, "external-subtitle-report.json");
            var readyPath = Path.Combine(output, "external-subtitle-ready.json");
            var finishPath = Path.Combine(output, "external-subtitle-finish.json");
            Require(!File.Exists(reportPath) && !File.Exists(readyPath) && !File.Exists(finishPath), "FreshOutputDirectoryRequired");
            var report = new ExternalSubtitleReport();
            var diagnostics = new List<string>();
            var cleanupErrors = new List<string>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(7));
            using var observation = new HlsApiObservationHandler();
            using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
            EmbyApiClient? authenticated = null;
            PlaybackCoordinator? coordinator = null;
            NativePlaybackEngine? engine = null;
            var finishAccepted = false;

            void Save()
            {
                lock (diagnostics) report.Diagnostics = [.. diagnostics];
                report.ApiEvents = observation.Events.Select(value => new ExternalSubtitleApiEvent
                {
                    Operation = value.Operation, StatusCode = value.StatusCode, PositionTicks = value.PositionTicks
                }).ToArray();
                report.UpdatedUtc = DateTimeOffset.UtcNow;
                WriteJson(reportPath, JsonSerializer.Serialize(report, ExternalSubtitleJsonContext.Default.ExternalSubtitleReport));
            }

            try
            {
                Save();
                Require(report.NativeAot, "NativeAotRequired");
                Require(new FileInfo(credentialsPath).Length is > 0 and <= 16 * 1024, "InvalidCredentialsFileSize");
                var credentialBytes = await File.ReadAllBytesAsync(credentialsPath, deadline.Token);
                var credentials = JsonSerializer.Deserialize(credentialBytes, ExternalSubtitleJsonContext.Default.ExternalSubtitleCredentials);
                Require(credentials is not null && !string.IsNullOrEmpty(credentials.Username)
                    && !string.IsNullOrEmpty(credentials.Password), "CredentialsMissing");
                Require(Uri.TryCreate(credentials!.ServerUrl, UriKind.Absolute, out var server)
                    && server.Scheme == "http" && server.Host == "127.0.0.1" && server.Port == 19096
                    && server.UserInfo.Length == 0 && server.Query.Length == 0 && server.Fragment.Length == 0
                    && server.AbsolutePath.TrimEnd('/') is "" or "/emby", "OwnedOfficialLoopbackServerRequired");
                var device = "native-external-subtitle-probe-" + Guid.NewGuid().ToString("N");
                report.DeviceIdHash = Hash(device);
                var api = new EmbyApiClient(http, new Uri("http://127.0.0.1:19096"), new ClientIdentity(
                    "Emby Native External Subtitle Probe", "Windows external subtitle probe", device, "0.1.0"));
                var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
                Require(publicInfo.Version == "4.9.5.0" && publicInfo.Id == "cf4feb10df224135877fc61204a28212",
                    "OfficialServerIdentityMismatch");
                report.ServerVersion = publicInfo.Version!;
                report.ServerIdHash = Hash(publicInfo.Id!);
                var authentication = await api.AuthenticateByNameAsync(credentials.Username!, credentials.Password!, deadline.Token);
                credentials.Password = null;
                Array.Clear(credentialBytes);
                Require(authentication.AccessToken is not null && authentication.User?.Id is not null
                    && authentication.ServerId == publicInfo.Id, "AuthenticationFailed");
                authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
                var item = await authenticated.GetItemAsync("5", deadline.Token);
                Require(item.Name == "Fixture" && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000, "ExpectedSyntheticFixtureMissing");
                var source = (item.MediaSources ?? []).SingleOrDefault(value => value.Id == "mediasource_5");
                var subtitle = (source?.MediaStreams ?? []).SingleOrDefault(value => value.Index == 2
                    && string.Equals(value.Type, "Subtitle", StringComparison.OrdinalIgnoreCase));
                Require(source is not null && subtitle is not null && subtitle.IsExternal == true
                    && subtitle.IsTextSubtitleStream == true, "ExpectedExternalSrtTrackMissing");

                engine = new NativePlaybackEngine(dispatcher, element, initialMuted: true);
                engine.Diagnostic += (_, value) =>
                {
                    lock (diagnostics) diagnostics.Add("Native:" + SafeCode(value.Operation) + ":" + SafeCode(value.ErrorCode));
                };
                coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
                {
                    EnableExternalWebVtt = true,
                    ProgressInterval = TimeSpan.FromSeconds(10),
                    ReportTimeout = TimeSpan.FromSeconds(10),
                    CleanupTimeout = TimeSpan.FromSeconds(10)
                });
                coordinator.Diagnostic += (_, value) =>
                {
                    lock (diagnostics) diagnostics.Add(SafeCode(value.Operation) + ":" + SafeCode(value.ErrorCode));
                };
                report.Status = "OpeningExternalSubtitle";
                Save();
                await coordinator.PlayAsync(new PlaybackSelection
                {
                    ItemId = "5", MediaSourceId = "mediasource_5", StartPositionTicks = 0,
                    SubtitleStreamIndex = 2, ForceTranscoding = false
                }, deadline.Token);
                var context = coordinator.ActiveContext ?? throw new ExternalSubtitleFailure("PlaybackContextMissing");
                report.DeliveryMethod = context.DeliveryMethod.ToString();
                report.SubtitleDeliveryMethod = context.Source.MediaStreams?.FirstOrDefault(value => value.Index == 2)?.DeliveryMethod;
                Require(context.DeliveryMethod == PlaybackDeliveryMethod.DirectStream
                    && report.SubtitleDeliveryMethod?.Equals("External", StringComparison.OrdinalIgnoreCase) == true,
                    "ExternalDirectStreamRequiredNoBurnInFallback");
                report.Status = "PlayingTowardCue";
                Save();
                var pauseDeadline = DateTimeOffset.UtcNow.AddSeconds(75);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Require(coordinator.ActiveContext?.PlaybackId == context.PlaybackId
                        && coordinator.Status is not (PlaybackStatus.Failed or PlaybackStatus.Idle), "PlaybackChangedBeforeCue");
                    var position = await OnDispatcherAsync(dispatcher, () => element.MediaPlayer?.PlaybackSession.Position.Ticks ?? -1);
                    if (position >= report.PauseTargetTicks) break;
                    Require(DateTimeOffset.UtcNow < pauseDeadline, "CuePositionNotReached");
                    await Task.Delay(40, deadline.Token);
                }
                await coordinator.PauseAsync(deadline.Token);
                report.NativeObservation = await engine.ObserveExternalSubtitleForProbeAsync(context.PlaybackId, report.ExpectedCue, deadline.Token);
                var native = report.NativeObservation ?? throw new ExternalSubtitleFailure("NativeObservationMissing");
                report.NativePausedPositionTicks = native.NativePositionTicks;
                Require(native.ActivePlaybackMatches && native.DeliveryMethod == nameof(PlaybackDeliveryMethod.DirectStream)
                    && native.HasDefaultProductPlayerOwner && native.HasIndependentSubtitleTransport
                    && native.HasSeparateSubtitleUri && native.SubtitleOriginIsOwnedLoopback
                    && native.HasScopedSubtitleAuthentication && native.DownloadedSubtitleBytes > 0
                    && native.WebVttHeaderObserved && native.TimedTextSourceCreated
                    && native.AttachedTimedTextSourceCount > 0 && native.TimedTextResolvedSuccessfully
                    && native.ResolvedExternalTrackCount > 0 && native.MatchingMetadataTrackCount > 0
                    && native.PlatformPresentedExternalTrackCount > 0 && native.ExpectedCueFoundInDownloadedText,
                    "NativeExternalSubtitlePipelineNotConfirmed");
                Require(native.NativeState == nameof(MediaPlaybackState.Paused)
                    && Math.Abs(native.NativePositionTicks - report.PauseTargetTicks) <= TimeSpan.FromSeconds(0.75).Ticks
                    && native.ExpectedCueStartTicks is long cueStart && native.ExpectedCueEndTicks is long cueEnd
                    && cueStart <= native.NativePositionTicks && native.NativePositionTicks < cueEnd
                    && native.NativeVideoWidth > 0 && native.NativeVideoHeight > 0, "ExpectedCueWindowNotPaused");
                lock (diagnostics) Require(diagnostics.Count == 0, "UnexpectedPlaybackDiagnostic");
                report.AutomatedChecksPassed = true;
                report.Status = "AwaitingVisualInspection";
                Save();
                var visualDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
                var ready = new ExternalSubtitleReady
                {
                    RunId = report.RunId, ProcessId = report.ProcessId, ExpectedCue = report.ExpectedCue,
                    PausedPositionTicks = native.NativePositionTicks, DeadlineUtc = visualDeadline
                };
                WriteJson(readyPath, JsonSerializer.Serialize(ready, ExternalSubtitleJsonContext.Default.ExternalSubtitleReady));
                while (DateTimeOffset.UtcNow < visualDeadline)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Require(coordinator.ActiveContext?.PlaybackId == context.PlaybackId
                        && engine.Snapshot?.State == PlaybackEngineState.Paused, "VisualInspectionPlaybackChanged");
                    var finish = await ReadFinishAsync(finishPath, deadline.Token);
                    if (finish is null) { await Task.Delay(200, deadline.Token); continue; }
                    Require(finish.RunId == report.RunId && finish.ConfirmedBy == "RootAgentScreenshotInspection", "VisualConfirmationIdentityMismatch");
                    Require(finish.PresentedCueConfirmed && finish.ObservedCue == report.ExpectedCue, "PresentedCueNotConfirmed");
                    var filename = finish.ScreenshotFileName;
                    Require(!string.IsNullOrWhiteSpace(filename) && filename == Path.GetFileName(filename)
                        && filename.Length <= 120 && filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase), "ScreenshotFileNameInvalid");
                    var screenshot = Path.Combine(output, filename!);
                    Require(new FileInfo(screenshot).Length is >= 8 and <= 100 * 1024 * 1024, "ScreenshotEvidenceMissing");
                    await using var stream = File.OpenRead(screenshot);
                    var signature = new byte[8];
                    await stream.ReadExactlyAsync(signature, deadline.Token);
                    Require(signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "ScreenshotMustBePng");
                    stream.Position = 0;
                    report.ScreenshotSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, deadline.Token));
                    report.ScreenshotFileName = filename;
                    report.PresentedCueConfirmed = true;
                    report.ConfirmedBy = "RootAgentScreenshotInspection";
                    finishAccepted = true;
                    report.Status = "VisualInspectionConfirmed";
                    Save();
                    break;
                }
                Require(finishAccepted, "VisualInspectionTimedOut");
            }
            catch (Exception error)
            {
                report.Status = "Failed";
                report.ErrorCode = ErrorCode(error);
            }
            finally
            {
                if (coordinator is not null)
                {
                    try { await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(35)); report.StopCompleted = true; }
                    catch (Exception error) { cleanupErrors.Add("Stop:" + ErrorCode(error)); }
                    try { await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(35)); report.CoordinatorDisposed = true; }
                    catch (Exception error) { cleanupErrors.Add("CoordinatorDispose:" + ErrorCode(error)); }
                }
                if (engine is not null)
                {
                    try
                    {
                        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                        report.NativeDisposed = true;
                        report.NativeOwnerCleared = await engine.ExternalSubtitleOwnerClearedForProbeAsync();
                        if (!report.NativeOwnerCleared) cleanupErrors.Add("NativeOwnerNotCleared");
                    }
                    catch (Exception error) { cleanupErrors.Add("NativeDispose:" + ErrorCode(error)); }
                }
                if (authenticated is not null)
                {
                    try
                    {
                        using var logout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await authenticated.LogoutAsync(logout.Token);
                        report.LogoutCompleted = true;
                    }
                    catch (Exception error) { cleanupErrors.Add("Logout:" + ErrorCode(error)); }
                }
                report.CleanupErrors = [.. cleanupErrors];
                report.FinishedUtc = DateTimeOffset.UtcNow;
                Save();
                var starts = report.ApiEvents.Count(value => value.Operation == "Start" && value.StatusCode is >= 200 and < 300);
                var stops = report.ApiEvents.Count(value => value.Operation == "Stop" && value.StatusCode is >= 200 and < 300);
                if (report.ErrorCode is null && finishAccepted && report.AutomatedChecksPassed && report.StopCompleted && report.CoordinatorDisposed
                    && report.NativeDisposed && report.NativeOwnerCleared && report.LogoutCompleted
                    && cleanupErrors.Count == 0 && report.Diagnostics.Length == 0 && starts == 1 && stops == 1)
                    report.Status = "Passed";
                else if (report.ErrorCode is null)
                {
                    report.Status = "Failed";
                    report.ErrorCode = "ExternalSubtitleFinalValidationFailed";
                }
                Save();
            }
            return report;
        }

        private static async Task<ExternalSubtitleFinish?> ReadFinishAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) return null;
            Require(new FileInfo(path).Length <= 4096, "VisualConfirmationTooLarge");
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                return JsonSerializer.Deserialize(text, ExternalSubtitleJsonContext.Default.ExternalSubtitleFinish);
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        private static Task<T> OnDispatcherAsync<T>(DispatcherQueue dispatcher, Func<T> action)
        {
            if (dispatcher.HasThreadAccess) return Task.FromResult(action());
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new ExternalSubtitleFailure("DispatcherUnavailable"));
            return completion.Task;
        }

        private static void WriteJson(string path, string text)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }

        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        private static string SafeCode(string value) => value.Length is > 0 and <= 80
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? value : "Other";
        private static string ErrorCode(Exception error) => error switch
        {
            ExternalSubtitleFailure failure => failure.Code,
            PlaybackException playback => SafeCode(playback.ErrorCode),
            EmbyApiException api => "ApiHttp" + ((int)api.StatusCode).ToString(CultureInfo.InvariantCulture),
            OperationCanceledException => "Cancelled",
            TimeoutException => "Timeout",
            _ => error.GetType().Name
        };
        private static void Require(bool condition, string code)
        {
            if (!condition) throw new ExternalSubtitleFailure(code);
        }
        private sealed class ExternalSubtitleFailure(string code) : Exception(code)
        {
            internal string Code { get; } = code;
        }
    }
}

namespace EmbyClient.App.Playback
{
    public sealed partial class NativePlaybackEngine
    {
        /// <summary>Reads the real product session; it does not create tracks, download captions, or alter modes.</summary>
        internal Task<EmbyClient.NativeProbe.ExternalSubtitleNativeObservation> ObserveExternalSubtitleForProbeAsync(
            Guid playbackId, string expectedCue, CancellationToken cancellationToken) => OnDispatcherAsync(async () =>
        {
            var observed = new EmbyClient.NativeProbe.ExternalSubtitleNativeObservation();
            var session = _current;
            if (session is null || session.Request.PlaybackId != playbackId || session.Retired) return observed;
            observed.ActivePlaybackMatches = true;
            observed.DeliveryMethod = session.Request.DeliveryMethod.ToString();
            observed.HasDefaultProductPlayerOwner = _playerOwner is not null && ReferenceEquals(session.Player, _playerOwner)
                && !ReuseNativeHttpControlPlayer && NativeHttpControlObservation is null;
            observed.HasIndependentSubtitleTransport = session.SubtitleTransport is not null;
            var subtitleUri = session.Request.ExternalSubtitleUri;
            observed.HasSeparateSubtitleUri = subtitleUri is not null && subtitleUri != session.Request.MediaUri;
            observed.SubtitleOriginIsOwnedLoopback = subtitleUri is { Scheme: "http", Host: "127.0.0.1", Port: 19096 };
            observed.HasScopedSubtitleAuthentication = session.Request.ExternalSubtitleHeaders.Keys.Any(name =>
                name.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase));
            observed.TimedTextSourceCreated = session.TimedText is not null;
            observed.AttachedTimedTextSourceCount = session.MediaSource?.ExternalTimedTextSources.Count ?? 0;
            observed.ResolvedExternalTrackCount = session.ExternalTracks.Length;
            // With an external URI, the product initializes this flag false and sets it true
            // only after a successful TimedTextSource.Resolved callback with actual tracks.
            observed.TimedTextResolvedSuccessfully = subtitleUri is not null && session.ExternalTextReady && session.ExternalTracks.Length > 0;
            if (session.Item is { } item)
            {
                var tracks = item.TimedMetadataTracks;
                for (uint index = 0; index < tracks.Count; index++)
                {
                    if (!session.ExternalTracks.Any(track => track == tracks[(int)index])) continue;
                    observed.MatchingMetadataTrackCount++;
                    if (tracks.GetPresentationMode(index) == TimedMetadataTrackPresentationMode.PlatformPresented)
                        observed.PlatformPresentedExternalTrackCount++;
                }
            }
            observed.NativePositionTicks = session.NativeSession?.Position.Ticks ?? -1;
            observed.NativeState = session.NativeSession?.PlaybackState.ToString() ?? "Missing";
            observed.NativeVideoWidth = session.NativeSession?.NaturalVideoWidth ?? 0;
            observed.NativeVideoHeight = session.NativeSession?.NaturalVideoHeight ?? 0;
            if (session.SubtitleStream is { Size: > 0 and <= 4 * 1024 * 1024 } stream)
            {
                observed.DownloadedSubtitleBytes = stream.Size;
                using var clone = stream.CloneStream();
                clone.Seek(0);
                using var reader = new DataReader(clone);
                try
                {
                    var loaded = await reader.LoadAsync((uint)stream.Size).AsTask(cancellationToken);
                    var bytes = new byte[checked((int)loaded)];
                    reader.ReadBytes(bytes);
                    var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                    observed.WebVttHeaderObserved = text.StartsWith("WEBVTT", StringComparison.Ordinal);
                    observed.DownloadedSubtitleSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    for (var line = 1; line < lines.Length; line++)
                    {
                        if (lines[line] != expectedCue) continue;
                        var timing = lines[line - 1].Split("-->", StringSplitOptions.TrimEntries);
                        if (timing.Length != 2) continue;
                        var formats = new[] { @"mm\:ss\.fff", @"hh\:mm\:ss\.fff" };
                        if (TimeSpan.TryParseExact(timing[0], formats, CultureInfo.InvariantCulture, out var start)
                            && TimeSpan.TryParseExact(timing[1].Split(' ', 2)[0], formats, CultureInfo.InvariantCulture, out var end))
                        {
                            observed.ExpectedCueFoundInDownloadedText = true;
                            observed.ExpectedCueStartTicks = start.Ticks;
                            observed.ExpectedCueEndTicks = end.Ticks;
                        }
                    }
                }
                finally { reader.DetachStream(); }
            }
            return observed;
        }).Unwrap();

        internal Task<bool> ExternalSubtitleOwnerClearedForProbeAsync() => OnDispatcherAsync(() =>
            _current is null && _playerOwner is null && _disposalTask?.IsCompleted == true);
    }
}
