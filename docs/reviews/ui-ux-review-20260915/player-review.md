# Player UI and UX review

Reviewed on 2026-09-15. Scope: the existing WinUI player, its presentation states, native flyouts, settings and queue panels, and host navigation/keyboard integration. This review does not change product source, build, run tests, or operate the application.

## Overall conclusion

The player has a sound structural foundation. The native window title bar, stable centered transport, responsive reduction of secondary commands, modeless settings panel, and fullscreen control hiding should be retained. Its next refinement should prioritize truthful control states and recoverable failures, then improve media-surface continuity and the hierarchy of playback settings. It does not need a new page architecture to become more polished.

Existing screenshots prove the basic native layouts, not every state. Most playback surfaces were captured with a synthetic H.264/AAC color sample rather than real images, subtitles, or multiple media sources. The previous verification explicitly excludes those scenarios. This review additionally incorporates native evidence collected by the primary reviewer for an 8 Mbps account limit and F11 with the settings panel open.

## Evidence and scope

- Native screenshots: `06-player-windowed.png`, `07-player-settings.png`, `08-playback-diagnostics.png`, `final-12-narrow-player.png`, `final-13-fullscreen-controls.png`, and `final-14-fullscreen-hidden.png` in `artifacts/ui-implementation-verification/native-20260914/screens`.
- Native observation record: `docs/implementation/verification/ui-refinement-native-20260914.json`, especially `nativeObservations` and `limits`. It records actual mute/pause, settings, fullscreen hiding, natural episode advancement, and updated progress after returning to details.
- Supplemental native evidence from the primary reviewer: `artifacts/ui-ux-review-20260915/native/15-bitrate-limit-selection-mismatch.png` and `.txt`, and `16-f11-with-settings-open.png` and `.txt` in the same directory. An isolated review account has `RemoteClientBitrateLimit = 8,000,000`; playback was paused before opening Settings and pressing F11.
- Source inspected: `PlayerView.xaml`, `PlayerView.xaml.cs`, `PlayerView.Presentation.cs`, `PlayerView.Diagnostics.cs`, player entry/return/keyboard handlers in `MainPage.xaml.cs`, and the relevant playback coordinator selection/replay/retry contract.
- A separate static reviewer independently checked keyboard focus, side panels, fullscreen behavior, and the error wrapper. Its findings are incorporated below.
- Priority meaning: P2 is a materially confusing or obstructed existing workflow; P3 is a smaller consistency/polish issue. A native reproduction gap is stated explicitly rather than reported as a verified runtime defect.

## Confirmed findings from source

### P2 — The failure message recommends controls that the failed state disables

When a stream fails, the UI recommends choosing another version or quality, or turning subtitles off. The same status handler calls `SetTransportAvailability(false)`, disabling the version, audio, subtitle, and quality controls. Opening the settings panel does not provide the advertised recovery action. The dedicated retry preserves the previous failed selection, while the main Play button replays it from the beginning.

- Error copy: `src/EmbyClient.App/Views/PlayerView.xaml.cs:792–805`.
- Failed-state transport availability: `src/EmbyClient.App/Views/PlayerView.xaml.cs:313–327`.
- Disabled selection controls: `src/EmbyClient.App/Views/PlayerView.xaml.cs:415–428`.
- Retry/replay actions: `src/EmbyClient.App/Views/PlayerView.xaml.cs:373–413`, `639–655`.
- Coordinator boundary: `src/EmbyClient.Playback/PlaybackCoordinator.cs:116–152` requires an active playback for selection changes; the UI cannot fix this just by enabling the existing ComboBoxes.

Conclusion: align the failure action and message. A later implementation should provide an explicit change-settings-and-retry path, or limit the message to available actions. Treat retry from the last position as the primary recovery when it is available, and make restart from the beginning unambiguous. This is a recovery workflow issue, not a request for additional visual decoration. Native error-state reproduction remains desirable.

### P2 — Account bitrate limits disagree with the selected quality option

