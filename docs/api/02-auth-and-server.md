# Server Connection, Authentication, and User Context

Research date: 2026-09-09. Status: documentation research only; no request in this document has been exercised against a target Emby Server.

This document defines the proposed connection and authentication contract for a Windows-only, interactive Emby client. `MVP` means required for the first release; `Later` means an optional extension. Paths below are relative to the configured Emby API base, normally `https://host[:port]/emby`. Preserve any reverse-proxy path prefix supplied by the user. These paths are Emby paths, not assumed Jellyfin equivalents.

## Evidence and Version Boundaries

The primary structured source is the official [Emby SDK OpenAPI file pinned to commit bdd0dd7c0801f6e069dff2795d80cddae6f91791](https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json). Its filename explicitly omits a server version. The [official REST workflow documentation](https://dev.emby.media/doc/restapi/index.html) and [endpoint reference](https://dev.emby.media/reference/RestAPI.html) provide the surrounding workflow. The [static API Browser schema](https://swagger.emby.media/openapi.json) identifies itself as version `4.1.1.0`; use it only as historical corroboration, never as a promise of compatibility with current servers.

Two source defects affect authentication:

- The newer generated reference/schema labels `/System/Info/Public`, `/Users/Public`, and `/Users/AuthenticateByName` as requiring user authentication. The official login workflow and historical schema describe use before a token exists. The client design follows that workflow, and pre-authentication behavior must be confirmed on supported server versions.
- The rendered reference sometimes displays object references such as `AuthenticationResult.User` as `UserDto[]`. The structured SDK schema defines `User` and `SessionInfo` as objects. Do not infer JSON cardinality from those rendered table labels.

## Endpoint Inventory

All authenticated calls carry the current server's user access token and client identity headers described below. Every path placeholder is required. Parameters called out as a client requirement may be optional in the schema but are intentionally always supplied by this application.

| Priority | Method and Emby path | Authentication and inputs | Success contract | Client use and official reference |
| --- | --- | --- | --- | --- |
| MVP | `GET /System/Info/Public` | Before login; no query parameters. See the source discrepancy above. | `200 PublicSystemInfo`: `Id`, `ServerName`, `Version`, `LocalAddress`, `WanAddress`; newer definitions also expose address arrays. | Resolve server identity before selecting saved credentials. [Reference](https://dev.emby.media/reference/RestAPI/SystemService/getSystemInfoPublic.html) |
| Later | `GET /Users/Public` | Before login; client identity header. See the source discrepancy above. | `200 UserDto[]`: `Id`, `Name`, `HasPassword`, `PrimaryImageTag`. An empty array is valid. | Optional visual account picker; manual username entry is sufficient for MVP. [Reference](https://dev.emby.media/reference/RestAPI/UserService/getUsersPublic.html) |
| MVP | `POST /Users/AuthenticateByName` | Required client identity header; JSON body with `Username` and `Pw`. No previously acquired token. | `200 AuthenticationResult`: `User` object, `SessionInfo` object, `AccessToken`, `ServerId`. | Interactive login, including accounts whose `HasPassword` is false. [Reference](https://dev.emby.media/reference/RestAPI/UserService/postUsersAuthenticatebyname.html) |
| MVP | `GET /Users/{Id}` | User token; `Id` is the current authenticated user's identifier. | `200 UserDto`, including `Configuration` and `Policy`. | Restore the user context and refresh preferences/policy after reconnecting. [Reference](https://dev.emby.media/reference/RestAPI/UserService/getUsersById.html) |
| Later | `GET /System/Info` | User token; verify actual access policy before use. | `200 SystemInfo`: server identity/version plus additional server status and capabilities. | Optional extended diagnostics; public server information is sufficient for MVP identity/version. Do not make this a startup dependency or require administrative details. [Reference](https://dev.emby.media/reference/RestAPI/SystemService/getSystemInfo.html) |
| MVP | `POST /Sessions/Logout` | Current user token; no request body required by the schema. | `200`, empty response in the documented schema. | Explicit logout revokes the token. Normal application exit is not logout. [Reference](https://dev.emby.media/reference/RestAPI/SessionsService/postSessionsLogout.html) |
| Later | `GET /Users/{Id}/Images/{Type}` | `Id`; use `Type=Primary`; optionally `Tag`, `MaxWidth`, `MaxHeight`. Pre-login image authorization must be checked with public-user behavior. | Image bytes, not JSON. | Optional account avatar only when `PrimaryImageTag` is present. [Reference](https://dev.emby.media/reference/RestAPI/ImageService/getUsersByIdImagesByType.html) |
| Later | `POST /Users/{Id}/Configuration` | User token; JSON `UserConfiguration` body. | `200`, empty response in the schema. | Persist selected server-side preferences after policy checks and a supported-version read/modify/write design. [Reference](https://dev.emby.media/reference/RestAPI/UserService/postUsersByIdConfiguration.html) |
| Later | `GET /Connect/Exchange` | Required `ConnectUserId`; `X-Emby-Token` contains the Connect `AccessKey`; client identity header. | `200 ConnectAuthenticationExchangeResult`: `LocalUserId`, `AccessToken`. | Exchange Connect credentials for a token local to a selected Emby Server. [Reference](https://dev.emby.media/reference/RestAPI/ConnectService/getConnectExchange.html) |

The MVP needs no user creation/deletion, password-reset, server-management, static API-key creation, or administrative policy-update endpoints. Reading a policy does not grant permission to change it.

## Client Identity and Token Headers

The [user authentication guide](https://dev.emby.media/doc/restapi/User-Authentication.html) requires application/device identity on requests and documents `X-Emby-Token` for the acquired token. The endpoint reference accepts either `Authorization` or `X-Emby-Authorization` for identity. Use one consistent form in the client:

```http
X-Emby-Authorization: Emby Client="Windows Native Client", Device="Windows PC", DeviceId="<stable-installation-id>", Version="0.1.0"
Accept: application/json
```

After authentication:

```http
X-Emby-Authorization: Emby UserId="<user-id>", Client="Windows Native Client", Device="Windows PC", DeviceId="<stable-installation-id>", Version="0.1.0"
X-Emby-Token: <access-token>
Accept: application/json
```

Proposed login request:

```http
POST /emby/Users/AuthenticateByName
Content-Type: application/json
X-Emby-Authorization: Emby Client="Windows Native Client", Device="Windows PC", DeviceId="<stable-installation-id>", Version="0.1.0"

{
  "Username": "<username>",
  "Pw": "<password>"
}
```

`Pw` is the password string, transmitted in the JSON body. The workflow guide writes this as `pw`; the SDK model uses `Pw`. The older static schema additionally exposes `Password`, but the newer SDK model contains only `Username` and `Pw`. Do not add a legacy password hash flow without version-specific evidence. Require valid HTTPS for password/token confidentiality on untrusted networks; support explicit user-configured HTTP for local Emby installations without silently downgrading an HTTPS address. Never bypass certificate validation.

Do not implement the generated security label as a conventional `Authorization: Bearer <token>` flow. Use the documented Emby identity/token headers. Static administrator API keys are intended for service integrations and are not the normal login mechanism for this user-operated player. [Authentication choices](https://dev.emby.media/doc/restapi/index.html)

## Connection and Session Lifecycle

1. Accept a server address and normalize its base URI without discarding a reverse-proxy prefix or duplicating `/emby`.
2. Read public server information. Keep the configured connection address separate from the returned server identity and advertised LAN/WAN addresses.
3. Match saved credentials by server identity and user identity. Never send a server's token to a newly entered, unrelated host simply because its display name matches.
4. If a remembered token exists for this verified context, request the current user; otherwise display manual username/password entry. A later account picker can also display public users.
5. Authenticate even when `HasPassword=false`, then capture `AccessToken`, `ServerId`, `User.Id`, and `SessionInfo.Id` as distinct values.
6. Load policy/preferences and user-scoped library views. Start the playback/session services only after this context is established.
7. On explicit logout, call `/Sessions/Logout`, remove the locally remembered credentials, and clear user-scoped in-memory state. If the request fails because the server is offline, local removal can still complete, but server-side revocation remains unconfirmed.

This flow and the distinction between closing the app and explicit logout follow the [official authentication guide](https://dev.emby.media/doc/restapi/User-Authentication.html). The URI, local-storage, and state-isolation rules are implementation recommendations.

Recommended stored records:

| Record | Fields | Handling |
| --- | --- | --- |
| Server profile | `ServerId`, display name, configured base URI, chosen connection address, last observed server version | Keep separate from credentials; do not treat an advertised address as automatically trusted. |
| User session | `ServerId`, `UserId`, token reference, remembered-login preference | Protect the token using Windows credential protection and load it only into the matching server context. |
| Device identity | Stable random installation identifier, application version, display device name | Reuse the device identifier across launches; do not regenerate it per request or use a hardware fingerprint. |

Never log passwords, access tokens, Connect access keys, or complete token-bearing media URLs. The client transport must avoid forwarding authorization headers across origins on redirects. Image and media transports need the same credential-routing rules as JSON requests.

## Discovery

Manual address entry is the MVP connection mechanism. Local discovery can be added later using Emby's documented UDP protocol:

| Transport | Request | Response | Scope |
| --- | --- | --- | --- |
| UDP broadcast to port `7359` | Exact text `who is EmbyServer?` | JSON containing `Address`, `Id`, `Name` | Optional LAN discovery; not a REST endpoint. |

Source: [Locating the Server](https://dev.emby.media/doc/restapi/Locating-the-Server.html).

Treat discovery responses as candidate addresses and confirm them through the connection flow before attaching credentials. A missing response does not prove that no server exists: Windows/network firewall rules, VPN routing, subnet boundaries, and disabled broadcast can prevent discovery. These are implementation considerations, not extra Emby protocol fields.

## User Policy and Preferences

`UserDto.Policy` supplies capability hints relevant to the player, including `IsDisabled`, `EnableMediaPlayback`, `EnableAudioPlaybackTranscoding`, `EnableVideoPlaybackTranscoding`, `EnablePlaybackRemuxing`, `EnableContentDownloading`, `EnableLiveTvAccess`, and `RemoteClientBitrateLimit`. Use only fields present on the selected server; the server remains the authority when a request is denied. Do not copy administrative access into the client merely because the account has `IsAdministrator=true`. [User model and policy reference](https://dev.emby.media/reference/RestAPI/UserService/getUsersById.html)

`UserDto.Configuration` includes playback preferences such as audio/subtitle language, default audio selection, subtitle mode, next-episode autoplay, and remembered track selection in the newer SDK definition. Apply relevant preferences when preparing playback. Preserve unknown configuration fields if a future settings editor sends a complete configuration object, or adopt a verified partial-update contract first. [User configuration reference](https://dev.emby.media/reference/RestAPI/UserService/postUsersByIdConfiguration.html)

## Emby Connect Extension

The official guide recommends implementing manual multi-server connection before adding Connect. Connect is a separate optional hosted service; local Emby credentials and Connect credentials must remain distinct. [Emby Connect workflow](https://dev.emby.media/doc/restapi/Emby-Connect.html)

| Priority | Method and full hosted URL | Inputs | Result and next action |
| --- | --- | --- | --- |
| Later | `POST https://connect.emby.media/service/user/authenticate` | `Content-Type: application/json`; `X-Application: <AppName>/<AppVersion>`; body `{ "nameOrEmail": "<username>", "rawpw": "<password>" }`. | `ConnectAccessToken`, `ConnectUserId` according to the guide. |
| Later | `GET https://connect.emby.media/service/servers?userId={ConnectUserId}` | `X-Application`; `X-Connect-UserToken` with the Connect token. | Server array: `AccessKey`, `SystemId`, `Name`, `Url`, `LocalAddress`. |
| Later | `GET {selected-server-api-base}/Connect/Exchange?ConnectUserId={ConnectUserId}` | `X-Emby-Token: <AccessKey>` and Emby client identity. | `LocalUserId`, `AccessToken`; use these only with the selected server. |

The guide switches terminology between `ConnectAccessToken` and `ConnectUserToken`; this is an unresolved naming ambiguity to confirm before implementation. It also says to persist the original `AccessKey` and perform the exchange whenever connecting to a server. No Connect request, account action, or token exchange was attempted during this research.

## Error Contract and Remote Acceptance Work

| Condition | Proposed client behavior |
| --- | --- |
| Login `400`, `401`, or `403` | Show a meaningful login/access error; do not assume all failures mean a wrong password. |
| Authenticated `401` | Treat the session as invalid and return to authentication according to the official workflow. |
| `401` plus `X-Application-Error-Code: ParentalControl` | Display restricted-access messaging instead of mislabeling it as a network failure. [Parental control](https://dev.emby.media/doc/restapi/Parental-Control.html) |
| `403` | Respect the server's permission denial; do not retry with an administrator token. |
| Network failure, `5xx`, timeout | Preserve the server profile and allow a bounded reconnect; do not discard credentials merely because the network is unavailable. |
| Public-user list empty | Keep manual login available; the server may deliberately hide accounts. |

Before claiming a supported Emby version, execute the following on the authorized remote `test-env`, not locally: pre-authentication public-info/user/image calls; normal and passwordless login; persisted-token restoration; same-name servers with different IDs; reverse-proxy base paths; explicit logout/revocation; parental-control handling; and offline logout behavior. Compare response fields with the target server's own API definition. These checks remain pending; documentation download is not server compatibility verification.
