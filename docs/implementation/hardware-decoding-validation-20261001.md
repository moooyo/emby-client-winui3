# Hardware decoding validation: 2026-10-01

The authorized local Windows run passed the final NativeAOT application build, NativeAOT product-linked probe build, 1,024 automated tests, and seven decoder/lifecycle controls. The measured hardware adapter was NVIDIA GeForce RTX 4090, driver 610.62, vendor ID 0x10DE, adapter LUID 63290. Intel and AMD use the same D3D11VA implementation and require separate device acceptance.

## Final candidate identities

| Artifact | SHA-256 |
| --- | --- |
| NativeAOT application | `867E845D971A46AF87938C7EA7854E53647507AE72DB467FF7BFE8EE9142DD4B` |
| NativeAOT product-linked probe | `E0BB13A5410658AAC7F253AD45361F745EF5FCE0B4E6927EE8FBD1BFF61C8257` |

Evidence stays under `artifacts/hardware-decoding/`. `verification-summary.json` records each control's result hash; `product-source-hashes.json` identifies the five decoder/transport source files. Final build logs are `app-final-build.log` and `probe-complete-build.log`. The application build retained one generated WinUIEx obsolete-icon warning. Neither final build introduced trimming or AOT warnings. The final payload includes the retained license notices and the 22-component package SBOM.

## Verification results

| Check | Result | Evidence |
| --- | --- | --- |
| API, application state, media transport, platform, and playback tests | 1,024 passed, zero failed/skipped | `tests-final.log` (174 + 111 + 183 + 355 + 201) |
| Authenticated direct-stream hardware playback | D3D11 on RTX 4090; pause, 20-second seek, resume, stop, reporting and network retirement passed | `direct-nvidia-final/result.json` |
| Same direct stream with hardware disabled | Actual software decoder; same lifecycle passed | `direct-software-final/result.json` |
| Authenticated HLS hardware playback | D3D11 on RTX 4090; pause, reopen at 20 seconds, resume, stop and upstream drain passed | `hls-nvidia-final/result.json` |
| HLS with hardware disabled | Actual software decoder; same lifecycle passed | `hls-software-final/result.json` |
| Two audio streams with the second marked default | Container indices 1 and 2 mapped to native indices 1 and 0; languages en and fr matched | `multiaudio-nvidia-final/result.json` |
| Two audio streams with the same language | Container indices remained unambiguous after default-track reordering | `multiaudio-same-language-final/result.json` |
| Automatic fallback with MPEG-4 Part 2 | Hardware remained requested; actual software decoding and HardwareFallback=true passed for both audio streams | `automatic-fallback-final/result.json` |

The multi-audio controls reused one engine across hardware, software, and hardware openings. They confirmed that a changed preference affects the next opening and that the reused MediaPlayer does not retain the preceding decoder policy.

The 183 media-transport tests include authenticated playlist/segment/key/init-resource rewriting, same-origin redirects, ranges, cancellation, and five resource-reclamation controls. A simulated sliding playlist advanced through 300 windows beyond the configured resource capacity without accumulating historical mappings. Other cases preserved VOD future segments, two-minute retirement grace, active requests, recently accessed resources, and unique capability paths.

An earlier direct-stream hardware/software comparison also sampled process-specific Windows GPU engine counters. The hardware process used the VideoDecode engine on the same adapter LUID, reaching 2% utilization; the software control reported 0% VideoDecode utilization. Raw observations are nvidia-gpu-samples.json and software-gpu-samples.json. These establish the decoder paths for a low-complexity synthetic 720p clip. Representative CPU reduction and 4K/HDR performance require appropriate media measurements.

## Native UI observations

Computer Use exercised the normal application with an isolated, normally protected synthetic account. Turning hardware decoding off wrote HardwareDecoding=false; subsequent playback and diagnostics showed actual software decoding. Turning it on wrote true, and the final application restored that preference after restart. The final candidate displayed synthetic video frames and responded to keyboard pause. Diagnostics retained the actual decoder and GPU after stop.

Screenshots and UI receipts remain in the evidence root. ui-settings.png and ui-software-diagnostics.png belong to the earlier frozen application, identified in ui-interim.json. ui-final-hardware-playback.png and ui-final-hardware-diagnostics.png belong to the final application hash above. Frozen launch receipts and payload inventories are under artifacts/lumen-acceptance/hardware-app-final-20261001/.

## Scope

Runtime media were locally generated H.264/AAC solid-color clips, MPEG-TS HLS segments, duplicate synthetic AAC audio streams, and an MPEG-4 Part 2 fallback control. Real Emby transcoding, additional GPU models/drivers, other codec profiles, 4K, HDR, audible output, and physical media keys need their own evidence. The direct-stream control exercised the real playback coordinator; HLS and multi-audio controls constructed engine requests against owned authenticated loopback endpoints. Previous native-system-decoder acceptance requires renewed verification for the FFmpeg decoder path.
