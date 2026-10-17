# Opt-in measured embedded PGS media

`--embedded-pgs` is disabled by default. It reuses `--media-dir` but selects `fixture-h264-aac-pgs.mkv`, `fixture-h264-aac.json`, and the bounded `fixture-h264-aac.ffprobe.json` in that directory. The ordinary MP4 fixture retains its original container and two embedded media streams. Embedded PGS and `--subtitle-file` are mutually exclusive.

Prepare the MKV from the project's owned, authored synthetic H.264/AAC movie and genuine SeConv Blu-ray SUP. The JSON must record the actual MKV filename, size, SHA-256, duration, dimensions, audio properties, frame-rate rational, and global video/audio/subtitle stream indices. `ProbeSha256` identifies the exact fresh FFprobe JSON. This mode accepts only a bounded 64 MiB, roughly sixty-second H.264/AAC/HDMV PGS MKV and compares its hash and every advertised stream property with that evidence before binding. Probe and metadata inputs are bounded at 64 KiB. This is explicit QA data, not media discovery or Emby compatibility certification.

The separate `--allow-long-embedded-pgs` flag requires `--embedded-pgs` and selects only the 99-to-101-second acceptance window, with a 1920-by-1080 dimension bound. It does not change the default 59-to-61-second window, accept an arbitrary longer movie, or bypass the existing 64 MiB media and 64 KiB evidence bounds. It retains all media/probe hashes, codec, stream-index, dimension, language, and disposition checks. Before binding, this case also reads the bounded Matroska Segment/Info duration independently from the media bytes and requires it to match the metadata and FFprobe duration. Rewriting both JSON durations and recomputing their hashes cannot disguise a shorter container as a hundred-second fixture. This structural duration check does not decode packets or certify that every encoded frame is valid.

The service delivers the real MKV bytes through the existing authenticated, session-owned `Videos/<itemId>/stream?Static=true` route with `video/x-matroska` and normal Range support. Source IDs remain opaque, consistent aliases. All item and PlaybackInfo sources describe the same actual MKV timeline, size, codec facts, and stream indices. No remux, transcode, external download, media-frame extraction, or replacement subtitle renderer is added.

The PGS stream reports the exact global file index, `Codec="pgssub"`, `Type="Subtitle"`, `IsExternal=false`, `IsTextSubtitleStream=false`, `DeliveryMethod="Embed"`, and no `DeliveryUrl`. Language, title, default, and forced flags come from the observed PGS stream. A non-default embedded PGS retains `DefaultSubtitleStreamIndex=-1` and must be explicitly selected in the normal application UI. The actual decoder still validates the native track; this fixture does not bypass that check.

For a new owned acceptance environment, pass `-EmbeddedPgs -MediaDirectory C:\absolute\owned\embedded-pgs` to `tools/LumenAcceptance/Start-Environment.ps1`. The helper verifies hashes, records the native subtitle index in the ownership receipt, and requires the expected opt-in state, MKV container, media/probe hashes, and index during readiness. It does not launch or drive the application.

For a separately measured hundred-second fixture, also pass `-AllowLongEmbeddedPgs`. The launcher records the explicit long-mode flag and measured duration, forwards both server opt-ins, and requires the live statistics to report that same duration and mode before normal protected-account bootstrap. Prepare each packet-layout case in its own directory and run directory. An existing sixty-second server must keep its original runtime and inputs.

The dedicated boundary scope uses actual separately prepared media:

```powershell
dotnet FixtureBoundaryChecks.dll C:\absolute\EmbyClient.FixtureServer.dll --embedded-pgs-only C:\absolute\owned\embedded-pgs
dotnet FixtureBoundaryChecks.dll C:\absolute\EmbyClient.FixtureServer.dll --long-embedded-pgs-only C:\absolute\owned\embedded-pgs-hundred-seconds
```

The long scope checks truthful item, catalog, PlaybackInfo, and statistics durations; original-byte delivery; default rejection; explicit bounds; rehashed false probe evidence; and the independent container-duration check. A short fixture is also rejected by the explicitly selected long window. All tests operate on fresh owned copies and leave their measured inputs unchanged.

HTTP negotiation and exact Range bytes establish the fixture's delivery contract. Actual production UI selection, paused PGS pixels, seek behavior, disabling, and native cleanup require separate root-owned application acceptance.
