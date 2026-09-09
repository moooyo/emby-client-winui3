# Native external WebVTT visual inspection

This independent mode uses the default product `NativePlaybackEngine`, a real `PlaybackCoordinator` with `EnableExternalWebVtt=true`, and the existing official Emby Server 4.9.5.0 fixture at `127.0.0.1:19096`. It accepts only item `5`, source `mediasource_5`, and external SRT stream index `2`. It creates a unique client/device authentication session using the supplied disposable account. It does not create users or change account configuration.

The entry point is independent of App fields or CLI wiring:

```csharp
Task<ExternalSubtitleReport> ExternalSubtitleProbe.RunAsync(
    DispatcherQueue dispatcher,
    MediaPlayerElement element,
    string credentialsPath,
    string outputDirectory,
    CancellationToken cancellationToken = default);
```

The application now wires this entry point through `--output-dir <new-directory> --external-subtitle --credentials-file <owned-file>`. It keeps the dispatcher/window alive through the bounded observation and cleanup, then exits. The official server ID is checked before authentication.

The caller must show and retain the window and `MediaPlayerElement`, await the entry point without blocking the dispatcher, and keep the dispatcher alive during cleanup. It may use `report.Status == "Passed"` to select its final exit code. Publish/build/run must be coordinated separately with the NativeProbe owner.

Use a fresh output directory. The mode rejects preexisting report/ready/finish files, requires NativeAOT, authenticates only to the owned loopback origin, and uses a new device ID. It leaves audio selection automatic and does not force transcoding. A burned-in fallback is a failed external-caption observation, even if readable text appears on screen.

The product downloads WebVTT into its own separate subtitle transport and memory stream. A probe-only partial observer reads that completed buffer, its header/hash, the real `TimedTextSource`, successful resolution state, resolved tracks, and their actual `PlatformPresented` modes. It does not inject a player, transport, track, presentation mode, or engine event. It performs no second subtitle download and does not invent an HTTP status code from the observed memory buffer.

The native clock runs from zero until 41 seconds, when the coordinator pauses it. The expected visible cue is:

```text
Seek and subtitle delivery check.
```

Its actual timing is read from the downloaded WebVTT. The mode requires a paused native position inside that cue, a DirectStream context, positive video dimensions, an independent authenticated subtitle path, a successfully resolved external track, and a platform-presented mode. These checks are prerequisites for visual inspection; none is treated as proof of caption pixels.

At that point the mode writes `external-subtitle-ready.json` and records `AwaitingVisualInspection` in `external-subtitle-report.json`. The source remains paused for at most three minutes. The root agent must use computer-use screenshot inspection to actually observe this cue, save the screenshot as a PNG in the output directory, then write `external-subtitle-finish.json` with the current ready file's run ID:

```json
{
  "RunId": "COPY_FROM_THIS_RUN_READY_FILE",
  "PresentedCueConfirmed": true,
  "ConfirmedBy": "RootAgentScreenshotInspection",
  "ObservedCue": "Seek and subtitle delivery check.",
  "ScreenshotFileName": "external-subtitle-cue.png"
}
```

Only that exact confirmation identity, expected text, matching run ID, and a local PNG permit `PresentedCueConfirmed=true`. The report records the screenshot hash. The probe does not inspect or judge screenshot pixels itself; confirmation is attributed solely to the root agent's screenshot inspection. An absent, negative, stale, or malformed confirmation cannot become a visual pass. It must not be written merely because API/property checks succeeded.

Check the capture's actual encoding before submitting it. Some screenshot tools return JPEG even when a chosen filename ends in `.png`. Preserve that original as `.jpg`, decode it with Pillow, and save a separate real PNG without resizing or editing image content. Verify the PNG signature `89 50 4E 47 0D 0A 1A 0A` and compare the decoded RGB bytes of both images before writing the finish file. Renaming JPEG bytes is not conversion. A failed completed run remains failed; a new run requires its own screenshot inspection and matching run ID.

After confirmation, timeout, cancellation, or failure, the mode independently attempts its coordinator stop/dispose, native engine disposal, and its own logout. The normal coordinator handles only its own playback and encoding cleanup; no unscoped stop-encoding request is sent. `Passed` additionally requires clean disposal, no playback diagnostics, and exactly one successful start and stop report. The supplied disposable account may receive ordinary playback history for this fixture; historical user data is not silently rewritten afterward.

This result covers one plain external WebVTT cue rendered by the Windows native presenter. It does not establish ASS style fidelity, bitmap/PGS rendering, subtitle resource-loop behavior, or a broad subtitle-format matrix.
