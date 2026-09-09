# Official Emby 4.9.5.0 API validation

Date: 2026-09-09. Result: **40 passed, 0 failed, 0 blocked** using the .NET 10 `win-x64` NativeAOT `ApiProbe` executable against the official Emby Server 4.9.5.0 Linux amd64 release.

The probe directly referenced the current `EmbyClient.Api` and `EmbyClient.Playback` projects. It used the application's conservative device profile and actual public API methods. The server ran in a WSL user/network namespace with only loopback interfaces/routes; the Windows endpoint was a loopback-only relay at `127.0.0.1:19096`. The Debian package was extracted without installing packages or registering services. [Environment and package verification](README.md)

The test library contained only the project's generated H.264/AAC color-and-sine-wave clip, approximately 60.01 seconds long, and generated English SRT cues. A dedicated nonadministrator local account was used. No Emby Connect account or Premiere key was supplied.

| Area | Observed result |
| --- | --- |
| Server and authentication | Public and authenticated server information reported 4.9.5.0. Valid credentials succeeded; wrong credentials failed; the logged-out token was rejected. |
| User and session | Current user, capability registration, and separate session identity succeeded. |
| Browsing | Views, items, latest/resume/next-up endpoints, details, search, an empty result, and pagination completed successfully. Empty collection results only establish their response contracts. |
| User state | Favorite and played booleans were toggled, read back, restored, and verified. Resume position began and ended at zero. Historical counters/timestamps cannot be restored through the current public client. |
| Original streaming | An authenticated range request returned HTTP 206 and 4,096 bytes with a validated `Content-Range`. |
| HLS conversion | A forced HLS negotiation succeeded. Two playlist levels were read, with 21 media segment references. An actual 82,532-byte MPEG-TS segment returned HTTP 200 and valid packet sync bytes. |
| External subtitles | Subtitle stream index 2 negotiated external delivery. SRT-to-WebVTT returned HTTP 200, `text/vtt`, 175 bytes, a `WEBVTT` header, and timed cues. |
| Lifecycle and cleanup | Start/progress/stop reports and corresponding session readbacks passed. Cleanup completed for the original, HLS, and subtitle negotiations. |

The sanitized machine-readable report is written to ignored `artifacts/emby-validation/api-probe-report.json`. The preparation ledger and namespace snapshot are also retained in that ignored directory. The tracked source for reproducing the checks is in [ApiProbe](ApiProbe/README.md). Raw local logs and credentials are not part of the committed evidence.

## Compatibility defect found and corrected

The first actual-client authentication attempt returned HTTP 400. A controlled comparison against this same official server found that chunked JSON authentication failed while the same JSON body with a known `Content-Length` succeeded. The client previously used unbuffered `JsonContent`.

The API implementation was updated to serialize through its source-generated `JsonTypeInfo` into `ByteArrayContent`, preserving NativeAOT compatibility while sending a known content length. Both JSON POST helpers use the shared content factory. The complete NativeAOT probe passed after this change. This is observed behavior of the tested server/version and is not a claim that every HTTP server rejects chunked request bodies.

## Evidence boundaries

These results establish the exercised API contracts and authenticated HTTP media delivery on one official server version. Playback reports were synthetic, paused/muted API probes. The report explicitly records native UI, decoder execution, first frame, audio output, and subtitle rendering as `NotRun`. Those require the separate Windows application's native acceptance checks.

The one-clip library does not establish broad codec, container, HDR, hardware-decoding, multi-track, large-library, server-version, reverse-proxy, or production-network compatibility. Docker startup was not exercised on this machine because Docker was unavailable; the WSL namespace route was the actual test environment.

## Later environment-loss observation

At 2026-09-08 22:53:59 UTC (2026-09-09 06:53:59 local), the Windows client showed a playback-report warning near a natural playback end, followed by an unreachable-server state. Inspection found that TCP `127.0.0.1:19096` refused connections, the original tool execution handle no longer existed, and the original WSL runtime directory had disappeared. No original server logs were available because its exit-only archive had not been written. These observations establish loss of the validation service; they do not establish which progress/stop/encoding-cleanup request failed first, nor an HTTP 404 cleanup response.

