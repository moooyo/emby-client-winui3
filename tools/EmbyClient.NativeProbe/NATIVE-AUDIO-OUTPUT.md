# Native audio output probe

This QA mode links the real product `NativePlaybackEngine`, `NativeAudioInterop`
and `PlaybackCoordinator`. It reuses the owned ephemeral loopback media origin
used by the native HLS/audio selection probes. A measured synthetic source is
served byte-for-byte; there is no second playback engine.

The fixture manifest must explicitly declare `Synthetic=true` and `QaOnly=true`,
identify the pinned 60-second H.264/AAC input, and contain measured AC3, EAC3 and
AAC metadata and SHA256 hashes. The probe checks every media hash before opening
the origin. AC3 and EAC3 are QA conversion fixtures; no user media is analyzed.

Run only after other playback windows have closed:

```powershell
& 'D:\absolute\publish\EmbyClient.NativeProbe.exe' --output-dir 'D:\absolute\new-run' --native-audio-output --media-dir 'D:\absolute\qa-fixtures'
```

The result distinguishes policy-off PCM, unsupported AAC PCM, real native AC3
and EAC3 output attempts, and actual accepted native carrier frames. Pause,
paused seek, resume, stop, backend retirement and origin retirement are checked.
If a native carrier remains active, software volume exercises the product PCM
backend replacement and checks that its logical playback ID and single synthetic
server session remain intact. An unsupported physical endpoint is a measured
fallback result, never evidence of bitstream receiver output.

The in-process QA API handler records product coordinator negotiation and
playback-report calls. Its receipts are explicitly synthetic and independent of
the real native backend assignment count. The output retains native endpoint,
format, carrier/content parameters and submitted frames without writing tokens,
media URLs or real server information.

A passing result certifies these bounded control and fallback paths on the
current endpoint. It never certifies audible output, physical receiver decoding,
other codecs/endpoints, device hotplug or real Emby server behavior.

## Windows acceptance on 2026-10-13

The frozen `publish-02` NativeAOT build completed with exit code 0, no compiler
warnings, and 101 unchanged source inputs. `run-01` executed from 08:23:16.3639304
to 08:23:24.5677241 UTC. Its owned process, PID 44064, exited with code 0 without
forced termination; the process handle was pinned before waiting. Subsequent
checks confirmed process absence and unchanged source inputs.

The native runtime and published DLL had the same SHA256:

```text
7C33D10013829BA9D0BC69DF6EB51AF8BBA9BD4E60D7EF408BB0BC19EA6945D5
```

| Case | Actual native source codec | Observed output | Native format attempt | Carrier submitted |
| --- | --- | --- | --- | --- |
| AC3 / Off | `ac3` | Decoded PCM with passthrough disabled | No | No |
| AC3 / All | `ac3` | PCM after the native exclusive output attempt returned `FormatUnsupported` | Yes | No |
| EAC3 / All | `eac3` | PCM after the native exclusive output attempt returned `FormatUnsupported` | Yes | No |
| AAC / All | `aac` | PCM with `CodecUnsupported` | No | No |

The default endpoint observed by the AC3/EAC3 attempts was
`Speakers (BH900 PRO)`. Both attempts were real native output calls, and neither
established a successful exclusive output or submitted carrier frames. The AAC
control matched the exact pinned original media hash and the actual native
decoder source codec.

All four cases passed bounded PCM playback controls: pause drift was zero,
paused seek resolved to exactly 80,000,000 ticks, resume advanced the real
playback clock, and stop detached the player. Every case recorded one native
backend assignment, one synthetic negotiation, one synthetic Start report and
one synthetic Stop report. Engine/source retirement and listener retirement
completed, and upstream requests remained quiescent after stop.

The observed endpoint did not provide a successful bitstream case. Consequently,
this run did **not** cover successful carrier submission, carrier pause/resume
and synchronization, software-volume-triggered local PCM backend replacement,
or retirement after a successfully opened exclusive output. Physical receiver
decoding, DTS/DTS-HD/TrueHD, grouped muxer EOF behavior and device hotplug remain
unverified. A passing fallback run does not certify those paths.

## Source corrections after run-01

Subsequent source review confirmed and corrected two gaps:

- Buffered statistics could remain stale while paused because only core
  `Publish` refreshed the cache. The engine now subscribes to the native
  `BufferedRangesChanged` and `BufferingProgressChanged` events, coalesces them
  to at most one pending dispatcher refresh, checks the active session before
  reading, and unsubscribes on retirement. This refresh changes only the cache;
  it does not publish server progress or estimate buffered time.
- An independently failed PCM replacement could be mistaken for cancellation
  after the original backend had retired. `Fail` now faults `Started` and
  `PlaybackConfirmed` with the same `PlaybackException`. The recovery
  `OperationCanceledException` catch still retires the replacement, then
  propagates its recorded failure only when the recovery lifetime has not been
  cancelled and `PlaybackConfirmed` is faulted. This covers
  subtitle restoration and the active-session check before resuming playback;
  normal Stop, Dispose and owner cancellation retain their cancellation semantics.

These source corrections received an independent source-only review. The
`publish-02` hashes and `run-01` receipts above identify the earlier candidate;
that run did not execute these corrections and does not verify the newer
source. The independently published current candidate and `run-02` observations
are documented below. No successful carrier was available, so fault recovery
and cleanup after successful carrier output remain unverified on hardware.

Evidence:

- [Frozen build manifest](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/publish-02/build-manifest.json)
- [Actual execution receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/execution.json)
- [Four-case native observations](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/result.json)
- [Post-execution source and process checks](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/post-execution-verification.json)
- [Measured QA fixture manifest](../../artifacts/lumen-windows-continuation-20261013/native-audio-fixtures/native-audio-fixtures.json)

## Current candidate run-02

The new `publish-03` NativeAOT probe linked the frozen current managed playback
implementation with native05. Publication exited 0 with 104 unchanged
managed/project inputs. The native DLL SHA256 was
`D2DFD9236319F071A35A57E10CCAC00C8F577503B1FA1799B2CB3677B392D7E7`.

The independent audio execution ran from
`2026-10-13T10:01:47.4255672+00:00` through
`2026-10-13T10:01:55.5959653+00:00`. Owned PID 17432 had its process handle pinned,
exited normally with actual code 0, and was subsequently absent. All four cases
passed. AC3/EAC3 reached actual native exclusive format attempts against
`Speakers (BH900 PRO)` and received `FormatUnsupported`, then used PCM. AAC used
the `CodecUnsupported` PCM fallback, and AC3 Off retained PCM. No carrier was
submitted or exclusive output established.

Every case had zero paused clock drift, an exact paused seek to 80,000,000 ticks,
real clock advancement after resume, and successful Stop, native owner cleanup,
engine/origin disposal, and post-stop request quiescence. Each recorded one real
native backend assignment and separately one synthetic negotiation, Start and
Stop. After execution, all 104 managed/project inputs, 132 native05 source
inputs, three media files, media manifest and candidate binaries remained
unchanged.

The root explicitly released the audio slot while its exact owned app03 process
remained idle at Home and held all main player input. The isolated current-run
runner checked that ownership exception and unchanged live fixture counters.
It did not interact with any Windows UI. Full attribution, the bounded exception
and current results are retained in the
[run-02 acceptance receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md).

This current fallback/control execution does not inject a PCM replacement
failure, separately establish a paused buffered-event refresh, or replace the
main UI Stop regression. Successful carrier controls, software-volume PCM
replacement, cleanup after accepted exclusive output, and physical receiver
decoding remain unverified on this endpoint.
