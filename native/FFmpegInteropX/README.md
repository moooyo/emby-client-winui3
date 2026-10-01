# Native decoder API fork

This directory contains the Apache-2.0 FFmpegInteropX source from commit
`841fc8e79346a235906f1dbd45be53034b2e2f52`, with a Windows x64 build project and
per-source Intel QSV, AMD AMF, and NVIDIA NVDEC selection. The original IDL and
FFmpegInteropX 2.1.0 managed projection ABI are unchanged. The native library adds
three C exports: `EmbyDecoderApiVersion`, `EmbyConfigureDecoderApi`, and
`EmbyGetDecoderObservation`. Configuration belongs to each `MediaSourceConfig`;
observations belong to each `FFmpegMediaSource`.

## Build

Use PowerShell 7 on Windows with Visual Studio x64 C++ tools and Windows SDK
10.0.26100.0 installed:

```powershell
./scripts/Prepare-DecoderDependencies.ps1
./scripts/Build-DecoderNative.ps1
```

The scripts discover the current Visual Studio installation through `vswhere`.
They verify every downloaded archive against `dependencies.lock.json`, reuse the
NuGet cache when available, and generate C++/WinRT headers with the locked
2.0.250303.1 tool. WinAppSDK 2.4.0's Foundation, InteractiveExperiences, WinUI, and
WebView2 metadata are projection inputs; the application's existing WinAppSDK
runtime still supplies those APIs. No CUDA SDK or vendor SDK installation is
required for this build.

The release wrapper links the MSVC C++ runtime statically (`/MT`), so this fork
does not add a requirement for separately installed Visual C++ runtime DLLs.
FFmpeg remains dynamically linked and separately replaceable.

Output is under `artifacts/decoder-apis/native/runtime`. The build receipt records
source hashes, the toolchain, the archive hash, and output hashes. An unchanged
source fingerprint and verified output hashes permit reuse. `-Force` reruns the
native build. All generated headers, objects, downloaded packages, and binaries
remain under ignored artifact directories.

`DecoderNative.targets` is imported by the application and native probe. It
prepares the native build before compilation and copies the custom wrapper and
all seven FFmpeg DLLs after Build and Publish. The NuGet package remains the
managed projection provider; its native decoder payload is replaced. Do not mix
FFmpeg DLLs from the original NuGet payload with this distribution.

## Locked FFmpeg distribution

The selected distribution is the Windows x64 LGPLv2 pair from
[devenvy/ffmpeg release 8.1.3.0](https://github.com/devenvy/ffmpeg/releases/tag/8.1.3.0):

| Archive | SHA256 |
|---|---|
| `ffmpeg-8.1.3-win-x64-lgplv2.tar.gz` | `f437415870b78d6dad38b92de5fc3c62f3a5186a6e86ad1ebc0f2b97d45094e2` |
| `ffmpeg-8.1.3-win-x64-lgplv2-dev.tar.gz` | `c7c5a50d2afbe64bd4b06000403c0b0e929c35fdac0a1c3aa623d03bc82d7beb` |

The scripts reject content that differs from either lock. Runtime files, headers,
and MSVC import libraries are normalized into
`artifacts/decoder-apis/dependencies/ffmpeg-devenvy-8.1.3`; another FFmpeg
distribution is never overlaid into that directory. The complete runtime
`legal` directory is preserved byte-for-byte.

The runtime reports FFmpeg `8.1.3`. Its configuration enables `libvpl`, `amf`,
`ffnvcodec`, and `libdav1d`, while explicitly disabling GPL, nonfree code, and
external autodetection. It does not enable version3. The decoder inventory
contains the real `*_qsv`, `*_amf`, and `*_cuvid` implementations. These are
separate APIs from D3D11VA. VPL and the other external build dependencies are
incorporated into this FFmpeg family; there is no separate VPL DLL in its runtime
directory. NVIDIA and AMD driver runtime DLLs are dynamically loaded from
installed drivers and are not copied from the development machine into the
product. The pinned build recipe is
`fcd83b13adbbbdf616f3a48a565f1913dbdfa403` in devenvy/ffmpeg; its chromaprint
configuration uses the BSD-licensed KissFFT backend.

The provider can transfer hardware frames to system memory for the existing
Windows media presentation path. A system-memory output frame therefore does
not establish software decoding. Diagnostics must use the observed decoder API
and codec, and must retain the fallback reason when a requested vendor cannot
open or continue decoding.

## Distribution requirements

The selected FFmpeg payload is LGPL-2.1-or-later. The wrapper's Apache license
does not cover the FFmpeg payload or the external libraries listed in its build
configuration. Preserve the exact archive licenses, external notices,
`SOURCE_OFFER.txt`, source revisions, and build configuration when preparing a
release. The package retains GPLv2 text because LGPLv2.1 refers to it; its presence
is not evidence that GPL-only components were enabled.

The lock and original upstream links are provenance records, not a complete
corresponding-source bundle or a completed distribution compliance review.
Release materials must provide the corresponding library sources and build
scripts for the exact shipped binaries, including external components and any
patches. Keep FFmpeg separately dynamically linked and practically replaceable
by an interface-compatible modified build when using the shared-library route.
The wrapper fork's tracked source and build scripts must accompany its binary.
Consult [FFmpeg's distribution guidance](https://ffmpeg.org/legal.html) and the
retained licenses when establishing the release-specific route.

Source and build inventory do not prove codec support on every device. Intel and
AMD runtime acceptance requires suitable hardware and drivers. Build receipts,
decoder listings, runtime controls, and UI evidence describe distinct scopes.
