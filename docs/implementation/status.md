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
| Foundation | Solution, pinned tooling/packages, native shell, local build and AOT publish | Complete; visual interaction review remains pending desktop unlock |
| Emby transport | Typed client, source-generated JSON, authentication, browsing, playback/session API, focused contract tests | In progress |
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
- Interactive screenshot/layout validation is pending because the desktop was locked. A process and accessibility tree are startup evidence, not a full visual or playback acceptance result.
- A real Emby server compatibility matrix, hardware playback claims, signing identity, and repository license remain unresolved. No credentials or private server information belong in this file.

Stage completion records will describe actual commands and outcomes, including failures and remaining gaps. Research documents remain historical source material; this file and the implemented behavior record current decisions.
