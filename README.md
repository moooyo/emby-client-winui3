# Lumen: Emby-compatible Windows client

A Windows-only media client for Emby Server with the Lumen interface, using native WinUI 3 controls, WinUIEx, and CommunityToolkit.Mvvm. The project is intended for open-source distribution; the repository license is still awaiting an owner decision.

The repository contains local API documentation, an AOT-safe Emby client, playback coordination, a native library/player UI, and reproducible verification tools. The current UI is the native Lumen rewrite based on the v4 design handoff, not an embedded HTML prototype. See the [Lumen implementation and evidence boundaries](docs/implementation/lumen-ui.md) and [current handoff](HANDOFF.md). Wider media, hardware, accessibility, long-duration performance, and installed-distribution gates remain open.

The current fidelity candidate is AOT10. Its bounded design-handoff UI acceptance
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
failures are preserved. The latest human authorization restored local UI testing
and superseded the earlier VM-only route; no VM UI was ever tested. AOT10's three
owned local apps exited with actual code 0 and zero scoped crash events. Env12
cleanup released port 18984 and retained evidence. After approved elevation,
the owned worker-preparation observer, fixture, and port were also cleaned,
without desktop input or starting a new worker. No required UI/cleanup work
remains. Safe integration into `main` and push are now requested/in progress;
unrelated primary-checkout work is preserved separately, not mixed into
publication. Overall goal completion awaits successful publication. See the
[portable acceptance summary](docs/implementation/lumen-ui.md#current-acceptance-summary).

## Documentation

| Start here | Contents |
| --- | --- |
| [Lumen UI](docs/implementation/lumen-ui.md) | Native entry point, source map, implemented workflows, assets, and current verification boundaries |
| [Lumen API capabilities](docs/api/06-lumen-capabilities.md) | Discovery, collections, metadata, preferences, resume, markers, and official-server findings |
| [Current acceptance summary](docs/implementation/lumen-ui.md#current-acceptance-summary) | Portable AOT10 identity, all 15 visual states, runtime proof scope, completed owned cleanup, and publication status |
| [Historical AOT08 functional acceptance](artifacts/lumen-acceptance/final-report/ACCEPTANCE.md) | Earlier runtime identity, evidence categories, retained failures, and completed owned cleanup; not strict design-fidelity certification |
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

Initial research date: **2026-09-09**. Lumen checkpoint update: **2026-10-01**. Documentation is written in English according to the project instructions.

## Implemented development workflow

`MainWindow` composes `LumenShellPage`, which owns account/session transitions, navigation, settings, and the native player. The active experience includes an artwork-led home, movie/series/favorites walls, server-backed sorting and filters, movie/series/episode/person details, search with people and collections, real media information, and dark/light appearance settings. Library metadata and artwork come from the connected server; prototype sample titles, badges, and catalog counts are not product data.

Manual connection, protected saved accounts, playback negotiation, audio/subtitle selection, resume, progress reporting, favorites, watched state, a transient queue, and episode continuation remain integrated. The Lumen expansion adds similar items, local/remote trailers, genres, people search, hide-from-resume, collection creation/addition, metadata refresh/editing, and bounded user-configuration updates. Modern `TagItems` take presentation priority while legacy `Tags` retain their wire contract; sparse metadata edits preserve untouched raw server fields. Series quick play waits for the already-issued resume/next-up recommendation without blocking detail loading. Mutations honor endpoint-specific authorization. Emby Connect, Live TV UI, offline synchronization, and remote control are deferred.

The playback adapter uses Windows `MediaPlayer` and `MediaPlayerElement`. Its conservative profile targets SDR H.264/AAC MP4 and server-generated HLS. The custom native HUD adds chapter markers/previews, an episode drawer, source-specific playback speed, fullscreen, and compact overlay. Negotiated external WebVTT and local `.vtt` imports use the Windows text parser and an application caption layer; size, outline, placement, and delay controls cannot change subtitles burned into video. Codec availability and server transcoding permission remain explicit inputs. HDR, advanced ASS/PGS fidelity, passthrough, and universal codec support are not advertised; manual hardware-decoder and display refresh-rate controls are disabled with an explicit reason.

## Build and Native AOT

Use Windows with .NET SDK 10.0.301, Visual Studio 2026 C++ build tools, and Windows SDK 10.0.26100.0. Dependencies are pinned centrally. The application uses Windows App SDK 2.4.0, WinUIEx 2.9.3, and CommunityToolkit.Mvvm 8.4.2.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Publish-Aot.ps1
./scripts/Package.ps1
```

Debug builds use the managed development runtime; Release publishing enables Native AOT. Run `artifacts/aot/EmbyClient.App.exe` with `artifacts/aot` as the working directory. Keep the entire self-contained output folder together. `Package.ps1` packages the existing publish output and validates its structure without rebuilding it; its output is unsigned and has not passed installation or clean-machine playback testing.

The accepted scoped AOT08 executable is [EmbyClient.App.exe](artifacts/lumen-aot-candidate-08/EmbyClient.App.exe), 23,101,952 bytes. Keep its entire `artifacts/lumen-aot-candidate-08` self-contained folder together. Its exact identity and completed verification/cleanup are recorded in the [final acceptance report](artifacts/lumen-acceptance/final-report/ACCEPTANCE.md) and [machine-readable receipt](artifacts/lumen-acceptance/final-report/acceptance-summary.json).

## Research and verification status

Official Emby documentation, official SDK definitions, and upstream Windows/package documentation were read online. The initial public static API definition reports `4.1.1.0`; the Lumen contract audit uses an official SDK snapshot declaring `4.10.1.0`. Neither establishes the project's supported server range.

The task authorized local Windows verification and isolated server work through `ssh test-env`; the latest human approval restored local UI checks after the intermediate VM-only restriction. Scoped AOT10 checks and all owned test-resource cleanup have completed; the portable acceptance summary records their boundaries without requiring local artifact directories. Historical artifact links below are optional local QA records and may be absent from a public checkout. The following AOT08 results are historical, not current fidelity certification. The final AOT08 source has **919 passing Release product tests**, zero failures/skips (API 174, AppState 99, MediaTransport 125, Platform 326, Playback 195), a successful Native AOT publication with one existing generated `CS0618` warning, and 302 compared unchanged build inputs. Bootstrap 42, PowerShell safety 12, fixture HTTP 131, and earlier isolated official Emby `4.9.5.0` endpoint checks 21/21 remain separate evidence.

AOT08 passed scoped native fixture regressions at actual 628x594 and 2560x1392 captures: loaded search/grid/settings, series quick-play resume, local WebVTT import and immediately available Off, paused seeking/captions, complete chapter previews without overlap, compact-overlay caption/return, episode drawer, and normal exit. The same executable passed official Emby `4.9.5.0` native restore/search/detail/editor-prefill/cancel and real HLS playback regressions, including bright English/French cues, pause-preserving audio/subtitle selections, and unchanged selected rows across 187.297 seconds. These do not establish physically heard audio or universal subtitle fidelity. Both final apps exited normally; local fixture and owned official-backend/tunnel cleanup completed with zero remaining owned active resources and evidence retained. The consolidated outcome is `AcceptedForScopedDesignHandoff`. Earlier candidate failures remain recorded. Read the [Lumen record](docs/implementation/lumen-ui.md) before treating an area as passed. Product tests, fixture HTTP contracts, official-server APIs, native UI/decoder output, and physical audio remain separate evidence.

Earlier milestones established scoped original-video/HLS playback, burned SRT, native lifecycle/recovery, finite keyboard/focus behavior, and Native AOT/unsigned-package structure. Their executable identities and failures remain in the [historical implementation record](docs/implementation/status.md) and [V7 native report](docs/implementation/v7-native-acceptance-20260915.md). Those results are not relabeled as acceptance of the rewritten Lumen UI or a newly published candidate.

Signed installation/upgrades, clean-machine playback, broad server/device compatibility, complete accessibility, long-session resource behavior, complex subtitles, and physical output remain release gates. The progressive HTTP transport and its earlier AOT harness evidence do not establish native progressive rendering or resolve the retained ASS/PGS failures. The [research verification plan](docs/research/verification-plan.md) is historical; current authorization and result boundaries belong to each implementation checkpoint.
