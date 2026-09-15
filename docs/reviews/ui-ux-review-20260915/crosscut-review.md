# Queue, diagnostics, and cross-surface UI/UX review

Date: 2026-09-15. Review only: no production edits, builds, tests, or UI automation were performed for this review fragment.

## Evidence and limits

Inspected the current XAML and C# for both queue presentations, diagnostics, shell title bar/navigation, library text-scale sizing, and player presentation. Visually inspected `final-05-queue.png`, `final-06-queue-empty.png`, `08-playback-diagnostics.png`, `final-10-narrow-navigation.png`, `final-11-dark-series.png`, `final-12-narrow-player.png`, and `final-13-fullscreen-controls.png` in `artifacts/ui-implementation-verification/native-20260914/screens`.

These are native captures with synthetic media. They establish actual control geometry in the captured states, not real-world artwork quality, long metadata handling, contrast-theme compliance, Narrator behavior, or 200% text-scale support. The player queue side panel is assessed from current source; the available captures do not show that surface open. Previously recorded test totals are not repeated as visual or accessibility evidence.

## Findings ordered by impact

### Q1 — P2 UX defect: adding to the queue has no visible confirmation with the navigation pane collapsed

The successful branch only raises a live-region event and returns. The sole visible numeric indicator is explicitly collapsed below a footer width of 120. In the compact rail, a sighted user can therefore add an item without seeing whether anything happened. Repeating the action adds another entry, since duplicates are accepted by the queue. This is a feedback defect, not a recommendation to prohibit duplicates.

Evidence: [MainPage.xaml.cs:254](D:/Code/emby-client-winui3/src/EmbyClient.App/MainPage.xaml.cs:254), [MainPage.xaml.cs:324](D:/Code/emby-client-winui3/src/EmbyClient.App/MainPage.xaml.cs:324), [TransientPlaybackQueue.cs:21](D:/Code/emby-client-winui3/src/EmbyClient.App/Services/TransientPlaybackQueue.cs:21).

Recommended direction: give the invoked action a brief success state or show a quiet, nonmodal `Added to queue` message with an `Open queue` action. Retain the compact navigation rail. Do not introduce a blocking confirmation or a persistent success banner.

Evidence classification: source-confirmed missing visual feedback; native reproduction should document the final visible outcome.

### Q2 — P2 design opportunity: explicit queue wording obscures automatic episode continuation

The queue summary and empty state describe only explicit queue entries, but use the broad phrase `0 upcoming items`. `AdvanceAsync` additionally looks up the following episode or server Next Up suggestion when the explicit queue is empty. The manual controls explicitly say `Next in queue` and are enabled only when that queue is nonempty. Their disabled state is therefore consistent with their named action, not evidence that automatic episode advancement is broken. The broader summary makes the distinction harder to discover.

Evidence: [PlayerView.Presentation.cs:363](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.Presentation.cs:363), [PlayerView.xaml.cs:187](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml.cs:187), [PlayerView.xaml.cs:742](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml.cs:742), [PlayerView.xaml:151](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:151).

Recommended direction: distinguish `Queued by you` from `Next episode`. If a concrete next episode has been resolved, show it separately and expose an accurately named action. If it has not been resolved, label the count `0 queued items` and explain automatic continuation without asserting that nothing is upcoming. Do not manufacture a next episode merely to populate the panel.

Evidence classification: the two mechanisms and their labels are established in source; their clarity is a semantics/discoverability judgment. Historical native verification observed natural episode advancement. Availability/failure of real episode lookup and the complete current queue-panel interaction remain to be checked.

### Q3 — P2 information defect: the player queue loses episode identity and ordering cues present in the library queue

The library dialog provides a visual order number, `Up next`, item type, year or season/episode, and an automation name containing the position. The side panel uses only title and series name/type. Two episodes with the same title in different seasons are indistinguishable there; manual ordering also lacks the dialog's explicit first-item cue. This weakens both visual scanning and the semantic label available to assistive technology.

Evidence: [PlaybackQueueDialog.xaml.cs:155](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlaybackQueueDialog.xaml.cs:155), [PlaybackQueueDialog.xaml.cs:170](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlaybackQueueDialog.xaml.cs:170), [PlayerView.xaml:154](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:154), [TransientPlaybackQueue.cs:101](D:/Code/emby-client-winui3/src/EmbyClient.App/Services/TransientPlaybackQueue.cs:101).

Recommended direction: share a queue-entry presentation contract: title, series plus S/E when applicable, ordinal, and a quiet `Up next` marker. Keep playback context in the side panel's `Now playing` header. Show no guessed runtime, episode number, or artwork.

Evidence classification: source-confirmed information inconsistency. Actual duplicate-title presentation and Narrator announcements are unverified.

### Q4 — P2 unverified accessibility risk: the overlay playback panel leaves covered controls in keyboard order

