# Browse-page UI and UX review

Date: 2026-09-15. Scope: Home, My media, Continue watching, Next up, Recently added, media-library collections, Favorites, Search, and their loading/error/empty states.

This is a read-only implementation review. No production files were changed, and no tests, builds, or UI interactions were run by this reviewer. Existing test source was inspected but was not treated as proof of native rendering. All paths below are relative to `D:/Code/emby-client-winui3`.

## Evidence and limits

- Current native captures reviewed: `artifacts/ui-implementation-verification/native-20260914/screens/final-00-library-loading.png`, `final-01-home.png`, `final-04-search.png`, and `final-07-search-focus.png`.
- Older native capture reviewed for collection composition only: `05-movie-library.png`. Its navigation-selection state is not treated as evidence of the current implementation.
- Source reviewed: `Views/LibraryView.xaml`, `LibraryView.xaml.cs`, `LibraryView.Browsing.cs`, `LibraryView.Accessibility.cs`, `LibraryView.Presentation.cs`, `ViewModels/LibraryViewModel.cs`, `LibraryViewModel.Presentation.cs`, `MediaCardViewModel.cs`, `MediaShelfViewModel.cs`, and `Services/PosterWallLayout.cs`, all under `src/EmbyClient.App`.
- Existing captures use a very small synthetic catalog. They do not establish performance or final aesthetic quality with genuine photography, many libraries, long titles, mixed media, large catalogs, or missing metadata.
- Additional native evidence from the integration reviewer corroborates B4: `artifacts/ui-ux-review-20260915/native/03-favorites-request-failed.png` and `.txt`, followed by `04-failed-favorites-dismissed-as-empty.png` and `.txt`. Both screenshots and UI Automation text were inspected. The integration reviewer used the isolated review fixture on port 18977 with a single next-item-request HTTP 503 failure.
- Additional native evidence corroborates B1's lost scroll position: the 200-item review library was scrolled to movies 000041-000050, movie 000046 was opened, and the actual title-bar Back button returned to movie 000001 at the top. Evidence: `native/08-refresh-during-pagination.png`, `native/09-deep-item-details.png` and `.txt`, and `native/10-history-back-resets-list.png` and `.txt`, under the review artifact directory. The screenshot named `08-refresh-during-pagination` establishes the prior viewport only; it does not establish B3's pagination race.
- No available capture proves Favorites populated or successfully empty, no search results, a zero-result watch filter, background refresh, a next-page failure, or the late-search focus race. These are explicitly separated from visually observed findings.
- Priority: P2 = material usability or information issue; P3 = visual or minor interaction refinement. No P0/P1 issue was established in this scope.

## Page conclusions

