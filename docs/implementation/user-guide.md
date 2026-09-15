# Using the Windows client

This guide describes the functionality implemented in the V7 development source. It uses native WinUI 3 controls and Windows media playback. V7 native UI acceptance is paused; see [implementation status](status.md) for the exact build evidence, observed scope, and unfinished release gates.

## Connect and manage accounts

On first launch with no saved accounts, enter a complete server address, including `https://` or `http://`, your Emby username, and your password directly in the sign-in form. A reverse-proxy base path can be part of the address. The client uses your Emby Server account; Emby Connect is not implemented. A small progress ring and status appear during connection, with **Cancel** available while the request is running.

Choosing to remember your sign-in saves the access token using Windows user-scoped data protection. Passwords are never stored. On the next launch, choose a saved account and use **Continue with this account**. Manual credentials are collapsed when that account has a saved token; expand **Use a password or another account** when needed. Missing or expired credentials require a password. A network failure keeps the saved sign-in available for another attempt, while a changed server identity requires a fresh sign-in. Account policy restrictions explain why a connection is unavailable.

The account button at the bottom of the navigation pane shows the signed-in user and server. Its menu contains appearance, playback diagnostics, account switching, account addition, sign-out, and removal from this device. In the compact navigation pane, the avatar opens the same menu.

**Switch account** opens a saved-account chooser, and **Add account** opens a blank connection form. The current library and session remain available until the new account connects successfully. **Cancel** requests cancellation of an unfinished connection and waits for that request to settle. A successful connection then ends the old local session and activates the new one; a cancel request arriving after the connection has committed does not undo that completed connection. The old saved sign-in remains available. The chooser identifies accounts by username and server host.

**Sign out** asks for confirmation, ends the local session, removes the saved token, and asks the server to revoke the session. If local token removal or server revocation fails, a persistent notice identifies the unfinished step and offers **Retry sign-out**. The account's address and username can remain available for another sign-in.

Use the remove button beside a saved account or **Remove account from this device** in the account menu to remove that local saved sign-in after confirmation. Removing the active account first ends its local session. This action does not delete the Emby account or media on the server. Both sign-out and removal dialogs default to **Cancel**.

If saved settings cannot be loaded, use **Retry settings** or **Sign in for this session**. A temporary session does not save accounts or preferences and leaves the existing settings file unchanged. An account that was never saved does not offer a saved-account removal action.

Use **Appearance** to choose the Windows system theme, a light theme, or a dark theme. The menu marks the current choice, and only one theme can be selected. Settings are stored for the current Windows user.

The window remembers its size and position between launches. WinUIEx restores that placement when the monitor layout still matches; otherwise the default placement applies. This works for both the development folder and the packaged application.

## Browse your library

The menu button in the native window title bar expands or collapses the navigation pane. Wide windows retain your collapse preference; narrower windows use an icon rail or an overlay pane that closes after you choose a destination. The title-bar **Back** button returns through the current browsing history or returns from playback to the library. Browsing history retains loaded pages, sort and filter choices, and scroll and focus anchors when you open an item and return.

Home shows **My media**, **Continue watching**, **Next up**, and recently added shelves for each library on one page. Continue watching and episodes use landscape artwork; recent titles use posters. Browse each shelf with its native horizontal scrollbar, touch or touchpad gestures, or keyboard navigation. **See all** opens the full collection separately from scrolling the shelf. Open a library from its compact icon-and-name tile or the navigation pane. Available content can appear before the remaining shelves finish. Empty shelves are omitted, and a failed shelf does not discard the other results. A partial refresh retains the previous contents of failed shelves while updating successful ones.

Libraries, Favorites, search, and recently added collections use an adaptive poster wall that fits its columns to the available width. Sort by title, year, or date added, reverse the order, or filter by watch status. Scrolling near the end automatically loads another page without a **Load more** button. Sorting and filtering keep the header, toolbar, and current results in place while the content-edge progress indicator shows activity. New results replace the collection when ready; a failed filter change restores the previous selection. An empty filtered collection offers **Clear watch filter**. The application does not load the complete library into memory.

Choose **Search** in the navigation pane or press **Ctrl+F** to open the dedicated Search page and focus its input. Enter a query and press Enter or its search icon to submit. Typing alone does not request results. The input stays available when the navigation pane is collapsed. Empty search pages do not request the full library. Returning from an item restores its search query and filters.

The **Watch status** selector provides **All items**, **Unplayed**, and **Played** alongside **Sort by**. **Continue watching** and **Next up** retain the server's ordering and do not offer unsupported sort or watch-status choices. Refresh is in **Page options** beside the title and is also available with **Ctrl+R**. An initially empty page shows a progress ring within its content. Background refresh keeps the existing media and scroll position while a thin progress bar overlays the content edge; a failed refresh leaves those results available.

