# V7 WinUI implementation handoff

Updated: 2026-09-15. Continue in **`D:\Code\emby-client-winui3`**.

## Current stopping point: non-UI completion passed

The user requested completion of the V7 non-UI work while deferring actual UI acceptance. The non-UI stage is complete on `codex/v7-non-ui-completion`: the Release solution build, all 610 product tests, isolated fixture checks, Native AOT publication, unsigned MSIX structural verification, and V7 documentation synchronization passed. The local completion commit preserves the cumulative existing implementation changes. No remote push or public release was requested.

**Actual native UI acceptance remains paused.** Do not launch or operate the application for UI acceptance until the user resumes it. The final AOT executable has not been launched, and the MSIX has not been signed or installed. The earlier initial sign-in screenshot belongs to the prior managed candidate.

### Non-UI corrections

- `ConnectionService.cs` and `ConnectionService.AccountTransactions.cs` put cancellable authentication and capability registration before the final atomic settings save. A successful save commits the connection; the initial sign-in and account-dialog callers accept that result even if cancellation arrives afterward.
- Connection/settings gates prevent stale writes and overlapping new-token cleanup; account restrictions use a concurrent dictionary. Cancellation keeps prior settings and saved credentials. Restore never revokes its borrowed token.
- Failed new connections attempt cleanup only for a known token distinct from saved or previously accepted credentials. Unknown or potentially shared tokens are preserved. Cleanup has an independent five-second deadline and cannot replace the original error; an attempted logout does not guarantee server revocation.
- `AccountCancellationTests.cs` adds 25 deterministic cases covering authentication, current-user lookup, capability registration, persistence waits, post-commit cancellation, actual Windows-protected tokens, shared/temporary sessions, cleanup failures, and concurrent settings changes. AccountStore itself required no transaction change.
- The artifact `V7-FixtureLifecycle.ps1` uses `[NullString]::Value` for the File.Replace backup path. The subsequent complete fixture lifecycle passed.
- The user guide, Fluent implementation notes, and implementation status now describe V7 and separate historical evidence from current results.

### Final verification

See [the durable completion receipt](docs/implementation/verification/v7-non-ui-20260915.json). Detailed logs, source hashes, and generated outputs are under `artifacts/ui-v7-non-ui-20260915`.

| Check | Result |
| --- | --- |
| Release solution build | Passed; zero errors, one existing generated WinUIEx Icon CS0618 warning |
| API / AppState / MediaTransport / Platform / Playback | 53 / 46 / 125 / 244 / 142 passed |
| Product total | 610 passed; zero failures and zero skips |
| Synthetic fixture | 8 lifecycle and 56 HTTP checks passed, separately counted; owned processes stopped and ports released |
| Native AOT | Passed; Windows x64 executable, 19,896,320 bytes; not launched |
| Unsigned MSIX | Passed; 332 payload files, 295 preserved resource keys, complete unpack/hash round trip |

All 187 recorded source/test/script input hashes remained unchanged through build, tests, publication, and packaging. The 18-component CycloneDX inventory passed the script's JSON readback; CycloneDX schema validation was not run.

Final AOT candidate: `artifacts/ui-v7-non-ui-20260915/publish/EmbyClient.App.exe`. Keep its complete directory, including runtime files, assets, licenses, and SBOM. SHA-256: `EB6936CEA053A6478E55492135F24C9F446DA1818403F2260796BB6D99C8B568`.

Unsigned package: `artifacts/ui-v7-non-ui-20260915/packages/EmbyClient.Windows_0.1.0.0_x64_unsigned_20260915-071740010-fc77e782.msix`. SHA-256: `6D60CFA958CA62DDEDC27B3879D2D8CCB76E655A3D9844F971700DD704D42EF5`. Package review: `artifacts/packaging/20260915-071740010-fc77e782/package-review.json`.

### Later UI resumption

The approved V7 design and the native checklist paths below remain valid. When the user resumes UI acceptance, use the final candidate above, a fresh owned fixture, and a unique new process-only `EMBY_CLIENT_DATA_ROOT`; do not reuse the old fixed `native-preview-data` example. Retain each new OwnershipPath and use its guarded stop script for cleanup. The fixture supports one H.264/AAC source, six people, two episodes, and a 200-item collection; it does not establish native playback, alternate versions, subtitles, photographic composition, or real-server compatibility.

Signed installation, upgrades, clean-machine playback, advanced media/device compatibility, and large-library resource acceptance remain separate release gates in `docs/implementation/status.md`. No production signing identity or repository-license decision was made. The previous full handoff is preserved at `artifacts/ui-v7-non-ui-20260915/HANDOFF-before-non-ui-completion.md`.

## Historical continuation: native UI acceptance paused

