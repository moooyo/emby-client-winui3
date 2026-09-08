# API conventions and transport

Status: documentation research, **2026-09-09**. Protocol facts link to official sources; client policies below are implementation proposals.

## API root and URL handling

Emby's documented API root is `http[s]://hostname:port/emby/`. Paths in these documents are relative to that API root: `/Users/AuthenticateByName` means `{apiRoot}/Users/AuthenticateByName`. Use JSON for application requests and responses. Media, images, subtitles, and HLS playlists have their own response content types. [Official API introduction](https://dev.emby.media/doc/restapi/index.html)

Keep three concepts distinct:

| Value | Example | Meaning |
| --- | --- | --- |
| User-entered server address | `https://media.example.test/media` | May include a reverse-proxy prefix |
| Validated API root | `https://media.example.test/media/emby/` | Prefix used for endpoint construction; actual deployment must confirm it |
| Returned playback URL | Relative or absolute URL from `PlaybackInfo` | Resolve according to its form without corrupting parameters or prefixes |

The examples describe one proxy arrangement, not a guarantee that every installation adds `/emby`. Normalize an address once, retain any proxy prefix, and confirm a candidate with public server information. Accept a user-supplied API root without duplicating `/emby`. Do not remove a meaningful path with a leading-slash `Uri` combination. Reject embedded user credentials, unexpected URL schemes, and fragments as connection configuration; keep tokens separate.

For returned media URLs, distinguish absolute URLs, origin-relative URLs, and API-relative URLs. Preserve server-generated query parameters and existing escaping. Add authentication only according to the delivery contract and expected origin. A redirected media host must not automatically receive the Emby token; support expected CDN delivery through an explicit origin policy or a server-provided signed URL.

Use the user's configured endpoint. Never use the `servers` address embedded in the public static Swagger sample; it is an example instance, not an Emby discovery service.

## Authentication and identity

