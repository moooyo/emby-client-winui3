# Isolated LibVLC WinUI and NativeAOT probe

This Windows x64 research project evaluates the official LibVLCSharp WinUI control with the application's .NET 10 and Windows App SDK versions. It is deliberately isolated from the product, solution, central package versions, and production playback engine. It does not establish product compatibility merely by compiling.

## Pinned dependencies

| Dependency | Version |
| --- | --- |
| .NET SDK, from the repository `global.json` | 10.0.301 |
| Target framework | net10.0-windows10.0.26100.0 |
| Microsoft.WindowsAppSDK | 2.4.0 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 |
| LibVLCSharp.WinUI | 3.10.1 |
| VideoLAN.LibVLC.Windows | 3.0.23.1 |
| Transitive SharpDX, SharpDX.DXGI, SharpDX.Direct3D11 | 4.2.0 |

The project disables central package version management only for itself and records its dependency graph in `packages.lock.json`. Default builds keep `PublishAot`, `PublishTrimmed`, the AOT analyzer, the trim analyzer, and the CsWinRT AOT optimizer enabled. No warnings are suppressed. `TrimmerSingleWarn=false` expands the publisher's aggregate warning into individual diagnostics; it does not disable analysis.

## Reproduce

Run these commands from the repository root in PowerShell. The fixture generator must have produced the synthetic MP4 and its JSON metadata first; see [the generator instructions](../EmbyClient.MediaFixtures/README.md). The probe accepts only that generated filename and checks the metadata's synthetic flag and file size before loading media.

```powershell
dotnet build .\tools\EmbyClient.VlcProbe\EmbyClient.VlcProbe.csproj -c Release -p:Platform=x64
dotnet publish .\tools\EmbyClient.VlcProbe\EmbyClient.VlcProbe.csproj -c Release -p:Platform=x64 -p:TrimmerSingleWarn=false -o .\tools\EmbyClient.VlcProbe\artifacts\aot
.\tools\EmbyClient.VlcProbe\run-probe.ps1 -MediaPath .\tools\EmbyClient.MediaFixtures\artifacts\fixture-h264-aac.mp4 -Mode Aot
```

The script publishes before running, opens a 480 by 300 window without activating it, starts muted at volume zero, and holds the process handle until exit. The application allows 30 seconds for its playback checks; the runner enforces a separate 45-second process deadline and terminates only the process it started if necessary. An existing output directory is rejected to preserve previous evidence. The script records `publish.log`, `process-id.txt`, `process-result.json`, and `result.json` under an ignored `artifacts` directory. Failure produces a nonzero script exit and a failed result rather than a passing smoke test.

The intended playback gate requires `VideoView.Initialized`, accepted playback, at least two seconds of progress, positive decoded and displayed frame counts, a video output, pause, a seek to four seconds followed by resumed progress, no playback error event, and disposal. Playback remains muted. Frame statistics are engine observations and do not constitute visual inspection or a broad codec compatibility suite.

A separately labeled JIT comparison is available:

```powershell
.\tools\EmbyClient.VlcProbe\run-probe.ps1 -MediaPath .\tools\EmbyClient.MediaFixtures\artifacts\fixture-h264-aac.mp4 -Mode JitBaseline
```

Only this comparison passes `PublishAot=false` and `PublishTrimmed=false` on its command line. Its outputs are not NativeAOT or trimming compatibility evidence, and it does not change the project defaults. A following default Release restore/build restores the AOT dependency graph if the comparison changed the lock file. A plain `dotnet` launch of the built DLL is not the supported WinUI launch path; use the published executable with its application manifest.

## Recorded result: September 9, 2026

The final source compiled with **zero build warnings and zero build errors**. NativeAOT publication succeeded and generated native code. Its trim analysis produced six SharpDX warnings:

| Code | Location in SharpDX 4.2.0 | Concern |
| --- | --- | --- |
| IL2087 | `CppObject.FromPointer<T>`, line 143 | Reflection construction lacks constructor preservation annotations. |
| IL2075 | `ShadowContainer.Initialize`, lines 55, 76, and 112 | Interface discovery lacks the required preserved interface metadata. |
| IL2072 | `ShadowContainer.Initialize`, line 95 | Reflection construction lacks a preserved public parameterless constructor. |
| IL2070 | `ResultDescriptor.AddDescriptorsFromType`, line 254 | Reflected fields lack preservation annotations. |

