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

1. The native player can report Paused before its first actual start because AutoPlay is disabled during initialization. If direct opening then failed, the coordinator initially treated this as user intent and paused the fallback conversion. The coordinator now requires actual earlier playback before inheriting that state. Explicit paused-restart intent remains authoritative. The 61-test coordinator suite verifies both new playback and nonzero resume; a newer UI executable must recheck this path.
2. On paused French-audio switching at approximately 17.6 seconds, the UI kept its displayed position but the video returned to the blue start of the sample. The server received StartTimeTicks and the correct audio/source indexes, yet returned a complete HLS VOD playlist beginning with segment zero. It did not crop the output at that requested time. Treating engine zero as source 17.6 was therefore incorrect. The correction must perform a real initial seek on the complete HLS timeline; changing only the displayed number is insufficient.

The direct-play lifetime probe also still failed its resource gate at this checkpoint. Passing these UI flows does not override that failure. The native HTTP control subsequently passed the same 20-cycle resource criteria and motivates the session-scoped relay implementation; the complete product path needs its own final run.

## Remaining acceptance

The subsequent relay/SMTC application checkpoint, executable SHA-256 `2ED8644A666DBDA41178171B802E15D1856F680D23C99EBBA2D19C68501FDA4C`, displayed real original video and closed its playing window without an observed crash dialog. The native probe independently checked SMTC Playing, Paused, and retired state. The automation tool did not support the physical media-key input, so that input path has not been established.

After the latest 253-test run and successful Release/Native AOT publication, computer interaction was stopped by the user's physical Escape key before the new executable could be inspected. No final screenshot or visual acceptance is claimed for that output. Real HLS lifecycle probes subsequently established functional initial seeking and paused reopening at 45 seconds, but the 20-cycle resource gate still failed (+52 handles against a limit of 32). Neither native clock values nor those functional checks replace source-frame agreement in the application.

Recheck the final executable after the relay and HLS timeline corrections: paused audio/version/subtitle switching with source-frame agreement, resumed conversion, actual system media commands, and resource lifecycle. Complete package structural verification for the final publish. Narrator, audible output/device changes, multi-monitor DPI, signed installation, clean-machine playback, and broad server/codec/HDR matrices remain distinct unverified gates.
