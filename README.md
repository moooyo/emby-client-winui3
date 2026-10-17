# Lumen: Emby-compatible Windows client

A Windows-only media client for Emby Server with the Lumen interface, using native WinUI 3 controls, WinUIEx, and CommunityToolkit.Mvvm. The project is intended for open-source distribution; the repository license is still awaiting an owner decision.

The repository contains local API documentation, an AOT-safe Emby client,
playback coordination, a native library/player UI, and reproducible verification
tools. The current UI follows the Lumen Windows Client V2 handoff in
`design_handoff_lumen_windows_client`, implemented with native WinUI 3 controls.
The HTML prototype is a design reference.

The [current code closeout](docs/implementation/current-code-closeout-20261017.md)
records final16 as **BuildPassed**: Release/Debug, fresh Native AOT,
package/runtime and offline schema checks have actual exit 0. The EXE is
30,613,504 bytes, SHA256
`D72B62373A3E45A972C3F900AB8D9E92540B51823121F24EA58663AC9CFC4651`;
inventories are 675/260/24/595. All 2,430 product cases and the 114-input
NativeProbe compilation reuse matching complete prior compiled closures,
without reruns for this UI-only delta.

Final16 repairs Home's temporary focus anchor and wall-scroller navigation
while retaining DataTemplate host pooling and virtualization.
**BoundedNavigationObserved** covers the short regression;
**OriginalFiniteUiWorkloadCovered_WithObservationLimit** covers formal06's
finite UI sequence, fixed idle windows and in-cap sign-out/normal exit 0.
Tail warm returned to the real first viewport without repeating its numeric
tooltip. **InvestigateHighRetention / PerformanceNotAccepted** remains the
resource result: two increments exceed the 32 MiB investigation threshold,
and library-cycle handles keep rising with attribution unresolved. Final owned
cleanup and system restoration are complete.

The user renewed unattended continuation; the earlier physical-Escape stop
and goal-service blocked receipt are historical, with no new goal-service
status claimed. **LANDeferred** still covers discovery, public Save and account
restoration. Earlier candidate results and failed/aborted attempts retain their
own identities. CI, release, licensing/signing and installation/distribution
remain outside this acceptance scope. The user separately authorized committing
and pushing the current source snapshot to `main`; source publication does not
change the recorded acceptance results.

The [bitmap subtitle fixes](docs/implementation/lumen-subtitle-fixes.md) and
completed publish-05 checkpoint below preserve their earlier source/results;
their historical 1,509-case component result is not the current project total.

