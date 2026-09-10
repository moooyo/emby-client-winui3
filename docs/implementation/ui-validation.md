# Native UI validation record

Date: 2026-09-09. These checks used the actual Windows Native AOT application and the isolated official Emby 4.9.5.0 environment. All media and accounts were created for this task. UI actions used the native application's controls and its observable accessibility tree; API probes were not substituted for UI playback.

The inspected application executable at this checkpoint had SHA-256 `645917E6590E6D090F4F21047BC1BA3F2D00F840BCB7F2B22A6E5D6C08364BC1`. It preceded the final HLS timeline correction and the native HTTP relay integration. This is a development checkpoint, not a release acceptance certificate.

## Observed behavior

| Flow | Observed result |
| --- | --- |
| Sign-in | The dedicated server account signed in and loaded both movie and TV libraries. Password text was masked and cleared after connection. |
| Saved account | Remembering the dedicated test sign-in, switching accounts, and restoring without entering a password returned to the correct server/user library. |
| Favorites | Adding Track Validation changed the detail action to Remove favorite; the Favorites view returned the item. Removing it restored Add favorite. |
| Watched state | Mark played and Mark unplayed changed the real server-backed detail state. Short-video progress rules can mark generated clips played after actual playback. |
| Search | Entering Track returned the matching movie. |
| Series navigation | Official Validation TV opened Validation Series, Season 1, and two ordered episode entries with the correct episode numbers. |
| Direct playback and seek | S01E01 displayed changing video frames. Seeking near 57 seconds displayed the corresponding purple portion of the generated sample. |
| Automatic episode continuation | Natural completion of S01E01 advanced to S01E02 without another play action. The new title, an advancing position, and actual blue video frames were visible. |
| Return to library | Returning from playback stopped it and refreshed the original library context. |
| Track and version controls | The movie exposed 720p/480p versions and English/French audio tracks. Selecting French caused a new server conversion and restored the paused state. Timeline accuracy for this transition failed as described below. |

An earlier Native AOT checkpoint also displayed original movie playback and visible SRT text burned into the official server's HLS output. Fullscreen entry and Escape exit were inspected while video was playing. The screenshot of automatic episode continuation is retained in ignored `artifacts/ui-validation/2026-09-09/automatic-next-episode.png`.

## Defects found by real playback

1. The native player can report Paused before its first actual start because AutoPlay is disabled during initialization. If direct opening then failed, the coordinator initially treated this as user intent and paused the fallback conversion. The coordinator now requires actual earlier playback before inheriting that state. Explicit paused-restart intent remains authoritative. Regression tests and the later 0251D189 interactive checkpoint verify the corrected new-play behavior.
2. On paused French-audio switching at approximately 17.6 seconds, the UI kept its displayed position but the video returned to the blue start of the sample. The server received StartTimeTicks and the correct audio/source indexes, yet returned a complete HLS VOD playlist beginning with segment zero. It did not crop the output at that requested time. Treating engine zero as source 17.6 was therefore incorrect. The correction performs a real initial seek on the complete HLS timeline; the later 0251D189 checkpoint verifies corresponding source pixels.

The direct-play lifetime probe also failed its resource gate at the initial checkpoint. Passing those UI flows did not override that failure. Subsequent complete product lifecycle receipts are tracked separately in [implementation status](status.md).

## Later interactive checkpoint: 0251D189

The user explicitly resumed desktop validation. The actual AOT executable with SHA-256 `0251D1893FBC1995B7915E4B44D292B6D08CF401509196D91A70353A473992AE` passed these additional interactive checks against the same isolated server:

