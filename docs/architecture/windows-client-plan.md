# Windows Client Implementation Plan

Research date: 2026-09-09.

Status: proposed architecture, based on official documentation, upstream source, and published NuGet metadata. No application has been implemented or executed. No local build, test, runtime probe, or compatibility verification was performed. Version availability and source inspection do not establish runtime compatibility.

## 1. Recommended direction

Build a Windows desktop application in C# with WinUI 3, Windows App SDK, WinUIEx, and CommunityToolkit.Mvvm. Use native XAML controls and Windows window management throughout the application. Keep Emby protocol handling independent of the UI and of the playback engine.

The first technical milestone should compare two small playback adapters: Windows `MediaPlayerElement` as the native baseline, and the official `LibVLCSharp.WinUI` integration as the leading candidate for broader direct playback. Select the production engine only after the playback and packaging acceptance gate below. Keep mpv as a documented alternative if subtitle rendering or other measured playback requirements justify its extra integration and distribution work.

This is a Windows-only product. Cross-platform UI frameworks, browser-based application shells, and shared mobile presentation layers are outside the proposed architecture. Separate projects exist to isolate responsibilities and support testing, not to create a cross-platform product.

## 2. Version baseline and evidence

The following are research snapshots, not a tested dependency lock file. Recheck support and package metadata when scaffolding the solution, then pin the chosen versions centrally and record the successful combination.

| Component | Candidate baseline | Evidence and decision |
| --- | --- | --- |
| .NET | .NET 10 LTS, latest supported servicing patch | Microsoft lists .NET 10 support through 2028-11-14. The page reviewed listed 10.0.11. .NET 8 and 9 both approach end of support on 2026-11-10, so they are poor new-project defaults. |
| Windows App SDK | `Microsoft.WindowsAppSDK` 2.4.0 stable | The official stable channel and release notes list the 2026-08-13 release. The NuGet package also uses version 2.4.0. |
| WinUIEx | `WinUIEx` 2.9.3 | Published metadata targets `net8.0-windows10.0.19041` and depends on `Microsoft.WindowsAppSDK.WinUI` 1.8.250906003. Resolve it under the selected application SDK and verify compatibility; its package version is independent of the Windows App SDK version. |
| MVVM | `CommunityToolkit.Mvvm` 8.4.2 | Use observable state, generated commands, and asynchronous commands. It is UI-framework independent and can be used by WinUI 3. |
| Additional XAML controls | Selected `CommunityToolkit.WinUI.*` 8.2.251219 packages | Stable versions were observed for SettingsControls, Extensions, Behaviors, and Animations. 8.3 packages observed were previews. Add individual packages when needed. |
| DI, logging, configuration, HTTP | `Microsoft.Extensions.DependencyInjection`, `Logging`, `Options`, and `Http`, aligned with .NET 10 | Use constructor injection and managed HTTP lifetimes. Add `Microsoft.Extensions.Hosting` only when background-service lifetime management is useful. |
| Native playback baseline | `Microsoft.UI.Xaml.Controls.MediaPlayerElement` and `Windows.Media.Playback.MediaPlayer` | Native control composition and Windows media integration; actual format support depends on the OS, installed codecs, device, and stream. |
| Broad playback candidate | `LibVLCSharp.WinUI` 3.10.1 plus `VideoLAN.LibVLC.Windows` 3.0.23.1 | Both packages exist on NuGet. The managed view package does not include the native engine. Compatibility with this project's SDK baseline remains unverified. |
| Interop if needed | Windows interop APIs; a narrowly scoped generated or maintained binding | Introduce HWND/Direct3D bindings only in the Windows playback or platform layer. Do not spread P/Invoke calls through view models. |

