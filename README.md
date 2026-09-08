# Emby-compatible Windows client

An open-source, Windows-only media client for Emby Server, using native WinUI 3 controls, WinUIEx, and selected Windows Community Toolkit packages.

The repository contains the API research and an initial native Windows application. Implementation is in progress; see the [current stage and verification evidence](docs/implementation/status.md). No server compatibility matrix or project license has been finalized.

## Documentation

| Start here | Contents |
| --- | --- |
| [API guide](docs/api/README.md) | Feature scope, API groups, priorities, and reading order |
| [Windows implementation plan](docs/architecture/windows-client-plan.md) | Technology choices, native UI, playback engines, architecture, packaging, and milestones |
| [Sources and compatibility notes](docs/research/sources-and-compatibility.md) | Research provenance, conflicting official definitions, and unresolved compatibility questions |
| [Remote verification plan](docs/research/verification-plan.md) | The evidence required before claiming server, player, or Windows compatibility |

Research date: **2026-09-09**. Documentation is written in English according to the project instructions.

## Proposed first release

Support manual server connection, user sign-in, library browsing, search, movie and episode details, playback negotiation, audio/subtitle selection, resume, progress reporting, favorites, and watched state. Keep the interface native to Windows. Introduce Emby Connect, Live TV, offline synchronization, and remote control after the core playback path is reliable.

Use a small typed Emby HTTP client and a playback-engine abstraction. The UI remains WinUI 3 even if a native decoding engine is introduced later. Treat media decoding capability, server transcoding permission, and subtitle rendering as explicit negotiation inputs.

## Build and Native AOT

Use Windows with .NET SDK 10.0.301, Visual Studio 2026 C++ build tools, and Windows SDK 10.0.26100.0. Dependencies are pinned centrally. The application uses Windows App SDK 2.4.0, WinUIEx 2.9.3, and CommunityToolkit.Mvvm 8.4.2.

```powershell
./scripts/Build.ps1
./scripts/Publish-Aot.ps1
```

Debug builds use the managed development runtime; Release publishing enables Native AOT. Run `artifacts/aot/EmbyClient.App.exe` with `artifacts/aot` as the working directory. The current development artifact is unpackaged and self-contained. MSIX release packaging is a later implementation stage; its manifest is already present.

## Research and verification status

Official Emby documentation, official SDK definitions, and upstream Windows/package documentation were read online. The public static Emby API definition reports version `4.1.1.0`; the newer official SDK definition does not declare a server version. Neither establishes the project's supported server range.

The user has explicitly authorized local builds and tests for the implementation task. The initial Debug build, Native AOT publish, and native process startup succeeded. Interactive UI review is pending desktop unlock, and real Emby playback has not yet been verified. The earlier [research verification plan](docs/research/verification-plan.md) is historical; the [implementation record](docs/implementation/status.md) tracks current authorization and results.
