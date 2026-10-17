# Lumen bitmap subtitle reselection and HUD fixes

Current authority: the [2026-10-17 code closeout](current-code-closeout-20261017.md).
Final16 is **BuildPassed**: Release/Debug, fresh Native AOT, package/runtime and
offline schema checks have actual exit 0. Its 30,613,504-byte EXE is
D72B6237...4651; inventories are 675/260/24/595. All 2,430 product cases and the
114-input NativeProbe compilation use matching complete prior compiled-closure
evidence, with no reruns for this UI-only delta.

**BoundedNavigationObserved** covers three short 005000/000001 End/Home rounds,
the next Right action and normal exit 0 after the Home anchor/scroller repair.
Formal06 is **OriginalFiniteUiWorkloadCovered_WithObservationLimit**: its finite
UI sequence, fixed idle windows and in-cap sign-out/normal exit are covered;
tail warm returned to the real first viewport without repeating its numeric
tooltip. The resource result is **InvestigateHighRetention / PerformanceNotAccepted**:
two increments exceed the 32 MiB investigation threshold and library-cycle
handles keep rising, with attribution unresolved. Final owned cleanup and
system restoration are complete.

The user renewed unattended continuation. The earlier physical-Escape stop and
goal-service blocked receipt are historical; no new goal-service status is
claimed. **LANDeferred** still covers discovery, public Save and account
restoration. Earlier candidates and all failed/aborted attempts retain their
own scope. CI, release, licensing/signing and installation/distribution remain
outside this acceptance scope. The user separately authorized committing and
pushing the current source snapshot to `main`; source publication does not change
the recorded acceptance results.

The remainder preserves the **2026-10-14 frozen checkpoint**: native-06, two
selected managed test projects, Debug/AOT06 and package readback passed.
Its 1,509 cases and Paused/Pending rows are historical, not the latest project
total or the current authorization state. The new ledger keeps the different
native binary identities and all original failures explicit.

This is a separate follow-up to the
[Windows continuation](lumen-windows-continuation.md). Its native-05/publish-05
pixels, 2,294 unit cases, distinct HTTP scopes and PCM fallback observations
retain their original candidate identities. They are not execution or visible
acceptance of the new native-06/publish-06 changes described here.

## Frozen change and candidate identity

The new evidence root is
`artifacts/lumen-subtitle-fixes-20261014/run-01`. The
[source freeze](../../artifacts/lumen-subtitle-fixes-20261014/run-01/freeze/source-freeze.ready.json)
records 668 production files and 254 test inputs, with an 812-file union;
the memberships overlap and must not be added as disjoint totals. This fix
changes 14 production inputs and adds 40 cases: 22 geometry cases and 18
coordinator cases. The frozen source is not changed by this documentation work.

- Native-06 DLL SHA256: `73539B234A073DE8534A5D309C03552B1634FEDB373E8B4EA35C563B2769694B`.
- Publish-06 App EXE SHA256: `FF75915C99EE5F14CAEB9F42B0267D27BB0A570F473CE0F139D5BAE2FE8193EE`.
- [Current self-contained publish-06 output](../../artifacts/lumen-subtitle-fixes-20261014/run-01/publish-06/).
- [Verification result](../../artifacts/lumen-subtitle-fixes-20261014/run-01/verification/verification-06.result.json)
  SHA256: `33A9CE4B16D6B4A119D9A6E21F560407EF7161FF5B3486D8F9C8C0CE6BF3738F`.

The old DLL, source inventories, publication directories and acceptance receipts
remain historical. Neither an earlier successful frame ABI gate nor the new
build/readback alone proves the new paused-reselection or HUD pixels.

## Preserve a mapped bitmap decoder across Off and reselection

The earlier PGS review recorded a paused Off-to-PGS transition inside an active
cue that stayed blank until a seek and fresh demux. The new source provides a
bounded control on the existing decoder rather than always retiring playback
for that subtitle change.

[PlaybackCoordinator](../../src/EmbyClient.Playback/PlaybackCoordinator.cs) first
tries `IPlaybackSubtitleSelectionEngine.TrySelectEmbeddedBitmapSubtitleAsync`.
A successful operation keeps the decoder, playback ID and existing server
PlaySessionId, updates the selected subtitle index and reports the track change
without a new negotiation or Start/Stop pair. The current playback position and
Playing/Paused state are retained.