Sources: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [Windows App SDK release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels), [Windows App SDK 2.0 series release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0?pivots=stable#version-240), [WinUIEx package metadata](https://api.nuget.org/v3-flatcontainer/winuiex/2.9.3/winuiex.nuspec), [MVVM package versions](https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/index.json), [SettingsControls package versions](https://api.nuget.org/v3-flatcontainer/communitytoolkit.winui.controls.settingscontrols/index.json), [MVVM Toolkit documentation](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/), and [Windows Community Toolkit setup](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/getting-started).

### Windows App SDK naming must not be conflated

The official release page includes the short excerpt: "Version 2.4.0 Released: August 13, 2026". The servicing table groups this release under the **2.0 product series**, with end of servicing on **2027-04-29**. The 1.8 series reaches its documented end of servicing on the research date, **2026-09-09**.

The published [`Microsoft.WindowsAppSDK` 2.4.0 manifest](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/2.4.0/microsoft.windowsappsdk.nuspec) contains these distinct package versions:

| Package | Version recorded in the aggregate package manifest |
| --- | --- |
| `Microsoft.WindowsAppSDK` | `2.4.0` |
| `Microsoft.WindowsAppSDK.WinUI` | `2.3.6` |
| `Microsoft.WindowsAppSDK.Foundation` | `2.3.9` |
| `Microsoft.WindowsAppSDK.Base` | `2.0.4` |
| `Microsoft.WindowsAppSDK.Runtime` | `[2.4.0]`, exact dependency |

Use the aggregate stable package first. A later decision to use smaller component packages must preserve the documented dependency set and deployment behavior. Do not independently force every component to the same numeric version.

### Toolchain and operating systems

The current [Microsoft WinUI quick start](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) documents Visual Studio 2026 with the WinUI application development workload, and a .NET 10 command-line template workflow. Pin the SDK in `global.json`, use central package management, and record the Windows SDK and toolchain used by the remote build environment. The proposed target framework is `net10.0-windows10.0.26100.0`; the actual supported OS floor must be configured independently and proven with the selected dependencies.

Use serviced Windows 11 releases as the primary product and UI acceptance target. Consider Windows 10 compatibility separately if needed. The SDK's historical ability to run on Windows 10 1809 does not mean that every chosen dependency supports that floor or that Microsoft still services that OS. WinUIEx's currently published target already raises a practical dependency consideration to Windows build 19041. Do not advertise an OS support matrix until it is exercised.

Start with Windows x64. Keep ARM64 in project and packaging design, but publish a native ARM64 package only after every native playback dependency, codec module, and hardware test is available. Do not infer ARM64 native-binary availability from a managed project's list of runtime identifiers. x86 is deferred unless there is a concrete user requirement.

## 3. Native UI and community packages

Use `NavigationView` for the application shell, a `Frame` or explicit page navigation service, `GridView` or another virtualized collection control for media collections, `AutoSuggestBox` for search, `InfoBar` for actionable failures, and native `ContentDialog`, `MenuFlyout`, and settings controls. Retain WinUI spacing, typography, focus visuals, accessibility semantics, light/dark themes, and high-contrast behavior.

WinUIEx should own the practical window conveniences: a `WindowEx` root window, window persistence, size and position helpers, and HWND access where required. Prefer current Windows App SDK APIs for features already provided by the platform. In particular, the upstream WinUIEx README marks its old TitleBar control as deprecated in favor of the Windows App SDK TitleBar. Use system backdrops where supported; the player itself should normally provide an opaque dark viewing surface. See [WinUIEx features](https://github.com/dotMorten/WinUIEx) and [WindowEx documentation](https://dotmorten.github.io/WinUIEx/concepts/WindowEx.html).

Use `CommunityToolkit.WinUI.Controls.SettingsControls` for structured settings only when it improves the application. Add Behaviors or Extensions for an identified need, and keep standard WinUI animation as the first choice. The current toolkit repository is [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows). Its component packages use `CommunityToolkit.WinUI.*` for WinUI 3; do not start with the old umbrella `CommunityToolkit.WinUI` 7.x package or the archived WindowsCommunityToolkit repository.

Treat poster loading as an application service: request server-sized images, bound disk and memory caches, key cache entries by server and image identity, and cancel requests when virtualized cells are recycled. Avoid downloading full-resolution backdrops for collection thumbnails. UI virtualization is a requirement for large libraries, not a later optimization.

## 4. Suggested solution boundaries

The names below are proposed project boundaries; no solution is created by this document.

| Project or module | Responsibility | Must not depend on |
| --- | --- | --- |
| `EmbyClient.App` | WinUI views, view models, navigation, application composition root, themes, accessibility | Raw Emby endpoint construction inside views |
| `EmbyClient.Core` | Domain identifiers, application use cases, playback contracts, error categories | WinUI, a specific media engine |
| `EmbyClient.Emby` | HTTP transport, Emby DTOs, protocol serialization, authentication context, library and playback API facades | UI types and media-engine objects |
| `EmbyClient.Playback.Windows` | Native media adapter and presentation surface integration | Emby HTTP route definitions |
| `EmbyClient.Playback.Vlc` | The selected VLC integration, native lifetime, event translation, stream capabilities | View-model navigation and Emby authentication ownership |
| `EmbyClient.Platform.Windows` | Credential persistence, application settings, Windows media commands, power/display management | A particular Emby server version |
| `EmbyClient.Tests` | Contract fixtures, coordinator behavior, pure application logic | A locally installed production Emby server |

Start with fewer physical projects if that avoids premature overhead; preserve these dependency directions in namespaces and interfaces. The UI can stay entirely Windows-specific while the protocol and playback state logic remain easy to exercise remotely.

Use `ObservableObject`, generated observable properties, and `AsyncRelayCommand` for view-model state. Pass cancellation through search, pagination, detail loading, and playback negotiation. Marshal engine callbacks to `DispatcherQueue` before changing UI-bound state. Do not use an application-wide messenger as a substitute for explicit ownership of a playback session.

Constructor injection should resolve services from one composition root. Suggested lifetimes are: application settings and playback coordinator for the application lifetime; server/account context for a connection lifetime; view models for a page or navigation lifetime; and engine/media objects for their owning playback surface/session. A DI scope alone does not make native callbacks or windows safe to dispose.

`IHttpClientFactory` supports named or typed HTTP clients and handler pooling. Keep credentials on individual requests or on a strictly isolated server client; do not mutate shared default headers while switching accounts. Avoid retaining a transient typed client in a singleton indefinitely. Log sanitized request information rather than complete authenticated streaming URLs. These decisions follow the [official HTTP client factory guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory).

If using the [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host), explicitly connect host startup and shutdown to the WinUI application lifecycle. Do not block the XAML dispatcher with a console-style `Run()` call. Initially, direct DI plus asynchronous disposal is sufficient unless real hosted services justify a host.

## 5. Playback engine comparison

| Option | Practical advantages | Important limits | Proposed role |
| --- | --- | --- | --- |
| Windows `MediaPlayerElement` | Native XAML surface, transport controls, normal WinUI composition, system media integration | Device/OS codec availability varies; advanced subtitles, audio passthrough, HDR output, and individual containers need evidence | First baseline and possible MVP engine when server transcoding is acceptable |
| `LibVLCSharp.WinUI` + native LibVLC | Official WinUI view now exists; managed playback API; a broad media engine; swap-chain-based view integration | Newer integration with older SDK and archived SharpDX dependencies; native package size; rendering and teardown must be proven | Leading broad-playback PoC candidate |
| `LibVLCSharp` core + dedicated Win32 child window | Uses documented `MediaPlayer.Hwnd`; avoids depending on the WinUI view package | Native-window airspace, input, focus, DPI, clipping, and overlay design become application work | Alternative if the official swap-chain path fails |
| libmpv + Win32 child window | Documented embedding and asynchronous client API; strong candidate for advanced playback needs | Custom C interop and event loop; no official WinUI control established here; native-binary sourcing and license composition matter | Later option driven by measured failures or requirements |
| libmpv render API + custom composition bridge | More control over rendering ownership | The inspected render API exposes OpenGL and software render backends, not a ready WinUI Direct3D swap-chain adapter | Research only; not an MVP shortcut |

The [native MediaPlayerElement documentation](https://learn.microsoft.com/en-us/windows/apps/design/controls/media-playback) describes media transport controls and system media integration. The [Windows codec documentation](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/supported-codecs) explicitly makes device codec availability relevant: HEVC and AV1 may require optional codec packages, and AC-3 is no longer included by default starting with Windows 11 24H2. Its timed-text list includes more than plain text subtitles, but a format appearing in that list is not a guarantee of ASS styling fidelity, embedded font handling, or correct behavior for every container and media pipeline.

Do not advertise universal codec support, lossless audio passthrough, Dolby Vision, Dolby Atmos, HDR passthrough, or full ASS compatibility for any candidate. Test these as separate capabilities. Do not infer HDR output correctness from successful decoding or from an engine's general HDR feature list.

### 5.1 Official LibVLCSharp.WinUI route

This package is real as of the research date. The [official WinUI README](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp.WinUI/README.md), [published 3.10.1 manifest](https://api.nuget.org/v3-flatcontainer/libvlcsharp.winui/3.10.1/libvlcsharp.winui.nuspec), and [official WinUI sample](https://github.com/videolan/libvlcsharp/tree/3.x/samples/LibVLCSharp.WinUI.Sample) establish a supported project direction rather than an imagined package name.

The source uses `Microsoft.UI.Xaml.Controls.SwapChainPanel` under the `WINUI` compilation flag and a Direct3D 11 swap chain. It exposes an `Initialized` event carrying `SwapChainOptions`; the upstream sample passes those options when constructing `LibVLC`. This is a different route from setting a player HWND. See [VideoViewBase source](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp/Platforms/Windows/VideoViewBase.cs) and [sample initialization](https://github.com/videolan/libvlcsharp/blob/3.x/samples/LibVLCSharp.WinUI.Sample/MainWindow.xaml.cs).

The published package already contains the core LibVLCSharp APIs. Do not add a second core `LibVLCSharp` package indiscriminately alongside it: check assembly identity and dependency resolution before dividing adapters among projects. The native `VideoLAN.LibVLC.Windows` package is still required separately.

Specific risks found in source and metadata:

- The 3.10.1 package depends on Windows App SDK 1.7.250909003, while the upstream sample uses 1.8.251106002. Neither establishes compatibility with the proposed 2.4.0 application baseline.
- It depends on SharpDX.Direct3D11 4.2.0; [SharpDX is archived](https://github.com/sharpdx/SharpDX). This is a maintenance risk to track, not evidence that the integration cannot work.
- The inspected swap-chain source selects `B8G8R8A8_UNorm`. Treat this path as an SDR candidate until HDR behavior is independently established; do not promise a 10-bit HDR surface.
- Surface initialization, resize, DPI changes, unload, and player disposal must be coordinated. The upstream sample is illustrative and should not be copied as complete lifecycle handling.

Prefer this route for a small remote PoC before writing a custom native host. Prove XAML transport controls, flyouts, dialogs, subtitle visibility, GPU behavior, and repeated teardown on the exact selected package versions.

### 5.2 HWND fallback route for VLC or mpv

The window handle returned by `WinRT.Interop.WindowNative.GetWindowHandle(window)` identifies the top-level WinUI window. It is not the handle of a `Grid` or a video placeholder. Microsoft's [HWND interop documentation](https://learn.microsoft.com/en-us/windows/apps/develop/ui-input/retrieve-hwnd) describes this distinction.

A feasible custom host design is:

1. Create and own a dedicated Win32 child rendering window under the appropriate desktop window. Give it a clear lifetime separate from the XAML placeholder.
2. Translate the placeholder's layout rectangle from device-independent units into the correct native-window coordinate space. Update it on layout, DPI, move, fullscreen, visibility, and navigation changes.
3. Assign that child handle to `LibVLCSharp.Shared.MediaPlayer.Hwnd`, or use mpv's documented `wid` option. mpv creates its own video window parented to the supplied HWND. Follow the upstream Win32 handle conversion rules instead of assuming every integer conversion is safe.
4. Route keyboard and pointer input deliberately. Ensure media shortcuts, focus restoration, fullscreen exit, and accessible transport controls remain usable.
5. Stop rendering and detach native callbacks before destroying the rendering window. Own native handles and callbacks in the adapter, never in a view model.

VLC's [MediaPlayer source](https://github.com/videolan/libvlcsharp/blob/3.x/src/LibVLCSharp/Shared/MediaPlayer.cs) exposes `Hwnd`; mpv's [window options](https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst) document `wid` behavior.

Native child windows introduce a separate rendering and hit-test surface. Do not assume a higher XAML `ZIndex` makes a control appear above the video. For the first HWND PoC, keep native XAML transport controls in a non-overlapping strip. If overlay controls are required, explicitly investigate an owned overlay window or a composition-based renderer and test z-order, activation, dialogs, screen transitions, and accessibility. A WinUI `Grid` is not a ready-made equivalent of WPF's `HwndHost` or `WindowsFormsHost`.

Avoid CPU-frame-copy rendering as the default 4K path. It changes the performance problem and does not establish HDR support. Likewise, libmpv's [render API](https://github.com/mpv-player/mpv/blob/master/include/mpv/render.h) is not an immediate Direct3D-to-WinUI bridge; it has explicit graphics-context and threading requirements.

## 6. Playback orchestration and Emby integration

The application should own one `PlaybackCoordinator` above the engine adapter and Emby API facade. The coordinator negotiates with Emby, creates an engine request, maps events back to server progress, and handles teardown. The engine must not independently invent Emby URLs or report server sessions.

Suggested contracts:

| Contract | Responsibility |
| --- | --- |
| `IPlaybackEngine` | Open, play, pause, seek, stop, rate, volume, track selection, engine events, disposal |
| `IPlaybackSurface` | Attach and detach a Windows rendering surface; window/UI thread ownership |
| `PlaybackRequest` | Resolved URI, narrowly scoped request headers, start position, chosen streams, delivery method |
| `PlaybackCapabilities` | Measured containers, codec/profile/level limits, subtitle delivery, seeking, rate, and output capabilities |
| `PlaybackSessionContext` | Server, account, item, media source, play session, live stream, selected stream IDs, timeline origin |
| `IPlaybackSessionReporter` | Server start/progress/stop reports with ordering and cancellation |

Use the following sequence as the conceptual contract; exact routes, DTO fields, and conditional live-stream steps are specified in the local API documents:

1. Load the item and available media sources for the selected account.
2. Request playback information using the selected engine's conservative capability profile and current network/quality settings.
3. Select a candidate from the returned `MediaSources` using the user's edition choice, the source's `Supports*` flags, effective engine capabilities, and returned URLs. The client chooses the delivery path; there is no assumed single server-selected mode field. Honor opening requirements for that source and resolve server-relative paths without discarding a reverse-proxy base path.
4. Create the engine request with the required authorization. Native playback libraries do not automatically inherit the application's `HttpClient` headers; authenticated HLS manifests, segments, redirects, and subtitles need explicit verification.
5. Report playback start when actual playback begins. Send periodic progress plus meaningful state changes; serialize or coalesce reports to avoid out-of-order positions.
6. Preserve the canonical item timeline through seeking, resume, and restarted transcodes. Keep Emby ticks as 64-bit integers and convert engine time units at the boundary.
7. On engine or network failure, classify the cause. Renegotiate once with a narrower capability/quality profile when justified, then present an actionable error. Stop and clean up the old server session/stream before replacing it; prevent fallback loops.
8. On completion, navigation, logout, window close, or cancellation, issue the appropriate stop and conditional stream cleanup operations. Cancellation before successful engine start must also release any server resources already allocated.

Model states such as `Idle`, `Negotiating`, `Opening`, `Playing`, `Paused`, `Buffering`, `Seeking`, `Stopping`, `Ended`, and `Failed`. Do not derive the whole state machine from a single `IsPlaying` Boolean. Distinguish seeking support for direct media, static streams, and restarted transcodes.

The server capability profile must describe the effective engine and output path, not the broadest capabilities advertised by its vendor. Audio track indexes, subtitle indexes, and IDs from an engine must be mapped explicitly to Emby stream indexes. Track changes that require remuxing, subtitle burning, or transcoding should trigger server renegotiation and a controlled resume.

## 7. Windows platform services

Persist tokens using a Windows-protected credential mechanism behind `ICredentialStore`; keep ordinary account names and non-secret settings separate. A practical candidate is Windows Credential Manager or user-scoped DPAPI. Do not place passwords or access tokens in plain JSON, diagnostic exports, command lines, or image cache filenames.

Provide a stable installation/device identifier and isolate caches and settings by server and user. Log transport failure categories, engine/version information, and sanitized media characteristics. Keep full URLs, personal library contents, and account secrets out of default logs.

Connect system media commands to the playback coordinator. The native engine path provides platform integration; custom engines require a Windows media-control bridge with coherent metadata and command state. Handle display-sleep inhibition only while actively playing video, and release it on pause/stop according to the chosen user policy. Test minimize, suspend/resume, network loss, multi-monitor moves, and audio-device changes.

## 8. Packaging, open-source distribution, and CI

### Packaging recommendation

Use packaged MSIX as the primary installed distribution model, initially x64. Plan a Microsoft Store route and signed direct download only when an actual release is prepared. A self-contained unpackaged ZIP can be a later option for users who need portable deployment; it is not a reason to skip testing package identity and native-library discovery.

Windows App SDK deployment and .NET deployment are separate decisions. A self-contained .NET publish does not by itself prove that the Windows App SDK runtime or the native media engine is included. Document all runtime and native plugin dependencies for every artifact.

For a Store build, framework-dependent Windows App SDK deployment is the natural first candidate. For direct downloads, either distribute the required runtime dependencies correctly or choose self-contained deployment and accept responsibility for republishing security/servicing updates. Preserve native-engine directory structure where the engine expects plugins. Single-file publishing is deferred until native extraction, paths, startup, and package restrictions are proven.

Sources: [Windows packaging options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/) and [Windows App SDK deployment models](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview).

### License decisions before release

Choose the application repository's license before accepting substantial outside contributions. Keep that choice distinct from the license and redistribution conditions of each shipped binary.

| Dependency or distribution | Evidence | Release implication |
| --- | --- | --- |
| WinUIEx and Windows Community Toolkit | Upstream MIT licenses | Retain required notices. |
| `LibVLCSharp.WinUI` 3.10.1 | NuGet license expression `LGPL-2.1-or-later` | Track the exact managed package, modifications, and applicable redistribution obligations. |
| `VideoLAN.LibVLC.Windows` 3.0.23.1 | NuGet license expression `LGPL-2.1-or-later` | Audit the exact native payload and plugin/dependency licenses; preserve notices and provide required corresponding-source/relinking materials as applicable. |
| mpv | Upstream default is GPLv2-or-later; an LGPL build requires excluding GPL-only code and satisfying all included dependency licenses | Select an intentional compatible distribution route. A build flag or an ISC-licensed header alone does not relicense the engine. |
| FFmpeg and other native dependencies | Determined by the actual selected build and enabled components | Keep build provenance, licenses, and source obligations with the release. |

Sources: [WinUIEx license](https://github.com/dotMorten/WinUIEx/blob/main/LICENSE), [Windows Community Toolkit license](https://github.com/CommunityToolkit/Windows/blob/main/License.md), [LibVLCSharp.WinUI manifest](https://api.nuget.org/v3-flatcontainer/libvlcsharp.winui/3.10.1/libvlcsharp.winui.nuspec), [LibVLC native package manifest](https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/3.0.23.1/videolan.libvlc.windows.nuspec), [mpv copyright file](https://github.com/mpv-player/mpv/blob/master/Copyright), and [VideoLAN redistribution guidance](https://www.videolan.org/legal.html).

The implementation should make a release inventory possible: exact package/native versions, checksums, upstream source revisions, bundled notices, and an SBOM. Open-source copyright licenses and codec/patent/trademark questions are separate. Review the concrete selected distribution before publication rather than assuming that the managed wrapper's license covers every native codec module.

### Remote verification and CI

The task's verification policy requires connecting through `ssh test-env` and executing verification only there unless the user explicitly authorizes local verification for the task. This research task did not perform those checks. If the remote environment is unavailable, report verification as blocked and do not fall back to the local machine.

For implementation, first establish whether `test-env` provides the Windows toolchain and interactive GPU session needed by each gate. A headless or non-Windows host can exercise portable protocol logic but cannot prove WinUI rendering, native Windows decoding, HDR output, UI automation, or MSIX behavior. If required remote Windows facilities are absent, record those gates as blocked.

A future repository CI plan should contain:

- A Windows build lane with a pinned .NET SDK, Windows SDK, native architectures, package lock, and reproducible artifact metadata.
- Protocol/serialization and coordinator tests without production credentials, plus a separate authorized Emby integration lane using a disposable account and media fixtures.
- An interactive Windows/GPU acceptance lane for rendering, accessibility, fullscreen, codec/subtitle matrices, and device transitions.
- A packaging lane that installs, upgrades, uninstalls, and verifies native dependency discovery on clean supported Windows images.
- A release-only signing lane with protected secrets, checksums, notices, SBOM, and provenance. Pull requests must not receive signing or Emby credentials.

[GitHub's runner documentation](https://docs.github.com/en/actions/reference/runners/github-hosted-runners) currently lists Windows x64 and ARM64 options. Runner labels and preinstalled tools change; pin and document a suitable image, and do not mistake a successful hosted build for GPU playback acceptance. This document proposes CI only; it does not create, authorize, or run a workflow.

## 9. First PoC acceptance gate

The PoC should be a small vertical slice: connect to one authorized Emby server, authenticate, choose one item, negotiate playback, play it in a WinUI window, and report progress/stop. Do not build a large media-browser UI before resolving the rendering and session-lifecycle risks.

| Gate | Required evidence |
| --- | --- |
| Dependency compatibility | A remote Windows build and launch using the pinned .NET, Windows App SDK, WinUIEx, Toolkit, and engine packages; no unresolved package/assembly conflicts |
| Direct playback baseline | H.264/AAC MP4 plays with working pause, resume, seek, volume, and position reporting |
| Server delivery | At least one direct-stream/remux scenario where available and one forced transcode play correctly; HLS requests remain authorized |
| Session correctness | Start/progress/stop reach the server in the right order; resume position is correct; cancellation and errors clean up allocated resources |
| Native composition | Transport controls, menus, dialogs, and captions remain visible and interactive on the selected rendering route |
| Window behavior | Resize, minimize/restore, fullscreen entry/exit, and movement between 100%, 150%, and 200% DPI displays do not leave stale surfaces or steal keyboard focus |
| Track behavior | Select at least two audio tracks and external SRT/WebVTT subtitles; map engine tracks to server stream indexes correctly |
| Complex subtitles | Record results for styled ASS with embedded fonts and PGS independently; unsupported cases must use a verified server fallback or be clearly unsupported |
| Color and audio | Record SDR correctness first; evaluate HDR-to-SDR, HDR display output, multichannel audio, and passthrough separately without promoting untested features |
| Lifetime | Complete at least 20 open/play/stop cycles without a crash or persistent native resource growth; close the app while opening and while playing |
| Packaging | Install and play from a clean-machine MSIX artifact without relying on developer PATH values or an existing VLC installation |
| Security | Tokens do not appear in logs, exception UI, exported diagnostics, or command lines; account switching cannot reuse another account's session |
| Accessibility | Core flow is usable with keyboard and Narrator; focus is restored after dialogs and fullscreen transitions |

Record engine choice with the tested media characteristics, Windows/GPU/driver versions, output path, package versions, and known failures. Timing and performance targets should be based on this measured baseline; there is no performance result from the research stage.

## 10. Delivery roadmap

| Phase | Scope | Exit condition |
| --- | --- | --- |
| P0: architecture and protocol PoC | Toolchain compatibility, native versus VLC comparison, authentication, one item, full session lifecycle, MSIX | First PoC gate passes and an engine decision is recorded |
| P1: usable library client | Manual server/account sign-in, libraries, continue watching, latest items, search, details, movies/episodes, basic source/audio/subtitle/quality selection, a transient local queue, favorites, watched state, basic settings | A user can sign in, find media, choose a supported delivery/track, play it, stop, and resume consistently |
| P2: playback quality | Refined media-version and track UX, wider subtitle/codec support, robust fallback, automatic next episode, advanced queue behavior, media keys, diagnostics | A published capability matrix matches measured behavior |
| P3: release hardening | Accessibility, large-library performance, cache limits, network recovery, signing, updater strategy, license inventory, clean-machine tests | Reproducible installable release with documented support matrix |
| P4: optional features | Live TV, music refinements, remote control, playlists, offline downloads, ARM64 release, advanced rendering | Each feature has its API contract, server-resource lifecycle, and acceptance evidence |

Downloads, Live TV, casting/remote-control targets, server administration, and automatic updater implementation should not expand the first milestone. A library player can establish its core value before these features introduce additional state and support obligations.

## 11. Remaining decisions and unverified assumptions

1. Which current Emby server versions and reverse-proxy configurations will form the compatibility matrix?
2. Does `LibVLCSharp.WinUI` 3.10.1 behave correctly with Windows App SDK 2.4.0 and the selected WinUIEx/Toolkit versions?
3. Is the official swap-chain path adequate for required overlay, accessibility, SDR/HDR, and teardown behavior, or is an HWND fallback necessary?
4. Which subtitle styles and fonts, audio passthrough formats, hardware decoders, and display outputs are actually required for the first release?
5. Is the product Windows 11 only, or will a separately tested Windows 10 compatibility tier be maintained?
6. Are native ARM64 playback binaries with suitable provenance and licenses available for the selected engine build?
7. Does `test-env` have the required Windows, GPU, audio, display, and MSIX facilities for verification?
8. Which application license, signing identity, release channel, and native-distribution terms will be selected?

These are implementation and release gates, not claims of completed functionality. The available documentation supports the proposed direction; the exact combined stack and playback behavior still require remote verification.