On 2026-09-15, the user resumed the task and explicitly authorized local isolated verification after `ssh test-env` identified the remote host as Debian Linux. The user then stopped Computer Use with Escape and asked to defer actual UI acceptance. **Do not continue native UI interaction or screenshot acceptance until the user resumes it.**

The 155 source/test files matched the handoff hashes. The recorded managed Release executable was launched with a fresh process-only `EMBY_CLIENT_DATA_ROOT`; only its initial sign-in page was observed and captured. Sign-in, connected navigation, playback, and the acceptance journeys were not completed. The isolated application has been stopped. No new product source change, build, or test-suite run occurred in this continuation.

The first fixture launch exposed a PowerShell string-binding issue in `V7-FixtureLifecycle.ps1`: `File.Replace` received an empty backup path from `$null`. The artifact now uses `[NullString]::Value`; a bounded file-replacement reproduction passed. This does not establish a successful full fixture lifecycle. The failed launch's newly created upstream exited through retained-handle cleanup; ports 18978/18979 were observed free, and no replacement fixture was started.

Continuation evidence is under `artifacts/ui-v7-native-20260915/`: `entry-state.json`, `app-run.json`, `app-cleanup.json`, `fixture-recovery-receipt.json`, `native-acceptance-status.json`, and `screens/01-sign-in-light.*`. The full native checklist remains pending. Earlier local build/test results below remain historical evidence, not newly rerun results.

## Implementation-stage stopping point (historical)

The user approved implementing the final V7 UI/UX design, then asked to finish the current stage, write this handoff, and continue in another session. Implementation is integrated, the managed Release application build passes, and all 585 automated tests pass. **Native application launch, interaction, screenshots, and visual acceptance have not started in this implementation stage.** That is the next stage, not completed work.

The working tree is intentionally uncommitted on `main`, based on commit `0f1f73c93a4b34b811b5a1ef09b5e31ad52af2a7`. It already contained substantial changes before this stage. Preserve all existing changes. Do not reset or replace the checkout with a clean worktree. No commit, merge, package publication, or deployment was performed.

All implementation agents are complete. No native app or synthetic service was started by this stage, so there is no new service ownership record to clean up. Pre-existing user services were not stopped.

## User instructions and authorization

- User-facing conversation must be Chinese; code, comments, and documentation must be English.
- The environment is Windows with PowerShell.
- The user explicitly approved local compilation/tests earlier in this task and allowed the native preview, necessary synthetic services, and screenshots. This authorization was used for the build/tests below. The later handoff request is the reason native acceptance is deferred.
- Do not touch real account records during validation. A process-scoped data directory is now supported specifically to isolate the next native run.
- Continue from the approved design; do not restart the HTML mockup phase.

## Design baseline

Final approved interactive design:

`C:\Users\moooyo\.codex\visualizations\2026\09\14\01a09d77-f577-7ff1-9805-a78f8385acb9\acceptance-v7\emby-ui-ux-v7.html`

The adjacent `acceptance-notes.md`, `foundation.css`, `details.js`, `player.js`, and screenshots capture the specification. Do not print `assets.js` in full; it contains large encoded artwork. The illustrative media and people are fictional and must not be copied into production data.

The cumulative approved design includes:

- One continuous detail backdrop beneath the content, sidebar, and title bar; no separate opaque navigation slab.
- Theme-aware gradient scrims; gentle entrance animation; high-contrast and reduced-effects behavior.
- Movie/episode media information below the main content and cast: Video, Audio, and Subtitles cards using real server metadata. Multi-track and technical fields expand within their groups.
- Translucent card, badge, divider, secondary control, next-episode, and episode-row surfaces. Text and primary playback actions retain clear contrast.
- A lightweight account flyout, cancellable account changes, and honest recovery states.
- Independent player queue/settings panels; previous/next episode controls independent of the manual queue; persistent playback failure and settings-and-retry recovery.
- Browsing return anchors, independent person exploration, explicit loading/error/empty distinctions, progressive useful content, and season-switch ownership.

## Implemented code

### Backdrop, shell, and resources

- `App.xaml` defines stable mutable `Detail*Brush` resources and native NavigationView pane/content brush overrides.
- `MainWindow.xaml` has a non-interactive backdrop layer spanning both title bar and content rows. Existing WinUIEx Mica and system caption behavior remain.
- `MainPage.DetailAppearance.cs` owns authenticated image loading through the existing cache, fixed 1600x1000 image requests, aspect-preserving decode, cancellation/identity checks, theme and system-preference subscriptions, cleanup, and a 220ms optional fade.
- `Services/DetailAppearanceResources.cs` updates the same brush instances so existing XAML references update when appearance changes. High contrast removes the image. Disabling advanced effects makes surfaces solid while retaining ordinary imagery.
- `WindowRoot` provides an F11 fallback for title-bar focus. `TryToggleFullscreen` permits a nonmodal settings panel and still blocks modal/session transitions.

