# Native playback lifecycle probe

This Windows-only .NET 10 / Windows App SDK probe publishes as an unpackaged, self-contained NativeAOT executable. It compiles the product's `src/EmbyClient.App/Playback/*.cs` files through project links and references the real API and playback coordinator projects. It does not maintain a second playback implementation. Dependency versions come from the repository's central package management.

The default lifecycle and synthetic controls accept only `http://127.0.0.1:18961/emby/`. Before using the fixed synthetic `demo` / `demo` account, the probe checks the fixture response header, the explicit synthetic statistics flag, and exact synthetic public server metadata. It then requires item `1001` to identify itself as synthetic and have a measured duration between 59 and 61 seconds. Do not use real credentials with the fixture. The separately authorized `--real-hls` mode described below has a different fixed identity gate and credentials-file contract; it cannot silently replace a synthetic run.

The probe opens a small native `MediaPlayerElement` window without requesting activation. It uses programmatic API calls, never UI input injection, and initializes the actual product engine with `initialMuted: true` before media is attached.

## Run

Generate 60-second synthetic media and start a dedicated fixture on port 18961. Keep that fixture exclusive to this probe so that request deltas remain attributable to the run:

```powershell
dotnet run --project tools/EmbyClient.MediaFixtures/EmbyClient.MediaFixtures.csproj --configuration Release -- --output-dir artifacts/probes/media-60 --duration-seconds 60
dotnet run --project tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release -- --media-dir artifacts/probes/media-60 --port 18961
```

In another PowerShell session, publish only the probe project:

```powershell
dotnet publish tools/EmbyClient.NativeProbe/EmbyClient.NativeProbe.csproj --configuration Release -p:Platform=x64 --output artifacts/probes/native-probe-publish
$runDirectory = Join-Path (Get-Location) ('artifacts/probes/native-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$probe = Start-Process -FilePath (Join-Path (Get-Location) 'artifacts/probes/native-probe-publish/EmbyClient.NativeProbe.exe') -ArgumentList @('--output-dir', $runDirectory) -WindowStyle Hidden -PassThru
$probe.Id
```

Pass a new output directory for every run. The tool refuses to replace an existing `result.json`. Inspect that JSON while the process is running; it is atomically replaced after each completed loop and includes `ProcessId`, timestamps, stage, completed-loop count, and a final status. Check the recorded process before starting another instance. The window closes after the report is finalized, and the exit code is zero only for a passing run. A report left at `Running` after process termination is incomplete evidence, never a pass.

## What is measured

All 20 loops perform API negotiation, actual native open/play, pause, seek, resume, and stop. Each loop records:

- Positive native media position plus nonzero native video width and height.
- Initial mute, pause completion, and no more than 50 ms of clock drift during a 650 ms paused interval.
- A seek to 8-17 seconds on the original source timeline, within 750 ms of the requested position, followed by an advancing resumed clock.
- Native player detachment, coordinator retirement, and a native stopped snapshot.
- Authenticated media range traffic, exactly one start and stop report, ordered pause/resume progress reports, and a stopped position matching the source timeline.
- No additional media requests, range requests, playback negotiation, or reports for the retired session during a 1.3-second post-stop observation.
- Private memory, working set, managed memory, process handles, and thread count after consistent finalizer drainage that leaves the UI dispatcher available.

The final resource check compares the median of the last five loops with loops 5-9. Growth must remain within 64 MiB private memory and 32 process handles. It additionally fails if the last eight samples all increase in handle count or all increase in private memory by more than 1 MiB per loop. Raw samples and the actual deltas remain in the report; these limits are bounded regression observations, not proof that no long-running leak exists.

Per-loop resource samples run after the native loop method returns and explicitly releases its player reference, so the probe's predicate closures do not intentionally retain a stopped player. After all loops, the coordinator is disposed and the synthetic account is logged out. Three additional samples at approximately 2, 10, and 30 seconds idle record delayed native release; these diagnostic samples do not override a failed per-loop resource threshold. The fixture counters must also remain unchanged across this final idle observation.

This probe deliberately retains normal `MediaPlayerElement` rendering. It does not switch the player into frame-server mode. Native position and dimensions **do not prove that the first pixel frame reached the display**. The JSON explicitly leaves pixel capture/first presented frame, real Emby versions, audible output and device switching, HLS/transcoding, and external subtitles unverified. Visual rendering must be inspected separately. A synthetic pass must never be represented as real-server compatibility.

JSON serialization and deserialization always use `ProbeJsonContext` source-generated metadata. Reports include synthetic IDs and safe error codes, not tokens, passwords, request bodies, or exception messages. A 12-minute deadline bounds each run; engine cleanup and synthetic logout execute before the final result is written.

Both coordinator diagnostics and the native engine's individual release-operation diagnostics are retained. Native diagnostics use the `Native:` prefix, followed by the operation and safe error code. Any diagnostic fails the complete lifecycle run even when all 20 operation loops finish.

