# Real external WebVTT fixture

`--subtitle-file <absolute-vtt-path>` is an optional test-data input. It is disabled by default and leaves the existing two-stream H.264/AAC source and `DefaultSubtitleStreamIndex=-1` unchanged. It does not change the application, require a picker, bypass authentication, inject a profile, embed text into the MP4, or claim subtitle burn-in/transcoding.

When explicitly enabled, the fixture reads one existing `.vtt` file into a bounded, immutable startup snapshot. The file must be at least eight bytes and no larger than 4 MiB, strict UTF-8, and contain a WebVTT header, blank separator, and timed-cue marker. These are bounded preliminary checks, not a replacement for actual Windows timed-text parsing. Language metadata comes only from explicit filename labels: `.zh.vtt` declares `zho`, `.en.vtt` declares `eng`, and other names do not advertise an inferred language.

The real external text stream has index 2, type `Subtitle`, codec `vtt`, `IsExternal=true`, `IsTextSubtitleStream=true`, `DeliveryMethod=External`, and the same-origin `DeliveryUrl` `/emby/Videos/<itemId>/<sourceId>/Subtitles/2/Stream.vtt`. It is the configured source default. Explicit client selection of `-1` still disables subtitles. Both real fixture aliases advertise the text without changing the underlying video/audio facts.

The exact GET/HEAD route remains behind the ordinary fixture token and requested-user authorization checks. It accepts only a known playable item, one of that item's real fixture source IDs, and index 2. It returns the original UTF-8 bytes with `text/vtt; charset=utf-8` and private/no-store caching. Request segments never become filesystem paths. Subtitle text, input paths, and credentials are not exposed in statistics.

For a newly owned acceptance environment, add this option to the normal fixture command:

```powershell
--subtitle-file C:\absolute\owned\media\fixture-test.zh.vtt
```

The application already resolves this normal metadata into an authenticated same-origin download, checks the WebVTT header and 4 MiB bound, attaches `TimedTextSource.CreateFromStream`, and uses the existing application-presented timed-text pipeline. Actual NativeAOT cue capture, styling, timing, and visible rendering still require real application verification. Serving bytes and API contract success do not prove those behaviors or Emby Server compatibility. Restart only the owned test fixture when switching snapshots; this tool does not mutate a running environment.

The separate test profile is:

```powershell
dotnet FixtureBoundaryChecks.dll C:\absolute\EmbyClient.FixtureServer.dll --subtitle-only
```

It checks default-off behavior, truthful external stream metadata, exact source bytes, authorization, known-item/source/index routing, startup snapshots, preserved actual video/audio metadata, and bounded invalid-input rejection. The tests use marker-bearing test MP4 bytes and do not decode video or claim native rendering.
