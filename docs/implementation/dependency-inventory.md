# Product dependency and notice inventory

Recorded on 2026-09-09 from existing restored metadata. No restore, build, test, or application run was performed for this inventory.

## Scope and evidence

The product is [EmbyClient.App](../../src/EmbyClient.App/EmbyClient.App.csproj), including its `EmbyClient.Api` and `EmbyClient.Playback` project references. The reviewed configuration is Windows x64, .NET 10, NativeAOT, `SelfContained=true`, and `WindowsAppSDKSelfContained=true`.

The resolved versions come from the app's [lock file](../../src/EmbyClient.App/packages.lock.json), the existing `src/EmbyClient.App/obj/project.assets.json` target `net10.0-windows10.0.26100.0/win-x64`, its framework download dependencies, and the matching NuGet cache `.nuspec` files. License fields below reproduce package metadata; a linked repository's license must not replace a NuGet package's different license metadata.

Snapshot hashes:

- `packages.lock.json` SHA-256: `90390282B816FC8CFC77B6869EEBA3C8D44239C6E78ADD7AE3759F27483E226F`.
- `project.assets.json` SHA-256: `392718FF9D85FE5FFF1EAF4475397A8FD139BD81B66063942507FA1E798825B2`.

Read-only inspection of the existing `artifacts/aot-verified` payload confirms, among other files, `Microsoft.ui.xaml.dll`, `Microsoft.WindowsAppRuntime.dll`, `DWriteCore.dll`, `WebView2Loader.dll`, `DirectML.dll`, `onnxruntime.dll`, and `Microsoft.Windows.AI.MachineLearning.dll`. Windows App SDK imports copy some native payload through MSBuild targets rather than NuGet's `runtime` or `native` asset lists. Consequently, an assets entry containing only `build` does not by itself prove that its package is irrelevant to distribution.

The table describes the resolved product graph and its distribution sources. It does not claim that every optional API is called or that every managed assembly survives NativeAOT trimming. Managed dependencies may be incorporated into the executable rather than remain as separate DLLs. The final release payload must be compared with this inventory after any dependency, SDK, trimming, or packaging change.

## Product packages

Package links point to the publisher's exact NuGet version. `expression` and `file` are the actual `.nuspec` license metadata types.

| Package | Resolved version | Product role | License metadata and retained text |
| --- | --- | --- | --- |
| [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2) | 8.4.2 | Direct MVVM runtime and source-generator dependency | `expression: MIT`; [License.md](../../licenses/third-party/CommunityToolkit.Mvvm-8.4.2/License.md) and `ThirdPartyNotices.txt` |
| [WinUIEx](https://www.nuget.org/packages/WinUIEx/2.9.3) | 2.9.3 | Direct native-window integration dependency | `expression: MIT`; [LICENSE](../../licenses/third-party/WinUIEx-2.9.3/LICENSE) from the package's source commit |
| [Microsoft.WindowsAppSDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.4.0) | 2.4.0 | Direct meta-package selecting the following SDK/runtime family | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK-2.4.0/license.txt) and `NOTICE.txt` |
| [Microsoft.WindowsAppSDK.Runtime](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.4.0) | 2.4.0 | App-local runtime payload delivered through SDK targets | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.Runtime-2.4.0/license.txt) and `NOTICE.txt` |
| [Microsoft.WindowsAppSDK.WinUI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.6) | 2.3.6 | WinUI projection and native XAML integration | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.WinUI-2.3.6/license.txt) and the package-root `NOTICE.txt` |
| [Microsoft.WindowsAppSDK.Foundation](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Foundation/2.3.9) | 2.3.9 | Lifecycle, resource, notification, and other projections; native bootstrap assets | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.Foundation-2.3.9/license.txt) |
| [Microsoft.WindowsAppSDK.Base](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Base/2.0.4) | 2.0.4 | SDK/deployment coordination within the runtime graph | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.Base-2.0.4/license.txt) and `NOTICE.txt` |
| [Microsoft.WindowsAppSDK.DWrite](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.DWrite/2.1.0) | 2.1.0 | DirectWrite integration; native payload includes `DWriteCore.dll` | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.DWrite-2.1.0/license.txt) |
| [Microsoft.WindowsAppSDK.InteractiveExperiences](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.6) | 2.1.6 | Interactive-experience projection and native SDK integration | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.InteractiveExperiences-2.1.6/license.txt) |
| [Microsoft.WindowsAppSDK.AI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.AI/2.4.4) | 2.4.4 | Transitive AI projections; associated native files are in the self-contained SDK payload | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.AI-2.4.4/license.txt) |
| [Microsoft.WindowsAppSDK.ML](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML/2.1.74) | 2.1.74 | Transitive integration package selecting MachineLearning | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.ML-2.1.74/license.txt) and `ThirdPartyNotices.txt` |
| [Microsoft.Windows.AI.MachineLearning](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.1.74) | 2.1.74 | Managed projection/ONNX bindings and native DirectML, ONNX Runtime, and MachineLearning DLLs | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.Windows.AI.MachineLearning-2.1.74/license.txt) and `ThirdPartyNotices.txt` |
| [Microsoft.WindowsAppSDK.Search](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Search/2.4.4) | 2.4.4 | Transitive search projection and SDK runtime payload | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.Search-2.4.4/license.txt) |
| [Microsoft.WindowsAppSDK.Widgets](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Widgets/2.0.5) | 2.0.5 | Transitive widgets projection and SDK runtime payload | `file: license.txt`; [license.txt](../../licenses/third-party/Microsoft.WindowsAppSDK.Widgets-2.0.5/license.txt) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77) | 1.0.3719.77 | WinUI dependency; selected native asset is `WebView2Loader.dll` | `file: LICENSE.txt`; [LICENSE.txt](../../licenses/third-party/Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt) and `NOTICE.txt` |
| [System.Numerics.Tensors](https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0) | 9.0.0 | Transitive MachineLearning managed dependency, subject to trimming | `expression: MIT`; [LICENSE.TXT](../../licenses/third-party/System.Numerics.Tensors-9.0.0/LICENSE.TXT) and `THIRD-PARTY-NOTICES.TXT` |

