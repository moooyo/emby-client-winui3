# Configurable hardware video decoding

The [2026-10-01 decoder API validation](decoder-api-validation-20261001.md) records the final NativeAOT candidate, actual Intel/NVIDIA decoding, AMF fallback, native UI, and measured limits.


Implementation date: 2026-10-01. This describes source behavior and provenance for the decoder API extension. The [backend record](decoder-api-backends.md) gives its native boundaries and acceptance criteria. The earlier [D3D11 validation receipt](hardware-decoding-validation-20261001.md) describes its recorded executable and common D3D11 path; it does not establish acceptance of the new QSV, AMF, or NVDEC providers.

## Playback pipeline

`NativePlaybackEngine` uses a source fork of FFmpegInteropX, based on [commit 841fc8e79346a235906f1dbd45be53034b2e2f52](https://github.com/ffmpeginteropx/FFmpegInteropX/tree/841fc8e79346a235906f1dbd45be53034b2e2f52), to demux and decode into a `MediaPlaybackItem`. Windows `MediaPlayer` owns timing, audio output, system media integration, and presentation through the WinUI `MediaPlayerElement`. Emby negotiation, progress reporting, source replacement, and retirement keep their existing ownership.

`FFmpegInteropX` 2.1.0.81200 and `FFmpegInteropX.Desktop.Lib` 2.1.0 retain the unchanged managed projection. Product output replaces the native wrapper and FFmpeg payload with the custom `FFmpegInteropX.dll` and one complete, locked FFmpeg 8.1.3 shared-library family. `FFmpegInteropX.Desktop.FFmpeg` 8.1.2 remains in the NuGet graph, but its DLLs are superseded. Mixing the two FFmpeg families is unsupported. See the [dependency inventory](dependency-inventory.md), [native build record](../../native/FFmpegInteropX/README.md), and [current runtime provenance](../../licenses/third-party/Devenvy-FFmpeg-8.1.3.0/README.md).

## Preference and fallback

`LumenPreferences.VideoDecoderApi` is a persisted string, defaulting to `Auto`. Playback settings offer:

| Value | Decoder path | Failure behavior |
| --- | --- | --- |
| `Auto` | Existing FFmpeg D3D11VA provider using the Windows media device | Fall back to FFmpeg software. This is not a search across vendor APIs. |
| `D3D11` | Explicit common D3D11VA provider | Fall back to FFmpeg software. |
| `IntelQsv` | Real FFmpeg `*_qsv` implementation with an Intel VPL/QSV device | Fall back to FFmpeg software if the API, codec, device, initialization, or continuing decode fails. |
| `AmdAmf` | Real FFmpeg `*_amf` implementation with an AMD AMF device | Same software fallback policy. |
| `NvidiaNvdec` | Real FFmpeg `*_cuvid` implementation with a NVIDIA CUDA/NVDEC device | Same software fallback policy. |
| `Software` | FFmpeg software decoder | No hardware decoder is requested. A GPU can still present video. |

Files without the string migrate `HardwareDecoding=true` to `Auto`, and `false` to `Software`. A known string takes precedence; invalid loaded values normalize to the default, while saving invalid or conflicting values is rejected. The legacy Boolean stays synchronized for compatibility. Each opening captures its own API. `ConfigureVideoDecoderApi` affects subsequent opens without interrupting the current session.

Intel and AMD derive vendor contexts from D3D11 devices filtered to vendor IDs `0x8086` and `0x1002`. Their codec implementation and frame type remain QSV or AMF. NVIDIA creates CUDA device `0` and reads identity from that context; the settings do not expose a per-adapter selector. A missing/incompatible native extension forces software for explicit vendor requests with `DecoderApiUnavailable`.

Fallback changes the local decoder, not Emby negotiation or server transcoding. It can consume more CPU and does not promise smooth playback of every stream. The first vendor implementation transfers hardware frames with `av_hwframe_transfer_data` into system memory, then feeds CPU-visible NV12/P010 samples through the existing presentation path. It is hardware decoding with a frame transfer, not zero-copy presentation. The common D3D11 provider retains its surface path. `HdrSupport.Disabled` remains configured; P010 output does not establish verified HDR presentation or refresh-rate matching.

## Authenticated media and HLS

FFmpeg receives a private loopback capability URL. Original media uses `SessionHttpRelay`, progressive transcoding uses `ProgressiveHttpRelay`, and HLS uses `HlsHttpRelay`. HLS child manifests, segments, initialization resources, and keys are rewritten to session capabilities and requested through `ScopedMediaTransport`. Credentials, origin validation, cancellation, and failure classification stay in managed transport. Sliding playlists release retired URI mappings after a two-minute grace period; current and future VOD references remain available.

FFmpeg protocols are limited to `http,tcp,crypto`, with a 15-second read timeout; the relay handles upstream HTTPS. Retirement disposes the source and relay. A late source creation result is disposed when its session was replaced. Lifecycle and authenticated HLS checks must use the new payload; historical receipts do not accept a new executable.

## Actual decoder and GPU diagnostics

`VideoDecodingSnapshot` separates requested policy from native observations. Actual results are committed after a decoded sample is accepted by `MediaStreamSource`; before then they can remain `Pending`.

| Field | Meaning |
| --- | --- |
| `RequestedApi` | API captured for this opening. |
| `ActualApi` | `D3D11`, `IntelQsv`, `AmdAmf`, `NvidiaNvdec`, `Software`, or `Pending`. |
| `Decoder` | Actual API's display label: `D3D11`, `QSV`, `AMF`, `NVDEC`, `Software`, or `Pending`. |
| `NativeDecoderName` | FFmpeg implementation name, such as `h264_qsv`, `h264_amf`, or `h264_cuvid`. |
| `DecodedFrames`, `HardwareDecodedFrames` | Cumulative accepted video-sample counts, not every internal frame. Hardware counts require the expected hardware frame and device type. |
| `HardwareRequested`, `HardwareFallback` | Whether hardware was requested and whether the observed current result is software. |
| `FallbackReason` | API, codec, device, initialization, or runtime decode failure for a software fallback. |
| `Codec` | Allowlisted stream codec label, separate from implementation name. |
| `GpuName`, `GpuVendorId`, `AdapterLuid` | Actual decoder device identity when available. Software results omit a decoder GPU. |

QSV/AMF identity comes from the vendor-filtered D3D11 device used to derive the context. NVDEC identity comes from its CUDA device, including a LUID when exposed. D3D11 identity comes from the media D3D11 device and DXGI adapter. An installed GPU, preference, decoder listing, or renderer device is insufficient proof of hardware decoding.

Refresh diagnostics after playback advances. Acceptance requires the matching actual API/implementation, increasing hardware sample counts, matching decoder vendor/device, and successful playback. Reopen the same fixture under `Software` and record increasing total samples without new hardware samples. Also check pause/resume, seek, stop, source replacement, HLS authorization, cancellation, audio/subtitles, and runtime fallback. Hardware counts can remain nonzero after fallback, so inspect the current API and counter changes together.

Development hardware includes RTX 4090 and Intel UHD, with no physical AMD adapter. An AMF software fallback on this machine is not AMD hardware acceptance. This source record makes no three-vendor acceptance claim.

## Source and distribution provenance

The original IDL and C#/WinRT projection ABI are preserved. Three versioned C exports configure per-source selection and read observations, as described in [decoder-api-backends.md](decoder-api-backends.md).

The selected runtime is `devenvy/ffmpeg` release `8.1.3.0`, with FFmpeg `8.1.3`, runtime archive SHA-256 `f437415870b78d6dad38b92de5fc3c62f3a5186a6e86ad1ebc0f2b97d45094e2`, and matching development archive SHA-256 `c7c5a50d2afbe64bd4b06000403c0b0e929c35fdac0a1c3aa623d03bc82d7beb`. The build source is pinned to `fcd83b13adbbbdf616f3a48a565f1913dbdfa403`. It enables VPL/AMF/FFNVCODEC and disables GPL/nonfree code; the selected LGPLv2.1 lane uses BSD-3-Clause Highway and KissFFT routes. The Apache wrapper license does not cover FFmpeg or incorporated external libraries.

Preserve the complete notice tree in published output, including the original runtime `legal/` tree and supplemental header/component notices. Release materials must provide applicable corresponding source for the exact FFmpeg binaries and incorporated libraries, including changes and build/install scripts. Establish the LGPLv2.1 section 6 route and practical replacement with interface-compatible FFmpeg DLLs if relying on section 6(b). The wrapper fork's tracked source, changes, and scripts must accompany its provenance. The [current runtime record](../../licenses/third-party/Devenvy-FFmpeg-8.1.3.0/README.md) is a notice/source inventory, not a corresponding-source bundle or completed distribution-compliance declaration.