The session clamps `_bitrate` to `RemoteClientBitrateLimit`, but the quality ComboBox retains its prior option and all five fixed options remain selectable. For an account capped at 8 Mbps, the main control can display 8 Mbps while the settings ComboBox still displays 20 Mbps; choosing 40 or 80 Mbps continues to apply 8 Mbps. The quick menu checks the ComboBox selection rather than the effective bitrate.

- Fixed options: `src/EmbyClient.App/Views/PlayerView.xaml.cs:78–83`.
- Session clamp: `src/EmbyClient.App/Views/PlayerView.xaml.cs:104–108`.
- Change clamp: `src/EmbyClient.App/Views/PlayerView.xaml.cs:707–714`.
- Effective value and limit caption: `src/EmbyClient.App/Views/PlayerView.Presentation.cs:296–306`.
- Quick-menu checked value: `src/EmbyClient.App/Views/PlayerView.Presentation.cs:270–281`.

Native corroboration: `15-bitrate-limit-selection-mismatch.png` shows 8 Mbps in the transport, 20 Mbps selected in Settings, and an account-limit caption of 8 Mbps at the same time. Its paired `.txt` confirms the transport's accessible name and limit caption. This reproduces the mismatch under an actual WinUI session using the isolated review account. Choosing 40/80 Mbps remains a source-derived behavior, not an additional native claim.

Conclusion: distinguish the requested cap from the effective account cap and make the choices truthful. A simpler user-facing model is an effective maximum bitrate selector with unsupported choices removed or clearly unavailable. Also label this control as a maximum bitrate; it does not assert the actual resolution, output bitrate, or visual quality.

### P2 — Opening read-only playback information silently dismisses the current error

`RunAsync` unconditionally closes `PlaybackNotice` before every operation. Both the queue and diagnostics entry points use it. Consequently, a user inspecting diagnostics after a playback failure loses the visible error context even though playback is still failed. No explicit dismissal or successful recovery is required, and closing the dialog does not restore the error.

- Unconditional clear: `src/EmbyClient.App/Views/PlayerView.xaml.cs:777–789`.
- Read-only entry points: `src/EmbyClient.App/Views/PlayerView.xaml.cs:718–722`.
- Dialog lifecycle does not restore playback error state: `src/EmbyClient.App/Views/PlayerView.Diagnostics.cs:18–33`.

Conclusion: opening a queue or diagnostic view should preserve the current failure. Clear or replace errors when a recovery attempt begins, a successful operation resolves the problem, or the user explicitly dismisses them. Native error-state reproduction is still useful, but the unconditional source path is definite.

### P3 — The advertised F11 shortcut stops working while a modeless panel is open

`MainPage.OnPageKeyDown` returns for `Player.IsSettingsOpen` before checking F11. That property includes the modeless queue/settings panel. The fullscreen button still changes fullscreen and continues to advertise F11. Escape's layered behavior is correct: it closes a flyout/panel before exiting fullscreen.

- Guard before F11: `src/EmbyClient.App/MainPage.xaml.cs:345–363`.
- Panel included in settings state: `src/EmbyClient.App/Views/PlayerView.xaml.cs:49–50`.
- Tooltip and click: `src/EmbyClient.App/Views/PlayerView.xaml.cs:200–208`, `723`.

Native corroboration: after the primary reviewer pressed F11 with Settings still open, `16-f11-with-settings-open.png` and `.txt` still show the native window title bar and the Enter fullscreen button. The settings-panel case is reproduced; the equivalent queue-panel path remains supported by the shared source guard.

Conclusion: window-level fullscreen commands should remain consistent while a nonmodal panel is open, subject to any actual popup/dialog key handling.

## Candidate requiring native reproduction

### P2 candidate — Covered transport controls remain in the keyboard order under a narrow side panel

Below 1040 DIP, the side panel occupies the same grid column as the player and covers its right side. The player remains enabled and its controls remain in the Tab order. Opening the panel only focuses its close button once; no overlay-specific focus scope or traversal behavior is declared. Unlike the wide inline panel, focus can therefore potentially move onto controls hidden beneath the overlay.