`ClosedReadOperationsDuringStop` is retained only for historical report compatibility. The failed managed bridge and its counter were removed from the product; new reports leave this field at zero (not applicable). Historical JSON files are not rewritten.

The product lifecycle probe now updates system media controls on coordinator status changes and during its native state checks. Every loop checks the synthetic title and enabled playing/paused controls. A synchronous native `Stopped` event observer checks disabled controls, closed status, and cleared metadata after `RetireMediaControls` but before the owning `MediaPlayer.Dispose`. It reads the metadata type first: `Unknown` is valid evidence that `ClearAll` removed metadata; `VideoProperties.Title` is read only when the remaining type is `Video`. Every getter stage and any HRESULT are recorded. Reading those COM properties after player disposal is not a valid assertion; the observer is unsubscribed after each loop. This exercises the real SMTC subscription and cleanup paths. It does not inject or certify physical system media keys. The separate `--instrumentation-smoke` mode executes one loop and never evaluates or claims the 20-loop resource gate.

## Auditable publication and committed evidence

Use `Publish-Probe.ps1 -OutputDirectory <new-directory>` for final acceptance builds. It hashes repository sources before and after publication and rejects changes during compilation. Its `build-manifest.json` contains relative source paths and SHA-256 values, the actual SDK/resolved package identities, and the NativeAOT executable's hash and size. It contains no credential-file contents or command-line secrets. Copy that manifest next to the run's report before starting the executable. The report independently checks NativeAOT at runtime.

Final sanitized evidence is stored under `verification/` so reviewers do not depend on ignored working artifacts. Those reports retain all per-loop resource samples, tested thresholds, exact final judgments, protocol counters where observed, and explicit limitations. Historical failures are summarized separately and are never rewritten as passes.

## Owned official-server HLS validation

`--real-hls --credentials-file <local-json-file>` is a separate mode for the explicitly authorized disposable Emby Server 4.9.5.0 instance. It accepts only an HTTP loopback URL on port 19096 and requires server ID `cf4feb10df224135877fc61204a28212` before logging in. The local JSON contains `ServerUrl`, `Username`, and `Password`; values are read in-process and are never accepted as CLI arguments, printed, or saved in reports. Authentication must return the same server ID. Item `5` must have the expected 59-61 second duration.

The mode forces HLS transcoding for 20 native playback loops, alternating initial positions of zero and 17 seconds. It reads actual `MediaPlayer.PlaybackSession.Position` and the engine snapshot, requires a full-source timeline with zero offset, pauses and measures clock drift, seeks to 45 seconds, resumes, then stops and checks detachment. Successful API start/progress/stop order and per-session encoding cleanup are recorded using hashed play-session IDs. API response codes and source ticks are preserved, but raw media URLs, tokens, passwords, and credentials-file paths are absent.

A probe-only partial class observes whether the current native session owns the adaptive creation response after initial open and after logical seek. The report records only two booleans per loop. This observation verifies that a response-lifetime experiment exercised a non-null response; it neither exposes response content nor changes the functional or resource acceptance criteria.

Append `--native-http-control` to the real HLS command to run an isolated HTTP-construction control. Its `ExecutionMode` is `NativeHttpHlsControlNotProductAcceptance`, and its success status is `ControlPassed`, never a product pass. It preserves twenty complete coordinator cycles and forty initial/replacement native graphs, including SMTC and the same resource thresholds. A synchronous filter requires the identity-checked origin's exact loopback host and port before attaching in-memory authentication headers, then returns `HttpBaseProtocolFilter.SendRequestAsync` directly. It does not create a managed async-operation wrapper, buffer a response in managed memory, or manufacture a native HTTP response. Redirects, proxy use, credential UI, cookies, and HTTP caching are disabled. Counters cover filter creation/disposal and accepted/rejected requests; no completion callback is installed. Absence of new guard calls after stop is not packet-level proof that already-issued native requests have ended. This control is limited to the explicitly owned validation server and cannot accept arbitrary real servers.

Use `--native-http-shared-player-control` instead to preserve that native HTTP path while reusing one player for all forty source/session graphs. The report explicitly identifies `SharedPlayerNativeHttpHlsControlNotProductAcceptance`. Each session must have a unique playback ID and find an empty `Player.Source` before binding; open and logical seek must bind a source and reach the actual expected native position. Every stop must clear the source and detach the element. All source, event, SMTC, and HTTP retirement still occurs per session. The probe closes its one shared player after coordinator disposal, including failure cleanup, and requires exactly one creation and one disposal. The unchanged resource gate applies to the same twenty complete cycles. Even a passing control would not establish concurrent cancellation, fallback, stale-callback isolation, or a safe product reuse design.

The API observer does not intercept native HLS segment requests. `NoApiRequestsAfterStop` therefore makes an API-only assertion; packet-level media quiescence, displayed frame colors/subtitle pixels, audible output, and other servers/formats remain outside the result. The original 20-loop resource thresholds and 30-second final idle sampling are unchanged. Never run this mode against a server that has not been explicitly authorized and identity-checked.

