# Fluent library UI

This revision restructures the browsing interface around playback and the user's Emby library. The supplied web screenshots inform content hierarchy and artwork proportions. The implementation uses native WinUI 3 navigation, controls, theme resources, keyboard focus, and scrolling.

## Structure

| Surface | Behavior |
| --- | --- |
| Navigation | Home, Favorites, and server-provided libraries; queue and account menu stay at the bottom. |
| Account menu | Appearance uses native mutually exclusive radio menu items with the current theme selected. Playback diagnostics, account switching, and sign-out remain available. Compact navigation keeps icon access to both footer actions. |
| Home | Library tiles, landscape continue-watching and next-up shelves, and recent poster shelves for each library. Shelves use native horizontal scrolling; a separate See all action opens the complete collection. |
| Library and search | Virtualized adaptive poster wall with title/year/date-added sorting, direction, and unplayed filtering. Scrolling near the end loads the next page automatically, without a Load more button. |
| Movie and episode details | Backdrop, poster, title, rating/year/runtime, primary play or resume action, favorite and watched-state actions, overview, format summary, directors, and available cast images. |
| Series details | Season selection, landscape episode cards, and a primary action that identifies the episode to resume or play. Empty seasons keep the season selector accessible. |

Server administration, metadata editing, media deletion, and the external-player plugin controls in the references are outside this interface. Playback source, audio, and subtitle selection remain in the existing player.

## Visual rules

- Native Segoe typography: 32 DIP page/detail titles, 20 DIP shelf titles, 14 DIP card titles, and 12 DIP supporting metadata.
- A 232 DIP expanded navigation pane and the native compact width.
- A 28–32 DIP content inset, 8 DIP artwork corners, restrained native control surfaces, and the system accent for primary actions and progress.
- Home uses a 156 × 234 DIP poster, 272 × 153 DIP landscape card, and 220 × 124 DIP library tile. Poster-wall columns share the available width while retaining the 2:3 artwork ratio.
- Detail text and poster use the same continuous vertical scroll surface as episodes and cast. Below 760 DIP of detail content width, the poster is hidden to leave readable space for actions and description.
- Light and dark artwork veils follow the application's appearance setting. High contrast adds an opaque system window-color layer behind detail text.
- Home and detail shelves use native horizontal scrollbars, touch and touchpad gestures, and keyboard navigation. Custom left/right buttons do not duplicate the scrollbars. See all is a separate navigation action, not a scroll control.
- Sorting and filtering retain the header, toolbar, scroll container, and current results while loading. Progress uses a reserved area, so its appearance does not shift the page. Results are replaced in place when ready.

## Data and lifetime

Home requests at most 16 items per shelf with at most three concurrent metadata requests. Successful shelves survive failures in other requests. Navigating, searching, switching accounts, and changing seasons invalidate obsolete responses.

Artwork selection distinguishes Poster, Landscape, and Backdrop. Landscape cards prefer appropriate episode artwork, thumbnails, or backdrops; a detail backdrop does not fall back to a portrait poster. Existing authenticated cache isolation, four-download concurrency, bounded byte storage, container realization ownership, cancellation, and old-result rejection are retained. Resetting a collection retires its images without clearing unrelated library tiles or the detail hero.

The main poster wall keeps a bounded viewport and virtualized GridView. Home and detail shelves use bounded horizontal viewports. Incremental loading appends the next page without moving the existing content. No complete-library image or item preload is introduced.

## Interaction guidance

[Windows scrolling guidance](https://learn.microsoft.com/en-us/windows/apps/design/controls/scroll-controls) recommends built-in collection scrolling and documents the input-aware native scrollbar. The [panning guidelines](https://learn.microsoft.com/en-us/windows/apps/design/input/guidelines-for-panning) recommend hiding scroll indicators when custom navigation replaces them. This interface instead keeps native scrollbars as its single shelf-scrolling control and separates navigation to the full collection.

[Windows progress guidance](https://learn.microsoft.com/en-us/windows/apps/design/controls/progress-controls) recommends a single progress bar above a loading virtualized collection. The reserved loading area applies that guidance without changing the surrounding layout. [RadioMenuFlyoutItem](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.radiomenuflyoutitem) provides native mutually exclusive choices for the appearance menu.

## Broader control audit

The broader control audit also corrected the following surfaces:

| Area | Correction | Guidance |
| --- | --- | --- |
| Text scaling | Auto-sized text rows, text-scale-aware poster and shelf height, scalable library tiles, and no fixed paragraph line height | [Text scaling](https://learn.microsoft.com/en-us/windows/apps/design/input/text-scaling) |
| High contrast | Container foreground inheritance for list captions; system color pairs over library artwork | [High contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes) |
| Navigation | Parent selection retained on detail routes, meaningful focus after back navigation, and semantic section headings | [Landmarks and headings](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings) |
| Window chrome | App theme propagated to the title bar; fullscreen affordance driven by the actual presenter state | [Title bar](https://learn.microsoft.com/en-us/windows/apps/design/controls/title-bar) |
| Player | Immediate autoplay switch, accessible tooltips, stable tabular time fields, and secondary playback settings in a native flyout | [Tooltips](https://learn.microsoft.com/en-us/windows/apps/design/controls/tooltips), [Toggle switches](https://learn.microsoft.com/en-us/windows/apps/design/controls/toggles) |
| Dialogs | Native diagnostic action buttons, dynamic queue command overflow, wrapping text, and operation-status announcements | [Dialogs](https://learn.microsoft.com/en-us/windows/apps/design/controls/dialogs-and-flyouts/dialogs), [CommandBar](https://learn.microsoft.com/en-us/windows/apps/design/controls/command-bar) |
| Feedback | Field-level sign-in errors and stable connecting progress; ordinary queue additions update the queue count instead of inserting a persistent banner | [InfoBar](https://learn.microsoft.com/en-us/windows/apps/design/controls/infobar) |

## Verification boundary

Verification follows the task's execution permissions. The Linux test environment can exercise cross-platform API/playback/transport tests and static or isolated semantic checks. Those checks cannot establish actual Windows XAML activation, visual layout, keyboard/Narrator behavior, theme transitions, or native playback acceptance. Consult the records in [verification](verification/) for completed checks; Windows visual and interaction acceptance must be recorded separately from builds and automated tests.
