# Implementation status

Snapshot: 2026-09-09. The repository contains a functional Windows x64 development client built with .NET 10, WinUI 3, WinUIEx, and CommunityToolkit.Mvvm. Native AOT publication succeeds. Default direct/HLS playback, native isolation, and a real cold-range network retry pass their scoped checks. Actual queue, diagnostics, paused transitions, and manual recovery have UI evidence. Release acceptance remains incomplete, including large-library memory investigation, wider subtitle coverage, Windows integration, and installed distribution.

## Implemented scope

| Area | Current implementation | Acceptance boundary |
| --- | --- | --- |
| API and accounts | Typed Emby client, generated JSON, authentication, protected saved tokens, stable device identity, account switching, and logout | Automated coverage and one official server version; no Emby Connect |
| Library | Continue watching, latest items, next episodes, paged libraries/search, favorites, details, seasons/episodes, watched state, and bounded account-scoped image caching | Implemented in the native UI; final complete user-flow verification remains open |
| Playback | Windows `MediaPlayer`/`MediaPlayerElement`, source negotiation, conservative profile, direct-stream fallback, tracks, subtitles, bitrate selection, pause/resume, seeking, fullscreen, and reporting | Native and real-server evidence below covers a limited SDR H.264/AAC baseline |
| Lifecycle and controls | Single active context, cancellation and cleanup, source-specific track indexes, paused restarts, stale-event protection, scoped system media commands, editable transient queue, manual retry, local diagnostics, and episode continuation | Queue editing/continuation, diagnostic copy/save, and manual retry have actual UI evidence; physical media keys remain unverified |
| Distribution groundwork | Build/test/publish scripts, passing hosted CI, dependency inventory/SBOM tooling, unsigned MSIX packaging and structural checks | No signed release, verified package installation, or clean-machine acceptance is claimed |

The current playback path uses a private session-scoped HTTP relay for original HTTP representations, with controlled upstream authentication, redirects, ranges, bounded caching, and shutdown drainage. Server-generated HLS retains the original scoped adaptive HTTP path. One native `MediaPlayer` belongs to each engine/window; sessions borrow it and independently clear their source, event subscriptions, media controls, and transport resources. Concurrent engine disposal shares one completion and closes the owner after drainage. LibVLC remains an isolated comparison rather than a product dependency; see the [engine decision](../architecture/playback-engine-decision.md).

For the observed finite Emby VOD HLS route, the engine uses the full source timeline and performs an actual initial seek. A returned `StartTimeTicks` hint is not a reporting offset. In-place native HLS seeking is disabled after observed timeouts: a logical seek negotiates a new session, opens at the requested absolute position, and restores pause after actual playback starts. See the [playback API reference](../api/04-playback-and-sessions.md).

## Current verification

The local toolchain is .NET SDK `10.0.301`, Windows SDK `10.0.26100.0`, and Visual Studio Community 2026 `18.7.3`. Product dependencies pin Windows App SDK `2.4.0`, WinUIEx `2.9.3`, and CommunityToolkit.Mvvm `8.4.2`.

The latest development AOT executable SHA-256 is `313A94C3C7BE6821B489E49A2A7AC705617D47CC53EFEA3984641C262305D659`. Detailed UI checkpoints identify which artifact establishes each observation. Display and placement services are partial types so the C#/WinRT source generator supplies AOT-compatible interface metadata; their initial `CsWinRT1028` warnings have been resolved.