- Overlay layout: `src/EmbyClient.App/Views/PlayerView.Presentation.cs:133–151`.
- Panel declarations: `src/EmbyClient.App/Views/PlayerView.xaml:119–173`.
- Initial focus only: `src/EmbyClient.App/Views/PlayerView.Presentation.cs:320–342`.

Reproduction: use an 800–1000 DIP window, open Settings, traverse with Tab and Shift+Tab, then repeat with Queue and an expanded native ComboBox. Observe both the focus outline and the invoked action. Confirm whether a hidden settings/fullscreen/transport control can receive focus. A wide inline panel may legitimately allow navigation back to the visible transport; the overlay mode needs a different focus policy if this candidate reproduces.

## Page and state conclusions

| Page or state | Current conclusion | Improvement or acceptance condition |
|---|---|---|
| Windowed playback, light theme | Native title bar and centered play control establish a clear hierarchy. Screenshot 06 shows the pale dock as a visually separate strip beneath a black media stage. | Retain the title bar integration. Consider a coherent dark media stage and transport surface in the light application shell. This is a visual direction, not a Fluent compliance defect. |
| Windowed playback, dark theme | The narrow screenshot demonstrates a quieter relationship between media and dock. The screenshot was taken at the initial 0:00 state, so its dim Play/Pause appearance is not evidence of a persistent contrast defect. | Check playing, paused, hover, pressed, and disabled states before changing primary-button colors. |
| Narrow playback | Secondary buttons drop out in deliberate order while volume, primary transport, settings, and fullscreen remain. No clipping is visible in the existing narrow screenshot. | Preserve the adaptive structure. Resolve the overlay focus candidate and check the minimum window with system text scaling. |
| Fullscreen, controls visible | Header and dock overlay the media with restrained gradients. The title and state are visible, and the picture remains dominant. | Keep the arrangement. Verify subtitle overlap and high-contrast surfaces with real media. |
| Fullscreen, controls hidden | The native record confirms hiding after about 3.6 seconds. Source protects open panels, keyboard control focus, dragging, loading, errors, pause, and buffering. | Retain those safeguards. A polished media experience should also consider hiding the stationary pointer over the video; the current code only hides header/dock opacity and the screenshot still shows a pointer. |
| Fullscreen reserved control area | The layout intentionally reserves the dock row while shrinking the media stage, and high contrast avoids transparent gradients. | It is an understandable optional setting, but no native screenshot proves real image-embedded or selectable subtitle readability in this mode. |
| Loading item / preparing playback | A centered native ProgressRing and disabled transport prevent invalid commands. In ordinary windows the header containing `StateText` is collapsed, so the visible state is essentially a generic spinner. | Add a brief state label near the ring when the wait matters, with the requested item's identity. Distinguish loading metadata, starting playback, and buffering without a large banner. |
| Buffering / seeking | Existing state handling retains the player and disables unsafe actions; the fullscreen chrome remains available. Seeking retains the displayed timeline during interaction. | Verify delay, drag, and keyboard behavior on actual media. Avoid moving or replacing the player layout during these operations. |
| Failure / retry | Recovery ownership and a separate retry-from-position action exist. The user-facing action hierarchy and error persistence have the confirmed gaps above. | Correct those gaps before aesthetic work on the error surface. |
| Timeline and ten-second seek | Native Slider, readable time tooltip, tabular digits, and pointer/playback ownership are good foundations. Twelve-point minus/plus-ten labels are visually weak beside the primary action in screenshot 06. | Keep native seek behavior. Improve the seek symbol/number treatment and evaluate clarity at normal scale, rather than increasing every control. |
| Volume / mute flyout | A compact native flyout keeps the dock quiet; mute was exercised in the native record. The closed icon changes when muted. | Keep it. Consider exposing current muted/volume state in the button's accessible name or tooltip and verify that keyboard changes remain evident. |
| Version and audio selection | Real source/stream data is used, and a single available choice becomes read-only text rather than a fake dropdown. That behavior is appropriate. | Retain the conditional UI. The fixture only proves a single source and audio track; long names and switching need dedicated native evidence. |
| Subtitle menu / selector | Off is explicit and options derive from real stream indexes. A one-choice subtitle list becomes read-only text and the shortcut is disabled. | The UI structure is reasonable. A combined audio/subtitle entry may reduce the frequent need to open the full settings pane, but that is a design choice. Native subtitle selection/rendering is unverified. |
| Quality menu / selector | The maximum bitrate is currently presented as streaming quality; the account clamp causes a real inconsistency. | Correct semantics and selected-value truth first. Then decide whether it warrants a prominent dock shortcut. |
| Playback settings | The panel has clear close and Queue/Settings controls, and real media values are readable. Screenshot 07 reads as a long ungrouped form, particularly around the lengthy reserved-space setting. | Group current media, viewing behavior, and low-frequency actions. Use a shorter setting label with a concise subordinate explanation. Retain native scrolling. |
| Queue panel, populated / empty | Current item, upcoming count, item actions, and local clear semantics are present. Empty state explains how to add media. The actual modeless queue panel is not in the supplied screenshot set; the captured queue is the separate library dialog. | Keep queue editing separate from immediate playback. Make the target of Play next clear when a non-head row is selected. Verify long queues, selected-item removal, and narrow overlay focus. |
| Playback ended / automatic next | Natural advance from episode one to episode two and progress refresh were observed natively. A replay action remains at the end. | Preserve this working behavior. When no successor exists, make the ended state clear in ordinary windows, where `StateText` is currently hidden. A future up-next preview is optional scope, not a missing required feature. |
| Return to library | The host stops playback, restores the library, refreshes data, and returns focus. The native record demonstrates updated watched/resume status. | Keep this existing contract. If delayed server stop makes return visibly slow, show an explicit transient leaving/stop state rather than an apparently unresponsive player. Continuing playback while browsing is a separate feature idea, not a defect here. |
| Diagnostics opened from playback | The native dialog is readable and uses the standard modal surface. Media behind it remains the playback context. | Preserve the originating error as described above. Detailed diagnostics-dialog review belongs to the dialog owner. |

