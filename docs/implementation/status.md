# Implementation status

Snapshot: 2026-09-09. The repository contains a functional Windows x64 development client built with .NET 10, WinUI 3, WinUIEx, and CommunityToolkit.Mvvm. Native AOT publication succeeds. Release acceptance is incomplete: the real-server HLS resource gate still fails, and final integrated UI validation was interrupted by Escape before completion.

## Implemented scope

| Area | Current implementation | Acceptance boundary |
| --- | --- | --- |
| API and accounts | Typed Emby client, generated JSON, authentication, protected saved tokens, stable device identity, account switching, and logout | Automated coverage and one official server version; no Emby Connect |
| Library | Continue watching, latest items, next episodes, paged libraries/search, favorites, details, seasons/episodes, watched state, and bounded account-scoped image caching | Implemented in the native UI; final complete user-flow verification remains open |
| Playback | Windows `MediaPlayer`/`MediaPlayerElement`, source negotiation, conservative profile, direct-stream fallback, tracks, subtitles, bitrate selection, pause/resume, seeking, fullscreen, and reporting | Native and real-server evidence below covers a limited SDR H.264/AAC baseline |
| Lifecycle and controls | Single active context, cancellation and cleanup, source-specific track indexes, paused restarts, stale-event protection, scoped system media commands, transient queue, and episode continuation | System-control state inspection passed; physical media keys and complete queue/continuation UI acceptance remain unverified |
| Distribution groundwork | Build/test/publish scripts, passing hosted CI, dependency inventory/SBOM tooling, unsigned MSIX packaging and structural checks | No signed release, verified package installation, or clean-machine acceptance is claimed |

The current playback path uses a private session-scoped HTTP relay for original HTTP representations, with controlled upstream authentication, redirects, ranges, bounded caching, and shutdown drainage. Server-generated HLS uses the native adaptive-media path. LibVLC remains an isolated comparison rather than a product dependency; see the [engine decision](../architecture/playback-engine-decision.md).

For the observed finite Emby VOD HLS route, the engine uses the full source timeline and performs an actual initial seek. A returned `StartTimeTicks` hint is not a reporting offset. In-place native HLS seeking is disabled after observed timeouts: a logical seek negotiates a new session, opens at the requested absolute position, and restores pause after actual playback starts. See the [playback API reference](../api/04-playback-and-sessions.md).

## Current verification

The local toolchain is .NET SDK `10.0.301`, Windows SDK `10.0.26100.0`, and Visual Studio Community 2026 `18.7.3`. Product dependencies pin Windows App SDK `2.4.0`, WinUIEx `2.9.3`, and CommunityToolkit.Mvvm `8.4.2`.

The final AOT executable SHA-256 for this checkpoint is `803C17E0937E2196A127A72286C1962F563A0C0A323B3D4566DDA9A667FA4EDA`. This identifies the published artifact; it does not mark the interrupted UI check as passed.

| Check | Latest result | What it establishes |
| --- | --- | --- |
| `scripts/Test.ps1 -Configuration Release` | **253 passed**: API 47, media transport 65, Windows platform 33, playback 108 | Contracts, transport boundaries, actual Windows user-scoped storage, and coordinator behavior with test adapters |
| Release solution build | **0 errors**; a fresh build reports one upstream generated WinUIEx `Icon` warning, `CS0618` | Compilation; the warning remains visible |
| App Native AOT publish | **Succeeded** | Native code and the complete self-contained Windows App SDK payload are produced |
| Hosted Windows CI | **All steps passed** for `b0de519` in [run 34296241270](https://github.com/moooyo/emby-client-winui3/actions/runs/34296241270) | Independent locked restore, Release build, 253 tests, AOT/SBOM output, and artifact upload; later edits require another run |
| Unsigned MSIX checkpoint | **Structural verification passed**; 332 payload files and 287 preserved resource keys | Byte-for-byte unpack round trip, notices and SBOM inclusion; no installed runtime claim |
| Official Emby API probe | **40 passed, 0 failed, 0 blocked** against Emby Server `4.9.5.0` | Actual authentication, browsing/user state, media ranges, HLS/WebVTT responses, reports, and cleanup; not native rendering |
| Direct relay plus system media controls | **20 functional cycles and resource gate passed**; handle growth **+30** against limit **32**, private-memory growth **847,872 bytes** | Bounded synthetic MP4 lifecycle and media-control Playing/Paused/retired state inspection; physical media-key input was not supported by the test tool |
| Official-server HLS | **20 functional cycles passed**, including starts at 0/17 seconds, new-session seeks to 45 seconds, pause restoration, **40 encoding cleanups**, and no diagnostics | The tested real-server logical-seek lifecycle works; resource acceptance remains separate |
| HLS resources | **Failed**: handle growth **+52** exceeds limit **32**; private-memory growth approximately **4.51 MB** | The handle-growth issue remains under investigation; functional success does not complete this gate |
| Integrated UI | Earlier runs displayed original video, HLS conversion, burned-in SRT, and fullscreen entry/exit | The final run was interrupted by Escape and is **incomplete**; earlier observations do not certify the final build |

The native resource gate compares the final five-loop median with loops 5-9, permits at most 32 additional handles and 64 MiB of private-memory growth, and checks for sustained growth. Its limits were not relaxed. The direct and HLS results are separate measurements and must not be combined into a general stability claim.

Real-server checks used an isolated official Emby `4.9.5.0` Linux amd64 instance over loopback, dedicated test accounts, and generated media. No production credentials or user media were used. Detailed scopes are in the [server results](../../tools/EmbyClient.ServerValidation/RESULTS.md), [native probe](../../tools/EmbyClient.NativeProbe/README.md), and [capability matrix](capabilities.md).

## Remaining release gates

- Resolve and repeat the real HLS resource gate, then complete final integrated UI verification, including paused source/track changes, queue continuation, and session transitions.
- Complete physical media-key, keyboard-only, Narrator, contrast, focus, DPI/multi-monitor, audio-device, and long-duration/network-failure acceptance.
- Decide the repository license and complete redistribution review. Finalize a signing identity and verify signed installation, activation, upgrades, uninstall, and playback on clean supported Windows machines. Unsigned MSIX structure checks do not satisfy these requirements.
- Establish an explicit Windows/server/media compatibility matrix. Broad codec/container coverage, HDR and Dolby formats, audio passthrough, and subtitle fidelity remain unverified.

Music specialization, persistent playlists, downloads, Live TV UI, remote control, Emby Connect, ARM64 binaries, and advanced rendering remain deferred. Existing API methods do not make a deferred area a completed user-facing feature.
