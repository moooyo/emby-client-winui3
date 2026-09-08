# Emby API guide for the Windows client

Research date: **2026-09-09**. This is an original, curated client implementation reference, rather than a mirror of all Emby server administration APIs.

## Reading order

1. [Conventions and transport](01-conventions.md): API roots, identity, data types, errors, caching, and compatibility.
2. [Authentication and server connection](02-auth-and-server.md): locating a server, sign-in, token lifecycle, and user context.
3. [Library and user data](03-library-and-user-data.md): home, queries, details, images, search, episodes, favorites, watched state, and playlists.
4. [Playback and sessions](04-playback-and-sessions.md): capability negotiation, sources, streams, subtitles, reporting, and cleanup.
5. [Optional features](05-optional-features.md): music expansion, display preferences, downloads, synchronization, Live TV, and remote control.
6. [Source provenance](../research/sources-and-compatibility.md) and [remote verification](../research/verification-plan.md).

## Scope and priority

`MVP` means the first usable movie/episode client. `Conditional MVP` means required when the negotiated source needs it; it must not be omitted merely because ordinary files do not exercise it. `Later` is planned expansion. Administrative operations are outside the current product scope.

| User feature | API surface | Priority | Local reference |
| --- | --- | --- | --- |
| Enter and remember a server | `GET /System/Info/Public`; optional UDP discovery | MVP; discovery later | [Server connection](02-auth-and-server.md) |
| Sign in and restore a session | `POST /Users/AuthenticateByName`, `GET /Users/{Id}` | MVP | [Authentication](02-auth-and-server.md) |
| Optional public account picker | `GET /Users/Public` | Later | [Authentication](02-auth-and-server.md) |
| Explicit sign-out | `POST /Sessions/Logout` | MVP | [Authentication](02-auth-and-server.md) |
| Browse user libraries | `GET /Users/{UserId}/Views`, `GET /Users/{UserId}/Items` | MVP | [Library](03-library-and-user-data.md) |
| Latest and continue watching | `GET /Users/{UserId}/Items/Latest`, `GET /Users/{UserId}/Items/Resume` | MVP | [Library](03-library-and-user-data.md) |
| Search | `GET /Users/{UserId}/Items` with `SearchTerm` | MVP | [Library](03-library-and-user-data.md) |
| Details and artwork | `GET /Users/{UserId}/Items/{Id}`, `GET /Items/{Id}/Images/{Type}` and indexed image variant | MVP | [Library](03-library-and-user-data.md) |
| Seasons, episodes, next up | `GET /Shows/{Id}/Seasons`, `GET /Shows/{Id}/Episodes`, `GET /Shows/NextUp` | MVP | [Library](03-library-and-user-data.md) |
| Favorites and watched state | `POST` / `DELETE /Users/{UserId}/FavoriteItems/{Id}` and `/PlayedItems/{Id}` | MVP | [User data](03-library-and-user-data.md) |
| Choose a playable source | `POST /Items/{Id}/PlaybackInfo` | MVP | [Playback](04-playback-and-sessions.md) |
| Fetch playable media | Negotiated URL; video stream / HLS routes | MVP | [Playback](04-playback-and-sessions.md) |
| Choose audio/subtitle tracks | Playback negotiation, source stream indices, subtitle delivery routes | MVP | [Playback](04-playback-and-sessions.md) |
| Open/close managed sources | `POST /LiveStreams/Open`, `POST /LiveStreams/Close` | Conditional MVP | [Playback](04-playback-and-sessions.md) |
| Report start, progress, stop | `POST /Sessions/Playing`, `/Sessions/Playing/Progress`, `/Sessions/Playing/Stopped` | MVP | [Playback](04-playback-and-sessions.md) |
| Release server encoding | `DELETE /Videos/ActiveEncodings` | Conditional MVP | [Playback](04-playback-and-sessions.md) |
| Register device capabilities | `POST /Sessions/Capabilities/Full` | MVP integration; advertise only implemented capabilities | [Sessions](04-playback-and-sessions.md) |
| Build a temporary play queue | Local application state; ordinary playback/reporting per item | MVP | [Implementation plan](../architecture/windows-client-plan.md) |
| Manage persistent playlists | `/Playlists` and playlist-item operations | Later | [Playlists](03-library-and-user-data.md) |
| React to server changes | Emby WebSocket, reconnect, selective cache refresh | Later | [Sessions](04-playback-and-sessions.md) |
| Music-focused navigation | Artists, album artists, music genres, item queries, audio delivery | Later | [Optional features](05-optional-features.md) |
| Save server display preferences | `/DisplayPreferences/{Id}` | Later | [Optional features](05-optional-features.md) |
| Emby Connect | Connect service authentication/server list, then local token exchange | Later | [Authentication](02-auth-and-server.md) |
| Download and offline playback | Item download; optional `/Sync/*` workflow | Later | [Optional features](05-optional-features.md) |
| Live channels, guide, recordings | `/LiveTv/*` plus the shared playback lifecycle | Later | [Optional features](05-optional-features.md) |
| Control another client | `/Sessions` and session command routes | Later | [Optional features](05-optional-features.md) |

Chapter navigation normally uses `ChapterInfo` metadata returned with an item/source and local seek. It does not require a dedicated server seek endpoint. Fullscreen, volume, mute, local queue order, keyboard shortcuts, and window state are Windows/client responsibilities.

## Deliberate exclusions

The client does not need user administration, library creation, metadata editing, media deletion, plugin management, scheduled tasks, server restart, tuner configuration, or API-key administration. A playback client should not require an administrator account or a shared administrator API key.

Game, book, and photo-library specialization; DLNA server behavior; watch parties; camera upload; and third-party plugin APIs are not part of this proposal. Skip-intro markers, trickplay thumbnails, and similar enhanced navigation should be separate compatibility research before being promised.

## Contract status

Methods and paths are documented from official Emby sources. Required parameters, response shapes, authorization, and available fields must still be checked against the target server. Some generated references omit schemas or disagree with the conceptual guides. These cases are called out in the detailed pages and the [compatibility ledger](../research/sources-and-compatibility.md).

Examples are illustrative and contain placeholders. They were not executed. Supported Emby server versions remain **TBD** until remote verification is complete. Jellyfin compatibility is not implied.