[NativePlaybackEngine.NativeSubtitles](../../src/EmbyClient.App/Playback/NativePlaybackEngine.NativeSubtitles.cs)
accepts only Off (`-1`) or the same uniquely mapped embedded bitmap track of
the same opened source. The decoder, native session and original timed metadata
track must still match. The fast path requires actual Playing/Paused state,
the original full-source/static delivery and no simultaneous source, quality,
audio, external subtitle, burn-in or transcoding change. It invalidates pending
image presentation before changing the track mode, so an old upload cannot
reappear after Off or a newer selection.

The native bitmap provider retains the already decoded composition associated
with its existing `TimedMetadataTrack.Cues`. On reselection it finds the cue
covering the real playback-session clock and recreates its pixels from that
decoded composition when the prior image has been released. It does not depend
on a new paused `CueEntered` notification, seek, packet read, second decoder,
synthetic clock or fabricated cue timeline.

This is not a general track switch. A different track, an initially Off source
without an established mapping, an unsupported/ambiguous mapping or an older
runtime declines the fast path and retains the existing rebuild/fallback
workflow. Immediate restoration covers already decoded cues only. Unknown or
unread intervals are not reconstructed or filled with a guessed image.

## Feature version, lifetime and timing safety

The new `EmbyBitmapSubtitleSelectionApiVersion` export reports version 1.
[NativeAssInterop.Frames](../../src/EmbyClient.App/Playback/NativeAssInterop.Frames.cs)
requires that marker in addition to the existing frame bridge. A missing export,
unsupported version or unavailable older DLL returns `false` for the new
capability; the coordinator then uses its established fallback. It does not
assume the new behavior merely because an older DLL supports frame snapshots.
The frame API stays version 1, pack 8 and 96 bytes. The existing observation
layout remains 192 bytes. The recorded export count is 21 Emby C API names,
excluding unrelated WinRT factory exports.

The accompanying safety changes keep the decoded-subtitle map and cue access
under the provider's consistent lock, including packet insertion and timed-cue
addition, selection reads, callbacks and seek/flush retirement. Owned
`AVSubtitle` data is released with `avsubtitle_free` before its allocation is
freed on rejected, empty, failed or retired compositions. Snapshot generation
and selection updates invalidate stale image work rather than borrowing pixels
from another decoder or playback ID.

Cue matching applies the current requested delay once to the decoded source
timestamp. A negative shifted start may be clamped for presentation, but expiry
uses the original unclipped endpoint; clamping to zero must not extend the cue.
The coordinator links cancellation to the current playback lifetime, checks
ownership after awaited work and captures the latest confirmed position before
a declined operation falls back. A canceled or superseded control cannot commit
its selection to a replacement session.

## Keep bitmap glyphs clear of visible bottom controls

[AssSubtitleLayoutPolicy.AvoidBottomControls](../../src/EmbyClient.App/Playback/AssSubtitleLayoutPolicy.cs)
starts with the existing Uniform/SAR mapping of the authored crop. The
[native image presenter](../../src/EmbyClient.App/Views/PlayerView.NativeImages.cs)
measures the visible timeline/transport bounds in the actual video viewport's
DIPs. Only a non-source-styled bitmap that intersects those visible controls
is raised above their occupied rectangle with a 12 DIP clearance.

The whole image keeps its width, height, horizontal position and scale. The
video rectangle and native cue coordinates do not change. Source-styled ASS
retains its authored placement; a non-overlapping narrow letterboxed cue also
stays where it was. The policy is based on intersection, not a window-width
guess. If the available space cannot fit the unchanged image within the video,
the conservative result is the original placement rather than shrinking,
clipping or moving it outside the video.

Clearance remains through the existing HUD fade and returns to authored
placement when the controls have finished hiding. Resize, fullscreen/restore
and visibility changes recompute the current placement; this introduces no
additional subtitle decoder, media clock or per-animation layout loop.

## Recorded verification

The fresh
[verification result](../../artifacts/lumen-subtitle-fixes-20261014/run-01/verification/verification-06.result.json)
records actual exit 0 for its driver and all 11 child commands. Actual exits are
read from the retained launched process instances, not inferred from logs.

| Executed scope | Actual result | Boundary |
| --- | --- | --- |
| Native-06 compilation | Actual exit 0; new DLL identity above | New native source, not the historical native-05 DLL. |
| Native parsing, feature and ABI checks | Actual exit 0; 21 Emby exports, feature version 1, frame version 1/96 bytes and observation 192 bytes | Four original ASS parser cases, original SSA V4 parsing and the frame/feature parameter gates do not create a playback source or accept displayed bitmap pixels. |
| Platform Release project | 1,091 passed, 0 failed, 0 skipped; actual exit 0 | Includes the new geometry coverage. |
| Playback Release project | 418 passed, 0 failed, 0 skipped; actual exit 0 | Includes the new coordinator coverage; controlled test engines are not actual native cue/runtime acceptance. |
| Normal App Debug and Native AOT06 | Both actual exit 0 | Compilation and generated XAML, not UI/runtime acceptance. |
| Scoped SBOM, ASS closure and independent publication readback | Actual exit 0; 53 components, 54 dependency nodes and 595 payload files | Package/identity/structure checks, not presentation, receiver or display acceptance. |

