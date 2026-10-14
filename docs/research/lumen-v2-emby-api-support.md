# Emby support audit for the Lumen V2 gaps

Research date: **2026-10-13**. This continues the [Lumen V2 implementation review](../implementation/lumen-v2-review.md#remaining-functional-gaps) and classifies all eleven gap groups. It does not change the earlier implementation or acceptance results.

The audit's code observations describe the source before the subsequent
[supported-gap implementation](../implementation/lumen-v2-supported-gaps.md).
That follow-up implements the documented integrations and client behavior and
tracks its own verification; it does not retroactively change this research or
the 2026-10-12 native acceptance receipts.

This remains the original static research record. Its pinned API baseline and
code observations before implementation are unchanged. Statements below about
unperformed local verification describe that research phase; historical test
counts and `NotRun` receipts retain their original source scope.

The subsequent [Windows continuation](../implementation/lumen-windows-continuation.md)
records the later authorization for local Windows compilation, tests, UI/UX
checks and computer use. That authorization was renewed after the temporary
pause. Fresh app-04 and app-05 runs used the same `publish-04` payload, with its
668 production inputs and 24 runtime files unchanged. The later App-only
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

The latest executed suite passed 2,250 product tests and 44 acceptance-tool
tests: 2,294 total, with zero failures or skips. Earlier 233 HTTP fixture checks
and a separate 31-check synthetic PGS scope total 264 checks across distinct
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
[saved pixel evidence](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-03/native-resume-20261014/),
records acceptance for the measured ASS/SSA fixture scope: the full static ASS
style and drawing layer, paused seek/move samples at 10 and 12 seconds,
white/100% and yellow/150% transform samples, karaoke word-color samples at 29
and 31 seconds, real playing clock advancement, LocalASS Replay restoration,
immediate paused refresh at delay 0/+0.1/-0.1 seconds, ASS/SSA Off clearing,
original SSA V4 import with two visible lines, natural Ended at `1:00 / 1:00`,
and Off retained on Replay. These samples do not
prove continuous interpolation across the whole animation. Capture 13's
misleading paused filename describes a Replay startup transition; it is not
pause or Ended acceptance.

The current `native-05` AudioProbe
[run-02 receipt](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md)
records four passed PCM policy/rejection cases, zero paused clock drift, a
paused seek to 8 seconds, resume clock advancement, and Stop/engine/listener
cleanup. Owned probe PID 17432 has an actual retained-handle exit code of 0 and
was subsequently absent. The BH900 PRO endpoint rejected AC3/EAC3 exclusive
formats with `FormatUnsupported`; AAC reported `CodecUnsupported`, and no
carrier frames were submitted. Successful receiver output, active-carrier
controls, bitstream-to-PCM replacement for software volume and physical HDR
remain unverified. The earlier `native-03` / `run01` observations retain their
own candidate attribution.

These verification scopes are separate from this API research. App-01's twelve
real BIF JPEG samples remain its historical result; its actual exit code remains
unknown. App-02's retained-handle actual exit code is 0. The fresh publish-04
[app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
now accepts sampled static ASS mapping at 2560 x 1440 fullscreen, restored wide
size and an ordinary 780 x 894 window, natural Ended at `1:00 / 1:00` with
Timeline `60.0106458`, twelve actual BIF JPEG cells at 0, 5, ..., 55 seconds,
and actual Home Play, Watch again and Continue S1:E3 tooltips. It also retains
the Copy feedback/Close visual overlap addressed by publish-05.

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
continuous ASS interpolation remain unestablished. The linked continuation
retains exact candidate identities and the remaining output limits.
The API classification and product exclusions below are unchanged: no
waveform/bitrate analysis, local media analysis or custom push relay is added,
and external IDX/SUB pair import is not requested in this round.

Most gaps are client implementation work, not missing Emby server APIs. Library alphabet queries, LAN discovery, online library events, resume data, remembered track preferences, and downloadable text subtitles already have official interfaces. Windows integration and media output require client work. The reviewed public contract does not provide ready-made audio waveform samples or a media bitrate time series.

The product scope was refined on 2026-10-13: media-data features require an
established official Emby data contract. Missing waveform or media-time bitrate
data will not be replaced by local decoding, packet analysis, custom analysis
services, or synthetic samples. Normal Windows behavior, playback presentation,
local preferences, and reading timestamps already returned in SRT/VTT remain
client responsibilities. Unknown push or native-output contracts remain deferred.

## Evidence and meaning of support

The versioned API baseline is the official [Emby SDK 4.10.1.0 definition](https://github.com/MediaBrowser/Emby.SDK/blob/1faf8176df7ede9152c38f27a5925e6ccaf5e12b/Resources/OpenApi/openapi_v2.json), last changed by that commit on 2026-09-29 and retrieved again on the research date. Its 433 paths and 338 definitions were inspected alongside official API guides, official client source, and current official product documentation. This is the schema version, not a claim that every target server is running that version or that every feature was introduced in it.

The older `Documentation/Download/openapi_v2_noversion.json` copy and the historical static API browser are not the version authority for this audit. Direct, read-only HTTP retrieval was used because the web search gateway was unavailable.

Three different claims must remain separate:

- **Official product support:** an identified Emby app or server feature is documented. Platform, version, device, and Premiere conditions still apply.
- **Public API or protocol support:** the server supplies the required data, negotiation, or events. The client still implements the resulting behavior.
- **No ready-made contract found:** the reviewed sources do not define the requested result. This is narrower than claiming that the feature is impossible or absent from every plugin, private interface, or future version.

No build, test suite, runtime probe, media playback, or request to a user Emby server was performed in this task. The code observations below are static inspection. Existing verification receipts remain attached to their original tasks and versions.

## Classification of the original eleven gap groups

| Original gap | Official Emby support | Public API boundary and remaining work |
| --- | --- | --- |
| **1. HDR and Dolby Vision output** | The current Windows app documents HDR display control. Official Windows evidence also confirms Dolby Vision client tone mapping; it does not establish native Dolby Vision output to the display. | `MediaStream` describes color, bit depth, HDR/DV type, and DV subtype. `PlaybackInfo` negotiates delivery. Rendering, tone mapping, display mode, and output compatibility belong to the player. **Primarily a client/media-engine gap; native DV output remains an unconfirmed capability.** See [display control](https://emby.media/support/articles/apps/windows/features/Win-Feature-DisplayControl.html) and the [official DV explanation](https://emby.media/community/topic/134736-dolby-vision-tonemapping/#findComment-1409183). |
| **2. Refresh-rate matching and passthrough** | The Windows app supports refresh-rate switching and audio passthrough with compatible devices. TrueHD support is platform/build/device dependent; the Android TV product explicitly advertises it on compatible devices. | Frame-rate and audio metadata plus delivery negotiation already exist. Switching display modes or opening an audio output in bitstream mode is client work. `AllowAudioStreamCopy` does not prove receiver passthrough. **Not an Emby API blocker.** See [Windows display control](https://emby.media/support/articles/apps/windows/features/Win-Feature-DisplayControl.html), [official Windows passthrough clarification](https://emby.media/community/topic/142246-no-audio-passthrough-on-new-glorius-app/#findComment-1467143), and [Android TV capabilities](https://emby.media/emby-for-android-tv.html). |
| **3. Advanced subtitle rendering** | Official subtitle support includes ASS/SSA and SUB/IDX; the Windows FAQ discusses styled ASS/SSA and bitmap DVDSub. This is not a guarantee of perfect reproduction of every ASS animation, font, or effect. | `DeviceProfile.SubtitleProfiles` and negotiated subtitle delivery support native delivery or server encoding as appropriate. The renderer must implement effects and bitmap presentation. Burned-in subtitles cannot be restyled locally. **A client renderer gap, with documented delivery and server fallback.** See [subtitle formats](https://emby.media/support/articles/Subtitles.html) and [Windows subtitle limitations](https://emby.media/support/articles/apps/windows/Emby-Windows-FAQ.html). |
| **4. Media-analysis timelines** | Text subtitle timing is available through official subtitle delivery. No ready-made waveform or per-media bitrate sample series was identified in the reviewed public contract. | Read and display cue timestamps already supplied in downloadable SRT/VTT. Waveforms and media-time bitrate curves are outside the product scope because the required official data is absent; do not add local media analysis or a custom service to fill that gap. Scalar server bitrate metadata remains usable. See [subtitle API](https://dev.emby.media/doc/restapi/Subtitles.html), [audio delivery](https://dev.emby.media/doc/restapi/Audio-Streaming.html), and [BitrateTest](https://dev.emby.media/reference/RestAPI/MediaInfoService/getPlaybackBitratetest.html). |
| **5. Windows product integrations** | Official Windows documentation describes startup via a Startup-folder shortcut. Emby also supplies library events and notification integrations. Complete JumpList behavior and a third-party Windows background push workflow were not established. | JumpList, activation, and startup registration are Windows client work. Online new-episode alerts can combine WebSocket library events with authenticated item queries. Push capability fields exist, but a complete third-party Windows push contract remains unconfirmed. **Split local integration from supported online events and conditional background push.** See [Windows FAQ](https://emby.media/support/articles/apps/windows/Emby-Windows-FAQ.html), [WebSocket guide](https://dev.emby.media/doc/restapi/Web-Socket.html), and [capability registration](https://dev.emby.media/reference/RestAPI/SessionsService/postSessionsCapabilitiesFull.html). |
| **6. Idle system-media resume** | The API supplies resume candidates and positions. No reviewed official Windows document promises the complete idle-SMTC or terminated-app activation behavior requested here. | `GET /Users/{UserId}/Items/Resume` and `PlaybackPositionTicks` supply content state. Keeping or recreating a Windows media session, routing Play, and preparing playback are client responsibilities. **Not an Emby API blocker; idle-app resume and cold activation are separate requirements.** See [resume query](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItemsResume.html) and [Windows SMTC integration](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/system-media-transport-controls). |
| **7. Offline and reconnect UX** | Official clients have connection/retry logic. Emby separately supports Downloads & Sync, subject to its feature and account requirements. | An offline banner, cached library presentation, retry policy, and refresh after reconnect are client state management. Actual media downloads have item-download and sync interfaces, but are broader than the original banner/reconnect gap. **Client UX work; offline media is an additional supported API workflow.** See [official connection client](https://github.com/MediaBrowser/Emby.ApiClient.Javascript/blob/master/connectionmanager.js), [authentication lifecycle](https://dev.emby.media/doc/restapi/User-Authentication.html), and [Downloads & Sync](https://emby.media/support/articles/Sync-Introduction.html). |
| **8. Cross-restart player persistence** | Emby supports playback position and remembered audio/subtitle preferences. General settings storage also exists. | Session volume, rate, and subtitle-offset fields do not promise automatic persistence of the full per-title player state. Local external subtitle files require local identity and file-access restoration. **Client persistence work, with standard server support for a subset and optional custom settings storage.** See [playback reports](https://dev.emby.media/doc/restapi/Playback-Check-ins.html), [user configuration](https://dev.emby.media/reference/RestAPI/UserService/postUsersByIdConfiguration.html), and [display preferences](https://dev.emby.media/reference/RestAPI/DisplayPreferencesService/getDisplaypreferencesById.html). |
| **9. Remaining settings actions** | Official Windows app updates use Microsoft Store. The reviewed documentation does not establish that every requested cache, sleep, and window switch exists in every official client. | Our cache quota, update download/install, and switches for existing sleep/window behavior belong to this application and its distribution channel. Emby server configuration and package APIs do not implement those behaviors for this third-party client. **Client settings and release work, not an Emby API blocker.** See [Windows FAQ](https://emby.media/support/articles/apps/windows/Emby-Windows-FAQ.html). |
| **10. Complete alphabet navigation** | Official user-item queries support server-side name ranges, sorting, and pagination. | `NameStartsWith`, `NameStartsWithOrGreater`, `NameLessThan`, `SortBy`, `SortOrder`, `StartIndex`, and `Limit` allow querying beyond loaded records. **API support is already present; the UI must request and navigate the appropriate range.** See [Items query parameters](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItems.html). |
| **11. Additional server management** | Official LAN discovery is documented. The official JavaScript connection manager also saves a validated server before requiring sign-in. | LAN discovery uses UDP, not REST. Saving an address/name independently from credentials is local persistence. **Documented discovery protocol plus client account-model work.** See [server discovery](https://dev.emby.media/doc/restapi/Locating-the-Server.html) and [official ConnectionManager](https://github.com/MediaBrowser/Emby.ApiClient.Javascript/blob/master/connectionmanager.js). |

## Important API details

### Media delivery does not implement media output

`POST /Items/{Id}/PlaybackInfo` accepts the device profile, source and track selection, and direct-play/transcoding preferences. The result describes available sources and delivery. `MediaStream` provides `AverageFrameRate`, `RealFrameRate`, `BitDepth`, color fields, `ExtendedVideoType`, and `ExtendedVideoSubType`. These are inputs to a client implementation, not operations that enable HDR or switch the Windows display. See [PlaybackInfo](https://dev.emby.media/reference/RestAPI/MediaInfoService/postItemsByIdPlaybackinfo.html) and [MediaStream](https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Entities.MediaStream.html).

The current type enum includes HDR10, HDR10+, HLG, and Dolby Vision; DV subtypes identify multiple profiles. Recognition of a DV source, tone mapping that source, and emitting native DV output are different claims. The official Windows [DV statement](https://emby.media/community/topic/134736-dolby-vision-tonemapping/#findComment-1409183) establishes client tone mapping, not all three.

Windows TrueHD behavior also needs a concrete build and output-device boundary. The official [TrueHD discussion](https://emby.media/community/topic/137382-emby-app-222220-for-windows-doesnt-passthrough-dolby-truehd-atmos/) includes exclusive-mode and speaker-layout requirements; a later [developer response](https://emby.media/community/topic/146009-windows-app-atmos-truehd-passthrough-audio-stuttering-and-interruption/#findComment-1517067) discusses a particular beta fix. Neither supports an unconditional claim that every Windows release and receiver combination works. The [official Windows product](https://emby.media/emby-for-windows.html) also has its own Premiere requirement for full playback; that is not an independent API switch for enabling HDR or TrueHD in this project.

### Subtitle format support, rendering, and timing

The [official subtitle guide](https://dev.emby.media/doc/restapi/Subtitles.html) documents conversion/download of text subtitles as SRT or VTT when `IsTextSubtitleStream` applies. The route is:

```text
GET /Videos/{Id}/{MediaSourceId}/Subtitles/{Index}/Stream.srt
GET /Videos/{Id}/{MediaSourceId}/Subtitles/{Index}/Stream.vtt
```

The versioned schema also lists the `/Items/...` variants. The [operation reference](https://dev.emby.media/reference/RestAPI/SubtitleService/getVideosByIdByMediasourceidSubtitlesByIndexStreamByFormat.html) includes `StartPositionTicks`, `EndPositionTicks`, and `CopyTimestamps`. An accurate visualization needs client parsing and a consistent original-media time base. It does not require a dedicated server waveform-style endpoint for subtitle cues.

For playback, use the actual negotiated `DeliveryMethod`, `DeliveryUrl`, and `DeliveryFormat`. `SubtitleDeliveryMethod` includes `Encode`, `Embed`, `External`, `Hls`, and `VideoSideData`. A free-form `{Format}` path parameter is not proof that arbitrary ASS or bitmap conversions are supported. Converting ASS to SRT/VTT also discards the basis for complete ASS effect fidelity. The SDK includes an attachment-stream route for media attachments, but font selection/loading and rendering remain client tasks.

Bitmap subtitles and already burned-in subtitles do not inherit the text-cue guarantee. The current public schema also does not establish an upload-and-restore workflow for a subtitle file selected only on this PC. Persisting a local file selection is separate from downloading subtitles already known to the server.

### Waveforms and bitrate curves are outside scope

The reviewed specification has no defined waveform/peak sample result or media-time bitrate sample collection. Existing `MediaStream.BitRate` and `MediaSourceInfo.Bitrate` are scalar metadata; transcode bitrate fields are also scalars. `GET /Playback/BitrateTest?Size=...` does not take an item or media-time range and does not define such a result. See the [bitrate test reference](https://dev.emby.media/reference/RestAPI/MediaInfoService/getPlaybackBitratetest.html) and [playback guidelines](https://dev.emby.media/doc/restapi/Playback-Guidelines.html).

This client will not derive waveforms from decoded audio, sample media packets
to create a bitrate history, or add a custom analysis service. Network throughput
and configured transcode targets are also unsuitable substitutes. Display only
the scalar values actually returned by Emby; omit the unsupported analysis charts.

### Online library events and background push are different integrations

The [official WebSocket guide](https://dev.emby.media/doc/restapi/Web-Socket.html), [official API client](https://github.com/MediaBrowser/Emby.ApiClient.Javascript/blob/master/apiclient.js), and [LibraryUpdateInfo model](https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Entities.LibraryUpdateInfo.html) provide evidence for `LibraryChanged` and added/updated/removed item IDs. The client can then fetch item details or [latest episodes](https://dev.emby.media/doc/restapi/Latest-Items.html), apply user-visible filtering, and show a notification. `NotificationAdded` is not itself proof that a new episode arrived.

`POST /Sessions/Capabilities/Full` accepts `PushToken`, `PushTokenType`, and `AppId`. It would therefore be incorrect to state that Emby has no push-related API. However, the reviewed [capability reference](https://dev.emby.media/reference/RestAPI/SessionsService/postSessionsCapabilitiesFull.html) does not establish a complete third-party Windows push integration, including accepted provider values, app credentials, or background delivery guarantees. That portion remains conditional research rather than a promised feature.

Official [notification integrations](https://emby.media/support/articles/Notifications.html) and [webhooks](https://emby.media/support/articles/Webhooks.html) are another route for server-originated notifications; webhooks have a Premiere requirement and require an actual receiving integration. They are not a substitute for implementing this application's activation and notification behavior. No event replay or exactly-once delivery guarantee is assumed.

### Standard resume state and custom player settings

The standard server-backed subset includes `UserItemDataDto.PlaybackPositionTicks`, the resume query, and `UserConfiguration.RememberAudioSelections` / `RememberSubtitleSelections`. Negotiated `DefaultAudioStreamIndex` and `DefaultSubtitleStreamIndex` must still be applied correctly. The official [playback preferences](https://raw.githubusercontent.com/EmbySupport/Emby.Docs/master/apps/common/User-Prefs-Playback.md) and [subtitle preferences](https://raw.githubusercontent.com/EmbySupport/Emby.Docs/master/apps/common/User-Prefs-Subtitles.md) describe the product behavior.

`PlaybackProgressInfo` and `PlayerStateInfo` contain `VolumeLevel`, `PlaybackRate`, and `SubtitleOffset`. Their appearance in session state does not establish durable per-title settings. The reviewed `UserItemDataDto` has no corresponding fields or `AudioDelay`; local external subtitle selections also lack a standardized per-title restoration model. The client must define what it saves, where it saves it, and how it restores it.

There is nevertheless official storage support:

| Storage surface | Established contract and boundary |
| --- | --- |
| `GET /DisplayPreferences/{Id}` and `POST /DisplayPreferences/{DisplayPreferencesId}` | `CustomPrefs` is a string-to-string dictionary, with user/client context. It can carry client-defined preferences. Namespacing, per-title scope, migration, and interpretation remain client work. See [DisplayPreferences](https://dev.emby.media/reference/RestAPI/DisplayPreferencesService/getDisplaypreferencesById.html). |
| `GET/POST /UserSettings/{UserId}` and `POST /UserSettings/{UserId}/Partial` | Present in the current schema. GET returns a dictionary, but generated POST documentation describes a string array and Partial describes an unspecified binary stream. Do not implement a guessed payload from those annotations. See the [service reference](https://dev.emby.media/reference/RestAPI/DisplayPreferencesService.html). |
| `GET/POST /Users/{UserId}/TypedSettings/{Key}` | Also present, but the generated response and payload schemas do not define a complete arbitrary-type storage contract. See [TypedSettings](https://dev.emby.media/reference/RestAPI/UserService/getUsersByUseridTypedsettingsByKey.html). |

The absence of a normalized player-restoration model is not the absence of all server settings storage. Local storage is sufficient for this PC's playback overrides and file-access restoration; cross-device preference sharing is a separate choice.

### Discovery, navigation, and offline boundaries

Official [LAN discovery](https://dev.emby.media/doc/restapi/Locating-the-Server.html) broadcasts the exact text `who is EmbyServer?` to UDP port `7359`, with `Address`, `Id`, and `Name` in the response. The absence of a REST discovery endpoint is irrelevant to this support claim.

The [user Items query](https://dev.emby.media/reference/RestAPI/ItemsService/getUsersByUseridItems.html) provides the name-range and pagination parameters needed for A-Z navigation. UI scroll position, letter availability, and rules for non-Latin titles remain client concerns. Queries must follow the server's sorting semantics rather than infer global contents from one loaded page.

The original offline gap is a cached-state banner and reconnect workflow, not a complete offline media system. If offline playback is added, [Downloads & Sync](https://emby.media/support/articles/Sync-Introduction.html) and [download options](https://emby.media/support/articles/Sync.html) provide an official workflow with Premiere and user-permission conditions. Relevant routes include `POST /Sync/Jobs`, `GET /Sync/JobItems/{Id}/File`, and [POST /Sync/OfflineActions](https://dev.emby.media/reference/RestAPI/SyncService/postSyncOfflineactions.html). Plain original-file download through `/Items/{Id}/Download` is a distinct operation; the documentation excludes ordinary browser file downloads from the Downloads & Sync feature definition.

Two compatibility discrepancies matter when this research becomes implementation:

- Generated `/System/Info/Public` annotations say authentication is required, while the official connection client uses it before sign-in. Follow the documented login workflow and confirm the actual target contract instead of relying solely on generated annotations.
- The older Sync tutorial refers to `User.Policy.EnableSync`; the reviewed 4.10.1.0 policy instead includes `EnableContentDownloading` and `EnableSyncTranscoding`. Do not copy the old field into a current permission check without server-specific evidence.

## Original audited code locations and proposed implementation scope

This table describes the original static source snapshot and its proposed next
steps. The linked implementation and Windows continuation records above govern
later source and verification status.

These observations locate the missing behavior without changing product code.

| Area | Original code evidence | Proposed next scope at the research checkpoint |
| --- | --- | --- |
| Alphabet navigation | [API query builder](../../src/EmbyClient.Api/EmbyApiClient.cs) already sends name-range parameters. [Wall controls](../../src/EmbyClient.App/Views/Lumen/LumenLibraryView.WallControls.cs) derive enabled letters and jump targets only from `ViewModel.Items`. | Wire server range queries and paging into navigation. |
| Media capability claims | [Playback DTOs](../../src/EmbyClient.Api/PlaybackModels.cs) carry metadata/negotiation. The [device profile](../../src/EmbyClient.Playback/ConservativeDeviceProfile.cs) advertises a limited AVC/AAC baseline and server subtitle encoding fallback. | Add actual renderer/output capability before widening the profile. Metadata badges alone do not change playback support. |
| Media analysis | [Media visualization](../../src/EmbyClient.App/Views/Lumen/LumenDetailView.MediaVisualization.cs) uses official metadata and now consumes negotiated text subtitle delivery. | Retain timestamps supplied in SRT/VTT. Exclude waveform and bitrate analysis; do not replace absent API results with locally derived data. |
| Connection management | [Public discovery](../../src/EmbyClient.App/Services/ConnectionService.PublicDiscovery.cs) queries a manually supplied address and public users; it is not LAN broadcast discovery. | Add UDP discovery and a saved-server model independent from authenticated accounts. |
| Playback persistence | [Lumen preferences](../../src/EmbyClient.App/Services/LumenPreferences.cs) and [preference storage](../../src/EmbyClient.App/Services/LumenPreferenceStore.cs) do not model the complete per-title volume/rate/delay/local-subtitle restoration requirement. | Define and persist that client state; reuse standard server resume/selection data where appropriate. |
| Windows controls | [Media controls](../../src/EmbyClient.App/Playback/NativePlaybackEngine.MediaControls.cs) operate on active playback. [Display requests](../../src/EmbyClient.App/Services/PlaybackDisplayRequest.cs) and [window placement](../../src/EmbyClient.App/Services/WindowPlacementStore.cs) already exist. | Implement idle resume/activation and expose the missing switches. JumpList and startup registration remain Windows integration work. |
| Cache and updates | [ImageCache](../../src/EmbyClient.App/Services/ImageCache.cs) already caps memory at 32 MiB and 128 entries. [Settings](../../src/EmbyClient.App/Views/Lumen/LumenSettingsView.cs) expose cache clearing and a GitHub version check. | Add configurable budgets and an application-owned download/install workflow. The gap is not an unlimited existing cache or an absent version check. |

`ServerConfiguration.RunAtStartup`, `CachePath`, and `DatabaseCacheSizeMB` refer to Emby Server. They do not configure this client. The package target enum includes legacy app targets as well as server targets, so it is also inaccurate to call every `/Packages` route server-only; nevertheless, those routes do not supply an installer and process-replacement contract for this third-party GitHub application.

The implementation scope is documented API/protocol integration, normal Windows
behavior, local persistence, and playback presentation supported by the actual
player interfaces. Missing media-analysis datasets are excluded. Background
third-party push and native Dolby Vision output remain deferred until their
contracts are established. Unsupported audio/subtitle presentation should use
documented negotiated server fallback; do not force unsupported capabilities
through an invented data source, OCR, renderer, or relay service.