## Design opportunities without confirmed functional defects

1. Make the media stage and dock read as one viewing surface. The strongest visual improvement is coherent local tone, not another banner, rounded card, or saturated accent.
2. Strengthen the transport hierarchy: keep Play/Pause prominent, make ten-second seek symbols legible, and reduce the emphasis of empty-queue counts and maximum bitrate.
3. Reorganize settings into a small number of meaningful sections. The single-choice read-only presentation is already the right basis.
4. Make nonplaying states legible in windowed mode. Current `StateText` lives in a header that is hidden there (`PlayerView.Presentation.cs:136`); buffer/retry/end copy should appear where the media state is experienced.
5. Consider a short reveal/hide transition and pointer inactivity behavior for fullscreen only. The current transition directly sets opacity (`PlayerView.Presentation.cs:206–210`). Any future animation must preserve the existing focus/error/drag protections and respect reduced-motion preferences.

## Acceptance gaps, not assertions of failure

- Actual multi-source/audio/subtitle switching, long stream names, selected subtitles over contrasting video, and reserved-control-area behavior.
- A bitrate limit below 5 Mbps, and changing higher options under a restricted account. The between-options 8 Mbps mismatch is now natively corroborated.
- Metadata preparation failure, stream negotiation failure, unsupported subtitle, retry, and opening diagnostics/queue while failed.
- Pointer and keyboard timeline seeking, canceled drags, seeking while buffering, and dragging across playback replacement.
- Narrow overlay side-panel keyboard traversal and F11 with the Queue panel open. F11 with Settings open is now natively corroborated.
- System text scaling, high contrast, Narrator state announcements, and real bright/dark moving imagery. Existing unit/service tests are not substitutes for those native interaction observations.
- Long populated queue panel, keyboard reorder/remove/clear, empty transition, and focus restoration after a panel becomes too narrow to show its original trigger.

No production implementation is recommended as part of this review turn. The next decision should separate the three P2 state/recovery corrections from the visual refinement pass; both can then be reviewed against concrete native states.
