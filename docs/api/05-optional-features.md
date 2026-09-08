# Optional client API extensions

These features follow the movie/episode MVP. All paths are relative to the validated Emby API root unless an external host is stated. Authentication uses the current user's token; server policy remains authoritative.

The endpoint inventory below was read from the [official SDK definition pinned to commit bdd0dd7](https://github.com/MediaBrowser/Emby.SDK/blob/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) on **2026-09-09**. It is a research baseline, not a verified feature contract. Parameters listed as client inputs include values the application should send even where the schema does not mark them required.

## Music library expansion

| Method and path | Client inputs | Response/use |
| --- | --- | --- |
| `GET /Artists` | `UserId`, optional `ParentId`, `StartIndex`, `Limit`, `SearchTerm` | `QueryResult<BaseItemDto>`; artist browsing |
| `GET /Artists/AlbumArtists` | `UserId`, library/paging filters | `QueryResult<BaseItemDto>`; album-artist navigation |
| `GET /MusicGenres` | `UserId`, optional library/paging filters | Music-genre facet query; inspect target response before enabling |
| `GET /Users/{UserId}/Items` | `IncludeItemTypes=MusicAlbum` or `Audio`, `ParentId`/artist filters, sorting | Shared item query for albums and tracks |
| `GET /Albums/{Id}/InstantMix` | Album `Id`, `UserId`, optional `Limit` | `QueryResult<BaseItemDto>`; server-selected play queue |
| `GET /Items/{Id}/InstantMix` | Item `Id`, user context | Shared instant-mix variant, subject to item type |
| `GET /Audio/{Id}/universal` | Item `Id`, optional `DeviceId`, `StartTimeTicks` in the pinned definition | Binary/streaming response; content schema is unspecified |

Use the same source negotiation, authenticated delivery, start/progress/stop reporting, and cleanup as video where applicable. Check a negotiated URL before selecting an audio-specific route. Do not infer every audio query option from an older universal-audio implementation. See [Audio streaming](https://dev.emby.media/doc/restapi/Audio-Streaming.html), [ArtistsService](https://dev.emby.media/reference/RestAPI/ArtistsService.html), and [UniversalAudioService](https://dev.emby.media/reference/RestAPI/UniversalAudioService.html).

Gapless playback, replay gain, output-device selection, exclusive audio, and passthrough require player-engine work in addition to API integration. They are not promised by this endpoint list. Persistent playlists are documented in [library and user data](03-library-and-user-data.md).

## Display preferences

| Method and path | Required/input values | Response |
| --- | --- | --- |
| `GET /DisplayPreferences/{Id}` | Path preference `Id`; query `UserId` and `Client` are required in the pinned definition | `DisplayPreferences` |
| `POST /DisplayPreferences/{DisplayPreferencesId}` | Path preference ID, query `UserId`, JSON `DisplayPreferences` body | Empty success in the pinned definition |

The pinned DTO contains `Id`, `SortBy`, `SortOrder`, `Client`, and `CustomPrefs`. Use the item's display-preference identity where supplied, and a stable client name. Preserve unrelated custom preferences when updating. These are server display preferences; window dimensions, selected theme, cache budgets, and playback-engine choice belong in Windows application settings. [DisplayPreferencesService](https://dev.emby.media/reference/RestAPI/DisplayPreferencesService.html)

## Direct download and managed synchronization

| Method and path | Inputs | Response/use |
| --- | --- | --- |
| `GET /Items/{Id}/Download` | Path item `Id`; current user credentials | Download response; the pinned schema leaves content unspecified |
| `POST /Sync/Jobs` | JSON `SyncJobRequest`; app supplies user, selected items/category, and `TargetId` | `SyncJobCreationResult` |
| `GET /Sync/Jobs` | Filters supported by the actual server | `QueryResult<SyncJob>`; show job state |
| `GET /Sync/JobItems` | Query `TargetId` required | `QueryResult<SyncJobItem>`; device's work |
| `GET /Sync/JobItems/{Id}/File` | Sync job-item `Id` | Transfer prepared media |
| `POST /Sync/JobItems/{Id}/Transferred` | Sync job-item `Id` | Report completed transfer after the file is durable |

An original-file download and an Emby synchronization job are different workflows. Both the old public `4.1.1.0` schema and the pinned SDK contain `/Items/{Id}/Download`. The old schema additionally contains `/Items/File`, which is absent from the pinned SDK. This does not establish a migration or universal removal. Use the item download route only after verifying its actual authorization and delivery contract; do not silently substitute undocumented legacy routes.

Emby's sync guide requires advertising `SupportsSync` and a `DeviceProfile`, checking the user's `Policy.EnableSync`, and checking item `SupportsSync` when requesting `SyncInfo`. It identifies `TargetId` with the client's `DeviceId`. Those fields and their current permission/feature behavior must be verified on the target server. [Official Sync guide](https://dev.emby.media/doc/restapi/Sync.html), [SyncService](https://dev.emby.media/reference/RestAPI/SyncService.html)

The guide leaves several offline-management sections unfinished. The listed routes are an inventory for a later design, not a complete synchronization protocol. Before implementation, specify additional-file/subtitle transfer, cancellation, removal acknowledgements, retention, disk quota, interrupted transfers, user switching, permissions, offline metadata, and conflict resolution for watched state. Do not advertise `SupportsSync=true` until that lifecycle works.

For a basic download feature, use a user-selected destination and a temporary file, inspect response disposition/content, and implement cancellation and atomic completion. HTTP range resume must be verified; it is not guaranteed by an unspecified binary response schema. Respect download permission and server feature availability independently of ordinary playback permission.

## Live TV and recording

Start with `GET /LiveTv/Info`. The documented `LiveTvInfo` has `IsEnabled` and `EnabledUsers`; hide or disable unavailable features and still handle authorization failures from individual operations. Channel images use normal item image routes. [Official Live TV guide](https://dev.emby.media/doc/restapi/Live-TV.html)

| Method and path | Inputs that matter | Response/use |
| --- | --- | --- |
| `GET /LiveTv/Info` | User authentication | `LiveTvInfo`; availability |
| `GET /LiveTv/Channels` | `UserId`, paging, optional `AddCurrentProgram` | `QueryResult<BaseItemDto>` |
| `GET /LiveTv/Channels/{Id}` | Channel `Id`, user context | `BaseItemDto` |
| `GET /LiveTv/Programs` | `UserId`, `ChannelIds`, bounded start/end dates and paging | EPG query; pinned schema does not declare the response shape |
| `POST /LiveTv/Programs` | Required JSON `Api.BaseItemsRequest`; endpoint-specific query options | Alternative EPG query; response shape also unspecified |
| `GET /LiveTv/Programs/{Id}` | Program `Id`, user context | `BaseItemDto` |
| `GET /LiveTv/Recordings` | `UserId`, optional `ChannelId`, `Status`, `IsInProgress`, paging | Recording query; response schema unspecified |
| `GET /LiveTv/Timers/Defaults` | Optional `ProgramId` | `SeriesTimerInfoDto` in the pinned definition; default times/padding |
| `GET /LiveTv/Timers` | Optional channel/series-timer filters | `QueryResult<TimerInfoDto>` |
| `GET /LiveTv/Timers/{Id}` | Timer `Id` | `TimerInfoDto` |
| `POST /LiveTv/Timers` | Required JSON `TimerInfoDto`, based on server defaults | Schedule one recording |
| `POST /LiveTv/Timers/{Id}` | Path `Id` and required `TimerInfoDto` body | Update one recording |
| `DELETE /LiveTv/Timers/{Id}` | Timer `Id` | Cancel a scheduled recording |
| `GET /LiveTv/SeriesTimers` | Optional sort/filter inputs | `QueryResult<SeriesTimerInfoDto>` |
| `GET /LiveTv/SeriesTimers/{Id}` | Series-timer `Id` | Pinned schema says `TimerInfoDto`; confirm the actual series response |
| `POST /LiveTv/SeriesTimers` | Required `LiveTv.SeriesTimerInfo` body | `SeriesTimerInfoDto` |
| `POST /LiveTv/SeriesTimers/{Id}` | Path `Id` and required `LiveTv.SeriesTimerInfo` body | Update series rule |
| `DELETE /LiveTv/SeriesTimers/{Id}` | Series-timer `Id` | Cancel series rule |

The pinned DTO types for series rules differ between create/update, defaults, and individual retrieval. Capture real fixtures instead of assuming one generated class represents every response. Use UTC/offset-aware dates and server defaults for padding. Missing `ProgramInfo` is valid for a manual timer or unavailable EPG metadata. [LiveTvService reference](https://dev.emby.media/reference/RestAPI/LiveTvService.html)

Play a channel or recording through `PlaybackInfo` and the shared player coordinator. A source that requires opening must be closed even if playback fails. Live content may have unknown duration or a limited seek window; do not expose arbitrary VOD seeking. Schedule/cancel operations are user actions with server-side effects, so they need explicit UI intent and operation-specific retry handling. Tuner hosts, providers, channel mappings, and other server administration remain outside scope.

## Remote control

| Method and path | Inputs/use |
| --- | --- |
| `GET /Sessions` | `ControllableByUserId` limits the list to sessions controllable by the current user |
| `POST /Sessions/{Id}/Playing` | Target session `Id`, item IDs, `PlayCommand`, and optional starting position/source/track choices |
| `POST /Sessions/{Id}/Playing/{Command}` | Target session and supported play-state command; seek uses `SeekPositionTicks` |
| `POST /Sessions/{Id}/Command/{Command}` | Supported general command and a JSON arguments object where needed |

`/Sessions/{Id}/Playing` sends a command to another session. `/Sessions/Playing` reports this client's playback. Keep separate service methods so they cannot be confused. Advertise and accept only implemented commands, and refresh controllability when the user or server changes. [Official Remote Control guide](https://dev.emby.media/doc/restapi/Remote-Control.html), [SessionsService](https://dev.emby.media/reference/RestAPI/SessionsService.html)

The guide describes play-command values as post data, while the pinned SDK also declares required query fields for the operation. Confirm exact placement against the target server before implementation. Receiving commands depends on verified WebSocket routing and capability registration; see [playback and sessions](04-playback-and-sessions.md).

## Emby Connect

Emby Connect uses a separate service host and a distinct credential flow. Its guide recommends implementing manual multi-server connection first. Follow the [authentication document](02-auth-and-server.md) for the researched Connect flow; do not reuse a local server token as a Connect token. [Official Emby Connect guide](https://dev.emby.media/doc/restapi/Emby-Connect.html)