| Page or surface | Conclusion | Evidence and required follow-up |
| --- | --- | --- |
| Home | The native shell and content hierarchy are coherent. The page spends too much first-screen height on library navigation and reserved shelf space before useful viewing content. Duplicate in-progress content can weaken the distinction between Continue watching and Next up. | `final-01-home.png`; XAML 278-292 and 216-234; Accessibility 68-72. Continue watching starts around y=320 in the 1268x834 capture; Recently added is below the first screen. This is a design opportunity, not an overflow bug. |
| My media | Quiet native tiles are an appropriate replacement for decorative library banners. Keep them as navigation, with a compact layout and a clear overflow method. | XAML 144-154 and 281-290; Accessibility 115-119. Tiles are 220x64 DIP at normal text scale, while their viewport reserves 92 DIP plus a 24 DIP bottom margin. Every tile currently uses the same library glyph; media-type glyphs would improve recognition but are optional. |
| Continue watching shelf | Series identity, episode identity, and progress are available in the correct order. The shelf is visually useful but taller than its ordinary one-line content requires. | `final-01-home.png`; XAML 125-140 uses `DisplayTitle`; MediaCardViewModel 15-22 and 85-88. No direct playback action is exposed on a card: it always opens details. This is a possible intentional design choice, not a broken control. |
| Continue watching full collection | Server ordering is preserved and unsupported sorting is correctly omitted. The universal poster template loses series context for episodes, and back navigation loses the deep browsing position. | LibraryViewModel 805-812, Presentation 25-26; XAML 321; findings B1/B2. Native full-collection coverage is missing. |
| Next up shelf | The role is understandable only when it differs from Continue watching. The reviewed fixture displays the same series/episode in both shelves. | `final-01-home.png`; Presentation 137-143 and 170-178 independently add both results without cross-shelf deduplication. Distinguish a resumed episode from the next untouched episode, while retaining access to all items. |
| Next up full collection | Keeping server order is correct. Episode identification needs the same correction as other poster-based episode lists. | LibraryViewModel 809-810; findings B1/B2. Native full-collection capture is missing. |
| Recently added | Per-library sections have a useful scope and clear headings. Initial Home load waits for every shelf, and See all changes the query's grouping semantics. | Presentation 145-167; LibraryViewModel 344-350 and 815-828. Native capture with enough recent items and episodes is needed before deciding on grouping behavior. |
| Media library collection | The responsive portrait wall, two-line titles, filter labels, and native controls are a sound base. Main gaps are return-context restoration, episode labels, and state transitions. | Older `05-movie-library.png`; current XAML 299-338; PosterWallLayout 12-30. Do not mistake sparse test data for excessive grid spacing: the wall intentionally retains stable poster sizing instead of stretching two posters to fill the whole width. |
| Favorites | Reuse of the collection pattern is consistent. The empty-state instruction gives a real recovery path. Dismissing a first-load failure incorrectly claims there are no favorites. | LibraryViewModel 428-431 and Presentation 73; finding B4, corroborated by native screenshots/UI Automation `native/03` and `native/04`. Populated and successfully empty native states remain missing. |
| Search results | Input focus is visible and Enter-based submission is explicitly described. The centered input breaks alignment with the title, count, filters, and results; mixed episode results repeat the episode title and omit the series name. | `final-04-search.png`; XAML 301-303; findings B2/B6. |
| Search before submission | The quiet entry state is appropriate: it does not show a spinner or pretend there are zero results. The typed draft is intentionally separate from the submitted query. | `final-07-search-focus.png` shows a typed but unsubmitted draft. LibraryViewModel 120, 555-556 and 583; XAML.cs 305-307. An instruction to press Enter should be visible as well as present in automation help if discoverability testing warrants it. |

## Findings

### B1 — P2, source-confirmed UX defect, lost position natively corroborated: Back loses the collection position and item focus

Trigger: browse far down a library, search result, Favorites, Continue watching, Next up, or Recently added; open an item; use Back.