Both the actual NativeAOT executable and the separate JIT executable loaded the packaged native LibVLC libraries successfully, then failed during the official `VideoView` XAML initialization with `XamlParseException`, HRESULT `0x802B000A`. Neither reached `VideoView.Initialized`, created a LibVLC playback instance, or decoded a video frame. Both processes exited with code 1 and required no forced termination. The matching JIT failure means this observed XAML failure has **not** been isolated to NativeAOT. Its underlying XAML/WinUI package integration cause remains unresolved.

The checked-in [verification snapshot](verification/2026-09-09.json) contains the observed exception stacks, warning text, executable and synthetic fixture hashes, and process results. Repository paths in that snapshot are normalized to `<repository>`. Full generated outputs remain ignored. `DynamicCodeSupported=false` is recorded as a runtime feature flag; it is not by itself proof of native compilation because an IL application's runtime configuration can also disable that feature. The AOT claim additionally relies on the native publisher output and the executable hash in the snapshot.

An earlier code-created view waited 30 seconds without an initialized event. The final probe uses a XAML-declared view like the official sample and reports the explicit load exception. No player performance, codec coverage, hardware acceleration, HDR, subtitle, or long-duration resource claim can be made from these failed initialization runs.

## Official integration and AOT findings

The official NuGet manifests confirm the pinned [managed WinUI package](https://www.nuget.org/packages/LibVLCSharp.WinUI/3.10.1) and [native Windows package](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1). The managed package's repository commit is `4896d0e06d19ac40b46802cf7dc167d432f3fd63`; its .NET 10 asset depends on SharpDX.Direct3D11 4.2.0 and requests Windows App SDK 1.7.250909003 as its minimum dependency. This probe explicitly resolves Windows App SDK 2.4.0.

The [official sample](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/samples/LibVLCSharp.WinUI.Sample/MainWindow.xaml.cs) waits for `VideoView.Initialized` and supplies `e.SwapChainOptions` to the `LibVLC` constructor. These options carry the D3D context and swap chain pointers. This probe follows that ordering and explicitly uses `Core.Initialize` with the packaged `libvlc/win-x64` directory to distinguish native library loading from view initialization.

There is also a separate source-confirmed NativeAOT obstacle: [VideoViewBase](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/src/LibVLCSharp/Platforms/Windows/VideoViewBase.cs) calls `ComObject.As<ISwapChainPanelNative>(_panel)` during swap chain creation and destruction. [SharpDX 4.2.0](https://github.com/sharpdx/SharpDX/blob/v4.2.0/Source/SharpDX/ComObject.cs) implements this object overload through `Marshal.GetIUnknownForObject`. Microsoft documents that [NativeAOT does not support built-in COM on Windows](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#limitations-of-native-aot-deployment), and the [.NET 10 no-COM implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Marshal.NoCom.cs) throws `PlatformNotSupportedException` for that call. This path would need replacement; preserving reflection metadata alone cannot address it. The runtime runs here failed earlier and did not observe that particular exception.

LibVLCSharp's [release notes](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/NEWS) announce core AOT support in 3.9.7. The official [AOT test application](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/src/LibVLCSharp.AOTCompatibility.TestApp/Program.cs) references public core types without creating this WinUI view or playing media. That evidence does not establish complete WinUI playback compatibility.

## Architecture recommendation

Retain the current native Windows playback engine as the first implementation and keep LibVLC outside product dependencies. Treat the official LibVLC WinUI integration as an unsuccessful candidate for this .NET 10, Windows App SDK 2.4.0, and NativeAOT combination until its XAML initialization, SharpDX interop, and trimming issues have a supported resolution. The project remains Windows-only and native WinUI.

Reconsider the candidate after an upstream change or a separately reviewed integration can initialize the actual published AOT view and pass real playback, seeking, track selection, subtitles, cleanup, and repeated lifecycle checks. Replacing the SharpDX bridge or maintaining a fork is a separate engineering decision, not a small package switch. This bounded experiment does not alter third-party source or attempt that replacement.
