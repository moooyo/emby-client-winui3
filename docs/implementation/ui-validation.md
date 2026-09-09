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

## Remaining final-build acceptance

An earlier relay/SMTC application checkpoint, executable SHA-256 `2ED8644A666DBDA41178171B802E15D1856F680D23C99EBBA2D19C68501FDA4C`, displayed real original video and closed its playing window without an observed crash dialog. The native probe independently checked SMTC Playing, Paused, and retired state. The automation tool did not support the physical media-key input, so that input path has not been established.

The C6E8E17B and 313A94C3 checkpoints establish the listed shared-owner frames, paused transitions, queue editing/continuation, diagnostics, and manual recovery. Large-library memory behavior and native external subtitles retain separate acceptance records. Narrator, audible output/device changes, multi-monitor DPI, signed installation, clean-machine playback, and broad server/codec/HDR matrices remain distinct unverified gates. Native resource success cannot substitute for those observations.
