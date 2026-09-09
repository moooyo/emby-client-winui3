# Emby-compatible Windows client

A Windows-only media client for Emby Server, using native WinUI 3 controls, WinUIEx, and CommunityToolkit.Mvvm. The project is intended for open-source distribution; the repository license is still awaiting an owner decision.

The repository contains local API documentation, an AOT-safe Emby client, playback coordination, a native library/player UI, and reproducible verification tools. Development is in progress; see the [current stage and verification evidence](docs/implementation/status.md). HLS resource ownership, final interactive acceptance, and formal distribution gates remain open.

## Documentation

| Start here | Contents |
| --- | --- |
| [API guide](docs/api/README.md) | Feature scope, API groups, priorities, and reading order |
| [Windows implementation plan](docs/architecture/windows-client-plan.md) | Technology choices, native UI, playback engines, architecture, packaging, and milestones |
| [Sources and compatibility notes](docs/research/sources-and-compatibility.md) | Research provenance, conflicting official definitions, and unresolved compatibility questions |
| [Remote verification plan](docs/research/verification-plan.md) | The evidence required before claiming server, player, or Windows compatibility |
| [User guide](docs/implementation/user-guide.md) | Account management, browsing, playback controls, queue, and current limits |
| [Playback engine decision](docs/architecture/playback-engine-decision.md) | Measured native and LibVLC integration evidence |
| [Windows CI](docs/implementation/ci.md) | Locked restore, tests, Native AOT publishing, and artifact limits |
| [MSIX packaging](docs/implementation/packaging.md) | Unsigned package creation, resource preservation, and release gates |
| [Capability matrix](docs/implementation/capabilities.md) | Measured behavior and explicit unsupported or unverified areas |
| [Dependency inventory](docs/implementation/dependency-inventory.md) | Exact dependencies, upstream license metadata, and preserved notices |
| [CycloneDX SBOM](docs/implementation/sbom.md) | Reproducible package inventory and schema validation |

Research date: **2026-09-09**. Documentation is written in English according to the project instructions.

## Implemented development workflow

The current application supports manual server connection, protected saved accounts, library browsing, search, movie and episode details, playback negotiation, audio/subtitle selection, resume, progress reporting, favorites, watched state, a transient queue, and automatic episode continuation. Emby Connect, Live TV UI, offline synchronization, and remote control are deferred.

The primary playback adapter uses Windows MediaPlayer and MediaPlayerElement. Its conservative profile targets SDR H.264/AAC MP4 and server-generated HLS; subtitles use server burning by default. Codec availability and server transcoding permission remain explicit negotiation inputs. HDR, advanced subtitle fidelity, passthrough, and universal codec support are not advertised.

## Build and Native AOT

Use Windows with .NET SDK 10.0.301, Visual Studio 2026 C++ build tools, and Windows SDK 10.0.26100.0. Dependencies are pinned centrally. The application uses Windows App SDK 2.4.0, WinUIEx 2.9.3, and CommunityToolkit.Mvvm 8.4.2.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Publish-Aot.ps1
./scripts/Package.ps1
```

Debug builds use the managed development runtime; Release publishing enables Native AOT. Run `artifacts/aot/EmbyClient.App.exe` with `artifacts/aot` as the working directory. Keep the entire self-contained output folder together. `Package.ps1` packages the existing publish output and validates its structure without rebuilding it; its output is unsigned and has not passed installation or clean-machine playback testing.

## Research and verification status

Official Emby documentation, official SDK definitions, and upstream Windows/package documentation were read online. The public static Emby API definition reports version `4.1.1.0`; the newer official SDK definition does not declare a server version. Neither establishes the project's supported server range.

The user has explicitly authorized local builds and tests for the implementation task. Debug builds, actual Native AOT publication, and native rendering have succeeded. An isolated official Emby 4.9.5.0 server passed 40 API checks; actual original video, HLS transcoding, and visible burned-in SRT subtitles have also been inspected in the application. These are separate forms of evidence and do not establish a broad server or hardware support matrix.

All 288 focused tests pass. The product's session-scoped HTTP relay, including system media control state and retirement, passes 20 native direct-play lifecycle cycles and the unchanged resource criteria. Real-server HLS passes 20 functional cycles with actual initial seeking, paused restart at 45 seconds, and server cleanup, but its handle-growth gate still fails. Final pixels and physical media commands remain separate acceptance work. The earlier [research verification plan](docs/research/verification-plan.md) is historical; the [implementation record](docs/implementation/status.md) tracks current authorization and results.