Below 1040 DIP, the 336-DIP side panel overlays the player instead of occupying a separate column. Opening it focuses its Close button, but the underlying dock controls remain enabled and in the same tree. There is no explicit focus boundary or background tab-order adjustment. At a 640-DIP width, backward traversal from the panel may reach controls physically covered by it. An inline panel does not need to trap focus, so the solution must distinguish inline and overlay modes.

Evidence: [PlayerView.Presentation.cs:139](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.Presentation.cs:139), [PlayerView.Presentation.cs:320](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.Presentation.cs:320), [PlayerView.xaml:119](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:119).

Required native evidence: open Settings and Queue at 640 DIP; traverse Tab and Shift+Tab, including after closing with Escape. Record whether focus ever becomes visually hidden. This is not labeled a reproduced defect.

### Q5 — P2 unverified layout risk: fixed player controls and non-scrolling queue chrome at large text sizes

The +/-10 text buttons are fixed at 36x36 with 8-DIP padding while the text retains automatic scaling. The queue side panel has fixed-width framing, non-scrolling header/tabs/now-playing/actions/toggle, and only a central ListView that scrolls. At minimum window height with 200% text, the fixed regions can consume the available height. Library shelf sizing does account for system text scale, but uses estimates rather than content measurement; that support does not establish whole-app coverage.

Evidence: [PlayerView.xaml:22](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:22), [PlayerView.xaml:96](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:96), [PlayerView.xaml:149](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlayerView.xaml:149), [LibraryView.Accessibility.cs:68](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/LibraryView.Accessibility.cs:68), [LibraryView.Accessibility.cs:97](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/LibraryView.Accessibility.cs:97).

Required native evidence: 640x600 with 200% Windows text size, long episode/series names, a populated player queue, and both dialogs. Check visible/focusable Close, Remove, Clear queue, playback controls, and diagnostics result feedback. Do not disable text scaling to make a crowded design fit.

### Q6 — P3 design opportunity: diagnostics export is successful but unnecessarily difficult to retrieve

`Save snapshot` silently writes to a fixed local directory and replaces the previous `snapshot.json`; the full location and overwrite behavior appear only in the resulting text. This works as local diagnostic storage but is weaker than the user's usual export model. The screenshot also places Environment and five recent events ahead of the actual support actions, making the dialog visually dense for an occasional troubleshooting task.

Evidence: [PlaybackDiagnosticsDialog.xaml.cs:183](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlaybackDiagnosticsDialog.xaml.cs:183), [PlaybackDiagnosticsDialog.xaml:15](D:/Code/emby-client-winui3/src/EmbyClient.App/Views/PlaybackDiagnosticsDialog.xaml:15), screenshot `08-playback-diagnostics.png`.

Recommended direction: lead with current playback status/recent failure when available, keep Environment compact, retain raw snapshot under Technical details, and offer a clear export destination or an `Open folder` action after saving. A native Save As flow with a timestamped default name is another option. Preserve the existing allowlisted snapshot and clipboard protections.

Evidence classification: design opportunity. Save and failure callbacks were inspected, not executed during this review.

## Surface-by-surface conclusions