### Detail layout and metadata

- `Views/LibraryView.xaml` and `.Presentation.cs` remove the local hero backdrop and use the shared detail content brush.
- `Views/MediaInformationSection.xaml(.cs)` and `MediaStreamGroupView.xaml(.cs)` render real source/stream data and retain disclosures per media/source/track.
- `ViewModels/MediaInformationViewModel.cs` maps actual API fields; no sample streams are inserted into the application.
- `ViewModels/MediaCardViewModel.cs` supplies full episode identity, parent context, playback restrictions, accessible labels, and media-information state.
- `Api/PlaybackModels.cs` includes the required actual stream fields, with contract tests.
- Cast fills a complete responsive row. Movie/episode information has parent/next routes; episode artwork is landscape. First-load and pending-season feedback remain local.

### Browse and people

- `LibraryViewModel.History.cs` and `.Context.cs` preserve loaded windows, location/filter/sort state, user-data updates, and explicit page outcomes.
- `Views/LibraryView.History.cs` preserves scroll/focus anchors without replacing virtualization.
- `PersonDetailsView.xaml(.cs)` is a true independent view hosted by `PersonPageHost`, not a reopened person dialog. Works paging, biography expansion, scroll, and focus survive opening a work and returning.
- Request failure remains failure after a dismiss; cancellation does not trigger a stalled-pagination result.
- Initial Home and episode data can be shown before optional recommendations. Partial Home refreshes retain failed shelves while committing successful ones. Continue/Next are deduplicated by identity and state.

### Player, queue, and diagnostics

- `PlayerView.xaml` and presentation/recovery/episode partials provide separate queue/settings panels, remembered panel scroll/focus, narrow layouts, and a local dark viewing surface.
- `Services/PlaybackEpisodeNeighbors.cs` resolves actual episode order; movie playback omits episodic controls, unavailable neighbors are disabled, and episode navigation does not consume the manual queue.
- `PlaybackCoordinator.RetryAsync` has a settings-change overload preserving recovery identity, position, and pause state.
- Error context survives queue/diagnostic opening and closing. Retry settings are a draft with explicit apply/cancel behavior. Bitrate choices match the effective cap.
- Queue identities are consistent; diagnostics are status-first, use safe fields, and offer native Save As.

### Account flows and isolated data

- `MainPage.Accounts.cs`, `MainPage.xaml.cs`, `Views/AccountConnectionDialog.cs`, and `ConnectionService.cs` implement cancellable Add/Switch, credential-vs-network recovery, settings retry/temporary sessions, host identity, policy reasons, partial sign-out retry, and unsaved-account removal availability.
- The partial-sign-out recovery stays in the global notice so choosing another saved account cannot discard it.
- Root added all requested XAML recovery controls and a first-run direct form via `MainPage.SignInLayout.cs`; the same named credential controls move between the direct host and the saved-account disclosure.
- `Services/AppDataPaths.cs` provides `EMBY_CLIENT_DATA_ROOT`; AccountStore, WindowPlacementStore, and PlaybackDiagnostics use it only for their default paths. Explicit constructor paths still take precedence. Without the environment variable, existing application paths are unchanged.

## Completed verification

Authoritative receipt and logs:

`artifacts\ui-v7-implementation-20260915\verification-receipt.json`

Final managed Release build command:

```powershell
dotnet build src/EmbyClient.App/EmbyClient.App.csproj -c Release -p:Platform=x64 -p:RestoreLockedMode=true --nologo
```

Result: **0 errors, 1 existing generated-code warning**: CS0618 for WinUIEx `Icon` in `XamlTypeInfo.g.cs`. This was already present before the revision. New nullable and CsWinRT partial-class warnings were fixed, then the application was rebuilt.

| Test project | Passed | Failed |
|---|---:|---:|
| AppState | 46 | 0 |
| Api | 53 | 0 |
| Playback | 142 | 0 |
| MediaTransport | 125 | 0 |
| Platform | 219 | 0 |
| Total | **585** | **0** |

Tests used `dotnet test --project <project.csproj> --configuration Release --no-ansi`. Platform was run again after the final test-warning cleanup. AppState source links include all new history/person/media-information view models, and Platform links the new AppDataPaths helper. Tests were not run concurrently with the final application build.

The built application is:

`src\EmbyClient.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\EmbyClient.App.exe`

This is a build, **not an AOT publish or native runtime acceptance**.

## Next stage: native acceptance

