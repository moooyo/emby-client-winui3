# Lumen native UI

Current fidelity candidate: AOT10, 23,432,192 bytes, SHA-256
`41FC56C007CE339636A4C4A8B0900D00951EB6B90B38400A3D63682487EEA6F0`.
Publication and all 966 product tests exited 0, including five new default-audio-
intent cases. The recorded post-test snapshot matched 308 production inputs at
`2026-09-30T18:28:56.0838558+00:00`; later edits are outside that evidence.
AOT10 adds default-audio-intent handling in helper/detail playback routes,
explicit localization of two recovery commands, and zero timeline/time for null
playback context. The same AOT10 executable now passed bounded local native
checks: 15 wide references re-captured/reviewed, default movie Continue,
localized zero-position recovery, 628x594 caption panels/local VTT import/Off,
PiP/fullscreen return, and three actual normal exits with zero scoped crash
events. See the [portable acceptance summary](#current-acceptance-summary); the
[independent visual review](../../artifacts/lumen-acceptance/fidelity-comparisons/AOT10-INDEPENDENT-VISUAL-REVIEW.md)
is an optional local QA artifact, not required to read the published scope.
Selected states 12/14 use the populated drawer and actual light-wall hover;
wrong intermediates remain retained and excluded. Final recheck confirms the
same 308 inputs and executable SHA after runtime acceptance.

AOT09's independent 15/15 wide review found no major layout collision, but
overall acceptance did not pass: movie Continue failed during Negotiating and
recovery commands were untranslated. Its correct The Fallen Ground captures,
other original states, review, normal exit code 0, and zero scoped crash events
remain AOT09 evidence only. Env11 owned cleanup released port 18984 with evidence
retained. Later Env12 guarded cleanup released port 18984 with seven Starts/seven
Stops and 247 partial responses recorded by the synthetic fixture. These current
results do not prove physical audio, motion, HDR, universal media support, or
release readiness. Local UI authorization was restored and supersedes the old
VM-only checkpoint; no VM UI was ever run. Approved elevated worker-preparation
cleanup completed without desktop input or starting a new worker. All owned
test resources are cleaned and no required UI/cleanup work remains. Safe
integration into `main` and push are requested/in progress, with unrelated
primary-checkout work preserved separately and excluded from publication.
Goal completion awaits publication/root confirmation. Private environment
details remain outside repository documentation. The local
[fidelity checkpoint](../../artifacts/lumen-acceptance/fidelity-final-report/ACCEPTANCE.md)
retains detailed QA records; the portable summary below is the publication entry.

Updated: **2026-10-01**. This is the implementation and verification checkpoint
for the native rewrite based on the v4 `design_handoff_emby_player_ui` reference
bundle. The frozen AOT08 source passed product
tests, publication, and scoped native fixture/official-server regressions. Owned
cleanup is complete, and the [final report](../../artifacts/lumen-acceptance/final-report/ACCEPTANCE.md)
and [receipt](../../artifacts/lumen-acceptance/final-report/acceptance-summary.json)
record the historical `AcceptedForScopedDesignHandoff` status. That functional
checkpoint did not establish matched high-fidelity restoration. The current
[fidelity checkpoint](../../artifacts/lumen-acceptance/fidelity-final-report/ACCEPTANCE.md)
records current AOT10 source results, reviewed AOT09 captures, and all intermediate AOT03/AOT04 and
later failure evidence. The fidelity AOT08 movie captures 04-06 used Star Trails
instead of the reference The Fallen Ground, and its Movies-tab poster load
failed; original receipts are retained, not promoted into a correct comparison.
AOT05/AOT07 shutdown crashes and the subsequent source guards retain their own
identities. AOT09's normal exit is a scoped regression observation, not universal
crash resolution. The latest human instruction restored local UI authorization;
the requested bounded AOT10 native checks completed with local runtime cleanup.
The goal-manager Blocked ledger is historical and goal completion still awaits
safe main integration/push and root confirmation. Release/device
certification remains separate.

## Current Acceptance Summary

Status: `UiAcceptancePassedAllOwnedTestResourcesCleanedPublicationPending`.
Date: 2026-10-01. The native WinUI 3 AOT10 design-handoff and named regression
scope passed; requested repository integration/push is in progress. No final
publication commit or overall goal-complete flag is asserted before that step
finishes. Unrelated work in the primary checkout is preserved separately,
not merged into this publication.

Verified executable: 23,432,192 bytes, SHA-256
`41FC56C007CE339636A4C4A8B0900D00951EB6B90B38400A3D63682487EEA6F0`.
Publication and 966 product tests exited 0, including five focused default-audio-
intent cases. All 308 recorded production inputs matched both the post-test
snapshot and the final post-native recheck. This is a bounded input manifest,
not a hash of unrelated repository changes.

All fifteen selected 1440x900 wide reference states were freshly captured from
that executable and independently inspected beside the design. No significant
visible layout/readability collision was found in those states, eight named
narrow images, and fullscreen. This is qualitative visible-state review, not
a pixel-perfect score or proof of every control's timing/backend operation.

| Reference | Current observed surface |
| --- | --- |
| 01 | Dark Home: Hero, navigation, commands, six thumbnails, continuation track |
| 02 | Movie wall: eight columns and complete first-poster hover controls |
| 03 | Series wall: hover controls, watched/unplayed badges, alphabet index |
| 04 | Matching movie detail: artwork, metadata, resume, selectors, cast |
| 05 | Anchored audio menu with the genuine selected AAC option |
| 06 | Supported More commands and divider without major clipping |
| 07 | Matching series detail, selected second season, current episode row |
| 08 | Search query/tabs, best match, people, collection, loaded media posters |
| 09 | Dark playback settings with visible grouped controls |
| 10 | Sign-out confirmation with both commands visible |
| 11 | Player tracks panel with readable two-line Chinese cue and transport |
| 12 | Populated ten-episode drawer with current row and complete cue |
| 13 | Light Home with the historical content/fade overlap corrected |
| 14 | Actual first-poster hover in the light eight-column movie wall |
| 15 | Light settings with readable surfaces, states, switches, and dividers |

Reference 12 selects the populated drawer, and reference 14 selects actual hover.
Dismissed-panel, idle-wall, and preliminary occluded Home captures are retained
locally as intermediates, not substituted as successful reference evidence.
All selected references and narrow checks followed the first owned Home app's
normal close. Historical failures and older candidate identities remain separate.

Actual default movie Continue began at 21.6238116 seconds and later paused at
41.4638082. The expected direct-only fixture rejection after explicit audio
selection separately showed localized recovery commands and 0:00/0:00 with the
timeline reset. At 628x594, Medium/Large Tracks and Large Episodes kept complete
two-line cues; the episode drawer's tenth row was reachable by scrolling. The
native desktop picker imported the actual English VTT file while paused at
15.08 seconds; Off immediately removed its cue/local row and released the panel
band. PiP 628x354 returned to 628x594 paused at 15.12 seconds, and actual
2560x1440 fullscreen entry/return was observed.

Three pre-attached exact-owned process handles recorded actual normal exit code
0, and their scoped Windows application/.NET crash-event audits found zero
matches. The local fixture recorded seven Starts/seven Stops and 247 partial
responses for 247 media/range requests. Local apps, fixture/port, and execution
sessions were cleaned. Approved elevated worker-preparation cleanup also stopped
its owned observer/fixture and released the port at
`2026-09-30T21:34:13.7616788+00:00`, with no desktop input, new worker, or error.
No VM application UI was ever tested, so this does not imply VM UI acceptance.

Original screenshot/UIA groups, one-to-one decoded comparison boards, native
exit/event receipts, synthetic fixture stats, and input manifests remain local
QA records under `artifacts/lumen-acceptance`; private worker cleanup receipts
remain outside the repository. These optional ignored artifacts may be absent
from a public checkout. This committed summary carries the current scope without
publishing screenshots, crash dumps, private environment paths, or account data.

The fixture uses actual approximately 60-second SDR H.264/720p media, AAC audio,
and genuine external/local WebVTT. Real catalog sizes, track counts, progress,
and avatar fallbacks are not invented to match prototype metadata. Acceptance
does not establish frame motion, independently heard physical audio, HDR,
universal codecs/subtitles, long-session reliability, complete accessibility,
signed installation/upgrades, or clean-machine release certification.

## Native architecture

[`MainWindow`](../../src/EmbyClient.App/MainWindow.xaml.cs) hosts
[`LumenShellPage`](../../src/EmbyClient.App/Views/Lumen/LumenShellPage.cs) directly.
The HTML handoff is a reference, not a WebView frontend. The shell composes native
WinUI controls and owns account/session transitions, navigation, preferences,
window-presentation requests, player return, and bounded shutdown. Existing API,
account protection, image-cache, playback coordination, reporting, recovery, and
transport components remain in use.

| Source | Responsibility |
| --- | --- |
| `src/EmbyClient.App/Views/Lumen/LumenShellPage.*` | Connection/session ownership, navigation, settings, and player composition |
| `src/EmbyClient.App/Views/Lumen/LumenLibraryView.*` | Home, paged movie/series/favorites walls, search, people, and viewport history |
| `src/EmbyClient.App/Views/Lumen/LumenDetailView.*` | Details, real source/track information, seasons/episodes, supplementary discovery, and explicit mutations |
| `src/EmbyClient.App/ViewModels/LibraryViewModel.PlaybackRecommendation.cs` | Nonblocking detail recommendations and owner-scoped quick-play waiting without duplicate discovery requests |
| `src/EmbyClient.App/Views/Lumen/LumenSettingsView.cs` | Playback, subtitles, appearance, account, and about views |
| `src/EmbyClient.App/Views/PlayerView*` | Custom native video HUD, queue/recovery, tracks, chapters, episode drawer, and captions |
| `src/EmbyClient.App/Services/LumenTheme.cs`, `LumenText.cs`, `LumenPreferenceStore.cs` | Shared brush identities, localized interface text, and bounded atomic preference persistence |
| `src/EmbyClient.App/Playback/NativePlaybackEngine.Lumen.cs`, `NativePlaybackEngine.LocalSubtitles.cs`, `NativePlaybackEngine.SubtitleDiagnostics.cs` | Source-specific native rate support, external/local timed-text presentation, targeted AOT projection preservation, and opt-in safe diagnostics |
| `src/EmbyClient.Playback/PlaybackCoordinator.cs`, `PlaybackRequestFactory.cs`, `PlaybackContracts.cs` | Source negotiation, independent video-conversion/subtitle-burn fallbacks, selection changes, and session lifetime |
| `src/EmbyClient.Api/EmbyApiClient.Lumen.cs`, `LumenModels.cs`, `Models.cs`, `EmbyJsonContext.cs` | Typed discovery, modern/legacy tag schemas, sparse raw-JSON mutations, and generated serialization |

The earlier `MainPage` and V7 views are not the active root. The historical
[implementation record](status.md), [V7 native report](v7-native-acceptance-20260915.md),
and [earlier user guide](user-guide.md) retain their named checkpoint context;
their screenshots and accepted executable identities do not certify Lumen.

## Implemented workflows

| Surface | Implementation scope |
| --- | --- |
| Home | Server-backed artwork hero with six latest candidates, selectable thumbnails and optional seven-second rotation; continue watching, next up, real library tiles/counts, and latest shelves |
| Media walls | Movies, series, favorites, virtualized poster/list layouts, responsive 6-10 column preference, server-backed name/year/date/rating sort, genre/watched filters, alphabet query, paging, and explicit retry/empty states |
| Details | Server metadata/artwork, favorite/watched actions, resume/restart, cast/person/work navigation, real seasons/episodes and resume recommendation, source/audio/subtitle selection, media information, similar items, and trailers |
| Search | Debounced server queries, all/movie/series/people/collection tabs, bounded paging, returned-text highlighting, person details/works, and back-state restoration |
| Mutations | Hide from continue watching without changing played/progress; create/add collections; request metadata refresh; edit permitted metadata fields while preserving complete untouched JSON |
| Settings | Dark/light theme, Gold/Coral/Sage/Blue accent, hero rotation, poster columns/watched marks, resume behavior, direct-play preference and bitrate caps, subtitle defaults, countdown/marker policy, account switching/sign-out, diagnostics, bundled licenses, and public release check |
| Player | Native `MediaPlayerElement` with play/pause, seek, volume/mute, source/track/quality selection, recovery, queue, previous/next episodes, episode/season drawer, source-specific 0.5-2.0 speed, fullscreen, compact overlay, and real chapter preview/skip/countdown controls |

Library titles, ratings, technical badges, counts, markers, and artwork come from
server DTOs or observed native output. Missing values are omitted or have an
explicit unavailable state. Prototype sample media is not embedded as a fake
catalog. A chapter image is a server chapter preview, not an arbitrary exact-time
video thumbnail. Intro/credits controls require valid server marker pairs or
`CreditsStart`; chapter names alone do not enable skipping.

Search highlights inspect returned title, tags, or overview. They do not invent
a server relevance score or matched-field provenance. Local trailers use normal
playback negotiation; HTTP(S) remote trailer metadata opens externally without
passing the Emby token.

Metadata editing uses `CanEditItems` when supplied and administrator status only
as an absent-capability fallback. Metadata refresh and collections do not share
an assumed administrator-only gate. Server authorization remains authoritative.
Non-null modern `TagItems` supply displayed tag names, including when the modern
array is empty; legacy `Tags` keep their own wire field and are used only when
modern tags are null/absent. Sparse edits preserve raw tag IDs/unknown fields,
and unchanged genre/tag editor text is never reparsed. This prevents a no-op save
from splitting existing names that contain comma/semicolon separators.
See [Lumen capability contracts](../api/06-lumen-capabilities.md) for endpoint
shapes, bounds, field-preserving mutations, and the official `4.9.5.0` finding
that resume queries require `MediaTypes=Video` when the caller omits media types.

Series quick play waits for the resume/next-up recommendation already issued by
detail loading, without adding duplicate requests or blocking the detail page.
The waiting intent rechecks page, session, season, navigation, and permission
ownership before dispatching playback. Optional discovery failure uses loaded
episodes; explicit movie/episode play and season-specific choices stay explicit.
Canceling only the wait does not cancel the underlying detail recommendation.

Search layout applies an unchanged-width/page/tab/revision key and only writes
changed grid/size properties. Best-match content uses a bounded maximum width
instead of a SizeChanged feedback loop. This addresses the AOT05 loaded-search
layout cycle; cold narrow loaded queries were later observed on AOT06/AOT08.
Real person-to-search history restoration and fresh empty Search navigation are
different scenarios; an empty navigation capture is not cached-query proof.

## Preferences and assets

Client preferences live in `lumen-preferences.json`, independently of saved
account credentials. The store uses generated JSON, a 64 KiB bound, validation,
serialized writes, a same-directory temporary file, and atomic replacement.
Unreadable/invalid data does not silently overwrite the existing file. Changes
to next-episode autoplay, subtitle language, and subtitle mode save sparse server
configuration updates when the user's policy allows preference access. Theme,
accent, layout, bitrate behavior, caption style, and countdowns remain local.
New-profile LAN bitrate defaults to `0`/Unlimited; it removes only the client
cap, not authenticated server/account limits. Existing persisted choices are
not reset by this default change.

The complete, unmodified Noto Serif SC and Manrope variable fonts are included
in `Assets/Lumen/Fonts`; Chinese interface text has Windows font fallback. The
handoff Lucide icons are included in `Assets/Lumen/Icons`. No font or icon is
downloaded at runtime. Their [font provenance and SIL OFL notices](../../licenses/third-party/Lumen-Fonts/README.md)
and [Lucide provenance/ISC notice](../../licenses/third-party/Lumen-Lucide/README.md)
ship with the publish output. The repository's own license still awaits an
owner decision.

## Player boundaries

The custom HUD auto-hides after three seconds when no active interaction blocks
hiding. Native caption buttons remain visible in windowed mode: Windows ignores
their foreground alpha. Fullscreen uses the native fullscreen presenter without
those buttons. Windowed chrome is not advertised as completely hidden.

External WebVTT uses the Windows `TimedTextSource` parser. Parsed cues are
presented in an app caption layer against the native playback clock; supported
text tracks expose size, outline, bottom/black-band placement, and +/-10-second
delay in 0.1-second steps. Local `.vtt` import is bounded to 4 MiB, belongs to one
playback ID, and is not uploaded. It selects server subtitles off before loading
local text so burned captions are not falsely treated as removable text.
When only server Off exists, a valid owned local import can enable that one Off
command without enabling unavailable server tracks. Picker completion refreshes
its eligibility after the modal flag clears. Actual Off clears local caption
presentation without restarting the paused media clock.

Video conversion and subtitle burning are independent selection states:
`ForceTranscoding` does not itself replace external WebVTT with burned text.
`ForceSubtitleBurnIn` is a bounded fallback for a subtitle-delivery failure and
is cleared when the source/subtitle changes, not by unrelated quality/audio
changes. Their pure contract coverage is separate from native caption output.

The native cue wrapper requires targeted AOT preservation: `DynamicDependency`
on `CaptureSubtitleCues` retains nonpublic constructors/public fields on
`TimedTextCue` and `TimedTextLine`, matching the generated WinRT wrapper scope.
This source repair addresses the observed AOT01 runtime-class projection loss;
AOT02 subsequently showed plain English/French cues and local WebVTT, while
AOT08 repeated local import, paused seek, immediate Off, and caption clearance
on the fixture, plus actual English/French server cues and paused track changes
on official Emby. An opt-in,
64 KiB bounded `native-subtitle-diagnostics.log` under an existing absolute
`EMBY_CLIENT_DATA_ROOT` records counts, wrapper types, and sanitized error
metadata. It records no text, URLs, IDs, credentials, or raw exception messages
and cannot alter playback outcomes.

These features are source/native-format dependent. Server-burned subtitles
cannot be styled or delayed by the app. The baseline remains SDR H.264/AAC MP4
and server-generated HLS with explicit codec/transcoding constraints. Hardware
decoder override, HDR output, and refresh-rate matching are visibly disabled with
accurate reasons. Advanced ASS/PGS rendering, HDR/Dolby Vision, passthrough,
multichannel output, universal codecs, and physical media commands remain
unverified or unsupported; the API expansion does not change those boundaries.

## Historical Functional Verification

The task authorized local Windows verification and isolated backend/test-data
work through `ssh test-env`. Desktop operation is currently suspended by the
latest human instruction. This table retains the historical functional AOT08
checkpoint, not the newest fidelity AOT10 identity or permission to resume UI.
Evidence categories remain separate; counts do not imply native output.

| Evidence | September 30 checkpoint | Location/boundary |
| --- | --- | --- |
| Product compilation | Fifth Debug application build succeeded; zero errors and one existing generated WinUIEx `Icon` `CS0618` warning | `artifacts/lumen-build-debug-fifth.log`; compilation is not AOT/runtime acceptance |
| Final product test suites | Exit 0; 919 passed, zero failures/skips: API 174, AppState 99, MediaTransport 125, Platform 326, Playback 195 | `artifacts/lumen-tests-release-final-candidate.log`; added coverage includes 40 TagItems, 13 quick-play recommendation, and 53 external-text/delivery-fallback cases |
| AOT08 publication | Exit 0; 23,101,952-byte executable, one existing generated WinUIEx `CS0618` warning | `artifacts/lumen-aot-candidate-08`, `artifacts/lumen-publish-aot-eighth.log` |
| AOT08 source inputs | 302 captured build inputs compared unchanged | `artifacts/lumen-acceptance/final-report/source-inputs-aot08.json`; source/assets/licenses/root configuration only, not the entire repository or native acceptance |
| Bootstrap boundaries | 42/42 passed with explicit administrator-role verification; tool compilation has zero warnings/errors | `artifacts/lumen-acceptance/tool-build/bootstrap-admin-tests.log` and `bootstrap-admin-build.log`; the earlier 34-case result remains historical |
| PowerShell and fixture contracts | PowerShell safety 12 and fixture HTTP protocol 131 passed | `artifacts/lumen-acceptance/20260930-071441-1922ba56/environment-summary.json`, `artifacts/lumen-acceptance/tool-build/fixture-harness.log`; test-only bytes were not decoded |
| Official server APIs | Official Emby `4.9.5.0` on `ssh test-env`, 21/21 endpoint checks with generated media and temporary accounts/data | `artifacts/lumen-official-validation/new-ui-api-summary.json`; native UI/decoder/first frame are `NotRun` |
| Scoped native Debug UI | Home dark/light, movie walls at 6/8/10 columns, descending sort, watched filter/list, series seasons/resume recommendation, search/person/back state, favorites, theme/accent/carousel settings | `artifacts/lumen-acceptance/app-debug-02/native`; screenshots/UIA are per-candidate observations, not a full acceptance matrix |
| AOT01 publication | Completed with exit 0; frozen executable 22,947,328 bytes, SHA-256 `BCDBA8AA3760A3564EDC2EC6FE7DD6AC31042A92C6C2E375CBDF13CF6DBCD89B` | `artifacts/lumen-publish-aot-first.log`; `artifacts/lumen-acceptance/aot-official-01/candidate` |
| Scoped native AOT01 | Real-server home/detail, transcoded 540p first frame/motion, pause/seek, pause-preserving French subtitle selection, 1.5x rate, Space/Escape, fullscreen hiding, compact-overlay entry/return | `artifacts/lumen-acceptance/aot-official-01/native`; observations are specific to AOT01, not the later source fixes |
| AOT01 caption and compact-overlay failures | Local WebVTT selected but expected four-second cue remained blank; normal-window minimum size distorted compact-overlay proportions | `native/failure-local-webvtt-empty-at-four-seconds.png` and `native/picture-in-picture-minimum-size-issue.png` under the AOT01 native directory; retain as failed attempts |
| AOT02 official-server repair observations | Publication completed with exit 0; candidate `aot-02-20AA3B3E` showed English/French cues, audio/subtitle selection preserving pause, size 38, delay +0.1, local WebVTT import/clear, stable rows over 12.3 seconds, and restored compact proportions | `artifacts/lumen-acceptance/aot-official-02/native`; physical sound and broad subtitle fidelity are not established |
| AOT03 rich-fixture observations | Candidate `aot-03-73395895`: persisted Light/Sage, search/settings layout, season drawer without changing playback, S2E5 resume at 31 seconds, natural S1E1-to-E2 and S2E5-to-E6 continuation, chapter preview, and intro skip | `artifacts/lumen-acceptance/aot-fixture-03/native`; controlled markers are not official-server intro-detection certification |
| AOT05 partial results and failure | Bright captions/localized AV headings and native administrator name-only metadata preservation/restore; loaded `Night` at 628x594 crashed with `LayoutCycleException` `0x802B0014`, `NotAccepted` | `artifacts/lumen-acceptance/aot-final-admin/native`, `aot-final-narrow/native`; safe metadata summary in `artifacts/lumen-official-validation` |
| AOT06/AOT07 boundaries | AOT06 loaded narrow/wide search and person history worked, but local Off eligibility and narrow chapter-label clipping failed; AOT07 was an intermediate publish without native acceptance | `artifacts/lumen-acceptance/aot-accepted-narrow/native`; these findings remain distinct from AOT08 |
| Native AOT08 fixture | Actual narrow/wide search/settings, quick-play S2E5 resume, file-picker import/immediate Off, paused seek/captions, chapter-preview clearance, PiP return, six-episode season drawer, and normal exit | `artifacts/lumen-acceptance/aot-final-fixture-08/native` and `app-exit.json`; same SHA as the published candidate |
| Native AOT08 official server | Same SHA; 1428x894 admin restore/search/detail/editor-prefill/cancel, real HLS video, pause-preserving English/French cues and track choices, stable selected rows for 187.297 seconds, normal close | `artifacts/lumen-acceptance/aot-final-official-08/native` and `app-exit.json`; not a physical-audio or universal subtitle pass |
| Local fixture cleanup | Owned fixture stopped, port released, evidence retained at 03:18:12 UTC | `artifacts/lumen-acceptance/20260930-071441-1922ba56/cleanup.json`; remote official cleanup is separate |
| Remote cleanup/final report | Completed; zero remaining owned active resources, evidence retained, final outcome `AcceptedForScopedDesignHandoff` | `artifacts/lumen-official-validation/cleanup-summary.json` and final `acceptance-summary.json`/`ACCEPTANCE.md`; local fixture and remote cleanup retain separate proofs |

The initial official-server 20/21 attempt retained a resume check without
`MediaTypes=Video`; correcting the request produced the 21/21 result rather than
weakening the assertion. Temporary mutation users/collections were removed, but
the official backend was retained at that protocol checkpoint and removed by
the final owned cleanup. The early combined unit-test
log includes a failing Platform attempt; the unified 919-case pass does not
rewrite that log. The initial administrator bootstrap failure remains retained
in `artifacts/lumen-acceptance/tool-build/bootstrap-admin-initial-failure.json`;
the later 42-case tool checks do not turn that attempt into a success.

AOT01 captures include `official-home.png`, `official-movie-detail.png`,
`official-transcoded-video.png`, `fullscreen-video-hud-hidden.png`, and
`playback-rate-one-point-five-paused.png`. Screenshot JSON identifies that
candidate but is not a standalone assertion of motion, keyboard input, physical
audio, or clean shutdown. The native session exited normally and diagnostics
showed `ResourceReleased` without cleanup errors. AOT02's PID 52336 also exited
normally. Final ownership cleanup/reporting completed with the separate proofs
below; launch ownership receipts are not exit logs. Do not replay recorded PIDs.

Post-AOT01 source fixes also preserve unchanged track rows/focus during periodic
reports, remove false `None` video-range badges, localize media group headings,
and align the new LAN default with Unlimited. Compact-overlay requests lower
the native minimum to 320x180 before changing presenter, then restore 640x600
for the ordinary window. AOT02 captured the repaired compact window at 628x354
and unchanged track-row identities/selections across at least 12.3 seconds;
successful AOT01 entry/return did not prove those corrections.

AOT02 captures include `official-english-subtitle-paused-four-seconds.png`,
`official-french-subtitle-paused.png`, `french-audio-preserves-subtitle-and-pause.png`,
`large-subtitle-style-applied.png`, `subtitle-delay-one-step.png`,
`local-webvtt-rendered-after-import.png`, `local-subtitle-off-clears-text.png`,
and `picture-in-picture-restored-aspect.png`. `track-identity-stability.json`
records the 12.3-second unchanged-row check. These establish the named plain-text
and selection behavior, not independently heard sound or all timing/style modes.

AOT03 captures include `home-light-persisted.png`,
`settings-playback-layout-repaired.png`, `search-layout-polish-confirmed.png`,
the `episode-drawer-*` captures, `natural-end-continued-to-episode-two.png`,
`server-marker-intro-skip-visible.png`, and `intro-skip-seeks-to-marker-end.png`.
Live observations additionally cover Sage persistence, the chapter preview, and
S2E5-to-E6 continuation; those are not independently asserted by a dedicated
screenshot file. Fixture intro markers define 3-10 seconds; the skip control
appeared near five seconds and the command reached 10.001 seconds. The capture
named `settings-narrow-window` still records 1428x894, so its name alone does not
certify a narrow viewport. All these observations belong to their named candidate.

## AOT08 native fixture

The published executable is `artifacts/lumen-aot-candidate-08/EmbyClient.App.exe`,
SHA-256 `D6732C40499306BB1CAA8168C773275BDE335DCF963E2C020B9FE62252515B2A`.
The build-input inventory's SHA-256 is
`85F21C6C944F80DB4C3E1B4AA365220550D62D7057287249F95B0F8E3A698E76`.
Its 302 entries cover 263 `src` files/assets, 36 licenses, and three root
toolchain/property files. Their unchanged comparison does not include docs,
tests, or tools, and the manifest does not itself assert native acceptance.

Sixteen screenshot/UIA groups in `artifacts/lumen-acceptance/aot-final-fixture-08/native`
identify `aot-08-D6732C40`. Actual 628x594 captures cover restored Home, loaded
`Night` with 39 results, the four-column grid, stacked settings, and six series.
Poster Play resolved S2E5: the first observed timeline was 31.21 seconds and the
explicit pause was 40.5599946 seconds, before natural completion. Separate
fixture resume reseeding is test setup, not native rendering evidence.

A normal file picker imported local WebVTT near 40.57 seconds. Off became enabled
immediately without reopening tracks. A pointer seek to 30 seconds retained
pause and a 52-character cue; the full chapter preview and its title remained
visible with caption clearance. PiP
captured 628x354 with a local cue, and return to 628x594 retained pause at 30
seconds. A 2560x1392 capture repeated wide chapter/caption clearance. Clicking
Off actually cleared caption/local row, disabled caption style controls, and
kept pause at 30 seconds.

Season 2's drawer loaded six episodes with current S2E5. The first attempted
click merely dismissed the AV panel; only the subsequent loaded drawer is the
pass. Return to series details showed the S2E5 resume hero. Older season-one
watched/progress data is persistent library state, not current live playback.
Relevant captures include `final-local-import-off-immediately-enabled.png`,
`final-narrow-caption-complete-chapter-title.png`,
`final-local-off-clears-caption-and-row.png`, and
`final-episode-drawer-loaded-season-two.png`.

`artifacts/lumen-acceptance/aot-final-fixture-08/app-exit.json` records normal UI
close, PID 30308 absent, zero new unhandled entries, and retained historical
AOT05 layout-cycle evidence. An initial receipt guard counted blank lines;
correcting its nonblank comparison was a tooling fix, not an app failure.
Fixture marker consumption does not certify official-server intro detection,
and these native captures do not establish independently heard physical audio.

## Retained intermediate results

AOT05 `54B0DE21` (23,091,200 bytes) published successfully, but its loaded narrow
search crashed and remains `NotAccepted`. Its administrator name-only metadata
summary records preserved tags `Case`, `Director, cut`, and `Unchanged; marker`,
IDs/genres, and restoration. Strict differences in `SortName`, `ForcedSortName`,
and `MediaSources` are retained and classified as server name derivation or
subtitle-preference projection; the refined 48-key comparison has no mismatch.
Prepared unknown tag fields numbered zero, so this is not a native unknown-field
sample. The safe receipt is
`artifacts/lumen-official-validation/metadata-name-only-ui-save-and-restore-summary.json`.

AOT06 `958F5623` (23,100,416 bytes) observed the loaded-search fix, real
Julian-to-search Back restoration, S2E5 resume, local cues, and chapter-image
clearance. It still had disabled local Off and a clipped narrow chapter label.
AOT07 was published, then rejected by static review for picker-eligibility timing
before native acceptance was attempted. The later dock/preview measurement slots
and post-picker Off refresh are AOT08 fixes; successful old scenes are not
relabeled as final proof.

## AOT08 official server

Twelve 1428x894 screenshot/UIA groups in
`artifacts/lumen-acceptance/aot-final-official-08/native` identify the same
`aot-08-D6732C40` executable. Cold administrator restore reached Home; exact
`Open Water` search returned one result and its details opened item `20`.
Reported media information was 540p H.264/59.04 MB with two AAC and two SRT
tracks, without a false `None` badge. More exposed seven actions with Edit
enabled. The editor prefilled the original tag `LumenSyntheticAcceptance` and
Adventure/Drama genres, then Cancel closed it without Save. This read/prefill
observation is separate from the earlier AOT05 name-only save/readback/restore.

Actual HLS transcoding showed a bright English cue at 4.902 seconds, changing
pattern/counter frames, and Pause at 15.8199582 seconds. Pointer seek reopened
the stream and completed paused at 4.0099792 seconds with a cue. The final
`final-official-english-caption-paused-four-seconds.png` records completed
reopening; the temporary loading capture under that reused label was not a pass.
Chinese Audio/Subtitles headings displayed two AAC options and three subtitle
options including Off, with text-style controls enabled. Selecting French AAC
index 2 preserved English subtitle index 3/cue and pause at 4.0199792 seconds;
selecting French SRT index 4 retained audio 2 and pause at 4.0299792 seconds with
`Film de test Lumen.` visible.

`final-track-identity-stability.json` marks the exact selected rows 670/677,
paused state, and timeline unchanged across 187.297 seconds. This is not an
independent count of HTTP progress reports or evidence of physically heard
sound. Safe parser observations reported three native/text/captured WebVTT cues,
`TimedTextCue` wrappers, and no exception/HRESULT failure. The final profile's
`app-exit.json` records normal UI close, PID 55124 absent at 03:37:41 UTC, zero
unhandled entries, and retained evidence. All final app instances were observed
absent; the old fixture-profile AOT05 exception was not erased.

## Completion and cleanup

The final 919-case product run, AOT08 publication/build-input comparison,
same-executable native fixture/official-server regressions, and owned cleanup
are complete. The final outcome is `AcceptedForScopedDesignHandoff`; both final
apps are absent and the local fixture port is released. The official cleanup
summary records completion at 03:47:01 UTC: one owned container, network, remote
relay, private run directory, and local SSH tunnel plus two auxiliary directories
removed/stopped, two loopback endpoints released, and zero owned active resources.
Shared official images and four preexisting containers remain preserved.

Cleanup evidence is `artifacts/lumen-official-validation/cleanup-summary.json`,
`remote-cleanup-proof.json`, and `local-tunnel-cleanup-proof.json`. Credentials
were not printed; no source/system configuration change or Docker prune occurred.
The consolidated final report and receipt retain every candidate's scope and
failure history. No additional source changes, builds, tests, UI checks,
installation, or deployment are required to complete this scoped task.

## Future certification

Full accessibility (high contrast, 200% text, Narrator, keyboard/focus), late
native responses/cancellation, large-library and long-session resources, broad
server/media/device support, physical output, signed installation/upgrades, and
clean-machine playback remain open release gates. No signing, installation,
deployment, or actual PR/issue write is implied by this checkpoint. These future
release gates are not blockers for completing the requested UI rewrite batch.
