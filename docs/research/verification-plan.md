# Remote verification and acceptance plan

Status: **planned, not executed**. The current deliverable is documentation and implementation research; there is no application to build yet.

## Environment rule

Per the project instructions, tests, validation suites, build verification, smoke tests, and runtime probes must run through `ssh test-env` unless the user explicitly authorizes local verification for the current task. If that environment is unavailable, mark the affected verification blocked. Do not fall back to the local machine.

This task performed read-only source research and document editing. It did not attempt a test-environment connection, claim that `test-env` was available, or run API calls against a user's Emby server.

Before the first implementation trial, establish whether `test-env` provides a Windows host with an interactive desktop, the selected .NET SDK, supported Windows App SDK tooling, and usable graphics/audio devices. SSH access alone does not prove that an interactive GPU-backed WinUI test can run. A Linux-only remote host may run API/portable unit tests, but WinUI build/UI/player results must remain blocked until an appropriate remote Windows environment is available.

## Evidence to record

Record the application commit, Windows edition/build, CPU architecture, graphics/audio driver, display/HDR setting, exact NuGet/native binary versions, and server version. Keep a dedicated test account and a small authorized fixture library. Record only sanitized requests/responses and timings, with no credentials or private media paths.

| Area | Required cases | Passing evidence |
| --- | --- | --- |
| Connection | Direct URL, reverse-proxy prefix, already-suffixed API root, bad URL, unreachable endpoint | Correct server identity; no duplicated/dropped API prefix; actionable failure |
| Authentication | Correct/incorrect credentials, hidden user, saved token, revoked token, explicit logout | Correct transitions; token protection; no secret logging; no stale user context |
| Access control | Restricted libraries, disallowed transcoding, disallowed downloads | UI and requests honor policy; server denial is handled without escalating permissions |
| Browse/search | Empty and large libraries, paging, Unicode, cancellation, source without optional fields | No missing/duplicated page items; stale responses discarded; responsive lists |
| JSON contracts | Auth object shape, latest array, item query wrapper, episodes response, enum/field additions | Sanitized fixtures deserialize with forward-compatible handling |
| User state | Favorite/unfavorite, played/unplayed, failed mutation, switch user/server | Server state reflected; rollback/reconciliation; no cross-user cache leak |
| Direct media | Conservative MP4 H.264/AAC fixture, resume, range seek, authenticated stream | First frame and correct audio; seek/progress use the media timeline |
| Remux/transcode | Incompatible container/audio, bandwidth limit, forced video transcode | Negotiation chooses an allowed route; explainable fallback; server resources released |
| HLS | Protected playlist, protected segments, URL expiry, discontinuity, seek/reopen | Authentication reaches every needed request without token leakage to unrelated hosts |
| Audio tracks | Default and non-default languages, multiple audio codecs, track switching | Correct source stream index maps to the chosen player track |
| Subtitles | External SRT/WebVTT, embedded text, ASS styles, PGS/image, forced track, disabled track | Accurate declared support; conversion or burn-in when needed; correct seek offset |
| Lifecycle | Start, pause, seek, resume, natural end, stop, crash-like failure, rapid item replacement | Ordered check-ins; correct `PlaySessionId`; matching encoding/live-stream cleanup |
| Failure recovery | Network loss, server restart, expired token, invalid source, transcode failure | Bounded retries; one controlled renegotiation path; no infinite reopen loop |
| Native composition | Resize, maximize, fullscreen, dialogs over video, DPI transition, theme/high contrast | Video and native controls remain aligned; correct focus and input; no surface lifetime crash |
| Accessibility | Keyboard navigation, focus order, automation names, Narrator | Core connection, browse, and playback flows usable without a mouse |
| Packaging | Clean install, launch, upgrade, uninstall, selected architecture | Runtime/native dependencies present; no developer-machine dependency |
| Graphics/media claims | HEVC/AV1, HDR/SDR output, tone mapping, passthrough, ARM64 if promised | Evidence on each advertised hardware/runtime combination; unsupported claims omitted |

For player trials, vary one axis at a time. Begin with known-good SDR H.264/AAC media, then add codec/container, subtitle, network, and HDR complexity. A media engine working with an unauthenticated local file does not prove Emby playback works.

## Acceptance gates

### Gate 1: API contract

Manual connection, sign-in, user library listing, details, search, and resume work against the selected server versions. The source discrepancies in the [compatibility ledger](sources-and-compatibility.md) have recorded decisions for every MVP operation. Optional features remain disabled when unverified.

### Gate 2: Player proof of concept

The selected engine renders inside the native WinUI shell, plays authenticated media, supports a conservative direct profile and one server-transcoded fallback, maps tracks correctly, reports the media timeline, and releases its resources. Native overlays, DPI changes, disposal, and fullscreen work on the target remote Windows host.

### Gate 3: Usable MVP

Login, home, libraries, search, details, playback, audio/subtitle selection, continue watching, favorites, and watched state function together. A stop updates server state and the home page. Navigation and server/user switching cannot leave old playback or HTTP work active under a new identity.

### Gate 4: Release readiness

Pin dependencies and record a tested server/Windows/architecture matrix. Complete signed packaging or the chosen distribution route, component notices, license selection, crash diagnostics with redaction, keyboard/accessibility review, and update behavior. Future CI should use the approved remote Windows environment or a subsequently authorized CI service; it is not configured by this documentation task.

## Suggested focused test layers

- Portable unit tests for URL composition, query serialization, tick conversion, DTO fallback behavior, cache scoping, and playback state transitions.
- Contract tests against a real Emby test server for the selected endpoints and sanitized fixture snapshots.
- Windows integration tests for credential storage, window/media surface lifetime, engine track mapping, and package startup.
- Interactive player/accessibility trials for cases that headless tests cannot represent accurately.

Choose tests that exercise behavior and failure modes. Do not add assertions merely to duplicate DTO declarations or recheck unchanged documents. Once the selected checks pass, proceed to the next acceptance gate unless a change or new concern warrants repetition.