- Saved sign-in restored the dedicated account and loaded native library navigation and search.
- New multitrack playback reached Playing through server conversion, without inheriting the native initialization pause.
- A paused HLS seek from approximately 39 seconds to 17 seconds changed the displayed source frame from orange to red and remained Paused at 17 seconds.
- French-to-English and English-to-French changes each reopened at 17 seconds, retained pause, and displayed the red source frame.
- Switching the selected version from 720p to 480p retained the 17-second paused red frame. The server selected French audio; this check does not claim that the file's default-language label overrides Emby's user-specific stream selection.
- The selected English SRT was visibly burned into HLS at 41 seconds, including the generated cue "Seek and subtitle delivery check." over the expected orange frame.
- Moving the window changed the tool-reported origin from `(494, 215)` to `(269, 215)`. Closing and relaunching restored `(269, 215)` with the same `1268 x 834` captured dimensions. The original origin was restored afterward. These are tool-reported geometry values, not a multi-monitor/DPI certification.

The generated video has five equal 12-second color sections: blue, red, green, orange, and purple. Expected frame colors come from the fixture generator, not the UI's displayed time alone. Screenshots remain in ignored `artifacts/ui-validation/2026-09-09/` with the `0251-` prefix. Both applications used for the placement round trip were closed through their native close control; no root UI playback remains active after this checkpoint.

This executable predates the final shared-player owner, explicit retry UI, editable queue, and diagnostics UI. It establishes the listed behavior at this exact checkpoint.

## Shared-owner, queue, and diagnostics checkpoint: C6E8E17B

Executable SHA-256: `C6E8E17BA130BB4D10A32831F11DB617F26D8F6283D97A9DE19784E25FB2483C`. This publish includes the final shared native owner, upstream relay error classification, editable queue with delayed consumption, and the diagnostics/retry UI. It predates the subsequent recovery-selection correction that preserves default versus explicit track intent.

| Flow | Observed result |
| --- | --- |
| Saved sign-in | The dedicated official-server account restored and loaded its movie library and search. |
| Queue editing | Fixture and Track Validation appeared as two separate entries. Moving the first entry down and back up changed the visible order and retained selection. Boundary move buttons disabled correctly. |
| Queue keyboard and empty state | Clicking the list and pressing Delete removed the selected Fixture entry. Clear queue removed the remaining entry and displayed the empty-state message with all edit buttons disabled. Escape closed the dialog. The tool's focused-element field remained stale for this modal, so the actual list/count change establishes Delete behavior. |
| Automatic queue consumption | Track Validation was queued, then Fixture played through DirectStream. Seeking to approximately 57 seconds showed the purple source section. Natural completion opened Track Validation through HLS with advancing time and a blue frame; the queue became empty and Next disabled. |
| Paused HLS seek | Track Validation was paused, then sought to 17 seconds. After reopening completed, the UI was Paused at 17 seconds and displayed the expected red source frame. |
| Audio and version changes | French-to-English audio selection retained Paused, 17 seconds, and the red frame. Changing 720p to 480p again retained pause/time and the corresponding red frame. This run showed English selected after the version change; no general server-default-language rule is inferred. |
| Native diagnostics dialog | The dark native dialog displayed application/runtime/Windows baseline versions, 34 retained events, source categories, and copy/save/refresh controls. Copy reported success with history/roaming disabled. Save succeeded; Refresh retained a valid view and cleared its previous result notice. |
| Diagnostic file | The saved `snapshot.json` parsed successfully: 17,182 bytes, 34 events, `LocalOnly=true`, and only the defined root/event fields. Targeted inspection found no loopback server address, test username, media title, or credential marker. This complements the service's hostile-input and retention tests. |
| Close during playback | Resume returned the HLS session to Playing. Closing the native window completed; the window and application process disappeared. The final local diagnostic event was `ResourceReleased` / `None`. This is not a packet-level network-silence assertion. |

Screenshots remain in ignored `artifacts/ui-validation/2026-09-09/` with the `c6e8-` prefix. A library success notice remained visible when entering playback; the next source revision clears that notice on starting a player flow. No crash dialog was observed. Actual playback Retry is not marked passed by this checkpoint.

## Manual recovery checkpoint: 313A94C3