The package-source metadata identifies [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet), [dotMorten/WinUIEx](https://github.com/dotMorten/WinUIEx), [Microsoft Windows App SDK](https://github.com/microsoft/WindowsAppSDK), [Microsoft WebView2](https://aka.ms/webview), and [dotnet/runtime](https://github.com/dotnet/runtime). The Windows App SDK and MachineLearning NuGet packages identify a license **file**, not an MIT SPDX expression. Their original files are retained without substituting the repository license or drawing a redistribution conclusion.

The presence of AI, ML, Search, Widgets, or WebView2 files in the SDK payload does not mean the application implements those features. In particular, this inventory records the WebView2 SDK/loader dependency; it is not an inventory of a separately installed browser runtime.

## Framework and bundled runtime components

| Component | Resolved version/provenance | Classification and retained text |
| --- | --- | --- |
| [Microsoft.NETCore.App.Runtime.NativeAOT.win-x64](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.NativeAOT.win-x64/10.0.9) | 10.0.9; framework download dependency | Product NativeAOT runtime implementation and native support libraries; `.nuspec` declares `expression: MIT`. Retain [LICENSE.TXT](../../licenses/third-party/Microsoft.NETCore.App.Runtime.NativeAOT.win-x64-10.0.9/LICENSE.TXT) and the full `THIRD-PARTY-NOTICES.TXT`. Source metadata points to [dotnet/dotnet commit 901ca941](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353). |
| [Microsoft.Windows.SDK.NET.Ref](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.26100.57) | 10.0.26100.57; framework targeting pack | Includes Windows projections and bundled `WinRT.Runtime.dll`, so it is not solely a compile-time reference for this inventory. Its `.nuspec` has only `licenseUrl: https://aka.ms/WinSDKLicenseURL`, without an SPDX expression or embedded license file. Preserve the URL's original [sdk_license.rtf](../../licenses/third-party/Microsoft.Windows.SDK.NET.Ref-10.0.26100.57/sdk_license.rtf). |
| Bundled C#/WinRT runtime | `WinRT.Runtime.dll` file version 2.2.0.48161; product version suffix `8649ee3eeb2445ca2a36d80d878ef60b96a6c65d` | Supplied inside the preceding targeting pack, not as a separate app NuGet reference. Retain [LICENSE](../../licenses/third-party/CsWinRT-2.2.0.48161/LICENSE) from the [matching upstream source commit](https://github.com/microsoft/CsWinRT/tree/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d). This source-license record does not replace the targeting-pack metadata. |

The same assets file records downloads for `Microsoft.NETCore.App.Runtime.win-x64`, `Microsoft.AspNetCore.App.Runtime.win-x64`, and `Microsoft.WindowsDesktop.App.Runtime.win-x64`, all 10.0.9. Download presence does not establish a product framework reference or that those complete runtimes are shipped. The app's declared framework references are `Microsoft.NETCore.App` and `Microsoft.Windows.SDK.NET.Ref.Windows`; the selected release direction uses the NativeAOT runtime pack. Windows media APIs and installed OS codecs are platform facilities rather than NuGet payload selected by this repository.

## Build, test, and probe dependencies

These packages are outside the product runtime table. Compiler/analyzer executables, test hosts, SDK tools, and probe output must not be copied into the application release directory merely because they exist in the repository or NuGet cache.

| Package or group | Resolved version | Metadata and role |
| --- | --- | --- |
| [Microsoft.DotNet.ILCompiler](https://www.nuget.org/packages/Microsoft.DotNet.ILCompiler/10.0.9), [runtime.win-x64.Microsoft.DotNet.ILCompiler](https://www.nuget.org/packages/runtime.win-x64.Microsoft.DotNet.ILCompiler/10.0.9), [Microsoft.NET.ILLink.Tasks](https://www.nuget.org/packages/Microsoft.NET.ILLink.Tasks/10.0.9) | 10.0.9 | `expression: MIT`; automatically referenced AOT/linker tooling. Native support incorporated into the app is accounted for by the NativeAOT runtime-pack notices above. |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools/10.0.28000.2705) | 10.0.28000.2705 | Direct reference with `PrivateAssets=all`; `.nuspec` supplies the [Windows SDK license URL](https://aka.ms/WinSDKLicenseURL), not an SPDX expression. Build tools. |
| [Microsoft.Windows.SDK.BuildTools.MSIX](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100) | 1.7.251221100 | Transitive build/packaging tools; `file: sdk_license.txt`. |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.9.0), `Microsoft.CodeCoverage`, `Microsoft.TestPlatform.ObjectModel`, `Microsoft.TestPlatform.TestHost` | 18.9.0 | Test-project graph only; `expression: MIT`; [upstream vstest](https://github.com/microsoft/vstest). |
| `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.Telemetry`, `Microsoft.Testing.Extensions.TrxReport.Abstractions` | 2.3.3 | Test-project graph only; `expression: MIT`; [upstream testfx](https://github.com/microsoft/testfx). |
| `Microsoft.ApplicationInsights` | 2.23.0 | Test telemetry dependency, absent from the app graph; `expression: MIT`; [upstream](https://github.com/Microsoft/ApplicationInsights-dotnet). |
| `Microsoft.Bcl.AsyncInterfaces`; `Microsoft.Win32.Registry`; `System.Security.AccessControl` | 6.0.0; 5.0.0; 6.0.1 | Test-project transitive dependencies absent from the app graph; `expression: MIT`; [upstream runtime](https://github.com/dotnet/runtime). |
| `xunit.v3`, `xunit.v3.assert`, `xunit.v3.common`, `xunit.v3.core.mtp-v2`, `xunit.v3.extensibility.core`, `xunit.v3.mtp-v2`, `xunit.v3.runner.common`, `xunit.v3.runner.inproc.console` | 4.0.0 | Test framework and runners; resolved metadata declares `expression: Apache-2.0`; [upstream xUnit](https://github.com/xunit/xunit). |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.0); [xunit.analyzers](https://www.nuget.org/packages/xunit.analyzers/2.0.0) | 4.0.0; 2.0.0 | Test adapter/analyzer only; `expression: Apache-2.0`. |
| [LibVLCSharp.WinUI](https://www.nuget.org/packages/LibVLCSharp.WinUI/3.10.1); [VideoLAN.LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1) | 3.10.1; 3.0.23.1 | Isolated `EmbyClient.VlcProbe` only; package metadata declares `LGPL-2.1-or-later`. Neither is in the app lock file or runtime graph. |
| `SharpDX`, `SharpDX.DXGI`, `SharpDX.Direct3D11` | 4.2.0 | Transitive LibVLC probe dependencies only. The legacy nuspec supplies `licenseUrl: http://sharpdx.org/License.txt`, without an SPDX expression. [Versioned upstream source](https://github.com/sharpdx/SharpDX/tree/v4.2.0); see the [engine decision](../architecture/playback-engine-decision.md). |

`Microsoft.Windows.SDK.BuildTools.WinApp` 0.6.1 is defined centrally but is not an app `PackageReference` or a library in the reviewed app assets. A central version declaration alone is not a resolved product dependency. The native probe shares product dependencies, while the synthetic fixture tools and their server framework are development tools. Their complete outputs are not application release inputs.

## Notice files to include in distribution

The [third-party notice directory](../../licenses/third-party/SOURCES.md) contains **29 upstream files**, plus an index and a machine-readable source/hash manifest. Local NuGet files were copied byte-for-byte. WinUIEx did not embed a license file, so its exact nuspec repository commit supplied the original text. The C#/WinRT source commit comes from the bundled DLL's file metadata. The Windows SDK license URL returned an RTF document; its original bytes and final response URL are retained rather than converted into a new license text.

The app project copies the complete `licenses/third-party/` tree into the product output under the same relative path, including `SOURCES.md` and `manifest.json`. The packaging step preserves these files in its snapshot. `Publish-Aot.ps1` also generates a [CycloneDX package SBOM](sbom.md). Do not copy `tools/EmbyClient.VlcProbe` licenses or binaries into the product, and do not add build/test-tool notices to imply those tools are distributed with it.

Upstream notice texts remain in separate versioned directories, even when their bytes are identical, to keep each package's provenance explicit. Preserve their original copyright statements, formatting, and third-party lists. This collection records supplied terms and attribution material; it does not choose a license for the repository's own code or make a legal conclusion about a planned distribution. Reconcile it with the exact release payload and upstream terms when versions or deployment mode change.
