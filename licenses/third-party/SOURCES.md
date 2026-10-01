# Third-party source notices

The original runtime-package notice snapshot was recorded on 2026-09-09; Lumen fonts/icons were added on 2026-09-30, and FFmpegInteropX/Microsoft.Windows.CsWinRT plus the native decoder fork/selected FFmpeg runtime records on 2026-10-01. Upstream texts retain their original form. This index and `manifest.json` describe provenance without changing license texts or selecting a license for the repository's own code.

The runtime notices belong to the product's resolved NuGet runtime/deployment graph and its NativeAOT/Windows projection runtime. The corresponding package versions and the distinction between runtime, build, test, and probe dependencies are recorded in [dependency-inventory.md](../../docs/implementation/dependency-inventory.md). The separate Lumen entries below describe bundled static fonts and recolored Lucide SVG icons; they are not NuGet packages.

`manifest.json` lists the retained notice files, provenance READMEs, and selected build/source records, their upstream package archives/exact source URLs or local-document provenance, and their SHA-256 hashes. This index and the manifest itself are not recursively included in that inventory. Files copied from restored NuGet packages also record the source-file hash. WinUIEx's and the FFmpegInteropX wrappers' licenses are taken from the repository commits in their nuspecs. The original C#/WinRT runtime license is taken from the commit encoded in the SDK-bundled `WinRT.Runtime.dll` product version; Microsoft.Windows.CsWinRT 2.2.0 also has its own package-supplied notices. The Windows SDK license is the unmodified RTF returned by the targeting pack's `licenseUrl`, with the final download URL retained in the manifest.

Copy this complete directory, preserving the versioned subdirectories, to `licenses/third-party/` in the product publish directory before packaging. Keep the index and manifest with the upstream texts. This directory intentionally contains no LibVLC, SharpDX, test-framework, or SDK-tool payload: those development/probe dependencies are not in the product runtime graph.

The full contents of package-supplied notice files are retained, including material for SDK components that the application may not call directly. A notice's presence records its package provenance; it does not assert that every listed component is executed by the application. Review the inventory against the final distribution after dependency or deployment changes.

The current decoder output overrides the NuGet native wrapper and FFmpeg DLLs. The [native fork record](FFmpegInteropX.NativeFork-841fc8e/README.md) identifies its Apache base and downstream changes. The [selected FFmpeg runtime record](Devenvy-FFmpeg-8.1.3.0/README.md) identifies the fixed runtime/development archives, exact build revision, complete original legal tree, supplemental notices, and component source inventory. The selected LGPLv2.1 build uses BSD-3-Clause Highway and KissFFT routes. Original NuGet notices remain historical dependency/source evidence; they do not describe replacement binaries. Source/notice inventories are not complete corresponding-source bundles or release-compliance declarations.

## Retained files

See [manifest.json](manifest.json) for the authoritative file list and direct source URLs.

