# Windows continuous integration

The [Windows CI workflow](../../.github/workflows/ci.yml) builds the repository, runs the four test projects, publishes the Windows x64 Native AOT app, and uploads its complete unsigned development folder. It runs for pushes to `main`, pull requests, and explicit manual dispatches. [Hosted run 34296241270](https://github.com/moooyo/emby-client-winui3/actions/runs/34296241270) passed every step for commit `b0de51940416e0bb07fe41cb19e37478f80998d7`: locked restore, Release build, 253 tests, Native AOT publication, SBOM generation, and both artifact uploads.

The first hosted run rejected the workflow before allocating a job because `runner.temp` was referenced in job-level environment definitions. The corrected workflow initializes these paths in a step through `GITHUB_ENV`, where runner environment variables are available.

## Execution and dependency policy

The job uses `windows-2025`, PowerShell, a 45-minute timeout, and `contents: read`. Checkout does not persist credentials. The workflow does not request signing credentials, real Emby passwords or tokens, release permissions, deployment access, or an interactive user session.

The SDK version is read from `global.json`, currently `10.0.301`. It is installed into an isolated directory under the runner's temporary directory and verified with `dotnet --version`. This is deliberate: the repository permits `latestPatch` roll-forward, while the hosted image contains other SDK versions and recent `setup-dotnet` releases interpret that policy when using `global-json-file`. Installing the exact version alone in an isolated .NET root keeps CI on the SDK actually recorded in the repository without editing `global.json`. The pinned action's [SDK-selection code](https://github.com/actions/setup-dotnet/blob/a98b56852c35b8e3190ac28c8c2271da59106c68/src/setup-dotnet.ts) and [installation-directory code](https://github.com/actions/setup-dotnet/blob/a98b56852c35b8e3190ac28c8c2271da59106c68/src/installer.ts) support these inputs.

Windows App SDK and all NuGet dependencies come from `Directory.Packages.props` and committed `packages.lock.json` files; the workflow does not duplicate package version declarations. The current Windows App SDK pin is `2.4.0`. A separate temporary NuGet package directory is cached using all committed package-lock files. Restore runs in locked mode for the solution and every test project, including test projects that have not yet been added to the solution. `RestoreLockedMode=true` also applies to implicit restores inside the existing scripts. A dependency or SDK change therefore requires intentionally refreshed lock files.

The job then invokes the repository's existing entry points:

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test.ps1 -Configuration Release
./scripts/Publish-Aot.ps1 -OutputDirectory artifacts/aot
```

No CI-specific replacement of those scripts is introduced. `Test.ps1` discovers `*.Tests.csproj` recursively. At the time of configuration, this includes:

| Test project | Evidence supplied |
| --- | --- |
| `EmbyClient.Api.Tests` | Request contracts, source-generated JSON, identity isolation, cancellation/errors, and playback API behavior |
| `EmbyClient.Platform.Tests` | Real Windows user-level token protection, settings persistence, corruption handling, and initial-creation races |
| `EmbyClient.Playback.Tests` | Playback coordination, source/track decisions, reporting, and cleanup through test adapters |
| `EmbyClient.MediaTransport.Tests` | HTTP authentication boundaries and byte-range transport using loopback fixtures |

## Hosted Windows toolchain

The official [Windows 2025 image inventory at commit a0faebe84ce88a79331aebb1874c8c9de49bc5ba](https://github.com/actions/runner-images/blob/a0faebe84ce88a79331aebb1874c8c9de49bc5ba/images/windows/Windows2025-Readme.md), retrieved on 2026-09-09, identifies image `20260830.247.1` and includes:

- Visual Studio Enterprise 2022 `17.14.37614.0`.
- The native desktop workload and `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`.
- Windows SDK `10.0.26100.0`.
- Several .NET SDKs, including newer patches than the repository's `10.0.301`.

Microsoft's [Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) require Visual Studio 2022 or later with the C++ desktop workload for Windows. The workflow checks that the x64 linker and the target Windows SDK library exist, records their paths, and logs the actual SDK and image version. The hosted image label is rolling; pinning action commits does not make the entire hosted machine immutable. A later image change can require an explicit repository update or a dedicated runner.

The successful run's actual image was `win25-vs2026 20260824.214.3`, with Visual Studio 2026 Enterprise, .NET SDK `10.0.301`, and Windows SDK `10.0.26100.0`. This is the executed toolchain, distinct from the earlier published inventory snapshot above. Its downloaded logs confirm test totals of 47, 65, 33, and 108 and an 18-component SBOM. The uploaded AOT archive was 75,138,199 bytes with GitHub artifact digest `sha256:393c3f9cf0860518d078dfe6844eeddfae585b9056020222391d977be367a2de`. Later revisions need their own successful run.

## Pinned official actions

Official release tags were resolved through the GitHub API and the corresponding action metadata was read before writing the workflow. All `uses` references point to full immutable commit IDs.

| Official action | Release observed | Pinned commit |
| --- | --- | --- |
| [actions/checkout](https://github.com/actions/checkout/releases/tag/v7.0.1) | `v7.0.1`, 2026-07-20 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| [actions/setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0) | `v6.0.0`, 2026-07-16 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| [actions/upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.1) | `v7.0.1`, 2026-04-10 | `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` |

These pinned action definitions use Node 24. The official action documentation requires a sufficiently recent runner; checkout/setup-dotnet document `2.327.1` or later for this runtime. The current official [runner release observed was v2.337.0](https://github.com/actions/runner/releases/tag/v2.337.0). GitHub manages hosted-runner agent updates; the workflow does not install or launch its own runner.

## Artifacts and practical limits

On success, `emby-client-windows-x64-aot-unsigned-<commit>` contains the entire `artifacts/aot` directory, including the native executable, Windows App SDK runtime dependencies, resources, and an artifact README. Extract the entire folder before launching `EmbyClient.App.exe`; copying the executable alone does not preserve the deployment. This is an unsigned development build, not a signed package or release. Build/test/publish text logs are uploaded separately when available, including after a failed step. Both artifact types expire after seven days. The pinned [upload-artifact inputs](https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/action.yml) support explicit archive mode, retention, and failure when expected files are missing.

Hosted checks establish compilation, the named automated tests, and successful Native AOT publication for that runner revision. They do not establish native UI rendering, focus/fullscreen behavior, graphics-driver compatibility, audio-device behavior, actual codec rendering, HDR, packaging/signing readiness, or compatibility with a real Emby installation. Interactive playback trials and authorized real-server contract tests remain separate evidence. The workflow deliberately neither generates media nor opens the app in a headless job to claim those results.

Updates to action pins should repeat the official-release and commit verification. Updates to `global.json`, central packages, or Windows targets should refresh the affected lock files and review the hosted image inventory before the next run.
