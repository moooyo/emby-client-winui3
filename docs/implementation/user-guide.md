# Using the Windows client

This guide describes the current development build. It uses native WinUI 3 controls and Windows media playback. See [implementation status](status.md) for measured verification and unfinished release gates.

## Connect and manage accounts

Enter a complete server address, including `https://` or `http://`, your Emby username, and your password. A reverse-proxy base path can be part of the address. The client uses your Emby Server account; Emby Connect is not implemented.

Selecting **Remember this account** saves the access token using Windows user-scoped data protection. Passwords are cleared after the connection attempt and are never stored. On the next launch, select the saved account and restore its session. A changed server identity requires a fresh sign-in. Expired credentials return the application to sign-in.

The account button at the bottom of the navigation pane shows the signed-in user and server. Its menu contains appearance, playback diagnostics, account switching, and sign-out. In the compact navigation pane, the avatar opens the same menu.

**Switch account** stops playback and returns to the connection page while retaining any saved token. **Sign out** also removes the saved token and asks the server to revoke the session. If the server is unreachable, the client reports that revocation was not confirmed. The account's address and username can remain available for another sign-in.

Use **Appearance** to choose the Windows system theme, a light theme, or a dark theme. The menu marks the current choice, and only one theme can be selected. Settings are stored for the current Windows user.

The window remembers its size and position between launches. WinUIEx restores that placement when the monitor layout still matches; otherwise the default placement applies. This works for both the development folder and the packaged application.

## Browse your library

Home shows **My media**, **Continue watching**, **Next up**, and recently added shelves for each library on one page. Continue watching and episodes use landscape artwork; recent titles use posters. Browse each shelf with its native horizontal scrollbar, touch or touchpad gestures, or keyboard navigation. **See all** opens the full collection separately from scrolling the shelf. Open a library from its artwork tile or the navigation pane. Empty shelves are omitted, and a failed shelf does not discard the other results.

Libraries, Favorites, and search use an adaptive poster wall that fits its columns to the available width. Sort by title, year, or date added, reverse the order, or show only unplayed items. Scrolling near the end automatically loads another page without a **Load more** button. Sorting and filtering keep the header, toolbar, and current results in place while a reserved loading area shows progress. New results replace the collection when ready. The application does not load the complete library into memory.

Movie and episode details show a backdrop, poster, metadata, description, and available cast images. The primary action resumes an unfinished item or starts playback. Favorite and watched-state buttons update the server. **More playback options** contains playback from the beginning and queue actions.

Series details include a season selector and episode cards with artwork, dates, duration, and descriptions when supplied by the server. The primary action prioritizes a resumable episode, then the next episode, then an available episode. Selecting a season updates its episode list. Resume uses the saved position supplied by Emby; whether short videos appear in continue watching is also governed by the server's resume rules.

Images use a bounded, account-scoped in-memory cache. Switching accounts clears the active library and cancels old image requests. A failed library request presents a retry action.

## Play and control video

Play or resume an item from its details. The player negotiates a source with the server before opening it. Its status describes preparation, playing, pausing, or recovery; technical delivery information remains in playback diagnostics. Unsupported direct playback can fall back to server transcoding when the account and server permit it.

The main transport row provides pause/resume, next in queue, restart, volume, mute, and fullscreen. Open **Playback settings** for version, audio, subtitles, streaming quality, and the **Play next automatically** switch. A selector is disabled when there is no alternative. Track indexes come from the selected Emby media source; they are not assumed to match arbitrary native decoder indexes. The timeline tooltip displays a readable playback time.

Changing the version, audio track, subtitle, or quality settings can restart the server stream. The coordinator preserves paused playback across this transition. Transcoded seeking can also require a new stream. A short delay during negotiation is expected. The bitrate setting is a maximum used during negotiation, not a guarantee that the server will deliver that exact bitrate. Finite VOD HLS has been verified against the documented Emby version; consult the capability matrix for the measured scope.

The initial playback profile targets SDR H.264/AAC MP4 and server-generated HLS. Selected subtitles use server burning by default. Do not assume that installing an optional Windows codec makes it part of the application's advertised direct-play profile.