Movie and episode details use one continuous backdrop beneath the content, navigation pane, and title bar. Movies use poster artwork; episodes use landscape artwork and show their series and episode identity. Artwork stays visible above the detail text in narrow layouts. **Read more** expands a long description. The primary action resumes an unfinished item or starts playback. The favorite button updates the server, and **More** contains watched-state changes, playback from the beginning, and queue actions. Episode details provide a parent-series route and a next-episode detail card when that information is available.

**Media information** appears below the main content and cast when the server supplies relevant metadata. Its **Video**, **Audio**, and **Subtitles** cards show available tracks and fields; **Show all** reveals additional tracks within a group, and **Technical details** expands technical fields for an individual track. A media-version selector appears when multiple sources are available. Source, group, and track disclosures are retained when revisiting the same media during the session. Missing fields and groups are omitted.

Series details include a season selector and an episode list with artwork, watch progress, dates, duration, and descriptions when supplied by the server. Use the view toggle to switch between the list and landscape cards. Selecting a season keeps feedback local to that section and offers **Cancel** while the change is pending; episode actions wait for the selected season to finish loading. The primary action prioritizes a resumable episode, then the next episode, then an available episode. Resume uses the saved position supplied by Emby; whether short videos appear in continue watching is also governed by the server's resume rules.

Movie, series, and episode details include **Cast and crew** in a horizontally scrollable row that adapts to the available width. **View all** switches a larger cast to a grid; **Show less** returns to the row. Select a person to open an independent page with their portrait, biography, and **In your library** works. **Load more** requests another page of works. Open a work and use the title-bar **Back** action to return to the person's loaded works, biography expansion, scroll position, and focus anchor. These names, roles, and works come from the server; missing portraits use initials, and missing biographies or works are described explicitly. Biography and works failures have separate retry actions.

Images use a bounded, account-scoped in-memory cache. Switching accounts clears the active library and cancels old image requests. Loading, empty results, and failures have distinct states. A failed library request presents a retry action; dismissing its notice does not turn the failed request into an empty collection.

## Play and control video

Play or resume an item from its details. The player negotiates a source with the server before opening it. Its status describes preparation, playing, pausing, or recovery; technical delivery information remains in playback diagnostics. Unsupported direct playback can fall back to server transcoding when the account and server permit it.

The main transport row centers pause/resume between **Back 10 seconds** and **Forward 10 seconds**, below the timeline. Episode playback also has separate **Previous episode** and **Next episode** controls. Volume and mute open in a small flyout. Volume, subtitle, queue, playback settings, and fullscreen remain available at narrower widths; the maximum-bitrate shortcut appears when space allows and is also available in settings. **Restart from beginning** and **Playback diagnostics** are at the bottom of the settings panel; fullscreen also provides a **More playback options** menu. Track indexes come from the selected Emby media source; they are not assumed to match arbitrary native decoder indexes. The timeline tooltip displays a readable playback time.

**Playback settings** opens a right-side panel for version, audio, subtitles, maximum bitrate, and **Continue automatically**. **Queue** opens its own panel from the queue button; one panel is visible at a time, and each retains its scrolling and focus context. Panels fit beside the video in a wide window and overlay its right edge in a narrower window. A setting without alternatives appears as read-only information or a disabled selector. Windowed playback uses the normal title bar for the title and Back action; fullscreen places Back in the top overlay. During active fullscreen playback, controls hide after three seconds of inactivity; open controls, keyboard focus, seeking, pauses, and errors keep them available. Move the pointer or focus a control to reveal them. Enable **Reserve space for controls** to keep fullscreen controls below the picture.

Changing the version, audio track, subtitle, or quality settings can restart the server stream. The coordinator preserves paused playback across this transition. Transcoded seeking can also require a new stream. A short delay during negotiation is expected. The bitrate setting is a maximum used during negotiation, not a guarantee that the server will deliver that exact bitrate. Available choices respect the account's bitrate cap. Historical finite VOD HLS verification applies to the documented Emby version and recorded builds; consult the capability matrix for the measured scope.

The initial playback profile targets SDR H.264/AAC MP4 and server-generated HLS. Selected subtitles use server burning by default. Do not assume that installing an optional Windows codec makes it part of the application's advertised direct-play profile.

| Input | Action |
| --- | --- |
| F11 | Toggle fullscreen while the player is visible, including with a side panel open; modal dialogs and active quick flyouts take priority |
| Escape | Close an open player flyout or side panel; otherwise exit fullscreen |
| Space | Pause/resume when focus is on the player background |
| Left / Right | Seek by ten seconds when focus is on the player background |
| Timeline keyboard controls | Move the timeline with arrows, Home, End, Page Up, or Page Down |
| Volume slider Home / End | Set the focused volume slider to 0 / 100 |

