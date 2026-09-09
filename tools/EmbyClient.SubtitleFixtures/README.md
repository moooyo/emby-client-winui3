# Complex subtitle fixtures

This tool supports the Windows-only client's validation by creating two original test movies from the existing generated sixty-second H.264/AAC clip: genuine embedded ASS with an attached OFL font and genuine embedded HDMV PGS. The media-generation commands do not start Emby, create a library, change application settings, install a font, or run the native player. Separate server binding and diagnostic commands below require their own coordinated execution window.

Run only in an authorized local verification window with PowerShell 7:

```powershell
./tools/EmbyClient.SubtitleFixtures/Prepare-Dependencies.ps1
./tools/EmbyClient.SubtitleFixtures/Generate-Fixtures.ps1
```

Preparation requires the previously downloaded and verified official Emby 4.9.5.0 Windows portable package at `artifacts/emby-validation/portable-4.9.5.0/system` and its original archive at `artifacts/emby-validation/downloads/embyserver-win-x64-4.9.5.0.7z`. Generation requires `tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds/fixture-h264-aac.mp4` and its matching JSON metadata. It reuses that clip without re-encoding its audio or video or overwriting it.

`Prepare-Dependencies.ps1` downloads the fixed official SeConv v5.1.0 release and pinned Bungee Shade font. It verifies the release digest, every extracted package file against the verified archive, each font/license Git blob identity, and both existing Emby media executables against the fixed SHA-256 values in `SOURCES.md`. `Generate-Fixtures.ps1` independently rejects an Emby dependency manifest whose version/archive/tool hashes differ from those pins, then checks the actual executables against the accepted hashes before launching them. `artifacts/dependencies.json` records source URLs, exact versions, local paths, SHA-256 hashes, and the bundled license path. Downloads and generation results stay under this tool's ignored `artifacts` directory.

Each generation uses a fresh `fixtures-<UTC>-<random>` directory. Failed attempts remain available. `artifacts/last-successful-directory.txt` names the latest completed generation, including:

| Output | Purpose |
| --- | --- |
| `media/Styled ASS Validation (2026)/Styled ASS Validation (2026).mkv` | Original video/audio, embedded ASS stream, attached Bungee Shade TTF |
| `media/PGS Bitmap Validation (2026)/PGS Bitmap Validation (2026).mkv` | Original video/audio and actual PGS bitmap subtitle stream |
| `source/styled.ass`, `source/pgs.srt`, `source/pgs.sup` | Original caption source and generated Blu-ray SUP |
| `ass-reference.png`, `pgs-reference.png` | Locally rendered comparison frames at 41 seconds |
| `extracted-attachment.ttf` | Font extracted from the actual MKV and checked against the pinned font hash |
| `logs` | Bounded tool stdout/stderr, FFprobe stream/packet evidence and font-selection output |
| `manifest.json` | Content identities, hashes, cue intervals, references, and intentionally unbound server fields |
| `reference-sanity.json` | Basic colored-pixel checks to reject blank reference images; no native-player verdict |
| `licenses/BungeeShade-OFL.txt` | License accompanying the font embedded in the ASS fixture |

The ASS validation cue is `ATTACHED FONT CHECK` from 35 to 45 seconds. It uses Bungee Shade at 72 ASS units, green fill, magenta outline, and explicit upper-left placement. Rendering reads the MKV directly without an external `fontsdir`; logs must select `BungeeShade-Regular`. The extracted attachment hash proves which font was packaged. A later server playback screenshot must independently show its distinctive glyphs.

The PGS cue is `PGS BITMAP CHECK` over the same interval, with white fill, black outline, and bottom-center placement. SeConv renders the original SRT to `bluraysup`; FFprobe must report `hdmv_pgs_subtitle` for both SUP and MKV. This is not a DVD/VobSub or DVB substitute. PGS muxing uses `-copyts` and verifies all four display/clear packet timestamps remain exactly 2, 8, 35, and 45 seconds. The local graphical-subtitle reference render uses `-fix_sub_duration`; without that option, this Emby FFmpeg version can retain an unspecified subtitle duration and produce a blank reference. This option is a fixture-rendering requirement, not evidence about Emby's eventual server command.

SeConv uses the available Windows Arial font to rasterize PGS. Its pixels can vary with the installed Arial version; each generated SUP, MKV, and reference receives its own hash. No Arial font file is copied or embedded. MKV container IDs can also vary between runs, so repeatability means the recorded inputs, stream identities, timing, and visual behavior, not identical MKV bytes across generations.

## Bind to the owned server in a separate authorized window

