# Lumen design acceptance catalog

`--lumen-design-catalog` is an opt-in visual baseline for the external `Emby Player UI v4.dc.html` handoff. It implies `--lumen-catalog`, but the existing functional catalog is unchanged unless the new switch is present. The design mode has the separate server identity `synthetic-lumen-design-server-0001`, a synthetic public server name, and an explicit `LumenDesignCatalog` statistics field.

The catalog is test-only data. Titles, plots, people, years, ratings, favorites, and watched states are fictional. Primary Chinese labels are stored as ASCII Unicode escapes in fixture source, while `OriginalTitle` contains a separate English label. The six hero English names come from the handoff; English labels for its other titles are fixture translations, not verified movie metadata. No data is injected into the application and normal authenticated API routes are used.

## Enable

On the explicitly authorized acceptance machine, use an existing generated MP4 and its matching inspection JSON:

```powershell
dotnet tools/EmbyClient.FixtureServer/bin/Release/net10.0/EmbyClient.FixtureServer.dll --media-dir D:\Code\emby-client-winui3\tools\EmbyClient.MediaFixtures\artifacts\sixty-seconds --port 18962 --lumen-design-catalog --artwork-directory D:\Code\design_handoff_emby_player_ui
```

The external directory must remain absolute and explicit. The existing fixed numeric JPEG mapping, root containment, size checks, and JPEG marker checks are retained. No images are copied into the application or repository. The actual handoff contains only `assets/p`, `assets/b`, and `assets/s`; cast nodes advertise no image tag so the client can render their initials, as in the prototype.

## Visual Contract

The actual design catalog contains 32 movies, 18 series, 33 seasons, 360 episodes, 14 people, 22 genre nodes, three collections, and three local trailers. These are real fixture counts, not the prototype's decorative 1,284-movie and 326-series library totals. Movie IDs `1001` through `1032` preserve the first 32 entries of the functional catalog. The six existing series IDs remain stable; twelve additional series complete the handoff selection.

Movie and series wall `SortName` values preserve the handoff's pinyin-initial ordering with stable input order inside each initial. The following opt-in latest sequences apply to both grouped Latest and a descending DateCreated query. They are curated fixture ordering, not evidence of real server addition dates; premiere dates are not rewritten to force this order.

| Shelf | IDs, in order |
| --- | --- |
| Latest movies | `1004,1027,1001,1012,1029,1024,1006,1014,1019,1010` |
| Latest series | `2200,2400,5400,4200,2000,4400,3200,5000,3600,2800` |
| Continue watching | `2115,1006,2213,2418,1007` |
| Favorite membership | `1004,2000,1014,2200,1006,2400,1020,3400,1025,4800,1005,4400` |

The resume states are 52%, 38%, 30%, 78%, and 14%, respectively. Movie `1001` retains 36% detail progress and `1014` retains 66% wall progress, but both are initially hidden from the resume shelf to match its five visible handoff entries. Favorite membership matches the prototype, while subsequent client-selected sorting still applies normally.

The main series (`2000`) has three seasons (`2100`, `2110`, `2120`) of ten episodes each. Season one and the first four episodes of season two are played. `2115` is S2E5, the handoff's Silent Zone entry; its primary and thumb image are `s/541.jpg`. NextUp is derived from those states. Episode numbers 10 and above use a `-episode-NN` suffix to avoid colliding with numeric season IDs. The prototype's unplayed badge says six despite its 30 episodes and 14 played episodes. This fixture reports the consistent remaining 16 episodes. It does not invent a fourth special season, for which the handoff has only a visual tab.

The main movie cast is Lin Xia, Zhou Ye, He Chuan, Jiang Wan, Gu Nan, Xu Mo, Chen Yiming, Ye Zhiqiu, and Bai Zhou, with the handoff's corresponding Chinese names and roles. Series cast uses Lin Xia, Zhou Ye, Jiang Wan, He Chuan, Shen Ting, Gu Nan, Qiao An, Ye Zhiqiu, and Bai Zhou. The primary movie and series retain exactly those nine credits. The fictional search names Lin Ye, Ye Chuan, and Su Yebai have Actor, Director, and Actor credits on non-hero movies `1002`, `1017`, and `1029`, respectively. Each currently has one derived movie credit and no series credits; the prototype's decorative work totals are not copied. No real biography, portrait, or filmography is asserted.

## Exact Primary Artwork Mapping

