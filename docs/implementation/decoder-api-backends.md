# Native decoder API backends

The [2026-10-01 decoder API validation](decoder-api-validation-20261001.md) records the final NativeAOT candidate, actual Intel/NVIDIA decoding, AMF fallback, native UI, and measured limits.


Recorded on 2026-10-01. This is a source implementation record. Runtime acceptance must name the executable, native DLLs, driver, GPU, and stream. The earlier [hardware validation receipt](hardware-decoding-validation-20261001.md) establishes only its recorded common D3D11 implementation.

## ABI and ownership

The fork starts from FFmpegInteropX commit `841fc8e79346a235906f1dbd45be53034b2e2f52`. Original IDL and managed projection are unchanged. Three Cdecl exports extend the custom native DLL:

| Export | Contract |
| --- | --- |
| `EmbyDecoderApiVersion()` | Extension ABI version `1`. |
| `EmbyConfigureDecoderApi(configIUnknown, api)` | Select one API on a native `MediaSourceConfig` before opening. IDs: `Auto=0`, `D3D11=1`, `IntelQsv=2`, `AmdAmf=3`, `NvidiaNvdec=4`, `Software=5`. |
| `EmbyGetDecoderObservation(sourceIUnknown, observation, callerSize)` | Copy one native `FFmpegMediaSource` observation. Invalid pointers, sizes, or object types return an HRESULT error. |

`DecoderObservation` has 8-byte packing, a 432-byte version-1 size, and fixed UTF-16 arrays. It contains requested/actual API IDs, fallback reason, decoder GPU identity, total/hardware sample counts, and decoder name. Sources begin with `ActualApi=-1`. `VendorDecoderInterop` checks version/size and nonzero accepted sample count before using a result. Configuration is copied into per-source state; selection is not process-global.

`LumenPreferenceStore` migrates the legacy Boolean when the new string is absent. `NativePlaybackEngine` captures selection per opening. Its compatibility method `ConfigureHardwareDecoding` maps to `Auto` or `Software`. Missing vendor exports force software with an unavailable-API fallback.

## Decoder creation

`Auto` and `D3D11` retain the common FFmpegInteropX D3D11VA provider. Explicit vendor requests route through `CreateEmbyVendorVideoProvider`:

| API | FFmpeg decoder | Device/frame requirement |
| --- | --- | --- |
| Intel VPL / QSV | `*_qsv` | Derive `AV_HWDEVICE_TYPE_QSV` from an Intel (`0x8086`) D3D11 device; require `AV_PIX_FMT_QSV`. |
| AMD AMF | `*_amf` | Derive `AV_HWDEVICE_TYPE_AMF` from an AMD (`0x1002`) D3D11 device; require `AV_PIX_FMT_AMF_SURFACE`. |
| NVIDIA NVDEC | `*_cuvid` | Create `AV_HWDEVICE_TYPE_CUDA` on CUDA device `0`; require `AV_PIX_FMT_CUDA`. The CUVID implementation invokes NVDEC through the driver. |

The routing table includes H.264, HEVC, AV1, and VP9 for all three modes. QSV/NVDEC additionally route VP8, MPEG-2, VC-1, and MJPEG; NVDEC routes MPEG-1/MPEG-4, and QSV routes VVC. This is a source selection table, not a measured hardware support matrix. Decoder availability, stream profile/format, GPU generation, and driver can limit actual support.

The locked FFmpeg family enables `libvpl`, `amf`, and `ffnvcodec`. Software selection excludes hardware-only implementations and prefers `libdav1d` for AV1 when available. Compatible installed hardware/driver are required; vendor SDK developer installations are not. Driver libraries are loaded from the system and are not copied from a development installation.

## Frames and fallback

The provider observes the selected hardware format and its `AVHWFramesContext` device type before counting hardware. It uses `av_hwframe_transfer_data` to prepare CPU-visible NV12/P010 samples for the existing `MediaStreamSource`/`MediaPlayer` contract. This implementation does not provide zero-copy vendor presentation or verified HDR output.

An observation is committed only after `MediaStreamSource` accepts a sample. Counts describe delivered video samples, rather than every internal FFmpeg frame. Software samples clear current decoder GPU identity. Presentation can independently use a different GPU.

Opening failures select software with `ApiUnavailable`, `CodecUnsupported`, `DeviceUnavailable`, or `InitializationFailed`. A continuing vendor receive/transfer/sample-preparation failure attempts software and records `DecodeFailed`. A keyframe packet cache is bounded by 64 MiB, 4096 packets, and 60 seconds. Replay reconstructs decoder references without emitting samples at or before the last delivered timestamp. Without a usable cache, the provider waits for another keyframe. Seek flushes packet state. A software decoder can still fail; fallback does not guarantee every stream is playable.

Managed reasons are `DecoderApiUnavailable`, `CodecUnsupported`, `DeviceUnavailable`, `DecoderInitializationFailed`, and `DecoderRuntimeFailed`. Hardware counts remain cumulative across a runtime fallback. Interpret the current actual API together with counter changes.

## Runtime acceptance

Each explicit vendor needs successful playback, the matching actual API, its `*_qsv`/`*_amf`/`*_cuvid` implementation, increasing hardware sample counts, and identity from the vendor decoder device. Record fixture codec/profile/bit depth/resolution and driver version. Native builds and decoder listings establish binary availability; they do not establish decoded hardware frames.

Reopen the same fixture under `Software` and record increasing total samples without increasing hardware samples. Check unavailable-vendor and unsupported-codec fallback, seek, pause/resume, source replacement, stop/cancellation, authenticated HLS, and audio/subtitle selection. Identify injected runtime failures as controls. GPU utilization supplements the native observation; it does not replace it.

The development machine has RTX 4090 and Intel UHD, with no physical AMD hardware. AMF fallback there cannot establish AMD decode acceptance. No three-vendor acceptance is claimed here.

## Runtime payload

The custom wrapper replaces the NuGet native wrapper. All seven FFmpeg DLLs come from one locked FFmpeg 8.1.3 family: `avcodec-62.dll`, `avdevice-62.dll`, `avfilter-11.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, and `swscale-9.dll`. NuGet still supplies the managed projection. Archive utilities `ffmpeg.exe` and `ffprobe.exe` are not selected for product runtime output.

The lock, preparation/build scripts, and receipt are described in [native build documentation](../../native/FFmpegInteropX/README.md). The archive hash fixes its content for both runtime and matching development archives. See the [dependency inventory](dependency-inventory.md) and [runtime provenance](../../licenses/third-party/Devenvy-FFmpeg-8.1.3.0/README.md) for notices and corresponding-source requirements.
