# Continuous integration: Windows builds and Emby protocols

The latest audited Windows source is `bc3d290839e708beeefb0bd736f6206876ebda5e`. [Run 34406313675 / job 102650025727](https://github.com/moooyo/emby-client-winui3/actions/runs/34406313675/job/102650025727) completed successfully at 2026-09-09 21:23:28 UTC: **449 tests** (47 API, 125 media transport, 141 platform, 136 playback), Release build, Native AOT, unsigned-package verification, and uploads. The [retained receipt](verification/windows-ci-bc3d290.json) records the exact startup-repair source with zero failures/skips. No desktop, allocation-root-cause, decoder, or installed-runtime result is supplied by this CI run.

The previous [b5d9c2b run](https://github.com/moooyo/emby-client-winui3/actions/runs/34404127109) completed successfully with 440 tests; its [receipt](verification/windows-ci-b5d9c2b.json) remains unchanged. The 40 local fixture-boundary harness checks are a separate population and are not included in either hosted total.

The earlier repaired source `532f683` passed [run 34397362185](https://github.com/moooyo/emby-client-winui3/actions/runs/34397362185). Its [local code receipt](verification/code-batch-20260910.json) and [779B normal-app desktop receipt](verification/ui-779b-20260910/summary.json) retain their own scopes; the desktop memory gate remains unresolved. The B4C41DE8 observation build has a separate compilation receipt and has not been launched.

The two independent lanes first both passed at source `15652db20a78b527014c633a08c97d9c5368c7aa`. [Windows run 34321661741](https://github.com/moooyo/emby-client-winui3/actions/runs/34321661741) passed 392 tests and all 16 build/publish/package steps, completing at 2026-09-09 07:03:11 UTC. [Manual Emby protocol run 34321674576](https://github.com/moooyo/emby-client-winui3/actions/runs/34321674576) completed its first successful real-server check at 07:01:43 UTC: 18 Linux orchestration unit tests and a separate exact 40/40 official-server protocol result. These are distinct test populations, not one combined native acceptance suite. The protocol result is not relabeled as a run of 532f683.

The [Windows build workflow](../../.github/workflows/ci.yml) runs on pushes to `main`, pull requests, and manual dispatches. The [official Emby protocol workflow](../../.github/workflows/emby-protocol.yml) is a separate manual-only `main` lane. The section 8 independent real-server CI implementation and first hosted success are complete. Native rendering, desktop behavior, and installed-release acceptance remain separate. Earlier failures remain recorded below. The later integrated 779B memory measurements do not isolate the effect of the idle presentation-clock change.

The first hosted run rejected the workflow before allocating a job because `runner.temp` was referenced in job-level environment definitions. The corrected workflow initializes these paths in a step through `GITHUB_ENV`, where runner environment variables are available.

## Windows build execution and dependency policy

The job uses `windows-2025`, PowerShell, a 45-minute timeout, and `contents: read`. Checkout does not persist credentials. The workflow does not request signing credentials, real Emby passwords or tokens, release permissions, deployment access, or an interactive user session.

The SDK version is read from `global.json`, currently `10.0.301`. It is installed into an isolated directory under the runner's temporary directory and verified with `dotnet --version`. This is deliberate: the repository permits `latestPatch` roll-forward, while the hosted image contains other SDK versions and recent `setup-dotnet` releases interpret that policy when using `global-json-file`. Installing the exact version alone in an isolated .NET root keeps CI on the SDK actually recorded in the repository without editing `global.json`. The pinned action's [SDK-selection code](https://github.com/actions/setup-dotnet/blob/a98b56852c35b8e3190ac28c8c2271da59106c68/src/setup-dotnet.ts) and [installation-directory code](https://github.com/actions/setup-dotnet/blob/a98b56852c35b8e3190ac28c8c2271da59106c68/src/installer.ts) support these inputs.

Windows App SDK and all NuGet dependencies come from `Directory.Packages.props` and committed `packages.lock.json` files; the workflow does not duplicate package version declarations. The current Windows App SDK pin is `2.4.0`. A separate temporary NuGet package directory is cached using all committed package-lock files. Restore runs in locked mode for the solution and every test project, including test projects that have not yet been added to the solution. `RestoreLockedMode=true` also applies to implicit restores inside the existing scripts. A dependency or SDK change therefore requires intentionally refreshed lock files.

The job then invokes the repository's existing entry points:

```powershell
./scripts/Build.ps1 -Configuration Release
./scripts/Test.ps1 -Configuration Release
./scripts/Publish-Aot.ps1 -OutputDirectory artifacts/aot
./scripts/Package.ps1 -PublishDirectory artifacts/aot -OutputDirectory artifacts/packages
```

No CI-specific replacement of those scripts is introduced. `Test.ps1` discovers `*.Tests.csproj` recursively. At the time of configuration, this includes:

| Test project | Evidence supplied |
| --- | --- |
| `EmbyClient.Api.Tests` | Request contracts, source-generated JSON, identity isolation, cancellation/errors, and playback API behavior |
| `EmbyClient.Platform.Tests` | Real Windows user-level token protection, settings persistence, corruption handling, and initial-creation races |
| `EmbyClient.Playback.Tests` | Playback coordination, source/track decisions, reporting, and cleanup through test adapters |
| `EmbyClient.MediaTransport.Tests` | HTTP authentication boundaries, byte-range transport, and bounded progressive streaming/framing using loopback fixtures |

## Hosted Windows toolchain

The official [Windows 2025 image inventory at commit a0faebe84ce88a79331aebb1874c8c9de49bc5ba](https://github.com/actions/runner-images/blob/a0faebe84ce88a79331aebb1874c8c9de49bc5ba/images/windows/Windows2025-Readme.md), retrieved on 2026-09-09, identifies image `20260830.247.1` and includes:

- Visual Studio Enterprise 2022 `17.14.37614.0`.
- The native desktop workload and `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`.
- Windows SDK `10.0.26100.0`.
- Several .NET SDKs, including newer patches than the repository's `10.0.301`.

Microsoft's [Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) require Visual Studio 2022 or later with the C++ desktop workload for Windows. The workflow checks that the x64 linker and the target Windows SDK library exist, records their paths, and logs the actual SDK and image version. The hosted image label is rolling; pinning action commits does not make the entire hosted machine immutable. A later image change can require an explicit repository update or a dedicated runner.

The first two audited packaging runs used image `win25-vs2026 20260824.214.3`, with Visual Studio 2026 Enterprise, .NET SDK `10.0.301`, and Windows SDK `10.0.26100.0`. This is the executed toolchain, distinct from the earlier published inventory snapshot above. Their logs confirm test totals of 47, 65, 68, and 126 and an 18-component SBOM. The Release build summary reports one upstream generated WinUIEx `Icon` warning (`CS0618`) and zero errors; native code generation and publication then complete successfully. The subsequent `c6f967e` run again records image `win25-vs2026 20260824.214.3` and SDK `10.0.301`; its updated test totals are documented below.

## Hosted packaging receipts

The following results were checked read-only on 2026-09-09 using `gh run view` for job/step conclusions and logs, and the Actions artifacts API for names, sizes, and digests. No CI rerun, application/package download, installation, or local package hash verification was performed by this audit.

| Run | Exact source head | Completed UTC | Result |
| --- | --- | --- | --- |
| [34300925860 / job 102307461891](https://github.com/moooyo/emby-client-winui3/actions/runs/34300925860/job/102307461891) | `023b97dce33633e14aafd63b16b750a21c2e73f1` | 2026-09-09 01:56:15 | All job steps succeeded |
| [34301423262 / job 102308945888](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262/job/102308945888) | `a2e4e4338e1b1086defe8a97c096ffee87a917f9` | 2026-09-09 02:03:09 | All job steps succeeded |

Each run completed locked restore, the Release solution build, 306 tests with zero failures, Native AOT publication, SBOM generation, `Package.ps1` structural verification, and separate MSIX/AOT/log uploads. The packaging log explicitly leaves installation and packaged runtime behavior unverified. SBOM publication confirms JSON readback; the publisher script does not run schema validation.

For the earlier `a2e4e433` head, the API reported the following complete artifact names and archive metadata. All three were unexpired when inspected and use seven-day retention.

| Artifact | Archive bytes | GitHub archive SHA-256 digest |
| --- | ---: | --- |
| [emby-client-windows-x64-msix-unsigned-a2e4e4338e1b1086defe8a97c096ffee87a917f9](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262/artifacts/10085120746) | 54,167,100 | `b859fec3d237a5b355c9ae13dcbb10bc3f2ec338982010b4cc843132a66d1f3e` |
| [emby-client-windows-x64-aot-unsigned-a2e4e4338e1b1086defe8a97c096ffee87a917f9](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262/artifacts/10085122455) | 75,377,319 | `2493830a813ffc67cd82a6f749f00e41740df422ce9ced7a7c9950cea785e0da` |
| [windows-ci-logs-a2e4e4338e1b1086defe8a97c096ffee87a917f9](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262/artifacts/10085122789) | 3,476 | `21f66e4cc8059e76e451a9a569865f77ef45763751019402558a23b752e0e2b1` |

These hashes and sizes identify uploaded GitHub archives, not the contained executable or MSIX. That `a2e4e433` MSIX's builder-reported SHA-256 is `996D6E72C283B43F15D9485D1E23CAB68FAB1CABDE89FE61FE1AE8F591E0A278`; [packaging evidence](packaging.md#hosted-structural-evidence) records its exact filename and the preceding successful candidate. A newer working tree or artifact needs its own receipt.

The later [run 34304285991](https://github.com/moooyo/emby-client-winui3/actions/runs/34304285991), source `42011fb8fdce53a0771bd903dc7ffb1695bf42d8`, also passed every step. It covers the editable queue, diagnostics, relay classification, and default-track retry correction: 340 tests (47 API, 73 transport, 88 platform, 132 playback), a Release build with one existing `CS0618` warning and zero errors, Native AOT output, an 18-component SBOM, and unsigned package verification.

Its AOT archive [10086142609](https://github.com/moooyo/emby-client-winui3/actions/runs/34304285991/artifacts/10086142609) is 75,810,365 bytes with digest `sha256:c13f5df563b8b46d0283f668897024628240b582e6fd58f76434bd1497c530f1`. The MSIX archive [10086140732](https://github.com/moooyo/emby-client-winui3/actions/runs/34304285991/artifacts/10086140732) is 54,281,513 bytes with digest `sha256:ea933826abf169ea6719e0d59963aa4b235f8c9917c40110faaa5fade0370bdb`. The contained candidate is `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-024541020-620ea159.msix`; `Package.ps1` reports its file SHA-256 as `D82D7A1BD62DE44408DFF3A0563BDF66DA319C1CF578B403D870D4F9AD431985`. Archive and package hashes identify different files. This run predates the subsequent poster-lifecycle investigation and does not establish installed runtime behavior.

The subsequent [run 34309591888](https://github.com/moooyo/emby-client-winui3/actions/runs/34309591888), exact source `076dbbe21ebae00e3722174f1e882d854b84c004`, passed all job steps after collection Reset poster cleanup and the isolated observation tools were added. Its Release build reported one existing CS0618 warning and zero errors; all 340 tests passed (47 API, 73 transport, 88 platform, 132 playback), followed by Native AOT publication, an 18-component SBOM, unsigned MSIX structural verification, and all uploads. The normal application build excludes the library observer.

The AOT archive [10087940397](https://github.com/moooyo/emby-client-winui3/actions/runs/34309591888/artifacts/10087940397) is 75,822,354 bytes with archive digest `sha256:82c7f72e81d8b3733ec088a418ebe1af4240dbfeb6c685e3169bc12337aa5c99`. The MSIX archive [10087938664](https://github.com/moooyo/emby-client-winui3/actions/runs/34309591888/artifacts/10087938664) is 54,284,090 bytes with archive digest `sha256:542287d9f6a87ef7d11e8db7559efaf9ed8cd25b39effe2a78bf16138616fc7c`. The contained `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-040826837-a8c55b3e.msix` has builder-reported file SHA-256 `15ABC58F07A04168C7AFBE25416DECAA6E5F2AFE9DF923DAC9144EAC5B03C851`. The logs archive is [10087940712](https://github.com/moooyo/emby-client-winui3/actions/runs/34309591888/artifacts/10087940712), 3,491 bytes. These are scoped hosted build results, not installed runtime, desktop rendering, or a long-duration memory pass.

### Keyboard and focus revision c6f967e

Read-only GitHub API/job-log inspection confirmed [run 34313098711 / job 102343633461](https://github.com/moooyo/emby-client-winui3/actions/runs/34313098711/job/102343633461) for exact head `c6f967ee9be005378abbcc58f9d46cb084e08615`. All 16 reported steps, including setup and post-job steps, concluded `success`. The job completed at **2026-09-09 05:03:05 UTC**; the run metadata was updated one second later.

The logs establish locked restore, a successful Release build with **one existing generated `CS0618` warning and zero errors**, and **340 succeeded / zero failed / zero skipped tests**: 47 API, 73 media transport, 88 Windows platform, and 132 playback. Native code generation and AOT publication succeeded. The generated SBOM contains 18 components and passed JSON readback; schema validation was not performed by that script. Unsigned MSIX structural verification and all three uploads succeeded.

The contained package is `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-050241216-330b1ec4.msix`, with builder-reported file SHA-256 **`7833160FEE8EEBD6BDBFD9662CBB651256F2FB79D30965DD7AC698A70F7B5413`**. The Actions API separately reports these archive identities:

| Artifact | Archive bytes | GitHub archive SHA-256 digest |
| --- | ---: | --- |
| [emby-client-windows-x64-msix-unsigned-c6f967ee9be005378abbcc58f9d46cb084e08615](https://github.com/moooyo/emby-client-winui3/actions/runs/34313098711/artifacts/10089144103) | 54,283,155 | `7270c8ca57ac42130549d612986a329d0e6cc7a738ec21ba13899b05b0503e7a` |
| [emby-client-windows-x64-aot-unsigned-c6f967ee9be005378abbcc58f9d46cb084e08615](https://github.com/moooyo/emby-client-winui3/actions/runs/34313098711/artifacts/10089146006) | 75,832,398 | `7e911236760648ef460fa79b19cc9f575795ebc4dcc438960a32178e6f647d2a` |
| [windows-ci-logs-c6f967ee9be005378abbcc58f9d46cb084e08615](https://github.com/moooyo/emby-client-winui3/actions/runs/34313098711/artifacts/10089146307) | 3,483 | `0a66d86c0b773b6324ccb72f7ba5e2cb2181d89a5906dbc41e19f51d8595fa67` |

All three archives were unexpired at inspection, with expiry dates on 2026-09-16. No application/package artifact was downloaded or installed during this audit, so the contained-package hash remains builder-reported. The package log explicitly leaves installation and packaged runtime behavior unverified. The separately observed timeline keys and four Queue/Diagnostics focus-return paths do not expand hosted CI into native UI, Narrator, physical-key, complex-subtitle, or installed-release acceptance.

### Progressive transport revision 71e9ffb

Read-only job, log, and artifact inspection confirmed [run 34317011874 / job 102355241584](https://github.com/moooyo/emby-client-winui3/actions/runs/34317011874/job/102355241584) for exact source `71e9ffb7c5bb53857895a79aabb1edb3ead3266d`. All 16 steps succeeded, completing at **2026-09-09 06:01:43 UTC**. The runner reports image `win25-vs2026 20260824.214.3` and the pinned SDK `10.0.301`.

The unified Release run passed **387 tests**: API 47, media transport 120, Windows platform 88, and playback 132, with zero failures or skipped cases. The Release solution build retained one generated WinUIEx `Icon` `CS0618` warning and zero errors. Native AOT publication, the 18-component SBOM with JSON readback, unsigned MSIX structural verification, and all uploads succeeded. The 47 new progressive transport cases cover framing, streaming, and lifetime boundaries; CI did not run NativeProbe or a player window.

The contained package is `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-060117616-32986c8b.msix`; the builder reports file SHA-256 **`C03BF0C0AA903B3A2ED164F8C3239C32FFDD1BFBAD8E7313C799C251ABC419B9`**. The Actions API separately reports:

| Artifact | Archive bytes | GitHub archive SHA-256 digest |
| --- | ---: | --- |
| [Unsigned MSIX archive 10090502408](https://github.com/moooyo/emby-client-winui3/actions/runs/34317011874/artifacts/10090502408) | 54,317,035 | `c3369ed8701d100ea6704554a84d948630c39f702460ed49588f5106b3f89d4a` |
| [AOT development-folder archive 10090504347](https://github.com/moooyo/emby-client-winui3/actions/runs/34317011874/artifacts/10090504347) | 75,967,308 | `7f2b27609cc3102920b8053568c8590385d0bdf7d5284771ba489448baad7edc` |
| [CI logs archive 10090504729](https://github.com/moooyo/emby-client-winui3/actions/runs/34317011874/artifacts/10090504729) | 3,490 | `a0af9f8c61d23e175b17d227a713d906cb1aafaf3badc679307ae4cfb65dffeb` |

All three archives were unexpired at inspection. Their names end with the full source SHA above. No package or application archive was downloaded or installed for this audit; the contained package hash is builder-reported. The separate local [NativeProbe compile-only receipt](../../tools/EmbyClient.NativeProbe/verification/progressive-http-control-build.json) is not a hosted or runtime result. Native progressive rendering, complex subtitles, and installed playback remain unverified.

### Idle presentation clock revision 19b434d

The recorded read-only GitHub job/log/artifact inspection confirmed [run 34318519636 / job 102359801545](https://github.com/moooyo/emby-client-winui3/actions/runs/34318519636/job/102359801545) for exact source `19b434d8ffb58ed9d8f75557beacb64ae9fa29c3`. All reported job steps succeeded, completing at **2026-09-09 06:21:38 UTC**.

The unified Release test run passed **392 tests**: API 47, media transport 120, Windows platform 93, and playback 132. Every project's total equaled its succeeded count, with no failed or skipped cases. The five added platform cases cover display-release lifecycle and retry policy. The solution build retained **one existing generated `CS0618` warning and zero errors**. Native AOT publication, the 18-component SBOM, unsigned MSIX structural verification, and all uploads succeeded.

The contained package is `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-062118947-d4b70ed9.msix`, with builder-reported file SHA-256 **`848284D58E888A7F8A3458D72527FC77EA4C79A36FF1712A4B6FE348E3311803`**. The recorded Actions artifact metadata is:

| Artifact | Archive bytes | GitHub archive SHA-256 digest |
| --- | ---: | --- |
| [Unsigned MSIX archive 10091011572](https://github.com/moooyo/emby-client-winui3/actions/runs/34318519636/artifacts/10091011572) | 54,317,019 | `dbd15f55da0465ab90f205bb6c0acac22809f2e5b41a8845c82d362cf97d9704` |
| [AOT development-folder archive 10091013126](https://github.com/moooyo/emby-client-winui3/actions/runs/34318519636/artifacts/10091013126) | 75,972,397 | `2f9869817fd7577a57208d45476e1de9ae7574f68585cfbc63cb4f4acf7cbee3` |
| [CI logs archive 10091013651](https://github.com/moooyo/emby-client-winui3/actions/runs/34318519636/artifacts/10091013651) | 3,488 | `681d8e1c4b1bd2c9185785845aa146b71d145fd64f18ae91450180a452f6b539` |

All three archives were unexpired at the recorded inspection. Archive digests identify the uploaded archives, not their contained executable or package. No artifact download, installation, build, or runtime verification was performed for this documentation update; the contained package hash remains builder-reported.

This revision includes `PlaybackDisplayRequest.NeedsReleaseRetry` and terminal live-state reconciliation. The added platform tests cover display-release policy; compilation of the UI reconciliation path is not a runtime observation. This receipt does not show an actual dispatcher timer stopping, display sleep/release behavior on a live window, fewer wakeups, or reduced private memory. Earlier large-library growth and complex-subtitle failures remain unchanged. This earlier Windows run did not include the separate manual Emby protocol lane; its later first success is recorded below.

### Latest Windows receipt: protocol automation revision 15652db

The recorded job/log audit confirms [run 34321661741 / job 102369456878](https://github.com/moooyo/emby-client-winui3/actions/runs/34321661741/job/102369456878) at `15652db20a78b527014c633a08c97d9c5368c7aa`. All **16 steps succeeded**, completing at **2026-09-09 07:03:11 UTC**. The unified test run passed **392 cases: API 47, media transport 120, Windows platform 93, playback 132**, with every total equal to succeeded. The Release build retained one existing generated `CS0618` warning and zero errors. Native AOT, the 18-component SBOM, unsigned MSIX structural verification, and uploads all succeeded.

The contained package is `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-070246964-6d18eecf.msix`, with builder-reported file SHA-256 **`F0D814EB5EE80EA6B79860A4E17941B7E6AF32ADBD2B610A19C661A78BEB8CA7`**. The separate uploaded archive identities are:

| Artifact | Archive bytes | GitHub archive SHA-256 digest |
| --- | ---: | --- |
| [Unsigned MSIX archive 10092172663](https://github.com/moooyo/emby-client-winui3/actions/runs/34321661741/artifacts/10092172663) | 54,316,329 | `a061cae4dd99687f6bd8cac1633058bcb3da0706398eb9a7253483ac9c8f361c` |
| [AOT development-folder archive 10092174914](https://github.com/moooyo/emby-client-winui3/actions/runs/34321661741/artifacts/10092174914) | 75,970,914 | `e7d1d389bf58eea88711e0f2604149ee22784a44bb465395f89f2c52ee9ada8a` |
| [CI logs archive 10092175313](https://github.com/moooyo/emby-client-winui3/actions/runs/34321661741/artifacts/10092175313) | 3,483 | `8575dca2a60f3c1a4cbfd13acc9393e56ca1d71f198077c9d95acd5c34c02cba` |

These archive digests are not the contained package or executable hash. The recorded local product AOT remains `5C3C472A714316F177C3A4F62C9BE19DF9421CA1FA0B1097F6B33737C687C0D0`; this CI receipt does not supply a new desktop, timer, memory, or installed-package observation.

## Independent official Emby protocol CI: first pass 15652db

The [manual protocol workflow](../../.github/workflows/emby-protocol.yml) runs only through `workflow_dispatch` on `main`, on `ubuntu-24.04`, with `contents: read`. It builds a **framework-dependent portable CLI** from the existing API-probe sources. This Linux job is a protocol harness, not a Linux player or a Native AOT rendering test. [Collector documentation](../../tools/EmbyClient.ServerValidation/Ci/README.md) describes its owned server and report-filtering design.

[Run 34321674576 / job 102369510518](https://github.com/moooyo/emby-client-winui3/actions/runs/34321674576/job/102369510518), at the same `15652db20a78b527014c633a08c97d9c5368c7aa` head, completed successfully at **2026-09-09 07:01:43 UTC**. Its Linux `unittest` stage passed **18** orchestration/relay cases. The separate [retained protocol summary](../../tools/EmbyClient.ServerValidation/Ci/verification/protocol-pass-15652db.json) reports:

- Exactly 40 expected checks and 40 `Passed` results, zero unknown/duplicate steps, and probe exit code 0.
- Official Emby `4.9.5.0`, Docker `28.0.4`, and pinned image `emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a`.
- A generated 60-second 640 x 360 H.264/AAC/SRT fixture and a temporary playback user whose administrator flag is false.
- An internal Docker bridge with no requested or actual published Docker port, only the owned configuration and read-only synthetic media mounted, and an owned IPv4 loopback relay whose target matches the owned endpoint. Host networking and privileged mode are false.
- Public server information received on readiness attempt 2 after one recorded `ConnectionReset` with errno 104. This transient failure remains visible in the successful receipt.
- `CleanupCompleted=true` and `RelayCleanupCompleted=true`.

The checks exercise authentication, browsing/user data, PlaybackInfo, authenticated original ranges, HLS manifest/segment and WebVTT transfer, synthetic Start/Progress/Stop reports with server readback, and cleanup. `ProbeMode=FrameworkDependentPortableCli`, `NativeUi=NotRun`, and `NativeDecoderAndFirstFrame=NotRun` explicitly limit the result. The 18 Linux unit tests, 40 real-server checks, and 392 Windows tests retain their separate meanings.

The downloaded protocol-summary [archive 10092132358](https://github.com/moooyo/emby-client-winui3/actions/runs/34321674576/artifacts/10092132358) is **2,307 bytes**, with ZIP SHA-256 **`bb3756b5241e584151c5515180282aa12ecfa00b653d92e77271d3d11e2e524f`**. Its individual JSON, retained as `protocol-pass-15652db.json`, has file SHA-256 **`D92EE5C73A30F853004E14252D773A4EBD79DD9AB60C0941F0B001E1A3033A83`**, confirmed from the local retained file. These identify different objects and must not be substituted for one another.

The initial [run 34319687217](https://github.com/moooyo/emby-client-winui3/actions/runs/34319687217) at `0a0809f949949c098067dda8e378cafd82c3c8d1` remains a [retained startup failure](../../tools/EmbyClient.ServerValidation/Ci/verification/startup-failure-0a0809f.json): `ServerStartupDeadline` at `ServerReadiness`, with owned cleanup completed. It did not reach a passed protocol result and is not rewritten by the later success.

Independent real-server CI is now implemented and has its first scoped pass. It does not complete native playback, actual subtitle pixels, installed activation, signing, clean-machine playback, broad server/version coverage, or resource acceptance.

## Pinned official actions

Official release tags were resolved through the GitHub API and the corresponding action metadata was read before writing the workflow. All `uses` references point to full immutable commit IDs.

| Official action | Release observed | Pinned commit |
| --- | --- | --- |
| [actions/checkout](https://github.com/actions/checkout/releases/tag/v7.0.1) | `v7.0.1`, 2026-07-20 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| [actions/setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0) | `v6.0.0`, 2026-07-16 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| [actions/upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.1) | `v7.0.1`, 2026-04-10 | `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a` |

These pinned action definitions use Node 24. The official action documentation requires a sufficiently recent runner; checkout/setup-dotnet document `2.327.1` or later for this runtime. The current official [runner release observed was v2.337.0](https://github.com/actions/runner/releases/tag/v2.337.0). GitHub manages hosted-runner agent updates; the workflow does not install or launch its own runner.

## Artifacts and practical limits

The packaging step consumes the just-published folder, preserves its complete PRI and payload, and runs the same structural/hash checks as the local packager. Its separate `emby-client-windows-x64-msix-unsigned-<commit>` artifact contains the unsigned MSIX, checksum, and review JSON. No certificate, installation, trust modification, or release publishing is involved. A successful packaging job still does not prove installed runtime behavior. See the [update and servicing policy](update-policy.md).

On success, `emby-client-windows-x64-aot-unsigned-<commit>` contains the entire `artifacts/aot` directory, including the native executable, Windows App SDK runtime dependencies, resources, and an artifact README. Extract the entire folder before launching `EmbyClient.App.exe`; copying the executable alone does not preserve the deployment. This is an unsigned development build, not a signed package or release. Build/test/publish/package text logs are uploaded separately when available, including after a failed step. MSIX, AOT, and log artifacts expire after seven days. The pinned [upload-artifact inputs](https://github.com/actions/upload-artifact/blob/043fb46d1a93c77aae656e7c1c64a875d1fc6a0a/action.yml) support explicit archive mode, retention, and failure when expected files are missing.

The Windows build lane establishes compilation, its named automated tests, Native AOT publication, and unsigned MSIX structural/hash checks for that revision. The independent protocol lane adds the recorded 40 API/HTTP checks against one pinned real Emby version. Neither establishes native UI rendering, focus/fullscreen behavior, graphics-driver compatibility, audio-device behavior, actual codec rendering, HDR, signing readiness, or installed package activation. Interactive playback and installation trials remain separate evidence. The Windows build workflow neither generates media nor opens the app in a headless job; the protocol workflow generates synthetic media for HTTP checks and explicitly leaves native UI/decoding unrun.

Updates to action pins should repeat the official-release and commit verification. Updates to `global.json`, central packages, or Windows targets should refresh the affected lock files and review the hosted image inventory before the next run.
