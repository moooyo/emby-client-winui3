# Complex subtitles through the default server encoding profile

This mode has compiled and published as NativeAOT in independent source-audited directories. Its first ASS attempt failed before visual readiness and remains recorded separately; no complex-subtitle acceptance has been established. Running it requires the coordinated verification window from the task owner and the actual server-bound manifest. It does not supersede the completed external WebVTT, poster, or playback lifecycle receipts.

The independent entry point is:

```csharp
Task<ComplexSubtitleReport> ComplexSubtitleProbe.RunAsync(
    DispatcherQueue dispatcher,
    MediaPlayerElement element,
    string credentialsPath,
    string manifestPath,
    string caseId,
    string outputDirectory,
    CancellationToken cancellationToken = default,
    bool progressiveHttpProfileControl = false,
    string? expectedServerId = null);
```

The default-mode dispatch is `--output-dir <fresh-directory> --complex-subtitle --credentials-file <owned-file> --fixture-manifest <bound-manifest> --case <ass-styled|pgs-bitmap> [--expected-server-id <verified-id>]`. When runtime verification is authorized, it opens one independent native window without requesting activation and runs exactly one selected case. All new source files are included by the existing recursive NativeProbe source audit. Native product files are linked rather than copied into this probe. The independent [HTTP profile control](PROGRESSIVE-CONTROL.md) is explicitly marked and does not replace the default HLS result.

The credentials file is read only inside the process. The endpoint remains fixed to `http://127.0.0.1:19096` and version `4.9.5.0`. An explicit `--expected-server-id` must come from the operator's independent verification of the restored or newly created owned server, not from blindly reading the fixture manifest. It must contain exactly 32 ASCII hexadecimal characters. Its original spelling is compared exactly with the manifest, public system information, and authentication response; it is not normalized or inferred. Omitting the option retains the legacy pinned identity `cf4feb10df224135877fc61204a28212` for legacy-scope runs.

A newly created server requires a newly bound manifest containing its actual ID, item/source IDs, and API subtitle indexes, plus its own disposable credentials file. Do not relabel an old manifest as a new server or modify historical receipts. The case IDs select fixture descriptions, not Emby item IDs. Both item and media-source IDs are verified again against the actual API. A generation-only manifest with null IDs is rejected. An embedded subtitle index from FFprobe is insufficient until the selected API source confirms its codec, index, embedded status, and text/bitmap kind. The independent RealHls and ExternalSubtitle modes retain their separate legacy identity pins and are not broadened by this option.

The fixture manifest schema is `FormatVersion: 1`, `Synthetic: true`, `ServerId`, `ServerVersion`, and a `Cases` array. Each case contains these fields:

| Field | Meaning |
| --- | --- |
| `CaseId`, `Kind` | `ass-styled` / `AssStyleAndEmbeddedFont`, or `pgs-bitmap` / `PgsBitmapOverlay` |
| `ItemId`, `MediaSourceId`, `SubtitleStreamIndex` | Actual bound API identity and explicitly selected embedded subtitle |
| `ExpectedItemName`, `ExpectedSubtitleCodec`, `RunTimeTicks` | API binding checks; codec is `ass` or `pgssub` |
| `MediaSha256` | Generator's media-file hash; the probe does not re-download the media |
| `PauseTargetTicks`, `CueStartTicks`, `CueEndTicks` | 41, 35, and 45 seconds expressed in 100-nanosecond ticks |
| `ExpectedCue` | `ATTACHED FONT CHECK` or `PGS BITMAP CHECK` |
| `ExpectedVisualFeatureIds` | The exact required features listed below |
| `VisualReferenceFileName`, `VisualReferenceSha256` | Local reference PNG beside the manifest; its signature and hash are verified and it is copied into the run directory |
| `EmbeddedFontFamily`, `EmbeddedFontSha256` | ASS fixture's `Bungee Shade` attachment identity and generator hash |

The attachment hash is fixture preparation evidence, not proof that the server renderer loaded that font. The enabled screenshot must independently show its distinctive glyphs relative to the reference. PGS is inspected as a bitmap overlay; a cue label or text match is not substituted for its visual shape and placement.

The probe leaves `EnableExternalWebVtt` at its product default of false and does not supply a custom device profile. It explicitly chooses the target subtitle with `ForceTranscoding=false`, so a passed observation demonstrates that the default negotiation selected server encoding. The enabled phase requires a real Transcode context, selected stream delivery `Encode`, an HLS media route carrying `SubtitleMethod=Encode` and the requested subtitle index, no native external subtitle URI or TimedTextSource, and the default native player owner.