Use Tab and Shift+Tab to move between controls. Focused buttons, selectors, and sliders retain their normal Windows keyboard behavior: Space activates the focused button, including Back when that button is focused. Focus Pause/Resume before using Space to control playback. Return to the library to stop playback and refresh server-backed information.

While a player panel is open, its native keyboard behavior takes priority over background playback shortcuts; F11 remains available. The player uses a dark viewing surface, and the window title bar follows the chosen application theme. Detail cards and row spacing respond to Windows text scaling. High-contrast mode removes the detail backdrop and uses system colors; disabling advanced effects makes detail surfaces solid, while disabling animations removes the optional backdrop entrance fade.

While video is actually playing, the client requests that Windows keep the display on. Pausing, buffering, stopping, or disconnecting releases that request. Display-request availability does not prevent playback, and the client does not change the user's Windows power settings.

## Queue and episode continuation

Add items to the transient queue from the details menu. Open **Play queue** at the bottom of the navigation pane for its dialog, or **Queue** in the player for its independent side panel, to inspect up to 100 upcoming items. The player panel separates **Now playing** from **Queued by you**. Select an entry to move it up or down, remove it, or clear the queue. The Delete key removes the selected entry. Duplicate items have independent queue positions. **Play next in queue** advances to the first queued item regardless of the selected entry. An empty queue provides a short explanation, and unavailable actions are disabled. Queue contents are cleared when disconnecting or closing the application and are not saved as an Emby playlist.

During episode playback, **Previous episode** and **Next episode** follow the server's episode order without consuming or reordering manual queue entries. The controls are absent for movies and disabled while no usable neighbor is available or playback cannot change episodes. When the manual queue is empty, its panel can also show **Play next episode**.

With **Continue automatically** enabled, natural completion advances through queued items and then looks for a following episode for a series item. The queue and settings controls share this preference, whose initial value follows the Emby user's next-episode preference. Manually returning to the library stops playback rather than triggering automatic continuation.

## Recover playback and inspect diagnostics

After a recoverable connection or server failure, **Retry from last position** opens a new playback session with the retained position, source, tracks, bitrate limit, and pause state. Restore the connection before retrying. This action is available only while the failed playback still owns its recovery target. Starting another item, stopping, or disconnecting clears that target. **Restart from beginning** remains a separate action that starts the item from zero. Authentication and permission failures do not offer playback retry.

The failure remains visible while opening and closing the queue or diagnostics. **Change settings** opens a draft of the failed session's playback settings. **Apply and retry** uses those choices with the retained position and pause state. **Cancel**, closing the panel, or opening the queue discards the draft and retains the failure for another recovery attempt.

Open **Playback diagnostics** from the account menu or the bottom of the player's **Playback settings** panel to inspect a status summary, environment information, and recent local playback events. Fullscreen also provides this action in **More playback options**. Expand **Technical details** for the engine version and safe JSON snapshot. The records contain fixed event/error categories, selected source codec categories, and random local playback IDs. They omit server addresses, accounts, titles, media paths, access tokens, and raw exception text. Source metadata does not prove which decoder or output format was used.

**Copy snapshot** copies the safe JSON with clipboard history and roaming disabled. **Save as…** opens the native Windows save picker with a timestamped JSON filename so you can choose the export location. Canceling the picker exports nothing. **Refresh** rebuilds the view from the bounded local records. Nothing is uploaded automatically. Diagnostic write failures do not interrupt playback.

Copy and save use the dialog's native action buttons and leave the dialog open. Short operation results appear inline and can be announced by a screen reader. Queue actions automatically move into the overflow menu when space is limited.

## Current support limits

- This is a Windows x64 development build. A manifest minimum Windows version is not a tested operating-system support matrix.
- Official Emby 4.9.5.0 has been exercised in an isolated environment. Other server versions and reverse-proxy configurations require compatibility testing.
- HDR output, Dolby Vision, audio passthrough, multichannel output, styled ASS/font fidelity, and PGS are not certified capabilities.
- Offline downloads, Live TV UI, Emby Connect, remote control, specialized music playback, persistent playlists, and ARM64 releases are deferred.
- There is no automatic updater. Native AOT and unsigned MSIX results apply only to the source and artifact identities in their receipts. MSIX structure checks do not establish installation, upgrade, or clean-machine playback.

The application deliberately presents short errors without raw server response bodies, request URLs, or tokens. If playback reports that a server update could not be confirmed, restore the connection before relying on the server's saved position. The [verification tools](../../tools/) use generated media and dedicated test accounts; they are separate from the normal client workflow.
