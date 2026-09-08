# Emby-compatible Windows client

An open-source, Windows-only media client for Emby Server, using native WinUI 3 controls, WinUIEx, and selected Windows Community Toolkit packages.

The repository currently contains API research and an implementation proposal. It does not yet contain an application, a tested server compatibility claim, or a selected project license.

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

## Research status

Official Emby documentation, official SDK definitions, and upstream Windows/package documentation were read online. The public static Emby API definition reports version `4.1.1.0`; the newer official SDK definition does not declare a server version. Neither establishes the project's supported server range.

No application code, dependency installation, local build, test suite, or playback probe was run. Future verification must run through `ssh test-env` unless local verification is explicitly authorized for that task. See the [verification plan](docs/research/verification-plan.md).
