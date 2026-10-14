# Opt-in measured embedded PGS media

`--embedded-pgs` is disabled by default. It reuses `--media-dir` but selects `fixture-h264-aac-pgs.mkv`, `fixture-h264-aac.json`, and the bounded `fixture-h264-aac.ffprobe.json` in that directory. The ordinary MP4 fixture retains its original container and two embedded media streams. Embedded PGS and `--subtitle-file` are mutually exclusive.

Prepare the MKV from the project's owned, authored synthetic H.264/AAC movie and genuine SeConv Blu-ray SUP. The JSON must record the actual MKV filename, size, SHA-256, duration, dimensions, audio properties, frame-rate rational, and global video/audio/subtitle stream indices. `ProbeSha256` identifies the exact fresh FFprobe JSON. This mode accepts only a bounded 64 MiB, roughly sixty-second H.264/AAC/HDMV PGS MKV and compares its hash and every advertised stream property with that evidence before binding. Probe and metadata inputs are bounded at 64 KiB. This is explicit QA data, not media discovery or Emby compatibility certification.

The service delivers the real MKV bytes through the existing authenticated, session-owned `Videos/<itemId>/stream?Static=true` route with `video/x-matroska` and normal Range support. Source IDs remain opaque, consistent aliases. All item and PlaybackInfo sources describe the same actual MKV timeline, size, codec facts, and stream indices. No remux, transcode, external download, media-frame extraction, or replacement subtitle renderer is added.

The PGS stream reports the exact global file index, `Codec="pgssub"`, `Type="Subtitle"`, `IsExternal=false`, `IsTextSubtitleStream=false`, `DeliveryMethod="Embed"`, and no `DeliveryUrl`. Language, title, default, and forced flags come from the observed PGS stream. A non-default embedded PGS retains `DefaultSubtitleStreamIndex=-1` and must be explicitly selected in the normal application UI. The actual decoder still validates the native track; this fixture does not bypass that check.

For a new owned acceptance environment, pass `-EmbeddedPgs -MediaDirectory C:\absolute\owned\embedded-pgs` to `tools/LumenAcceptance/Start-Environment.ps1`. The helper verifies hashes, records the native subtitle index in the ownership receipt, and requires the expected opt-in state, MKV container, media/probe hashes, and index during readiness. It does not launch or drive the application.

The dedicated boundary scope uses actual separately prepared media:

```powershell
dotnet FixtureBoundaryChecks.dll C:\absolute\EmbyClient.FixtureServer.dll --embedded-pgs-only C:\absolute\owned\embedded-pgs
```

HTTP negotiation and exact Range bytes establish the fixture's delivery contract. Actual production UI selection, paused PGS pixels, seek behavior, disabling, and native cleanup require separate root-owned application acceptance.
