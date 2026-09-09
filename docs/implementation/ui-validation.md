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

This executable predates the final shared-player owner, explicit retry UI, editable queue, and diagnostics UI. It establishes the listed behavior at this exact checkpoint; those later changes still need integrated acceptance.

## Remaining final-build acceptance

An earlier relay/SMTC application checkpoint, executable SHA-256 `2ED8644A666DBDA41178171B802E15D1856F680D23C99EBBA2D19C68501FDA4C`, displayed real original video and closed its playing window without an observed crash dialog. The native probe independently checked SMTC Playing, Paused, and retired state. The automation tool did not support the physical media-key input, so that input path has not been established.

The next integrated run must use the final shared-player owner together with the editable queue, explicit retry, and diagnostics UI. Check actual frames, paused transitions, queue editing/continuation, retry ownership, and dialog/keyboard behavior, then package that exact publish. Narrator, audible output/device changes, multi-monitor DPI, signed installation, clean-machine playback, and broad server/codec/HDR matrices remain distinct unverified gates. Native resource success cannot substitute for those observations.
