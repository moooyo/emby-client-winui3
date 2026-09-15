# Media details and people review

Review date: 2026-09-15. Scope: current production source and existing native captures. No production edits, builds, tests, runtime probes, or UI operations were performed by this reviewer. An independent source reviewer checked the season and person-navigation state flows.

Evidence labels: **Native** means an existing actual WinUI capture was inspected; **Source** means current code establishes the behavior; **Inference** means the visual or interaction consequence still requires a native scenario. Design suggestions are review judgments, not claims that a particular Fluent rule mandates one layout.

## Page-by-page conclusions

| Page or state | Conclusion | Evidence and remaining uncertainty |
|---|---|---|
| Movie detail, light | Keep the title, real poster, quiet metadata, one accent playback action, and progressive disclosure. Improve reading width, cast density, and bottom spacing. | Native: `final-02-movie-details.png`. Real photographic backdrops, very long titles, and long overview expansion remain unverified. |
| Series detail, episode list | The default list is useful and playback progress is clear. The list viewport is too tall for a small season and becomes a large inner scrolling region for long seasons. Cast discoverability suffers. Pending-season feedback needs improvement. | Native: `final-03-series-details.png`, `final-15-playback-return-progress.png`; Source: section sizing and season transitions. |
| Series detail, episode cards | Keep as an alternative view. It is visually clearer for artwork browsing but currently reserves substantial extra vertical space and moves cast below the first screen. | Native: `final-08-episode-cards.png`. Horizontal scrolling with many episodes and keyboard focus across the viewport needs confirmation. |
| Series detail, narrow and dark | Basic contrast, native controls, overlay navigation, and content survive the tested narrow window. It is still a desktop poster-and-text layout at the minimum captured width, with unnecessary vertical cost. | Native: `final-09-narrow-series.png`, `final-11-dark-series.png`. The breakpoint interaction around 640-680 outer pixels requires a fresh observation; do not claim a proven resize failure. |
| Season detail | The generic detail implementation can display season episodes, but the containing series is not given a visible link. Empty technical information and overly generic artwork treatment are possible. | Source only. A dedicated current native season capture was not present in the inspected final set. |
| Episode detail | Season/episode numbers are available, but the parent series identity is missing from the visible detail header and cannot be opened directly. A 2:3 artwork slot can crop landscape episode artwork. | Source. Old episode captures predate this implementation and are not proof of current geometry. |
| Overview expanded / collapsed | Native text selection and a trim-dependent Read more / Show less control are appropriate. The reading measure is unbounded, and expanding a long overview shifts every lower section. | Source: `LibraryView.Presentation.cs:117-151`, `LibraryView.xaml:405-406`. Long-content native inspection is outstanding. |
| Cast preview / expanded | Real people, role labels, initials fallback, and optional expansion are sound. Exactly four cards at every width creates a sparse desktop section. Expanded mode introduces another vertical scroller. | Native preview; source expansion. Verification JSON records all six people were expanded in the prior run. No current full expanded-state capture was supplied in the final gallery. |
| Person profile / works | Biography and works load independently, errors are recoverable, and works are scoped to this account. The dialog is suited to quick inspection, but nested scrolling and destruction of browsing context make multi-work exploration cumbersome. | Source plus prior verification record, not a retained final profile screenshot. Long biography and 24+ works are not visually verified. |
| Missing artwork / metadata | Keep native initials and quiet placeholders; do not manufacture portraits or cast. Missing cast/overview text is explicit. Hide an empty technical expander; distinguish lack of playback permission from lack of playable media. | Native missing-portrait preview; source guards and fallbacks. Permission and empty technical states need current native captures. |
| Detail or person loading / failure | Keep initial loading scoped to the content, atomic season commits, and separate biography/works retries. Separate episode availability from optional resume-query latency; make season switching visible near the affected region. | Source. Slow requests, partial failures, expired sessions, and retry ergonomics need targeted native observation. |

## Findings

### D01 — P2 — Episode and season details lose their parent-series context

**Trigger:** Open an episode or season directly from search, favorites, or a home shelf.

**Evidence:** `Views/LibraryView.xaml:378` binds the heading to `Detail.Title`; `ViewModels/MediaCardViewModel.cs:33-40` includes the episode number in metadata but not the series name. `ViewModels/LibraryViewModel.cs:610` stores `SeriesName` in `Subtitle`, but `HeaderVisibility` hides that header on details (`:118`, XAML `:252-269`) and `MainPage.xaml.cs:56` supplies an empty titlebar subtitle outside playback. The only inner back link uses the remembered source library or history (`Views/LibraryView.Presentation.cs:103-134`). There is no parent-series command in this view.