The earlier [Windows continuation](docs/implementation/lumen-windows-continuation.md)
completed its local acceptance and owned cleanup within the recorded scope on
2026-10-14 under the user's Windows authorization.
That checkpoint's final candidate is `publish-05`, with a bounded correction to the
two playback-information Copy feedback calls in `PlayerView.Information.cs`.
Its [build and readback report](artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
records actual exit 0 for normal Debug/generated XAML, Native AOT, SBOM
generation, the ASS package assertion and independent payload readback. The
668-file production inventory differs from publish-04 in exactly that one
source file; all 24 native runtime files retain their native-05 identities.

Fresh publish-04 runs supplied the later desktop evidence. The
[app-04 review](artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
accepts the sampled ASS wide/fullscreen/restored and 780-pixel narrow mapping,
resume progression, the natural `1:00 / 1:00` Ended clock, twelve real BIF JPEG
cells, and the named Play/Watch again/Continue Home tooltips. The
[app-05 review](artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
establishes two native PGS cues, observed first-cue expiry, Off clearing,
recovery after an actual seek to 34 seconds, narrow/fullscreen mapping and
Back-to-Home image clearing. Paused overlapping-cue reselection did not restore
pixels immediately; wide/fullscreen controls cover some lower glyph pixels.
The 780-pixel narrow sample is unobstructed. Both
[app-04](artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json)
and [app-05](artifacts/lumen-acceptance/windows-continuation-20261013/app-05/app-exit-final.json)
have actual retained-handle exit code 0. The separate
[app-04](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app04-final-non-ui-20261014/APP04-NON-UI-AUDIT.md)
and [app-05](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app05-final-non-ui-20261014/APP05-NON-UI-AUDIT.md)
post-close audits each matched 595/595 copied payload files and completed
bounded log and timestamp/session review.

The 2,294 passing unit cases remain the publish-04 source-freeze result: 2,250
product tests plus 44 acceptance-tool tests, with zero failures or skips. They
were not rerun for publish-05. The earlier 233 HTTP fixture checks and separate
31-check synthetic PGS scope give 264 HTTP assertions across distinct frozen
execution scopes, separate from unit tests. Native audio run-02 retains four
passed PCM policy/rejection and control/cleanup cases; successful receiver
bitstream, active-carrier controls, software-volume replacement and physical
HDR remain unverified. Full PiP parity, arbitrary scripts/SAR and complete
continuous animation also retain their limits.

The [app-06 review](artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts publish-05's bottom Copy success feedback with the Close glyph clear,
feedback disappearance followed by actual panel Close, basic PGS playback at
6.2241041 seconds and `1x` in the 780-pixel narrow window, Back to detail with
24% Resume, and actual retained-handle exit 0. Clipboard contents, clicking
Close while feedback is present and the unavailable-feedback branch remain
outside this targeted check; the earlier full subtitle matrix was not rerun.
The [final non-UI audit](artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
matched both app-06 and current publish-05 payloads at 595/595, plus the complete
668-source/24-runtime set against the publish-05 baseline with no differences.

The [final owned cleanup](artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
confirms app-04/app-05/app-06 actual exits 0, fixture cleanup commands exiting 0,
all recorded owned PIDs absent and both listener ports released, with evidence
retained. Helper outcomes keep their separate identities: the initial SBOM
driver exit 1, initial 593/595 privacy-refused static-icon readback and later
permitted full readback, and outer collector exit 1 versus original launcher
exit 0 are preserved. The earlier app-03 computer-use pause and cleanup remain
historical; its actual exit and AOT03's build-process exit remain `Unknown`.
The continuation is complete within these bounded observations, with the
hardware, renderer and combination limits above retained.

The historical 2026-10-12 checkpoint completed its local Native AOT build and
scoped runtime/visual review with 1,141 passing product tests and 38 fixture
boundary checks. The subsequent
[supported-gap implementation](docs/implementation/lumen-v2-supported-gaps.md)
adds API integrations and client capabilities in source with separate remote
checks; that earlier binary is not acceptance evidence for the new source.
The [updated V2 alignment](docs/implementation/lumen-v2-updated-design.md)
follows the revised external design and records the
[API data and service limits](docs/research/lumen-v2-updated-api-limits.md).
The older binary and candidate 04's remote receipt each cover their own inputs.
The historical `final-02` source passed 1,655 unique remote cases and a bounded
C# semantic check; its 13 Windows cases retain their `RequiresWindows/NotRun`
results and its normal WinUI/XAML/native/AOT checks were unverified. See that
[execution receipt](artifacts/lumen-design-alignment-20261013/remote-verification/RECEIPT.md)
for its exact scope.
The subsequent [native playback follow-up](docs/implementation/lumen-native-playback-follow-up.md)
implements libass ASS/SSA rendering, exact-format WASAPI bitstream source paths,
BIF thumbnail images and real buffered-range seconds. It has a separate
remote verification scope; the earlier 1,655-case result does not certify these
changes. Later Windows build results and remaining actual output acceptance
belong to the continuation above.
See the [V2 review](docs/implementation/lumen-v2-review.md) and
[current handoff](HANDOFF.md) for candidate-specific evidence and remaining
Windows, media, hardware and installed-distribution gates.

## Previous v4 checkpoint

The previous v4 fidelity candidate was AOT10. Its bounded design-handoff UI acceptance
and named regression checks passed after renewed local UI authorization:
all 15 wide states were re-captured/reviewed, default movie Continue succeeded,
localized zero-position recovery was observed, and narrow captions, local VTT
import/Off, PiP/fullscreen return, and normal close passed scoped checks.
Publication and 966 product tests passed; the final recheck matched all 308
production inputs and the same executable SHA-256. This is not evidence of
frame motion, physical audio, HDR, universal codec support, or release readiness.

AOT09's 15/15 wide independent visual review found no major layout collision,
but overall acceptance did not pass: actual movie Continue failed during
Negotiating and its recovery commands were untranslated. The original captures,
review, normal exit code 0, and zero scoped crash events remain AOT09 evidence,
not AOT10 evidence. Historical AOT05/AOT07 crashes and AOT08 wrong-movie/search
failures are preserved. That v4 task's renewed human authorization restored local UI testing
and superseded the earlier VM-only route; no VM UI was ever tested. AOT10's three
owned local apps exited with actual code 0 and zero scoped crash events. Env12
cleanup released port 18984 and retained evidence. After approved elevation,
the owned worker-preparation observer, fixture, and port were also cleaned,
without desktop input or starting a new worker. No required UI/cleanup work
remained for that v4 checkpoint. Product commit `72f15ed72bcf875d5783fb61bea45a0a630a3d61`
(257 files) was safely merged into `main`, pushed, and verified on the remote.
Hosted CI was in progress when the production push was verified; a green CI result is not part of this acceptance.
Unrelated primary-checkout work remains separately preserved and was not mixed
into publication. The scoped implementation, acceptance, cleanup, and publication
are complete. See the
[portable acceptance summary](docs/implementation/lumen-ui.md#current-acceptance-summary).

## Documentation

Archived artifact paths below are preserved as literal references. These
historical artifacts are not materialized in this checkout and cannot be
independently reread here. Their reported results retain their original scope;
this link repair does not regenerate or independently reverify the old reports.

| Start here | Contents |
| --- | --- |
| [Windows continuation](docs/implementation/lumen-windows-continuation.md) | Current candidate provenance, frozen test scopes, scoped native subtitle/UI results, output limits and final receipts |
| [Updated V2 alignment](docs/implementation/lumen-v2-updated-design.md) | Revised external design, source changes and historical final-02 verification |
| [Native playback follow-up](docs/implementation/lumen-native-playback-follow-up.md) | ASS/SSA and bitstream source paths, BIF delivery, buffered duration and separate verification boundaries |
| [Updated API limits](docs/research/lumen-v2-updated-api-limits.md) | Missing data, delivery constraints, unconfirmed service guarantees and conditional supported capabilities |
| [Lumen V2 review](docs/implementation/lumen-v2-review.md) | Historical 2026-10-12 handoff, implementation inventory, visual review and candidate-specific verification |
| [Supported-gap follow-up](docs/implementation/lumen-v2-supported-gaps.md) | API integration, persistence, Windows behavior, update workflow and historical candidate-04 scope |
| [Earlier Lumen UI](docs/implementation/lumen-ui.md) | Previous v4 source map, workflows, assets, and candidate-specific evidence |
| [Lumen API capabilities](docs/api/06-lumen-capabilities.md) | Discovery, collections, metadata, preferences, resume, markers, and official-server findings |
| [v4 acceptance summary](docs/implementation/lumen-ui.md#current-acceptance-summary) | Historical AOT10 identity, all 15 visual states, runtime proof scope, completed owned cleanup, and publication status |
| Historical AOT08 functional acceptance: `artifacts/lumen-acceptance/final-report/ACCEPTANCE.md` | Archived local QA record: earlier runtime identity, evidence categories, retained failures, and completed owned cleanup; not strict design-fidelity certification |
| [API guide](docs/api/README.md) | Feature scope, API groups, priorities, and reading order |
| [Windows implementation plan](docs/architecture/windows-client-plan.md) | Technology choices, native UI, playback engines, architecture, packaging, and milestones |
| [Sources and compatibility notes](docs/research/sources-and-compatibility.md) | Research provenance, conflicting official definitions, and unresolved compatibility questions |
| [Remote verification plan](docs/research/verification-plan.md) | The evidence required before claiming server, player, or Windows compatibility |
| [Earlier user guide](docs/implementation/user-guide.md) | Previous UI workflows; consult the Lumen document for the active interface |
| [Playback engine decision](docs/architecture/playback-engine-decision.md) | Measured native and LibVLC integration evidence |
| [Progressive HTTP transport](docs/implementation/progressive-streaming.md) | Bounded streaming, framing and lifetime rules, and the unverified native profile control |
| [Windows CI](docs/implementation/ci.md) | Locked restore, tests, Native AOT publishing, and artifact limits |
| [Official-server protocol CI](tools/EmbyClient.ServerValidation/Ci/README.md) | Manual disposable-server checks, isolated loopback access, safe summaries, and cleanup |
| [MSIX packaging](docs/implementation/packaging.md) | Unsigned package creation, resource preservation, and release gates |
| [Update policy](docs/implementation/update-policy.md) | Development replacement, installed-release plans, data compatibility, and servicing |
| [Historical implementation record](docs/implementation/status.md) | Earlier development and V7 acceptance checkpoints, with their original candidate identities |
| [Historical capability matrix](docs/implementation/capabilities.md) | Earlier measured behavior and explicit unsupported or unverified areas |
| [Dependency inventory](docs/implementation/dependency-inventory.md) | Exact dependencies, upstream license metadata, and preserved notices |
| [CycloneDX SBOM](docs/implementation/sbom.md) | Reproducible package inventory and schema validation |

Initial research date: **2026-09-09**. Current V2 documentation update: **2026-10-17**. Documentation is written in English according to the project instructions.

## Implemented development workflow

`MainWindow` composes `LumenShellPage`, which owns account/session transitions, navigation, settings, and the native player. The V2 implementation adds full-page home/detail navigation, API-driven library tabs and overflow, back/forward history, public-account selection, recent searches, six settings sections, custom native caption regions, and extended player interactions. Library metadata and artwork come from the connected server; prototype sample titles, badges, and catalog counts are not product data. Implementation presence and verified behavior are tracked separately in the [V2 review](docs/implementation/lumen-v2-review.md).

Manual connection, protected saved accounts, playback negotiation, audio/subtitle selection, resume, progress reporting, favorites, watched state, a transient queue, and episode continuation remain integrated. The Lumen expansion adds similar items, local/remote trailers, genres, people search, hide-from-resume, collection creation/addition, metadata refresh/editing, and bounded user-configuration updates. Modern `TagItems` take presentation priority while legacy `Tags` retain their wire contract; sparse metadata edits preserve untouched raw server fields. Series quick play waits for the already-issued resume/next-up recommendation without blocking detail loading. Mutations honor endpoint-specific authorization. Emby Connect, Live TV UI, offline synchronization, and remote control are deferred.

The playback adapter uses a FFmpegInteropX source fork with configurable `Auto`, D3D11VA, Intel VPL/QSV, AMD AMF, NVIDIA NVDEC, and software decoding, while Windows `MediaPlayer`/`MediaPlayerElement` presents video. Explicit vendor modes use real `*_qsv`, `*_amf`, and `*_cuvid` decoders; the initial vendor path transfers hardware frames into CPU-visible samples for the existing presenter. Diagnostics distinguish the requested API from the decoder, frame counts, decoder GPU, and software-fallback reason. The conservative profile targets SDR H.264/AAC MP4 and server-generated HLS, with conditional native output capabilities. The custom HUD adds chapter previews, an episode drawer, playback speed, fullscreen, and compact overlay. External WebVTT and local `.vtt`/`.srt` captions use the application caption layer. Complete original ASS/SSA scripts and embedded packets use the separately pinned libass renderer. Native bitstream source paths use exact-format WASAPI with PCM recovery; buffered duration comes from Windows ranges. These source paths retain the existing video/HDR presenter. Their current Windows build receipts, scoped subtitle pixels and remaining physical-output gates belong to the [continuation](docs/implementation/lumen-windows-continuation.md). External binary VobSub pair import, native Dolby Vision and universal codec/device compatibility remain unresolved. See the [native follow-up](docs/implementation/lumen-native-playback-follow-up.md), [decoder API record](docs/implementation/decoder-api-backends.md) and [hardware configuration](docs/implementation/hardware-decoding.md). The earlier [D3D11 validation](docs/implementation/hardware-decoding-validation-20261001.md) retains its measured scope; it does not establish three-vendor API acceptance. AMD hardware acceptance requires an AMD device. The [final decoder API validation](docs/implementation/decoder-api-validation-20261001.md) records actual Intel/NVIDIA decoding and the native selector UI.

## Build and Native AOT

Use Windows with .NET SDK 10.0.301, Visual Studio 2026 C++ build tools, and Windows SDK 10.0.26100.0. Dependencies are pinned centrally. The application uses Windows App SDK 2.4.0, WinUIEx 2.9.3, and CommunityToolkit.Mvvm 8.4.2.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Publish-Aot.ps1
./scripts/Package.ps1
```

Debug builds use the managed development runtime; Release publishing enables Native AOT. Run `artifacts/aot/EmbyClient.App.exe` with `artifacts/aot` as the working directory. Keep the entire self-contained output folder together. `Package.ps1` packages the existing publish output and validates its structure without rebuilding it; its output is unsigned and has not passed installation or clean-machine playback testing.

The historical v4 scoped AOT08 record identifies the archived executable at
`artifacts/lumen-aot-candidate-08/EmbyClient.App.exe`, 23,101,952 bytes. Its
original self-contained bundle must stay together. The recorded identity and
completed verification/cleanup refer to the archived report at
`artifacts/lumen-acceptance/final-report/ACCEPTANCE.md` and receipt at
`artifacts/lumen-acceptance/final-report/acceptance-summary.json`. These
historical artifacts are not materialized in this checkout and cannot be
independently reread here. The recorded candidate is not a V2 candidate.

## Research and verification status

Official Emby documentation, official SDK definitions, and upstream Windows/package documentation were read online. The initial public static API definition reports `4.1.1.0`; the Lumen contract audit uses an official SDK snapshot declaring `4.10.1.0`. Neither establishes the project's supported server range.

The 2026-10-12 V2 checkpoint authorized local Windows build, runtime, and visual review after implementation. Its candidate-specific results belong to the [V2 review](docs/implementation/lumen-v2-review.md). The original updated 2026-10-13 alignment task restricted verification to `ssh test-env` and did not authorize local tests, builds, runtime probes or computer use. The later [Windows continuation](docs/implementation/lumen-windows-continuation.md) explicitly authorizes those local checks and records their current results separately. The previous v4 task authorized local Windows verification and isolated server work through `ssh test-env`; its scoped AOT10 checks and owned cleanup completed. Historical artifact links below are optional local QA records and may be absent from a public checkout. These AOT08 results are historical, not V2 fidelity certification. The final AOT08 source has **919 passing Release product tests**, zero failures/skips (API 174, AppState 99, MediaTransport 125, Platform 326, Playback 195), a successful Native AOT publication with one existing generated `CS0618` warning, and 302 compared unchanged build inputs. Bootstrap 42, PowerShell safety 12, fixture HTTP 131, and earlier isolated official Emby `4.9.5.0` endpoint checks 21/21 remain separate evidence.

AOT08 passed scoped native fixture regressions at actual 628x594 and 2560x1392 captures: loaded search/grid/settings, series quick-play resume, local WebVTT import and immediately available Off, paused seeking/captions, complete chapter previews without overlap, compact-overlay caption/return, episode drawer, and normal exit. The same executable passed official Emby `4.9.5.0` native restore/search/detail/editor-prefill/cancel and real HLS playback regressions, including bright English/French cues, pause-preserving audio/subtitle selections, and unchanged selected rows across 187.297 seconds. These do not establish physically heard audio or universal subtitle fidelity. Both final apps exited normally; local fixture and owned official-backend/tunnel cleanup completed with zero remaining owned active resources and evidence retained. The consolidated outcome is `AcceptedForScopedDesignHandoff`. Earlier candidate failures remain recorded. Read the [Lumen record](docs/implementation/lumen-ui.md) before treating an area as passed. Product tests, fixture HTTP contracts, official-server APIs, native UI/decoder output, and physical audio remain separate evidence.

Earlier milestones established scoped original-video/HLS playback, burned SRT, native lifecycle/recovery, finite keyboard/focus behavior, and Native AOT/unsigned-package structure. Their executable identities and failures remain in the [historical implementation record](docs/implementation/status.md) and [V7 native report](docs/implementation/v7-native-acceptance-20260915.md). Those results are not relabeled as acceptance of the rewritten Lumen UI or a newly published candidate.

Signed installation/upgrades, clean-machine playback, broad server/device compatibility, complete accessibility, long-session resource behavior, complex subtitles, and physical output remain release gates. The progressive HTTP transport and its earlier AOT harness evidence remain separate from the continuation's later scoped ASS/PGS image acceptance. Historical failed candidates retain their identities. The [research verification plan](docs/research/verification-plan.md) is historical; current authorization and result boundaries belong to each implementation checkpoint.
