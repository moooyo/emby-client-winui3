# Lumen design capability contracts

Research date: **2026-09-30**. The design handoff requests an immersive home, movie/series walls, details, search, settings, and a custom player. This page records API additions and their limits; it does not certify a server version, decoder, physical output, or release.

## Primary sources

The endpoint and DTO inventory was checked against the [official SDK definition at commit 1faf8176](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v2.json), whose `info.version` is `4.10.1.0`. The [matching OpenAPI v3 definition](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v3.json) provides a second rendering of that snapshot. Generated specifications do not establish the first release supporting an operation or the capabilities of a deployment.

All paths below are relative to the validated API root. User-scoped query parameters always use the authenticated client context, not an arbitrary caller-supplied user. Tokens remain in headers; IDs, names, genres, and search terms are URI-escaped by the existing transport.

## Existing capabilities

Home shelves use `GET /Users/{UserId}/Items/Latest`, `GET /Users/{UserId}/Items/Resume`, and `GET /Shows/NextUp`. Movie/series walls use the user item query with `IncludeItemTypes`, `SortBy`, `SortOrder`, `Genres`, `IsPlayed`, and `IsFavorite`. Seasons and episodes use the existing shows operations. Favorites, watched state, images, playback negotiation, source/track selection, session reporting, and cleanup already have typed contracts.

The UI must request only the fields it needs. A home hero's metadata or a technical quality badge is real only when supplied by the item/media streams, not because the handoff's placeholder has that badge.

An isolated official Emby `4.9.5.0` check on 2026-09-30 found a resume-query compatibility requirement: `/Users/{UserId}/Items/Resume` returned no records when `MediaTypes` was omitted, but the identical request with `MediaTypes=Video` returned the seeded, genuinely resumable items. An `IncludeItemTypes=Episode` filter alone did not provide that default. `GetResumeItemsAsync` now supplies `Video` when a caller passes a query with null/empty media types, while preserving explicit `Audio` or combined types and all other filters. This corrects series resume recommendations without altering watched state or fabricating runtime/progress.

## Discovery additions

| API method | HTTP contract | Response and use |
| --- | --- | --- |
| `GetSimilarItemsAsync` | `GET /Items/{Id}/Similar?UserId=...&Limit=...&Fields=...` | `QueryResult<BaseItemDto>`; related media on details |
| `GetLocalTrailersAsync` | `GET /Users/{UserId}/Items/{Id}/LocalTrailers` | Bare `BaseItemDto[]`; playable local trailers |
| `GetGenresAsync` | `GET /Genres?UserId=...` with item-query filters | `QueryResult<BaseItemDto>`; server-provided genre facets |
| `GetPersonsAsync` | `GET /Persons?UserId=...&SearchTerm=...` with item-query filters | `QueryResult<BaseItemDto>`; people search |

