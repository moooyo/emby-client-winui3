# Updated Lumen V2 design alignment

Alignment checkpoint: 2026-10-13. Current Windows status: 2026-10-14.
This record supersedes the earlier supported-gap source
description where the external V2 design has changed. Existing unrelated
working-tree changes are preserved. No commit, push, PR, release, installation
or application launch was authorized by the original alignment task.

## Current Windows continuation

The authorized [Windows continuation](lumen-windows-continuation.md) is complete
within its recorded local scope. Its current final candidate is
[publish-05](../../artifacts/lumen-windows-continuation-20261013/publish-05/).
The [build and readback report](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
records actual exit 0 for normal Debug/generated XAML, Native AOT, ASS packaging
and SBOM/payload readback. Exactly one of 668 production files changed relative
to publish-04: two Copy feedback calls use the existing bottom `ShowPlayerToast`.
All 24 native runtime files and native/libass build receipts remain unchanged.
The 2,294 unit cases and 264 HTTP assertions below retain their earlier frozen
scopes; they were not rerun for this feedback change.

The publish-04 [app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
records sampled ASS wide/fullscreen/restored/narrow mapping, natural Ended,
twelve BIF JPEGs and action-specific Home tooltips. Its separate
[app-05 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
records both native PGS cues, first-cue absence by 18.7904166 seconds, Off
clearing, seek/demux recovery, viewport changes and Back to Home clearing.
Paused mid-cue reselection did not immediately restore the earlier bitmap;
wide/fullscreen controls cross some lower glyph pixels. The publish-05
[app-06 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts bottom Copy success feedback clear of Close at 780 x 894, later
feedback disappearance and panel Close, a complete first PGS cue during `1x`
playback and Back to details. This targeted feedback/basic-PGS regression does
not rerun the earlier lifecycle matrix. Clipboard contents, failure feedback
and Close while feedback is displayed remain unobserved. All three fresh runs
have actual same-handle normal-exit receipts with code 0.

The [final non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
records full 595/595 file readbacks for app-06 and current publish-05, plus a
fresh complete 668-source/24-runtime manifest matching the publish-05 freeze.
Initial helper failures remain preserved with their completed recoveries.
[Final owned cleanup](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical fixture Stop command exits 0, absent owned processes and
released ports 18985/18986. The outer collector naturally exited 1 after pipe
closure and its deferred wrapper guard, separate from the original launcher
exit 0; no collector stop signal was sent. Complete continuous ASS
interpolation, arbitrary overlapping-PGS reconstruction, full PiP parity,
successful receiver bitstream and physical HDR remain outside accepted scope.
The API exclusions below are unchanged.

### Historical pause checkpoint (2026-10-14)

The later [Windows continuation](lumen-windows-continuation.md) has explicit
user authorization for local Windows compilation, tests, UI/UX checks and
computer use. At this earlier checkpoint, its resume retained the original
`publish-04` app-03 candidate, with 668 stable production inputs and 24 stable
runtime files. Further computer use was temporarily paused at the user's
request. The following receipts retain that historical scope.

Owned PID 7464 still matched at 02:28 UTC on 2026-10-14; read-only observations
at 02:33 and 02:35 found it absent. The
[app-03 exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/app-exit.json)
retains an unobserved actual exit code (`null`) and `Unknown` exit cause after
the earlier retained monitor returned 258 (timeout). Normal UI closure was not
observed. The [bounded session Stop audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/session-stop-audit-20261014.json)
and [zero-match exact-path event 1000/1026 audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/crash-audit-20261014.json)
do not establish exit code 0. The
[owned cleanup receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.json)
and [execution receipts](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.execution.json)
record guarded CLI cleanup of both fixtures after `ValidateOnly`, with actual
Stop command exit codes of 0. Fixture PIDs 29348/45576 are absent and ports
18985/18986 were released at that checkpoint. No owned app or fixture remained
running then; source, inputs, media and synthetic profiles were retained.

The suite recorded at that freeze passed 2,250 product tests and 44
acceptance-tool cases: 2,294 total, with zero failures or skips. Earlier 233 HTTP fixture checks
and the separate 31-check synthetic PGS scope total 264 checks across distinct
frozen execution scopes. This is not a single-binary rerun, and HTTP checks are
separate from the unit-test total. The earlier 16-file PowerShell Parser result
retains its original scope. The `native-05` build, normal Debug build with
generated XAML, `publish-04` Native AOT, packaging and SBOM generation/readback
have actual exit-code-0 receipts. Historical AOT03 build exit remains `Unknown`.
The old `final-02` counts and `RequiresWindows/NotRun` records below retain their
original source and Linux scope.

After the unrelated security prompt was manually cleared, the
[2026-10-14 app-03 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/UI-REVIEW-resume-20261014.md), based on
[saved pixel evidence](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/),
records acceptance for the measured ASS/SSA fixture scope. It shows the full
static ASS style and drawing layer, paused seek/move samples at 10 and 12 seconds,
white/100% and yellow/150% transform samples, karaoke word-color samples at 29
and 31 seconds, real playing clock advancement, LocalASS Replay restoration,
immediate paused refresh at delay 0/+0.1/-0.1 seconds, ASS/SSA Off clearing,
original SSA V4 import with two visible lines, natural Ended at `1:00 / 1:00`,
and Off retained on Replay. This establishes
visible pixels for that frozen native bitmap presenter within the sampled
scope; it does not prove continuous interpolation across the whole animation.
Capture 13's paused filename is misleading: it is a Replay startup transition,
not pause or Ended acceptance.

The `native-05` AudioProbe
[run-02 receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md)
records four passed PCM policy/rejection cases, zero paused clock drift, paused
seek to 8 seconds, resume clock advancement, and Stop/engine/listener cleanup.
Owned probe PID 17432 has an actual retained-handle exit code of 0 and was
subsequently absent. BH900 PRO rejected AC3/EAC3 exclusive formats with
`FormatUnsupported`; AAC reported `CodecUnsupported`, and no carrier frames
were submitted. Successful receiver bitstream, active exclusive-carrier
controls, bitstream-to-PCM replacement for software volume and physical
HDR/display output remain unverified. The earlier `native-03` / `run01` results
retain their own candidate attribution.

The twelve actual BIF JPEG previews in app-01 remain historical observations;
its actual exit code remains unknown. App-02 has an observed retained-handle
actual exit code of 0. At the pause checkpoint, PGS visible pixels, fullscreen
and ordinary narrow-window image layout, BIF/Home tooltip rechecks, and observed
normal UI closure of a fresh owned run remained **Pending**. PGS had not been
client-started before that cleanup. These desktop checks required fresh owned
environment and app run directories; later app-04/app-05 evidence above records
the completed samples using the same `publish-04` payload. The old PID and run
directory were retired. Complete PiP prototype parity remains outside accepted
scope. The linked continuation records exact candidate identities and evidence
boundaries. The excluded
waveform/bitrate datasets, local media analysis and custom push relay remain
excluded, and external IDX/SUB pair import is not requested in this continuation.

## Original alignment authority and source boundary

The authority is `D:\Code\design_handoff_lumen_windows_client\Lumen 原型 V2.dc.html`.
Its README and data module provide context; the main HTML wins when their
timings or interactions disagree. The application remains native WinUI 3,
C#, XAML and Windows Composition. Prototype media and simulated OS surfaces
are not production assets or runtime components.

The 2026-10-12 `app-17-final` acceptance and the subsequent candidate 04's
1,312-test receipt cover their own source snapshots. Neither certifies this
alignment. Verification of that alignment checkpoint was restricted to
`ssh test-env`; local builds, tests, probes and application UI work were not
authorized in that original task. The continuation has its own authorization
and receipts above.

## Alignment ledger

The subsequent [native playback follow-up](lumen-native-playback-follow-up.md)
adds libass ASS/SSA rendering and exact-format WASAPI bitstream source paths,
server-delivered BIF images and real buffered-range duration. These changes
have their own verification scope and are not certified by the historical
`final-02` results below.

| Requirement and authority | Source implementation | Boundary |
| --- | --- | --- |
| Fixed Home, Movies and TV navigation; avatar highlight for favorites/settings; Ctrl+1/2/3. HTML 1104, 1299-1311. | `LumenNavigationBar.cs`, `LumenShellPage.cs`, `LumenLibraryView.State.cs`. Dynamic top-level Views and overflow are removed. | Actual libraries remain available within their media type page. |
| Library selector, All aggregation, server order, mixed libraries and filter preservation. HTML 143, 1098; latest View all at 1733. | `LibraryViewModel.LibraryNavigation.cs`, `LumenLibraryView.WallLibraries.cs`, typed library counts. | Missing server counts remain unknown; selection is restored only after current Views are known. |
| Per-account movie/series library selection across restart. README library picker and `libPick`. | `MediaLibrarySelectionStore.cs`, `LumenShellPage.LibrarySelections.cs`. | Only two library IDs are stored. Temporary sessions skip storage, and corrupt reads preserve the original file. |
| Real login users, bounded account selection and the two radial background layers. HTML 46 and the sign-in views. | `LumenShellPage.SignIn.cs`, `SignInBackdrop.cs`, connection and account-prompt hooks. | Server/user IDs own password prompts. Temporary sessions retain memory behavior and skip local account-specific library, playback and idle-resume storage. |
| Native caption controls at the top-right, Tall system title bar. README Windows mapping; HTML global chrome. | `MainWindow.xaml.cs`. System buttons replace the custom XAML caption button tree. Physical native insets are excluded from custom drag regions. | Windows supplies caption accessibility, Snap and system button metrics. Full accessibility and Snap behavior require separate acceptance beyond the scoped normal Close observations above. |
| Home center playback, bottom padding, eight-second carousel, one right-rail Next/Top control. HTML 83-95, 529, 773, 1362-1364, 1815-1816. | `LumenLibraryView.HomePlayback.cs`, `HomeVisuals.cs`, `HomePaging.cs`, `LumenPageDeck.cs`. | Playback and progress use authenticated items. Rotation pauses for dialogs, menus and a hidden host. |
| Four search tabs and server-backed discovery. HTML 324-359, 1521. | `LumenLibraryView.Search*.cs`, normal All media queries restricted to Movie/Series. | Explicit BoxSet operations remain available. Demo people and genres are not used. |
| Detail media stage, shared version selection, file header and no full parameter dialog. HTML 257, 268-280, 862-866. | `LumenDetailView.MediaVisualization.cs`, `MediaDialog.cs`, media-info navigation and restored card focus. | Source facts use Emby metadata and actual delivery. |
| Flat audio bars, striped bitmap subtitle lanes and real text cue intervals. HTML 280-281. | `LumenDetailView.MediaVisualization.cs`, `SubtitleTimeline.cs`, `SubtitleTimelineService.cs`. | No waveform, bitrate sampling, OCR or fabricated timeline is added. |
| Actual thumbnail positions and chapter artwork. HTML 279, 1466. | `EmbyApiClient.Thumbnails.cs`, `BifThumbnailReader.cs`, `LumenDetailView.Thumbnails.cs`. | Verified BIF JPEG images retain actual server timestamps; absent packages keep actual chapter artwork and ThumbnailSet metadata. No arbitrary frame extraction or guessed image route. |
| Burned subtitle controls disabled independently; bitmap size and delay capabilities separated; local files labeled PC-only. HTML 449-455, 1013-1014, 1555, 1589-1591. | `PlayerView.SubtitleControls.cs`, interaction, quick controls and information panel. | Actual Encode delivery decides burn-in, including encoded text subtitles. |
| Information panel uses source bitrate or actual current session transcode target. HTML 467-473, 1602. | `EmbyApiClient.Sessions.cs`, `PlayerView.ServerPlaybackInformation.cs`, `Information.cs`, native buffered-range and actual audio observations. | Unique device/user/item session matching is required. Buffered seconds require actual Windows ranges; bitstream requires accepted native output. No peak, throughput substitute or invented dropped-frame count. |
| P5 conversion before presentation and a real compatible 1080p alternative. HTML 919-935, 475-481. | `PlaybackRequestFactory.cs`, `PlayerView.DolbyVision.cs`, `Toast.cs`. | An alternative is enabled only after PlaybackInfo confirms it; source index 1 is not assumed compatible. |
| Conditional native HDR10 and live display eligibility. README HDR output and HTML settings 1538. | `Hdr10PlaybackPolicy.cs`, conditional device profile/request selection, `NativePlaybackEngine.Hdr.cs`, `PlayerView.Hdr.cs`, shell/settings/window bridges. | Requires unambiguous HDR10, HEVC Main10, 10-bit PQ/BT2020 progressive MP4/MKV, actual native support and Windows HDR-on. Other HDR formats or failed opening checks use fresh server SDR conversion. Active HDR10 eligibility loss or Off requests conversion with position/pause retained. Physical output is not yet accepted. |
| Embedded PGS/VobSub and complete ASS/SSA source styles. README native subtitle scope. | `NativeSubtitlePlaybackPolicy.cs`, `NativePlaybackEngine.NativeSubtitles.cs`, the libass renderer/bridge, subtitle preparation and diagnostics. | Embedded delivery requires the original static source and a unique actual native index/codec match. Complete original ASS/SSA uses libass when its exact versioned API is available, with external full scripts preferred. Native image/source-styled cues keep real delay and disable unsupported appearance edits. Unknown mapping retains confirmed conversion or an explicit unavailable result. Windows rendering was pending at the source-only alignment checkpoint; the completed scoped observations and remaining renderer limits are recorded above. |
| Online followed/favorite episode notifications with hero image and two actions. HTML 628, 1200-1206, 1240. | `EpisodeNotificationEligibility.cs`, notification content/service/target, `LumenShellPage.ServerEvents.cs`. | Favorites or actual resume/played evidence is required. NextUp alone is not proof of watching history. Disconnect/fullscreen/account generations retire pending delivery. |
| Reconnect and online library changes refresh Home shelves and cached history as well as the visible route. README reconnect behavior. | `LibraryViewModel.HomeRefresh.cs`, history/presentation guards, library wrapper and shell event integration. | Reads actual Views and all Home lists. Partial failures retain old rows and stale history; only successful current-route and Home reads consume the refresh revision. An active Home avoids duplicate requests. |
| Details opens the series; Play now starts the episode at zero; Remind later dismisses. HTML 1240. | Notification intent parsing, current-account lookup, existing playback preparation. | No future reminder is scheduled and no closed-application push relay is added. |
| JumpList has up to four real Resume items and Search, Settings, Switch account tasks. HTML 1229-1232. | `WindowsJumpListService.cs`, packaged/unpackaged publishers and resume presentation helpers. | OS slots, removed items and account boundaries are respected. Idle SMTC retains its separate single-item history. |
| Content refresh-rate matching with ownership and restoration. README Windows mapping; HTML settings 1538. | `ContentRefreshRateService.cs`, `ContentRefreshRatePolicy.cs`, `MainWindow.Display.cs`, actual delivered native frame-rate snapshot. | Only fullscreen playing video is eligible. Integer Windows Hz represent nominal matching; fractional or hardware acceptance is not claimed. |
| Real diagnostic folder, online notification explanation and truthful decoder/output settings. HTML 1535-1540. | `LumenSettingsView.cs`, `PlayerView.Diagnostics.cs`, `LumenShellPage.cs`. | No invented GPU, decoder, output or hardware status. |

These native paths connect formal interfaces already present in the pinned
renderer. At the source-only checkpoint, two native fixes corrected ordinary
ASS style detection and subtitle delay direction. They required a newly built
Windows native runtime; the old bundled DLL did not certify either correction.
Native Dolby Vision and audio bitstream output are not inferred from metadata
or decoded PCM.
Complete ASS effects/font fidelity and bitstream output now have the native
source paths described in the follow-up. Windows build and actual output
acceptance were pending at that source-only checkpoint; the completed scoped
Windows results and remaining output limits are tracked by the continuation.
External paired IDX/SUB handling remains a separate file-workflow gap and is
not requested in this continuation.

## API exclusions for product decisions

The [updated API limits audit](../research/lumen-v2-updated-api-limits.md)
separates three missing normalized datasets, one delivery representation
constraint and two unconfirmed guarantees/service contracts from client work:

- Audio waveform/peak samples.
- Media-time bitrate curves and instantaneous/peak media bitrate.
- A normalized bitmap subtitle cue timeline or OCR reconstruction.
- Independent styling, position and local delay for already burned pixels.
- Event history replay and lossless/exactly-once notification guarantees.
- Closed-application third-party Windows push without a confirmed delivery contract.

Native Dolby Vision, TrueHD/DTS bitstream, complete ASS effects and paired local
VobSub files are renderer/platform boundaries, not proof of missing Emby APIs.
Offline Sync and CustomPrefs have official API foundations but are separate
product scope. Text cues, thumbnail metadata, favorites and resume queries
must not be included in the no-API list.

## Historical final-02 verification and presentation limits

The frozen `final-02` source was verified only through `ssh test-env`, Debian
13 x86_64 and SDK 10.0.301. The [execution receipt](../../artifacts/lumen-design-alignment-20261013/remote-verification/RECEIPT.md)
and [machine-readable record](../../artifacts/lumen-design-alignment-20261013/remote-verification/final/receipt.json)
retain commands, logs, source hashes, original failures and exact scope.

| Executed scope | Passed | Failed |
| --- | ---: | ---: |
| Full API project | 302 | 0 |
| Full AppState project | 222 | 0 |
| Full Playback project | 293 | 0 |
| Selected 32-class Platform Linux synthetic subset | 838 | 0 |

All 1,655 unique executed cases passed with no runner skips. Ten Windows-only
methods expand to 13 discovered cases and remain `RequiresWindows/NotRun`;
they are excluded from the passing total. No earlier passing result is reused.
The 727 code/configuration/native/XAML inputs remained unchanged across execution.
The source archive SHA256 is
`0a2c001cfb7613613a3b3f8e495efa322f90c0579b8d8f56d7a04736427079fa`.

`SupplementedCSharpSemanticOnly` compiled with zero errors using 198 current
App C# sources, actual managed references/source generators, 27 historical
generated sources and 10 field declarations derived strictly from current
XAML root names and actual SDK types. Every added declaration records its
XAML line/type/hash and SDK evidence. The supplement supplies no initialization,
handlers, bindings or method bodies. The sole warning is an obsolete Icon
member in historical generated XAML. This does not certify current XAML
regeneration or the normal WinUI/native/AOT build.

The initial attempt's runtime/compile failures are preserved. Necessary fixes
correct session extension-data deserialization, foreground Resume read behavior,
native subtitle validation after confirmed server burn-in and formal Windows
subtitle API names. Search/history assertions follow the changed canonical
design; the refresh-rate test continues to reject a 59 Hz mode and additionally
recognizes the actual compatible 60 Hz current mode. A background concurrent
Resume-removal regression verifies the required fresh read. Original race,
identity, cancellation and file-locking expectations remain intact.

At that Linux checkpoint, Windows-only file sharing, DPAPI, native build/AOT,
title bar, notifications, JumpList, rendering, display-mode restoration and hardware behavior required a
Windows verification environment. No Linux synthetic result replaces them.

Native Acrylic and text layout do not expose one-to-one controls for the
prototype's CSS blur/saturation or balance/pretty line wrapping. System
ToolTips and OS surfaces retain Windows behavior. Source alignment does not
establish pixel-perfect or physical-output acceptance.

During the original research phase, two hidden official-document browser tabs
were accidentally opened by research subtasks despite the no-computer-use
constraint. That activity was stopped;
subsequent research reads static official HTTP/source documents. No application
UI was launched or driven. The incident is not described as a successful UI
verification step.