| Input | Action |
| --- | --- |
| F11 | Toggle fullscreen while the player is visible |
| Escape | Close an open playback settings panel; otherwise exit fullscreen |
| Space | Pause/resume when focus is on the player background |
| Left / Right | Seek by ten seconds when focus is on the player background |
| Timeline keyboard controls | Move the timeline with arrows, Home, End, Page Up, or Page Down |
| Volume slider Home / End | Set the focused volume slider to 0 / 100 |

Use Tab and Shift+Tab to move between controls. Focused buttons, selectors, and sliders retain their normal Windows keyboard behavior: Space activates the focused button, including Back when that button is focused. Focus Pause/Resume before using Space to control playback. Return to the library to stop playback and refresh server-backed information.

While the playback settings panel is open, its native keyboard behavior takes priority over player shortcuts. The window title bar follows the chosen application theme. Card text and row spacing respond to Windows text scaling; high-contrast mode uses the user's system colors.

While video is actually playing, the client requests that Windows keep the display on. Pausing, buffering, stopping, or disconnecting releases that request. Display-request availability does not prevent playback, and the client does not change the user's Windows power settings.

## Queue and episode continuation

Add items to the transient queue from the details menu. Open **Play queue** at the bottom of the navigation pane, or **Queue** in the player to inspect up to 100 upcoming items. Select an entry to move it up or down, remove it, or clear the queue. The Delete key removes the selected entry. Duplicate items have independent queue positions. **Next in queue** advances to the first queued item. Queue contents are cleared when disconnecting or closing the application and are not saved as an Emby playlist.

With **Play next automatically** enabled, natural completion advances through queued items and then looks for a following episode for a series item. The initial setting follows the Emby user's next-episode preference. Manually returning to the library stops playback rather than triggering automatic continuation.

## Recover playback and inspect diagnostics

After a recoverable connection or server failure, **Retry from last position** opens a new playback session with the retained position, source, tracks, bitrate limit, and pause state. Restore the connection before retrying. This action is available only while the failed playback still owns its recovery target. Starting another item, stopping, or disconnecting clears that target. **Restart** remains a separate action that starts the item from zero. Authentication and permission failures do not offer playback retry.

Open **Playback diagnostics** from the account menu, or **Diagnostics** in the player to inspect retained local playback events and application/runtime versions. The records contain fixed event/error categories, selected source codec categories, and random local playback IDs. They omit server addresses, accounts, titles, media paths, access tokens, and raw exception text. Source metadata does not prove which decoder or output format was used.

**Copy snapshot** copies the safe JSON with clipboard history and roaming disabled. **Save snapshot** writes `%LOCALAPPDATA%\EmbyClient.Windows\diagnostics\snapshot.json`, replacing a previous snapshot. **Refresh** rebuilds the view from the bounded local records. Nothing is uploaded automatically. Diagnostic write failures do not interrupt playback.

Copy and save use the dialog's native action buttons and leave the dialog open. Short operation results appear inline and can be announced by a screen reader. Queue actions automatically move into the overflow menu when space is limited.

## Current support limits

- This is a Windows x64 development build. A manifest minimum Windows version is not a tested operating-system support matrix.
- Official Emby 4.9.5.0 has been exercised in an isolated environment. Other server versions and reverse-proxy configurations require compatibility testing.
- HDR output, Dolby Vision, audio passthrough, multichannel output, styled ASS/font fidelity, and PGS are not certified capabilities.
- Offline downloads, Live TV UI, Emby Connect, remote control, specialized music playback, persistent playlists, and ARM64 releases are deferred.
- There is no automatic updater. The current Native AOT folder is an unsigned development artifact. MSIX structure checks do not establish installation, upgrade, or clean-machine playback.

The application deliberately presents short errors without raw server response bodies, request URLs, or tokens. If playback reports that a server update could not be confirmed, restore the connection before relying on the server's saved position. The [verification tools](../../tools/) use generated media and dedicated test accounts; they are separate from the normal client workflow.