**Impact:** A search result can lead to a detail titled only by the episode name. Users can go back to the source, but cannot move directly to its series or select another season. A season called “Season 1” has even less identity.

**Recommendation:** Add a compact hierarchy such as a linked series name plus season and episode information. Keep history-back separate from the parent-series action. Preserve the selected season when opening the parent series.

**Confidence:** Source; native direct-entry episode and season captures still needed. This is not a claim that the episode number is missing.

### D02 — P2 — Opening a person's work discards the person-browsing context

**Trigger:** Open a cast member, scroll or load more works, open a work, then go back to compare another work.

**Evidence:** `Views/PersonDetailsDialog.xaml.cs:85-89` closes the dialog on work click. `Views/LibraryView.People.cs:33-40` detaches it and navigates to the work. Detach disposes its view model (`Views/PersonDetailsDialog.xaml.cs:58`), including loaded works and biography. `ViewModels/LibraryViewModel.cs:441-452,548` remembers the original media item, not the person dialog or its viewport. A later Detail change also resets page scroll and overview state (`Views/LibraryView.xaml.cs:685-688`; `Views/LibraryView.Presentation.cs:117-124`).

**Impact:** Global Back does return to the original media detail, so the chain is not broken. However, the user must find the cast section and person again, reopen the dialog, reload works, and recover the prior position. The inner back arrow can jump directly to the remembered media library instead (`Views/LibraryView.Presentation.cs:103-114`).

**Recommendation:** Represent person exploration in navigation state, or offer a clear return to the originating person's works with restored list position. Keep the quick dialog for a brief biography if desired; extended work exploration benefits from its own navigable view.

**Confidence:** Source.

### D03 — P2 — A pending season switch temporarily separates the selector from committed playback

**Trigger:** Change the season over a slow connection, then immediately press the main playback or queue action.

**Evidence:** `ViewModels/LibraryViewModel.cs:760-762` changes `SelectedSeason` before the request returns. `Items` and `PlayableDetail` are replaced only at `:772-774`. The main button at `Views/LibraryView.xaml:383-385` has no pending-season disable condition, and `RequestPlay` (`Views/LibraryView.xaml.cs:438`) only checks playability and account permission. The section heading intentionally stays on the committed old season (`ViewModels/LibraryViewModel.cs:127-128`). Since existing items remain present, the loading state maps to the top “Refreshing media” progress bar, not a local episode-loading cue (`ViewModels/LibraryViewModel.Presentation.cs:16-17`; XAML `:451-453`).

**Impact:** The selector names the requested season while the still-actionable primary target belongs to the previous one. The Play label itself still names the old committed season/episode. This is a temporary UX ambiguity, not evidence of a mislabeled playback target, wrong episode playback, or late-response data corruption.

**Recommendation:** Show a local “Switching to Season N” state and temporarily suspend season-dependent main actions, or explicitly retain the committed selector until the change is ready. Continue keeping old content visible and preserving request-owner checks.

**Confidence:** Source; delayed native request confirmation recommended.

### D04 — P2 response-time opportunity — Optional resume lookups delay an otherwise available episode list

**Trigger:** Episode data returns promptly but the resume or next-up endpoint is slow.

**Evidence:** `ViewModels/LibraryViewModel.cs:693-702` starts episodes, resume, and next-up requests together, waits for all three, and only then appends episode items. `:126,678,707` keep season selection disabled during this initialization.

**Impact:** An already available season remains visually loading, and users cannot switch seasons while an optional recommendation is unresolved.

**Recommendation:** Publish the completed episode list under the existing page/request checks, then update the recommended primary playback target when resume/next-up data arrives. Keep one understandable primary playback state while its recommendation is unresolved.

**Confidence:** Source; latency impact depends on the server.

### D05 — P2 — Playback restrictions are hidden rather than explained

**Trigger:** An account can view metadata but `EnableMediaPlayback` is false, or a known virtual item has no playable media.

**Evidence:** `ViewModels/LibraryViewModel.cs:136-140` collapses the main playback action. `Views/LibraryView.xaml:398-399` collapses playback and queue menu commands by the same condition. The permission explanation in `Views/LibraryView.xaml.cs:441` is only reached by requesting playback; ordinary hidden commands cannot reach it. Virtual items similarly fail `CanPlay` (`ViewModels/MediaCardViewModel.cs:69-70`).

**Impact:** The hero silently loses its main action. Users cannot distinguish account restrictions, unavailable media, and a missing feature.

