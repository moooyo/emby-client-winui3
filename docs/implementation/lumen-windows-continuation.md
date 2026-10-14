# Lumen Windows continuation

Updated: 2026-10-14. **Local continuation and owned cleanup completed within
the recorded scope; hardware, renderer and untested combination limits retained.**

This is the completed publish-05 checkpoint. The later
[bitmap subtitle fixes](lumen-subtitle-fixes.md) describe the current
native-06/publish-06 source and successful build/1,509-case component verification.
Their UI/runtime acceptance is deferred while computer use is paused; resource
cleanup and actual App exit 0 do not establish those new fix pixels. The results
below retain their publish-05 and earlier candidate identities.

This record continues the Emby API support and native playback work after the
user explicitly authorized local Windows compilation, tests, UI/UX acceptance
and computer use. The historical remote-only receipts remain evidence for
their original source snapshots; their earlier authorization limits are not
the execution limits of this continuation.

The implementation remains native WinUI 3, C#, XAML and the pinned native
renderer. This record reports observed results only. A successful build, ABI
check or subtitle import does not establish visible rendering, receiver output
or physical HDR acceptance. Execution uses task-owned synthetic profiles;
ordinary saved accounts are not used. Only the acceptance profile's explicitly
selected subtitle/audio preferences were inspected. No PR or issue write is
part of this continuation.

## Recorded publish-05 candidate