The optional `--no-inflight-gc` argument runs a separately identified causality experiment over the same 20-loop product lifecycle. It requests a fixed 64 MiB no-GC budget immediately before each open and ends the region immediately after stop and explicit read-operation cleanup. Normal post-stop GC/resource measurement follows. Every loop records successful region entry and exit. An unavailable, exceeded, or interrupted budget fails instrumentation explicitly. Reports set `ExecutionMode=NoInFlightGcExperiment` and use a distinct success status, so this experiment can never be substituted for a normal-runtime regression pass. The same resource thresholds and 30-second final idle observation remain in force.

## NativeAOT entry point

The project uses the Windows App SDK XAML compiler's generated STA entry point: initialize C#/WinRT `ComWrappers`, call `Application.Start`, install `DispatcherQueueSynchronizationContext`, and create `App`. It intentionally does not add a separate competing `Main` implementation. Microsoft documents [Windows App SDK NativeAOT and partial-class requirements](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6#native-aot-support) and the [NativeAOT toolchain prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). The repository's current SDK-generated `App.g.i.cs` was inspected when creating this tool. The application checks `RuntimeFeature.IsDynamicCodeSupported` and rejects a JIT run.

## Isolated resource controls

Optional controls investigate a failed lifecycle resource check. They produce a distinct `ControlReport`, never a passing product lifecycle result. Each control starts with ten seconds idle for independent handle-type observation, captures a baseline, performs 20 cycles with a 1.3-second disposal observation, then captures samples at 2, 10, and 30 seconds idle. They apply the same early/late five-cycle medians, 64 MiB/32 handle limits, and final eight-sample sustained-growth rules as the lifecycle probe. A control's completion does not override any failed lifecycle report.

Append one of these arguments after the normal `--output-dir <new-directory>` arguments:

- `--control media-player`: only create and dispose `Windows.Media.Playback.MediaPlayer`. No source, transport, element binding, or property configuration.
- `--control media-player-configured`: create and dispose the player with the product's initial `AutoPlay=false`, `IsMuted=true`, `Volume=1`, and `CommandManager.IsEnabled=false` configuration. No source, transport, or element binding.
- `--control http-range`: connect only to the same verified synthetic fixture and use the linked product `ScopedMediaTransport` and `HttpRangeStream` to read the beginning, middle, and end of an authenticated original stream. Require successful partial responses and no new range requests after disposal. No `MediaPlayer`, `MediaSource`, WinRT stream bridge, or playback report is created.
- `--control file-playback --media-dir <synthetic-directory>`: require the generated 60-second MP4 and its synthetic metadata, then run actual native open/play/pause/seek/resume/stop using `MediaSource.CreateFromStorageFile`. Match the product's player configuration, `MediaPlaybackItem`, element binding, and corresponding native event subscriptions, including explicit detachment and disposal. The separate harness uses no HTTP, custom WinRT random-access stream, or playback coordinator. Native clock/video dimensions are asserted; displayed pixels are not captured.
- `--control file-stream-playback --media-dir <synthetic-directory>`: reuse the exact `file-playback` control cycle and change only source creation to `MediaSource.CreateFromStream` over `StorageFile.OpenReadAsync`. The Windows-native random-access stream is disposed after the source and player. This separates the platform's stream-source behavior from the product's managed HTTP/WinRT stream bridge.
- `--control file-managed-stream-playback --media-dir <synthetic-directory>`: reuse the file cycle over an explicit asynchronous `System.IO.FileStream`, Microsoft's `AsRandomAccessStream` adapter, and `MediaSource.CreateFromStream`. It intentionally does not use `StorageFile.OpenStreamForReadAsync`, which may unwrap to a Windows-native stream. This separates Microsoft's generic managed adapter from the product's custom cloneable HTTP bridge.
- `--control native-http-playback`: verify the same synthetic loopback fixture, log in with its fixed development account, negotiate each source in-process, and attach the synthetic fixture token to the native URI only in memory. Use `MediaSource.CreateFromUri`, `MediaPlaybackItem`, and the same native cycle. URI credentials are never accepted on the command line or written to reports/application logs. Record native HTTP request and partial-response counts, and require no new requests after retirement. The source has no product managed stream bridge; the control does not certify production relay security or real Emby compatibility.

For example:

```powershell
$controlDirectory = Join-Path (Get-Location) ('artifacts/probes/control-player-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Process -FilePath (Join-Path (Get-Location) 'artifacts/probes/native-probe-publish/EmbyClient.NativeProbe.exe') -ArgumentList @('--output-dir', $controlDirectory, '--control', 'media-player') -WindowStyle Hidden -PassThru
```

The two player controls never connect to a server or load media. The range control repeats the synthetic header, server identity, account, item, and duration checks before requesting bytes. Its report records actual media-request and partial-response totals. Each invocation requires its own output directory and has a three-minute deadline. The report explicitly states which properties were configured and which components were absent.
