# Lumen native UI handoff

Updated: 2026-10-14. Repository: `emby-client-winui3`.
Original v4 base revision: `1a178f6bac39e933cd1d4a0d12da3c836c741f3d`. Current branch: `main`.
Status: `SubtitleFixesBuildVerified_UiAcceptanceDeferredByUser`.

The current [bitmap subtitle fixes](docs/implementation/lumen-subtitle-fixes.md)
have a separate native-06/publish-06 freeze, successful build/readback and 1,509
selected Platform/Playback cases. Actual subtitle/UI runtime acceptance is
deferred while computer use is paused. The App has a matched actual exit 0 with
unknown cause; canonical fixture cleanup completed. The publish-05 record below
remains the earlier completed checkpoint, not acceptance of the new source.

The [native playback follow-up](docs/implementation/lumen-native-playback-follow-up.md)
adds the separately pinned libass renderer, genuine WASAPI passthrough with PCM
recovery, server BIF image delivery and actual Windows buffered-range seconds.
The original native follow-up had its own remote verification scope.
Historical `final-02` results below are not reused as acceptance of these paths.
The later Windows continuation now has separate build, test and owned desktop
records; physical output and current ASS presentation retain their explicit
acceptance limits.

## Completed publish-05 Windows continuation

The user explicitly authorized this continuation's local Windows compilation,
tests, UI/UX checks and computer use. The
[Windows continuation record](docs/implementation/lumen-windows-continuation.md)
links the actual source identities, original failures and candidate-specific
observations. Desktop acceptance resumed after the historical app-03 pause.
That checkpoint's final candidate is `publish-05`; its
[build and readback report](artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
records normal Debug/generated XAML, Native AOT, SBOM generation, the complete
ASS package assertion and independent payload readback with actual exit 0.
The full 668-file production set has exactly one change from publish-04:
`PlayerView.Information.cs` routes the two Copy feedback calls through the
existing `ShowPlayerToast`. All 24 runtime files and native-05 receipt hashes
are unchanged. The initial post-build launcher exited 1 after the two builds
had already exited 0; the preserved recovery ran only SBOM/ASS/readback and
exited 0. It did not repeat compilation or erase that original failure.

Recorded publish-05 App SHA256 is
`0DE5DCC02368C59DE445B673BD7402DCDC868A7A478199F7AAD7CA5CD19D92C7`;
the retained native DLL SHA256 is
`D2DFD9236319F071A35A57E10CCAC00C8F577503B1FA1799B2CB3677B392D7E7`.
The later app-06 Copy feedback check binds to this exact App executable.
The [final current-source/runtime readback](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/app06-source-runtime-readback.json)
matches all 668 source files and 24 runtime files against the publish-05 build
baseline, with unchanged native/libass receipts and no differences. Its fresh
manifest SHA256 is
`A085FC4FAC8BCB5205B3037AA99E9000AF9959D850B0B90C95422142D8B67886`.

The [publish-04 verification](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/RESULTS.md)
retains 2,294 passing unit cases (2,250 product plus 44 LumenAcceptance),
including its 47 added Replay/geometry cases, with no failures or skips, and
the earlier 16-file Parser scope. These suites were not rerun for publish-05.
HTTP evidence stays separate: 233 earlier assertions plus 31 independently
executed embedded-PGS delivery checks give 264 across distinct binary scopes.
Native-05, AOT-04, package/SBOM readback, 20 exports, five ASS/SSA parsing cases
and six 96-byte frame-ABI gates keep their original execution identities.
Old AOT-03's build-process exit remains unknown.

The [app-04 UI review](artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
accepts the publish-04 ASS sample in wide, 2560 x 1440 fullscreen, restored and
780 x 894 ordinary narrow views, resume progression and the natural narrow
Ended clock at `1:00 / 1:00` with no old image. It also accepts the narrow
information display, twelve actual BIF cells at 0 through 55 seconds, and
the named Play, Watch again and Continue S1:E3 Home tooltips. Buffer and
Dropped frames display Unavailable; numerical measurements and clipboard
contents were not established. The Copy OSD's visual overlap with the panel
Close area is an app-04 observation that prompted the publish-05 correction.
Complete continuous same-cue animation, other SAR values and full PiP parity
are not inferred from these samples.

The [app-04 non-UI audit](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app04-final-non-ui-20261014/APP04-NON-UI-AUDIT.md)
matched all 595 copied payload files, preserved its bounded exact-path
Application event/log review and grouped 101 retained fixture playback events
by timestamps and session identity. Its one Start, 99 Progress and one Stop
are retained-window observations, not cumulative-counter subtraction.

The fresh [app-05 PGS review](artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
uses the same publish-04 App/native identities and establishes both native cues,
observed first-cue expiry, Off clearing, actual seek to 34 seconds with demux
recovery, narrow/fullscreen/restored mapping and Back-to-Home clearing. Paused
reselection inside the second cue did not immediately restore its bitmap;
the later demux recovery does not erase that limitation. Wide/fullscreen HUD
controls cross lower glyph pixels, while the 780 x 894 narrow cue is complete
and clear of controls. The
[app-05 non-UI audit](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app05-final-non-ui-20261014/APP05-NON-UI-AUDIT.md)
matched 595/595 payload files and completed bounded log/event review. Its 94
retained events contain four Start, 86 Progress and four Stop observations,
grouped by timestamps and four session identities; all four Stops report
Failed=false. These are retained-window facts, not lifetime-counter deltas.
The strong
[app-04 exit](artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json)
and [app-05 exit](artifacts/lumen-acceptance/windows-continuation-20261013/app-05/app-exit-final.json)
receipts both retain the exact matching process handle through actual exit 0.
These normal exits belong to those two fresh runs, not app-03 or publish-05.

The targeted [app-06 UI review](artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts the publish-05 Copy success-feedback correction in the actual 780 x 894
narrow window. Bottom feedback leaves the Close glyph and panel clear, then
disappears; the subsequent actual Close dismisses the unchanged panel at paused
position 14.559 seconds. Earlier actual PGS playback at 6.2241041 seconds and
`1x` shows a complete first cue without HUD overlap. Back reaches detail with
24% Resume and no old image. This is a targeted check, not a rerun of the
publish-04 ASS/SSA/PGS lifecycle matrix. Clipboard contents, exact toast duration,
Close while feedback is present and the unavailable branch remain untested.

The [app-06 strong exit receipt](artifacts/lumen-acceptance/windows-continuation-20261013/app-06/app-exit-final.json)
retains the same identity-matched handle for PID 9624 through WaitResult 0 and
actual exit 0 at `2026-10-14T04:46:22.7528096Z`. The immediate 117ms window
inventory still listed the window; subsequent independent absence is not used
to infer the exit code. The
[app-06 final non-UI audit](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
matched candidate/current publish-05 payloads separately at 595/595, completed
the full 120-second Application 1000/1026 reporting tail with zero bounded
records and found no non-None error in ten selected playback diagnostics.
Its raw fixture snapshot has 115 retained events; timestamps/session identity
select app-06's 21 events (one Start, 19 Progress, one Stop Failed=false) and
exclude the 94 earlier events. No ordinal or cumulative-counter delta is used.

The initial independent current-publish readback is retained as Failed at
593/595 because its privacy guard refused the two static
`settings_20_regular.svg` icon paths. It recorded no content hash mismatch.
The final exact-path permission produced
[full readback 02](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/publish05-readback-02.json)
at 595/595 Passed without repeating compilation or widening profile/credential
access. The first failed record is not overwritten.

The
[four-case native audio run-02](artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md)
uses native-05 and passed PCM policy, pause/seek/resume and stop cleanup. Its
owned probe retained a handle, exited 0 and was absent after cleanup. AC3/EAC3
exclusive-format attempts were rejected by the current endpoint and fell back
to PCM with `FormatUnsupported`; AAC fell back with `CodecUnsupported`.
Successful receiver bitstream, active exclusive-carrier controls, exclusive
cleanup and software-volume replacement from bitstream remain unverified.
The older run-01 keeps its own native-03 provenance; neither audio suite was
rerun for the Copy feedback delta. Physical HDR/display output remains unverified.

[app-02's retained process handle](artifacts/lumen-acceptance/windows-continuation-20261013/app-02/app-exit.json)
established exit code 0. The
[app-01 monitor](artifacts/lumen-acceptance/windows-continuation-20261013/app-01/app-exit.json)
recorded process absence with `ExitCode=null`; its exit code remains unknown
and must not be filled in as 0. Neither result is a physical-output claim.

The [app-02 pixel review](artifacts/lumen-acceptance/windows-continuation-20261013/app-02/UI-REVIEW.md)
retains its ASS image and Ended-clock failures alongside its scoped visual
passes. New native bitmap presentation corrects the deployed WinUI release's
zero-size `ImageCue` clip; Replay, Ended clock, SAR and lifecycle fixes are in
the frozen current source. After the user handled the unrelated security
prompt, app-03 from `publish-04` supplied
[23 saved captures and an independent review](artifacts/lumen-acceptance/windows-continuation-20261013/app-03/UI-REVIEW-resume-20261014.md)
on 2026-10-14. These establish bounded ASS static layers/drawing, sampled
movement/transform/karaoke, real playback progression, signed paused delay,
original SSA V4 pixels, Off clearing, Local ASS/Off same-item Replay and the
natural Ended clock at 1:00 / 1:00. Non-atomic UIA/raster differences are retained;
capture 13 is a Replay-start transition and is not paused or final-clock proof.

The user then temporarily paused computer use. That pause and its unperformed
fullscreen/narrow/PGS/BIF/Home checks are historical; the app-04/app-05 results
above extend the observed scope after the user resumed computer use. The
security obstruction is historical. The
[non-UI checkpoint](artifacts/lumen-acceptance/windows-continuation-20261013/app-03/non-ui-checkpoint-20261014.json)
records the original app PID's later absence, with 668/24 inputs still stable.
Its retained-handle monitor had timed out with wait result 258, so app-03's
actual exit code and cause remain unknown. Logical Stop and zero exact-path
Application events in the bounded IDs 1000/1026 audit do not establish normal
exit. No app closure was performed by the agent during the pause.

Both exact owned fixtures then completed guarded CLI
[cleanup](artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.json),
with actual cleanup-command exit 0, both PIDs absent and ports 18985/18986
released. Candidate/media/source evidence and synthetic profiles are retained.
The prepared environment-02 PGS fixture never received client playback; its
zero counters do not describe the later environment-04/app-05/app-06 runs.
The [final cleanup receipt](artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical guarded cleanup commands with actual exit 0 for environments
03/04, independent absence of their PIDs and listeners on ports 18985/18986,
and actual retained-handle exits 0 for app-04/app-05/app-06. Fixture process exit
codes themselves were not observed. Source, media, receipts and synthetic
profiles remain retained; no recorded owned active resource remains.

The outer collector's inherited pipes released naturally after fixture
cleanup. Its [completion receipt](artifacts/lumen-acceptance/windows-continuation-20261013/fresh-environments-03-04-3f1a2991/outer-collector-completion-20261014.json)
records PID 10704 actual exit 1 from a deferred post-start owner-wrapper guard;
the original launcher PID 26468 actual exit 0 remains unchanged. Both are absent.
No collector stop signal was sent, and the earlier rejected termination attempt
remains recorded. These outcomes are not summarized as all helpers exiting 0.
The local continuation and owned cleanup are complete within the measured
scope; historical unknown exits and untested hardware/renderer combinations
remain explicit.

Excluded waveform and media-time bitrate datasets, local media analysis and a
custom background push relay are not added. External paired IDX/SUB import is
not requested in this continuation. These current results do not alter the
historical Linux counts, old `NotRun` receipts or earlier static API research.

## Source alignment and historical verification

The current source follows the
[updated V2 design alignment](docs/implementation/lumen-v2-updated-design.md)
and [revised API boundary](docs/research/lumen-v2-updated-api-limits.md).
The main external HTML remains the visual authority. The prior
[supported-gap follow-up](docs/implementation/lumen-v2-supported-gaps.md) added
server-backed alphabet windows, LAN discovery and independent saved servers,
online events and notifications, disconnected-state recovery, actual text
subtitle timelines, per-item playback persistence, Windows activation/idle
resume, practical settings and explicit update staging/install. Conditional
native HDR10 metadata wiring used conservative negotiation. The updated source
adds conditional strict HDR10, native embedded bitmap subtitle delivery,
display-rate ownership/restoration, actual thumbnail metadata, typed library
navigation and cached Home refresh. Verification for that original alignment
task was remote-only; no application, local test, native build, startup write,
notification registration or installer was launched while developing that
checkpoint. Its no-computer-use constraint and retired research-browser
incident remain part of the historical alignment ledger. The separate current
Windows authorization and completed bounded acceptance are recorded above.

The updated `final-02` source passed 1,655 unique newly executed remote cases
(API 302, AppState 222, Playback 293, selected Platform 838) and the bounded
`SupplementedCSharpSemanticOnly` compile with zero errors. The
[new execution receipt](artifacts/lumen-design-alignment-20261013/remote-verification/RECEIPT.md)
records 727 stable code/configuration/native/XAML inputs and 13 Windows cases
that require Windows and were not run. Its current-XAML field declarations do
not regenerate XAML or certify normal WinUI/AOT/native builds or physical output.

The earlier supported-gap candidate 04 passed 1,312 unique remote executable
test cases and its separate application C# semantic compile. These results do
not certify the updated design source. Three Windows file-lock
methods, native/WinUI builds, desktop checks and installer execution remain
outside that Linux verification. Original failures, input-hash reuse and the
generated-XAML boundary are retained in the follow-up record.

The published binary and acceptance figures in the next paragraph belong to
the 2026-10-12 source checkpoint and do not certify this follow-up.

The active native UI follows the new Lumen Windows Client V2 handoff in the
external `design_handoff_lumen_windows_client` bundle. Its main HTML is the
design authority; the application remains WinUI 3 and does not embed the
prototype. The [V2 implementation and review](docs/implementation/lumen-v2-review.md)
tracks the current source scope, candidate-specific observations, remaining
features, and verification boundaries. Local Release Native AOT publishing,
scoped runtime/visual review, 1,141 product tests, 38 fixture boundary checks,
and owned cleanup are complete. The final candidate is `app-17-final`, published
under `artifacts/lumen-acceptance/v2-20261012/publish-17-final`, with a normal
exit code of 0. Product capability gaps and unexercised combinations remain
explicitly listed in the V2 review.

The v4 checkpoint and older candidate records below are retained as historical
evidence. Their completion, cleanup, and publication statements apply only to
those named candidates and do not certify the V2 rewrite.

## Previous v4 checkpoint (2026-10-01)

Archived artifact paths in this checkpoint and the historical ledger below are
preserved as literal references. These historical artifacts are not materialized
in this checkout and cannot be independently reread here. The link repair
preserves the recorded results and counts without regenerating or independently
reverifying their old reports.

Previous candidate: fidelity AOT10, SHA-256
`41FC56C007CE339636A4C4A8B0900D00951EB6B90B38400A3D63682487EEA6F0`,
23,432,192 bytes, output `artifacts/lumen-fidelity-aot-10`. `Publish-Aot.ps1` and
`Test.ps1` exited 0; 966 product tests passed, including five new default-audio-
intent cases. `source-stability-aot10.json` records 308/308 unchanged production
inputs after publication/tests at `2026-09-30T18:28:56.0838558+00:00`. The build
and test logs are `artifacts/lumen-fidelity-publish-10.log` and
`artifacts/lumen-fidelity-tests-final-10.log`. This is a bounded source snapshot,
not native acceptance or certification of future edits.

AOT10 bounded UI acceptance passed after renewed local UI authorization. All 15
wide references in `fidelity-aot-app-10-r2/native` were re-captured and reviewed,
using `12-player-episodes-populated.jpg` and `14-light-movies-wall-hover.jpg`.
Incorrect intermediate captures and their correction notes are retained.
The archived independent review reference is
`artifacts/lumen-acceptance/fidelity-comparisons/AOT10-INDEPENDENT-VISUAL-REVIEW.md`.
The historical review inspected 15 wide states, eight narrow images, current regressions, and fullscreen
without significant visible layout/readability collision. The preliminary
occluded Home capture is not selected evidence; all references/narrow checks
were collected after the first owned Home app closed.

Actual default movie Continue started at 21.6238116 seconds and later paused at
41.4638082; `movie-continue-fixture-receipt.json` and the regression capture keep
the backend/runtime evidence separate. An expected explicit-track fixture
rejection showed Chinese recovery commands and zero timeline/time, not a repeat
of the fixed default-intent route. Current 628x594 Medium/Large Tracks and Large
Episodes retain readable two-line cues; the drawer's tenth row was reachable.
The modern picker imported `fixture-test.en.vtt` while paused at 15.08 seconds;
Off immediately cleared its cue/local row and released the panel band. PiP
628x354 returned to 628x594 paused at 15.12; fullscreen 2560x1440 returned normally.
Originals are in `fidelity-aot-narrow-10/native`.

Actual exit-code receipts for owned PIDs 10880, 51388, and 57520 all record 0;
each exact-owned crash audit has zero matches. Env12 guarded cleanup released
port 18984; final fixture stats record seven Starts/seven Stops and 247 partial
responses for 247 range/media requests. Final local recheck records unchanged
308 inputs/SHA and zero owned local app processes/fixture listeners. No app/test
execution session remains. These are bounded checks, not motion/physical-audio,
HDR, broad media compatibility, or universal crash certification.

All 15 original native reference groups are in
`artifacts/lumen-acceptance/fidelity-aot-app-09/native`; states 04-06 show the
correct The Fallen Ground movie. Its completed independent 15/15 wide review
found no major layout collision but did not pass overall acceptance: default
movie Continue after paused S02E05 failed during Negotiating, and recovery
commands were untranslated. See `fidelity-comparisons/AOT09-INDEPENDENT-DESIGN-REVIEW.md`.
The failure and AOT09 observations remain AOT09 proof, not AOT10 proof. AOT09 did close normally from
the light movie wall after browse/playback: monitored actual exit code 0 and zero
matching owned event IDs 1000/1026. This scoped observation does not erase old
shutdown crashes or establish long-session reliability.

The latest human authorization restored local UI testing and superseded the
earlier VM-only checkpoint. No VM app UI was ever run. Approved elevated cleanup
completed at `2026-09-30T21:34:13.7616788+00:00`: the owned worker-preparation
observer/fixture stopped and its port was released, without desktop input or a
new worker. No required UI/cleanup work remains. Connection/account/path details
stay outside the repository.

Product commit `72f15ed72bcf875d5783fb61bea45a0a630a3d61` (257 files) was merged
into `main`, pushed, and verified on the remote. Hosted CI was in progress at that
verification; a green CI result is not part of this acceptance. All 23 unrelated dirty files in
the primary checkout retain their exact hashes and are preserved separately on
`codex/preserve-main-ui-work-20261001`; none was mixed into the product commit.
SVG checkout bytes were restored to original LF, and `.gitattributes` now enforces
LF for SVG files. All 308 production-input hashes still match exactly. The sole
acceptance-tool EOF blank removal is formatting-only; no application source or
accepted output changed.

The scoped implementation, acceptance, cleanup, and publication are complete.
No additional UI or cleanup gate remains. The old Blocked ledger is historical,
not a new threshold claim. Read the
[portable acceptance summary](docs/implementation/lumen-ui.md#current-acceptance-summary)
before optional local QA artifact links and historical entries below.

## Historical State

The candidate descriptions below retain their historical v4 checkpoint context;
their use of "current" or "newest" does not supersede the V2 state above.
Fidelity AOT08's original movie states 04-06 used Star Trails, not the required
The Fallen Ground. Its search Movies posters also failed to load; originals
remain in `fidelity-aot-app-08/native`. Its later `app-exit.json` records a normal
exit, separately from the initial physical-Escape/forced-cleanup checkpoint.
Neither incorrect content nor failed posters are relabeled as passing evidence.

At that checkpoint, the active application was the native Lumen rewrite of the v4
`design_handoff_emby_player_ui` reference bundle. `MainWindow` hosts `LumenShellPage`;
the HTML prototype is a design reference, not the application runtime.
The earlier functional/runtime checkpoint is complete, but it did not establish
strict visual restoration. Its AOT08 source passed 919 product cases and native
fixture/official-server regressions, with owned cleanup retained. The historical
record cites the archived report at
`artifacts/lumen-acceptance/final-report/ACCEPTANCE.md` and receipt at
`artifacts/lumen-acceptance/final-report/acceptance-summary.json` with their
original identity; `AcceptedForScopedDesignHandoff` must not be interpreted
as a matched design-fidelity pass.

The current fidelity follow-up corrects materials, gradients, cover/crop,
typography, hover controls, slider/dialog themes, search counts, list quick play,
and scoped playback commands. The full product suites pass 961 cases. Ordinary
desktop access returned; all 15 AOT03 states were captured and independently
reviewed. The review found light-home content 52px too high and actual wide
captions wrapping unnecessarily; AOT04 corrects both.

AOT07 is the newest candidate, 23,421,440 bytes, SHA-256
`84C0CABF89F924010DAD1470D71829C176D77F03F88432DE1963D2E7BD51A217`.
Publication exited 0; 961 product tests passed and its 307 recorded production
inputs matched unchanged. No AOT07 native reference state has been captured.
The seven AOT04 wide states and named player interactions remain historical.
AOT05 corrects actual narrow-caption overlap and adopts the modern desktop VTT
picker; these changes have no completed native acceptance. Its PID13612 crashed
while draining DispatcherQueue during shutdown, with callback HRESULT E_POINTER.
The exact delegate is unknown. AOT06 adds caption-layout lifecycle/epoch guards;
AOT07 also guards queued high-contrast layout after disconnect/unload. Normal
shutdown and crash resolution remain unproven.
After the latest physical Escape, the human requested no desktop operation.
Input remains stopped pending renewed permission. Owned application/backend processes are absent and cleanup receipts
are retained. Do not launch the UI until permission resumes. All 15 current
design states, narrow/PiP, local import and normal-close gates remain pending.
The archived fidelity checkpoint reference is
`artifacts/lumen-acceptance/fidelity-final-report/ACCEPTANCE.md`.

The [Lumen implementation record](docs/implementation/lumen-ui.md) owns the
source map and candidate boundaries. The [API capability contract](docs/api/06-lumen-capabilities.md)
records endpoints, modern/legacy tag schemas, bounded field-preserving mutations,
the real-server resume default, and endpoint-specific authorization. Full Noto
Serif SC and Manrope fonts and the handoff Lucide assets ship with their notices.
The repository's own license remains awaiting an owner decision.

## Completed evidence

The table below is the historical functional checkpoint, not V2 certification.
The v4 publication/test/hash evidence remains in its historical fidelity report.

| Scope | Recorded result and location |
| --- | --- |
| Debug compilation | Fifth application Debug build succeeded; zero errors and one existing generated WinUIEx `Icon` `CS0618` warning; `artifacts/lumen-build-debug-fifth.log` |
| Final product test run | Exit 0; 919 passed, zero failed/skipped: API 174, AppState 99, MediaTransport 125, Platform 326, Playback 195; `artifacts/lumen-tests-release-final-candidate.log` |
| AOT08 publication | Exit 0 with one existing generated WinUIEx `CS0618` warning; `artifacts/lumen-publish-aot-eighth.log`; output `artifacts/lumen-aot-candidate-08` |
| AOT08 build inputs | 302 captured source/assets/license/toolchain inputs compared unchanged; `artifacts/lumen-acceptance/final-report/source-inputs-aot08.json`; docs/tests/tools are outside this manifest's scope |
| Bootstrap boundaries | 42/42 passed after explicit administrator-role support; `artifacts/lumen-acceptance/tool-build/bootstrap-admin-tests.log`; the tool build has zero warnings/errors |
| PowerShell and fixture boundaries | Safety 12 and fixture HTTP 131 passed; `artifacts/lumen-acceptance/20260930-071441-1922ba56/environment-summary.json` and `artifacts/lumen-acceptance/tool-build/fixture-harness.log`; no media decoding is implied |
| Official server APIs | Official Emby `4.9.5.0` on `ssh test-env`, 21/21; `artifacts/lumen-official-validation/new-ui-api-summary.json`; temporary mutation data removed, backend retained at that protocol checkpoint and later removed by final cleanup |
| Native Debug UI | Home dark/light, 6/8/10-column walls, descending sort, watched filter/list, two-season detail/resume, search/person/back, favorites, and appearance; `artifacts/lumen-acceptance/app-debug-02/native` |
| Native AOT01 | Published with exit 0; real-server video/motion, pause/seek, paused subtitle selection, 1.5x rate, Space/Escape, fullscreen, and compact-overlay entry/return; retained caption and minimum-size failures |
| Native AOT02 | Candidate `aot-02-20AA3B3E`; real-server English/French cues, pause-preserving French audio/subtitle selections, size 38, delay +0.1, local WebVTT import/clear, stable track rows across 12.3 seconds, and restored compact proportions; `artifacts/lumen-acceptance/aot-official-02/native` |
| Native AOT03 | Candidate `aot-03-73395895`; rich-fixture Light/Sage persistence, search/settings layout, season drawer, explicit S2E5 resume at 31 seconds, natural continuation, chapter preview, and marker-driven intro skip; `artifacts/lumen-acceptance/aot-fixture-03/native` |
| Native AOT08 fixture | Candidate `aot-08-D6732C40`; actual narrow/wide search/settings, series quick-play resume, local import/immediate Off, paused seek/captions, complete chapter preview clearance, PiP return, episode drawer, and normal close; `artifacts/lumen-acceptance/aot-final-fixture-08/native` |
| Native AOT08 official server | Same `aot-08-D6732C40`; admin restore/search/detail/editor prefill-and-cancel, real HLS video, pause-preserving English/French text and audio selections, 187.297-second row stability, normal close; `artifacts/lumen-acceptance/aot-final-official-08/native` |
| Local fixture cleanup | Exact owned fixture stopped, port released, evidence retained at 03:18:12 UTC; `artifacts/lumen-acceptance/20260930-071441-1922ba56/cleanup.json` |
| Remote cleanup and final report | Completed; `artifacts/lumen-official-validation/cleanup-summary.json` records `Removed` and zero remaining owned active resources; final report/receipt record scoped acceptance |

The old Bootstrap 34-case receipt remains historical; it is not added to the new
42-case result. Earlier independent product totals and failing attempts remain
in their original logs; the unified 919-case result does not rewrite them.
The official API receipt explicitly marks native UI/decoder/first frame `NotRun`;
later native observations are separate evidence. Its mutation cleanup is not
backend teardown. Product tests and all tooling/API counts must remain separate.

## Candidate boundaries

AOT08 is `artifacts/lumen-aot-candidate-08/EmbyClient.App.exe`, 23,101,952 bytes,
SHA-256 `D6732C40499306BB1CAA8168C773275BDE335DCF963E2C020B9FE62252515B2A`.
Keep the complete folder. The input manifest's SHA-256 is
`85F21C6C944F80DB4C3E1B4AA365220550D62D7057287249F95B0F8E3A698E76`.
It lists 263 `src` inputs, 36 license inputs, and three root toolchain/property
files; it is not a native acceptance receipt or a hash check of the entire
worktree. Its unchanged comparison does not include this documentation.

AOT01's frozen executable is
`artifacts/lumen-acceptance/aot-official-01/candidate/EmbyClient.App.exe`,
22,947,328 bytes, SHA-256
`BCDBA8AA3760A3564EDC2EC6FE7DD6AC31042A92C6C2E375CBDF13CF6DBCD89B`.
Its PID 18904 exited normally with observed `ResourceReleased` and no cleanup
errors. Retain `failure-local-webvtt-empty-at-four-seconds.png` and
`picture-in-picture-minimum-size-issue.png` in its native directory as failed
AOT01 evidence, even though AOT02 later observed the corrections.

AOT02's PID 52336 exited normally. Its captured compact window is 628x354;
French audio selection is not a claim of independently heard physical output.
Its concrete cue/import/clear observations do not certify ASS/PGS or all styles,
media, devices, or subtitle timings. AOT03's intro range 3-10 seconds, skip shown
at about five seconds, and seek to 10.001 seconds exercise controlled fixture
markers, not licensed official-server intro detection. Field screenshots and
live observations retain their respective evidence scope; file names alone do
not prove motion or keyboard behavior. In particular, the AOT03 capture named
`settings-narrow-window` records 1428x894 and does not independently certify a
narrow client viewport. AOT02/AOT03 are not proof for the final AOT08 executable.

AOT05 (`54B0DE21`, 23,091,200 bytes) published with exit 0 and observed bright
captions, localized Audio/Subtitles headings, and an administrator name-only
metadata save/readback/restore. The safe summary is
`artifacts/lumen-official-validation/metadata-name-only-ui-save-and-restore-summary.json`.
It preserved the three separator-containing tag names and their IDs, then
restored the original metadata. Its refined comparison classifies server
name-derived fields and user subtitle-preference projections; it is not raw
JSON byte equality, and the prepared tags had zero unknown fields. Actual
628x594 Home/settings/empty search worked, but loaded `Night` search crashed
with `LayoutCycleException` `0x802B0014`; AOT05 is `NotAccepted`.

AOT06 (`958F5623`, 23,100,416 bytes) repaired that loaded-search cycle and
observed narrow/wide queries, real person-return history restoration, quick-play
resume, captions, and chapter-image clearance. Its local Off row remained
disabled for a server with only Off, and the narrow chapter label was clipped;
both failures remain retained. An empty Search navigation screenshot is not
query-cache evidence. AOT07 published as an intermediate build; static review
found a picker/eligibility timing defect before native acceptance was attempted.
Neither candidate is relabeled as AOT08 evidence.

## AOT08 fixture observations

- Actual 628x594 captures show Home, loaded `Night` with 39 results, a four-column
  grid, stacked playback settings, and the six-series wall without a new crash.
- Poster Play resolved S2E5: first timeline 31.21 seconds, then paused at
  40.5599946 seconds before its natural end. Resume fixture reseeding is separate
  test setup, not a native playback observation.
- A normal file-picker import at about 40.57 seconds made local subtitles active
  and Off immediately enabled without reopening the panel. Pointer seek to 30
  seconds retained pause and a 52-character cue; the narrow chapter image and
  full title remained visible without overlapping captions.
- PiP captured 628x354 with a local caption; return to 628x594 preserved pause at
  30 seconds. A 2560x1392 capture repeated chapter/caption clearance. Clicking
  Off actually removed caption/local row, disabled style controls, and retained
  pause/position at 30 seconds.
- Season 2's drawer loaded six episodes with current S2E5. The first click merely
  dismissed the AV panel and is not a drawer pass; the subsequent click opened
  the loaded drawer. Returning to series details retained the S2E5 resume hero;
  old season-one watched/progress marks are not live playback status.
- PID 30308 closed normally. `artifacts/lumen-acceptance/aot-final-fixture-08/app-exit.json`
  records process absence and zero new unhandled entries while retaining the old
  AOT05 exception. An initial receipt guard counted blank lines; its corrected
  nonblank comparison is a tooling fix, not a new application failure.

These are fixture-native observations of the exact AOT08 executable. They do
not establish licensed official-server marker generation or physical audio.

## AOT08 official observations

The same 23,101,952-byte AOT08 executable restored the isolated administrator
profile at 1428x894, searched `Open Water` to its one exact result, and opened
item `20`. Real media information showed 540p H.264, 59.04 MB, two AAC and two
SRT tracks without a false `None` badge. The seven-action More menu enabled
metadata editing. The editor prefilled `LumenSyntheticAcceptance` and genres
Adventure/Drama, then Cancel closed it without Save. This is not a second
AOT05 name-only mutation or preservation test.

Real HLS playback showed an English cue at 4.902 seconds and changing test
pattern frames before Pause at 15.8199582 seconds. Pointer seeking reopened HLS
and completed paused at 4.0099792 seconds with a bright cue; its final completed
capture, not the temporary loading view, is the passed scene. The localized
Audio/Subtitles panel showed two AAC choices and three subtitle choices including
Off, with caption controls enabled. French audio selected index 2 while English
subtitle index 3/cue and pause remained; French subtitle index 4 then preserved
audio 2 and pause with `Film de test Lumen.` visible. The resulting pause
positions were 4.0199792 and 4.0299792 seconds respectively.

`final-track-identity-stability.json` records selected UIA rows 670/677 and the
same paused timeline across 187.297 seconds. It does not independently count
backend progress reports or establish physically heard French audio. Parsed
server WebVTT observations had three native/text/captured cues, `TimedTextCue`
wrappers, and no parser exception. PID 55124 closed normally at 03:37:41 UTC;
`artifacts/lumen-acceptance/aot-final-official-08/app-exit.json` records absence
and zero unhandled entries. The fixture's old AOT05 exception remains historical.

## Latest source contracts

- `TagItems` names are authoritative when non-null, including an empty array;
  legacy `Tags` remain separately mapped to their original wire field. Numeric
  tag IDs use `long` and accept integer/string representations. Sparse metadata
  updates preserve untouched raw tag values, IDs, and unknown members. Explicit
  modern tag edits reuse exact-name objects and synchronize modern/legacy names;
  legacy-only responses keep legacy-only writes. Forty added API cases cover
  these boundaries. Unchanged genre/tag editor text is not reparsed, preserving
  separator-containing names on no-op saves.
- Series quick play awaits the already-issued resume/next-up recommendation
  under its page/session/season owner; details still load without waiting.
  Failed optional discovery falls back to loaded episodes, while explicit
  movie/episode choices are never reinterpreted. Thirteen added AppState cases
  cover the recommendation race, cancellation, and owner changes.
- Targeted AOT `DynamicDependency` preserves `TimedTextCue`/`TimedTextLine`
  projections. `ForceTranscoding` and `ForceSubtitleBurnIn` are independent;
  playback's 195 tests include 53 external-text/delivery-fallback cases.
- Track rows retain identity across unchanged periodic reports. An owned local
  subtitle can enable only its Off command when server tracks are unavailable;
  picker completion refreshes eligibility immediately. Compact-overlay
  limits become 320x180 before entering and restore 640x600 for the ordinary
  window. False `None` badges and untranslated media headings are corrected.
  New-profile LAN bitrate is zero/Unlimited without resetting saved preferences
  or bypassing account/server limits.
- Subtitle diagnostics are opt-in through an existing absolute
  `EMBY_CLIENT_DATA_ROOT`, bounded to 64 KiB, and record only sanitized
  wrapper/count/error metadata, not text, URLs, IDs, or credentials. Final source
  also improves caption foreground and chapter-preview/caption avoidance. Search
  sizing is keyed/idempotent and avoids best-match size feedback. Narrow/wide/
  compact dock slots and the enlarged chapter-preview slot are covered by the
  scoped AOT08 fixture checks, not broad accessibility certification.

## Completion and cleanup

The official cleanup summary records completion at 03:47:01 UTC: one owned
container/network/remote relay/private run directory/local SSH tunnel and two
auxiliary directories removed or stopped, two loopback endpoints released, and
zero owned active resources remaining. Shared official images and four
preexisting containers were preserved. Exact remote-resource and guarded local
tunnel proofs are `artifacts/lumen-official-validation/remote-cleanup-proof.json`
and `local-tunnel-cleanup-proof.json`; local fixture cleanup remains its own receipt.
Evidence was retained, credentials were not printed, and cleanup changed no
source/system configuration or Docker prune state.

No further source edits, builds, tests, UI checks, installation, or deployment
are required for this scoped goal. Existing release/device/accessibility
boundaries are future gates, not unfinished rewrite work. No commit, push,
external PR/issue write, signing, or installation was performed. Never replay
historical PIDs or stop unrelated services.

## Behavior and authorization

The native HUD auto-hides after three seconds when interaction permits. Windows
caption buttons remain visible in windowed mode because Windows ignores their
foreground alpha; fullscreen uses the native fullscreen presenter. Hardware
decoder override, HDR, and refresh-rate matching are disabled with accurate
reasons. The SDR H.264/AAC/HLS baseline is unchanged. Caption style/delay applies
only to parsed external text, never burned video; local `.vtt` is not uploaded.

The user authorized local Windows builds/runtime/UI acceptance and isolated
backend/test-data fill, edit, and deletion through `ssh test-env` for this task.
Use task-owned profiles under process-scoped `EMBY_CLIENT_DATA_ROOT`; leave
normal saved accounts untouched. Explicit administrator bootstrap is restricted
to the separately authorized synthetic metadata-editing account and verifies
its expected role. Do not print credential/profile files or write actual
PRs/issues, sign/install packages, push, or deploy without authorization.

The prior [V7 native report](docs/implementation/v7-native-acceptance-20260915.md)
and [historical implementation record](docs/implementation/status.md) retain
their original candidates. Existing accessibility, stale native response/race,
large-library/long-session, broad media/server/device/physical-output, signed
installation/upgrades, and clean-machine playback gates remain open.

Use Chinese for user-facing conversation and English for code, comments, and
documentation. The shell is PowerShell. Continue native Lumen; do not restart
the prototype or turn unobserved requirements into passed results.