It opens at 41 seconds through the normal product timeline handling, pauses, and samples the real native clock twice 650 ms apart. Both samples must be paused, the drift must be at most 50 ms, the position must be within 750 ms of the target and inside the cue, and video dimensions must be positive. These prerequisites do not prove pixels. The mode writes `complex-subtitle-enabled-ready.json` and waits at most three minutes for real screenshot inspection.

The current instrumentation preserves both native observations, paired coordinator status/context/recovery observations, and a bounded passive event log from the native engine and coordinator. Only fixed event/state/error codes, numeric positions/durations, and hashed playback IDs are recorded. These subscriptions do not alter classification, request another playlist, inject an event, or relax the observation gates. A missing native session has `ActivePlaybackMatches=false` and position -1; it is not a real position sample. Natural retirement between observations remains a failure, with its observed event code retained separately from the assertion failure.

Required enabled feature IDs are:

- ASS: `ass-green-fill`, `ass-magenta-outline`, `ass-upper-left-position`, `bungee-shade-glyphs`.
- PGS: `pgs-white-fill`, `pgs-black-outline`, `pgs-bottom-center-position`.

The root agent must view the actual probe window, compare it with the reference image, save a new PNG screenshot in the run directory, and write the ready file's named finish file. For example, an ASS enabled confirmation has this structure:

Use `Wait-ComplexSubtitleReady.ps1 -OutputDirectory <run-directory> -ProbeProcessId <pid> -Phase Enabled` for bounded monitoring. It checks the ready file's existence and the process while the probe runs; it does not repeatedly open the mutable report. A ready file or completed report is read briefly using `FileShare.ReadWrite | FileShare.Delete`, so the monitor permits atomic replacement. Read the report after process exit. Do not use a continuous `Get-Content` loop against the active report. The earlier PGS output failure has no captured operation/HResult and remains unassigned; the old monitor's reads are only a possible source of contention.

Report writes are serialized by the scenario task; passive event handlers do not save files. A write failure now records its original exception type and HResult, the scenario's `FailureStage`, and a fixed `OutputFailureOperation` such as `Report.WriteTemporary`, `Report.Replace`, or `EnabledReady.Replace`. Access-denied errors are not assumed to be sharing errors and are not suppressed or retried. The finish-file reader also permits read/write/delete sharing. These changes affect output observation only and do not modify the media checks.

```json
{
  "RunId": "COPY_FROM_THIS_READY_FILE",
  "CaseId": "ass-styled",
  "Phase": "Enabled",
  "ConfirmedBy": "RootAgentScreenshotInspection",
  "VisualConfirmed": true,
  "ReferenceCompared": true,
  "ConfirmedFeatureIds": [
    "ass-green-fill",
    "ass-magenta-outline",
    "ass-upper-left-position",
    "bungee-shade-glyphs"
  ],
  "ScreenshotFileName": "ass-enabled.png"
}
```

Use the exact selected case and feature set from the ready file. Do not create a positive finish merely because API or native-property checks passed. The protocol records the confirmation as the root agent's visual judgment; the probe does not run OCR or automatically judge those pixels. A reference image cannot serve as the actual screenshot. Preserve JPEG capture bytes as `.jpg` when applicable, decode and save a real PNG without resizing or editing content, verify the PNG signature and decoded RGB equivalence, and only then submit it. Renaming JPEG bytes does not satisfy the PNG requirement.

After the enabled confirmation, `ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 })` uses the normal product path to retire the encoded session and open a new selection. The mode requires new playback and server-session IDs, the same source, native subtitle selection -1, restored pause state, and a native position within 750 ms of the enabled paused position. It then writes `complex-subtitle-disabled-ready.json`, including the previous screenshot's filename/hash, and waits for a second real screenshot. That finish must use `Phase: "Disabled"` and exactly `ConfirmedFeatureIds: ["subtitles-absent", "same-video-scene"]`. The enabled screenshot cannot be reused as the disabled screenshot. The disabled delivery method is recorded; it is not forced to DirectStream because source compatibility remains the server's decision.

Regardless of success, rejection, timeout, or cancellation, cleanup attempts the probe's coordinator stop/disposal, engine disposal with cleared owner, and logout. No global or unrelated-session stop is issued. A final `Passed` also requires two successful Start/Stop pairs, each ordered by its hashed server-session ID, and one successful StopEncoding after Stop for every Transcode session. API observations do not measure native HLS packet quiescence. Both visual phases and cleanup are required; any failure stays failed in its original report.

Each run covers one prepared cue and its subsequent disabling. ASS style/font and PGS bitmap evidence remain separate. No broad format matrix, long-duration stability, twenty-loop subtitle resource result, or native client-side ASS/PGS renderer is claimed.
