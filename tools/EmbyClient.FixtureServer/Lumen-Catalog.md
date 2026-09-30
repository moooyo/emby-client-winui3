# Lumen synthetic catalog

This opt-in mode supplies a populated development library for the Lumen client redesign. It is still a loopback-only synthetic fixture, not Emby Server or a compatibility certification. The account remains `demo` / `demo`, all titles and people are fictional, and every playable item streams the same original generated color-and-tone MP4. Movie and episode descriptions explicitly identify this distinction.

## Start

Use an existing generated MP4 together with its matching inspection JSON. For example, on the authorized Windows acceptance machine:

```powershell
dotnet run --project tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release -- --media-dir D:\Code\emby-client-winui3\tools\EmbyClient.MediaFixtures\artifacts\sixty-seconds --port 18962 --lumen-catalog --artwork-directory D:\Code\design_handoff_emby_player_ui
```

`--lumen-catalog` is off by default. It preserves the default small catalog's existing IDs in the default mode; those IDs have richer fictional titles only when the new mode is enabled. It cannot be combined with `--large-library-items`, which remains a separate acceptance profile. Existing image delays, injected negotiation failures, and bounded boundary controls can still be used.

`--artwork-directory` requires an absolute path and `--lumen-catalog`. Without it, the new catalog uses generated PNG artwork with the appropriate portrait or landscape shape. With it, the server reads its fixed numeric mapping from `assets/p/*.jpg`, `assets/b/*.jpg`, and `assets/s/*.jpg` inside the explicitly named handoff directory. Required JPEGs are checked and loaded before binding. Missing directories, missing files, invalid JPEG markers, and files larger than 4 MiB are rejected. Request IDs and image types never become arbitrary filesystem paths.

These handoff images are test-only placeholders. They remain outside the repository and are never copied to `EmbyClient.App/Assets`, published as production media, or downloaded by the fixture. Person entries deliberately use the client's initials fallback instead of presenting a landscape image as a real actor portrait. No logo image is advertised when the handoff does not contain one.

## Catalog Contract

The initial catalog has 40 movies, 6 series, 12 seasons, 72 episodes, 12 people, 16 genres, 3 collections, and 3 local trailers, plus three library views. Each series has two seasons with six episodes each. Counts, years, ratings, plots, cast credits, favorites, and watched states are synthetic development data.

| ID | Entry |
| --- | --- |
| `movies`, `shows`, `collections` | Library views |
| `1001` | The Fallen Ground, primary movie and two honest fixture source aliases |
| `1002` | Sleepless City, secondary movie |
| `1001` through `1040` | Movies |
| `2000` | Echoes of the Deep, primary series |
| `2100`, `2110` | Primary series seasons 1 and 2 |
| `2101` through `2106` | Primary series season 1 episodes |
| `2111` through `2116` | Primary series season 2 episodes; `2115` starts with resume progress |
| `2200`, `2400`, `2600`, `2800`, `3000` | Rain Night, Bamboo Wanderer, Letters from Istanbul, Lost Bearings, Fog Forest |
| `person-01` through `person-12` | Fictional cast and crew |
| `50001` through `50016` | Genre nodes; numeric IDs match `GenreItems[].Id` |
| `collection-night`, `collection-frontier`, `collection-home` | Initial BoxSet collections |
| `trailer-1001`, `trailer-1002`, `trailer-2000` | Local synthetic trailers |

Movies `1001`, `1006`, and `1008` and episodes `2115` and `2212` initially have resume progress. The primary series season 1 and the first four season 2 episodes are played, so NextUp initially returns `2115`. NextUp orders season number before episode number. Supplying a season from a different series to the episodes route is rejected.

Every playable item reports the MP4's actual inspected duration, resolution, file length, H.264 video, AAC audio, channel count, sample rate, and available frame-rate metadata. The `1001` source IDs are `synthetic-mp4` and `synthetic-mp4-alternate`; the latter is named "Duplicate fixture alias" and streams the exact same bytes. A selected source ID is honored during negotiation and checked when a stream includes a source ID. This is a version-selector exercise, not a claim of multiple encodes. Non-media Person, Genre, Series, Season, and BoxSet nodes cannot negotiate playback.

Chapter positions are proportional to the real test clip duration and remain within it. The data includes `Chapter`, `IntroStart`, `IntroEnd`, and `CreditsStart` markers, with monotonically increasing chapter indices. These are deterministic synthetic markers, not detected film intros or credits.

