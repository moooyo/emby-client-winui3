# Synthetic Emby development fixture

This loopback-only ASP.NET Core tool serves deliberately synthetic data for developing the Windows client. It is **not Emby Server**, a production server implementation, or evidence that the client is compatible with any real Emby version. All user state, access tokens, sessions, and counters are held in memory and reset when the process exits.

The fixed development account is `demo` with password `demo`. Never enter real credentials into this fixture. Public server metadata and every HTTP response identify the service as synthetic. It listens only on `127.0.0.1`; environment endpoint settings and arbitrary `--urls` arguments are not accepted. Request bodies, credentials, and token-bearing URLs are not logged.

## Run

Generate the original color-and-tone media using the adjacent Windows media tool first:

```powershell
dotnet run --project tools/EmbyClient.MediaFixtures/EmbyClient.MediaFixtures.csproj --configuration Release
dotnet run --project tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release -- --media-dir tools/EmbyClient.MediaFixtures/artifacts
```

Connect the app to `http://127.0.0.1:18960` using the development credentials. `--port` can select another nonprivileged port; binding remains IPv4 loopback. The tool requires `fixture-h264-aac.mp4` and the companion `fixture-h264-aac.json` inside `--media-dir`. It refuses missing, empty, malformed, or mismatched media instead of manufacturing an empty stream. Duration, resolution, codec, and channel metadata come from the generated file's Windows media inspection.

Stop the server with Ctrl+C in its managed terminal session. No independent visible window or background service installation is required.

For a bounded real-AOT scrolling/memory trial, see [Large-library acceptance](Large-Library-Acceptance.md). The optional `--large-library-items 5000` mode adds a separate paged library; `--image-delay-ms 100` adds a bounded image delay, and `--fail-first-playback-info` injects one playback-only HTTP 503 for a Retry trial. All three are off by default and require no real server or account. Use a separate port and isolated artifact output as shown in that guide.

The project excludes local `artifacts/**`, `bin/**`, and `obj/**` from default SDK item discovery. This prevents generated assembly files or published payloads from being compiled or copied by later normal solution builds. Do not remove a running artifact directory to work around build errors; retain the project exclusions and use a separate output directory for each active fixture instance.

## Implemented development routes

All Emby-shaped paths use the `/emby` prefix. Except public metadata/user discovery and login, routes require a token issued by the fixture. Original-byte media also accepts that fixture token in `api_key` for clients that cannot attach headers.

- Public/system information, public/current user, `AuthenticateByName`, and logout.
- Two library views, synthetic movies and television, item details, search, pagination, latest, resume, seasons, episodes, and next-up.
- Locally generated PNG poster bytes. The same bounded poster is returned for image variants; this is not a complete Emby image transformation implementation.
- Favorite and played-state changes, held in memory for the synthetic user.
- `POST /Items/{Id}/PlaybackInfo`, returning one original HTTP MP4 source, with a distinct play-session ID and the measured generated-media metadata.
- Authenticated `GET`/`HEAD /Videos/{Id}/stream`, using ASP.NET Core file-range processing (`200`, `206`, `416`). The fixture does not pretend to transcode, expose HLS, open live sources, or deliver subtitles.
- Start, progress, stop, capability registration, and encoding-cleanup counters. Playback reports must reference a play session previously issued for that item.

The default catalog is deliberately small and does not reproduce every Emby filter, sort order, permission, version difference, or mutation rule. A completed synthetic playback marks the item played at 90% of its duration; that threshold is a fixture policy, not an assertion about Emby configuration.

## Read development statistics

```powershell
Invoke-RestMethod http://127.0.0.1:18960/_fixture/stats
```

The loopback statistics endpoint is intentionally readable without login. It exposes only synthetic IDs, bounded recent playback events, and counters for login, negotiation, range media requests, partial responses, capabilities, start/progress/stop, and encoding cleanup. It does not expose access tokens, passwords, request bodies, or local media paths. Playback event history is bounded to the most recent 200 events; event-derived counts refer to that retained history.

Use these counters to confirm that an end-to-end UI trial negotiated media, read actual ranges, and sent appropriate check-ins. Passing a synthetic trial does not replace tests against an authorized real Emby server, native playback/rendering inspection, or the documented compatibility matrix.

## Implementation sources

JSON reads and writes pass explicit generated `JsonTypeInfo` from `EmbyJsonContext` or the small fixture context. No reflection JSON serialization or extra package is used. The Emby-shaped contracts follow the repository's curated [API documentation](../../docs/api/README.md).

Microsoft's [minimal API response guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0) and [Results.File reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.results.file?view=aspnetcore-10.0) document `enableRangeProcessing`, partial-content responses, and file delivery. The [Kestrel endpoint guide](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0) documents binding directly to `IPAddress.Loopback`. These sources were read on 2026-09-09 before implementing the fixture.
