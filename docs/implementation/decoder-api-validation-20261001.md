# Decoder API validation: 2026-10-01

The final implementation exposes Auto, D3D11VA, Intel VPL/QSV, AMD AMF, NVIDIA NVDEC, and software decoding through a persisted native ComboBox. NativeAOT builds succeeded and all 1,041 automated tests passed (API 174, application state 111, transport 183, platform 372, playback 201).

The authorized local machine successfully decoded H.264 through NVIDIA NVDEC on RTX 4090 and Intel VPL/QSV on Intel UHD Graphics. Native observations recorded h264_cuvid and h264_qsv respectively, the decoder device identity, and positive hardware sample counts. The machine has no AMD adapter; AMF decoder registration and code integration were verified, and missing-device software fallback passed. This is not physical AMD hardware acceptance.

## Frozen artifacts

| Artifact | SHA-256 |
| --- | --- |
| Final NativeAOT application with native selector | 94FC9C25FBF0816D0B45735CE3C2B9C8195B94FF03C2A92D9B566E3443DF2D72 |
| Custom FFmpegInteropX.dll | ea6ab30dd8c7c08b615e6eb025af4042afbee262b9364658004495610e5af39f |
| FFmpeg avcodec-62.dll | bf6fe4ab928c416080be7e05aeff8c277b4d886de0f88bbf86f7d6fe87c6d203 |

The native dependency lock identifies the devenvy FFmpeg 8.1.3 runtime and matching development archives. Build receipts verify all seven FFmpeg DLLs and the custom wrapper. Both published application and probes were checked against that receipt. The application retains 127 notice files indexed by source and hash, plus a 31-component SBOM including the actual native overrides.

## Runtime controls

All thirteen selected controls passed. Evidence is under artifacts/decoder-apis/final-controls; verification-summary.json records the executable and report hashes.

| Control | Result |
| --- | --- |
| nvdec-direct | Actual NvidiaNvdec / h264_cuvid, RTX 4090, positive hardware samples; pause, seek, resume, stop, reports and network retirement passed. |
| qsv-direct | Actual IntelQsv / h264_qsv, Intel UHD, positive hardware samples; same lifecycle passed. |
| amf-device-fallback | Requested AmdAmf retained; actual software / h264, DeviceUnavailable, no decoder GPU or hardware samples. |
| d3d11-direct | Explicit D3D11VA decoded through the Windows media device. |
| auto-direct | Automatic selection decoded through D3D11VA. |
| software-direct | Forced software produced software samples with no hardware samples. |
| nvdec-hls, qsv-hls | Authenticated HLS, source-position reopen, pause restoration, resume, stop and upstream drain passed for both APIs. |
| amf-hls-fallback | Same HLS lifecycle passed while preserving the unavailable AMF request and reporting software fallback. |
| nvdec-multiaudio, qsv-multiaudio | Default-track reordering and container indices matched. One engine was reopened under the selected API, software, and the original API. |
| nvdec-same-language | Duplicate language tags did not obscure container audio stream selection. |
| qsv-codec-fallback | MPEG-4 Part 2, unsupported by the selected QSV mapping, used software with CodecUnsupported while retaining IntelQsv. |

Primary controls used the probe-clean-final executable. The codec fallback control used probe-codec-control, which relaxes only the validation option restriction and records an allowed fallback reason. Both use the identical native decoder payload; their executable hashes are in verification-summary.json.

## Native UI

The first custom Flyout API selector caused XAML fail-fast on opening in NativeAOT. The owned crash dump confirmed a failed UI delegate; it did not establish the exact callback line. The API selector was replaced with a native ComboBox containing six string/Tag items. The final candidate successfully expanded all six choices, persisted NvidiaNvdec, displayed generated video, responded to keyboard pause, and retained actual API/GPU diagnostics after stop. Screenshots are ui-api-options.png, ui-nvdec-playback.png, and ui-nvdec-diagnostics.png in the evidence root. Frozen launch metadata is in artifacts/lumen-acceptance/decoder-apis-native-selector-20261001.

An initial validation assumption that NVDEC would reject MPEG-4 Part 2 was false: the actual mpeg4_cuvid path delivered hardware samples on this RTX 4090. That failed expectation remains in nvdec-codec-fallback-fixed and is excluded from the passing control matrix. QSV supplied the unsupported-codec control instead. Earlier BtbN candidate tests and failed UI candidates remain separate from the final runtime payload.

## Limits

Media were original synthetic 720p H.264/AAC clips, MPEG-TS HLS segments, duplicated AAC tracks, and MPEG-4 Part 2 fallback media. Real Emby compatibility, additional codec profiles, 4K/HDR, physical audio/media keys, AMD hardware, long-duration resource trends, and representative performance differences need separate measurements. Vendor APIs currently transfer decoded frames to CPU-visible NV12/P010 for MediaPlayer presentation. D3D11VA retains the existing GPU-surface path; a vendor-specific API should not be assumed faster solely from its name.
