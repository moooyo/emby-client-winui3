# Implementation status

Implementation started on 2026-09-09. The user authorized development directly on `main`, a commit and push after each completed stage, and local builds/tests for this implementation task. This authorization supersedes the research-stage remote-only restriction for the current task.

## Required implementation direction

- .NET 10 and the latest stable Windows App SDK, currently 2.4.0.
- A native Windows UI built with WinUI 3, WinUIEx, and CommunityToolkit.Mvvm.
- Native AOT as a verified publish target, with explicit JSON source generation and no reflection-dependent application architecture.
- Implement the researched connection, library, user-state, and playback lifecycle, followed by playback refinements and release hardening.
- Keep optional scope and unresolved release requirements visible; a scaffold or a passing isolated test is not completion of the product.

## Delivery stages

| Stage | Scope | State |
| --- | --- | --- |
| Research baseline | Local API reference, compatibility research, Windows architecture | Complete; initial commit |
| Foundation | Solution, pinned tooling/packages, native shell, local build and AOT publish | Complete; initial native window visually inspected after desktop unlock |
| Emby transport | Typed client, source-generated JSON, authentication, browsing, playback/session API, focused contract tests | Complete; 40 HTTP contract tests passed |
| Windows account storage | User-protected tokens, atomic account settings, stable device identity | Complete; 30 Windows platform tests passed |
| Playback orchestration | Engine abstraction, negotiation, state transitions, reporting, fallback, source cleanup | Complete; 39 coordinator tests passed using simulated transport/engine |
| Usable client | Protected accounts, native navigation, home/library/search/details, playback surface, tracks, reporting and cleanup | Pending |
| Playback refinements | Queue, episode continuation, quality/source selection, failure recovery, media keys, engine comparison | Pending |
| Release hardening | Accessibility, cache policy, automated checks, packaging, distribution evidence, accurate compatibility documentation | Pending |
| Optional extensions | Music specialization, playlists, downloads, Live TV, remote control, ARM64 | Deferred as in the research plan; each needs separate complete lifecycle verification |

## Verification evidence

- Local SDK: .NET SDK 10.0.301; Visual Studio Community 2026 18.7.3; Windows SDK 10.0.26100.0.
- Latest stable package metadata inspected: Windows App SDK 2.4.0, WinUIEx 2.9.3, CommunityToolkit.Mvvm 8.4.2.
- `dotnet build src/EmbyClient.App/EmbyClient.App.csproj -c Debug -p:Platform=x64 --nologo`: succeeded.
- `dotnet publish src/EmbyClient.App/EmbyClient.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts/aot --nologo`: succeeded and generated native code.
- Native output contains an 8,412,160-byte `EmbyClient.App.exe` for the initial shell. Process launch with the publish directory as its working directory produced a responsive window and the expected welcome-page accessibility tree.
- The initial build reports an upstream generated-XAML `CS0618` warning for WinUIEx's obsolete `Icon` type. Application code uses `AppWindow.SetIcon`; the warning is not suppressed.
- After the user unlocked the desktop, the initial welcome window was visually inspected: native title bar, Mica surface, welcome text, and layout were displayed. This does not establish playback acceptance.
- `dotnet build src/EmbyClient.Api/EmbyClient.Api.csproj -c Release`: succeeded, 0 warnings/errors.
- `dotnet test --project tests/EmbyClient.Api.Tests/EmbyClient.Api.Tests.csproj --configuration Release --no-restore`: 40 passed, 0 failed, 0 skipped. Tests use simulated HTTP; they do not certify an actual Emby server version.
- Protocol regressions fixed during tests: preserving proxy paths in returned `/emby/...` media URLs, rejecting unknown query-result shapes, and preserving numeric genre/studio IDs. The .NET 10 test runner is Microsoft.Testing.Platform.
- `dotnet test --project tests/EmbyClient.Platform.Tests/EmbyClient.Platform.Tests.csproj --configuration Release`: 30 passed, 0 failed, 0 skipped. These tests exercised actual Windows user-scoped data protection in isolated temporary directories, not production account settings.
- Initial settings creation now uses a non-overwriting atomic move. Competing instances read the winning persisted device identity instead of overwriting it or returning different identities.
- `dotnet test --project tests/EmbyClient.Playback.Tests/EmbyClient.Playback.Tests.csproj --configuration Release`: 39 passed with no build warnings. The suite verifies real coordinator behavior with a simulated engine/HTTP transport, including ordering barriers, cancellation races, fallback limits, known HDR rejection from the direct baseline, absolute timestamps, and cleanup after failures.
- The native player integration and full client compiled and published with Native AOT. Native rendering, streaming, and subtitle behavior are a separate acceptance gate and are not proved by coordinator unit tests.

## Official server compatibility correction

An isolated official Emby Server 4.9.5.0 instance was started inside a WSL user/network namespace with loopback-only forwarding. It uses only generated test media and dedicated test accounts. No existing Emby instance, system network configuration, or production credentials were used.

The real server rejected chunked JSON request bodies with HTTP 400. The transport now serializes requests through the existing generated `JsonTypeInfo` into UTF-8 bytes and sends a known `Content-Length`. This preserves Native AOT compatibility. Seven regressions check the length before a handler reads/buffers the body; all seven failed against the old implementation.

- API test suite after correction: 47 passed, 0 failed, 0 skipped.
- Native AOT API probe against official Emby 4.9.5.0: 40 passed, 0 failed, 0 blocked. Coverage includes actual sign-in, library/user-state operations, original HTTP ranges, HLS manifests and a segment, WebVTT delivery, session reports, encoding cleanup, and logout invalidation.
- This API probe is not native video rendering evidence. Native UI and sustained player resource checks remain in progress.
- A real Emby server compatibility matrix, hardware playback claims, signing identity, and repository license remain unresolved. No credentials or private server information belong in this file.

Stage completion records will describe actual commands and outcomes, including failures and remaining gaps. Research documents remain historical source material; this file and the implemented behavior record current decisions.