The two freshly executed managed projects total **1,509** cases. This is not a
full-project rerun, a forecast obtained by adding 40 to 2,294, or a claim that
other historical test projects ran on this freeze. Native/ABI cases remain
separate from that managed total. The
[Platform execution](../../artifacts/lumen-subtitle-fixes-20261014/run-01/verification/embyclient.platform.tests-release-06.execution.json)
and [Playback execution](../../artifacts/lumen-subtitle-fixes-20261014/run-01/verification/embyclient.playback.tests-release-06.execution.json)
retain the corresponding actual process results.

<a id="paused-runtime-acceptance-and-final-resource-state"></a>

## Historical paused runtime acceptance and final resource state

The fresh owned App, PID 45292, reached Home only. No playback or PGS/ASS/HUD
fix pixels have been accepted for this candidate. The user explicitly paused
computer use; a launch, build, export, ABI gate, decoded-cue counter or unit
result cannot fill these runtime gates.

The [final paused-status receipt](../../artifacts/lumen-acceptance/subtitle-fixes-20261014/PAUSED-FINAL-STATUS-20261014.json)
supersedes the earlier pre-poll process state. The exact retained-handle
[app exit receipt](../../artifacts/lumen-acceptance/subtitle-fixes-20261014/app-01-pgs/app-exit-final.json)
records matched identity, WaitResult 0 and actual exit 0 at
`2026-10-14T12:33:52.2765530Z`; the process is absent. Exit cause remains
`Unknown`: root performed no normal Close input, so this is not accepted normal
UI closure.

Both fresh fixtures completed canonical cleanup with actual command exit 0:
[environment-01-pgs](../../artifacts/lumen-acceptance/subtitle-fixes-20261014/environment-01-pgs/cleanup.json)
and [environment-02-ass](../../artifacts/lumen-acceptance/subtitle-fixes-20261014/environment-02-ass/cleanup.json).
Their exact PIDs are absent, ports 18985/18986 have no listeners and receipt
run identities match. Fixture process exit codes themselves were not observed.
Source, candidate, media and synthetic-profile evidence is retained.

The [initial capture exclusion](../../artifacts/lumen-acceptance/subtitle-fixes-20261014/app-01-pgs/native/initial-capture-excluded.json)
records the excluded initial raster. It is absent and is not product evidence.
Its earlier filesystem cleanup had already taken effect before the SDK stop;
the later shell deletion command was rejected by policy before execution and
was not retried. No rereading or regeneration of that raster is used for
acceptance. The earlier UI-pause receipt is a historical pre-exit-poll checkpoint,
not a replacement for the actual final process result.

| Historical 2026-10-14 acceptance scope | Status at that checkpoint | Required observed boundary |
| --- | --- | --- |
| Paused bitmap Off/reselection | **Paused/Pending** | Same mapped source/track must visibly clear on Off and restore the actual overlapping decoded cue at unchanged paused clock, without a new decoder/playback ID/server session. |
| Bitmap HUD intersection and hide/fade | **Paused/Pending** | Actual wide/fullscreen overlap must clear with unchanged image size; non-overlapping narrow and source-styled ASS placement, HUD hide/fade and restoration need their own saved pixels. |
| Related playback/runtime regressions | **Paused/Pending** | No new playback, resume, seek or expiry acceptance is inferred from implementation/test doubles; broader track/format combinations remain outside the verified scope. |
| Owned App process exit | Actual exit 0; cause Unknown | The same identity-matched retained handle records WaitResult 0. This is not normal UI-close acceptance because no root Close input was observed. |
| Fresh fixture resource cleanup | Completed within recorded resource scope | Canonical cleanup commands exited 0; exact PIDs are absent and ports released with evidence retained. Fixture process exit codes are not inferred. |

The prior native-05 PCM policy/rejection cases remain historical; successful
receiver/carrier output, bitstream-to-PCM software-volume replacement and
physical HDR were not established by this checkpoint. The broader renderer,
font/SAR/script, real-server and device boundaries remain explicit. The table
preserves its original state; later observations and owned-resource receipts
are recorded in the current closeout instead of rewriting historical rows.