| Component | Original file | Source |
| --- | --- | --- |
| CommunityToolkit.Mvvm 8.4.2 | [CommunityToolkit.Mvvm-8.4.2/License.md](CommunityToolkit.Mvvm-8.4.2/License.md) | [Upstream](https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/8.4.2/communitytoolkit.mvvm.8.4.2.nupkg) |
| CommunityToolkit.Mvvm 8.4.2 | [CommunityToolkit.Mvvm-8.4.2/ThirdPartyNotices.txt](CommunityToolkit.Mvvm-8.4.2/ThirdPartyNotices.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/8.4.2/communitytoolkit.mvvm.8.4.2.nupkg) |
| Custom FFmpegInteropX native fork | [LICENSE](FFmpegInteropX.NativeFork-841fc8e/LICENSE) and [README.md](FFmpegInteropX.NativeFork-841fc8e/README.md) | [Apache base 841fc8e79346a235906f1dbd45be53034b2e2f52](https://github.com/ffmpeginteropx/FFmpegInteropX/tree/841fc8e79346a235906f1dbd45be53034b2e2f52), tracked downstream source/build changes |
| Selected FFmpeg 8.1.3 / devenvy release 8.1.3.0 | Complete [legal tree](Devenvy-FFmpeg-8.1.3.0/legal/), [supplemental notices](Devenvy-FFmpeg-8.1.3.0/source-notices/), and [provenance](Devenvy-FFmpeg-8.1.3.0/README.md) | [Fixed release](https://github.com/devenvy/ffmpeg/releases/tag/8.1.3.0), [build source fcd83b13adbbbdf616f3a48a565f1913dbdfa403](https://github.com/devenvy/ffmpeg/tree/fcd83b13adbbbdf616f3a48a565f1913dbdfa403), and its pinned `deps.json` |
| FFmpegInteropX 2.1.0.81200 | [FFmpegInteropX-2.1.0.81200/LICENSE](FFmpegInteropX-2.1.0.81200/LICENSE) | [Exact wrapper source LICENSE](https://raw.githubusercontent.com/ffmpeginteropx/FFmpegInteropX/841fc8e79346a235906f1dbd45be53034b2e2f52/LICENSE) |
| FFmpegInteropX.Desktop.Lib 2.1.0 | [FFmpegInteropX.Desktop.Lib-2.1.0/LICENSE](FFmpegInteropX.Desktop.Lib-2.1.0/LICENSE) | [Exact wrapper source LICENSE](https://raw.githubusercontent.com/ffmpeginteropx/FFmpegInteropX/841fc8e79346a235906f1dbd45be53034b2e2f52/LICENSE) |
| FFmpegInteropX.Desktop.FFmpeg 8.1.2 | All eight files under [FFmpegInteropX.Desktop.FFmpeg-8.1.2/licenses/](FFmpegInteropX.Desktop.FFmpeg-8.1.2/licenses/) | [Exact native NuGet archive](https://api.nuget.org/v3-flatcontainer/ffmpeginteropx.desktop.ffmpeg/8.1.2/ffmpeginteropx.desktop.ffmpeg.8.1.2.nupkg) |
| FFmpegInteropX.Desktop.FFmpeg 8.1.2 | [FFmpeg-LICENSE.md](FFmpegInteropX.Desktop.FFmpeg-8.1.2/source-notices/FFmpeg-LICENSE.md) | [Exact FFmpeg source license explanation](https://raw.githubusercontent.com/FFmpeg/FFmpeg/38b88335f99e76ed89ff3c93f877fdefce736c13/LICENSE.md) |
| libiconv in FFmpegInteropX native build | [libiconv-COPYING.LIB](FFmpegInteropX.Desktop.FFmpeg-8.1.2/source-notices/libiconv-COPYING.LIB) and [libiconv-README](FFmpegInteropX.Desktop.FFmpeg-8.1.2/source-notices/libiconv-README) | [Pinned libiconv source](https://github.com/ffmpeginteropx/libiconv/tree/d601c3e8e69db72980edd4794e90177f0a74f86d); library LGPL text and library/program distinction |
| liblzma in FFmpegInteropX native build | [liblzma-COPYING](FFmpegInteropX.Desktop.FFmpeg-8.1.2/source-notices/liblzma-COPYING) | [Pinned liblzma source](https://raw.githubusercontent.com/ffmpeginteropx/liblzma/76a4518a3b74da3a9e424d4d925c1738a7eefe46/COPYING) |
| FFmpegInteropX.Desktop.FFmpeg 8.1.2 | [README.md](FFmpegInteropX.Desktop.FFmpeg-8.1.2/README.md) | Local package/source provenance, IJG attribution, and distribution requirements |
| Microsoft.Windows.CsWinRT 2.2.0 | [LICENSE](Microsoft.Windows.CsWinRT-2.2.0/LICENSE) and [NOTICE.txt](Microsoft.Windows.CsWinRT-2.2.0/NOTICE.txt) | [Exact NuGet archive](https://api.nuget.org/v3-flatcontainer/microsoft.windows.cswinrt/2.2.0/microsoft.windows.cswinrt.2.2.0.nupkg) |
| Microsoft.Web.WebView2 1.0.3719.77 | [Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt](Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3719.77/microsoft.web.webview2.1.0.3719.77.nupkg) |
| Microsoft.Web.WebView2 1.0.3719.77 | [Microsoft.Web.WebView2-1.0.3719.77/NOTICE.txt](Microsoft.Web.WebView2-1.0.3719.77/NOTICE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3719.77/microsoft.web.webview2.1.0.3719.77.nupkg) |
| Microsoft.Windows.AI.MachineLearning 2.1.74 | [Microsoft.Windows.AI.MachineLearning-2.1.74/ThirdPartyNotices.txt](Microsoft.Windows.AI.MachineLearning-2.1.74/ThirdPartyNotices.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windows.ai.machinelearning/2.1.74/microsoft.windows.ai.machinelearning.2.1.74.nupkg) |
| Microsoft.Windows.AI.MachineLearning 2.1.74 | [Microsoft.Windows.AI.MachineLearning-2.1.74/license.txt](Microsoft.Windows.AI.MachineLearning-2.1.74/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windows.ai.machinelearning/2.1.74/microsoft.windows.ai.machinelearning.2.1.74.nupkg) |
| Microsoft.WindowsAppSDK.AI 2.4.4 | [Microsoft.WindowsAppSDK.AI-2.4.4/license.txt](Microsoft.WindowsAppSDK.AI-2.4.4/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.ai/2.4.4/microsoft.windowsappsdk.ai.2.4.4.nupkg) |
| Microsoft.WindowsAppSDK.Base 2.0.4 | [Microsoft.WindowsAppSDK.Base-2.0.4/NOTICE.txt](Microsoft.WindowsAppSDK.Base-2.0.4/NOTICE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.base/2.0.4/microsoft.windowsappsdk.base.2.0.4.nupkg) |
| Microsoft.WindowsAppSDK.Base 2.0.4 | [Microsoft.WindowsAppSDK.Base-2.0.4/license.txt](Microsoft.WindowsAppSDK.Base-2.0.4/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.base/2.0.4/microsoft.windowsappsdk.base.2.0.4.nupkg) |
| Microsoft.WindowsAppSDK.DWrite 2.1.0 | [Microsoft.WindowsAppSDK.DWrite-2.1.0/license.txt](Microsoft.WindowsAppSDK.DWrite-2.1.0/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.dwrite/2.1.0/microsoft.windowsappsdk.dwrite.2.1.0.nupkg) |
| Microsoft.WindowsAppSDK.Foundation 2.3.9 | [Microsoft.WindowsAppSDK.Foundation-2.3.9/license.txt](Microsoft.WindowsAppSDK.Foundation-2.3.9/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.foundation/2.3.9/microsoft.windowsappsdk.foundation.2.3.9.nupkg) |
| Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.6 | [Microsoft.WindowsAppSDK.InteractiveExperiences-2.1.6/license.txt](Microsoft.WindowsAppSDK.InteractiveExperiences-2.1.6/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.interactiveexperiences/2.1.6/microsoft.windowsappsdk.interactiveexperiences.2.1.6.nupkg) |
| Microsoft.WindowsAppSDK.ML 2.1.74 | [Microsoft.WindowsAppSDK.ML-2.1.74/ThirdPartyNotices.txt](Microsoft.WindowsAppSDK.ML-2.1.74/ThirdPartyNotices.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.ml/2.1.74/microsoft.windowsappsdk.ml.2.1.74.nupkg) |
| Microsoft.WindowsAppSDK.ML 2.1.74 | [Microsoft.WindowsAppSDK.ML-2.1.74/license.txt](Microsoft.WindowsAppSDK.ML-2.1.74/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.ml/2.1.74/microsoft.windowsappsdk.ml.2.1.74.nupkg) |
| Microsoft.WindowsAppSDK.Runtime 2.4.0 | [Microsoft.WindowsAppSDK.Runtime-2.4.0/NOTICE.txt](Microsoft.WindowsAppSDK.Runtime-2.4.0/NOTICE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.runtime/2.4.0/microsoft.windowsappsdk.runtime.2.4.0.nupkg) |
| Microsoft.WindowsAppSDK.Runtime 2.4.0 | [Microsoft.WindowsAppSDK.Runtime-2.4.0/license.txt](Microsoft.WindowsAppSDK.Runtime-2.4.0/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.runtime/2.4.0/microsoft.windowsappsdk.runtime.2.4.0.nupkg) |
| Microsoft.WindowsAppSDK.Search 2.4.4 | [Microsoft.WindowsAppSDK.Search-2.4.4/license.txt](Microsoft.WindowsAppSDK.Search-2.4.4/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.search/2.4.4/microsoft.windowsappsdk.search.2.4.4.nupkg) |
| Microsoft.WindowsAppSDK.Widgets 2.0.5 | [Microsoft.WindowsAppSDK.Widgets-2.0.5/license.txt](Microsoft.WindowsAppSDK.Widgets-2.0.5/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.widgets/2.0.5/microsoft.windowsappsdk.widgets.2.0.5.nupkg) |
| Microsoft.WindowsAppSDK.WinUI 2.3.6 | [Microsoft.WindowsAppSDK.WinUI-2.3.6/NOTICE.txt](Microsoft.WindowsAppSDK.WinUI-2.3.6/NOTICE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.winui/2.3.6/microsoft.windowsappsdk.winui.2.3.6.nupkg) |
| Microsoft.WindowsAppSDK.WinUI 2.3.6 | [Microsoft.WindowsAppSDK.WinUI-2.3.6/license.txt](Microsoft.WindowsAppSDK.WinUI-2.3.6/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.winui/2.3.6/microsoft.windowsappsdk.winui.2.3.6.nupkg) |
| Microsoft.WindowsAppSDK 2.4.0 | [Microsoft.WindowsAppSDK-2.4.0/NOTICE.txt](Microsoft.WindowsAppSDK-2.4.0/NOTICE.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/2.4.0/microsoft.windowsappsdk.2.4.0.nupkg) |
| Microsoft.WindowsAppSDK 2.4.0 | [Microsoft.WindowsAppSDK-2.4.0/license.txt](Microsoft.WindowsAppSDK-2.4.0/license.txt) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/2.4.0/microsoft.windowsappsdk.2.4.0.nupkg) |
| System.Numerics.Tensors 9.0.0 | [System.Numerics.Tensors-9.0.0/LICENSE.TXT](System.Numerics.Tensors-9.0.0/LICENSE.TXT) | [Upstream](https://api.nuget.org/v3-flatcontainer/system.numerics.tensors/9.0.0/system.numerics.tensors.9.0.0.nupkg) |
| System.Numerics.Tensors 9.0.0 | [System.Numerics.Tensors-9.0.0/THIRD-PARTY-NOTICES.TXT](System.Numerics.Tensors-9.0.0/THIRD-PARTY-NOTICES.TXT) | [Upstream](https://api.nuget.org/v3-flatcontainer/system.numerics.tensors/9.0.0/system.numerics.tensors.9.0.0.nupkg) |
| Microsoft.NETCore.App.Runtime.NativeAOT.win-x64 10.0.9 | [Microsoft.NETCore.App.Runtime.NativeAOT.win-x64-10.0.9/LICENSE.TXT](Microsoft.NETCore.App.Runtime.NativeAOT.win-x64-10.0.9/LICENSE.TXT) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.nativeaot.win-x64/10.0.9/microsoft.netcore.app.runtime.nativeaot.win-x64.10.0.9.nupkg) |
| Microsoft.NETCore.App.Runtime.NativeAOT.win-x64 10.0.9 | [Microsoft.NETCore.App.Runtime.NativeAOT.win-x64-10.0.9/THIRD-PARTY-NOTICES.TXT](Microsoft.NETCore.App.Runtime.NativeAOT.win-x64-10.0.9/THIRD-PARTY-NOTICES.TXT) | [Upstream](https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.nativeaot.win-x64/10.0.9/microsoft.netcore.app.runtime.nativeaot.win-x64.10.0.9.nupkg) |
| WinUIEx 2.9.3 | [WinUIEx-2.9.3/LICENSE](WinUIEx-2.9.3/LICENSE) | [Upstream](https://raw.githubusercontent.com/dotMorten/WinUIEx/72f2975d2a237c0d7ad1113fe617d5894e66feb6/LICENSE) |
| Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 | [Microsoft.Windows.SDK.NET.Ref-10.0.26100.57/sdk_license.rtf](Microsoft.Windows.SDK.NET.Ref-10.0.26100.57/sdk_license.rtf) | [Upstream](https://aka.ms/WinSDKLicenseURL) |
| CsWinRT 2.2.0.48161 | [CsWinRT-2.2.0.48161/LICENSE](CsWinRT-2.2.0.48161/LICENSE) | [Upstream](https://raw.githubusercontent.com/microsoft/CsWinRT/8649ee3eeb2445ca2a36d80d878ef60b96a6c65d/LICENSE) |

## Historical FFmpeg NuGet distribution scope

The original NuGet notice set remains separate from the selected replacement runtime. Retain `bzip2.txt`, `dav1d.txt`, `ffmpeg.txt`, `iconv.txt`, `liblzma.txt`, `libxml2.txt`, `openssl.txt`, and `zlib.txt` in full. The native package's metadata declares `LGPL-2.1-or-later AND Zlib AND MIT`; the Apache license of FFmpegInteropX's wrappers does not replace the licenses of FFmpeg and its enabled native dependencies. The supplemental source texts explain the package's GPL-only iconv notice and liblzma notice without removing or editing those package originals. The [provenance README](FFmpegInteropX.Desktop.FFmpeg-8.1.2/README.md) also retains the required Independent JPEG Group attribution.

Copying these notices does not provide complete corresponding source or establish compliance with LGPL distribution requirements. Publish the applicable source materials for the shipped libraries and establish the combined application's section 6 route, including practical modified-DLL replacement when relying on dynamic linking or the applicable relinking materials for another route. Preserve build/source provenance and downstream changes with the release; see the README and [FFmpeg's official guidance](https://ffmpeg.org/legal.html).

## Lumen Static Assets

The fonts are unmodified variable TrueType files from the pinned Google Fonts
revision; only their local filenames differ. Their SHA-256 values, family names,
weight axes, and both font-internal and upstream metadata copyright notices are
retained in [Lumen-Fonts/README.md](Lumen-Fonts/README.md). Preserve that README
with both OFL texts instead of dropping the supplementary copyright provenance.

The SVGs are Lucide artwork despite the supplied handoff's Fluent-shaped
filenames. The handoff changed color, stroke width, and some filled variants and
did not identify an exact Lucide release. The complete upstream notice includes
both ISC and the Feather-derived icon list/MIT notice. See
[Lumen-Lucide/README.md](Lumen-Lucide/README.md); do not omit its MIT portion.

| Component | Retained file | Source |
| --- | --- | --- |
| Noto Serif SC | [Lumen-Fonts/NotoSerifSC-OFL.txt](Lumen-Fonts/NotoSerifSC-OFL.txt) | [Pinned Google Fonts OFL](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/notoserifsc/OFL.txt) |
| Manrope | [Lumen-Fonts/Manrope-OFL.txt](Lumen-Fonts/Manrope-OFL.txt) | [Pinned Google Fonts OFL](https://raw.githubusercontent.com/google/fonts/23e54b51ddffbc7713c583748e3bd86f62b1fa4a/ofl/manrope/OFL.txt) |
| Noto Serif SC and Manrope | [Lumen-Fonts/README.md](Lumen-Fonts/README.md) | Local asset provenance, including the pinned binary/metadata source links |
| Lucide SVG icons | [Lumen-Lucide/ISC.txt](Lumen-Lucide/ISC.txt) | [Complete pinned Lucide LICENSE](https://raw.githubusercontent.com/lucide-icons/lucide/e715245d62667c800e7f54c94b1b023692e900a3/LICENSE), including Feather/MIT notices |
| Lucide SVG icons | [Lumen-Lucide/README.md](Lumen-Lucide/README.md) | Local handoff and complete-license provenance |

These entries supplement the package notice inventory, not the NuGet-only
CycloneDX component graph. The app project copies the whole notice tree and all
`Assets/Lumen` files to its output; check the actual published payload separately.
