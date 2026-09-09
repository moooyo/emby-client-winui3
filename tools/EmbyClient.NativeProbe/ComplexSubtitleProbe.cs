using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.NativeProbe;

/// <summary>Observes one complex subtitle's default server burn-in and subsequent disabling.</summary>
internal static partial class ComplexSubtitleProbe
{
    internal static async Task<ComplexSubtitleReport> RunAsync(DispatcherQueue dispatcher, MediaPlayerElement element,
        string credentialsPath, string manifestPath, string caseId, string outputDirectory,
        CancellationToken cancellationToken = default, bool progressiveHttpProfileControl = false)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, "complex-subtitle-report.json");
        Require(!File.Exists(reportPath) && !Directory.EnumerateFiles(output, "complex-subtitle-*-ready.json").Any()
            && !Directory.EnumerateFiles(output, "complex-subtitle-*-finish.json").Any(), "FreshOutputDirectoryRequired");
        var report = progressiveHttpProfileControl ? new ComplexSubtitleReport
        {
            ExecutionMode = "ProgressiveHttpProfileControl", ProfileControl = true, DefaultEncodingProfileUsed = false,
            Scope = "Independent profile control using the original complex-subtitle fixture and actual product engine/coordinator. Only TranscodingProfiles container/protocol changes from ts/hls to mp4/http; ForceTranscoding stays false. Enabled, disabled, and progressive source-tail screenshots are separate prerequisites. This is not a default-profile or HLS compatibility result."
        } : new ComplexSubtitleReport();
        var diagnostics = new List<string>();
        var playbackEvents = new List<ComplexSubtitlePlaybackEvent>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var observer = new HlsApiObservationHandler();
        using var http = new HttpClient(observer) { Timeout = Timeout.InfiniteTimeSpan };
        EmbyApiClient? authenticated = null;
        PlaybackCoordinator? coordinator = null;
        NativePlaybackEngine? engine = null;
        void Save()
        {
            report.UpdatedUtc = DateTimeOffset.UtcNow;
            report.ApiEvents = observer.Events;
            lock (diagnostics) report.Diagnostics = [.. diagnostics];
            lock (playbackEvents) report.PlaybackEvents = [.. playbackEvents];
            WriteJson(reportPath, JsonSerializer.Serialize(report, ComplexSubtitleJsonContext.Default.ComplexSubtitleReport), "Report");
        }
        void RecordPlaybackEvent(ComplexSubtitlePlaybackEvent value)
        {
            lock (playbackEvents)
            {
                if (playbackEvents.Count >= 2000) { report.DroppedPlaybackEvents++; return; }
                value.Ordinal = playbackEvents.Count + 1;
                playbackEvents.Add(value);
                if (value.State == "Failed" && value.ErrorCode is not null) report.FirstPlaybackFailureCode ??= value.ErrorCode;
            }
        }
        ComplexSubtitlePlaybackEvent[] GetPlaybackEvents() { lock (playbackEvents) return [.. playbackEvents]; }
        try
        {
            Save();
            Require(report.NativeAot, "NativeAotRequired");
            Require(new FileInfo(manifestPath).Length is > 0 and <= 64 * 1024, "ManifestSizeInvalid");
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, deadline.Token);
            var manifest = JsonSerializer.Deserialize(manifestBytes, ComplexSubtitleJsonContext.Default.ComplexSubtitleManifest);
            Require(manifest is { FormatVersion: 1, Synthetic: true }
                && manifest.ServerId == "cf4feb10df224135877fc61204a28212" && manifest.ServerVersion == "4.9.5.0",
                "BoundOwnedFixtureManifestRequired");
            var fixture = manifest!.Cases.SingleOrDefault(value => value.CaseId == caseId);
            Require(fixture is not null, "FixtureCaseMissing");
            ValidateFixture(fixture!);
            report.Fixture = fixture;
            report.FixtureManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            var referencePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, fixture!.VisualReferenceFileName!);
            var referenceHash = await HashPngAsync(referencePath, deadline.Token);
            Require(referenceHash == fixture.VisualReferenceSha256, "VisualReferenceHashMismatch");
            const string copiedReference = "complex-subtitle-reference.png";
            File.Copy(referencePath, Path.Combine(output, copiedReference), overwrite: false);

            Require(new FileInfo(credentialsPath).Length is > 0 and <= 16 * 1024, "CredentialsFileSizeInvalid");
            var credentialBytes = await File.ReadAllBytesAsync(credentialsPath, deadline.Token);
            var credentials = JsonSerializer.Deserialize(credentialBytes, ExternalSubtitleJsonContext.Default.ExternalSubtitleCredentials);
            Require(credentials is not null && !string.IsNullOrEmpty(credentials.Username)
                && !string.IsNullOrEmpty(credentials.Password), "CredentialsMissing");
            Require(Uri.TryCreate(credentials!.ServerUrl, UriKind.Absolute, out var server)
                && server.Scheme == "http" && server.Host == "127.0.0.1" && server.Port == 19096
                && server.UserInfo.Length == 0 && server.Query.Length == 0 && server.Fragment.Length == 0
                && server.AbsolutePath.TrimEnd('/') is "" or "/emby", "OwnedOfficialLoopbackServerRequired");
            var device = "native-complex-subtitle-" + report.RunId;
            report.DeviceIdHash = Hash(device);
            var api = new EmbyApiClient(http, new Uri("http://127.0.0.1:19096"),
                new ClientIdentity("Emby Native Complex Subtitle Probe", "Windows complex subtitle probe", device, "0.1.0"));
            var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(publicInfo.Id == manifest.ServerId && publicInfo.Version == manifest.ServerVersion,
                "OfficialServerIdentityMismatch");
            report.ServerIdHash = Hash(publicInfo.Id!);
            AuthenticationResult authentication;
            try { authentication = await api.AuthenticateByNameAsync(credentials.Username!, credentials.Password!, deadline.Token); }
            finally { credentials.Password = null; Array.Clear(credentialBytes); }
            Require(authentication.AccessToken is not null && authentication.User?.Id is not null
                && authentication.ServerId == publicInfo.Id, "AuthenticationFailed");
            authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
            var item = await authenticated.GetItemAsync(fixture.ItemId!, deadline.Token);
            Require(item.Id == fixture.ItemId && item.Name == fixture.ExpectedItemName, "ManifestItemBindingMismatch");
            var source = item.MediaSources?.SingleOrDefault(value => value.Id == fixture.MediaSourceId);
            Require(source is not null && source.Container?.Equals("mkv", StringComparison.OrdinalIgnoreCase) == true
                && source.RunTimeTicks is long duration && Math.Abs(duration - fixture.RunTimeTicks) <= TimeSpan.FromSeconds(2).Ticks,
                "ManifestSourceBindingMismatch");
            var subtitle = source!.MediaStreams.SingleOrDefault(value => value.Index == fixture.SubtitleStreamIndex
                && value.Type?.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) == true);
            Require(subtitle is not null && subtitle.Codec?.Equals(fixture.ExpectedSubtitleCodec, StringComparison.OrdinalIgnoreCase) == true
                && subtitle.IsExternal == false, "ManifestEmbeddedSubtitleBindingMismatch");
            Require(fixture.Kind == "AssStyleAndEmbeddedFont" ? subtitle!.IsTextSubtitleStream == true
                : subtitle!.IsTextSubtitleStream == false, "SubtitleTextBitmapKindMismatch");
            report.ActualApiBindingVerified = true;

            engine = new NativePlaybackEngine(dispatcher, element, initialMuted: true);
            engine.EventReceived += (_, value) => RecordPlaybackEvent(new ComplexSubtitlePlaybackEvent
            {
                Observer = "Native", Kind = value.Kind.ToString(), State = value.Snapshot.State.ToString(),
                ErrorCode = value.ErrorCode is null ? null : SafeCode(value.ErrorCode), PlaybackIdHash = Hash(value.Snapshot.PlaybackId.ToString("N")),
                PositionTicks = value.Snapshot.PositionTicks, DurationTicks = value.Snapshot.DurationTicks
            });
            engine.Diagnostic += (_, value) => { lock (diagnostics) diagnostics.Add("Native:" + SafeCode(value.Operation) + ":" + SafeCode(value.ErrorCode)); };
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(10), CleanupTimeout = TimeSpan.FromSeconds(10)
            }, deviceProfile: progressiveHttpProfileControl ? CreateHttpControlProfile() : null);
            coordinator.StatusChanged += (_, value) => RecordPlaybackEvent(new ComplexSubtitlePlaybackEvent
            {
                Observer = "Coordinator", Kind = "StatusChanged", State = value.Status.ToString(),
                ErrorCode = value.ErrorCode is null ? null : SafeCode(value.ErrorCode),
                PlaybackIdHash = value.Context is null ? null : Hash(value.Context.PlaybackId.ToString("N")),
                PositionTicks = value.Context?.PositionTicks,
                RecoveryErrorCode = value.Recovery?.ErrorCode is { } recoveryCode ? SafeCode(recoveryCode) : null
            });
            coordinator.Diagnostic += (_, value) => { lock (diagnostics) diagnostics.Add(SafeCode(value.Operation) + ":" + SafeCode(value.ErrorCode)); };
            report.Status = "OpeningEncodedSubtitle";
            Save();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = fixture.ItemId!, MediaSourceId = fixture.MediaSourceId, SubtitleStreamIndex = fixture.SubtitleStreamIndex,
                StartPositionTicks = fixture.PauseTargetTicks, ForceTranscoding = false
            }, deadline.Token);
            var enabledContext = coordinator.ActiveContext ?? throw new ComplexSubtitleFailure("EnabledContextMissing");
            Require(enabledContext.DeliveryMethod == PlaybackDeliveryMethod.Transcode && enabledContext.Source.Id == fixture.MediaSourceId,
                "DefaultServerEncodingRequired");
            var enabled = NewPhase("Enabled", enabledContext);
            report.Phases.Add(enabled);
            await coordinator.PauseAsync(deadline.Token);
            await ObservePauseAsync(engine, coordinator, enabledContext, enabled, fixture.PauseTargetTicks, deadline.Token, progressiveHttpProfileControl);
            var on = enabled.Native!;
            Require(on.SelectedSubtitleStreamIndex == fixture.SubtitleStreamIndex && on.SelectedSubtitleDeliveryMethod == "Encode"
                && on.MediaRouteKind == (progressiveHttpProfileControl ? "ProgressiveVideo" : "HlsPlaylist") && on.RequestSubtitleMethod == "Encode"
                && on.RequestSubtitleStreamIndex == fixture.SubtitleStreamIndex, "ActualEncodeRouteNotConfirmed");
            if (progressiveHttpProfileControl)
            {
                RequireProgressiveStream(on);
                Require(on.TimelineOffsetTicks == fixture.PauseTargetTicks && on.PositionTicks <= TimeSpan.FromMilliseconds(750).Ticks,
                    "ProgressiveInitialEngineZeroNotConfirmed");
            }
            Require(on.SourcePositionTicks >= fixture.CueStartTicks && on.SourcePositionTicks < fixture.CueEndTicks, "EnabledCueWindowMissing");
            enabled.AutomatedChecksPassed = true;
            Save();
            await ConfirmVisualAsync(output, report, fixture, enabled, enabledContext, coordinator, engine,
                fixture.ExpectedVisualFeatureIds, fixture.Kind!, copiedReference, referenceHash, Save, deadline.Token);

            report.Status = "DisablingSubtitle";
            Save();
            await coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 }, deadline.Token);
            var disabledContext = coordinator.ActiveContext ?? throw new ComplexSubtitleFailure("DisabledContextMissing");
            report.SubtitleDisableReplacedPlayback = disabledContext.PlaybackId != enabledContext.PlaybackId
                && disabledContext.PlaySessionId != enabledContext.PlaySessionId;
            Require(report.SubtitleDisableReplacedPlayback && disabledContext.Source.Id == fixture.MediaSourceId
                && disabledContext.Selection.SubtitleStreamIndex == -1, "SubtitleDisableDidNotReplaceSelection");
            var disabled = NewPhase("Disabled", disabledContext);
            report.Phases.Add(disabled);
            await ObservePauseAsync(engine, coordinator, disabledContext, disabled, on.SourcePositionTicks!.Value, deadline.Token, progressiveHttpProfileControl);
            Require(disabled.Native!.SelectedSubtitleStreamIndex == -1, "NativeSubtitleDisableMissing");
            report.SubtitleDisablePreservedPauseAndPosition = true;
            disabled.AutomatedChecksPassed = true;
            Save();
            await ConfirmVisualAsync(output, report, fixture, disabled, disabledContext, coordinator, engine,
                ["subtitles-absent", "same-video-scene"], "SubtitleDisabled", copiedReference, referenceHash, Save, deadline.Token);
            if (progressiveHttpProfileControl)
                await RunProgressiveTailAsync(output, report, fixture, disabledContext, coordinator, engine,
                    copiedReference, referenceHash, Save, GetPlaybackEvents, deadline.Token);
            report.Status = "VisualObservationsComplete";
        }
        catch (Exception error)
        {
            report.FailureStage = report.Status;
            report.ErrorCode = ErrorCode(error);
            report.ErrorType = error is ComplexSubtitleOutputException outputError ? outputError.OriginalType : error.GetType().Name;
            report.ErrorHResult = error.HResult;
            report.OutputFailureOperation = (error as ComplexSubtitleOutputException)?.Operation;
            report.Status = "Failed";
        }
        finally
        {
            if (coordinator is not null)
            {
                try { await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(35)); report.StopCompleted = true; }
                catch (Exception error) { report.CleanupErrors.Add("Stop:" + ErrorCode(error)); }
                try { await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(35)); report.CoordinatorDisposed = true; }
                catch (Exception error) { report.CleanupErrors.Add("CoordinatorDispose:" + ErrorCode(error)); }
            }
            if (engine is not null)
            {
                try
                {
                    await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    report.NativeDisposed = true;
                    report.NativeOwnerCleared = await engine.ComplexSubtitleOwnerClearedForProbeAsync();
                    if (!report.NativeOwnerCleared) report.CleanupErrors.Add("NativeOwnerNotCleared");
                }
                catch (Exception error) { report.CleanupErrors.Add("NativeDispose:" + ErrorCode(error)); }
            }
            if (authenticated is not null)
            {
                try
                {
                    using var logout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await authenticated.LogoutAsync(logout.Token);
                    report.LogoutCompleted = true;
                }
                catch (Exception error) { report.CleanupErrors.Add("Logout:" + ErrorCode(error)); }
            }
            Save();
            report.ApiSessionOrderAndCleanupVerified = VerifySessionCleanup(report.Phases, report.ApiEvents,
                progressiveHttpProfileControl, report.TailUsedNewEncodedSession);
            if (report.ErrorCode is null && report.Phases.Count == (progressiveHttpProfileControl ? 3 : 2)
                && report.Phases.All(value => value.AutomatedChecksPassed && value.VisualConfirmed)
                && report.SubtitleDisableReplacedPlayback && report.SubtitleDisablePreservedPauseAndPosition
                && report.StopCompleted && report.CoordinatorDisposed && report.NativeDisposed && report.NativeOwnerCleared
                && report.LogoutCompleted && report.ApiSessionOrderAndCleanupVerified && report.CleanupErrors.Count == 0 && report.Diagnostics.Length == 0
                && report.FirstPlaybackFailureCode is null && report.DroppedPlaybackEvents == 0
                && (!progressiveHttpProfileControl || report.TailNativeEndedObserved && report.TailCoordinatorEndedObserved && report.TailDrainCompleted))
                report.Status = progressiveHttpProfileControl ? "ControlPassed" : "Passed";
            else if (report.ErrorCode is null) { report.Status = "Failed"; report.ErrorCode = "ComplexSubtitleFinalValidationFailed"; }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            Save();
        }
        return report;
    }

    private static void ValidateFixture(ComplexSubtitleCase fixture)
    {
        Require(fixture.CaseId is "ass-styled" or "pgs-bitmap" && Identifier(fixture.ItemId) && Identifier(fixture.MediaSourceId)
            && fixture.SubtitleStreamIndex >= 0 && fixture.ExpectedItemName is { Length: > 0 and <= 200 }
            && fixture.ExpectedCue is { Length: > 0 and <= 200 }, "BoundFixtureCaseRequired");
        Require(fixture.PauseTargetTicks == TimeSpan.FromSeconds(41).Ticks && fixture.CueStartTicks == TimeSpan.FromSeconds(35).Ticks
            && fixture.CueEndTicks == TimeSpan.FromSeconds(45).Ticks && fixture.RunTimeTicks >= TimeSpan.FromSeconds(46).Ticks
            && fixture.RunTimeTicks <= TimeSpan.FromMinutes(3).Ticks, "ExpectedFixtureTimelineRequired");
        Require(IsHash(fixture.MediaSha256) && IsHash(fixture.VisualReferenceSha256)
            && LocalPngName(fixture.VisualReferenceFileName), "FixtureEvidenceHashesRequired");
        string[] expectedFeatures = fixture.CaseId == "ass-styled"
            ? ["ass-green-fill", "ass-magenta-outline", "ass-upper-left-position", "bungee-shade-glyphs"]
            : ["pgs-white-fill", "pgs-black-outline", "pgs-bottom-center-position"];
        Require(fixture.ExpectedVisualFeatureIds.Order().SequenceEqual(expectedFeatures.Order()), "ExpectedVisualFeaturesMismatch");
        Require(fixture.CaseId == "ass-styled"
            ? fixture.Kind == "AssStyleAndEmbeddedFont" && fixture.ExpectedSubtitleCodec == "ass"
                && fixture.ExpectedCue == "ATTACHED FONT CHECK" && fixture.EmbeddedFontFamily == "Bungee Shade" && IsHash(fixture.EmbeddedFontSha256)
            : fixture.Kind == "PgsBitmapOverlay" && fixture.ExpectedSubtitleCodec == "pgssub"
                && fixture.ExpectedCue == "PGS BITMAP CHECK", "ComplexSubtitleFixtureKindMismatch");
    }

    private static ComplexSubtitlePhase NewPhase(string phase, PlaybackContext context) => new()
    {
        Phase = phase, PlaybackIdHash = Hash(context.PlaybackId.ToString("N")), SessionHash = Hash(context.PlaySessionId),
        DeliveryMethod = context.DeliveryMethod.ToString()
    };

    private static async Task ObservePauseAsync(NativePlaybackEngine engine, PlaybackCoordinator coordinator, PlaybackContext context,
        ComplexSubtitlePhase phase, long expectedPosition, CancellationToken token, bool progressiveControl = false)
    {
        phase.CoordinatorBeforePauseStability = ObserveCoordinator(coordinator, context.PlaybackId);
        var before = await engine.ObserveComplexSubtitleForProbeAsync(context.PlaybackId);
        phase.NativeBeforePauseStability = before;
        await Task.Delay(650, token);
        var after = await engine.ObserveComplexSubtitleForProbeAsync(context.PlaybackId);
        phase.CoordinatorAfterPauseStability = ObserveCoordinator(coordinator, context.PlaybackId);
        phase.Native = after;
        phase.PauseDriftTicks = before.ActivePlaybackMatches && after.ActivePlaybackMatches
            ? Math.Abs(after.PositionTicks - before.PositionTicks) : null;
        var validTimeline = !progressiveControl
            ? after.TimelineKind == nameof(PlaybackTimelineKind.FullSource) && after.TimelineOffsetTicks == 0
            : after.DeliveryMethod == nameof(PlaybackDeliveryMethod.Transcode)
                ? after.TimelineKind == nameof(PlaybackTimelineKind.ProgressiveSegment) && after.HasProgressiveRelay && !after.HasFullSourceRangeRelay
                : after.DeliveryMethod == nameof(PlaybackDeliveryMethod.DirectStream)
                    && after.TimelineKind == nameof(PlaybackTimelineKind.FullSource) && after.TimelineOffsetTicks == 0 && after.HasFullSourceRangeRelay;
        Require(before.ActivePlaybackMatches && after.ActivePlaybackMatches && after.DefaultPlayerOwnerPresent
            && before.State == "Paused" && after.State == "Paused" && phase.PauseDriftTicks is long drift && drift <= TimeSpan.FromMilliseconds(50).Ticks
            && after.VideoWidth > 0 && after.VideoHeight > 0 && after.MediaOriginIsOwnedLoopback
            && !after.HasExternalSubtitleUri && !after.HasTimedTextSource
            && validTimeline && after.SourcePositionTicks is long sourcePosition
            && Math.Abs(sourcePosition - expectedPosition) <= TimeSpan.FromMilliseconds(750).Ticks,
            "ActualPausedSourcePositionNotConfirmed");
    }

    private static ComplexSubtitleCoordinatorObservation ObserveCoordinator(PlaybackCoordinator coordinator, Guid expectedId)
    {
        var context = coordinator.ActiveContext;
        var recovery = coordinator.Recovery;
        return new ComplexSubtitleCoordinatorObservation
        {
            Status = coordinator.Status.ToString(), ActivePlaybackMatches = context?.PlaybackId == expectedId,
            PositionTicks = context?.PositionTicks, TimelineKind = context?.TimelineKind.ToString(),
            RecoveryErrorCode = recovery is null ? null : SafeCode(recovery.ErrorCode), RecoveryIsPaused = recovery?.IsPaused,
            RecoveryPositionTicks = recovery?.Selection.StartPositionTicks
        };
    }

    private static async Task ConfirmVisualAsync(string output, ComplexSubtitleReport report, ComplexSubtitleCase fixture,
        ComplexSubtitlePhase phase, PlaybackContext context, PlaybackCoordinator coordinator, NativePlaybackEngine engine,
        string[] expectedFeatures, string evidenceKind, string referenceFile, string referenceHash, Action save, CancellationToken token)
    {
        var phaseName = phase.Phase.ToLowerInvariant();
        var finishFile = "complex-subtitle-" + phaseName + "-finish.json";
        var finishPath = Path.Combine(output, finishFile);
        var visualDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var ready = new ComplexSubtitleReady
        {
            RunId = report.RunId, CaseId = fixture.CaseId!, Phase = phase.Phase, EvidenceKind = evidenceKind,
            ProcessId = report.ProcessId, ProfileControl = report.ProfileControl,
            PausedPositionTicks = phase.Native!.SourcePositionTicks!.Value, NativePositionTicks = phase.Native.PositionTicks,
            TimelineOffsetTicks = phase.Native.TimelineOffsetTicks, TimelineKind = phase.Native.TimelineKind, ExpectedCue = fixture.ExpectedCue,
            ExpectedFeatureIds = expectedFeatures, ReferenceFileName = referenceFile, ReferenceSha256 = referenceHash,
            EnabledScreenshotFileName = phase.Phase == "Disabled" ? report.Phases[0].ScreenshotFileName : null,
            EnabledScreenshotSha256 = phase.Phase == "Disabled" ? report.Phases[0].ScreenshotSha256 : null,
            FinishFileName = finishFile, DeadlineUtc = visualDeadline
        };
        report.Status = "Awaiting" + phase.Phase + "VisualInspection";
        save();
        WriteJson(Path.Combine(output, "complex-subtitle-" + phaseName + "-ready.json"),
            JsonSerializer.Serialize(ready, ComplexSubtitleJsonContext.Default.ComplexSubtitleReady), phase.Phase + "Ready");
        while (DateTimeOffset.UtcNow < visualDeadline)
        {
            token.ThrowIfCancellationRequested();
            Require(coordinator.ActiveContext?.PlaybackId == context.PlaybackId && engine.Snapshot?.State == PlaybackEngineState.Paused,
                "VisualInspectionPlaybackChanged");
            var finish = await ReadFinishAsync(finishPath, token);
            if (finish is null) { await Task.Delay(200, token); continue; }
            Require(finish.RunId == report.RunId && finish.CaseId == fixture.CaseId && finish.Phase == phase.Phase
                && finish.ConfirmedBy == "RootAgentScreenshotInspection", "VisualConfirmationIdentityMismatch");
            Require(finish.VisualConfirmed && finish.ReferenceCompared && finish.ConfirmedFeatureIds.Order().SequenceEqual(expectedFeatures.Order()),
                "RequiredVisualFeaturesNotConfirmed");
            Require(LocalPngName(finish.ScreenshotFileName), "ScreenshotFileNameInvalid");
            phase.ScreenshotSha256 = await HashPngAsync(Path.Combine(output, finish.ScreenshotFileName!), token);
            Require(phase.ScreenshotSha256 != referenceHash, "ReferenceCannotReplaceActualScreenshot");
            Require(phase.Phase != "Disabled" || phase.ScreenshotSha256 != report.Phases[0].ScreenshotSha256,
                "EnabledScreenshotCannotReplaceDisabledObservation");
            Require(phase.Phase != "TailFrame" || report.Phases.Take(2).All(previous => previous.ScreenshotSha256 != phase.ScreenshotSha256),
                "EarlierScreenshotCannotReplaceTailObservation");
            phase.ScreenshotFileName = finish.ScreenshotFileName;
            phase.ConfirmedFeatureIds = finish.ConfirmedFeatureIds;
            phase.ConfirmedBy = finish.ConfirmedBy;
            phase.VisualConfirmed = true;
            save();
            return;
        }
        throw new ComplexSubtitleFailure("VisualInspectionTimedOut");
    }

    private static bool VerifySessionCleanup(List<ComplexSubtitlePhase> phases, HlsApiEvent[] events,
        bool progressiveControl, bool tailUsedNewSession)
    {
        var expectedSessions = progressiveControl && tailUsedNewSession ? 3 : 2;
        if (phases.Count != (progressiveControl ? 3 : 2) || phases.Select(value => value.SessionHash).Distinct().Count() != expectedSessions) return false;
        var sessions = phases.GroupBy(value => value.SessionHash).Select(group => group.First()).ToArray();
        if (events.Any(value => value.StatusCode is < 200 or >= 300)) return false;
        if (events.Count(value => value.Operation == "Start") != expectedSessions || events.Count(value => value.Operation == "Stop") != expectedSessions
            || events.Count(value => value.Operation == "StopEncoding") != sessions.Count(value => value.DeliveryMethod == "Transcode")) return false;
        foreach (var phase in sessions)
        {
            var own = events.Where(value => value.SessionHash == phase.SessionHash).ToArray();
            if (own.Count(value => value.Operation == "Start") != 1 || own.Count(value => value.Operation == "Stop") != 1) return false;
            var start = Array.FindIndex(own, value => value.Operation == "Start");
            var stop = Array.FindIndex(own, value => value.Operation == "Stop");
            if (start < 0 || stop <= start || own.Any(value => value.SessionHash is null)) return false;
            var expectedEncoding = phase.DeliveryMethod == "Transcode" ? 1 : 0;
            if (own.Count(value => value.Operation == "StopEncoding") != expectedEncoding) return false;
            if (expectedEncoding == 1 && Array.FindIndex(own, value => value.Operation == "StopEncoding") <= stop) return false;
        }
        return true;
    }

    private static async Task<ComplexSubtitleFinish?> ReadFinishAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        Require(new FileInfo(path).Length <= 8192, "VisualConfirmationTooLarge");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize(await reader.ReadToEndAsync(token), ComplexSubtitleJsonContext.Default.ComplexSubtitleFinish);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static async Task<string> HashPngAsync(string path, CancellationToken token)
    {
        Require(new FileInfo(path).Length is >= 8 and <= 100 * 1024 * 1024, "PngEvidenceMissing");
        await using var stream = File.OpenRead(path);
        var signature = new byte[8];
        await stream.ReadExactlyAsync(signature, token);
        Require(signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "ScreenshotMustBePng");
        stream.Position = 0;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    private static bool LocalPngName(string? value) => value is { Length: > 0 and <= 120 } && value == Path.GetFileName(value)
        && !value.Contains(':') && !value.Contains('/') && !value.Contains('\\') && value.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string SafeCode(string value) => value.Length is > 0 and <= 80
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? value : "Other";
    private static string ErrorCode(Exception error) => error switch
    {
        ComplexSubtitleFailure failure => failure.Code,
        ComplexSubtitleOutputException => "ProbeOutputFailure",
        PlaybackException playback => SafeCode(playback.ErrorCode),
        EmbyApiException api => "ApiHttp" + ((int)api.StatusCode).ToString(CultureInfo.InvariantCulture),
        OperationCanceledException => "Cancelled", TimeoutException => "Timeout", _ => error.GetType().Name
    };
    private static void WriteJson(string path, string text, string purpose)
    {
        // Save is called only by the single scenario task. Event handlers do not write files.
        // No access-denied error is reclassified as sharing or silently retried.
        try { File.WriteAllText(path + ".tmp", text, new UTF8Encoding(false)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ComplexSubtitleOutputException(purpose + ".WriteTemporary", error); }
        try { File.Move(path + ".tmp", path, overwrite: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ComplexSubtitleOutputException(purpose + ".Replace", error); }
    }
    private static void Require(bool condition, string code) { if (!condition) throw new ComplexSubtitleFailure(code); }
    private sealed class ComplexSubtitleFailure(string code) : Exception(code) { internal string Code { get; } = code; }
}

internal sealed class ComplexSubtitleOutputException : Exception
{
    internal ComplexSubtitleOutputException(string operation, Exception error) : base("Probe output failed.")
    {
        Operation = operation;
        OriginalType = error.GetType().Name;
        HResult = error.HResult;
    }
    internal string Operation { get; }
    internal string OriginalType { get; }
}