All entries below use `assets/p/<key>.jpg` for their primary artwork. The original handoff has only eight backdrop keys and 21 landscape keys. Known hero backdrops and available landscape images retain their corresponding key; other movies use `b/515` and other series use `b/541`, with the same type-based fallback for missing thumbs. These bounded fallbacks are documented limitations, not purported per-title production art.

| ID | English fixture label | Key |
| --- | --- | --- |
| 1001 | The Fallen Ground | 515 |
| 1002 | Sleepless City | 249 |
| 1003 | Beyond the Dunes | 525 |
| 1004 | Star Trails | 683 |
| 1005 | Train in the Mist | 227 |
| 1006 | The Way Home | 804 |
| 1007 | Blue Van | 655 |
| 1008 | The Forest Guest | 633 |
| 1009 | Summer Solstice | 778 |
| 1010 | Fireworks | 660 |
| 1011 | Snowline | 726 |
| 1012 | The Nameless | 375 |
| 1013 | The Lone Walker | 331 |
| 1014 | The Ninth Station | 494 |
| 1015 | Under Neon | 579 |
| 1016 | Red Rock | 247 |
| 1017 | A Night in Prague | 545 |
| 1018 | Old Valley | 556 |
| 1019 | Distant Mountains | 447 |
| 1020 | Deep Blue | 581 |
| 1021 | The Long Road | 563 |
| 1022 | London Fog | 122 |
| 1023 | Echo | 453 |
| 1024 | Her Reading Room | 832 |
| 1025 | A Father's City | 838 |
| 1026 | Last Bus | 408 |
| 1027 | The Tunnel | 396 |
| 1028 | Sunset Boulevard | 402 |
| 1029 | Nightwalker | 352 |
| 1030 | Eye of the Storm | 404 |
| 1031 | Sixty Degrees North | 786 |
| 1032 | The Journey | 757 |
| 2000 | Echoes of the Deep | 541 |
| 2200 | Rain Night | 797 |
| 2400 | Bamboo Wanderer | 666 |
| 2600 | Letters from Istanbul | 670 |
| 3200 | Above the Clouds | 685 |
| 2800 | Lost Bearings | 291 |
| 3000 | Fog Forest | 543 |
| 3400 | Black and White City | 43 |
| 3600 | The Lighthouse | 58 |
| 3800 | The Last Highway | 339 |
| 4000 | Between the Mountains | 443 |
| 4200 | Old Street | 419 |
| 4400 | Night Flight | 594 |
| 4600 | Skyline | 391 |
| 4800 | Ranch Memories | 729 |
| 5000 | Underground | 345 |
| 5200 | The Amusement Park | 639 |
| 5400 | Water City | 826 |

Season two's ten episode image keys are `700,612,841,552,541,581,293,594,404,598`, all from `assets/s`. The rain-night resume episode (`2213`) uses `s/797`; the bamboo resume episode (`2418`) uses `s/666`. The first two title collections are unchanged memberships; the home collection contains only the matching design movies `1006,1025,1032`.

## Honest Playback Boundary

All movie, episode, and trailer `RunTimeTicks`, media sources, chapters, source dimensions, H.264 video, AAC audio, channel counts, frame rate, container, size, and negotiated streaming behavior still describe the original generated synthetic MP4. With the sixty-second fixture, every playable title is actually sixty seconds. This mode deliberately does not copy the prototype's feature-film duration, 4K/HDR/Dolby Vision/Atmos/HEVC labels, alternate language tracks, subtitle tracks, or unsupported playback capabilities. The primary movie's second source is still explicitly a duplicate alias of that same file.

Consequently, remaining-time labels and technical badges will differ from the prototype. API contract success and design-fixture availability establish neither visual fidelity nor Emby compatibility. Acceptance needs separate application screenshots and a comparison against the handoff.

## Bounded Contract Checks

The existing test profile remains unchanged. A separate `--lumen-design-only` profile checks title/cast separation, wall and shelf ordering, five resume entries, twelve favorite selections, S2E5 and real played counts, numeric image mappings, and truthful source metadata. It creates marker-bearing synthetic JPEG test bytes only to prove routing; it does not decode them as artwork or claim media playback verification. Run it only in the user-approved verification environment:

```powershell
dotnet tools/EmbyClient.FixtureServer/Tests/bin/Release/net10.0/FixtureBoundaryChecks.dll C:\absolute\path\EmbyClient.FixtureServer.dll --lumen-design-only
```