The final candidate for this checkpoint is
[publish-05](../../artifacts/lumen-windows-continuation-20261013/publish-05/).
The [Copy feedback build and readback report](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
records actual exit code 0 for normal Debug/generated XAML, Release Native AOT,
SBOM generation, the complete ASS package assertion and independent published
payload/generated-XAML readback. The latter verifies identity and SBOM structure,
not CycloneDX schema compliance or visible subtitle pixels.

The complete 668-file production inventory has no additions or removals and
exactly one changed file relative to publish-04:
`src/EmbyClient.App/Views/PlayerView.Information.cs`. Its two Copy feedback
success/failure calls now use the existing `ShowPlayerToast` with raw message
keys, retaining the existing single localization lookup. Clipboard creation,
options and copied text are unchanged. The app-04 review's Copy OSD overlap
with the narrow information-panel Close area remains historical evidence;
the targeted app-06 recheck below accepts the corrected bottom success feedback
on this new binary without relabeling the prior full subtitle matrix.

All 24 native runtime names, actual lengths and hashes, native source fingerprint
and native/libass build-receipt hashes match the prior freeze. The 668/24 sets
were stable during the recorded builds and readback. The final
[source/runtime readback](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/app06-source-runtime-readback.json)
also matches the complete current sets against the publish-05 post-build
baseline, with no differences and both build receipts stable. Its fresh
[full manifest](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/production-inputs-app06-final.json)
SHA256 is `A085FC4FAC8BCB5205B3037AA99E9000AF9959D850B0B90C95422142D8B67886`.
This comparison includes the authorized Copy delta and does not claim current
source identity with publish-04 or overwrite a legacy manifest.

- App EXE SHA256: `0DE5DCC02368C59DE445B673BD7402DCDC868A7A478199F7AAD7CA5CD19D92C7`.
- `FFmpegInteropX.dll` SHA256: `D2DFD9236319F071A35A57E10CCAC00C8F577503B1FA1799B2CB3677B392D7E7`.
- SBOM SHA256: `42A899F53907C5FA64329860691CDEED4D68813987E78C3C98C39CF8E5DB8D07`.
- [Production delta from publish-04](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/production-delta-from-publish-04.json).
- [Verified build/readback result](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/publish-05.verified-result.json).
- [Current published payload readback](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/publish-05-readback.json).

The initial driver actually exited 1 after both builds had already exited 0.
Its `Get-Command pwsh` result supplied two application paths, forming an invalid
SBOM launcher filename. That original failure and logs are preserved. The
post-build recovery used the current host's exact executable path and ran only
SBOM generation, ASS assertion and readback; those child commands and its driver
actually exited 0. Compilation was not repeated. The historical AOT03
build-process exit remains `Unknown`.

No unit, HTTP, native ABI, audio or playback suite was rerun for this two-call
feedback delta. The **2,294 unit cases** and **264 HTTP assertions** below remain
their earlier source/binary scopes, not new publish-05 counts. The four newly
parsed artifact helper scripts are separate from the historical 16-file Parser
scope. The observed app-04/app-05 subtitle/UI results belong to publish-04 and
support this narrowly bounded later change; they are not a complete new-candidate
walkthrough. The targeted app-06 Copy UI, final readbacks and owned cleanup
receipts below complete this local continuation within their measured scope.

## Historical publish-04 frozen verification

The `publish-04` candidate was copied without modification to
`app-03/candidate`; later app-04/app-05 runs used the same frozen App/native
payload. Its actual command receipts report normal Debug/XAML,
Native AOT and ASS runtime package checks with exit code 0:
[publication executions](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/publish-04.execution.json).
The [source stability receipt](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/production-source-stability-02.json)
records 668 unchanged product/build inputs, 24 unchanged native runtime files
and unchanged native/libass build receipts. These counts describe this new
manifest's scope and do not replace the earlier 674-input inventories.
The [resume readback](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/resume-source-stability-20261014.json)
and [post-UI readback](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/ui-pause-source-stability-20261014.json)
also matched the complete recorded source/runtime file sets. The latter
retained both build-receipt hashes. No production source changed during those
recorded app-03 resume/pause readbacks. The later Information.cs feedback delta
belongs to the separate publish-05 source interval above.

- App SHA256: `9CF7027E5E04518BF3DE39D488BBB80B761A2DD11B10C8C1F5AFBC803FC3F621`.
- `FFmpegInteropX.dll` SHA256: `D2DFD9236319F071A35A57E10CCAC00C8F577503B1FA1799B2CB3677B392D7E7`.
- [Frozen copied application inventory](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/app-inputs.json).
- [Native-05 build result](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/native-05.result.json): exit 0, all 132 native inputs stable.
- [Package/SBOM/generated-XAML readback](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/publish-04-readback.json): 53 components, 24 runtime payload hashes and the generated image-overlay fields checked.

The publish-04 managed executions passed **2,294 unit cases**, with no failures or
skips: API 376, AppState 222, MediaTransport 183, Platform 1,069 and Playback
400 give 2,250 product tests; the separate LumenAcceptance project adds 44.
The 47 added cases cover Replay preferences (9) and image geometry (38).
All 253 recorded test inputs remained unchanged during execution. The 16
requested PowerShell files also passed parsing. See the
[actual managed executions](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/managed-tests-02.execution.json).

HTTP evidence remains separate: 233 earlier assertions plus
[31 independently executed embedded-PGS delivery checks](../../artifacts/lumen-windows-continuation-20261013/pgs-fixture-verification.json)
give **264 HTTP assertions**. Their binaries and execution scopes differ;
this is not a single rerun against the current client, and the PGS checks do
not establish visible PGS pixels.

Native-05 retains the four original libass parsing cases and additionally
accepts the original SSA V4 document with two events. Six real P/Invoke checks
confirm the new version-1, pack-8, 96-byte frame API size/null-source gates.
The older observation ABI remains 192 bytes. This
[ABI receipt](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/native-frame-abi-05.json)
creates neither a playback source nor a bitmap and cannot substitute for
desktop image acceptance.

## Native audio probe run-02 and retained output limits

The Native AOT probe used native-05 and its own 104 frozen managed inputs.
Its [execution receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/execution.json)
records owned PID 17432, a retained process handle, actual exit code 0 and no
forced termination. All four cases passed. The
[post-execution receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/post-execution-verification.json)
confirms process absence, stable managed/native/media inputs and cleanup for
each engine and listener.

| Case | Observed current output | Real backend control scope |
| --- | --- | --- |
| `AC3_Off` | PCM; passthrough policy Off | Stationary pause, exact paused seek to 8 seconds, advancing resume and stop cleanup. |
| `AC3_All` | Actual native exclusive-format attempt; `Speakers (BH900 PRO)` rejected it; PCM with `FormatUnsupported` | The same controls passed on the actual PCM backend. |
| `EAC3_All` | Actual native exclusive-format attempt; the endpoint rejected it; PCM with `FormatUnsupported` | The same controls passed on the actual PCM backend. |
| `AAC_All` | PCM with `CodecUnsupported`; no carrier attempted | The same controls passed on the actual PCM backend. |

Every case recorded one backend assignment, one synthetic negotiation, one
Start and one Stop. Pause drift was zero. Player detachment, engine disposal,
listener closure and no upstream requests after stop were observed. The root
application stayed idle at Home throughout; its fixture counters remained
Start 5 / Stop 5 / PlaybackInfo 5 before and after the isolated probe.

Carrier submission remained zero, so successful receiver decoding, active
exclusive-carrier pause/seek/resume, exclusive cleanup and software-volume
bitstream-to-PCM replacement were not exercised. These four native cases are
separate from the 2,294 unit cases, 264 HTTP assertions and older run-01.

## Current image presentation change

The exact deployed WinUI image-cue path places the image under a zero-size
clip. Its runtime DLL identity and pinned official source correspondence are
recorded in the [root-cause evidence](../../artifacts/lumen-windows-continuation-20261013/ass-presentation-root-cause.md).
The client now selects application presentation for native image subtitles
and displays independent read-only native bitmap snapshots after WinUI upload
completion. libass retains the full ASS/SSA scripts, styles and real video
clock; embedded PGS/VobSub retain the existing FFmpeg decoder.

The presenter bounds work to one upload and one replaceable latest pending
frame. Playback identity, presentation epoch, track, generation and sequence
reject old work. Off, seek, retirement and natural end clear the image.
Geometry follows the actual Uniform video region, letterboxing and source
pixel aspect ratio. Three native DAR calculations were corrected to
`width / height * SAR`; PGS positions restore the existing provider's vertical
padding once and retain subpixel coverage. No second decoder, OCR, subtitle
timeline reconstruction or substitute media clock is introduced.

Replay now retains the same item's explicit Local/Off/Server choice even when
the global initial default is None. The Ended view keeps its owned final
position and duration. Buffer notifications refresh the actual cached range
statistics through the dispatcher, and native failure completion preserves
the original exception through audio recovery. These are current source
changes; their runtime results are recorded per candidate below.

## Historical app-03 resume and pause checkpoint on 2026-10-14

This section retains the same-process app-03 observations and subsequent
computer-use pause. Later fresh app-04/app-05 runs extend its observed scope
below without rewriting its unknown exit result or earlier resource history.

After the user's manual handling and instruction to continue, the same owned
app-03 process and frozen payload were identified again. The resumed Home
captures show the security prompt absent. The
[independent saved-pixel review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/UI-REVIEW-resume-20261014.md)
inspected captures 01-23 and their paired JSON records. All reviewed images
are 1428 x 894 pixels, using the same owned 1280 x 720, 16:9 SDR sample.

| Observed scope | Current candidate result | Evidence and boundary |
| --- | --- | --- |
| Paused ASS styles and drawing | Observed pass | [04](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/04-ass-static-paused-43.jpg) shows the yellow top layer, green drawing and complete outlined bottom line at 43.2299975 seconds. The drawing matches the original script's uniform geometry for this viewport. |
| Time-dependent ASS states | Observed pass at sampled positions | 05-06 show Motion moving after actual paused seeks to 10 and 12 seconds; 07-08 show the white 100% to yellow 150% transform endpoints; 09-10 show the original karaoke word-color timing. These samples do not establish complete continuous interpolation. |
| Playback progression with ASS | Observed pass within captured interval | 11-12 show progression from Motion at 10 seconds to the yellow transform around 23 seconds. They do not capture two continuous playing frames of the same cue or establish every animation refresh. |
| Signed subtitle delay while paused | Observed pass | 15-17 retain media position 10 seconds while delay 0, +0.1 and -0.1 changes the Motion phase in the expected directions. This establishes immediate paused redraw for the sample, not all-format timing latency. |
| Original SSA V4 import and styles | Observed pass | The actual FilePicker imported `styled-renderer.ssa`; [20](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/20-ssa-v4-paused-10-full.jpg) shows both complete original-V4 text layers with no old ASS drawing or Motion. |
| ASS/SSA Off clearing | Observed pass | 18 and 21 show the selected Off state and cleared ASS/SSA images at the unchanged paused media position. |
| Same-item Replay selection | Observed pass for Local ASS and Off | 14 restores visible local ASS after Replay; 23 retains explicit Off after `PlaybackEndedPrimaryButton` Replay, without old subtitle pixels. Other items, restart and every Replay entry point remain outside this observation. |
| Natural Ended clock and status layer | Observed pass | [22](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/22-natural-ended-final-clock.jpg) and its UIA tree both retain 1:00 / 1:00, Timeline 60.0106458, and a readable single Ended card. |

UIA and raster collection are not atomic. Captures 11, 12, 13 and 19 retain
their state differences explicitly. In particular, the file named
`13-ass-paused-after-continuous-playback` is a Replay-start transition: its
raster shows Starting playback at 0:00 / 1:00 while the earlier UIA tree is
still Ended. It is excluded from paused-frame and final-clock acceptance.
The independent review preserves approximate geometry, panel occlusion,
tooltip presence and the precise per-image times without broadening their
scope. Font attachments, arbitrary scripts, other SAR values, fullscreen,
narrow-window and PiP subtitle geometry are not certified by these images.

The original obscured Home capture and
[three-turn blocker audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/blocker-audit-03.json)
remain historical obstruction evidence. No security-dialog action was taken
by the agent. The security prompt is no longer the current blocker. The
user subsequently instructed the agent to temporarily stop computer use;
no further desktop input or live screenshot collection was performed during
that pause. Existing-image review, source readback and documentation
continued without operating the desktop.

The [02:28 live-process checkpoint](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/non-ui-checkpoint-20261014.process-alive.json)
matched the exact owned app path/start ticks/hash and both fixture
path/start-time/hash/listener identities. By the later
[non-UI checkpoint](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/non-ui-checkpoint-20261014.json),
the original app-03 PID was absent. No app closure or termination was performed
by the agent during this pause. Retained-handle monitor session 8996 had
already exited 1 after wait result 258 (timeout), so
[app-03's exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/app-exit.json)
records an unknown actual exit code and cause. Process absence cannot
establish exit 0 or normal UI closure. The
[partial fixture delta](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/fixture-ui-pause-partial-delta-20261014.json)
is measured against the resume baseline and does not establish final session
cleanup. Start/Progress/Stop counts describe a bounded 200-event window, so
they are not subtracted as cumulative session totals. Retained events are
filtered by timestamp and cannot recover evicted history. The initial draft's
window-count subtraction and ordinal event selection are explicitly excluded
from accepted evidence. The saved code-only subtitle diagnostic slice supplements attribution;
render/upload counters do not replace the visible images above.

The [bounded session-stop audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/session-stop-audit-20261014.json)
retains the latest logical session's Stop at 21.5799945 seconds and found no
new media requests during its short post-absence interval. It does not certify
native disposal or complete session history. The
[crash audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/crash-audit-20261014.json)
found zero exact-executable-path matches in Application event IDs 1000 and
1026 during the owned interval. This limited event query cannot establish
normal exit or exclude all possible reports.

Both fixtures then passed ownership-only validation and were stopped by the
guarded acceptance tool. Their
[cleanup command receipts](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.execution.json)
record actual command exit 0. The
[cleanup readback](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.json)
confirms both fixture PIDs absent, ports 18985/18986 released and the original
app PID absent. Candidate files, media, source receipts and synthetic profiles
are retained. The PGS environment's
[pre-cleanup counters](../../artifacts/lumen-acceptance/windows-continuation-20261013/environment-02-pgs/pre-cleanup-20261014.json)
are zero for playback negotiation, Start and media requests; it was prepared
but no PGS client playback occurred in environment-02. At that checkpoint the
remaining desktop work required fresh owned environment/application run
directories from the same frozen publish-04 after the user resumed computer use,
with exact retained handles for normal closure. The later environment-03/app-04
and environment-04/app-05 results below are those fresh runs; they do not reuse
the old PID or evidence directory.

## app-04 ASS layout, BIF and Home acceptance

After the user resumed computer use, fresh owned app-04 used publish-04 in
`environment-03-ass` with its own process/run/profile and retained exit handle.
The [independent app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
accepts saved captures 01-15 within the following measured scope.

| Observed scope | Recorded result and boundary |
| --- | --- |
| ASS viewport mapping | Static layers and drawing are observed in wide, actual `2560 x 1440` fullscreen, restored and actual `780 x 894` ordinary narrow windows. The narrow view follows the letterboxed 16:9 video and has no old-size remnants. Other SAR values and PiP geometry remain outside these samples. |
| Resume and natural Ended | Real narrow playback advances between Motion and transform cue ranges. The final `1:00 / 1:00` state retains Timeline 60.0106458, a readable Ended card and no ASS image. The captures do not prove complete continuous same-cue interpolation or exact 58-59-second ASS expiry. |
| Narrow information display | The panel and long audio value fit. Buffer and Dropped frames explicitly display Unavailable, so no numerical buffered/drop measurement is accepted. Copy feedback is visible but its top OSD covers part of the panel Close region; clipboard contents were not read. This is the historical trigger for publish-05. |
| Current BIF strip | Twelve actual JPEG cells at 0, 5, ..., 55 seconds are complete in the `2560 x 1392` maximized page. Cover preview and the flat audio lane remain distinct from those real timestamps. This is fixture delivery, not arbitrary video-frame extraction. |
| Home action tooltips | The actual Play, Watch again and Continue S1:E3 tooltips match the named action states. Play has a visible keyboard focus outline and operator-recorded Return activation. Watch again/Continue tooltip captures do not prove every keyboard path or their actual activation. |
| Return and normal Close | The observed details/media-information return has no old image. The owned title-bar Close and strong retained-handle final receipt establish actual exit 0 for app-04, not for another run. |

The [app-04 final exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json)
matches PID 24428, start ticks, executable path/hash and the retained pre-close
identity, with WaitResult 0 and actual `ExitCodeObserved=true`, `ExitCode=0`.
The [post-close non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app04-final-non-ui-20261014/APP04-NON-UI-AUDIT.md)
matched 595/595 copied payload files. Its bounded Application 1000/1026 query
covers the owned lifetime plus 120 seconds and found no records; its allowed
task-log slice found no non-None playback error. These are bounded channel/log
observations, not universal absence of failures or physical-output acceptance.

The fixture snapshot contains 101 actual retained playback events within a
capacity of 200: one Start, 99 Progress and one Stop for the timestamp-matched
session. The Stop is at 60.0106458 seconds with Failed=false. Events are selected
by time and grouped by session identity; retained-window counts are not
subtracted as lifetime-counter deltas. This audit establishes the unchanged
frozen app-04 payload, not an unchanged 668-file source set after the later
Information.cs delta. The final root cleanup below has its own resource evidence,
separate from this run's UI, exit and log observations.

## app-05 native embedded PGS acceptance

Fresh app-05 used the same publish-04 executable/native payload in
`environment-04-pgs` and actual embedded subtitle stream index 2. The
[independent PGS review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
accepts existing captures 01-16 within the following limits. The actual authored
cue intervals are 2-8 and 35-45 seconds; planned 5/41-second checkpoints are not
substituted for the actual recorded observations.

| Observed scope | Recorded result and boundary |
| --- | --- |
| Real native PGS cues | Both PGS START CHECK and PGS BITMAP CHECK are visible during real demux/playback and remain visible in sampled paused frames. Stable samples are 7.7809791, 39.7500833 and 43.6070833 seconds. Render/upload/positive-alpha counters are not used as pixel substitutes. |
| Expiry and Off | The first cue is absent by 18.7904166 seconds; this does not measure its exact 8-second clearing instant. Off clears the active second-cue image. The second cue's 45-second expiry was not sampled. |
| Paused reselection and demux recovery | PGS is checked again at 39.7607499 seconds, but the already-overlapping bitmap does not return while paused. The actual seek to 34 seconds and resumed demux restore it, and 43.6070833 retains the recovered cue paused. This does not erase the immediate-reselection limitation or prove arbitrary overlapping-cue reconstruction. |
| Viewport mapping and HUD overlap | Actual `780 x 894` narrow and restored samples show the complete second cue clear of controls. Fullscreen `2560 x 1440` maps it proportionally. Wide/fullscreen timeline/controls cross some lower glyph pixels; those samples are not accepted as fully unobstructed text. This is control-layer overlap rather than a missing bitmap. |
| Back-to-Home clearing | Leaving the still-visible cue produces stable Home with no PGS overlay. The actual destination is Home; no PGS return-to-details or every Stop/Back route is inferred. |
| Normal owned Close | The title-bar Close and strong final retained-handle receipt establish actual exit 0 for this app-05 run. An immediate inventory still contained the window; instantaneous disappearance is not claimed. |

The [app-05 final exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/app-exit-final.json)
matches PID 32804, start ticks, executable path/hash and the same retained
handle through WaitResult 0, actual `ExitCodeObserved=true`, `ExitCode=0`.
The [final non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app05-final-non-ui-20261014/APP05-NON-UI-AUDIT.md)
matched 595/595 copied payload files and completed the bounded Application
1000/1026 query after the full 120-second reporting grace, with no records.
Its allowed task-log slice contains 42 playback diagnostic records with no
non-None error; log alpha counts remain telemetry rather than rendering proof.

The fixture snapshot contains 94 retained events within capacity 200: four
Start, 86 Progress and four Stop events, grouped by timestamp and four session
identities. Every session has a retained Start/Stop, with Stop Failed=false.
Absolute lifetime counters remain verbatim and are not assigned as app-specific
deltas; local playback IDs are not joined to Emby PlaySessionIds. This audit
preserves the publish-04 payload identity and does not certify later source
stability or infer fixture cleanup from app exit. Historical app-03 actual exit
and AOT03 build-process exit remain `Unknown`.

## app-06 publish-05 targeted regression and final readbacks

Fresh app-06 uses the exact publish-05 App EXE and unchanged native-05 runtime
with the task-owned environment-04 PGS fixture. The
[independent targeted review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts saved captures 01-08, all `780 x 894`, within the following scope.

| Observed scope | Recorded result and boundary |
| --- | --- |
| Basic native PGS on publish-05 | PGS START CHECK is complete and clear of HUD overlap during actual Playing at 6.2241041 seconds and `1x`. This is the first cue only, not a rerun of the second-cue/Off/reselection/seek/fullscreen matrix. |
| Paused information display | The panel and long values fit at actual paused position 14.559 seconds. Buffer and Dropped frames display Unavailable; no numerical duration/drop or physical output is inferred. |
| Copy success-feedback correction | The actual bottom feedback leaves the panel and Close X clear. Feedback later disappears without changing the panel, and the subsequent actual Close dismisses it. Clipboard contents, exact timer duration, Close interaction while feedback is present and the unavailable-feedback branch were not exercised. |
| Back route | The actual result is detail with visible 24% Resume and no old player/PGS image. This is not Home or a complete server progress/persistence contract. |
| Normal owned Close | The Close input and exact retained-handle final receipt establish actual exit 0. The immediate 117ms window inventory still listed the window; instantaneous disappearance is not claimed. |

The [app-06 final exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/app-exit-final.json)
matches PID 9624, its creation time, path/hash and the same retained pre-close
handle through WaitResult 0 and actual exit 0 at
`2026-10-14T04:46:22.7528096Z`. Later independent process absence supplements
that actual exit receipt and does not supply or infer its code.

The [final non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
and [machine-readable final](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/app06-non-ui-final.json)
retain actual audit/source-readback command exit 0. The copied app-06 candidate
and current publish-05 directory independently match all 595 payload files.
The complete current 668-source/24-runtime manifest matches the publish-05
baseline as linked above; file readback does not measure loaded native memory.

The initial independent current-publish readback is preserved as
[Failed](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/publish05-readback.json)
with 593 files hashed. Its conservative privacy guard refused only
`Assets/Lumen/Icons/d/settings_20_regular.svg` and
`Assets/Lumen/Icons/w/settings_20_regular.svg`, recording no content hash
mismatch. With permission limited to those two manifest-authorized static icons,
[final readback 02](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/publish05-readback-02.json)
records 595/595 Passed. Other profile/settings/credential protections remain;
compilation was not repeated and the original failed record was not overwritten.

The Application 1000/1026 audit covers the owned lifetime plus the full
120-second post-exit tail and was queried after the entire interval elapsed.
It has zero bounded channel, exact-path and name-only unattributed records.
The allowed task-log slice has ten playback diagnostics with no non-None error.
This remains a bounded channel/log observation, not a universal failure or
receiver/display claim; positive-alpha telemetry is not pixel evidence.

The raw fixture snapshot has 115 retained playback events within capacity 200.
Timestamp/session selection excludes 94 earlier events and selects app-06's
21 events: one Start, 19 Progress and one Stop Failed=false for its actual
PlaySessionId. No ordinal selection or lifetime-counter subtraction is used,
and local diagnostic IDs are not joined to Emby PlaySessionIds. The prior
publish-04 ASS/SSA/PGS samples and their renderer limits retain their attribution.

## Final owned resource cleanup

The [final cleanup receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical ownership validation/cleanup commands with actual exit 0 for
both fresh fixture environments. Independent checks found their exact recorded
PIDs absent and no listeners on the owned ports.

| Resource | Final recorded result |
| --- | --- |
| environment-03-ass | PID 33768 absent; port 18985 released; canonical cleanup command actual exit 0. |
| environment-04-pgs | PID 48180 absent; port 18986 released; canonical cleanup command actual exit 0. |
| app-04, app-05 and app-06 | All exact owned PIDs absent; each has its own matching retained-handle actual exit 0 and normal Close record. |

The fixture process exit codes themselves were not observed; the command
exit codes and independent resource absence are the accepted cleanup evidence.
Candidate, source, media, receipt and synthetic-profile artifacts are retained.
The earlier environments 01/02 cleanup remains a separate historical checkpoint.

The collector's inherited pipes released naturally after canonical fixture
cleanup. Its [completion receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/fresh-environments-03-04-3f1a2991/outer-collector-completion-20261014.json)
records outer PID 10704 actual exit 1 from its deferred post-start owner-wrapper
guard; original launcher PID 26468 actual exit 0 is unchanged. Both are absent.
No collector stop signal was sent. The earlier rejected termination attempt
and policy remain recorded, rather than being bypassed or retried. The initial
SBOM-driver failure and privacy-refused readback also keep their original
results; not every helper exited 0. No recorded owned active resource remains.

## Earlier evidence roots and source identity

- [Windows build and test artifacts](../../artifacts/lumen-windows-continuation-20261013/).
- [Owned Windows acceptance environment and application artifacts](../../artifacts/lumen-acceptance/windows-continuation-20261013/).
- [Final test results](../../artifacts/lumen-windows-continuation-20261013/tests/RESULTS.md),
  [machine-readable summary](../../artifacts/lumen-windows-continuation-20261013/tests/test-summary.json)
  and [actual command executions](../../artifacts/lumen-windows-continuation-20261013/tests/final-verification.execution.json).
- [AOT-02 production input inventory](../../artifacts/lumen-windows-continuation-20261013/production-inputs-aot-02.json)
  and [source stability receipt](../../artifacts/lumen-windows-continuation-20261013/source-stability-aot-02.json).
- [app-01 copied candidate inventory](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/app-inputs.json).
- [Independent native audio probe execution](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/execution.json)
  and [measured results](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/result.json).

AOT-02's 674 recorded production inputs remained unchanged during its accepted
build interval. The app-01 executable SHA256 is
`F44679FF5F1EEF45FD941E1CCE42B747DAEB2F567C1FC6146AD4FF67F3BB68CD`.
Its copied `FFmpegInteropX.dll` SHA256 is
`F32EEC535CF5BB3622638A0602153A8B91F2B61B37E774E5FC5A0154DB2FECF4`.
Later ASS and presentation fixes require a new candidate and their own source
identity. The app-01 observations do not certify those later sources.

The new [AOT-03 source stability receipt](../../artifacts/lumen-windows-continuation-20261013/source-stability-aot-03.json)
records 674 unchanged inputs, executable SHA256
`46A54FF6DC14094CBE0F4236BF331548E4A65DFEFB351A5180C7DD192A6F5842`
and native DLL SHA256
`7C33D10013829BA9D0BC69DF6EB51AF8BBA9BD4E60D7EF408BB0BC19EA6945D5`.
An independent AOT-03 build-process exit receipt was not recovered from the
retained build/publication receipt scope. Its build exit therefore remains
unknown; completion text and stable hashes cannot establish exit 0. app-02's
separate retained application handle did establish application exit 0, but
that is not the build-process result. AOT-04 has its own actual exit-0 receipt.

## Earlier completed Windows tests and parsing

The earlier complete `scripts/Test.ps1 -Configuration Release` execution exited
with code 0 and passed all 2,203 product cases. The separate acceptance-tool
test project passed 44 cases and exited with code 0. There were zero failed or
skipped cases in these final runs, for **2,247 passing unit-test cases** in
total. Their before/after manifests contain 251
relevant test, linked product and configuration inputs, with no changes during
execution. Full application sources omitted from the test projects and native
presentation remain outside that unit-test scope.

| Executed scope | Passed | Failed | Skipped | Exit code |
| --- | ---: | ---: | ---: | ---: |
| API | 376 | 0 | 0 | 0 |
| AppState | 222 | 0 | 0 | 0 |
| MediaTransport | 183 | 0 | 0 | 0 |
| Platform, including the Windows-required cases | 1,022 | 0 | 0 | 0 |
| Playback | 400 | 0 | 0 | 0 |
| Product total | **2,203** | **0** | **0** | **0** |
| LumenAcceptance tool tests | 44 | 0 | 0 | 0 |
| Combined product and acceptance unit tests | **2,247** | **0** | **0** | **0** |
| PowerShell Parser, 16 requested scripts | 16 | 0 | 0 | 0 |

The HTTP fixture checks total 233 passing assertions: 131 default boundaries,
38 design catalog, 23 external subtitle contracts and 41 new BIF contracts.
The first three scopes retain their earlier binaries and execution context;
they were not rerun after the BIF fixture changes. The BIF result has its own
[verification receipt](../../artifacts/lumen-windows-continuation-20261013/bif-fixture-verification.json)
and [raw log](../../artifacts/lumen-windows-continuation-20261013/bif-fixture-boundary.log).
These HTTP assertions establish server contracts, not Windows image decoding.

Original failures remain visible. The first product script stopped after the
Platform round-trip test expected 30 preference properties and observed 31;
the test was corrected to cover the added `AudioPassthrough` property and its
non-default value. The original log is retained alongside the final passing
run. The initial parser receipt retains the two `Package-Release.ps1:168`
errors. Moving `-or` to the preceding line restored valid PowerShell
continuation; the final parser run passed all 16 scripts. The test evidence
also retains non-blocking analyzer/compiler warnings.

## Earlier Windows build ledger

| Attempt or scope | Recorded result | Evidence and boundary |
| --- | --- | --- |
| native-01 | Failed; retained | [Original log](../../artifacts/lumen-windows-continuation-20261013/build/native-01.log) records ambiguous `IUnknown` errors in the WASAPI headers. |
| PCH correction and native-02 | Passed, exit 0 | [Native rebuild log](../../artifacts/lumen-windows-continuation-20261013/build/native-02.log). The Windows header/import ordering correction resolves the original compiler failure. |
| app-debug-01 | Passed, exit 0 | [Normal WinUI build log](../../artifacts/lumen-windows-continuation-20261013/build/app-debug-01.log), including current generated XAML. |
| app-aot-01 | Passed, exit 0 | [AOT log](../../artifacts/lumen-windows-continuation-20261013/build/app-aot-01.log). |
| app-aot-02 | Passed, exit 0 | [AOT log](../../artifacts/lumen-windows-continuation-20261013/build/app-aot-02.log) and the separate 674-input stability receipt identify the app-01 candidate. |
| Native ABI and libass parsing | Passed within the recorded scope | [Receipt](../../artifacts/lumen-windows-continuation-20261013/build/native-abi-01.json) confirms versioned subtitle/audio APIs and four parsing cases, including six imported events for the styled document. It explicitly excludes image/audio acceptance. |
| native-03 after the later ASS fixes | Passed, exit 0 | [Rebuild log](../../artifacts/lumen-windows-continuation-20261013/build/native-03.log). ASS pixels still failed in app-02. |
| app-aot-03 | **Historical build-process exit unknown** | [Retained log](../../artifacts/lumen-windows-continuation-20261013/build/app-aot-03.log), [input inventory](../../artifacts/lumen-windows-continuation-20261013/production-inputs-aot-03.json) and its source stability receipt. Log completion text alone does not establish the process exit code. |

## app-01 native observations

The owned app-01 candidate was exercised on the local Windows desktop against
the isolated fixture environment. The native capture sequence 01 through 12
records the Home, settings, media information and player observations. These
observations apply only to the executable and native DLL identified above.

| Observed surface | Result | Evidence and limit |
| --- | --- | --- |
| Home and settings | Observed in the native application | [Home capture](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/01-home.json) and the retained capture sequence. This is not complete acceptance of every system integration or setting side effect. |
| BIF thumbnail strip | Twelve actual JPEG previews displayed at 0, 5, 10, ..., 55 seconds | [Media timeline capture](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/07-media-bif-timeline.json). The images come from the supplied fixture BIF package; this does not guarantee thumbnail availability for every target server or media version. |
| Native video and pause | D3D11VA reported; pause observed at 33.009995 seconds | [Paused player capture](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/08-player-paused.json). No physical HDR or presentation dropped-frame result is inferred. |
| Audio for the AAC fixture | Actual decoded PCM; passthrough fallback reason `CodecUnsupported` | [PCM diagnostics capture](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/09-player-pcm-diagnostics.json). This does not establish AC3/EAC3/DTS/TrueHD bitstream or receiver acceptance. |
| Styled local ASS | Import reached six events, but no subtitle pixels were visible at the paused 33-second observation or the playing 43.938-second observation | [Paused import](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/10-ass-paused-import-initial.json), [playing observation](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/11-ass-playing-initial.json) and [final initial state](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/native/12-ass-initial-final-state.json). **ASS rendering is not accepted for app-01.** |

The subsequent sources corrected ASS image ownership, late-cue presentation
and renderer-mode handling, and added code-only observation records. They also
corrected Copy localization, the Home tooltip and end-card overlap. native-03
compiled, and app-02 was then exercised. Its remaining image failure led to
the current WinUI clip correction; app-02 cannot certify that later change.

## Historical app-02 observations

app-02 used `publish-03`, with the executable and native identities listed
above. Its [independent saved-pixel review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-02/UI-REVIEW.md)
and [native acceptance notes](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-02/native/ACCEPTANCE.md)
retain the exact scope of each observation.

| Observed scope | app-02 result | Boundary |
| --- | --- | --- |
| ASS selected while paused at 13.82 seconds and playing at 20.08 seconds | Failed: expected text and green drawing absent | Native render/callback/alpha counts did not prove composition. |
| Chinese Copy label and fullscreen transport | Observed pass | Label/layout only; clipboard operation was not tested. |
| Natural-end state-layer deduplication | Observed pass | The duplicate stage heading no longer showed through the end-card actions. |
| Ended clock | Failed: `0:00 / 0:00` | The later owned final-position snapshot requires current-candidate evidence. |
| BIF strip | Observed pass: twelve JPEG cells at 0 through 55 seconds | Synthetic package delivery and image display, not real-server compatibility. |
| Compact overlay and restoration | Partial | Play/exit controls fit; restoration kept paused progress. Full prototype title/stop/time/transport parity was not established. |
| Ordinary narrow window and dynamic Home tooltip | Not established | The resize attempt did not change dimensions, and no tooltip state was captured. |

The exact retained handle for PID 15356 observed application exit code 0:
[app-02 exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-02/app-exit.json).
Its [bounded crash audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-02/crash-audit.json)
found zero matching Application events 1000/1026 for the exact executable path.
The fixture counters were cumulative across the shared environment, including
app-01; they are not app-02-only counts. These old passes and failures retain
their candidate identities.

## app-01 termination evidence

app-01 was closed through the normal UI and owned PID 4396 was subsequently
absent. Its [monitor receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/app-exit.json)
records `ExitCode=null` and `ExitCodeObserved=false`: the monitor did not retain
an exit-query handle. **The exit code is unknown; this must not be reported as
exit 0.**

The [crash audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-01/crash-audit.json)
found zero matching Application events with IDs 1000 or 1026 for the exact
owned executable path. That bounded audit does not recover the unknown exit
code or establish that every possible failure channel was absent.

## Historical independent native audio probe run-01

The standalone Native AOT audio probe's `run-01` completed with `Status=Passed`
for four cases. The [execution receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/execution.json)
records owned PID 44064, a pinned process handle, observed exit code 0 and no
forced termination. Its native runtime matches the frozen runtime above:
`7C33D10013829BA9D0BC69DF6EB51AF8BBA9BD4E60D7EF408BB0BC19EA6945D5`.
The probe executable SHA256 is
`0B785CDB7DEB804BAC633F766998410578D5176C7D24090B6ECE648D066ADCA6`;
its receipt confirms source inputs matched the frozen publication.

The probe links the real product `NativePlaybackEngine`, native audio interop
and `PlaybackCoordinator`, using measured synthetic 60-second H.264 media with
AC3, EAC3 and AAC through an owned ephemeral loopback origin. API negotiation
and server-report receipts are synthetic in-process observations. This scope
does not access real user media or establish real Emby server compatibility.
These four native probe cases remain separate from the 2,247 unit-test count
and the 233 HTTP fixture checks.

| Case | Probe result | Actual output and observed boundary |
| --- | --- | --- |
| `AC3_Off` | Passed | PCM with policy Off. No exclusive-format attempt or carrier submission. |
| `AC3_All` | Passed | The native exclusive-format attempt was observed, but the current default endpoint rejected the format. Actual output remained PCM with `FormatUnsupported`. |
| `EAC3_All` | Passed | The native exclusive-format attempt was observed, but the current default endpoint rejected the format. Actual output remained PCM with `FormatUnsupported`. |
| `AAC_All` | Passed | PCM with `CodecUnsupported`; no native carrier was attempted for the codec outside the passthrough policy. |

All four cases observed a stationary paused position, an exact seek to
80,000,000 ticks (8 seconds), advancing playback after resume, and successful
stop cleanup. Their [results](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-01/result.json)
record player detachment, no upstream requests after stop, engine disposal and
listener closure. These successful controls exercised the actual PCM backend.

Every case records `NativeCarrierSubmissionObserved=false` and
`ExclusiveCleanupExercised=false`. Active exclusive-carrier pause/seek/resume
and cleanup were not exercised. Software volume replacement from an active
bitstream output also remained
`NotExercised_NoActiveBitstreamOnThisEndpoint`; there is no successful
bitstream-to-PCM replacement or receiver-decoding acceptance claim. Physical
audible output, DTS/DTS-HD/TrueHD carrier delivery, other devices/drivers,
device hotplug and long-running resource regression remain unverified.

## Completed local acceptance and retained limits

| Recorded scope | Current status | Evidence or remaining boundary |
| --- | --- | --- |
| Current publish-05 build and published identity | Passed within recorded build/readback scope | Normal Debug/XAML, AOT05, SBOM generation, ASS package assertion and payload readback have actual exit 0. The full 668-file source set differs from publish-04 in only the two-call Information.cs feedback change; all 24 runtime files are unchanged. The initial driver exit 1 and its bounded recovery remain recorded. |
| Historical unit and HTTP executions | Passed in their original freezes; not rerun for publish-05 | 2,294 units are 2,250 product plus 44 acceptance-tool cases. HTTP 264 is 233 earlier plus 31 separately executed PGS checks and is not included in the unit total. Native ABI/audio/Parser receipts retain their own scopes. |
| app-03 ASS/SSA, Replay and Ended samples | Observed pass within the recorded samples | Its bounded style/drawing/movement/transform/karaoke, signed delay, SSA V4, Off, Local ASS/Off Replay and final clock remain accepted. Capture 13 is only Replay startup. Its actual exit remains Unknown. app-01/app-02 failures retain their identities. |
| app-04 ASS layout, information display, BIF and Home | Observed pass within captures 01-15 | Wide/fullscreen/restored/780-pixel narrow mapping, resume/natural Ended, twelve real BIF cells and the named three Home tooltip states are reviewed. Copy feedback's Close-area overlap remains an app-04 finding; numerical Buffer/Dropped frames and clipboard contents were not established. Its non-UI audit matched 595/595 payload files and bounded session/log evidence. |
| app-05 native embedded PGS | Observed pixels and bounded lifecycle/viewport acceptance with retained limitations | Both cues, first-cue observed expiry, Off clearing, actual seek34/demux recovery, narrow/fullscreen/restored mapping and Back-to-Home clearing are reviewed. Paused overlapping-cue reselection did not restore immediately, wide/fullscreen HUD crosses lower glyphs, and second-cue expiry was not sampled. The final non-UI audit matched 595/595 files and four timestamp-grouped Start/Stop session pairs. |
| publish-05 Copy feedback UI | Observed fixed within the targeted app-06 scope | Bottom success feedback leaves the narrow Close glyph clear, later disappears and is followed by actual panel Close. Basic Playing PGS at 6.2241041 seconds/1x and Back-to-detail are observed. Clipboard contents, during-feedback Close, exact timer duration and the unavailable branch remain untested. The full publish-04 subtitle matrix was not rerun. |
| Final current-source and copied-payload readback | Completed within the recorded final scope | Candidate and current publish-05 each match 595/595 payload files. The fresh complete 668-source/24-runtime manifest matches the publish-05 baseline with no differences and stable native/libass receipts. Initial 593/595 privacy refusal and final exact-icon-permitted readback 02 are both preserved. |
| Current owned environment cleanup | Completed within recorded resource scope | Environments 03/04 canonical validation/cleanup commands exited 0; exact fixture PIDs and ports are independently absent/released. Fixture process exits themselves remain unobserved. App/collector PIDs are absent and evidence retained. Collector exit 1 and original launcher exit 0 keep their separate receipts. Original 01/02 cleanup remains historical. |
| Normal owned exit | Actual exit 0 observed for app-04/app-05/app-06 | Each fresh run has its own exact retained handle, WaitResult 0, ExitCodeObserved=true and normal Close record. app-06's immediate 117ms window listing is retained; later absence is not used to infer exit. app-03's timeout/absence and AOT03's missing build exit remain Unknown. |
| Successful bitstream, exclusive-carrier controls and software volume replacement | Not exercised on the current endpoint | Both audio runs establish PCM policy/rejection fallback and PCM controls. A compatible receiver must accept and start real carrier delivery before exclusive-carrier controls and replacement can be accepted. |
| Physical HDR and display behavior | Not exercised | Physical compatible-display evidence is required. Metadata and a requested policy do not establish hardware output. |
| Complete animation, arbitrary fonts/scripts/SAR and PiP parity | Not established by these samples | Measured ASS/PGS states and specific viewports do not establish every continuous frame, attachment/font combination, aspect ratio or complete PiP prototype parity. |
| Broader Windows integrations and real target-server compatibility | Outside the completed synthetic acceptance scope | Do not infer notification/activation, SMTC, JumpList, display restoration or real-server behavior from these unit tests and owned synthetic walkthroughs. |

The local continuation's build, targeted publish-05 regression, final readbacks,
normal-exit and owned cleanup work are complete within the recorded scope.
Completion does not broaden the hardware, renderer or combination limits above.
Historical build/test or app-01/app-02/app-03 results are not relabeled as a later
run, and the publish-04 suites are not presented as new publish-05 executions.

The established API exclusions remain unchanged: no fabricated waveform,
media-time bitrate curve, bitmap cue dataset/OCR, edits to already burned
pixels, guaranteed event replay or unconfirmed closed-application push is
introduced. External paired IDX/SUB import and an unverified presentation
dropped-frame counter retain their separate implementation/capability limits.
