# Playback engine decision

Decision date: 2026-09-09. Status: accepted implementation direction; playback release acceptance remains incomplete.

## Decision

Use `Windows.Media.Playback.MediaPlayer` with the native WinUI `MediaPlayerElement` as the primary playback implementation. Preserve the engine boundary behind `IPlaybackEngine` and keep Emby negotiation, authentication, reporting, and cleanup in the existing API and playback layers. Keep `LibVLCSharp.WinUI` outside product dependencies at this stage.

This resolves the initial engine-selection question in the [implementation plan](windows-client-plan.md#5-playback-engine-comparison) using the available experiments. It does not certify every codec or close the plan's complete acceptance gate. Default direct/HLS lifecycle and system-control ownership checks now pass; final integrated UI and installed-runtime acceptance remain separate.

The evaluated platform is Windows x64 with .NET SDK 10.0.301, a .NET 10 Windows target, and Windows App SDK 2.4.0. Native WinUI remains the application's presentation model. No other operating system is in scope.

## Evidence and limits

| Candidate and check | Observed result | Decision implication |
| --- | --- | --- |
| Product native engine through `EmbyClient.NativeProbe` | The session-scoped HTTP relay path passes all 20 functional lifecycle cycles and the unchanged resource threshold, with +21 handles and no private-memory growth. | Use the relay for original HTTP media. Keep server, HLS, and long-duration evidence separate. |
| Official `LibVLCSharp.WinUI` 3.10.1 with `VideoLAN.LibVLC.Windows` 3.0.23.1: Release build | Zero build warnings and zero build errors. | The selected package graph compiles against the baseline. |
| Same LibVLC candidate: NativeAOT publication | Native code is generated successfully; six SharpDX trim warnings remain. | Publication alone does not establish a usable player or trim safety. |
| Same LibVLC candidate: actual published AOT executable | Native LibVLC libraries load; `VideoView` XAML initialization fails with `XamlParseException`, HRESULT `0x802B000A`. No initialized event or decoded video frame is observed. | The tested official view does not provide a working AOT playback path for this combination. |
| Same LibVLC candidate: separate JIT comparison | The same XAML initialization failure occurs before playback. | The observed XAML error has not been isolated to NativeAOT. Its underlying integration cause is unresolved. |

The native checkpoint is recorded in [implementation status](../implementation/status.md), and its scope and resource criteria are defined in the [native probe documentation](../../tools/EmbyClient.NativeProbe/README.md). That probe compiles the product's playback files through project links and exercises the real coordinator and API client against the authenticated synthetic fixture. Its functional loops cover opening, advancing native position and nonzero video dimensions, pause, seeking and resumed progress, start/progress/stop reporting, detachment, and a post-stop network observation.

The native resource gate compares the last five-loop median with loops 5-9, allowing at most 64 MiB of private-memory growth and 32 additional handles. It also rejects sustained growth across the final eight samples. These thresholds were not relaxed. Earlier managed-to-WinRT stream runs passed functional checks but failed this gate. Callback-lifetime changes reduced the growth without removing it. Native file/random-access and native HTTP controls remained within the same threshold.

The product consequently uses a session-scoped loopback HTTP relay for one fixed upstream representation. The relay retains origin-scoped upstream credentials, range reads, bounded cache, backpressure, and cancellation; the Windows player consumes a private local capability URL through its native HTTP stack. The complete product path then passed all 20 cycles, with 179 upstream partial responses, no diagnostics, and no requests after stop. The obsolete managed stream bridge was removed. This is a measured transport-path choice, not a claim that every upstream runtime issue was fully explained.

The native functional evidence is synthetic and bounded. Native clock progress and video dimensions do not prove that the first pixel frame reached the display. It does not establish real-server compatibility, audible output and device switching, HLS/transcoding, external subtitle behavior, broad codec coverage, or long-duration resource stability. The [verification plan](../research/verification-plan.md) and original playback acceptance gate continue to apply.

The LibVLC evidence is preserved in the [isolated probe instructions](../../tools/EmbyClient.VlcProbe/README.md) and [checked-in verification snapshot](../../tools/EmbyClient.VlcProbe/verification/2026-09-09.json), including warning text, exception stacks, executable hashes, fixture hashes, and process exit records. Both final processes exited with code 1 without forced termination. The JIT comparison explicitly disables AOT and trimming only for that separately named comparison; it is not a workaround claimed to satisfy the product's AOT direction. The actual AOT publisher output and executable identity support the native-compilation claim; `DynamicCodeSupported=false` alone is insufficient evidence.

## Final native ownership evidence

The initial direct relay measurements above are historical. Real HLS opening and logical seek require two source sessions per cycle. Repeatedly creating and closing a native player still exceeded the resource gate, even with a purely native HTTP control. Reusing one player in controlled experiments reduced the observed growth while retaining the original HTTP transport in the final control.

The product now assigns one `MediaPlayer` to an engine/window. Each session independently detaches the surface, clears its source, unregisters callbacks and media controls, closes its media/HTTP objects, and drains outstanding work. A replacement waits for that retirement. Concurrent engine disposal waits for the same task and closes the shared owner once. Late source cleanup is bound to its recorded use sequence.

The final, normal product configuration passed both unchanged 20-cycle gates: Direct +17 handles/+2,166,784 private bytes, and official-server HLS -6 handles/+720,896 private bytes with 40 encoding cleanups. Separate real opening cancellation, decoder rejection/recovery, old-callback/old-ID checks, and concurrent disposal also passed. [Three final receipts](../../tools/EmbyClient.NativeProbe/verification/README.md) use the same audited Native AOT executable `b4c1773df67e9265ec0a093c9d54188a79b7094f2625b97447eda1f4bbf16ac8`; they explicitly distinguish normal runs from injected faults and earlier controls. HLS stop observation covers API silence, not packet capture or displayed pixels.

## LibVLC integration findings

The official [managed package](https://www.nuget.org/packages/LibVLCSharp.WinUI/3.10.1) includes the WinUI control and core bindings; the [native Windows package](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1) supplies LibVLC separately. The managed package corresponds to repository commit `4896d0e06d19ac40b46802cf7dc167d432f3fd63`. Its .NET 10 asset depends on SharpDX.Direct3D11 4.2.0 and requests Windows App SDK 1.7.250909003 as a minimum; the probe resolves the project's required Windows App SDK 2.4.0.

The [official sample](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/samples/LibVLCSharp.WinUI.Sample/MainWindow.xaml.cs) waits for `VideoView.Initialized` and passes the event's `SwapChainOptions` into the `LibVLC` constructor. These options carry the Direct3D context and swap-chain pointers required by the view. The probe follows that sequence and records native library loading separately. The failed initialization runs do not support any comparison of decoding performance, visual quality, HDR, subtitle fidelity, or hardware acceleration.

There is also a separate source-confirmed AOT obstacle. [VideoViewBase](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/src/LibVLCSharp/Platforms/Windows/VideoViewBase.cs) uses `ComObject.As<ISwapChainPanelNative>(_panel)` when creating and destroying the swap chain. The [SharpDX 4.2.0 implementation](https://github.com/sharpdx/SharpDX/blob/v4.2.0/Source/SharpDX/ComObject.cs) calls `Marshal.GetIUnknownForObject` through this object overload. Microsoft documents that [NativeAOT does not support built-in COM on Windows](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#limitations-of-native-aot-deployment); the [.NET 10 no-COM implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Marshal.NoCom.cs) throws `PlatformNotSupportedException` for that operation. The view's path therefore requires an AOT-compatible interop replacement. This conclusion comes from source inspection: the recorded runtime failures happened earlier and did not demonstrate that specific exception.

SharpDX publication diagnostics additionally identify reflection metadata risks: IL2087 for constructor discovery, IL2075 at three interface-discovery locations, IL2072 for a parameterless constructor, and IL2070 for reflected fields. Hiding those warnings or preserving more reflection metadata would not resolve the separate built-in COM restriction.

LibVLCSharp's [release notes](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/NEWS) report core AOT support beginning in 3.9.7. Its [AOT test application](https://github.com/videolan/libvlcsharp/blob/4896d0e06d19ac40b46802cf7dc167d432f3fd63/src/LibVLCSharp.AOTCompatibility.TestApp/Program.cs) references public core types without constructing this WinUI view or playing media. Core-library compatibility must not be expanded into a claim that the complete WinUI rendering path has been verified.

## Consequences and reconsideration gates

The product-linked direct and real HLS lifecycles have passed their unchanged functional and resource criteria. Final application integration, external subtitles, physical input/output, and package activation still require their corresponding evidence. Native capabilities exposed to Emby remain conservative and tied to measured behavior; using the Windows engine does not guarantee every codec or subtitle format.

Reconsider the official LibVLC WinUI candidate when all of the following evidence is available:

1. An upstream release or separately reviewed integration resolves the actual `VideoView` XAML initialization problem on the required .NET and Windows App SDK versions, with successful execution of the published NativeAOT application.
2. The swap-chain bridge uses an AOT-supported COM/interop path, and each remaining trim diagnostic is either fixed or justified with a narrow, reviewed preservation rule and runtime evidence. Disabling AOT or trimming does not satisfy this gate.
3. The real rendering surface displays verified frames, and authenticated playback, pause, source-timeline seeking, resume, audio/subtitle selection, progress reporting, and stop/resource cleanup pass against supported Emby servers and representative media.
4. Repeated opening, playback, stopping, and window teardown meet the same lifecycle and resource gates as the native implementation. DPI changes, resizing, fullscreen, cancellation, and failure cleanup also pass the original acceptance criteria.
5. The exact native binary payload, supported architectures, packaging behavior, dependency provenance, and redistribution notices are reviewed and recorded before adding the engine to product dependencies.

Maintaining a SharpDX replacement or a third-party fork would be a separate implementation and maintenance decision. This bounded experiment makes no such change. A different LibVLC rendering adapter also needs its own WinUI composition, focus, input, lifetime, packaging, and AOT evidence before it can alter this decision.
