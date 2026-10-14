# Updated Lumen V2 API data and service limits

Research date: **2026-10-13**. This is a static review of the updated external
handoff at `D:\Code\design_handoff_lumen_windows_client`, its complete
`README.md` and `lumen-data.js`, the relevant templates and view-model methods
in `Lumen 原型 V2.dc.html`, the [capability contract](../api/06-lumen-capabilities.md),
the [earlier API audit](lumen-v2-emby-api-support.md), and the
[supported-gap implementation](../implementation/lumen-v2-supported-gaps.md).
The handoff line numbers below refer to this reviewed copy.

The official schema baseline remains [Emby SDK commit 1faf8176, OpenAPI
v2](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v2.json):
`info.version=4.10.1.0`, 433 paths and 338 definitions. The browser gateway was
unavailable, so the cited public official pages and raw official source were
retrieved using read-only HTTP. No tests, builds, runtime probes, playback,
desktop interaction, or requests to a user's Emby server were performed.
This document adds no target-server or Windows acceptance claim.

This remains the original static research record. The pinned API baseline and
the six boundary classifications below are unchanged. Statements above about
unperformed local verification apply to that research phase. Historical counts
and `NotRun` results remain attached to their original source snapshots.

The separately authorized [Windows continuation](../implementation/lumen-windows-continuation.md)
records the later local Windows compilation, tests, UI/UX checks and computer
use. That authorization was renewed after the temporary pause. Fresh app-04 and
app-05 runs used the same `publish-04` payload, with its 668 production inputs
and 24 runtime files unchanged. The later App-only
[publish-05 verification](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
records actual exit 0 for ordinary Debug/AOT compilation and package/SBOM
readback. Exactly one production file changed, routing two Copy feedback calls
to `ShowPlayerToast`; all 24 native runtime files and native/ASS receipts remain
identical. The [app-06 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts bottom Copy success feedback clear of Close in the ordinary 780 x 894
window, later feedback disappearance and panel Close, a complete PGS first cue
during `1x` playback and Back to the detail page. This targeted feedback/basic
PGS check does not rerun the publish-04 ASS/PGS lifecycle matrix. Its normal
title-bar Close has an [actual same-handle exit 0](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/app-exit-final.json).
Clipboard contents, failure feedback and Close during feedback remain unobserved.

The [final non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
records complete app-06/publish-05 payload readbacks and a fresh manifest of
668 production inputs and 24 native runtime files matching the publish-05
build freeze. The initial static-SVG privacy-guard refusal remains with the
later complete readback.
[Final owned cleanup](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical fixture Stop command exits 0, absent owned processes and
released ports. The outer collector naturally exited 1 after inherited-pipe
closure and its deferred wrapper guard, separate from the original launcher
exit 0; no collector stop signal was sent. The scoped local continuation is
complete, with the output limitations below retained.

Owned PID 7464 still matched at 02:28 UTC on 2026-10-14; read-only observations
at 02:33 and 02:35 found it absent. The
[app-03 exit receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/app-exit.json)
retains an unobserved actual exit code (`null`) and `Unknown` exit cause after
the earlier retained monitor returned 258 (timeout). Normal UI closure was not
observed. The [bounded session Stop audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/session-stop-audit-20261014.json)
and [zero-match exact-path event 1000/1026 audit](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/crash-audit-20261014.json)
do not establish exit code 0. The
[owned cleanup receipt](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.json)
and [execution receipts](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-20261014.execution.json)
record guarded CLI cleanup of both fixtures after `ValidateOnly`, with actual
Stop command exit codes of 0. Fixture PIDs 29348/45576 are absent and ports
18985/18986 were released at that historical checkpoint. Source, inputs, media
and synthetic profiles were retained. Later fresh runs have separate ownership
and cleanup records; this receipt does not describe their resource state.

Its latest executed suite passed 2,250 product tests and 44 acceptance-tool
tests: 2,294 total, with zero failures or skips. Earlier 233 HTTP fixture checks
and the separate 31-check synthetic PGS scope total 264 checks across distinct
frozen execution scopes, not one rerun on a single binary. HTTP checks remain
separate from unit tests. The earlier 16-file PowerShell Parser result retains
its original scope. The `native-05` build, normal Debug build with generated
XAML, `publish-04` Native AOT, packaging and SBOM generation/readback have actual
exit-code-0 receipts. Historical AOT03 build exit remains `Unknown`.
These unit, HTTP, native and audio results retain their earlier frozen-source
scope; they were not rerun for publish-05's two-call feedback change. Its first
post-build driver exit 1 is retained alongside the later exit-0 completion of
the unstarted verification commands, without repeating the builds.

The unrelated security prompt was manually cleared. The
[2026-10-14 app-03 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/UI-REVIEW-resume-20261014.md), based on
[saved pixels](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/),
records acceptance for the measured ASS/SSA fixture scope: full static ASS
styles/drawing, paused seek/move samples at 10 and 12 seconds, white/100% and
yellow/150% transform samples, karaoke word-color samples at 29 and 31 seconds,
actual playing-clock advancement, LocalASS Replay restoration, immediate paused
delay refresh at 0/+0.1/-0.1 seconds, ASS/SSA Off clearing, original SSA V4 import
with two visible lines, natural Ended at `1:00 / 1:00`, and Off retained on
Replay. The samples do not prove
continuous interpolation across the whole animation. Capture 13 is a Replay
startup transition despite its paused filename, not pause or Ended proof.

The current `native-05` AudioProbe
[run-02 receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md)
records four passed PCM policy/rejection cases, zero paused clock drift, paused
seek to 8 seconds, resume clock advancement, and Stop/engine/listener cleanup.
Owned probe PID 17432 has an actual retained-handle exit code of 0 and was
subsequently absent. BH900 PRO rejected AC3/EAC3 exclusive formats with
`FormatUnsupported`; AAC reported `CodecUnsupported`, with no carrier frames
submitted. Successful receiver output, active-carrier controls,
bitstream-to-PCM replacement for software volume and physical HDR remain
unverified. The earlier `native-03` / `run01` results retain their separate
candidate attribution.

App-01's twelve real BIF JPEG previews remain historical evidence and its actual
exit code remains unknown; app-02 has a retained-handle actual exit code of 0.
The fresh publish-04
[app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
now accepts sampled static ASS mapping at 2560 x 1440 fullscreen, restored wide
size and an ordinary 780 x 894 window, natural Ended at `1:00 / 1:00` with
Timeline `60.0106458`, twelve actual BIF JPEG cells at 0, 5, ..., 55 seconds,
and actual Home Play, Watch again and Continue S1:E3 tooltips. It retains the
Copy feedback/Close visual overlap addressed by publish-05.

The separate [app-05 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
accepts both embedded PGS cues, first-cue absence by 18.7904166 seconds, Off
clearing, seek-before-cue recovery, narrow/fullscreen/restored mapping and Back
clearing. Track reselect at the
paused mid-cue position did not alone restore the earlier bitmap. The
wide/fullscreen HUD crosses some lower glyph pixels; narrow/restored samples
show the complete second cue. Both fresh runs have
normal title-bar Close actions and actual retained-handle exit 0:
[app-04](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json),
[app-05](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/app-exit-final.json).
The [app-04](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app04-final-non-ui-20261014/APP04-NON-UI-AUDIT.md)
and [app-05 non-UI audits](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app05-final-non-ui-20261014/APP05-NON-UI-AUDIT.md)
each retain bounded event/log/session observations and all 595 copied payload
files matching. Complete PiP prototype parity, other SAR values and complete
continuous ASS interpolation remain unestablished. See the continuation for
exact candidate identities and remaining acceptance. These client results do
not add an Emby data or service contract or alter this research's exclusions:
no waveform/bitrate analysis,
local media analysis or custom push relay is added, and external IDX/SUB pair
import is not requested in this continuation.

## Behaviors without an established required data or service contract

| Requested result | Official evidence and precise boundary | Updated handoff treatment |
| --- | --- | --- |
| Real audio waveform or peak samples across the media timeline | The reviewed public schema does not define a waveform/peak sample collection. Audio delivery supplies media, while [MediaStream](https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Entities.MediaStream.html) supplies scalar metadata. `SampleRate` is not an amplitude series. | Excluded by README 27 and 199. Main HTML 280 now renders a continuous flat audio bar. It must not be presented as measured amplitude. |
| Media-time bitrate curve, instantaneous media bitrate, or peak bitrate | `MediaStream.BitRate`, `MediaSourceInfo.Bitrate`, and session/transcode bitrate fields are scalars. [BitrateTest](https://dev.emby.media/reference/RestAPI/MediaInfoService/getPlaybackBitratetest.html) accepts a test size, not an item or media-time range. It does not establish a media bitrate sample series. | Excluded by README 28 and 235. Main HTML 1602 displays a media-information value or transcode target. Network throughput and requested encoder budgets are not replacement media-time samples. |
| Ready-made PGS/VobSub cue timeline or OCR reconstruction of bitmap/burned text | The [subtitle guide](https://dev.emby.media/doc/restapi/Subtitles.html) limits its SRT/VTT download guarantee to `IsTextSubtitleStream=true`. The reviewed schema has no normalized bitmap cue-list result. This does not prove that delivered bitmap formats lack timestamps or that Emby cannot deliver bitmap subtitles. | Excluded by README 29 and 200; main HTML 281 and 1468 show the striped unavailable lane. Do not add OCR, media packet analysis, fabricated intervals, or a custom analysis service. Bitmap playback remains a separate renderer/delivery requirement. |
| Independent font, outline, position, or local delay edits to subtitles already burned into the current video | [SubtitleDeliveryMethod](https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.html) defines `Encode` as burn-in. The conclusion that the current video has no independent text layer to restyle is an inference from that delivery representation, not the absence of an ordinary styling REST endpoint. A new negotiation may select another delivery, but that is a different operation. | Excluded by README 30, 216, 232 and 284. Detect actual `DeliveryMethod=Encode`, including encoded text subtitles; do not infer burn-in from codec and video conversion alone. |
| Disconnection event replay, no lost notifications, or exactly-once notifications | The [WebSocket guide](https://dev.emby.media/doc/restapi/Web-Socket.html) establishes online events, but no recovery cursor, acknowledged replay, or exactly-once promise was identified. The [official JavaScript client](https://github.com/MediaBrowser/Emby.ApiClient.Javascript/blob/master/apiclient.js) does perform in-memory `MessageId` deduplication; deduplication is not durable event history. | The guarantee is removed by README 31 and 326. Reconnect refreshes current server lists and does not synthesize missed alerts. Online `LibraryChanged` processing remains supported. |
| New-episode delivery after Lumen has fully exited | [Capability registration](https://dev.emby.media/reference/RestAPI/SessionsService/postSessionsCapabilitiesFull.html) includes `PushToken`, `PushTokenType`, and `AppId`. These fields do not establish a complete third-party Windows provider registration, credentials, or terminated-application delivery contract. It would be inaccurate to say that Emby has no push-related API. | Deferred by README 32, 66 and 327. Alerts require the active application's online connection; no custom push relay is introduced. Windows notification display and activation remain client work. |

The exclusion of waveform and bitrate datasets is deliberate. This client will
not locally decode audio, analyze media packets, generate synthetic samples,
or introduce an analysis service to replace absent official media facts.

`UserNotificationInfo.EventIds` in the schema belongs to notification/webhook
configuration, not a WebSocket replay cursor. See the [pinned definition](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v2.json#L83286).
The official JavaScript source retrieved on the research date declares the
in-memory dictionary at line 248, reads `msg.MessageId` at 252, checks/stores it
at 256/260, and handles `LibraryChanged` at 276. No absence of `MessageId` is
claimed here.

## Supported data with conditional availability

These are not additional API exclusions. They require the real returned data
and cannot be fulfilled by copying prototype constants.

| Design surface | Established contract | Remaining boundary |
| --- | --- | --- |
| SRT/VTT/ASS text timeline, README 36, 199 and 370 | The [subtitle stream operation](https://dev.emby.media/reference/RestAPI/SubtitleService/getVideosByIdByMediasourceidSubtitlesByIndexStreamByFormat.html) and text-subtitle guide support real delivered text timestamps. | An ASS source can contribute cues through an actual server-provided SRT/VTT conversion. A free-form `{Format}` parameter is not a promise of native ASS download for every server/source. SRT/VTT conversion does not establish complete ASS style/effect fidelity. Accept usable delivery with a known original-media time base; otherwise keep the lane unavailable. The random blocks at main HTML 1460/1468 are demonstration data. |
| Video timeline thumbnails, README 199; main HTML 279/1466 | Real chapter previews use advertised `ChapterInfo.ImageTag` and `ChapterIndex` through [indexed images](https://dev.emby.media/doc/restapi/Images.html). The current SDK also defines [ThumbnailSet](https://dev.emby.media/reference/RestAPI/BifService/getItemsByIdThumbnailset.html), whose entries contain `PositionTicks` and `ImageTag`, and [BIF delivery](https://dev.emby.media/reference/RestAPI/BifService/getVideosByIdIndexBif.html). `ImageType` includes `Thumbnail`. | It is incorrect to claim that Emby has no thumbnail-timeline API. These interfaces do not guarantee that every item has generated images, exactly 12 images, complete coverage, or an image at every arbitrary seek position. Preserve actual sample positions and availability. The generated operation does not specify the BIF response's binary layout; at this research checkpoint its delivery/index contract still needed validation. The [Windows continuation](../implementation/lumen-windows-continuation.md) subsequently integrated a bounded BIF reader and observed twelve fixture-delivered JPEG previews. Do not create missing frames or pretend a chapter image is the exact hovered frame. |
| Intro skip and credits-time next-episode card, README 236-237/374 | `ChapterInfo.MarkerType` includes `IntroStart`, `IntroEnd`, and `CreditsStart`; intro detection has [server/library/Premiere requirements](https://emby.media/support/articles/Intro-Skip.html). | Pair real intro markers. The schema has no `CreditsEnd` or chapter `EndPositionTicks`. Missing markers do not authorize fabricated 20-110-second intros, chapter-name inference, or a claim that `duration - 32` is the real credits start. A playback-end countdown is a separate explicit client policy. Main HTML 955/957/1578 and data module 144-147 are demo assumptions. |
| Dolby Vision P5 conversion and alternate 1080p direct version, README 72/238-241 | [PlaybackInfo](https://dev.emby.media/reference/RestAPI/MediaInfoService/postItemsByIdPlaybackinfo.html) describes real sources, compatibility, conversion URLs, and error codes. | Describe only genuine engine capabilities and use the returned delivery. Omitting a fictional Dolby Vision flag is not itself a universal conversion guarantee. Permission is not the only failure: `NoCompatibleStream`, `RateLimitExceeded`, missing media, transport, or encoder failure can also prevent playback. An alternate 1080p source and direct eligibility must actually exist; main HTML 1012's fixed source index is not a contract. |
| Trailers, README 193 | Local trailer items have the existing operation; remote trailers are item metadata, as described by [item information](https://dev.emby.media/doc/restapi/Item-Information.html#localtrailercount-remotetrailers). | Hide unavailable local playback choices. A provider webpage URL is not a negotiated media stream. The main HTML's synthetic trailer duration/playback is not evidence that every title has a playable trailer. |
| Search best match and match explanation, README 212; main HTML 1507-1520 | User item and person queries support search. The [capability contract](../api/06-lumen-capabilities.md#discovery-additions) records the absence of a current `/Search/Hints` route or matched-field provenance in `BaseItemDto`. | A client may rank actual returned results or highlight text it can demonstrate. Do not label a prototype heuristic as a server relevance score, invent a matched field, or promise the same full-library title/original-title/genre ranking from one returned page. |
| Technical badges, file size, titles, and people metadata | Item/source/stream fields and real tags can supply these facts; they are optional. | `OriginalTitle` is not guaranteed to be an English translation. A 4K resolution does not imply HDR10, Atmos, or IMAX. Do not infer these badges from `it.q` as main HTML 811 does, derive file size from a fixed demo bitrate as data module 128 does, or generate an absent biography as main HTML 1491 does. Omit missing facts or use a neutral unavailable state. |

For series, technical media facts belong to an actual episode and selected
source; a series container is not a generic 48-minute media file. The prototype
uses that convention at `lumen-data.js:127`. Real season/episode counts and
order likewise come from queries rather than `seasonCount * identicalEpisodeCount`.
These are ordinary data binding responsibilities, not absent Emby interfaces.

## Player and Windows requirements are separate

README 36 deliberately keeps HDR10, refresh-rate matching, audio passthrough,
ASS/SSA and VobSub rendering in scope. Native limitations recorded by the
original supported-gap snapshot describe that implementation checkpoint. Later
source and verification status belong to the Windows continuation. These
requirements are client implementation and acceptance work rather than missing
Emby APIs.

| Area | Correct classification |
| --- | --- |
| HDR10 output, HDR-to-SDR tone mapping, refresh-rate switching, passthrough | Client renderer, audio endpoint, display and hardware work. Emby supplies metadata and negotiation. The [official Windows display-control feature](https://emby.media/support/articles/apps/windows/features/Win-Feature-DisplayControl.html) demonstrates product/platform behavior, not an API that enables it in this client. |
| Native Dolby Vision display output | Deferred by README 33. Source recognition and conversion have API support; native output depends on the playback engine, OS/display path and hardware. Do not classify this as missing media data or promote a source badge/tone mapping to native Dolby Vision output. |
| ASS/SSA effects, attachments/fonts, native PGS/VobSub presentation and bitmap delay | Renderer capability and negotiated delivery. [Official subtitle formats](https://emby.media/support/articles/Subtitles.html) include ASS/SSA and SUB/IDX. A text conversion fallback can preserve cues without proving native rendering fidelity. No new renderer is invented to fill a missing data contract. |
| Decoder/device detection, dropped frames, buffer duration and current audio output | Facts observed through actual player/Windows interfaces. `TranscodingInfo` describes server conversion; it cannot establish the local GPU decoder or receiver output. Main HTML 1538/1602 contains demo diagnostics. |
| SMTC, idle resume, JumpList, title bar, startup, notification layout/buttons/activation | Windows/client integrations using supported platform interfaces and normal Emby resume/item data. These are distinct from application-closed server push. Exact simulated system timings and geometry are not Emby data promises. |
| Cache budgets, update installation, local volume/rate/delay and local-subtitle persistence | Application-owned behavior. Local subtitle loading does not imply uploading or restoring that file on another device. |

Downloads/Sync and server custom preference storage are outside this follow-up's
chosen product scope, **not** missing-API entries. The official download/settings
interfaces recorded by the earlier audit remain acknowledged. Standard Emby
resume position and remembered track choices remain usable.

## Handoff consistency and product decisions

README 482 says that the main prototype is the visual authority. That does not
turn synthetic media values or its simplified state flags into production
contracts. Apply README 23's official-data and supported-interface boundary.

| Exact source locations | Difference or decision | Implication |
| --- | --- | --- |
| README 30/216/232/284; main HTML 1009/1555/1591 | The README excludes edits for burned subtitles generally, but the demo recognizes burn-in only for PGS/VobSub during conversion. Position/outline at 1577 also use shared controls. | The production state must use actual delivery, independently gate styling/delay, and cover encoded text subtitles. Conversion alone also does not prove burn-in. |
| README 36/199/370 | SRT/VTT/ASS wording is broader than the explicitly documented SRT/VTT conversion workflow. | Preserve ASS-source cues through confirmed text delivery; do not silently require an unestablished native ASS download contract. |
| README 199; main HTML 1466 | Fixed 12 thumbnails versus conditional server chapter/thumbnail availability. | Treat 12 as a visual maximum/layout choice, not fabricated samples or a guaranteed dataset. Expanding from existing chapter images to ThumbnailSet/BIF was a separate compatibility choice at this static checkpoint; the later continuation implements BIF delivery without changing the item-specific availability boundary. |
| README 324; main HTML 1207; `LumenShellPage.ServerEvents.cs` and `EpisodeNotificationEligibility.cs` | The README requests notifications only for followed/watched-in-progress or favorite series. The prototype cycles all new series; the implementation now requires actual favorite or resume/played-history evidence. NextUp alone does not prove that a series has been started. | This client behavior gap is addressed with ordinary favorite and continuation data. There is no need to invent a follow-service endpoint. |
| README 246; main HTML 1114/1122; README 36 | The accepted drag/drop suffix list contains `.sub`, but its prompt omits it, and a two-file VobSub `.idx` selection workflow is unspecified. | Decide the actual native-format boundary and the paired-file UX before promising external VobSub. This is renderer/file handling, not a server API gap. |
| README 140/145; main HTML 773/1362/1537 | The README and settings description say 7 seconds; the rotation and progress animation use 8 seconds. | Choose one timing from the canonical visual reference and align the text. This is a local presentation detail. |
| README 195/250; main HTML 862-865/1634-1635 | The README first removes a separate full media-information dialog, then still lists it among dialogs. The prototype navigates to the media-information stage and has no such dialog type. | Follow the main stage navigation and correct the stale dialog enumeration. |
| README 72/240-241; main HTML 920/1012 | The demo assumes P5 conversion succeeds except for a guest account and that source index 1 is a direct 1080p alternative. | Reflect actual server errors and available negotiated alternatives; these are conditional source choices rather than guarantees. |

The still-in-scope native output and subtitle rendering requirements remain
explicit throughout implementation and acceptance. Later source paths are
recorded by the continuation; source presence alone does not establish output
fidelity. If an engine change
or a wider native format commitment is required, that is a product/implementation
scope decision, while unsupported media keeps documented server fallback or an
explicit unavailable state.

The six boundaries above are three missing normalized datasets, one delivery
representation constraint, and two guarantees or service contracts that have
not been established. They must not all be summarized as nonexistent Emby
APIs. In particular, renegotiating a burned subtitle's delivery remains possible,
and online events, message deduplication and push-registration fields remain
acknowledged.
