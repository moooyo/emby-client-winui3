# Using the Windows client

This guide describes the current development build. It uses native WinUI 3 controls and Windows media playback. See [implementation status](status.md) for measured verification and unfinished release gates.

## Connect and manage accounts

Enter a complete server address, including `https://` or `http://`, your Emby username, and your password. A reverse-proxy base path can be part of the address. The client uses your Emby Server account; Emby Connect is not implemented.

Selecting **Remember this account** saves the access token using Windows user-scoped data protection. Passwords are cleared after the connection attempt and are never stored. On the next launch, select the saved account and restore its session. A changed server identity requires a fresh sign-in. Expired credentials return the application to sign-in.

**Switch account** stops playback and returns to the connection page while retaining any saved token. **Sign out** also removes the saved token and asks the server to revoke the session. If the server is unreachable, the client reports that revocation was not confirmed. The account's address and username can remain available for another sign-in.

Use **Appearance** to choose the Windows system theme, a light theme, or a dark theme. Settings are stored for the current Windows user.

The window remembers its size and position between launches. WinUIEx restores that placement when the monitor layout still matches; otherwise the default placement applies. This works for both the development folder and the packaged application.

## Browse your library

Home offers continue watching, latest items, and next episodes. Select a media library in the navigation pane or use search. Results are paged; the application does not load the complete library into memory. The Favorites view lists items marked as favorites for the signed-in account.

Open a movie or episode to see its available details and playback actions. Series lead to seasons and episodes. Favorite and watched-state actions update the server. Resume uses the saved position supplied by Emby; whether short videos appear in continue watching is also governed by the server's resume rules.

Images use a bounded, account-scoped in-memory cache. Switching accounts clears the active library and cancels old image requests. A failed library request presents a retry action.

## Play and control video

Play or resume an item from its details. The player negotiates a source with the server before opening it. The status indicates the negotiated delivery method. Unsupported direct playback can fall back to server transcoding when the account and server permit it.

The player provides pause/resume, restart, a seek timeline, volume, mute, fullscreen, version selection, audio selection, subtitles, and a streaming bitrate limit. A selector is disabled when there is no alternative. Track indexes come from the selected Emby media source; they are not assumed to match arbitrary native decoder indexes.

Changing the version, audio track, subtitle, or quality settings can restart the server stream. The coordinator preserves paused playback across this transition. Transcoded seeking can also require a new stream. A short delay during negotiation is expected. The bitrate setting is a maximum used during negotiation, not a guarantee that the server will deliver that exact bitrate. Final HLS timeline compatibility is still being validated; consult the capability matrix before relying on this development build for resumed transcoding.

The initial playback profile targets SDR H.264/AAC MP4 and server-generated HLS. Selected subtitles use server burning by default. Do not assume that installing an optional Windows codec makes it part of the application's advertised direct-play profile.

| Input | Action |
| --- | --- |
| F11 | Toggle fullscreen while the player is visible |
| Escape | Exit fullscreen |
| Space | Pause/resume when focus is on the player background |
| Left / Right | Seek by ten seconds when focus is on the player background |
| Timeline keyboard controls | Move the timeline with arrows, Home, End, Page Up, or Page Down |

Focused buttons, selectors, and sliders retain their normal Windows keyboard behavior. Return to the library to stop playback and refresh server-backed information.

While video is actually playing, the client requests that Windows keep the display on. Pausing, buffering, stopping, or disconnecting releases that request. Display-request availability does not prevent playback, and the client does not change the user's Windows power settings.

## Queue and episode continuation

Add items to the transient queue from the library. **Next in queue** advances to the next queued item. Queue contents are cleared when disconnecting or closing the application and are not saved as an Emby playlist.

With **Play next automatically** enabled, natural completion advances through queued items and then looks for a following episode for a series item. The initial setting follows the Emby user's next-episode preference. Manually returning to the library stops playback rather than triggering automatic continuation.

## Current support limits

- This is a Windows x64 development build. A manifest minimum Windows version is not a tested operating-system support matrix.
- Official Emby 4.9.5.0 has been exercised in an isolated environment. Other server versions and reverse-proxy configurations require compatibility testing.
- HDR output, Dolby Vision, audio passthrough, multichannel output, styled ASS/font fidelity, and PGS are not certified capabilities.
- Offline downloads, Live TV UI, Emby Connect, remote control, specialized music playback, persistent playlists, and ARM64 releases are deferred.
- There is no automatic updater. The current Native AOT folder is an unsigned development artifact. MSIX structure checks do not establish installation, upgrade, or clean-machine playback.

The application deliberately presents short errors without raw server response bodies, request URLs, or tokens. If playback reports that a server update could not be confirmed, restore the connection before relying on the server's saved position. The [verification tools](../../tools/) use generated media and dedicated test accounts; they are separate from the normal client workflow.