**Recommendation:** Place a short, state-specific explanation in the action region, with either a disabled native play control or a quiet message. Retain favorite/watched actions where permitted. Do not present retry as a cure for a known policy restriction.

**Confidence:** Source. The permission and virtual-item cases are distinct and should have distinct copy.

### D06 — P3 — Media information can expand into nothing, and director names are unlabeled technical content

**Trigger:** A series, season, or sparse item has no media streams and no directors; alternatively, it has only a director.

**Evidence:** `Views/LibraryView.xaml:443` always displays the expander. Both children at `:445-446` collapse independently. `ViewModels/MediaCardViewModel.cs:46-65` generates either a raw director-name string or a combined resolution/codec/audio string.

**Impact:** An empty disclosure is a dead interaction. When only names are shown, their meaning is unclear under “Media information,” and crew information is separated from Cast and crew.

**Recommendation:** Hide the expander when it has no data. Present technical rows with small clear labels when expanded. Put director identity with crew information, or label it explicitly if retained here.

**Confidence:** Source. The fixture series may be used to verify the empty case because its synthetic people are all actors.

### D07 — P2 design opportunity — Multiple vertical viewports add detail and person-browsing costs

**Trigger:** A season has more than five episodes, expanded cast exceeds its maximum height, or a person has several rows of works.

**Evidence:** The outer detail scroller is vertical (`Views/LibraryView.xaml:342-343`), with a second vertical GridView (`:426-432`). Its height is capped at five times 136 DIP before text scaling (`Views/LibraryView.Accessibility.cs:97`). Cast has another 448-DIP vertical viewport (`Views/CastSection.xaml:16-21`). A person dialog has an outer vertical scroller plus a 432-DIP vertical works grid (`Views/PersonDetailsDialog.xaml:9,38-43`), while “Load more” is outside that grid (`:69-70`).

**Impact:** The scroll target changes with pointer position. Large episode sections defer cast; long biography plus inner works scrolling separates the pagination command from the list's own scroll position. These are interaction costs, not a blanket claim that nested scrollers violate Fluent.

**Recommendation:** Prefer a clearly owned scrolling surface for each browsing task. Options include a virtualized unified details surface, a bounded episode preview with an explicit full-season view, or a dedicated person-works page. Preserve virtualization; do not replace it with an unbounded nonvirtualized list just to remove the nested scroller.

**Confidence:** Source + Native for the tall two-item section. Wheel chaining, keyboard focus, and pagination behavior need direct observation.

### D08 — P2 design opportunity — Fixed vertical reservations and unbounded overview width reduce the content-to-space balance

**Trigger:** Normal 100% text with short titles, short summaries, and a small season; wide movie detail windows.

**Evidence:** Native `final-02` shows four compact people followed by a large gap before Media information. `final-03` and `final-15` show a substantial gap after only two episode rows; `final-08` pushes Cast and crew almost beyond the first screen. Source reserves a 288-DIP hero minimum (`Views/LibraryView.xaml:348`), a cast cell of 176 DIP (`Views/CastSection.xaml:21`), and episode list/card heights independent of actual measured content (`Views/LibraryView.Accessibility.cs:97-98`). `OverviewText` stretches with the remaining hero column and has no reading-width cap (`Views/LibraryView.xaml:377-405`).

**Impact:** Users scan fewer useful sections in a laptop window, and long overview lines are harder to read. This makes the UI feel unfinished despite using native controls.

**Recommendation:** Size compact content from its actual image and text requirements, then apply consistent section spacing. Cap the overview's reading measure while retaining a full-width hero surface. Keep sufficient height for real long titles and system text scaling; do not solve this by reducing font size.

**Confidence:** Native + Source. Exact revised dimensions require realistic content, not only the synthetic fixture.

### D09 — P3 — The cast preview ignores available width

**Trigger:** A wide desktop window with more than four people.

**Evidence:** `Views/CastSection.xaml.cs:77-80` always displays exactly the first four people and uses a fixed label describing four. The 132-DIP item width comes from `Views/CastSection.xaml:21`. Native `final-02` leaves nearly half the cast row unused while the “View all (6)” action is at the far edge.

**Impact:** The section appears sparse and unnecessarily withholds people already available from the server. On narrower widths, a fixed count can instead wrap into a partially filled second row.

**Recommendation:** Preview one complete row based on usable width, with a sensible maximum. Preserve actor-first ordering and an explicit all-people action. Expanded people can group cast and crew roles if the data volume warrants it.