Native reproduction supplied by the integration reviewer: in the 200-item review library, the viewport displayed movies 000041-000050. Opening movie 000046 and then clicking the real title-bar Back button (rather than the page's library shortcut) returned to movie 000001 at the top. The inspected before/after screenshots and UI Automation snapshots corroborate the lost position. Focus restoration remains established by the source path below rather than a separate keyboard-focus reproduction.

- Before: `artifacts/ui-ux-review-20260915/native/08-refresh-during-pagination.png` and `.txt`.
- Item detail: `artifacts/ui-ux-review-20260915/native/09-deep-item-details.png` and `.txt`.
- After title-bar Back: `artifacts/ui-ux-review-20260915/native/10-history-back-resets-list.png` and `.txt`.

`GoBackAsync` navigates to the saved `BrowseLocation`, which stores sort/filter/season identifiers but no loaded page range, scroll anchor, or focused item. `NavigateAsync` clears the collection and resets `_nextIndex`. The view receives Reset and explicitly scrolls the wall to zero. `FocusBrowseDestination` then focuses the grid as a whole rather than the originating item.

- `ViewModels/LibraryViewModel.cs:447-452`, `:558`, `:568`, `:971-973`.
- `Views/LibraryView.xaml.cs:86-91`.
- `Views/LibraryView.Browsing.cs:18-27`.
- `Views/LibraryView.Accessibility.cs:137-143`.

Impact: browsing several items in a large catalog repeatedly costs scrolling, loading, and reorientation. Filter restoration itself is present; this finding is specifically about position and focus. Preserve a stable item anchor and the loaded window per history entry, with a sensible fallback if an item is removed. The native deep-scroll reproduction confirms the visible loss of position for media-library history navigation.

### B2 — P2, source-confirmed information defect: Poster-based episodes lose their series name

Trigger: an Episode appears in Search, Favorites, or a full Continue watching/Next up collection.

The shared poster template binds its first line to `Title`, which is the episode name. Its caption binds to `CardSubtitle`, which for episodes is the episode number followed by the same episode name. `DisplayTitle`, which would supply the series name, is used by the Home landscape template but not by the poster wall.

- `Views/LibraryView.xaml:119` and `:120` contain the poster title/caption bindings; the template starts at `:100`.
- `Views/LibraryView.xaml:138` uses `DisplayTitle` for the landscape template.
- `Views/LibraryView.xaml:321` fixes the full collection to `MediaCardTemplate`.
- `ViewModels/MediaCardViewModel.cs:14-22`.

Impact: episodes with common names such as Pilot cannot be reliably distinguished by visible text. Use series title plus episode number/name for episode cards; preserve movie title/year for movies. The automation name already includes `DisplayTitle` (`LibraryView.xaml.cs:342-345`), so visual and spoken information currently differ. Native mixed-episode coverage is needed.

### B3 — P2, source-confirmed asynchronous defect: Refresh can permanently stall automatic pagination

Trigger: automatic Load more is in flight; the user refreshes before the next page completes; the existing item count remains unchanged when the canceled old operation returns.

`RefreshAsync` cancels the old page lifetime, intentionally retains existing items, and commits new data without a collection reset. `LoadMoreAsync` treats cancellation as a normal return. Its view caller treats every normal return with no added items and no error as a repeated/stalled page and sets `_autoLoadStalled = true`. That flag is cleared only by `ResetCollectionScroll`; a successful in-place refresh does not trigger that method.

- `ViewModels/LibraryViewModel.cs:257-284`, `:299`, and `:480-506`.
- `Views/LibraryView.Browsing.cs:18-22`, `:146-147`, and `:158-165`.
- `ViewModels/LibraryViewModel.Presentation.cs:334-340`.
- `Views/LibraryView.xaml.cs:679` resets only on `CollectionRevision`; refresh does not increment it.

Impact: reaching the bottom can stop loading further pages until a navigation/filter operation resets the view. Canceled or superseded operations must not be classified as a server page that returned no new content. Preserve the safety guard against duplicate-page loops, but key it to the operation owner and outcome. A delayed-next-page/Ctrl+R native or targeted state reproduction is outstanding; this is a source-established execution path, not a measured incidence rate.

Integration evidence note: attempts with 5-second and 20-second pagination delays missed the in-flight cancellation window. A third Ctrl+R attempt was blocked by the tool's user-input guard. Neither `08-refresh-during-pagination.png` nor `12-cancel-pagination-with-refresh.png` proves successful cancellation or the stall. The filenames are not evidence of those operations having succeeded. B3 remains source-confirmed, not native-reproduced.

### B4 — P2, source-confirmed and natively corroborated state defect: Dismissing a first-load error produces a false empty state

Trigger: the first request to Favorites, a library, or a submitted Search fails; the user closes the error InfoBar.

Native reproduction supplied by the integration reviewer: the isolated review fixture returned HTTP 503 for the first Favorites items request. The app displayed Unable to load media and Retry. Closing the InfoBar immediately displayed "No favorites yet. Open an item and choose Add favorite." without a successful query in between. The before/after screenshots and matching UI Automation snapshots were inspected:

- `artifacts/ui-ux-review-20260915/native/03-favorites-request-failed.png` and `.txt`.
- `artifacts/ui-ux-review-20260915/native/04-failed-favorites-dismissed-as-empty.png` and `.txt`.

The native result corroborates Favorites; the corresponding library/search behavior follows the shared source path and was not independently reproduced in this review.

The InfoBar's open state is bound two-way to `HasError`. Dismissal sets `HasError` false. `EmptyVisibility` then becomes visible whenever the page is idle and has no items, without checking that a data request completed successfully. The empty copy consequently claims "No favorites yet", "No matching items", or "No items are available" after a transport or permission failure.

- `Views/LibraryView.xaml:273-275`.
- `ViewModels/LibraryViewModel.cs:120-121`, `:564-570`, and `:644-655`.
- `ViewModels/LibraryViewModel.Presentation.cs:63-76`.

Impact: users can mistake a failed request for an empty server or absent media. Track confirmed empty results separately from a dismissed error and keep a quiet retry surface for an unresolved first load. Existing content retained after a failed refresh does not have this exact problem.

### B5 — P2, design opportunity: Home should allocate its first screen to viewing intent

The current native frame spends the top section on Home title/subtitle, My media heading, and library tiles, placing Continue watching near y=320. Fixed shelf height and section margins leave roughly 80 DIP between the short Continue watching caption and the next heading in the fixture. The same in-progress series/episode appears again under Next up.

- Native: `final-01-home.png`.
- `Views/LibraryView.xaml:217`, `:252`, and `:280-292`.
- `Views/LibraryView.Accessibility.cs:51` and `:68-72`.
- `ViewModels/LibraryViewModel.Presentation.cs:137-143`, `:170-178`.

When viewing history exists, favor Continue watching near the top, compress the redundant library navigation surface, and reduce only the shelf's excess reserved space. Keep two-line title capacity; eliminating it would destabilize card alignment and harm long-title readability. Define deduplication by item identity and viewing state, not by series name alone, so distinct next episodes are not accidentally hidden.

### B6 — P3, visually confirmed design opportunity: Search uses competing alignment axes

`SearchBox` is stretched but capped at 640 DIP, making it centered within the broad content region; the title, count, filters, and results all remain left aligned. This is visible in `final-04-search.png` and `final-07-search-focus.png`.

- `Views/LibraryView.xaml:299-304`.

Align the search field to the collection's left edge, then use its maximum width to keep the input readable. Consider placing count and filters into a compact header rhythm without changing native control semantics.

### B7 — P2, conditional performance opportunity: One slow Home shelf delays every initial shelf

`QueryHomeShelvesAsync` limits concurrency to three and awaits every shelf through `Task.WhenAll`. Only then does initial navigation call `ApplyHomeShelves`. With many libraries or one slow endpoint, Continue watching may already be available but remains undisplayed until the slowest shelf completes.

- `ViewModels/LibraryViewModel.Presentation.cs:132-167`.
- `ViewModels/LibraryViewModel.cs:659-663`.

This is not a measured local performance failure. Consider progressive initial presentation for independent shelves, while retaining atomic background-refresh behavior where stable existing content is more important. Native verification should include several libraries and one slow latest-items request.

### B8 — P2, native verification risk: A completed search can steal focus from a user's next action

After awaiting any submitted search, the handler unconditionally calls `FocusSearchInput`. The later guard proves that Search is still open, not that focus still belongs to that submission or that the user has not intentionally focused sorting, results, or another control.

- `Views/LibraryView.xaml.cs:310-315`.
- `Views/LibraryView.Presentation.cs:63-82`.

Reproduce with a slow search: press Enter, then Tab to a filter before the request completes. Existing capture proves initial input focus, not this race. If reproduced, restore input focus only when the initiating focus ownership remains valid.

### B9 — P2, native accessibility risk: Reused cards may retain an old automation name

The view sets a concatenated `AutomationProperties.Name` during `ContainerContentChanging`. Background refresh and user-data mutations deliberately retain the same card and container while updating properties. The static automation string is not visibly subscribed to those changes, even though text/progress bindings update.

- `Views/LibraryView.xaml.cs:340-345`.
- `ViewModels/LibraryViewModel.Presentation.cs:88-105`.
- `ViewModels/MediaCardViewModel.cs:101-156`.

Verify the realized container's UI Automation name after a refreshed title, resume progress, or favorite status changes. Do not claim a Narrator failure without native inspection. Also verify that result-count changes are announced: the header count is not a declared live region, unlike the Loading more footer (`LibraryView.xaml:268-270`, `:329-331`).

### B10 — P3, semantic consistency opportunity: Recently added changes grouping after See all

Home requests latest items per library with `GroupItems = true`; See all uses a generic recursive item query filtered to several media types and sorted by DateCreated. Thus the full collection may contain individual episodes or other entries that are represented as grouped cards on Home.

- `ViewModels/LibraryViewModel.Presentation.cs:149-153`.
- `ViewModels/LibraryViewModel.cs:344-350` and `:815-828`.

Review native results containing several newly added episodes from one series. Decide explicitly whether See all expands groups or preserves the Home grouping; the UI should make the selected behavior predictable. This is a conditional information-architecture issue, not a proven server API defect.

## State-by-state conclusions

| State | Conclusion | Evidence |
| --- | --- | --- |
| First library load | Keep the centered ProgressRing and quiet label. No decorative loading banner is needed. Native capture shows the intended pattern. | `final-00-library-loading.png`; XAML 458-460; Presentation 14-19. |
| Initial Home shelves after libraries resolve | My media can display while a thin progress bar remains. Independent shelves are still delayed by the slowest request; see B7. | Presentation 14-17, 132-167. |
| Background refresh | The architecture is good: retain cards, stage data, then reconcile only for the current page/session/season owner. Failure keeps the previous content and explicitly says so. Preserve this behavior. | LibraryViewModel 257-317; Presentation 195-212, 334-340. Existing test source covers atomicity and cancellation; native motion/scroll stability was not observed here. |
| Filter pending | Controls show the pending choice while the old result set remains visible and a thin progress bar indicates activity. This prevents blank flashes, but a quiet Updating results label could better explain that the visible cards still belong to the previous query. | LibraryViewModel 367-403; Presentation 17; XAML 451-453. This is a clarification opportunity, not incorrect final filtering. |
| Successful filter | New results commit together, count/options update, and scrolling resets intentionally. This is appropriate for a new query. | LibraryViewModel 393-403. |
| Failed filter | The previous query, cards, and pagination survive; controls revert in finally. This is preferable to retaining a selected filter that never completed. | LibraryViewModel 405-417; existing `LibraryBrowseTests.cs:38`. |
| Watch filter yields zero results | The filter remains visible and a Clear watch filter action preserves sorting. Keep this behavior. | Presentation 22-29; LibraryViewModel 156-157; XAML 337-338; existing `LibraryBrowseTests.cs:80`. Native zero-state still needed. |
| Loading more | Existing cards stay visible, a footer explains the request, and a top thin bar also animates. The two indicators are redundant but harmless. More importantly, fix B3's canceled-operation stall before visual refinement. | XAML 329-331, 454-456; LibraryViewModel 480-506; Browsing 144-171. |
| Next-page failure | Previous cards and `_nextIndex` are preserved; automatic requests stop while HasError is true. Retry currently refreshes the whole loaded window rather than just the failed page. This is recoverable but can be slower for long sessions. | LibraryViewModel 480-506; Browsing 146-147; XAML 275. Native failure/retry needed. |
| No query / unsubmitted draft | No spinner, no false zero-result message, and a useful Search subtitle are correct. `SearchEmptyVisibility` and a stale sidebar-based message remain unused and are not reported as visible defects. | LibraryViewModel 120, 150, 555-556, 583; Presentation 74. |
| Submitted query has no results | Tailored copy suggests a shorter or different title; retaining the input is correct. Watch filters remain recoverable when active. | Presentation 66-75; XAML 301, 334-338. Native empty-search capture needed. |
| Empty Favorites | Copy points to the actual Add favorite action. A small illustration is optional; an invented banner or promotional content is unnecessary. | Presentation 73. B4 must distinguish network failure from confirmed emptiness. |
| Empty Home | Libraries count as useful Home content. With accessible libraries but no shelves, showing just My media is reasonable. With no libraries or shelves, the current instruction to browse a library is not actionable; prefer an account/server-aware explanation. | Presentation 70, 190; LibraryViewModel 120-121. Native no-library account coverage missing. |
| Error | Native InfoBar plus Retry is appropriate. Failed-first-load dismissal incorrectly becomes an empty state; see B4. | XAML 273-275; LibraryViewModel 900-918; corroborated in native Favorites captures `03` and `04`. |

## Preserve during any future revision

- Native NavigationView, native ComboBox controls, native focus visuals, and theme resource brushes.
- Two-line poster titles, title/subtitle tooltips, and media-type-specific metadata.
- Server-authoritative ordering for Continue watching and Next up rather than fake local sort controls.
- Stable object identity, retained content during refresh, cancellation/version ownership, and paging deduplication.
- Clear watch-filter recovery and successful-filter commit semantics.
- Quiet content-area loading, explicit failures, and progressive image loading.

## Native follow-up requested from the integration reviewer

1. Completed for position: a 200-item catalog returned from item 000046 to item 000001 after the actual title-bar Back action. Keyboard-focus restoration can be inspected separately.
2. Delayed next page, then Ctrl+R, then scroll again; confirm automatic loading resumes.
3. Completed for Favorites: failed first request, followed by InfoBar dismissal, natively corroborated the false empty state. Library/search remain source-confirmed through the shared path and can be checked for coverage.
4. Episode results from two series with the same episode name; inspect visible series identity.
5. Slow submitted search, then Tab to filtering; inspect focus after completion.
6. Favorites populated/empty, search empty, and active watch filter with zero results.
7. Large text and UI Automation on reused cards after refresh; verify name and result-count announcements.
8. Several libraries and a slow Home shelf; compare first useful content timing and Recently added grouping.

No further implementation is proposed as already authorized by this review. These conclusions are intended to support a separate decision on the next revision.
