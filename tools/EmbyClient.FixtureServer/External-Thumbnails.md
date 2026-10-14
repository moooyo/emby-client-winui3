# Explicit external BIF fixture

`--bif-file <absolute-bif-path>` is an optional test-data input, disabled by default. Without it, both `Items/<itemId>/ThumbnailSet` and `Videos/<itemId>/index.bif` retain their existing unavailable behavior. It does not change the production application or extract images from a video at runtime.

The fixture takes one immutable startup snapshot of an existing `.bif` file, bounded by the production `BifThumbnailReader.MaximumPackageBytes` limit of 32 MiB. The same reader validates the entire version-0 archive, every indexed JPEG, timestamps, offsets, sentinel, dimensions, and limits. At least one actual sample must fall within the measured synthetic MP4 duration. JPEG structure validation does not establish native entropy decoding or visible rendering; those require separate application acceptance.

Both routes remain behind the existing fixture token and requested-user checks. Only known playable synthetic video items with a positive duration can use them. Request segments never become filesystem paths. `GET Items/<itemId>/ThumbnailSet?Width=<positive-int>` returns the exact `ThumbnailSetInfo` model: selected original `PositionTicks`, source JPEG SHA-256 `ImageTag` values, and a common `AspectRatio` derived from parsed JPEG dimensions when all selected images agree. It advertises at most twelve selected images, without guessed URLs or image indexes.

`GET/HEAD Videos/<itemId>/index.bif?Width=<1..4096>` returns the original complete snapshot with `application/octet-stream`, its real byte length, and private/no-store caching. The explicit QA package is served unchanged for accepted widths; the fixture does not claim dynamic JPEG resizing. HEAD has no response body. Range requests receive the full archive. Thumbnail metadata selection may filter positions beyond the synthetic duration, but the delivered BIF bytes remain unchanged.

Credential-free statistics expose the input SHA-256 and byte count, the full indexed image count, the duration-available count, and the selected count separately. `BifArchivesServed` and `BifBytesServed` count completed full-archive GETs; HEAD and invalid requests do not advance them. `ThumbnailSetsServed` counts completed metadata responses. Input paths, source image bytes, and authentication values are not included in these observations.

For a fresh owned native acceptance environment, pass `-BifFile C:\absolute\owned\synthetic-video.bif` to `tools/LumenAcceptance/Start-Environment.ps1`. The helper records input ownership and verifies that the server's startup hash and byte length match the selected file. Its readiness check also requires a positive parsed sample count. Use a BIF made from real JPEG frames of the measured synthetic video and retain the generation receipt. Serving the package is a test fixture behavior and does not certify Emby Server compatibility.

The independent HTTP boundary profile requires that same separately prepared real BIF input:

```powershell
dotnet FixtureBoundaryChecks.dll C:\absolute\EmbyClient.FixtureServer.dll --bif-only C:\absolute\owned\synthetic-video.bif
```

The profile copies the supplied archive into its owned temporary directory and checks original bytes, actual metadata, default-off behavior, authorization, item and Width routing, immutable startup snapshots, distinct counts, duration filtering, and bounded rejection before binding. It never fabricates positive JPEG data. Its marker-bearing synthetic MP4 is only an HTTP routing stub and is not decoded as media.