See the official [similar-items operation](https://dev.emby.media/reference/RestAPI/LibraryService/getItemsByIdSimilar.html), [local-trailers operation](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsByIdLocaltrailers.html), and [items-by-name guide](https://dev.emby.media/doc/restapi/Items-by-Name.html). New discovery page sizes are bounded to 1-500. A null limit on a by-name query becomes 500 rather than an unbounded request.

`ItemQuery.NameStartsWith`, `NameStartsWithOrGreater`, and `NameLessThan` support a server-backed alphabet filter or jump. They operate on the server's sorting rules; the client must not infer complete alphabet availability from a single loaded page. `BaseItemDto.SortName` is available for the displayed initial.

The current SDK has no `/Search/Hints` route or matched-field provenance in `BaseItemDto`. Media and collections use user item queries with `SearchTerm` and appropriate `IncludeItemTypes`; people use `Persons`. Text highlighting can inspect returned title, effective modern/legacy tag names, or overview. Do not invent a server-provided relevance score or a match source when the returned data cannot demonstrate it.

Remote trailers are item metadata: `RemoteTrailers` contains `Url`/`Name`, while `LocalTrailerCount` indicates local trailers. There is no per-item remote-trailer retrieval operation in this contract. Prefer local trailers, as described by the [official item-information guide](https://dev.emby.media/doc/restapi/Item-Information.html#localtrailercount-remotetrailers). External links must not receive the Emby token. Do not treat a provider web-page URL as a negotiated media stream.

## Explicit mutations

| API method | HTTP contract | Result |
| --- | --- | --- |
| `RemoveFromResumeAsync` | `POST /Users/{UserId}/Items/{Id}/HideFromResume?Hide=true` | `UserItemDataDto` |
| `SetHideFromResumeAsync` | Same operation with `Hide=true` or `false` | Allows explicit reversal without marking played |
| `CreateCollectionAsync` | `POST /Collections?Name=...&Ids=...&IsLocked=...`, no body | `CollectionCreationResult` with a nonempty `Id` |
| `AddToCollectionAsync` | `POST /Collections/{Id}/Items?Ids=...`, no body | Empty success |
| `RefreshItemMetadataAsync` | `POST /Items/{Id}/Refresh` with refresh query options and JSON `{}` or `ReplaceThumbnailImages` | Empty success |
| `UpdateItemMetadataAsync` | Read current user item, merge bounded edits, then `POST /Items/{ItemId}` with complete JSON | Empty success |
| `UpdateUserConfigurationAsync` | Read current user, merge configuration edits, then `POST /Users/{Id}/Configuration` | Empty success |

The [hide-from-resume operation](https://dev.emby.media/reference/RestAPI/UserLibraryService/postUsersByUseridItemsByIdHidefromresume.html) is separate from `Played`. Its response DTO need not expose the hidden flag. Refresh the resume shelf after success; do not fabricate `Played=true`, erase progress, or use watched state as an approximation.

Collections are `BoxSet` items, not favorites or transient playback queues. Creation/addition accept 1-100 input IDs, deduplicate without altering caller arrays, and reject embedded comma separators and control characters. See [create collection](https://dev.emby.media/reference/RestAPI/CollectionService/postCollections.html), [add collection items](https://dev.emby.media/reference/RestAPI/CollectionService/postCollectionsByIdItems.html), and the [official collection guide](https://emby.media/support/articles/Collections.html).

Refresh modes are `ValidationOnly`, `Default`, and `FullRefresh`. `Default` follows provider rules; `FullRefresh` runs all providers. Replacement flags apply to full refresh and may replace existing metadata or custom artwork, so both replacement defaults are false. See the [refresh operation](https://dev.emby.media/reference/RestAPI/ItemRefreshService/postItemsByIdRefresh.html) and [mode semantics](https://dev.emby.media/reference/pluginapi/MediaBrowser.Controller.Providers.MetadataRefreshMode.html).

Metadata save is [a POST of `BaseItemDto`](https://dev.emby.media/reference/RestAPI/ItemUpdateService/postItemsByItemid.html), not PATCH. A thin DTO could omit provider IDs, artwork state, or unknown nested fields. The implementation fetches complete JSON immediately before saving, verifies that its `Id` equals the requested resource, and merges only supplied fields. This preserves untouched and unknown nested values. Full read/merge/write cycles are serialized on the same immutable client, so concurrent sparse configuration or metadata changes preserve one another. It cannot prevent a separate client/server editor update racing that read/write because this endpoint has no documented conditional-write guarantee.

Editable fields are `Name`, `OriginalTitle`, `Overview`, `ProductionYear`, `OfficialRating`, `Genres`, `Tags`, `LockData`, and `LockedFields`. Null means unchanged; empty strings/arrays clear optional fields. `Name` remains nonempty. Text/list/year bounds apply before network I/O, and complete mutation JSON is limited to 4 MiB. The [metadata-manager guide](https://raw.githubusercontent.com/EmbySupport/Emby.Docs/master/Metadata-manager.md) explains that later refreshes can overwrite unlocked edits.

### Modern and legacy tags

`BaseItemDto.TagItems` carries modern `NameLongIdPair[]` values. Pair IDs are
nullable `long` values and accept JSON integers or integer strings without
rounding through a floating-point representation. The source-generated DTO keeps
legacy wire `Tags` in `LegacyTags` via `JsonPropertyName("Tags")`; its public
`Tags` projection is ignored for serialization and prefers modern names whenever
`TagItems` is non-null. An empty modern array is authoritative rather than a
reason to revive stale legacy tags. Null/absent modern data uses the original
legacy values. Null/blank modern names are omitted from the effective projection;
other names retain returned spelling/order. Presentation does not invent a tag
ID or normalize names.

`ItemMetadataUpdate.Tags = null` leaves both raw tag properties untouched,
including unknown pair members and large IDs represented as integers or strings.
When no fields change, the API returns before network I/O. Explicit tag edits
on modern or mixed responses write the names to `Tags` and rebuild `TagItems`:
an exact, case-sensitive name match reuses the complete original object, while
new names are `{ "Name": "..." }` with no fabricated ID. The server owns new
identity, deduplication, and ordering. An empty edit clears both modern/mixed
properties. A response exposing only legacy `Tags` retains a legacy-only write
contract rather than introducing `TagItems`. Malformed/conflicting modern tag
shapes are rejected before POST when the caller actually edits tags.

The native metadata editor snapshots its initial genre/tag text and only parses
changed fields. Opening and saving without editing those inputs therefore does
not split existing names containing comma/semicolon separators or rewrite tag
JSON through the display projection. Explicit edits still use the editor's
comma/semicolon/newline list syntax. Forty API contract cases cover modern-first
reads, legacy compatibility, 64-bit IDs, raw-value preservation, explicit edits
and clears, malformed responses, and no-op behavior.

A separate native AOT05 administrator name-only save on the owned official
`4.9.5.0` deployment preserved `Case`, `Director, cut`, and `Unchanged; marker`,
their IDs and complete `TagItems`, and genres; the original metadata was restored.
The safe summary is
`artifacts/lumen-official-validation/metadata-name-only-ui-save-and-restore-summary.json`.
Its strict derived-name/media-source differences remain recorded; after
classifying server name derivation and user subtitle-preference projections,
the refined 48-key comparison has no mismatch. Prepared unknown tag fields
numbered zero, so this is not a real unknown-field sample or byte-identical raw
JSON claim. This AOT05 result is not final AOT08 native proof. AOT08 subsequently
displayed the restored tag and genres in the real native editor and canceled
without Save; that prefill/cancel is not another name-only mutation test.

All these generated operations require user authentication and list 401/403. That does not grant arbitrary users mutation rights. `BaseItemDto.CanEditItems` and `UserPolicy.IsAdministrator` enable conservative UI gating, and the server's response remains authoritative. Do not claim collections or metadata routes are universally administrator-only or universally writable.

The isolated official `4.9.5.0` check found `CanEditItems` omitted for both administrator and ordinary users. The ordinary user received 403 for item metadata save, but could refresh metadata and create a collection; the administrator could save and restore full metadata. Therefore metadata editing, metadata refresh, and collection operations must not share an assumed administrator-only gate. Prefer the explicit item editing capability for edits, use administrator status only when it is absent, and handle endpoint-specific authorization. These observations establish one synthetic test deployment, not a universal permission policy.

## Settings and markers

The [user-configuration operation](https://dev.emby.media/reference/RestAPI/UserService/postUsersByIdConfiguration.html) updates the current user's full configuration. The client preserves unknown JSON fields on reads, and saves sparse changes into a fresh configuration object rather than replacing it with only the fields currently understood. A missing or non-object configuration is a protocol failure, not permission to reset it. Null configuration properties mean unchanged. Callers supply only changed properties, not an old full configuration snapshot. Extension-data keys cannot impersonate known fields, even with different casing, and undefined JSON is rejected.

Server settings include audio/subtitle language, subtitle mode (`Default`, `Always`, `OnlyForced`, `None`, `Smart`, `HearingImpaired`), next-episode autoplay, remembered track selections, resume rewind seconds, and intro skip mode (`ShowButton`, `AutoSkip`, `None`). Local theme/accent, poster size, hero rotation, subtitle styling, next-episode countdown, player behavior, and window state are client settings. Hardware decoding, HDR output, and refresh-rate changes require verified Windows/engine capabilities, not merely an Emby preference.

`ChapterInfo.MarkerType` and `ChapterIndex` are carried on both item and media-source chapters. Marker values are `Chapter`, `IntroStart`, `IntroEnd`, and `CreditsStart`. There is no `EndPositionTicks` or `CreditsEnd` in this snapshot: pair intro markers and use the source runtime only when available. Unknown marker values remain strings for forward compatibility. Missing markers mean skip controls are unavailable; do not guess from chapter names. Intro detection has server/library/Premiere requirements described by [Intro Skip](https://emby.media/support/articles/Intro-Skip.html). `/Intros` means pre-play cinema intros, not detected opening segments.

## Player boundaries

Chapter preview images can use the existing image operation with `type=Chapter`, a chapter index, and `ImageTag`; arbitrary exact-time video thumbnails are not guaranteed by the chapter contract. Episode drawers use the existing seasons/episodes APIs and real ordering.

The current SDK exposes conversion of existing server subtitles through `/Items/{Id}/{MediaSourceId}/Subtitles/{Index}/Stream.{Format}`, subtitle-provider search/download, and deletion. It does **not** expose a local-subtitle upload route. Do not borrow Jellyfin's upload API. Local subtitle loading requires genuine native-format support or a proven parser; style/delay controls cannot alter subtitles already burned into server video.

Playback speed and picture-in-picture are Windows player/window work, not Emby library API capabilities. This API expansion does not broaden the conservative device profile, imply broad ASS/PGS support, or certify multichannel output, audio passthrough, HDR, Dolby Vision, physical media keys, or refresh-rate matching. Verification evidence must name the actual server, executable, media, and output scope independently of this contract audit.

## Verification

On 2026-09-30, the explicitly authorized local Windows unified Release product
run passed **919/919** tests, zero failures/skips: API 174, AppState 99,
MediaTransport 125, Platform 326, Playback 195. Its script completed with exit 0;
the final-source receipt is `artifacts/lumen-tests-release-final-candidate.log`.
The API suite's **174/174** includes 53 existing cases, 81 earlier Lumen/resume cases, and 40
modern/legacy TagItems cases. SDK: `10.0.301`; API target: `net10.0`, x64;
reflection JSON serialization remained disabled.

```powershell
dotnet test --project tests/EmbyClient.Api.Tests/EmbyClient.Api.Tests.csproj --configuration Release --no-restore --verbosity minimal -- --timeout 2m
```

An earlier 129-case run used a test-only rebuild with `BuildProjectReferences=false` to avoid concurrent shared API output writes. The later 134-case checkpoint rebuilt the API after the resume-default fix, independently of the application's Debug/x64 output, with no compilation warnings/errors; it preceded the 40 TagItems cases. An initial runtime check exposed a .NET 10 source-generation constructor-binding limitation for init-only record extension-data properties; ordinary setters on those extension-data containers fixed it, and the suite was rerun. These unit tests establish HTTP/DTO/control contracts with a recording transport, not real-server support, Native AOT execution, UI pixels, playback output, or physical-device behavior. The 13 added AppState recommendation cases preserve nonblocking detail loading while series quick play awaits already-issued discovery; they are not additional API cases.

A separate isolated official Emby `4.9.5.0` run on `ssh test-env` passed **21/21** endpoint checks with generated media and a dedicated temporary non-administrator account. It checked discovery wrappers, seasons/episodes, favorite/played restoration, hide-from-resume without changing played state or the 35-second position, unknown configuration field preservation/restoration, full nested metadata preservation/restoration, collections creation/addition/readback, endpoint-specific permissions, and an authenticated HTTP 206 range. Temporary users and collections were confirmed removed afterward; the main UI test account was unchanged. Its local receipt is `artifacts/lumen-official-validation/new-ui-api-summary.json`. The initial 20/21 attempt remains separate: its resume seed check omitted `MediaTypes=Video`; correcting that request produced the final successful run rather than weakening the check. The receipt marks native UI and native decoder/first frame **NotRun**. Do not add these 21 endpoint checks to the 174 API or 919 product tests, or treat them as Windows rendering, codec, or broad server-version certification.

Bootstrap 42, PowerShell safety 12, and fixture HTTP 131 checks are independent
tooling boundaries, not extra product cases. The Bootstrap's new explicit
administrator mode verifies the dedicated authorized test role; it does not
grant mutation permissions in the product. Later AOT02/AOT03 native observations
are recorded in [Lumen UI](../implementation/lumen-ui.md) without rewriting the
API receipt's `NotRun` fields. AOT05's name-only save passed its scoped metadata
readback, but loaded narrow search crashed and that candidate is `NotAccepted`.
AOT06 local-Off/preview-label failures and AOT07's unaccepted intermediate
publish remain separate. AOT08 publication, 302 compared unchanged build inputs,
and scoped same-executable native fixture/official-server regressions are
complete. The official native run showed real HLS motion, English/French
converted-text cues, pause-preserving audio/subtitle selections, and stable
selected rows across 187.297 seconds; it did not independently verify physical
sound or progress-report counts. Both final native apps exited normally, and
local fixture/official-backend/tunnel cleanup completed with zero remaining
owned active resources and evidence retained. The [final report](../../artifacts/lumen-acceptance/final-report/ACCEPTANCE.md)
and [receipt](../../artifacts/lumen-acceptance/final-report/acceptance-summary.json)
record `AcceptedForScopedDesignHandoff`; the cleanup summary is
`artifacts/lumen-official-validation/cleanup-summary.json`. Existing release/device
boundaries are future certifications, not unfinished API/UI implementation work.
No commit, push, external PR/issue write, signing, or installation was performed.