Executable SHA-256: `313A94C3C7BE6821B489E49A2A7AC705617D47CC53EFEA3984641C262305D659`. This publish adds the correction that retains default versus explicit track intent during recovery and clears the old library notice when entering playback.

The actual UI signed in to the separate synthetic fixture on port 18962 with temporary, nonremembered credentials. Selecting **Resume at 0:03** caused the fixture's one-shot playback-negotiation failure: a real HTTP 503 with an empty body. The player showed Failed and the separate **Retry from last position** action. Clicking it once opened DirectStream with actual blue video, an advancing clock, and a displayed position of 0:03. The retry action disappeared after recovery.

Supporting fixture records show exactly one injected failure and one subsequent successful PlaybackInfo negotiation. The new session's Start report was at 30,100,000 ticks (3.01 seconds), followed by Progress and a successful Stop when returning to the library. The library then showed the updated resume position. The temporary account was signed out and the application closed through its own controls.

The failure screen displayed 0:00 while no active stream existed; the recovered session and server Start establish that the retained target was three seconds. This observation does not claim an active-stream interruption or paused recovery; the [real cold-range network probe](../../tools/EmbyClient.NativeProbe/verification/network-retry-nativeaot.json) independently covers those native behaviors. Screenshots and the synthetic supporting receipt remain under ignored `artifacts/ui-validation/2026-09-09/` with the `313a-` prefix.

## Native external WebVTT inspection

The separately published native probe exercised the product engine with `EnableExternalWebVtt=true` against official Emby 4.9.5.0 and the generated Fixture item. The source remained DirectStream, while an independent authenticated request supplied 175 bytes of WebVTT converted from external SRT. The real timed-text source resolved one track in `PlatformPresented` mode.

At a native paused position of 41.0299911 seconds, root computer-use screenshot inspection showed the complete cue "Seek and subtitle delivery check." over the expected orange frame. The [retained PNG](../../tools/EmbyClient.NativeProbe/verification/external-subtitle-cue.png) is encoded from the tool's original JPEG without changing the decoded RGB pixels. Its SHA-256 is `1cd48a3f8352d32352980af682e63dc197fc45fe5448d8cb5941a8e9070b0ffb`. The [complete receipt](../../tools/EmbyClient.NativeProbe/verification/external-subtitle-nativeaot.json) passes actual visual confirmation, one successful Start/Stop pair, native/coordinator disposal, owner clearing, and logout, with no diagnostics or cleanup errors.

The [first attempt](../../tools/EmbyClient.NativeProbe/verification/external-subtitle-screenshot-format-failure.json) remains Failed because a JPEG was incorrectly saved with a PNG filename; it is not rewritten as a pass. Both attempts used the same audited executable. The successful second attempt establishes one plain external cue and cleanup, not ASS/PGS fidelity or a repeated subtitle-resource gate. The normal application profile still prefers server burning by default.

## Poster cleanup normal-publish checkpoint: B1CBF350

Executable SHA-256: `B1CBF350968A64456C975932591CDD62FC08D3ED72BDD6892925CEF76F9AD7A5`. This is the normal Native AOT publish after collection Reset poster cleanup, with `LibraryObservation` disabled. No observation manifest or runtime JSONL was present in the publish directory before or after these checks.

- The saved official-server account restored successfully. Its library and detail views displayed the expected placeholders for generated media without poster artwork.
- Fixture opened through server conversion with English SRT selected. Pausing at 14 seconds showed the red source section. Dragging the timeline to 41 seconds reopened the stream and restored Paused with the expected orange frame and the complete burned cue, "Seek and subtitle delivery check."
- Selecting subtitle Off briefly displayed Opening, then settled at Paused, 41 seconds, with the orange frame and no caption. The UI still reported Transcode; this is not a DirectStream assertion. Resume returned to Playing and the timeline advanced. Back returned to the correct detail view.
- After switching to the temporary synthetic large-library account, the ordinary build displayed Home artwork, the library's first-page posters, the selected detail poster, and the first-page posters again after Back. This is a finite navigation regression check, not a repeat of the instrumented memory experiment.
- The temporary account was signed out, and closing the native window removed both the window and application process. The normal output still contained no observation files.

