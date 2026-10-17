using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _bitmapSubtitleMediaDirectory;
    private Uri? _bitmapSubtitleFixtureRoot;

    private void ConfigureBitmapSubtitleSelectionMode(string mediaDirectory, string fixtureRoot)
    {
        if (!Path.IsPathFullyQualified(mediaDirectory))
            throw new ArgumentException("The measured embedded PGS directory must be an explicit absolute path.");
        if (!Uri.TryCreate(fixtureRoot, UriKind.Absolute, out var root) || root.Scheme != "http"
            || root.Host != "127.0.0.1" || root.Port != 19012 || root.AbsolutePath != "/emby/"
            || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("The isolated synthetic PGS fixture must be http://127.0.0.1:19012/emby/.");
        _bitmapSubtitleMediaDirectory = Path.GetFullPath(mediaDirectory);
        _bitmapSubtitleFixtureRoot = root;
    }

    private async Task RunBitmapSubtitleSelectionAsync()
    {
        var report = new BitmapSubtitleSelectionReport();
        var output = Path.GetDirectoryName(_resultPath!)!;
        var reportPath = Path.Combine(output, "bitmap-subtitle-selection-report.json");
        FileStream reportStream;
        try { reportStream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (Exception error)
        {
            Console.Error.WriteLine("Bitmap selection output reservation failed: " + error.GetType().Name);
            Environment.ExitCode = 2;
            _window!.Close();
            Exit();
            return;
        }
        using var ownedReportStream = reportStream;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var observation = new BitmapSubtitleHttpObservationHandler();
        using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
        NativePlaybackEngine? engine = null;
        PlaybackCoordinator? coordinator = null;
        EmbyApiClient? authenticated = null;
        BitmapSubtitleIdentityLease? identity = null;
        EventHandler<NativeAssSubtitleFrameChangedEventArgs>? frameObserver = null;
        EventHandler<PlaybackEngineEventArgs>? engineObserver = null;
        EventHandler<PlaybackDiagnosticEventArgs>? diagnosticObserver = null;
        var logicalId = Guid.Empty;
        string? serverSession = null;
        string? sourceId = null;
        string? mediaHash = null;
        string? probeHash = null;
        void Save()
        {
            report.UpdatedUtc = DateTimeOffset.UtcNow;
            report.ApiEvents = observation.Events;
            byte[] bytes;
            lock (report.EngineEvents)
            lock (report.Diagnostics)
                bytes = JsonSerializer.SerializeToUtf8Bytes(report, BitmapSubtitleSelectionJsonContext.Default.BitmapSubtitleSelectionReport);
            reportStream.Position = 0;
            reportStream.Write(bytes);
            reportStream.SetLength(bytes.Length);
            reportStream.Flush(flushToDisk: true);
        }
        void CleanupFailure(string operation, Exception error)
        {
            report.CleanupErrors.Add(operation + ":" + BitmapSafeCode(ErrorCode(error)));
        }
        bool SameLogicalContext() => coordinator?.ActiveContext is { } context
            && context.PlaybackId == logicalId && context.PlaySessionId == serverSession && context.Source.Id == sourceId;
        try
        {
            Save();
            Require(report.NativeAot, "NativeAotRequired");
            report.Stage = "BindMeasuredSyntheticPgs";
            var metadataPath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac.json");
            var mediaPath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac-pgs.mkv");
            var probePath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac.ffprobe.json");
            Require(new FileInfo(metadataPath).Length is > 0 and <= 64 * 1024
                && new FileInfo(probePath).Length is > 0 and <= 64 * 1024
                && new FileInfo(mediaPath).Length is > 0 and <= 64 * 1024 * 1024, "BitmapSelectionFixtureSizeInvalid");
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath, deadline.Token);
            using var metadata = JsonDocument.Parse(metadataBytes);
            var input = metadata.RootElement;
            Require(input.GetProperty("Synthetic").GetBoolean() && BitmapText(input, "Container") == "mkv"
                && BitmapText(input, "FileName") == "fixture-h264-aac-pgs.mkv"
                && input.GetProperty("FileLength").GetInt64() == new FileInfo(mediaPath).Length
                && input.GetProperty("DurationTicks").GetInt64() is >= 590_000_000 and <= 610_000_000,
                "BitmapSelectionMeasuredPgsRequired");
            await using (var file = File.OpenRead(mediaPath))
                mediaHash = BitmapHash(await SHA256.HashDataAsync(file, deadline.Token));
            probeHash = BitmapHash(SHA256.HashData(await File.ReadAllBytesAsync(probePath, deadline.Token)));
            Require(mediaHash.Equals(BitmapText(input, "FileSha256"), StringComparison.OrdinalIgnoreCase)
                && probeHash.Equals(BitmapText(input, "ProbeSha256"), StringComparison.OrdinalIgnoreCase),
                "BitmapSelectionFixtureHashMismatch");
            var track = input.GetProperty("EmbeddedSubtitle");
            Require(track.GetProperty("Index").GetInt32() == 2 && BitmapText(track, "Codec") == "pgssub"
                && BitmapText(track, "DeliveryMethod") == "Embed" && !track.GetProperty("IsExternal").GetBoolean()
                && !track.GetProperty("IsTextSubtitleStream").GetBoolean(), "BitmapSelectionFixtureTrackInvalid");
            report.MediaSha256 = mediaHash;
            report.FfprobeSha256 = probeHash;
            report.MetadataSha256 = BitmapHash(SHA256.HashData(metadataBytes));
            report.SubtitleStreamIndex = 2;
            var pristine = await ReadBitmapFixtureStatsAsync(http, observation, report, "Pristine", mediaHash, probeHash, deadline.Token);
            Require(pristine.LoginCount == 0 && pristine.PlaybackInfoCount == 0 && pristine.StartCount == 0
                && pristine.StopCount == 0 && pristine.Events.Length == 0 && pristine.ApiAttempts.Length == 0,
                "BitmapSelectionExclusiveFreshFixtureRequired");
            var api = new EmbyApiClient(http, _bitmapSubtitleFixtureRoot!, new ClientIdentity(
                "SYNTHETIC Native Bitmap Selection Probe", "Windows native probe", "synthetic-bitmap-" + report.RunId, "0.1.0"));
            var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(publicInfo.Id == "synthetic-server-0001" && publicInfo.Version == "synthetic-1.0", "BitmapSelectionSyntheticServerRequired");
            var auth = await api.AuthenticateByNameAsync("demo", "demo", deadline.Token);
            Require(auth.AccessToken is not null && auth.User?.Id == "synthetic-user-demo"
                && auth.ServerId == publicInfo.Id, "BitmapSelectionSyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(auth.AccessToken!, auth.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", deadline.Token);
            var source = item.MediaSources?.SingleOrDefault();
            Require(item.Id == "1001" && source is { Container: "mkv", SupportsDirectStream: true, SupportsTranscoding: false }
                && source.RunTimeTicks == input.GetProperty("DurationTicks").GetInt64(), "BitmapSelectionApiSourceMismatch");
            var embedded = source!.MediaStreams.SingleOrDefault(stream => stream.Index == 2 && stream.Type == "Subtitle");
            Require(embedded is { Codec: "pgssub", DeliveryMethod: "Embed", IsExternal: false, IsTextSubtitleStream: false },
                "BitmapSelectionApiPgsBindingMismatch");
            report.ActualApiBindingVerified = true;

            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
            Require(engine.NativeEmbeddedSubtitlesEligible && NativeAssInterop.IsBitmapSelectionAvailable,
                "BitmapSelectionNativeApiRequired");
            frameObserver = (_, value) =>
            {
                // The product disposes the event's independent frame after this synchronous observer returns.
                if (value.HasImage) report.ImageEvents++;
                else report.ClearEvents++;
            };
            engineObserver = (_, value) =>
            {
                lock (report.EngineEvents)
                {
                    if (report.EngineEvents.Count >= 1000) throw new PlaybackException("BitmapSelectionEventBudgetExceeded");
                    report.EngineEvents.Add(new BitmapSubtitleEngineEvent
                    {
                        Kind = value.Kind.ToString(), State = value.Snapshot.State.ToString(),
                        PlaybackIdHash = BitmapStringHash(value.Snapshot.PlaybackId.ToString("N")),
                        PositionTicks = value.Snapshot.PositionTicks, ErrorCode = value.ErrorCode is null ? null : BitmapSafeCode(value.ErrorCode)
                    });
                }
            };
            diagnosticObserver = (_, value) =>
            {
                lock (report.Diagnostics)
                {
                    if (report.Diagnostics.Count < 1000) report.Diagnostics.Add(BitmapSafeCode(value.Operation) + ":" + BitmapSafeCode(value.ErrorCode));
                    if (value.ErrorCode == "NativeReleaseFailed")
                        report.CleanupErrors.Add("NativeRelease:" + BitmapSafeCode(value.Operation));
                }
            };
            engine.SubtitleFrameChanged += frameObserver;
            engine.EventReceived += engineObserver;
            engine.Diagnostic += diagnosticObserver;
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(10), CleanupTimeout = TimeSpan.FromSeconds(10)
            });
            report.Stage = "DecodeActualPgsComposition";
            Save();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = "1001", MediaSourceId = source.Id, SubtitleStreamIndex = 2,
                NativeEmbeddedSubtitlesEligible = engine.NativeEmbeddedSubtitlesEligible
            }, deadline.Token);
            var active = coordinator.ActiveContext ?? throw new PlaybackException("BitmapSelectionContextMissing");
            logicalId = active.PlaybackId;
            serverSession = active.PlaySessionId;
            sourceId = active.Source.Id;
            report.PlaybackIdHash = BitmapStringHash(logicalId.ToString("N"));
            report.PlaySessionIdHash = BitmapStringHash(serverSession);
            report.MediaSourceIdHash = BitmapStringHash(sourceId!);
            Require(active.DeliveryMethod == PlaybackDeliveryMethod.DirectStream, "BitmapSelectionFullSourceDirectStreamRequired");
            await WaitForBitmapSampleAsync(engine, logicalId, null, value => value.NativeState == "Playing"
                && value.PositionTicks >= TimeSpan.FromSeconds(2).Ticks && BitmapHasActualPixels(value),
                "BitmapSelectionActualDecodedCueMissing", deadline.Token);
            await coordinator.PauseAsync(deadline.Token);
            await WaitForBitmapSampleAsync(engine, logicalId, null, value => value.NativeState == "Paused"
                && BitmapHasActualPixels(value), "BitmapSelectionPauseMissing", deadline.Token);
            await Task.Delay(300, deadline.Token);
            identity = await engine.CaptureBitmapIdentityForProbeAsync(logicalId);
            report.BeforeOff = await engine.ObserveBitmapSelectionForProbeAsync(logicalId, identity);
            Require(BitmapHasActualPixels(report.BeforeOff) && report.BeforeOff.NativeState == "Paused"
                && report.BeforeOff.PositionTicks is >= 20_000_000 and < 80_000_000
                && report.BeforeOff.CueSourceTimestampTicks is >= 19_000_000 and <= 21_000_000
                && BitmapAllIdentitiesMatch(report.BeforeOff), "BitmapSelectionPausedCachedCueRequired");
            report.ImageEventsBeforeOff = report.ImageEvents;
            report.ClearEventsBeforeOff = report.ClearEvents;
            Require(report.ImageEventsBeforeOff > 0, "BitmapSelectionProductImageEventMissing");
            var decoderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FFmpegInteropX.dll"));
            using (var process = Process.GetCurrentProcess())
                report.DecoderRuntimeLoadedFromProbeDirectory = process.Modules.Cast<ProcessModule>().Any(module =>
                    string.Equals(Path.GetFullPath(module.FileName), decoderPath, StringComparison.OrdinalIgnoreCase));
            Require(report.DecoderRuntimeLoadedFromProbeDirectory, "BitmapSelectionLoadedRuntimeBindingMismatch");
            await using (var runtime = File.OpenRead(decoderPath))
                report.DecoderRuntimeSha256 = BitmapHash(await SHA256.HashDataAsync(runtime, deadline.Token));
            await ReadBitmapFixtureStatsAsync(http, observation, report, "BeforeOff", mediaHash, probeHash, deadline.Token);
            report.Stage = "DisableCachedPgsInPlace";
            Save();
            await coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 }, deadline.Token);
            report.Off = await WaitForBitmapSampleAsync(engine, logicalId, identity,
                value => value.RequestedSubtitleStreamIndex == -1 && !value.HasImage && value.NativeState == "Paused",
                "BitmapSelectionOffDidNotClear", deadline.Token);
            Require(SameLogicalContext() && BitmapAllIdentitiesMatch(report.Off), "BitmapSelectionOffReplacedNativeObject");
            report.OffClearedNativeImage = !report.Off.SubtitlePresentationActive && !report.Off.HasPresentationTimer
                && !report.Off.HasPresentationTrack && !report.Off.IndependentlyAcquiredBitmap;
            report.ImageEventsAfterOff = report.ImageEvents;
            report.ClearEventsAfterOff = report.ClearEvents;
            Require(report.OffClearedNativeImage && report.ClearEventsAfterOff > report.ClearEventsBeforeOff,
                "BitmapSelectionOffPresentationNotCleared");
            await ReadBitmapFixtureStatsAsync(http, observation, report, "Off", mediaHash, probeHash, deadline.Token);
            report.Stage = "ReselectSameCachedPgsInPlace";
            Save();
            await coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = 2 }, deadline.Token);
            report.Reselected = await WaitForBitmapSampleAsync(engine, logicalId, identity,
                value => value.RequestedSubtitleStreamIndex == 2 && value.NativeState == "Paused" && BitmapHasActualPixels(value),
                "BitmapSelectionCachedCueRestoreMissing", deadline.Token);
            await Task.Delay(300, deadline.Token);
            report.ReselectedStable = await engine.ObserveBitmapSelectionForProbeAsync(logicalId, identity);
            report.ImageEventsAfterReselect = report.ImageEvents;
            report.ClearEventsAfterReselect = report.ClearEvents;
            report.ProductPresentationEventsRestored = report.ImageEventsAfterReselect > report.ImageEventsAfterOff;
            report.LogicalPlaybackAndServerSessionPreserved = SameLogicalContext();
            report.AllNativeIdentitiesPreserved = new[] { report.BeforeOff, report.Off, report.Reselected, report.ReselectedStable }
                .All(BitmapAllIdentitiesMatch);
            report.PausedPositionPreserved = new[] { report.Off, report.Reselected, report.ReselectedStable }
                .All(value => value.NativeState == "Paused"
                    && Math.Abs(value.PositionTicks - report.BeforeOff.PositionTicks) <= report.MaximumAllowedPauseDriftTicks);
            report.CachedCompositionPixelsRestored = BitmapSameComposition(report.BeforeOff, report.Reselected)
                && BitmapSameComposition(report.BeforeOff, report.ReselectedStable);
            report.CachedCueSourceTimestampRestored = report.Reselected.CueSourceTimestampTicks == report.BeforeOff.CueSourceTimestampTicks
                && report.ReselectedStable.CueSourceTimestampTicks == report.BeforeOff.CueSourceTimestampTicks;
            await ReadBitmapFixtureStatsAsync(http, observation, report, "Reselected", mediaHash, probeHash, deadline.Token);
            report.NoNegotiationStartOrStopDuringSelection = report.FixtureObservations.Where(value => value.Phase != "Pristine")
                .All(value => BitmapSelectionApiUnchanged(value, report.PlaySessionIdHash!));
            Require(report.LogicalPlaybackAndServerSessionPreserved && report.AllNativeIdentitiesPreserved
                && report.PausedPositionPreserved && report.CachedCompositionPixelsRestored
                && report.CachedCueSourceTimestampRestored && report.NoNegotiationStartOrStopDuringSelection
                && report.ProductPresentationEventsRestored,
                "BitmapSelectionInPlaceRestorationNotEstablished");
            report.Status = "ObservationsPassed";
            report.Stage = "ReleaseObserversBeforeProductStop";
            Save();
        }
        catch (Exception error)
        {
            report.Status = "Failed";
            report.ErrorCode = BitmapSafeCode(ErrorCode(error));
            report.ErrorType = error.GetType().Name;
            report.ErrorHResult = error.HResult;
        }
        finally
        {
            if (engine is not null)
            {
                if (frameObserver is not null) engine.SubtitleFrameChanged -= frameObserver;
                if (engineObserver is not null) engine.EventReceived -= engineObserver;
                frameObserver = null; engineObserver = null;
                try
                {
                    report.ObserverReferencesReleasedBeforeStop = identity is null || await engine.ReleaseBitmapIdentityForProbeAsync(identity);
                    report.ObserverNativeReferencesAtStop = identity?.HeldNativeReferences ?? 0;
                    report.ObserverProjectionReferencesAtStop = identity?.HeldProjectionReferences ?? 0;
                    report.ObserverBitmapLeasesAtStop = engine.BitmapProbeOutstandingBitmapLeases;
                    identity = null;
                }
                catch (Exception error) { CleanupFailure("ReleaseNativeIdentityObservers", error); }
            }
            if (coordinator is not null)
            {
                try
                {
                    await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(35));
                    report.StopCompleted = true;
                    report.NativeSessionDrainedAfterStop = engine is not null && await engine.BitmapProbeSessionDrainedAsync();
                }
                catch (Exception error) { CleanupFailure("Stop", error); }
                try
                {
                    await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(35));
                    report.CoordinatorDisposed = true;
                }
                catch (Exception error) { CleanupFailure("CoordinatorDispose", error); }
            }
            if (engine is not null)
            {
                try
                {
                    await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    report.NativeDisposed = true;
                    report.NativeOwnerCleared = await engine.BitmapProbeOwnerClearedAsync();
                }
                catch (Exception error) { CleanupFailure("NativeDispose", error); }
                finally
                {
                    // This scalar-only observer retains no native object and must see swallowed Release failures.
                    if (diagnosticObserver is not null) engine.Diagnostic -= diagnosticObserver;
                    diagnosticObserver = null;
                }
            }
            if (mediaHash is not null && probeHash is not null && logicalId != Guid.Empty)
            {
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var stopped = await ReadBitmapFixtureStatsAsync(http, observation, report, "Stopped", mediaHash, probeHash, cleanupDeadline.Token);
                    await Task.Delay(300, cleanupDeadline.Token);
                    var settled = await ReadBitmapFixtureStatsAsync(http, observation, report, "StoppedSettled", mediaHash, probeHash, cleanupDeadline.Token);
                    report.ExactFinalApiCleanup = settled.PlaybackInfoCount == 1 && settled.StartCount == 1 && settled.StopCount == 1
                        && settled.EncodingCleanups == 0 && settled.AuthenticationFailures == 0
                        && settled.Events.All(value => value.SessionHash == report.PlaySessionIdHash && value.ItemId == "1001")
                        && settled.Events.FirstOrDefault()?.Kind == "Start" && settled.Events.LastOrDefault()?.Kind == "Stop"
                        && settled.Events.LastOrDefault()?.Failed == false
                        && BitmapApiAttemptsMatch(settled.ApiAttempts, report.PlaySessionIdHash!, expectedStops: 1);
                    report.NoMediaOrProgressAfterStop = settled.MediaRequests == stopped.MediaRequests
                        && settled.ProgressCount == stopped.ProgressCount && settled.StopCount == stopped.StopCount;
                }
                catch (Exception error) { CleanupFailure("FinalRawHttpFixtureObservation", error); }
            }
            if (authenticated is not null)
            {
                try
                {
                    using var logoutDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await authenticated.LogoutAsync(logoutDeadline.Token);
                    report.LogoutCompleted = true;
                }
                catch (Exception error) { CleanupFailure("Logout", error); }
            }
            if (report.Status == "ObservationsPassed")
            {
                if (report.ObserverReferencesReleasedBeforeStop && report.ObserverNativeReferencesAtStop == 0
                    && report.ObserverProjectionReferencesAtStop == 0 && report.ObserverBitmapLeasesAtStop == 0
                    && report.StopCompleted && report.NativeSessionDrainedAfterStop && report.CoordinatorDisposed
                    && report.NativeDisposed && report.NativeOwnerCleared && report.LogoutCompleted
                    && report.ExactFinalApiCleanup && report.NoMediaOrProgressAfterStop && report.CleanupErrors.Count == 0)
                {
                    report.Status = "Passed";
                    report.Stage = "Complete";
                }
                else
                {
                    report.Status = "Failed";
                    report.ErrorCode ??= "BitmapSelectionCleanupNotEstablished";
                }
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            Save();
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private async Task<BitmapFixtureObservation> ReadBitmapFixtureStatsAsync(HttpClient http, BitmapSubtitleHttpObservationHandler observation,
        BitmapSubtitleSelectionReport report, string phase, string mediaHash, string probeHash, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(_bitmapSubtitleFixtureRoot!, "/_fixture/stats"), ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Require(bytes.Length is > 0 and <= 128 * 1024, "BitmapSelectionRawStatsSizeInvalid");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var bound = root.GetProperty("Synthetic").GetBoolean() && BitmapText(root, "ServerId") == "synthetic-server-0001"
            && root.GetProperty("EmbeddedPgsConfigured").GetBoolean() && BitmapText(root, "MediaContainer") == "mkv"
            && root.GetProperty("EmbeddedPgsNativeSubtitleIndex").GetInt32() == 2
            && mediaHash.Equals(BitmapText(root, "EmbeddedPgsMediaSha256"), StringComparison.OrdinalIgnoreCase)
            && probeHash.Equals(BitmapText(root, "EmbeddedPgsProbeSha256"), StringComparison.OrdinalIgnoreCase)
            && !root.GetProperty("LumenCatalog").GetBoolean() && !root.GetProperty("LumenDesignCatalog").GetBoolean();
        Require(bound, "BitmapSelectionRawStatsFixtureBindingMismatch");
        var result = new BitmapFixtureObservation
        {
            Phase = phase, RawHttpResponseSha256 = BitmapHash(SHA256.HashData(bytes)), HashBoundSyntheticPgsFixture = bound,
            LoginCount = root.GetProperty("LoginCount").GetInt32(), AuthenticationFailures = root.GetProperty("AuthenticationFailures").GetInt32(),
            PlaybackInfoCount = root.GetProperty("PlaybackInfoCount").GetInt32(), StartCount = root.GetProperty("StartCount").GetInt32(),
            ProgressCount = root.GetProperty("ProgressCount").GetInt32(), StopCount = root.GetProperty("StopCount").GetInt32(),
            EncodingCleanups = root.GetProperty("EncodingCleanups").GetInt32(), MediaRequests = root.GetProperty("MediaRequests").GetInt32(),
            ApiAttempts = observation.Events,
            Events = root.GetProperty("Events").EnumerateArray().Select(value => new BitmapFixtureEvent
            {
                Kind = BitmapSafeCode(BitmapText(value, "Kind")!), SessionHash = BitmapStringHash(BitmapText(value, "PlaySessionId")!),
                ItemId = BitmapSafeCode(BitmapText(value, "ItemId")!), PositionTicks = value.GetProperty("PositionTicks").GetInt64(),
                EventName = BitmapText(value, "EventName") is { } name ? BitmapSafeCode(name) : null,
                Failed = value.TryGetProperty("Failed", out var failed) && failed.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? failed.GetBoolean() : null,
                Timestamp = value.GetProperty("Timestamp").GetDateTimeOffset()
            }).ToArray()
        };
        report.FixtureObservations.Add(result);
        return result;
    }

    private static async Task<BitmapSubtitleNativeSample> WaitForBitmapSampleAsync(NativePlaybackEngine engine, Guid playbackId,
        BitmapSubtitleIdentityLease? identity, Func<BitmapSubtitleNativeSample, bool> predicate, string errorCode, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            var sample = await engine.ObserveBitmapSelectionForProbeAsync(playbackId, identity);
            if (predicate(sample)) return sample;
            if (engine.Snapshot?.State == PlaybackEngineState.Failed) throw new PlaybackException("BitmapSelectionNativePlaybackFailed");
            await Task.Delay(50, ct);
        }
        throw new PlaybackException(errorCode);
    }

    private static bool BitmapHasActualPixels(BitmapSubtitleNativeSample value) => value.ActivePlaybackMatches
        && value.ActualNativeDecoderConfigured && value.DefaultPlayerOwnerPresent && value.FullSourceDirectStream
        && value.SubtitlePresentationActive && value.HasPresentationTimer && value.HasPresentationTrack
        && !value.HasExternalSubtitle && value.RetainedNativeSubtitleStreamIndex == 2 && value.RetainedNativeSubtitleCodec == "pgssub"
        && value.HasImage && value.PresentationMode == 2 && value.IndependentlyAcquiredBitmap
        && value.NativeNonzeroAlphaPixels > 0 && value.NativeNonzeroAlphaPixels == value.CountedNonzeroAlphaPixels
        && value.BgraPixelSha256 is { Length: 64 };

    private static bool BitmapAllIdentitiesMatch(BitmapSubtitleNativeSample value) => value.ProductSessionMatchesBaseline
        && value.NativeIdentities.Count == 5 && value.NativeIdentities.All(identity => identity.IInspectableAndIUnknownQueriesSucceeded
            && identity.CanonicalIdentityMatchesBaseline && identity.ProjectionMatchesBaseline
            && identity.BaselineOrdinal == identity.ObservedOrdinal);

    private static bool BitmapSameComposition(BitmapSubtitleNativeSample before, BitmapSubtitleNativeSample after) => BitmapHasActualPixels(after)
        && before.BgraPixelSha256 == after.BgraPixelSha256 && before.TrackId == after.TrackId
        && before.BitmapWidth == after.BitmapWidth && before.BitmapHeight == after.BitmapHeight
        && before.VideoWidth == after.VideoWidth && before.VideoHeight == after.VideoHeight
        && before.PixelAspectRatio == after.PixelAspectRatio && before.X == after.X && before.Y == after.Y
        && before.Width == after.Width && before.Height == after.Height
        && before.NativeNonzeroAlphaPixels == after.NativeNonzeroAlphaPixels;

    private static bool BitmapSelectionApiUnchanged(BitmapFixtureObservation value, string sessionHash) => value.PlaybackInfoCount == 1
        && value.StartCount == 1 && value.StopCount == 0 && value.EncodingCleanups == 0 && value.AuthenticationFailures == 0
        && value.Events.Count(item => item.Kind == "Start") == 1 && value.Events.All(item => item.Kind != "Stop"
            && item.SessionHash == sessionHash && item.ItemId == "1001")
        && BitmapApiAttemptsMatch(value.ApiAttempts, sessionHash, expectedStops: 0);

    private static bool BitmapApiAttemptsMatch(HlsApiEvent[] events, string sessionHash, int expectedStops) =>
        events.Count(value => value.Operation == "PlaybackInfo") == 1
        && events.Count(value => value.Operation == "Start") == 1
        && events.Count(value => value.Operation == "Stop") == expectedStops
        && events.All(value => value.Operation == "PlaybackInfo" ? value.StatusCode == 200
            : value.Operation is "Start" or "Progress" or "Stop" && value.StatusCode == 204 && value.SessionHash == sessionHash);

    private static string? BitmapText(JsonElement value, string property) => value.TryGetProperty(property, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static string BitmapHash(byte[] hash) => Convert.ToHexStringLower(hash);
    private static string BitmapStringHash(string value) => BitmapHash(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string BitmapSafeCode(string value) => value is { Length: > 0 and <= 160 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '.' or ':') ? value : "Redacted";

    /// <summary>Records request attempts before awaiting HTTP, including rejected or failed playback controls.</summary>
    private sealed partial class BitmapSubtitleHttpObservationHandler : DelegatingHandler
    {
        private readonly object _gate = new();
        private readonly List<HlsApiEvent> _events = [];

        internal BitmapSubtitleHttpObservationHandler() : base(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        }) { }

        internal HlsApiEvent[] Events { get { lock (_gate) return [.. _events]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var operation = path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) ? "PlaybackInfo"
                : path.EndsWith("/Sessions/Playing/Stopped", StringComparison.OrdinalIgnoreCase) ? "Stop"
                : path.EndsWith("/Sessions/Playing/Progress", StringComparison.OrdinalIgnoreCase) ? "Progress"
                : path.EndsWith("/Sessions/Playing", StringComparison.OrdinalIgnoreCase) ? "Start"
                : path.EndsWith("/Videos/ActiveEncodings", StringComparison.OrdinalIgnoreCase) ? "StopEncoding" : null;
            HlsApiEvent? observed = null;
            var index = -1;
            if (operation is not null)
            {
                observed = new HlsApiEvent { Operation = operation };
                lock (_gate)
                {
                    if (_events.Count >= 1000) throw new PlaybackException("BitmapSelectionApiAttemptBudgetExceeded");
                    index = _events.Count;
                    _events.Add(observed);
                }
                if (operation is "Start" or "Progress" or "Stop")
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
                    var json = body.RootElement;
                    observed = new HlsApiEvent
                    {
                        Operation = operation,
                        SessionHash = BitmapText(json, "PlaySessionId") is { } session ? BitmapStringHash(session) : null,
                        PositionTicks = json.TryGetProperty("PositionTicks", out var position) && position.TryGetInt64(out var ticks) ? ticks : null,
                        EventName = BitmapText(json, "EventName") is { } name ? BitmapSafeCode(name) : null
                    };
                    lock (_gate) _events[index] = observed;
                }
            }
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (observed is not null) lock (_gate) observed.StatusCode = (int)response.StatusCode;
            return response;
        }
    }
}