For the existing cleanly stopped runtime, `wsl_resume_official_server.py` checks the private runtime UUID/owner, stopped state, absence of its previous process IDs and socket, available loopback port, and original official package hash. It archives prior diagnostic files without deleting them and invokes the established namespace runner with the same program data. The original server identity, users, media, and libraries are retained. The wrapper holds the shared start lock and continuously copies diagnostics to a new ignored `artifacts/emby-validation/wsl-resume-runs` directory. Keep it alive in the foreground, or use the documented hidden Windows `Start-Process` pattern for a longer native validation window.

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.SubtitleFixtures/wsl_resume_official_server.py
```

Once public identity and namespace ownership are confirmed, stage one completed generation and bind it. Replace the example generation directory with the actual `last-successful-directory.txt` value; adapt the repository path if needed.

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.SubtitleFixtures/wsl_stage_complex_media.py --manifest /mnt/d/Code/emby-client-winui3/tools/EmbyClient.SubtitleFixtures/artifacts/fixtures-EXAMPLE/manifest.json
./tools/EmbyClient.SubtitleFixtures/Bind-OfficialServer.ps1 -ManifestPath ./tools/EmbyClient.SubtitleFixtures/artifacts/fixtures-EXAMPLE/manifest.json
```

Staging verifies the manifest and actual media hashes, copies only the two generated media files to a new task runtime directory, checks their copied hashes, and refuses an existing destination. Binding uses the existing local administrator credentials only to add the independent movie library with online providers disabled; it then logs out and uses the existing playback-user credentials for item/source/track readback. It creates no playback session. Existing libraries are checked by ID/name, and the generated manifest is preserved. `bound-manifest.json`, `staging-receipt.json`, and `binding-receipt.json` contain no tokens or passwords. This helper deliberately targets the known resumed server ID; a fresh server requires a separately reviewed identity and setup instead of silently reusing old credentials.

Generation leaves `ServerId`, `ServerVersion`, `ItemId`, `MediaSourceId`, and `SubtitleStreamIndex` null. `RequiredServerVersion` records the intended official test version. `ObservedFileSubtitleStreamIndex` is FFprobe evidence only. The native complex-subtitle mode intentionally rejects this unbound manifest.

1. Start the existing isolated official test server using its established ownership/isolation procedure, and add only this run's `media` directory as a separate local movie library with network metadata disabled. Preserve all existing fixture entries.
2. Wait for the scan. Read public server identity and the new items' actual media sources. Match both unique item names and the intended media paths; do not assume item IDs or turn FFprobe indices into API IDs.
3. Create a separate bound manifest beside these reference PNGs. Populate actual `ServerId`, `ServerVersion`, `ItemId`, `MediaSourceId`, `SubtitleStreamIndex`, and `RunTimeTicks` from the API. Keep the generated media/reference/font hashes unchanged and set `BindingStatus` to describe the completed binding. Record the observed source and subtitle codec, `IsExternal`, and `IsTextSubtitleStream` in a separate safe binding receipt. Expected embedded metadata is ASS `ass`/false/true and PGS `pgssub`/false/false, but the receipt must contain observations, not these expectations.
4. Run each case through the independent default-profile [native complex-subtitle mode](../EmbyClient.NativeProbe/COMPLEX-SUBTITLES.md). Require the selected subtitle's server `Encode` delivery, HLS transcode, no native external TimedTextSource, the actual paused frame at 41 seconds, and a root-agent screenshot comparison with the matching reference. Then disable subtitles, verify absence at the preserved scene/time, and complete the case's own Stop/StopEncoding/logout cleanup.

API negotiation, an attachment hash, FFprobe, and these generated reference images are preparation evidence. They do not establish native playback pixels, server font use, or a passed ASS/PGS acceptance result. A successful later run is limited to the specific cue, style/font or bitmap, on/off transition, and cleanup it observed.

The first actual binding on official Emby 4.9.5.0 exposed the PGS codec as uppercase `PGSSUB`, with `IsExternal=false` and `IsTextSubtitleStream=false`; codec matching is case-insensitive. ASS was `ass`/false/true. This server exposed its attached TTF through `MediaStreams` with `Type=Attachment`, index 3, `Codec=ttf`, MIME `application/x-truetype-font`, size 304936, display title `BungeeShade-Regular`, and `IsExternal=false`. It did not return a separate `MediaAttachments` property. These are actual API field observations, not proof of server font rendering.

## Playlist-only diagnostic

`inspect_playlists.py` is a separately authorized HTTP diagnostic for the bound ASS item 19 and original SRT item 5 on the owned server. It uses independent device/authentication sessions, mirrors the current default profile and selection at 41 seconds with subtitle index 2, and records the reviewed profile/coordinator source hashes. Its route whitelist permits only public identity, authentication, PlaybackInfo, master/main playlists, own encoding cleanup, and logout. It refuses segment requests and playback reports, so it does not create a native playback or change a playback position through Playing reports.