| Check | Latest result | What it establishes |
| --- | --- | --- |
| `scripts/Test.ps1 -Configuration Release` | **340 passed**: API 47, media transport 73, Windows platform 88, playback 132 | Contracts, transport boundaries and upstream failure categories, real Windows account/placement storage, display/notification ownership, cache/queue/diagnostic bounds, and coordinator recovery with test adapters |
| Release solution build | **0 errors**; a fresh build reports one upstream generated WinUIEx `Icon` warning, `CS0618` | Compilation; the warning remains visible |
| App Native AOT publish | **Succeeded** | Native code and the complete self-contained Windows App SDK payload are produced |
| Hosted Windows CI | **All steps passed** for `42011fb` in [run 34304285991](https://github.com/moooyo/emby-client-winui3/actions/runs/34304285991) | Independent locked restore, Release build, 340 tests, AOT/SBOM output, unsigned MSIX construction, and artifact uploads; later image-lifecycle investigation is separate |
| Unsigned MSIX checkpoint | **Structural verification passed** for the 313A94C3 publish; 332 payload files and 289 preserved resource keys | Byte-for-byte unpack round trip, notices and SBOM inclusion; no installed runtime claim |
| Official Emby API probe | **40 passed, 0 failed, 0 blocked** against Emby Server `4.9.5.0` | Actual authentication, browsing/user state, media ranges, HLS/WebVTT responses, reports, and cleanup; not native rendering |
| Direct relay plus system media controls | **20 default-product cycles and resource gate passed** after the network fix; handle change **+7** against limit **32**, private-memory change **-237,568 bytes** | Bounded synthetic MP4 lifecycle, 178 partial responses, and 20 SMTC retirements; physical media-key input remains separate |
| Official-server HLS | **20 functional cycles passed**, including starts at 0/17 seconds, new-session seeks to 45 seconds, pause restoration, **40 encoding cleanups**, and no diagnostics | The tested real-server logical-seek lifecycle works; resource acceptance remains separate |
| HLS resources | **20 default-product cycles and resource gate passed**; handle change **-6**, private-memory growth **720,896 bytes** | The final test uses the product owner and original managed HTTP path, with diagnostic control modes disabled; post-stop silence checks cover API requests, not packet capture |
| Native isolation | **Passed**: real opening cancellation, decoder rejection/recovery, 42 old-handler/Fail replays, 54 old-ID commands, and concurrent disposal during opening | Fault injection is explicitly identified and separate from normal 20-cycle resource runs |
| Real cold-range recovery | **NetworkRetryPassed**: actual HTTP 503 classified as NetworkFailure, a new session at 90.01 seconds, preserved pause with zero measured drift, subsequent resume, and clean retirement | The target was a failed seek's native cursor, not a decoded-frame assertion. Default/explicit track intent is retained. |
| Integrated UI | C6E8E17B verifies shared-owner paused HLS at 17 seconds, audio/version changes, queue editing/automatic consumption, diagnostics, and close during playback. 313A94C3 verifies manual retry after HTTP 503 from a three-second resume target. | Earlier 0251D189 separately verifies burned SRT and window restoration. These scoped observations do not establish every Windows/device/subtitle mode. |
| Large-library trial | The actual 313A94C3 UI browsed a 5,000-item fixture through bounded pages and repeated an already loaded region; images drained after returning home. | Private memory rose through the short run and did not establish a stable plateau. Further investigation is required; UIA observation can affect this measurement. |
| Native external WebVTT | **Passed** for one real plain cue: DirectStream, separate authenticated WebVTT, resolved platform presentation, actual screenshot confirmation at 41.03 seconds, and clean Stop/disposal/logout | The app default remains server burning. No ASS/PGS or repeated subtitle-resource claim is made. |

The native resource gate compares the final five-loop median with loops 5-9, permits at most 32 additional handles and 64 MiB of private-memory growth, and checks for sustained growth. Its limits were not relaxed. The direct and HLS results are separate measurements and must not be combined into a general stability claim.

Real-server checks used an isolated official Emby `4.9.5.0` Linux amd64 instance over loopback, dedicated test accounts, and generated media. No production credentials or user media were used. Detailed scopes are in the [server results](../../tools/EmbyClient.ServerValidation/RESULTS.md), [native probe](../../tools/EmbyClient.NativeProbe/README.md), and [capability matrix](capabilities.md).

## Remaining release gates

- Resolve large-library memory behavior and complete the remaining subtitle and integrated acceptance checks.
- Complete physical media-key, keyboard-only, Narrator, contrast, focus, DPI/multi-monitor, audio-device, and long-duration/network-failure acceptance.
- Decide the repository license and complete redistribution review. Finalize a signing identity and verify signed installation, activation, upgrades, uninstall, and playback on clean supported Windows machines. Unsigned MSIX structure checks do not satisfy these requirements.
- Establish an explicit Windows/server/media compatibility matrix. Broad codec/container coverage, HDR and Dolby formats, audio passthrough, and subtitle fidelity remain unverified.

Music specialization, persistent playlists, downloads, Live TV UI, remote control, Emby Connect, ARM64 binaries, and advanced rendering remain deferred. Existing API methods do not make a deferred area a completed user-facing feature.