| Surface/state | Conclusion | Evidence/remaining limit |
|---|---|---|
| Library queue, one item | Fundamentally clear. Native modal, safe Close, explicit order and next marker, secondary editing commands. Keep this hierarchy. | `final-05-queue.png`; button availability and position labels in source. |
| Library queue, many items | Needs native review. Wrapped metadata is good, but the 300-DIP ListView is inside a second scroll viewer; long lists and large text may create nested scrolling friction. | `PlaybackQueueDialog.xaml:10`, `:31`; no many-item capture. |
| Library queue, empty | Clean, quiet, actionable explanation. Editing commands disappear. No loading animation is needed for this synchronous in-memory state. | `final-06-queue-empty.png`; `PlaybackQueueDialog.xaml.cs:70`. |
| Library queue, full/invalid/open failure | Messages explain remediation and preserve media. Successful addition has Q1's compact-mode feedback gap. | `MainPage.xaml.cs:261`, `:335`; failure states not captured. |
| Player queue, populated | Functional editing surface but not presentation-equivalent to the library dialog. Resolve Q3 before further decoration. `Play next` also needs clear head-of-queue scope distinct from the selected row. | `PlayerView.xaml:154-168`; current source only. |
| Player queue, empty | Refine the hierarchy of disabled action chrome and clarify that the count covers explicit entries. Automatic continuation is a separate concept; Next in queue is correctly named for its scope (Q2). | `PlayerView.Presentation.cs:363-374`; no capture. |
| Player queue, advancing/error | Disabling Next during `_advancing` is sensible. Errors route through playback notices; preserve queue state and show recovery near playback. No separate queue spinner is needed for in-memory edits. | `PlayerView.xaml.cs:736-770`; native failure/continuation not checked. |
| Diagnostics, events | Native scrolling, safe Close, readable event grouping, optional technical JSON are appropriate. Reduce information weight, not functionality (Q6). | `08-playback-diagnostics.png`. |
| Diagnostics, no events | Clear explanation; usable copy/export remains possible for the environment snapshot. | `PlaybackDiagnosticsDialog.xaml.cs:73-79`; source only. |
| Diagnostics, snapshot unavailable/storage issue | Refresh path and disabled copy/save for an unavailable snapshot are appropriate. Storage failure still permits an in-memory snapshot. No blocking secondary error dialog. | `PlaybackDiagnosticsDialog.xaml.cs:87-106`, `:205`; source only. |
| Diagnostics, copy/save success/failure | Inline feedback and explicit live-region announcement are good. Copy suppresses clipboard history/roaming. Export retrieval remains Q6. | `PlaybackDiagnosticsDialog.xaml.cs:164-226`; source only. |
| Navigation/title bar | Native title bar integration and narrow overlay pane are sound. Preserve OS caption controls and actual pane toggle. The historical Back and detail's source-library action need distinct wording; they can have different destinations despite similar arrows. | `final-10-narrow-navigation.png`, `MainWindow.xaml.cs:50-63`, `LibraryView.Presentation.cs:103`. |
| Light/dark | Captured neutral surfaces, system accent and rounded native controls form a coherent base. Dark library and dark player are readable in the screenshots. Treat local player-dark surfaces as a deliberate viewing context, not a global dark-theme mandate. | `final-11-dark-series.png`, `final-12-narrow-player.png`, diagnostics capture. |
| Contrast themes | Implementation has theme-resource foundations and replaces fullscreen gradients with solid surfaces in high contrast. Compliance is unverified; dark mode is not a substitute for a contrast theme. | `LibraryView.xaml:55-69`, `PlayerView.xaml:17-19`, `PlayerView.Presentation.cs:145-149`. |
| Keyboard/Narrator | Native controls, explicit names, headings, Delete support, Escape and focus-restoration hooks are positive. Q4 remains; UIA names/live settings alone do not prove complete Narrator behavior. | Queue dialog `.xaml.cs:53-61`, `:119-136`; player presentation `:239-251`, `:347-360`. |
| Large text/narrow windows | Source makes a real effort to adapt library cards; captured narrow states are useful but are not 200% text tests. Q5 requires proof across dialogs and player panels. | `LibraryView.Accessibility.cs`; `final-10/11/12` captures. |
| Product language | Current UI strings are consistently English but are embedded in XAML/C#. No `x:Uid`, `.resw`/`.resx`, or resource-loader infrastructure was found in the app scan. Product-language policy should be decided before localization work; the Chinese discussion alone is not authorization to translate the product. | App source search; examples throughout queue/diagnostics files. |

## Cross-surface visual direction

The strongest improvement is consistency of information, feedback, alignment, and content density. Retain native Segoe UI Variable typography, theme brushes, native focus treatment, CommandBar overflow, ContentDialog safety, and restrained corner radii. Reuse a small hierarchy of title/body/caption and spacing values across queue, diagnostics, detail, and player settings instead of introducing new decorative card treatments per page. System-aligned semantic layers matter more than increasing blur, shadow, or accent coverage.

Library queue and diagnostics do not need cosmetic redesign to qualify as modern Fluent UI. The player queue needs semantics and metadata parity first. Dense technical detail belongs behind expansion or in the diagnostics surface. Feedback should appear where the user acted and be perceivable in both expanded and compact navigation.

## Three highest-value native follow-ups for the root reviewer

1. Collapsed navigation: add an item from a detail menu and observe visual feedback; then play a series with an empty explicit queue and inspect automatic continuation, `0 upcoming items`, and disabled Next.
2. At 640x600 and 200% text size, inspect both queue surfaces and diagnostics with long titles/many entries, then traverse an overlay player panel with Tab/Shift+Tab/Escape. Specifically look for clipped controls, inaccessible action rows, and focus behind the overlay.
3. Switch through a contrast theme while the library, dialogs, and fullscreen player controls are visible; use keyboard and Narrator to inspect queue position/next-item announcements and whether controls stay legible after runtime theme changes. Record any unperformed scenario as unverified.

## Primary Fluent/accessibility references

Retrieved from Microsoft Learn on 2026-09-15 using read-only HTTP after the configured web provider was unavailable.

- [Accessible text requirements](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements): requires readable foreground/background contrast in the default experience, validation with Windows text settings and Narrator, and warns that text scaling is not uniform across all text sizes.
- [Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes): contrast themes differ from light/dark themes; semantic theme resources and system color pairings are appropriate foundations and need runtime evaluation.
- [Dialog controls](https://learn.microsoft.com/en-us/windows/apps/design/controls/dialogs-and-flyouts/dialogs): native ContentDialog supplies safe dismissal and appropriate action placement; a default Close button is permitted, so the captured accent Close buttons are not treated as Fluent violations.