Run only after other playback clients have released the coordinated server window:

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.SubtitleFixtures/inspect_playlists.py
```

The fresh ignored result directory contains a safe `playlist-evidence.json` and each case's raw master/main responses. Raw playlist files may contain credentials in their URLs; do not print, publish, or copy those files into tracked evidence. The safe report records media sequence, declared durations, terminal tags, segment numbers, selected non-secret query parameters, content hashes, and each cleanup HTTP status. These observations describe the returned playlist only; they do not prove that any listed segment can be fetched or decoded.

`replay_tail_commands.py` separately replays two fixed, recorded official FFmpeg commands: an actual successful MP4/SRT command that produced segment 20, and the ASS/MKV command from the failed server request for segment 20. It parses the recorded command into an argument array, preserves the actual inputs and encoding parameters, and replaces only the graph, segment-list, and segment-output paths with a fresh ignored directory. Each command runs in another user/network namespace with a private tool home/cache/temp, and it makes no server API requests. The official library environment is set after entering `unshare`, so it cannot replace the host utility's libc.

The ignored replay output directory retains the exact argument arrays. Its safe report records source log/input/executable hashes, selected command parameters, output filenames/bytes/hashes, FFmpeg exit/progress, and FFprobe streams and packet PTS/DTS. The two historical commands are observations rather than a controlled single-variable comparison: the successful command starts at 39 seconds/segment 13 and the failed tail restart at 60 seconds/segment 20. A locally valid TS does not establish that Emby's streaming initialization accepts it, and no replay result changes the original native failure receipt or marks the subtitle case passed.

## Progressive prefix control

`inspect_progressive_control.py` is an independent HTTP-only control for the existing ASS fixture on the fixed owned Emby 4.9.5.0 server: item 19, source `mediasource_19`, embedded ASS index 2. It makes one request at zero and one at 41 seconds. Its request flags correspond to `ForceTranscoding=false`: direct play is disabled, direct stream and transcoding remain enabled, and audio/video stream copy remain allowed. It changes only the copied profile's transcoding container/protocol from `ts`/`hls` to `mp4`/`http`; the remaining baseline profile fields and subtitle `Encode` entries are preserved. This control does not change the product's default capability profile or add a fallback policy.

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.SubtitleFixtures/inspect_progressive_control.py
```

The script verifies the fixed public server identity before authentication, allocates a separate device/session for each case, restricts routes and origins, and requires actual `http`/`mp4` and ASS `Encode` negotiation. It preserves a returned `StartTimeTicks`; if absent for a nonzero start, it mirrors the existing progressive factory behavior by adding it. In the retained observation the server already returned 41 seconds, so the script added no start parameter.

Each media GET requests `Range: bytes=0-65535`, reads at most 64 KiB, and disposes the response and connection immediately. It records the actual status, selected headers, prefix hash, and bounded top-level MP4 box metadata; it does not retain the media prefix bytes or a full URL. It sends no Playing reports and cleans only its own allocated encoding/session through StopEncoding and logout. A returned `200` must not be described as range support or treated as `206`.

The retained zero/41-second responses were `200 video/mp4`, chunked, with no Content-Length, `Accept-Ranges: none`, and no Content-Range. Both provided 65536 bytes containing complete top-level `ftyp`, `moov`, and `moof` boxes before the bounded read ended. Both own StopEncoding and logout calls returned 204. This is an observed progressive prefix, not proof of completed media, native decoding, seek/position behavior, rendered subtitles, cleanup of a native graph, or a resource gate. No native window was opened by this control.

## Retained evidence and unresolved HLS failures

The [verification index](verification/README.md) preserves sanitized copies of three completed diagnostic receipts with their original receipt hashes and explicit limits:

| Diagnostic | Observed fact | Remaining boundary |
| --- | --- | --- |
| [HLS playlist comparison](verification/hls-playlist-comparison.json) | ASS/MKV and SRT/MP4 both declare 21 three-second segments, 63 seconds total, sequence zero, ENDLIST, and start 41; master frame rates are 30.083 and 30.000 | No segments fetched; equal playlists do not explain the different tail outcomes |
| [Recorded tail-command replay](verification/ffmpeg-tail-command-replay.json) | Both actual FFmpeg commands exit zero and create an FFprobe-readable final TS with audio; the failed ASS restart has one video packet and the successful MP4 tail has two | Different historical commands are not a single-variable control; local output does not prove Emby streaming initialization accepts it |
| [Progressive prefix control](verification/progressive-prefix-control.json) | The original ASS source negotiates HTTP/MP4 Encode and supplies two bounded 64 KiB prefixes with the observed box structure and completed own HTTP cleanup | No native player, complete download, range support, pixels, subtitle disabling, or default-profile repair was verified |

The original default-HLS [ASS network failure](../EmbyClient.NativeProbe/verification/complex-ass-network-failure.json) and [PGS network failure](../EmbyClient.NativeProbe/verification/complex-pgs-network-failure.json) remain failures. They used the unchanged generated sources and recorded native/coordinator `NetworkFailure` before any completed visual/disable acceptance. The earlier [ASS session-retirement observation](../EmbyClient.NativeProbe/verification/complex-ass-initial-session-retirement.json) lacked passive failure instrumentation, and the [PGS output exception](../EmbyClient.NativeProbe/verification/complex-pgs-output-failure.json) remains a separate harness failure. No diagnostic above rewrites those receipts or establishes ASS/font or PGS pixel fidelity. The exact Emby tail-initialization rejection mechanism remains unresolved.