The disposable server was safely recreated in a new isolated namespace after confirming that no listener occupied the port. The subsequent run used an independent hidden Windows WSL process, a new server identity and regenerated temporary credentials, and continuous diagnostic snapshots. No production/user server was modified. The earlier 40-check API result remains a completed observation of the first official server run; later native UI results must be associated with the appropriate server generation.

## Additional source and episode indexing evidence

The replacement server generation (`cf4feb10df224135877fc61204a28212`) was extended with an independent generated movie and TV library while preserving original movie `5`:

| Media | Actual identifiers and properties |
| --- | --- |
| Track Validation | Primary movie `8`; source `mediasource_8` is 1280 × 720 / 60.034 seconds; source `mediasource_7` is 854 × 480 / 60.100 seconds. Both are H.264/MP4. |
| English audio | Stream index `1`, AAC, stereo, 48 kHz, default. Decoded samples strongly match the generated 440 Hz target. |
| French audio | Stream index `2`, AAC, stereo, 48 kHz. Decoded samples strongly match the generated 880 Hz target. |
| Validation Series | TV library `9`, series `11`, season `12`. |
| First episode | `S01E01`, item `14`, source `mediasource_14`, approximately 60.01 seconds. |
| Second episode | `S01E02`, item `13`, source `mediasource_13`, approximately 60.01 seconds. |

The movie list returned only the default source; both user-item details and `PlaybackInfo` returned both grouped versions. The filenames follow [Emby's multi-version movie convention](https://emby.media/support/articles/Movie-Naming.html#multi-version-movies). The TV files follow [Emby's episode naming conventions](https://emby.media/support/articles/TV-Naming.html#episode-naming-conventions). The original fixture and both episode copies had the same SHA-256.

Both episodes were unplayed when inspected. The current user's series-filtered `NextUp` result was empty before any episode playback. No watched state was changed merely to manufacture a next-up result. The native client must use episode numbers for ordering because server-assigned IDs were `14` then `13`.

The actual-source properties are recorded in ignored `multitrack-media-evidence.json`, `multitrack-api-evidence.json`, `episode-media-evidence.json`, `episode-api-evidence.json`, and `episode-user-evidence.json`. Native switching and automatic playback completion are separate acceptance evidence.

## HLS resume timeline observation

Native audio-switch acceptance exposed a timeline mismatch near source position 17.57 seconds. The app displayed that resumed position while the video showed the beginning of the source. Inspection of the active French-audio transcode showed that `StartTimeTicks=175666667` reached the server's actual media-playlist request, with audio index 2 and source `mediasource_8`. FFmpeg selected audio input `0:2`, used `-copyts -start_at_zero`, did not use `-ss`, and generated segments numbered from zero. Its first segment represented source seconds 0–3.

A separate diagnostic session requested position 17 seconds with video/audio stream copy disabled. Its source response explicitly contained `IsInfiniteStream=false`, `RunTimeTicks=600340000`, `TranscodingSubProtocol=hls`, `TranscodingContainer=ts`, `RequiresOpening=false`, `RequiresClosing=false`, and `Protocol=File`. The returned master URL preserved `StartTimeTicks=170000000` and passed it through to the media URL.

The served HTTP media playlist contained `PLAYLIST-TYPE:VOD`, `MEDIA-SEQUENCE:0`, `EXT-X-START:TIME-OFFSET=17`, 21 segment references, and `ENDLIST`. That start tag is a preferred starting position within the complete playlist; it is not evidence that the source was trimmed. The underlying FFmpeg-generated playlist on disk did not contain the server-added VOD/start tags, so the disk playlist alone does not represent the HTTP response.

Decoding the first actual HTTP segment produced RGB `(31, 88, 174)`, identical to source position zero. Source position 17 seconds produced RGB `(186, 55, 67)`. Thus this finite HLS route retains the full source timeline. Treating engine position zero as source position 17 merely by adding an offset causes the observed mismatch; the native player must explicitly seek within the full playlist timeline. The first MPEG-TS packet timestamps have their own transport baseline and must not be mistaken for source-time offsets.

The diagnostic session cleaned up only its own encoding job and logged out its own device session. Its sanitized response/tag/color evidence is in ignored `hls-timeline-evidence.json`. An initial stream-copy diagnostic received a server HTTP 500 despite the short remux process completing; the successful timeline comparison used the same software-transcode fallback shape as the observed client session. Progressive converted streams, third-party HLS, and infinite streams were not established by this check and must not inherit this VOD-specific conclusion.
