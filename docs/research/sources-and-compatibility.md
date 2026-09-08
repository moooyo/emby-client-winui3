# Research provenance and compatibility ledger

Research date: **2026-09-09**. The repository was empty apart from Git metadata when this work began.

This research read publicly available official documentation, official repository files, and package publisher metadata. The search gateway was unavailable, so official web resources were fetched directly with read-only HTTP requests. No Emby account, running server, playback fixture, package restore, or application runtime was exercised.

## Emby source hierarchy

Use the actual target server's documented contract and recorded behavior to settle deployment-specific questions. Cross-check that behavior with official guidance, particularly where generated authorization metadata is misleading. The following sources are reference material, not a supported-server declaration.

| Source | Observed metadata | How to use it |
| --- | --- | --- |
| [REST API documentation](https://dev.emby.media/doc/restapi/index.html) | Conceptual guides for authentication, library access, and playback | Intended client workflow and semantic guidance; some examples are old |
| [REST API reference](https://dev.emby.media/reference/RestAPI.html) | Separate service and operation pages | Method/path and field reference; generated pages can contain missing or misleading type/auth annotations |
| [Official SDK definition, pinned](https://github.com/MediaBrowser/Emby.SDK/blob/bdd0dd7c0801f6e069dff2795d80cddae6f91791/Documentation/Download/openapi_v2_noversion.json) | Swagger `2.0`; `info` has no server version; repository commit `bdd0dd7c0801f6e069dff2795d80cddae6f91791`, dated `2026-05-18` | Reproducible field/route baseline; cannot establish the server version that introduced a field |
| [Static API Browser](https://swagger.emby.media/?staticview=true) and [its public definition](https://swagger.emby.media/openapi.json) | OpenAPI `3.0.1`; `info.version` is `4.1.1.0` | Historical cross-check; includes example instance configuration and plugin-specific operations |
| [OpenApiService reference](https://dev.emby.media/reference/RestAPI/OpenApiService.html) | Lists `/openapi`, `/openapi.json`, `/swagger`, `/swagger.json` | Candidate routes for the actual target server's definition, with its own access requirements |

The static definition's embedded `servers` URL must never be imported as a connection default or contacted as part of testing. The local reference deliberately includes only client-relevant routes and does not copy the full external specification or its plugin inventory.

## Known source discrepancies

| Topic | Observation | Implementation decision / evidence still needed |
| --- | --- | --- |
| Server version | The static definition says `4.1.1.0`; the pinned SDK omits a version | Supported minimum and maximum server releases are TBD; capture each target's `System/Info/Public.Version` |
| Public/sign-in operations | Some generated reference pages label public information and sign-in as requiring user authentication | Follow the official login workflow; verify anonymous access and proxy policy on the target server |
| Authentication response types | Some reference renderings display `User`/`SessionInfo` as arrays; the pinned SDK models objects | Use object DTOs and confirm with a sanitized authentication fixture |
| Legacy search and filter shortcuts | `/Search/Hints`, `/Items/Filters`, `/Items/Filters2` exist in the old static definition but are absent from the pinned SDK | Use user-scoped item queries with `SearchTerm` for MVP; absence from one snapshot does not prove removal from all servers |
| Episode/EPG query returns | Some newer operations omit a concrete response schema | Do not infer array versus query-result wrapper from the page name; capture fixtures |
| Playlist addition | Older definition has no typed result; newer reference/SDK includes `AddToPlaylistResult` | Be deliberate about versioned response handling; keep playlist editing after MVP |
| Playback fields | Newer SDK includes fields such as `CurrentPlaySessionId`, `DirectStreamUrl`, and `AddApiKeyToDirectStreamUrl` that the old baseline lacks | Use them only where returned/supported; retain a conservative fallback and redact URLs |
| Direct-play terminology | Older video guidance uses terminology that does not cleanly match modern source flags | Make decisions using media-source flags, the actual transformation, and verified engine capability |
| Encoding cleanup | Older guidance describes `DeviceId`; newer definitions also require `PlaySessionId` | Scope cleanup to the correct attempt/session; verify stop and seek cleanup on the target |
| WebSocket URL | Conceptual examples and official client code differ in path construction | Verify the Emby WebSocket route behind the actual proxy; do not substitute a Jellyfin `/socket` route |
| Additional legacy file route | Both sources contain `/Items/{Id}/Download`; old static schema additionally contains `/Items/File`, absent from the pinned SDK | Do not infer a route migration; verify the shared download route before adding later download support |
| Remote play parameter placement | Conceptual guide describes post data; schema also defines query parameters | Confirm request placement before enabling remote control |
| Series-timer types | Default, create/update, and get-by-ID schemas use inconsistent timer types | Verify concrete response/request fixtures before implementing recording rules |

Detailed evidence and endpoint links appear in the corresponding [API pages](../api/README.md). These discrepancies are reasons for a small explicit compatibility layer, not for a generic client that guesses arbitrary endpoints.

## Windows and package sources

The [implementation plan](../architecture/windows-client-plan.md) records the full package and playback research. Recheck versions when scaffolding the project; no packages were installed during this task.

| Primary source | Decision informed |
| --- | --- |
| [Windows App SDK release channels and servicing](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) | Supported stable baseline, Windows support boundaries |
| [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | LTS runtime choice and maintenance horizon |
| [Microsoft.WindowsAppSDK package metadata](https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk/2.4.0/microsoft.windowsappsdk.nuspec) | Exact NuGet version mapping, distinct from general release-channel labels |
| [WinUIEx](https://github.com/dotMorten/WinUIEx) | Window helpers and lifecycle simplification |
| [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows) | Optional WinUI helpers/controls |
| [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) | MVVM source generators and messaging |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | Official native-player bindings and WinUI integration |
| [mpv](https://github.com/mpv-player/mpv) | Alternate engine integration and build-specific license considerations |

Do not equate an upstream package's listed target framework with a verified combination of .NET, Windows App SDK, graphics driver, native decoder binary, and CPU architecture. Record that entire combination when a playback proof of concept passes.

## Decisions that remain open

1. The supported Emby server release range and feature/permission matrix.
2. The playback engine selected after authenticated stream, subtitle, and WinUI composition trials.
3. Exact dependency pins after a remote restore/build and player proof of concept.
4. Minimum supported Windows release, including whether legacy Windows 10 installations receive support.
5. Initial CPU architectures; x64-first is a proposal, and ARM64 requires native decoder and driver verification.
6. Project license, release identity, signing, and decoder redistribution artifacts.

No project license was created. An open-source objective does not select a license by itself, and a publicly readable SDK repository does not automatically grant unrestricted copying. The implementation proposal uses original client code and upstream packages under their stated licenses; component notices and actual native binary build options must be reviewed before distribution.

## Updating this research

For each revision, record the retrieval date, exact official URL, immutable commit/package version when available, relevant differences, and the target server versions affected. Keep sanitized fixtures and verification results separate from research assertions. Never commit tokens, real server addresses, passwords, private media paths, or credential-bearing URLs into examples.
