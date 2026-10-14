# Server-delivered BIF timeline previews

Research date: 2026-10-13. The contract and source review below is supplemented
by the [authorized Windows continuation](../implementation/lumen-windows-continuation.md).
The native app-01, app-02 and fresh publish-04 app-04 observations establish
bounded synthetic BIF image decoding and display, while target-server
availability remains server dependent. The
[app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
records all twelve actual JPEG cells at their original 0, 5, ..., 55-second
positions in the 2560 x 1392 media-information page. Cover preview remains a
separate labeled image; the flat audio lane is not measured waveform data.

The current [Emby BIF operation](https://dev.emby.media/reference/RestAPI/BifService/getVideosByIdIndexBif.html)
defines authenticated `GET /Videos/{Id}/index.bif` with required `Width`.
The pinned [4.10.1.0 SDK schema](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v2.json#L57139)
does not give its binary response structure. That omission does not establish
that a thumbnail implementation is impossible.

[Roku's official BIF specification](https://github.com/rokudev/dev-doc/blob/b287227e5b1abd8ef1fd17e72be89ce97a7fc5a6/docs/DEVELOPER/media-playback/trick-mode/bif-file-creation.md#L155)
publishes the version-0 identifier, little-endian header, timestamp multiplier,
image count, timestamp/offset index, final sentinel, and adjacent image data.
Emby's [historical official BIF service](https://github.com/MediaBrowser/roku-bif/blob/3ac428a13da366f522515e9aa6af1b19706be9f4/RokuMetadata/Api/BifService.cs#L68)
delivers `application/octet-stream`; its [writer](https://github.com/MediaBrowser/roku-bif/blob/3ac428a13da366f522515e9aa6af1b19706be9f4/RokuMetadata/Drawing/VideoProcessor.cs#L217)
uses this standard. The historical source is supporting format evidence, not
proof of every current server's behavior or its old `MediaSourceId` parameter.

`EmbyApiClient.GetBifThumbnailsAsync` consumes this formal route. The portable
reader rejects invalid identifiers, unsupported versions/extensions, excessive
counts/sizes, truncated or overlapping indices, mismatched final offsets,
non-increasing or overflowing timestamps, and unsupported or excessive JPEG
frames. It checks the complete archive before retaining at most twelve actual
samples. Native JPEG decoding remains the final image check.

Limits are 32 MiB per package, 10,000 indexed images, 2 MiB per JPEG, 4,096 pixels
per dimension and 8 Mi pixels per image. A zero timestamp multiplier uses Roku's
specified 1,000 ms default. The optional media duration filters samples without
clamping their timestamps or creating a frame at the end.

The detail timeline uses only a known primary item source and positive duration.
It keeps exact sample timestamps, cancels when leaving the media stage, and
retains only the bounded selected bitmaps in the current account/detail/source
lifetime. Unload, account/detail replacement and source changes release this
memory. No image route is guessed from `ThumbnailSet.PositionTicks` or
`ImageTag`, no alternate-version mapping is assumed, and no video is opened for
frame extraction. Missing, empty, malformed or unavailable BIF delivery retains
real chapter previews and any separately returned thumbnail metadata.

Portable tests cover route/account isolation, cancellation, timeouts, bounded
downloads and format boundaries. The original implementation was reviewed under
remote-only verification authorization. The user subsequently authorized local
Windows builds, tests, UI/UX acceptance and computer use for this continuation;
that authorization applies to the recorded run.

The controlled fixture accepts an optional `--bif-file` (`Start-Environment.ps1
-BifFile`) and serves the supplied archive through the same authenticated formal
routes. It remains disabled by default. The new BIF fixture checks passed 41 HTTP
assertions with an actual process exit code of 0.

The task-owned BIF contains twelve JPEGs from the synthetic 60-second video at
0, 5, ..., 55 seconds. Its SHA256 is
`de0613209d14d2ee3a78312678d005d40c9a5e799713aa739e7cdff999894a14` and its
size is 5310 bytes. Frame extraction was confined to QA fixture preparation;
the product consumes server BIF bytes without opening or analyzing local media.
The app-01 and app-02 media-information pages actually displayed all twelve
images and their timestamps. After app-02, cumulative server receipts recorded
two BIF responses totaling 10620 bytes and two ThumbnailSet responses. These are
combined run counts, not app-02-only totals.

See the retained [app-02 capture and observation record](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-02/native/ACCEPTANCE.md).
The later [app-04 capture](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/native/12-current-bif-timeline.jpg)
rechecks this display on the frozen publish-04 payload. Its
[normal retained-handle exit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json)
is actual code 0, and its separate
[non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app04-final-non-ui-20261014/APP04-NON-UI-AUDIT.md)
records bounded session observations and all 595 copied payload files matching.
Those run-specific results do not relabel app-01's unknown actual exit or turn
the older combined response counts into app-04-only totals. The App-only
[publish-05 change](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
affects two Copy feedback calls rather than BIF delivery. Its
[app-06 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts the targeted Copy-feedback fix and a basic PGS sample; it does not
relabel the app-04 BIF strip as a publish-05 visual rerun. The
[final readback](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
records complete app-06/publish-05 payload inventories and a fresh source/runtime
manifest matching publish-05, preserving the initial static-SVG guard refusal
and later complete readback. [Final owned cleanup](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical fixture Stop command exits 0, absent owned processes and
released ports 18985/18986. The collector's natural exit 1 remains separate
from its original launcher exit 0; no collector stop signal was sent.
This acceptance covers the primary-source detail timeline in the controlled
fixture. It does not infer alternate-source mapping, a BIF-backed player hover
preview, or thumbnail availability on a real target server.