## Additional Routes

All routes below use `/emby`, require a fixture-issued token, and reject a supplied different user ID. The normal views/items/latest/resume/seasons/episodes/playback/report routes remain available.

- `GET /Genres` and `GET /Persons`: `QueryResult<BaseItemDto>`, scoped by parent and included media types, with name search and pagination.
- `GET /Items/{id}/Similar`: related same-type films or series, excluding the source, returned as `QueryResult<BaseItemDto>`.
- `GET /Users/{user}/Items/{id}/LocalTrailers`: a bare `BaseItemDto[]` array, with playable local trailer IDs.
- `GET`/`HEAD /Items/{id}/Images/{Primary|Backdrop|Thumb|Chapter}[/{index}]`: the normal image routes. Backdrop accepts index 0; Chapter accepts existing chapter indices. Unsupported types, negative/out-of-range indices, and extra path segments return 404. Handoff images are `image/jpeg` with content-derived tags; generated images are PNG.
- `POST /Users/{user}/Items/{id}/HideFromResume?Hide=true|false`: hides/restores resume visibility without discarding playback position. Applying it to a series changes descendant playable items.
- `POST /Collections?Name=...&Ids=...`: creates a synthetic BoxSet and returns `{Id,Name}`. `POST`/`DELETE /Collections/{id}/Items?Ids=...` adds/removes members. Members are deduplicated and validated before mutation; their original `ParentId` remains unchanged.
- `POST /Items/{id}`: accepts item metadata but changes only Name, OriginalTitle, Overview, ProductionYear, OfficialRating, Genres, Tags, LockData, and LockedFields. IDs, types, parentage, measured technical media data, and stream URLs are never taken from this request. New genres are in-memory nodes, bounded to 64 total.
- `POST /Items/{id}/Refresh`: records a synthetic refresh request without reading a real library, fetching artwork, or changing production files.
- `POST /Users/{user}/Configuration`: stores the submitted user configuration in memory, including the supported autoplay, resume-rewind, and intro-skip choices.

Collection and metadata actions are available only in Lumen mode, whose synthetic user policy advertises the corresponding administrator capability. At most 32 collections are retained; each member update accepts at most 50 IDs. Labels and metadata have bounded lengths. These policies describe the fixture, not real Emby permission or mutation behavior.

Item queries support `Genres` and `Tags` separated by `|`, comma-separated `PersonIds`, name prefix/range filters, favorites/played filters, and common Name, SortName, PremiereDate, ProductionYear, CommunityRating, RunTime, IndexNumber, and DatePlayed sorts. Latest episodes can be grouped into Series; `IncludeItemTypes=Series` is also accepted for this fixture's grouped latest endpoint. No claim is made that every Emby query option is implemented.

## Synthetic Identity and Observations

Before an automated profile signs in, it can check the `X-Synthetic-Fixture` response header, `GET /emby/System/Info/Public`, and `GET /_fixture/stats` without credentials. Lumen uses the fixed allowlisted identity:

```json
{
  "Id": "synthetic-lumen-server-0001",
  "ServerName": "SYNTHETIC Lumen Library",
  "Version": "synthetic-1.0"
}
```

Stats report `Synthetic=true`, `LumenCatalog=true`, `ServerId=synthetic-lumen-server-0001`, and `ExternalArtwork=true|false`, alongside the existing playback/image/query counters. Additional counters track collection updates, metadata updates, refresh requests, configuration updates, and hidden resume items. Stats never expose tokens, passwords, request bodies, the external handoff path, or the media filesystem path. Existing histories remain bounded to 200 events/queries.

## Bounded Contract Checks

The independent fixture harness exercises this mode and the default boundary profile against separately owned loopback instances. Build the fixture separately, then run:

```powershell
dotnet run --project tools/EmbyClient.FixtureServer/Tests/FixtureBoundaryChecks.csproj --configuration Release -- C:\absolute\output\EmbyClient.FixtureServer.dll
```

Append `--lumen-only` to omit the delay-heavy default boundary groups while retaining the Lumen and invalid-configuration checks. The harness creates protocol-only dummy MP4 bytes with a valid header and matching synthetic metadata; it does not decode those bytes and cannot prove playback. Its tiny external JPEG data only tests transport, route validation, and byte preservation. Real visual and decoding acceptance must use the separately generated playable file and the actual external handoff artwork on an explicitly authorized verification machine.