1. Read `artifacts\ui-v7-implementation-20260915\Native-Acceptance-Checklist.md` and `V7-Isolated-Fixture.md`.
2. Start an owned isolated fixture, checking the scripts' requirements first:

   ```powershell
   Set-Location 'D:\Code\emby-client-winui3'
   $run = & .\artifacts\ui-v7-implementation-20260915\Start-V7ReviewFixture.ps1 -FailFirstPlaybackInfo
   ```

   Default upstream/proxy ports are 18978/18979. App URL is `http://127.0.0.1:18979/emby`, with synthetic `demo` / `demo`. Occupied ports are refused; existing listeners are not replaced. Keep the returned `$run.OwnershipPath`.

3. Start the built application with a fresh process-only data root. Do not set machine/user environment variables or use the user's default application data:

   ```powershell
   $appExe = Join-Path (Get-Location) 'src\EmbyClient.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\EmbyClient.App.exe'
   $previewData = Join-Path (Get-Location) 'artifacts\ui-v7-implementation-20260915\native-preview-data'
   New-Item -ItemType Directory -Path $previewData -Force | Out-Null
   $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
   $startInfo.FileName = $appExe
   $startInfo.WorkingDirectory = Split-Path -Parent $appExe
   $startInfo.UseShellExecute = $false
   $startInfo.EnvironmentVariables['EMBY_CLIENT_DATA_ROOT'] = $previewData
   $previewProcess = [System.Diagnostics.Process]::Start($startInfo)
   ```

4. Use the installed Windows computer-use skill for native observation/input. Read its `SKILL.md`, `docs/guidance.md`, `docs/api.md`, and confirmations instructions before action. The intended package is `@oai/sky` through node_repl. No direct PowerShell UI Automation should be mixed into the same turn. The browser `cua_repl` previously reported unavailable Codex auth, so it should not be assumed to control native WinUI.
5. Verify first launch/resource resolution, actual theme updates and translucency, pane states, images, text scaling/focus, person/work/back, error-vs-empty and paging, account Cancel, separate player panels/F11, actual episode switching, retry, queue, and diagnostics. Capture the real window and compare it with V7. Fix any runtime/layout problems, then rerun relevant checks.
6. Close the isolated app and stop only the owned fixture:

   ```powershell
   & .\artifacts\ui-v7-implementation-20260915\Stop-V7ReviewFixture.ps1 -OwnershipPath $run.OwnershipPath
   ```

   Do not use the old review Stop script with historical hard-coded PIDs. The new scripts validate recorded executable/command/creation-time/listener ownership. They have been statically reviewed but have not yet been executed.

## Boundaries and remaining concerns

- No new native screenshots exist for this implementation. Old `native-20260914` screenshots and the V3-V7 HTML screenshots must not be presented as screenshots of this build.
- Native XAML resource resolution, physical layout, actual image masking, high contrast, reduced effects, UIA/Narrator, focus restoration under virtualization, and playback behavior still need observation. Unit tests and XAML compilation do not establish them.
- The isolated fixture has real generated H.264/AAC media, six synthetic people, person works, movies, a series, and two episodes. It adds backdrop tags for six known item/type pairs and reuses existing PNG routes. It does **not** provide genuine multi-version/multi-subtitle playback. Do not claim those paths validated using invented fixture tracks.
- Include a late-cancellation scenario for Add/Switch during server authentication/settings persistence/capability registration. Basic cancellation retains the old library by design, but transactional server-token/persistence behavior at those timing boundaries has not been exercised natively.
- The shader/brush and resource changes are intentionally native XAML/WinUI approximations of the design, not a promise of pixel identity with HTML.
- The generation/ownership, first-source metadata fallback, duplicate Default labeling, neutral checked-toggle foreground mismatch, title-bar F11 guard, partial-sign-out notice, and new build warnings were reviewed and corrected during integration. Do not reopen stale sub-agent findings without checking current source.

## Saved state and provenance

Under `artifacts\ui-v7-implementation-20260915`:

- `original/`: copies of 126 source/test files as they existed at implementation entry.
- `entry-git-status.txt`, `entry-source-hashes.json`: preserve the pre-existing dirty baseline.
- `changes-since-entry.json`: this stage's source/test additions and modifications.
- `handoff-source-hashes.json`: final source/test identity after compilation.
- `verification-receipt.json`: build identity, exact test counts, and explicit native-validation status.
- `native-build.log` and per-suite logs: actual command results.
- Fixture scripts, lifecycle helper, proxy, and checklists: ready for the next stage, not executed.

The detailed design folders remain outside the repository under the absolute V7 path above. No source files should be overwritten with HTML fixture data. Continue native validation and implementation refinement; commit only when the user requests or authorizes the intended commit scope.