Original JPEG screenshots and UTC stage records are in ignored `artifacts/ui-validation/2026-09-09/final-b1cb/`. The complete Release test script also passed all 340 tests after the cleanup change. The separately instrumented [Reset lifetime evidence](large-library-validation.md#libraryobservation-before-and-after-collection-reset-cleanup) establishes the bound-source count, which cannot be read from screenshots alone.

## Keyboard timeline and dialog focus checkpoints: DBC6C326 and 77A90D2D

The DBC6C326 executable (`DBC6C326B9B17B953EBE7F68E6270B489AEFB4E204F05CE4326C4EFC0AF5C5FB`) adds keyboard editing ownership to the timeline. Slider adjustment keys retain their native value behavior; the 500 ms position refresh cannot overwrite a pending edit, and KeyUp submits the captured value against its original playback ID. Lost focus, stopping, disconnection, unloading, and a replaced playback context invalidate the edit.

Actual DirectStream playback of the generated 60-second fixture was paused at approximately 13 seconds. Shift+Tab focused the timeline with a visible focus outline. Home sought to zero and showed the blue source frame; Right advanced to one second and Up to two seconds, with pause retained. Page Up at zero and Page Down at one second produced no visible value change; no large-step behavior is claimed. Tab moved to Resume, Space resumed playback, F11 entered fullscreen, Escape restored the window, and Space still paused the same player afterward. The tool sends individual key presses and did not verify a sustained physical key hold across a refresh interval. The held-edit guard also has static ownership/lifecycle review; it is not represented as a physical-key stress test.

This run exposed a separate focus defect: opening Queue from the library and closing it with Escape focused Appearance. Pressing Enter then opened the appearance menu instead of Queue. The original screenshots and stage times are retained under ignored `artifacts/ui-validation/2026-09-09/keyboard-dbc6/`.

The corrected executable is `77A90D2DD8D3AD527BFFCFF40361BEAB39FDC2A8F3C90F72E2340506BB21D9E8`. Queue and Diagnostics now receive the actual triggering control, re-enable both toolbars after the dialog completes, and restore that control only if the session, playback intent, view, and visible usable target remain current. No focus restoration is attempted into a replaced or collapsed view.

| Entry point | Observed corrected behavior |
| --- | --- |
| Library Queue | Escape returned the visible focus outline to Queue; Enter reopened Queue. Activating its Close button with Enter again returned focus to Queue. |
| Library Diagnostics | Tab from Queue reached Diagnostics; Enter opened it. Escape returned focus and Enter reopened Diagnostics. Clicking Close also allowed Enter to reopen the same dialog. |
| Player Queue | The same Escape/Enter round trip worked over paused DirectStream video. Activating Close with Enter returned the outline to the player Queue button. |
| Player Diagnostics | Tab/Enter opened the dark native dialog. Both Escape and the clicked Close button allowed Enter to reopen Diagnostics, with pause and the 18-second frame retained. |

The synthetic account was signed out and the final app window/process closed. Original screenshots and stage times are under ignored `artifacts/ui-validation/2026-09-09/focus-77a9/`. Both ordinary AOT publishes succeeded with only the existing generated CS0618 warning. These finite keyboard/focus checks do not establish Narrator, physical media keys, all keyboard layouts, or multi-monitor DPI acceptance.

## Interrupted normal-app checkpoint: 5C3C472A

The user completed saved sign-in on the synthetic large-library account, then resumed desktop verification on 2026-09-10. The [recorded identity](verification/ui-5c3c-20260910/identity.json) is the normal application executable `5C3C472A714316F177C3A4F62C9BE19DF9421CA1FA0B1097F6B33737C687C0D0`, PID 29912. Its process had already been running since 2026-09-09 09:53 UTC; this is not a cold-start or new-publish memory baseline.

The [eight original JPEGs and observation records](verification/ui-5c3c-20260910/observations.json) establish these finite results:

- The minimized application restored to the connected Home library.
- **Resume at 0:03** opened DirectStream with visible blue video and an advancing displayed position. Accessibility text and image capture are different samples and must not be treated as simultaneous timestamps.
- Pause settled at displayed 0:42 with the orange source scene. Dragging to displayed 0:17 retained pause and showed the red scene. The mute toggle was selected; no audible-output claim is made.
- F11 expanded to a 2560 by 1440 capture and Escape restored the 1268 by 834 window, still paused at 0:17. A subsequent actual minimize/restore also preserved the red paused frame. The intermediate restore-animation capture was excluded from stable-layout evidence.
- Back completed and the item detail offered **Resume at 0:17**.

The operator stopped computer use with the physical Escape key while navigating toward the synthetic test episodes. No new automatic-continuation, audio-track, subtitle, close-during-opening, or long-duration memory result was obtained. The single process-memory/handle snapshot in the identity record is not a convergence measurement. The user then requested code completion followed by a [consolidated validation session](validation-session.md), with no further desktop interruptions during implementation. These screenshots predate the subsequent account, preparation, pointer-drag, and resource-lifecycle repairs.

## Consolidated normal-app checkpoint: 779B23ED

The user authorized one consolidated local acceptance batch on 2026-09-10 and manually completed saved sign-in. The fixed candidate was `779B23ED8D28B845860AB72ABE1764D60FCDA7D9B2F6A089EFCFEA912BCBA93C`, source `532f683a3df68a6b5d0da62e34f277a2b1807a15`, PID 19652. No code changed during the batch. The [receipt](verification/ui-779b-20260910/summary.json), [26 original JPEG observations](verification/ui-779b-20260910/observations.json), and [file manifest](verification/ui-779b-20260910/manifest.json) retain the completed evidence. Observation archive times are save times, not exact capture/action timestamps.

All new playback used the dedicated synthetic fixture on port 18962, with generated 60-second H.264/AAC MP4 media. This adds native product observations, not another official Emby compatibility result. The library measurements ran before any video in this process, with no concurrent builds, native probes, or forced GC. Their [separate analysis](large-library-validation.md#normal-native-aot-779b-2026-09-10-follow-up) remains unresolved: Home private memory rose **13.95 MiB across 175.55 sampled seconds**, despite fixed image/query counts and zero active image samples.

| Flow | Actual result and boundary |
| --- | --- |
| Library and artwork | Six API pages returned 48 items each, 288 of 5,000 records. Five top-to-middle revisits restored the visible posters. Immediate scroll placeholders settled; no persistent missing artwork was observed. The second CSV ends before the fifth final checkpoint. |
| Native playback and mouse seeking | Episode One opened as Playing / DirectStream with an actual blue frame. Pause settled near 21 seconds. Mouse drags to 41, 17, and 57 seconds showed orange, red, and purple frames while preserving pause. The mute toggle was selected; no audible-output result is claimed. |
| Window transitions | F11 entered fullscreen and Escape returned to the original window at paused 17 seconds. A subsequent minimize/restore preserved Episode Two's paused red frame near 12 seconds. Restore-animation background pixels were excluded from the archive. |
| Automatic episode continuation | With an empty queue and Play next automatically checked, Episode One resumed at 57 seconds and naturally ended. No Next command was issued. Episode Two opened with a blue frame at about four seconds, advanced to the red segment, and responded to Pause. |
| Playback reports | Episode One session `synthetic-play-a8ffb0c5bc524b71a66eed23fe7e513e` has one Start and one successful Stop at 60.0106458 seconds. That Stop precedes Episode Two session `synthetic-play-8b5be91b6e8046b086905d658e705962`, which has one Start and one successful Stop at 12.1499994 seconds after Back. No later event belongs to either stopped session. |
| Diagnostics | The native player dialog opened and Save snapshot reported success. The saved JSON contains 128 retained events, including 18 from this batch with Error=None and one Ended. It was saved during Episode Two pause, before its Stop and final shutdown; it does not establish a final ResourceReleased event. Earlier retained events remain historical. |
| Return and queue | Back stopped Episode Two and returned to the originating Episode One detail, now marked Played. Adding Episode One produced one selected entry in the library queue dialog. This check does not establish queue-head editing during detail loading or another queued-item consumption cycle. |
| Ordinary account exit | Sign-out after both playback sessions had stopped returned to the login page. The dedicated synthetic protected token became empty and LastAccountKey became null; all three other account records remained unchanged. The four account records were retained. |
| Ordinary application exit | Closing the signed-out window left PID 19652 absent at 20:35:10.8789944 UTC. This was neither close during Opening nor sign-out with delayed playback cleanup. The desktop batch then ended. |

Relative to the pre-playback fixture checkpoint, this batch adds two PlaybackInfo negotiations, two Starts, two Stops, 28 Progress reports, and 19 media/range/partial responses; encoding cleanup and authentication-failure counts do not increase. The fixture's aggregate counts include an older movie session, so the scoped episode IDs establish the new result. Media/report counters remain unchanged between the post-stop and final snapshots. These are request/report observations, not packet capture or proof that every previously active socket was released.

The tool rejected one stale screenshot ID after leaving fullscreen; fresh observation preceded the successful retry. A minimized-state request with both capture flags false was rejected; the returned window was reselected and restored. Neither incident was a product failure or a user Escape stop. No previously rejected NativeProbe launch or official-server public-info request was retried.

Actual late pointer release after capture loss/replacement, failed detail loading and Retry loading item, queue-head edits while details load, whole-app closure during a proven Opening interval, and slow-cleanup sign-out/close overlap remain unverified here. The fixture has no media-opening delay, failed-detail mode, or slow-cleanup control; ordinary successful paths cannot certify those boundaries. No new HLS, audio/version, ASS/font, PGS, external-subtitle, progressive native-failure, or concurrent-disposal fault result was obtained. Their existing automated and historical native evidence retains its original scope.

## Session-triggered library observation: 257B0F46

The separately instrumented schema 3 Native AOT executable `257B0F462E2B0D45C34EB6632E2C29EDF184310047905FC48BC069D5E3212CBE`, PID 20760, has a [retained receipt](verification/ui-257b-20260910/summary.json), eleven original screenshots, and an immutable 154-row numeric prefix. It was connected to the existing port 18962 synthetic fixture when desktop work resumed. Authentication controls were not automated. The first capture shows Home with both continue-watching posters.

Five Load more actions followed the initial page. Scrolling to the loaded end triggered one further page, for seven offsets from 0 through 288 and 336 total loaded records. The sampled late-list placeholders recovered to posters. Three top revisits, two middle revisits, and return to Home all showed the expected synthetic posters. This finite sequence does not certify all 5,000 records. No video, account change, sign-out, or window close was performed; the playback repetition proposed before this batch was deferred to preserve the library-only sample.

After saving the final Home capture, desktop release was recorded at 2026-09-10 00:04:32 UTC and Computer Use was immediately reset. No further automated desktop calls were issued; user activity after release was not observed. The already-running logger supplied the subsequent passive tail. [Memory analysis](large-library-validation.md#session-triggered-257b-library-and-home-observation) records a 1.2539 MiB private-byte decline over the settled 210.273-second Home tail, with no new decodes or GC. That finite decrease does not resolve the previous memory gate or identify retained bitmap/native owners. The application remained open; this receipt makes no process-exit assertion.

## Finite keyboard media flow: 0E84F458

After renewed desktop authorization, the earlier observation process was absent. The normal Native AOT candidate `0E84F4581A3D2C3BE460DCBD0D936733A3F970B56DD4CE956D04E09932F18034` was launched and retained the same executable hash after this batch. PID 1780 started at 2026-09-10 00:40:20.733 UTC. Its build identity is defined by the [startup-repair publication receipt](verification/startup-initialization-aot-20260910.json); the repository revision at verification is not substituted for that build's source-input manifest. The user manually completed saved sign-in to the existing port 18962 synthetic account. Computer Use was reset during that handoff.

The [batch receipt](verification/ui-0e84-keyboard-20260910/summary.json), sixteen original JPEGs, saved accessibility trees, and [forty recorded input calls](verification/ui-0e84-keyboard-20260910/actions.json) preserve the actual sequence. An initial text-input call and Tab call produced no visible change. One mouse click then established visible search focus. From that setup, text input and Enter produced the single Synthetic Color Study result; Tab reached the result card, Enter opened details, and reverse Tab navigation reached Play.

Keyboard activation started actual DirectStream playback with visible blue frames. The first Space action activated the focused Back control, stopped playback, and returned to the original detail with Resume at 0:22; it is not a pause result. On the second playback, four forward Tab presses from Back reached Pause through Queue, Diagnostics, and the timeline. Space on the visibly focused Pause control produced Paused at about 47 seconds and retained the orange frame.

Further Tab navigation reached Mute and Volume. Space selected and then cleared the Mute toggle. Home on Volume displayed 0, and End restored 100 while the playback position remained paused at 47 seconds. Forward Tab navigation through AutoPlay, Fullscreen, Quality, and one outline-free stop returned to Back; Enter returned to the original details with Resume at 0:47. Intervening controls were traversed without activation. The native audio output was not assessed, and the volume keys were not timeline seek tests.

The [independent protocol comparison](verification/ui-0e84-keyboard-20260910/protocol-analysis.json) excludes the unchanged original 64 events. The two new item-1001 sessions each have one Start and one Stop with `Failed=false`; their recorded positions are 0.010 to 22.3409719 seconds and 0.03375 to 47.5199994 seconds. The second session therefore began again near zero rather than resuming at 22 seconds. Totals increase by two PlaybackInfo requests, two Starts, 32 Progress events, two Stops, and 18 media/range/partial responses. After pause settles, 23 further Progress events and Stop retain 47.5199994 seconds. This establishes scoped synthetic reporting, not native-resource release or audible output.

The tool's `focused_element` field repeatedly lagged behind the visible focus outline, including reporting the search edit while other controls held a focus rectangle. The initial `02-connected-home` note reflects that reported search focus only; actual input focus had not yet been established. Raw reported focus values remain in the action journal but are not treated as authoritative focus assertions. Screenshot `ArchivedAtUtc` values are save times; input-call timestamps are not exact Windows event or screenshot times. The outline-free Tab stop is recorded without assigning it an unobserved control identity.

Desktop release was recorded at 2026-09-10 00:52:55 UTC after the final detail screenshot, followed immediately by a Computer Use kernel reset. Subsequent work used saved files and one read-only fixture-counter snapshot. The app remained on the item detail; this batch did not sign out, close the application, run a memory trial, or repeat delayed/failure boundaries. These finite controls work after explicit focus setup; this does not certify a completely mouse-free flow, Narrator, all keyboard layouts, sustained physical keys, or exact navigation focus restoration.

## Remaining final-build acceptance

An earlier relay/SMTC application checkpoint, executable SHA-256 `2ED8644A666DBDA41178171B802E15D1856F680D23C99EBBA2D19C68501FDA4C`, displayed real original video and closed its playing window without an observed crash dialog. The native probe independently checked SMTC Playing, Paused, and retired state. The automation tool did not support the physical media-key input, so that input path has not been established.

The C6E8E17B and 313A94C3 checkpoints establish the listed shared-owner frames, paused transitions, queue editing/continuation, diagnostics, and manual recovery. Large-library memory behavior and native external subtitles retain separate acceptance records. Narrator, audible output/device changes, multi-monitor DPI, signed installation, clean-machine playback, and broad server/codec/HDR matrices remain distinct unverified gates. Native resource success cannot substitute for those observations.
