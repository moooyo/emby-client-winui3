# Library, Search, Images, User State, and Playlists

Research date: 2026-09-09. Status: official-source review only; all server behavior remains unverified against the intended Emby versions.

This catalog covers the browsing side of a Windows-native Emby player. `MVP` is the proposed first-release scope, `Later` is an extension, and `Legacy optional` means that an endpoint exists in an older official schema but is absent from the newer SDK snapshot. Paths are relative to the configured Emby API base, normally `/emby`. Every path placeholder is required. All library calls use the selected server's user token and client identity from [authentication and server context](02-auth-and-server.md).

The primary contract source is the official [SDK OpenAPI snapshot at commit bdd0dd7c0801f6e069dff2795d80cddae6f91791](https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json), cross-checked with [Emby endpoint reference](https://dev.emby.media/reference/RestAPI.html) and the workflow guides linked below. The [static schema](https://swagger.emby.media/openapi.json) reports version `4.1.1.0` and is only historical evidence. Absence from the newer snapshot is not proof that a route was removed from every Emby Server.

## Core Browsing Endpoints

| Priority | Method and Emby path | Required and selected parameters | Success contract | Use and official reference |
| --- | --- | --- | --- | --- |
| MVP | `GET /Users/{UserId}/Views` | `UserId`; explicitly supply `IncludeExternalContent=false` for the video-library MVP, because the schema marks it required. | `QueryResult<BaseItemDto>` | Top-level library navigation. Inspect each item's `CollectionType`. [Reference](https://dev.emby.media/reference/RestAPI/UserViewsService/getUsersByUseridViews.html) |
| MVP | `GET /Users/{UserId}/Items` | `UserId`; typically `ParentId`, `StartIndex`, `Limit`, `Recursive`, `IncludeItemTypes`, `SortBy`, `SortOrder`, `Fields`, `EnableUserData`. | `QueryResult<BaseItemDto>` | Folder content, movies, series, music, favorites, and full search. [Reference](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItems.html) |
| MVP | `GET /Users/{UserId}/Items/{Id}` | `UserId`, media or folder `Id`. | One `BaseItemDto` object | Detail page and authoritative refreshed user/item state. [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsById.html) |
| MVP | `GET /Users/{UserId}/Items/Latest` | `UserId`; selected `ParentId`, `Limit`, `IncludeItemTypes`, `IsPlayed`, `GroupItems`, `Fields`, `EnableUserData`. | `BaseItemDto[]`, a bare array | Home-page latest additions; do not deserialize as `QueryResult`. [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsLatest.html) |
| MVP | `GET /Users/{UserId}/Items/Resume` | `UserId`; selected `Limit`, `StartIndex`, `ParentId`, `MediaTypes=Video`, `Fields`, `EnableUserData=true`. | `QueryResult<BaseItemDto>` | Continue-watching row. [Reference](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItemsResume.html) |
| MVP | `GET /Shows/{Id}/Seasons` | `Id` is the series ID; always supply `UserId` even though the newer schema marks it optional; selected `Fields`, `EnableUserData`. | `QueryResult<BaseItemDto>` | Season selector. [Reference](https://dev.emby.media/reference/RestAPI/TvShowsService/getShowsByIdSeasons.html) |
| MVP | `GET /Shows/{Id}/Episodes` | `Id` is the series ID, not the season ID; client requires `UserId`; filter by `SeasonId` or `Season`; selected `StartIndex`, `Limit`, `Fields`, `EnableUserData`. | Historical schema: `QueryResult<BaseItemDto>`; newer SDK: success body unspecified. Confirm before binding a fixed response type. | Episode list. User-scoped generic item query is an available fallback design. [Reference](https://dev.emby.media/reference/RestAPI/TvShowsService/getShowsByIdEpisodes.html) |
| MVP | `GET /Shows/NextUp` | Schema-required `UserId`; selected `ParentId`, `SeriesId`, `StartIndex`, `Limit`, `Fields`, `EnableUserData`. | `QueryResult<BaseItemDto>` | Home-page next-up row and a server-provided next-episode suggestion. [Reference](https://dev.emby.media/reference/RestAPI/TvShowsService/getShowsNextup.html) |
| Later | `GET /Users/{UserId}/Items/Root` | `UserId`. | One `BaseItemDto` object | Generic root-folder navigation where needed. [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsRoot.html) |
| Later | `GET /Items/{Id}/Similar` | `Id`; client requires `UserId`; selected `Limit`, `Fields`. | `QueryResult<BaseItemDto>` | Similar titles on the details page. [SDK schema](https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) |
| Later | `GET /Users/{UserId}/Items/{Id}/SpecialFeatures` | `UserId`, `Id`. | `BaseItemDto[]` | Optional extras section when `SpecialFeatureCount` indicates content. [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsByIdSpecialfeatures.html) |
| Later | `GET /Users/{UserId}/Items/{Id}/LocalTrailers` | `UserId`, `Id`. | `BaseItemDto[]` | Local trailers when advertised by item details. [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/getUsersByUseridItemsByIdLocaltrailers.html) |

`GET /Items` also exists, with `UserId` as a query parameter. Prefer the explicitly user-scoped form for this application so that every request has an unambiguous user context. A global endpoint must never become a workaround for a user's restricted library. [Library browsing guide](https://dev.emby.media/doc/restapi/Browsing-the-Library.html)

## Query and Pagination Contract

`QueryResult<BaseItemDto>` is a JSON object containing `Items` and `TotalRecordCount` in the pinned SDK. It does not define a `StartIndex` response property; retain the requested offset locally. Use offset pagination with cancellation, deduplicate by `(ServerId, ItemId)`, and tolerate changing totals while a library changes. No API call should load an entire library merely to render the first viewport.

| Parameter | Wire value and intended use |
| --- | --- |
| `ParentId` | Limit a request to a library view, series, season, folder, or collection. Preserve the server-returned ID as an opaque string. |
| `StartIndex`, `Limit` | Integer offset and page size. A bounded client default such as 50 is a design choice, not an Emby guarantee. |
| `Recursive` | `true` for descendant queries; omitted or `false` for immediate folder children. |
| `IncludeItemTypes` | Comma-separated types, for example `Movie`, `Series`, `Episode`, `MusicAlbum`, `Audio`, `Playlist`, `BoxSet`. |
| `MediaTypes` | Comma-separated media categories such as `Video` or `Audio`; distinct from item types. |
| `SearchTerm` | Text search within the same user-scoped endpoint. URL-encode the supplied value. |
| `SortBy`, `SortOrder` | Comma-separated sort fields and corresponding directions; for example `SortBy=ProductionYear,SortName&SortOrder=Descending,Ascending`. |
| `Fields` | Comma-separated optional projection fields. Request only what the current view displays, such as `PrimaryImageAspectRatio`, `Overview`, `Genres`, or `People`. |
| `EnableUserData` | Request user state for progress/favorite badges. |
| `EnableImages`, `EnableImageTypes`, `ImageTypeLimit` | Limit image metadata included in list responses; these do not download image bytes. |
| `IsPlayed`, `IsFavorite` | Boolean filters for watched/favorite screens. |
| `Filters` | Comma-separated server-defined filters such as `IsResumable`, `IsUnplayed`, `IsFavorite`. |
| `Ids`, `Years` | Comma-separated IDs or production years. |
| `Genres`, `Tags`, `OfficialRatings` | Pipe-separated values in the documented item-query contract. Do not use one global comma-join rule for all filters. |
| `PersonIds`, `StudioIds`, `ArtistIds` | Use endpoint-specific delimiter definitions from the selected schema; the published definitions are not uniform. |

Sources: [item query reference](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItems.html) and [browsing workflow](https://dev.emby.media/doc/restapi/Browsing-the-Library.html).

Example request shapes, with placeholders rather than executable server credentials:

```http
GET /emby/Users/{UserId}/Views?IncludeExternalContent=false

GET /emby/Users/{UserId}/Items?ParentId={ViewId}&Recursive=true&IncludeItemTypes=Movie&StartIndex=0&Limit=50&SortBy=SortName&SortOrder=Ascending&Fields=PrimaryImageAspectRatio&EnableUserData=true

GET /emby/Users/{UserId}/Items/Latest?ParentId={ViewId}&IncludeItemTypes=Episode&Limit=20&IsPlayed=false&GroupItems=true

GET /emby/Users/{UserId}/Items/Resume?MediaTypes=Video&Limit=20&EnableUserData=true

GET /emby/Shows/NextUp?UserId={UserId}&ParentId={ViewId}&Limit=20&EnableUserData=true

GET /emby/Users/{UserId}/Items?Recursive=true&SearchTerm={EncodedSearchTerm}&IncludeItemTypes=Movie,Series,Episode&StartIndex=0&Limit=50
```

The latest-items guide describes `GroupItems=true` as the default: grouped episode results are series containers, with `ChildCount` carrying the grouping count. Expand a group using `ParentId={SeriesId}&GroupItems=false`. The workflow guide mentions `StartIndex` for latest items, but both inspected schemas omit it from that endpoint; do not promise pageable latest results without a server check. Use the generic query endpoint for a pageable complete listing. [Latest items guide](https://dev.emby.media/doc/restapi/Latest-Items.html)

## Item Model Needed by the UI

| Fields | Purpose and handling |
| --- | --- |
| `Id`, `Name`, `Type`, `IsFolder`, `MediaType`, `CollectionType` | Identity and page selection. Preserve unknown types and offer generic folder navigation where possible. A null collection type can indicate a mixed library. |
| `Overview`, `ProductionYear`, `PremiereDate`, `OfficialRating`, `CommunityRating`, `Genres`/`GenreItems`, `People`, `Studios` | Detail metadata; fields may be omitted or null. Request `Genres` to obtain `GenreItems` according to the guide. Render plain text by default. |
| `RunTimeTicks`, `Chapters` | Duration and chapter display. Keep ticks as 64-bit values; playback code must use the common time conversion rules. |
| `IndexNumber`, `IndexNumberEnd`, `ParentIndexNumber`, `SeriesId`, `SeriesName`, `SeasonId` | Episode/season ordering and labels; multi-episode files and specials must remain representable. |
| `ImageTags`, `BackdropImageTags`, `PrimaryImageAspectRatio`, parent-image IDs/tags | Artwork availability, sizing, and inheritance. |
| `UserData` | Favorite, played, and resume indicators for the active user. |
| `MediaSources`, `MediaStreams` | Technical detail and entry into playback preparation. `MediaStreams` on the item is display information; use negotiated playback information and its media sources for playback decisions. |
| `LocationType` and available media sources | Detect non-playable/missing entries. Do not assume every returned episode has playable media. |

The [item information guide](https://dev.emby.media/doc/restapi/Item-Information.html) says list responses are reduced projections while single-item responses include the full item representation. This does not guarantee every optional property exists. Separate server DTOs from view models, permit nullable optional fields, ignore unknown fields, and do not serialize unknown enum values as failures. The same guide explicitly says top-level `MediaStreams` has been superseded by `MediaSources` for playback use.

## Search and Filter Discovery

MVP search uses `GET /Users/{UserId}/Items?SearchTerm=...` with pagination, cancellation, and item-type filters. Debouncing the search box and discarding stale results are client implementation choices. This approach is present in both old and newer official contracts.

| Priority | Method and path | Inputs | Response and evidence |
| --- | --- | --- | --- |
| Legacy optional | `GET /Search/Hints` | Required `SearchTerm`; client must supply `UserId`; selected `Limit`, `StartIndex`, `IncludeItemTypes`, `MediaTypes`, `IncludePeople`, `IncludeMedia`, `IncludeGenres`, `IncludeStudios`, `IncludeArtists`. | Historical `SearchHintResult`: `SearchHints`, `TotalRecordCount`. Absent from the pinned newer SDK. [Historical official schema](https://swagger.emby.media/openapi.json) |
| Legacy optional | `GET /Items/Filters` | Client requires `UserId`; selected `ParentId`, `IncludeItemTypes`, `MediaTypes`. | `QueryFiltersLegacy`: `Genres`, `Tags`, `OfficialRatings`, `Years`. Documented in the older workflow but absent from the pinned newer SDK. [Filtering guide](https://dev.emby.media/doc/restapi/Filtering.html) |
| Legacy optional | `GET /Items/Filters2` | Same selected query scope as `/Items/Filters`. | Historical `QueryFilters`; genre entries have IDs and names. Absent from the pinned newer SDK. [Historical official schema](https://swagger.emby.media/openapi.json) |
| Later | `GET /Genres`, `GET /MusicGenres` | Client requires `UserId`; selected `ParentId`, `IncludeItemTypes`, `StartIndex`, `Limit`, `SearchTerm`. | `QueryResult<BaseItemDto>` in the pinned SDK; genre browsing and filter candidates. [SDK schema](https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) |
| Later | `GET /Persons`, `GET /Studios`, `GET /Artists`, `GET /Artists/AlbumArtists` | Client requires `UserId`; selected scope and pagination parameters per endpoint. | `QueryResult<BaseItemDto>` in the pinned SDK; actor/studio/artist exploration. [SDK schema](https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) |

Do not make `/Search/Hints` or `/Items/Filters2` a startup dependency. Resolve support in the target-version compatibility exercise; retain the generic search flow regardless. Do not present all server-wide genres as if they necessarily belong to the selected user's library.

## Images

| Priority | Method and path | Required parameters | Selected optional parameters | Response |
| --- | --- | --- | --- | --- |
| MVP | `GET /Items/{Id}/Images/{Type}` | `Id`, `Type`, commonly `Primary`, `Logo`, `Thumb`, or `Art`. | `Tag`, `MaxWidth`, `MaxHeight`, `Width`, `Height`, `Quality`, `Format`. | Binary image bytes; SDK success schema is unspecified. |
| MVP | `GET /Items/{Id}/Images/{Type}/{Index}` | `Id`, `Type`, `Index`; use an advertised index for `Backdrop` and later chapter/screenshot images. | Same image transforms and cache tag. | Binary image bytes. |
| Later | `GET /Users/{Id}/Images/{Type}` | User `Id`, typically `Type=Primary`. | Cache tag and dimensions. | Binary image bytes for optional account avatars. |

Sources: [image workflow](https://dev.emby.media/doc/restapi/Images.html), [item image reference](https://dev.emby.media/reference/RestAPI/ImageService/getItemsByIdImagesByType.html), [indexed image reference](https://dev.emby.media/reference/RestAPI/ImageService/getItemsByIdImagesByTypeByIndex.html), and [user image reference](https://dev.emby.media/reference/RestAPI/ImageService/getUsersByIdImagesByType.html).

Request images only when an item advertises an applicable tag. The guide warns that requesting a missing image returns `404`. Use `ImageTags` for single images and `BackdropImageTags[index]` for backdrops. For inherited art, use the corresponding `ParentLogoItemId`, `ParentLogoImageTag`, `ParentBackdropItemId`, `ParentBackdropImageTags`, `ParentArtItemId`, or parent-thumb values.

The `Tag` parameter is also a cache version. The guide describes strong caching headers when it is supplied. Recommended disk-cache identity is `(ServerId, authorization scope, item/user ID, image type, index, tag, dimensions, format, transforms)`. This extends the guide's host-independent caching suggestion with a server namespace and access isolation. It avoids collisions between different servers and continues to work when the same server is reached through LAN and WAN addresses.

Bound both memory and disk caches, cancel off-screen image work, and size requests for the Windows display scale. A tag change must create a different cache entry. Use the shared authenticated transport to retrieve bytes when the XAML image loader cannot apply required headers; do not put a token into every poster URL by default.

The newer SDK describes `Format` values `original,gif,jpg,png`; the prose guide contains the apparent typo `jpp`. For the MVP, retain the original format or request a documented common format. Transparent logos must retain transparency or specify an intentional `BackgroundColor`. Avoid burning dynamic watched/progress overlays into cached artwork; render badges natively from `UserData` instead. This last choice is a UI/cache recommendation, not an API constraint.

## Favorite, Played, and Resume State

| Priority | Method and Emby path | Inputs | Success contract and use | Official reference |
| --- | --- | --- | --- | --- |
| MVP | `POST /Users/{UserId}/FavoriteItems/{Id}` | User and item IDs; no JSON body specified. | `UserItemDataDto`; add favorite. | [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/postUsersByUseridFavoriteitemsById.html) |
| MVP | `DELETE /Users/{UserId}/FavoriteItems/{Id}` | User and item IDs. | `UserItemDataDto`; remove favorite. | [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/deleteUsersByUseridFavoriteitemsById.html) |
| MVP | `POST /Users/{UserId}/PlayedItems/{Id}` | User and item IDs; optional `DatePlayed` in documented format `yyyyMMddHHmmss`. Omit it unless the client is explicitly setting a date. | `UserItemDataDto`; explicit mark as played. | [Reference](https://dev.emby.media/reference/RestAPI/PlaystateService/postUsersByUseridPlayeditemsById.html) |
| MVP | `DELETE /Users/{UserId}/PlayedItems/{Id}` | User and item IDs. | `UserItemDataDto`; explicit mark as unplayed. | [Reference](https://dev.emby.media/reference/RestAPI/PlaystateService/deleteUsersByUseridPlayeditemsById.html) |
| Later | `POST /Users/{UserId}/Items/{Id}/HideFromResume` | User and item IDs; required query `Hide=true` or `Hide=false`. | `UserItemDataDto`; remove/restore an item in continue watching on supported versions. | [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/postUsersByUseridItemsByIdHidefromresume.html) |
| Later | `POST /Users/{UserId}/Items/{Id}/Rating` | User and item IDs; required `Likes=true` or `false`. | `UserItemDataDto`; like/dislike. This route is not a generic numeric-star-rating update. | [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/postUsersByUseridItemsByIdRating.html) |
| Later | `DELETE /Users/{UserId}/Items/{Id}/Rating` | User and item IDs. | `UserItemDataDto`; remove personal rating. | [Reference](https://dev.emby.media/reference/RestAPI/UserLibraryService/deleteUsersByUseridItemsByIdRating.html) |

Relevant `UserItemDataDto` fields are `IsFavorite`, `Played`, `PlaybackPositionTicks`, `PlayCount`, `LastPlayedDate`, `PlayedPercentage`, `UnplayedItemCount`, `Key`, and `ItemId`. Some fields are meaningful only for folders, may be absent, or may vary by version. `ServerId` in the newer model is explicitly a client-side field not used by Emby Server; do not rely on it being present in a response. [User-data definition](https://dev.emby.media/reference/RestAPI/UserLibraryService/postUsersByUseridFavoriteitemsById.html)

Apply the returned user-data object to all visible representations of that item, then invalidate affected resume/latest/next-up queries. An optimistic favorite toggle should roll back on failure. Avoid automatic retries of state mutations unless their behavior and duplicate effects have been confirmed. Store user state under `(ServerId, UserId, ItemId)`; it must not leak into another account's view.

These endpoints represent explicit user actions. Playback progress and normal completion use the playback session reporting lifecycle; they must not be simulated by repeatedly marking an item played or unplayed. Next-episode autoplay should follow the active episode queue and refreshed server state, handle specials/multi-episode files, and obtain new playback information for each selected episode. `/Shows/NextUp` is a suggestion query, not a media stream URL.

## Playlists and Queue Boundaries

Server playlists are a later feature. The MVP can hold a transient playback queue locally while using the same playback session API for each queue item. A local queue and a persisted server playlist have different identities and mutation behavior.

| Priority | Method and Emby path | Inputs | Success contract and purpose | Official reference |
| --- | --- | --- | --- | --- |
| Later | `GET /Users/{UserId}/Items?Recursive=true&IncludeItemTypes=Playlist` | User ID, pagination, optionally media type. | `QueryResult<BaseItemDto>`; available playlists. | [Playlist workflow](https://dev.emby.media/doc/restapi/Playlists.html) |
| Later | `POST /Playlists` | Query `Name` and either `Ids` (comma-separated media IDs) or `MediaType=Audio/Video`; the workflow also specifies `UserId`, although the pinned endpoint schema omits it. Confirm ownership semantics on the target version. | `PlaylistCreationResult`: `Id`, `Name`, `ItemAddedCount` in the newer SDK. | [Reference](https://dev.emby.media/reference/RestAPI/PlaylistService/postPlaylists.html) |
| Later | `GET /Playlists/{Id}/Items` | Playlist ID; client requires `UserId`; selected `StartIndex`, `Limit`, `Fields`, `EnableUserData`. | `QueryResult<BaseItemDto>`; ordered entries with `PlaylistItemId`. | [Reference](https://dev.emby.media/reference/RestAPI/PlaylistService/getPlaylistsByIdItems.html) |
| Later | `POST /Playlists/{Id}/Items` | Playlist ID; required `Ids` as comma-separated media IDs; client supplies `UserId`. | Newer SDK: `AddToPlaylistResult` with `Id`, `ItemAddedCount`; historical schema: empty success. | [Reference](https://dev.emby.media/reference/RestAPI/PlaylistService/postPlaylistsByIdItems.html) |
| Later | `DELETE /Playlists/{Id}/Items` | Playlist ID; required `EntryIds` as comma-separated playlist-entry IDs. | Empty success; remove selected occurrences. | [Reference](https://dev.emby.media/reference/RestAPI/PlaylistService/deletePlaylistsByIdItems.html) |
| Later | `POST /Playlists/{Id}/Items/{ItemId}/Move/{NewIndex}` | Playlist ID, `ItemId` route parameter, and integer `NewIndex`; confirm whether `ItemId` means a playlist-entry ID and confirm index origin on the target server before enabling drag/drop. | Empty success; reorder an occurrence. | [Reference](https://dev.emby.media/reference/RestAPI/PlaylistService/postPlaylistsByIdItemsByItemidMoveByNewindex.html) |

The official [playlist guide](https://dev.emby.media/doc/restapi/Playlists.html) requires `PlaylistItemId` for deletion. A repeated media item can have multiple playlist entries, so media `Id` and `PlaylistItemId` must remain distinct in the client model. The move reference calls its parameter `ItemId` without explaining the identity/index contract; this document marks that operation for targeted verification instead of asserting undocumented behavior.

Do not blindly retry playlist creation or add operations after a lost response: duplicates may be created. Re-read the playlist to reconcile state and let the user retry an ambiguous mutation. Media-library deletion routes are outside this player feature and must not be substituted for removing a playlist entry.

## Remote Compatibility Work Still Required

The following checks must run on authorized `test-env` infrastructure before declaring support. No local or remote API tests were run while preparing this document.

| Area | Required evidence |
| --- | --- |
| Library scope | Ordinary/restricted users; mixed libraries; no external content; empty views; unknown item types; pagination with changing totals. |
| Projection | Minimal list fields versus detail response; missing/null fields; images disabled; user data enabled; correct bare-array handling for latest items. |
| Search | `SearchTerm` query results, Unicode and encoded punctuation, cancellation; legacy hints/filter routes only if a supported version needs them. |
| Television | Episodes response shape in supported versions; season filter; specials; multi-episode files; missing items; next-up after completion. |
| Images | Missing images; inherited images; tag changes; server/user cache isolation; transparent logos; authenticated requests. |
| User state | Favorite and played/unplayed round trips; returned state propagation; two users; response-loss behavior; hide-from-resume availability. |
| Playlists | Ownership; duplicate entries; creation/add response differences; deletion by entry ID; move identity and index origin; ambiguous write recovery. |

Until these checks are complete, this is an implementation contract proposal backed by official documents, not a certified compatibility matrix or a generated client ready for release.