**Confidence:** Native + Source. This is an intentional refinement of the previously approved four-person default, not a claim that it was incorrectly implemented.

### D10 — P2 — Episode artwork is forced into the movie poster geometry

**Trigger:** An episode's primary image is a landscape frame or only a parent thumb is available.

**Evidence:** Every detail item uses the 160-by-240 poster frame and `UniformToFill` (`Views/LibraryView.xaml:370-374`). The general poster image selection prefers Primary then parent thumb (`Services/ImageCache.cs:176-180`), whereas the landscape selector explicitly understands episodes (`:181-188`).

**Impact:** Landscape frames can be cropped aggressively inside a portrait slot, and episode detail lacks a visual composition tailored to its content type.

**Recommendation:** Use episode-appropriate landscape artwork or a deliberate parent-series poster with clearly identified episode text. Preserve real image aspect ratios in the selected presentation.

**Confidence:** Source + Inference. Requires a current native episode capture with a real landscape image to judge the severity of cropping.

## Native checks still needed before the next implementation decision

1. **Breakpoint interaction:** The window minimum is 640 (`MainWindow.xaml.cs:16`), NavigationView switches at 640 with a 48-DIP rail (`LibraryView.xaml:241`), and hero reflow tests its own width against 620 (`LibraryView.xaml.cs:287-295`). The captured 634-pixel narrow client retains the large poster. Changing outer width just above the navigation threshold may reduce content width and cause counterintuitive reflow before it expands again. This is a hypothesis, not a confirmed defect.
2. **Long title, long resume label, and enlarged text:** The horizontal hero action StackPanel does not wrap (`LibraryView.xaml:382-404`) and the hero is clipped (`LibraryView.xaml.cs:286`). Verify that the main action, favorite, and More remain reachable at the supported minimum width and larger system text. Do not infer accessibility success from ordinary 100% text screenshots.
3. **Person dialog at the minimum window:** Long biography, many works, repeated Load more, no works, biography-only failure, works-only failure, permission failure, and no person ID. A person without an ID still looks clickable in the GridView although the handler does nothing (`CastSection.xaml.cs:89-99`; `PersonCardViewModel.cs:26-27`); determine whether a visible unavailable-profile treatment is needed.
4. **Return flow:** Search or Home -> episode -> parent series; movie -> person -> work from another library -> global Back and inner back link. Record the expected destination, selected navigation item, and restored scroll/focus.
5. **Season latency:** Fast episode response with slow resume/next-up; pending new season with old content visible; rapid changes followed by error. Confirm presentation while preserving the existing correct cancellation and atomic-commit behavior.
6. **Read-more and empty technical state:** Very long overview, no overview, no media streams, no director, and director-only metadata. Check expansion/collapse scroll and keyboard focus.
7. **Cast mutation / refresh:** `CastSection.xaml.cs:63,76-77` clears/recreates VisiblePeople when Item changes; a user-data update raises Item (`MediaCardViewModel.cs:138-139`). Confirm whether a background update or favorite action resets the expanded cast's inner viewport or flashes loaded portraits. This consequence is not yet confirmed.
8. **Real content visual review:** Bright/dark photographic backdrops, portrait and landscape source ratios, long international names, missing fields, and meaningful ratings. Current synthetic captures prove structural rendering, not final art direction across a real library.

## Confirmed strengths and already resolved items

- Real Emby people are used; the synthetic actors belong only to the loopback fixture. No production sample people are proposed.
- PersonPicture is explicitly sized to its slot (`PersonArtwork.xaml.cs:86-91`). The old `issue-person-portrait-clipping.png` is a repaired issue, not a current finding.
- Missing person images keep initials; missing work art keeps a quiet native placeholder. Image request ownership, cancellation, and unload guards are present.
- Person biography and works load independently (`PersonDetailsViewModel.cs:78`) and have separate failure/retry UI. Works use account-scoped PersonIds, 24-item pages, duplicate protection, and a HasMore calculation (`:120-139`).
- Season changes validate page/request ownership and cancellation before committing (`LibraryViewModel.cs:768-769`); failures restore the committed selection (`:789-794`). Do not report stale-season overwrite as an open bug.
- Opening a person's work checks that the source detail is still current (`LibraryView.People.cs:36`), and session cancellation closes/disposes the dialog.
- Playback return updates watched/resume status visibly in the final native capture. That behavior should remain intact during further visual changes.
- Overview text is selectable, and Read more is shown only when text is trimmed or already expanded.
- High-contrast resources explicitly remove decorative backdrop interference. A full high-contrast and screen-reader pass remains outside the observed evidence.
