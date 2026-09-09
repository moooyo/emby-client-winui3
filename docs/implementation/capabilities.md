# Capability and verification matrix

Snapshot: 2026-09-09. This matrix describes a development build, not a completed release or universal compatibility promise. API responses, compilation, native clock progress, visible pixels, and output-device behavior establish different facts.

## Evaluated environment

- Windows 11 Enterprise `10.0.28000`, x64.
- .NET SDK `10.0.301`, Windows SDK `10.0.26100.0`, Windows App SDK `2.4.0`, WinUIEx `2.9.3`, and CommunityToolkit.Mvvm `8.4.2`.
- An isolated official Emby Server `4.9.5.0` Linux amd64 instance accessed over loopback, with dedicated test accounts and no production media.
- Generated SDR H.264/AAC MP4 clips with changing color frames and a sine-wave audio track; generated SRT cues for subtitle checks. Installed hardware and codecs do not establish which decoder, GPU, or physical output handled a session.

## Current evidence

| Capability | Evidence | Limit |
| --- | --- | --- |
| Build and Native AOT | Release solution build has zero errors and one upstream generated WinUIEx `Icon` `CS0618` warning on a fresh build; App AOT publication succeeds. | Publication does not prove native rendering or clean-machine execution. |
| Automated checks | Unified Release run: **288 passed** across API 47, media transport 65, Windows platform 68, and playback 108. | Most HTTP/engine checks use test adapters; these are not a broad decoder or server certification. |
| Authentication and user state | 47 API tests and 40 actual official-server API checks; earlier UI sign-in and browsing observations. | One server version. Emby Connect is not implemented. |
| Protected account persistence | 33 account/storage tests exercise actual Windows user-scoped protection, atomic settings, and connection/logout races. | No roaming or portable credential-store claim. |
| Window and UI ownership | 35 additional tests cover window-placement storage, display-request ownership, dispatched playback ownership, and image-cache boundaries. | Geometry restoration and actual display sleep still need final interactive verification; display policy tests use a fake platform request. |
| Library and queue UI | Paged browsing/search, home sections, favorites, details, watched state, transient queue, and episode continuation are implemented. | Complete final user-flow acceptance remains open. A transient queue is not a persistent Emby playlist. |
| Negotiation and lifecycle | 108 coordinator tests cover absolute timelines, fallback, permissions, cancellation, cleanup, paused restarts, source-specific indexes, scoped commands, and queued-event protection. | Simulated engines do not prove decoder behavior; legitimate backward seeks remain allowed. |
| Original MP4 delivery | Authenticated ranges, native clock/dimensions, and earlier visible changing frames in the application; direct relay plus SMTC passes 20 functional cycles. | Limited synthetic SDR H.264/AAC baseline, not broad codec or long-duration evidence. |
| Direct-path resources | The 20-cycle relay/SMTC run passes the unchanged resource gate: **+30 handles** against limit **32**, **+847,872 private bytes**. | Separate from real HLS resource behavior. |
| Real-server HLS lifecycle | 20 functional cycles pass: 0/17-second starts, new-session seeks to 45 seconds, restored pause, 40 encoding cleanups, and no diagnostics. | **Product resource gate remains failed**: +52 initially and +70 after explicit creation-response ownership, against limit 32. An isolated shared-player/native-HTTP control passed at +3; product integration is still pending. |
| HLS seeking | Finite Emby VOD HLS uses a full source timeline and an actual initial seek. Logical seeking opens a new negotiated session and preserves pause. | Native in-place HLS seek is disabled after observed timeouts. Other server versions and unknown/infinite timelines are not certified. |
| Subtitles | API probe verifies SRT-to-WebVTT delivery; earlier application runs visibly display server-burned SRT in HLS. | Server burning is the default. Native external timed text is not enabled by default; styled ASS/fonts and PGS remain unverified. |
| System media controls | Play/Pause/Stop/seek commands are routed through playback-ID-scoped coordinator methods. The direct 20-cycle run inspects Playing, Paused, and retired control states. | The test tool does not support physical media-key input; it remains unverified. System next/previous commands are not advertised. |
| Integrated UI and fullscreen | Earlier native UI runs showed the library, original/transcoded video, subtitles, and fullscreen entry/Escape exit. | Final integrated UI validation was interrupted by Escape and remains **incomplete**. |
| Accessibility | Native controls expose names through UI Automation. | Narrator, contrast, keyboard-only, focus, and multi-monitor DPI acceptance are incomplete. |
| Packaging | Unsigned MSIX structure, complete resource-map preservation, MakeAppx checks, and payload hashes have been checked. | Repository license/redistribution review, final signing identity, installation, packaged activation, upgrades, uninstall, and clean-machine playback remain release gates. |

The resource criteria compare the final five-loop median with loops 5-9, allow at most 32 additional handles and 64 MiB of private-memory growth, and reject sustained growth. Passing the HLS functional checks does not override its failed handle criterion. See [implementation status](status.md) for the current overall result.

## Explicitly unverified or deferred

Windows 10 and other Windows 11 builds, a multi-version Emby support range, production reverse proxies, long-duration/large-media playback, and network-loss recovery need independent evidence. The final UI run, physical media keys, audible output, audio-device switching, multichannel output, audio passthrough, HDR passthrough and color accuracy, Dolby Vision, broad codecs/containers, styled ASS with embedded fonts, and PGS are not certified capabilities.

The application advertises a conservative effective profile. An installed codec package, GPU model, or successful metadata query is insufficient reason to expand it. Music specialization, persistent playlists, Live TV UI, remote control, offline downloads, Emby Connect, ARM64 releases, and advanced rendering remain deferred.

See the [native lifecycle probe](../../tools/EmbyClient.NativeProbe/README.md), [actual server results](../../tools/EmbyClient.ServerValidation/RESULTS.md), [engine decision](../architecture/playback-engine-decision.md), and [user guide](user-guide.md). Update claims only after the corresponding check completes; preserve failed results and their scope.