The interactive client uses user authentication and the returned `AccessToken`. Send the client identity header and `X-Emby-Token` on authenticated HTTP requests, following the [authentication reference](02-auth-and-server.md). The API-key workflow is intended for other integration scenarios. [User authentication](https://dev.emby.media/doc/restapi/User-Authentication.html), [API-key authentication](https://dev.emby.media/doc/restapi/API-Key-Authentication.html)

Treat `DeviceId` as a stable, random installation identity. Do not derive it from a MAC address or another hardware identifier. Keep `Client`, `Device`, and `Version` consistent between HTTP, playback requests, session capabilities, and WebSocket setup.

Use a context keyed by `(ServerId, UserId)`, with a separate API root and protected token. Server switching cancels outstanding work and prevents an old response from replacing the new user's UI. Do not place the active token in a process-wide mutable `HttpClient.DefaultRequestHeaders` collection shared by multiple servers.

Store credentials through a Windows credential protection service; choose the exact implementation in the [Windows plan](../architecture/windows-client-plan.md). Keep passwords only for the sign-in request. Redact `X-Emby-Token`, authorization headers, passwords, API-key query values, and signed stream URLs from diagnostics. Binary request handling requires the same protection as ordinary JSON requests.

## Wire types and DTO design

| Wire concept | C# representation and client rule |
| --- | --- |
| Server, user, item, source, stream-session identifiers | Opaque strings in the domain model. Do not assume every identifier is a numeric ID or a GUID. Isolate specific wire exceptions, such as `LiveStreamRequest.ItemId` being `int64`, in the transport adapter; see the playback reference. |
| `PositionTicks`, `RunTimeTicks`, `StartPositionTicks` | `long` or `long?`; 10,000,000 ticks per second. Preserve integer precision and use checked conversions at player boundaries. |
| Bitrates | Bits per second; explicit names such as `MaxStreamingBitrateBps` in the domain layer avoid confusing values with kbps. |
| Dates such as `PremiereDate`, `LastPlayedDate` | `DateTimeOffset?` where applicable; preserve UTC/offset information and format only in the UI. |
| Stream indices | Original `MediaStream.Index`, not the row position in an audio/subtitle menu. Index mapping belongs to the player adapter. |
| Optional fields | Nullable properties or explicit absent states. An omitted value does not prove a capability is false or an item is unavailable. |
| String enums | Preserve unknown future values. Map known values to domain enums with an `Unknown` fallback. |
| `QueryResult<T>` | `Items` plus `TotalRecordCount`; use pagination only when supported by that operation. |
| Latest items | A plain item array in the documented baseline, not automatically a `QueryResult<T>`. |
| Explicitly empty success response | Complete without requiring a JSON body; support typed content separately when a known newer contract returns it. |
| Success response with no declared schema | The body may still contain required JSON, images, media, or another format. Establish its content type and shape from the endpoint semantics and a target-server fixture before implementing the consumer. |

Emby uses PascalCase JSON property names in its published definitions. Preserve explicit wire names with `System.Text.Json` mappings. Request DTOs should contain only intended values; avoid serializing default flags that accidentally disable server behavior. Model request and response DTOs separately from view models.

Keep a deliberately small set of DTOs: `PublicSystemInfo`, `UserDto`, `AuthenticationResult`, `BaseItemDto`, `UserItemDataDto`, `QueryResult<T>`, `MediaSourceInfo`, `MediaStream`, `DeviceProfile`, playback request/report objects, and the session capability model. Expand only for implemented features. The [pinned SDK definition](https://github.com/MediaBrowser/Emby.SDK/blob/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) is a field reference, not a reason to generate every administration API into the app.

## Query construction and pagination

Use a centralized query builder with invariant number formatting, URL encoding, lowercase boolean values, and parameter-specific list serialization. Several Emby queries use comma-delimited lists; other fields use different representations. Follow the selected endpoint contract instead of globally joining every array the same way.

Start list views with a bounded page size, for example 50 items, and request only the fields and image sizes they need. This is a client default, not an Emby limit. Load rich `MediaSources`, `MediaStreams`, and chapters for details/playback rather than every poster. Cancel superseded search requests and ignore stale results.

Where `EnableTotalRecordCount` is supported and disabled, do not interpret the missing/default count as an empty library. Avoid count-free pagination until the stop condition has been implemented. If the server does not support a query option, record a targeted compatibility fallback rather than retrying indefinitely with arbitrary combinations.

## HTTP execution and error policy

Use typed services over `HttpClient` with per-request authentication, cancellation, bounded JSON timeouts, streaming responses for large binaries, and separate policies for API traffic and media transfers. Windows media frameworks may fetch their own manifests and segments; configuring the application's `HttpClient` does not configure the player's network stack.

| Condition | Proposed client behavior |
| --- | --- |
| `400` | Surface a request/compatibility failure; retain a redacted diagnostic. Do not blindly retry. |
| `401` | Inspect application error metadata first; show a parental-control restriction when indicated. For an invalid session, invalidate that context and return to sign-in. No refresh-token flow is assumed. |
| `403` | Explain that the account/server policy denies the feature; do not repeatedly prompt for the same password. |
| `404` | Distinguish missing item/image from missing optional endpoint; remove stale UI or use a recorded fallback. |
| `408`, network timeout, transient `5xx` | Bounded backoff for safe reads, respecting cancellation. |
| `429` if emitted | Honor `Retry-After` where present and reduce concurrency. Do not assume Emby always emits this status. |
| Proxy HTML or malformed JSON | Report an invalid server/proxy response without rendering returned HTML. |
| Empty body on success | Complete successfully for an operation that permits no body. |

Automatic retry is allowed only for known-safe reads by default. Authentication, stream opening, playlist additions, playback start/stop reporting, and recording creation need operation-specific handling of uncertain outcomes. Never install one blanket retry handler over every POST or DELETE. A GET that returns a media stream can allocate server resources, so HTTP method alone is insufficient for the media pipeline.

Playback progress reporting should serialize outgoing reports for an active session so older positions do not overtake a later stop. On an outage, keep a small latest-state record rather than an unbounded queue of stale reports. Reconcile before sending data after reconnect. See the [playback lifecycle](04-playback-and-sessions.md).

## Cache boundaries

Namespace library metadata and user data by server and user. An item's ID alone is not globally unique. Namespace artwork by server, user access context, image owner/type/index, image tag, and transform size; invalidate when the tag changes. Keep the cache bounded and separate from credential storage.

Favorites, watched state, and playback progress are server-owned. Update UI optimistically only if failure can be rolled back or reconciled. Refresh resume/next-up rows after playback stops. A WebSocket event is an invalidation signal, not a replacement for a complete HTTP response. Do not cache permission failures as if the item were permanently absent.

## Compatibility and evidence

The supported Emby version range is not selected yet. Record `System/Info/Public.Version` during connection and maintain fixtures for the actual supported server versions. Public Swagger, the SDK, and conceptual guides disagree in places; consult the [compatibility ledger](../research/sources-and-compatibility.md).

No network examples in this guide were executed against an Emby server. API, build, and player verification must follow the [remote-only verification plan](../research/verification-plan.md).
