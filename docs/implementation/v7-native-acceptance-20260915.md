# V7 native acceptance evidence — 2026-09-15

This completed batch is a scoped native acceptance record, not a 20/20 result. The user authorized local Windows observation with task-owned synthetic media, accounts, services, and application data. The latest executable passed build/tests/publication/unsigned-package checks and the final queue and poster regression sequences. The application and owned fixture have been stopped.

## Candidate identity

The current candidate is `ECC9578458C70D1BCAC6E337DDEC0A16552F5A53040EA95F2E92EA68E5405CA6`, in `candidate-accepted`, PID 11380 in the [process record][accepted-process]. Tracing is disabled. Earlier screenshots remain assigned to their actual candidates:

| Label | SHA-256 prefix | Evidence lane |
| --- | --- | --- |
| Original | `EB6936CE` | Startup failure; original non-UI publication |
| C1 | `3E225164` | Sign-in reached; Home binding failed |
| C2 | `5D0EBF70` | `screens/candidate-2` |
| C3 | `FA6A595F` | `screens/candidate-3` |
| C4 | `C561DA46` | `screens/candidate-4` |
| F | `0A646B16` | `screens/final`; column repair, then poster recovery failure |
| PF | `50467483` | `screens/posters-fixed`; poster/cast repairs, then stable queue-row failure |
| Current | `ECC95784` | `candidate-accepted`, `screens/accepted`; queue repair natively repeated |

Full earlier hashes and process/fixture identities remain in the [artifact evidence summary][summary] and adjacent process records. A directory named `final` or a screenshot named for an intended outcome is not itself evidence of success. Earlier native observations are not silently promoted to the current executable.

Capture conditions: the narrow window was 704×834 logical pixels, the maximized window 2560×1392, and fullscreen 2560×1440. The diagnostic layout/column traces on this display recorded RasterizationScale=1.5 and TextScaleFactor=1; system settings were not changed. Dark was used for the final regression lane; C2 also records Light. `screen-evidence-manifest.json` retains hashes for 201 screenshot and UIA files.

## Twenty-journey checklist

The [original checklist][checklist] defines these journeys. **Scoped observed** means the named behavior was observed on the cited candidate; **Partial** identifies remaining requirements or an open defect; **NotObserved** means native evidence is absent. These are evidence classifications, not a numerical pass score.

| ID | Status | Observed scope and limits |
| --- | --- | --- |
| V7-01 | Scoped observed | C2 fresh masked-password sign-in and basic Tab/Enter use; C4 expired saved-sign-in recovery followed by Home. [Sign-in][c2-signin], [recovery][c4-auth]. |
| V7-02 | Scoped observed | Home, library navigation, and compact/wide navigation states are retained. PF shows the library through narrow/maximized/restored layouts. [Home][c4-home], [restored wall][pf-restored]. |
| V7-03 | Scoped observed | Movie identity, primary action, continuous backdrop, and real H.264/AAC media fields are visible; technical disclosure stays within the card. [Movie][c2-detail], [metadata][c2-info]. |
| V7-04 | Partial | Six cast entries, independent person/work/back navigation, and return context are observed. PF confirms complete 4→6→4 cast presentation at 704 width; comprehensive focus-return acceptance remains open. [Person return][c2-person], [expanded cast][pf-cast-expanded]. |
| V7-05 | Scoped observed | C4 shows both episode identities in list and card modes for the single synthetic season. This does not cover multiple-season races. [List][c4-list], [cards][c4-cards]. |
| V7-06 | Partial | C2 Add-account Cancel retained the movie, account, scroll, and expanded fields. The complete query/filter/sort matrix was not exercised. [Cancel return][c2-add-cancel]. |
| V7-07 | Partial | C4 Switch-account Cancel returned to the same narrow person page before connecting. In-flight and late transaction-boundary cancellation remain unobserved. [Switch return][c4-switch]. |
| V7-08 | Partial | Light/Dark detail surfaces and metadata were inspected; Home and person states also have theme evidence. This is not exhaustive focus/contrast coverage. [Light metadata][c2-info], [Dark metadata][c2-dark-info]. |
| V7-09 | Partial | Recorded 704×834 person, series, queue, diagnostics, and search states are usable in their captured scope. PF repairs the clipped cast row and poster resize/scroll sequence; minimum-width and text-scale combinations remain open. [Collapsed cast][pf-cast-collapsed], [restored wall][pf-restored]. |
| V7-10 | NotObserved | Windows high-contrast configuration, native high-contrast behavior, and restoration were not exercised. |
| V7-11 | Scoped observed | C3 showed changing native color frames and mute; C4 showed pause and seek near the end at 2:54. [Playback][c3-playback], [seek][c4-seek]. |
| V7-12 | Partial | C3 Settings and Queue remained separate and preserved pause at 1:13; panel Escape behavior was observed. Complete invoking-control focus restoration remains unproven. [Settings][c3-settings], [Queue][c3-queue]. |
| V7-13 | Scoped observed | C4 add/reorder/remove/clear were observed. After PF's retained ordinal failure, ECC repeats removal in both entry points: total 1, visible ordinal 1, UIA 1 of 1, and Next in queue. Deleting the remaining player item yields 0 and Close queue focus without changing pause at 0:19. [Dialog][accepted-queue], [player][accepted-player-queue], [empty/focus][accepted-empty]. |
| V7-14 | Scoped observed | C3 Next/Previous switched 2101↔2102 with correct boundary controls. C4 episode navigation preserved its manual queue entry. [Next][c3-next], [Previous][c3-previous], [queue preserved][c4-next]. |
| V7-15 | Scoped observed | C4 naturally advanced from episode 1 to episode 2 once with queue empty, then returned with Played/Resume state updated. [Natural continuation][c4-natural], [series progress][c4-progress]. |
| V7-16 | Scoped observed | C3 covered F11 with Queue open and Escape closing the panel first. PF adds video-focus idle chrome hiding, Tab reveal with a visible white focus outline, and normal-window restoration paused at 2:24. [Fullscreen][pf-fullscreen], [idle][pf-idle], [keyboard reveal][pf-reveal], [restored pause][pf-pause]. |
| V7-17 | Scoped observed | C3 retained the injected first PlaybackInfo opening failure, retry draft On→Cancel→Off, and successful Apply and retry. This is not a cold-range transport failure. [Draft][c3-draft], [canceled draft][c3-cancel-draft], [recovered playback][c3-playback]. |
| V7-18 | Scoped observed | C4 exported a sanitized diagnostic snapshot. PF explicitly names Recent playback events and exposes its expanded safe-event group; the normal window returns paused at 2:24. [Saved export][c4-saved], [named events][pf-events], [settled expansion][pf-events-expanded], [return][pf-pause]. |
| V7-19 | Scoped observed | C4 returned to series with episode 1 Played and episode 2 Resume at 2:01. PF restored normal playback chrome without losing its 2:24 pause. [Series state][c4-progress], [restored pause][pf-pause]. |
| V7-20 | Partial | C4 catalog 503 showed an error and Retry, then loaded 200-item metadata. Two delayed pagination requests produced later-page content; stale-response navigation and injected catalog 401 were not exercised. [Error][c4-error], [loaded][c4-loaded], [third page][c4-page3]. |

## Additional bounded evidence

C4 search kept the committed unmatched-query result while `Color` was only a draft, then displayed one Synthetic Color Study result after submission. [Draft separation][c4-search-draft] and [submitted result][c4-search-result] establish distinct empty/result presentation, not every search race.

C4 [before/after natural-end samples][before-end] bound exactly one continuation: PlaybackInfo and Start increased 3→4, Stop 2→3, and Progress 36→43. The [after sample][after-end], native episode-2 capture, and diagnostic Resumed→Ended→Created→Negotiating→Opening→Started sequence agree. Final C4 counters are Start 4/Stop 4/Progress 91; cumulative counters are not independent playback passes.

The [exported JSON][diagnostic-export] contains 49 retained records, LocalOnly=true, StorageIssue=None, and zero dropped records. Review found sequence/time, local playback IDs, event/error classes, versions, and media categories; no URL, credential field, title, server address, or file path. Retained records are not all necessarily from C4. PF's settled event expansion shows five recent safe events; the earlier transitional expansion capture is not used as proof.

PF operator observations identify F11, VideoFocusTarget followed by waiting, and Tab as the fullscreen/idle/reveal sequence. Screenshots establish physical chrome hiding and the white focus outline; UIA continued listing hidden controls and reported an unreliable focused pane. This is not Narrator or full focus-system acceptance.

## Failures and repair evidence

- **Trace-confirmed:** Original startup failed when a base Brush projection reached a generic CLR derived cast; concrete WinRT As conversion allowed native launch. C1 then failed with the exact missing generated-bindable-property exception for AutomationLabel; the app-only generated property allowed subsequent Home rendering.
- **Trace-confirmed:** Oversized posters persisted after the preliminary panel-cast change. [Layout trace][layout-trace] found hidden Loaded with no cached ScrollViewer, then a live 1012×577 viewer after layout. Reconnection and cache invalidation corrected C4's initial wall; the earlier panel-type hypothesis alone was insufficient.
- **Trace-confirmed width mismatch:** The panel had 606.6667 available units inside a 608-unit viewport. Using the viewport for three slots made the third slot wrap; `column-trace.txt` records that discrepancy. Wall sizing now uses the panel's available width and padding. F and later candidates show three narrow columns and thirteen maximized columns.
- **Observed and retested:** F showed 3/13 columns but lost posters after restore. Static review identified weak managed-container ownership; active poster bindings now retain the container until the existing cancellation/unload cleanup. No GC/key-loss trace was taken, so that mechanism remains an inferred cause. PF retained images through 3→13→3 columns, scrolling to items 46–51 and returning; ECC repeated maximize/restore after playback. PF also corrected C4's partial next actor: settled narrow cast is 4→6→4 with matching dynamic action names. These captures establish the repaired scenarios without proving all widths or long-term image-cache behavior.
- **Observed failure and current native recovery:** PF's settled queue capture has total 1, visible ordinal 2, UIA 2 of 2, and no next marker; its filename expresses intent, not a pass. The repair updates both entry points after low-priority UpdateLayout, resolves containers by item, and uses concrete As. ECC's dialog and player captures now show ordinal 1, UIA 1 of 1, and the next marker after removal, with pause preserved. The failed PF capture remains retained.
- **Retained build failure:** The first queue-repair build had ambiguous DispatcherQueuePriority references. The coordinator corrected the namespace with an alias; the [failed log][failed-build] remains beside the successful logs.

## Current build, tests, and unsigned package

[Accepted build][accepted-build] and [publication][accepted-publish] logs record zero build errors, one existing generated WinUIEx Icon CS0618 warning, and Native AOT publication. The [five suite logs][accepted-logs] record 610 passing product tests: API 53, AppState 46, MediaTransport 125, Platform 244, Playback 142; zero failures/skips. [Source input hashes][accepted-inputs] retain that cycle's identity.

The [package log][accepted-package] records unsigned MSIX `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260915-091421469-ef32a64f.msix`, SHA-256 `7FF32711E0C372E4276DFD6912B72BD97EEB1C8670539117C7C2513358CF1A5E`, with structural verification passed. The [review directory][package-review] is `20260915-091421469-ef32a64f`. Installation and packaged runtime remain unverified. Earlier candidate packages and test cycles retain their own identities; no broad native result is inferred from them.

## Explicit limits

NotObserved: Windows high contrast, 200% system text, reduced-effects switching, Narrator, comprehensive keyboard/focus return, active/late account cancellation, injected catalog 401, delayed-person/stale-pagination navigation races, multiple-season ownership, full 200-item traversal, and long-session memory/handle acceptance. The retained pagination status records one Items failure, two delayed requests, and no injected unauthorized response.

The synthetic fixture has one real H.264/AAC source, six people, two episodes, and synthetic portrait artwork reused as backdrop data. Alternate playable versions, multiple selectable audio tracks, subtitles, HLS/transcoding, photographic composition, real-server/device compatibility, signed installation/upgrades, and clean-machine playback are not established. The unsigned MSIX result is structural only.

## Final-candidate completion

ECC95784 launched to [Home][accepted-home]. Its [dialog removal][accepted-queue] and [player keyboard-Delete removal][accepted-player-queue] agree on total 1, ordinal 1, UIA 1 of 1, and Next in queue. A further Delete empties the queue, leaves playback paused at 0:19, and visibly returns the white focus outline to [Close queue][accepted-empty]. Other rows retain their named candidate scopes; the PF failure is not relabeled.

ECC additionally returned to the original movie with Resume at 0:19, then displayed a thirteen-column maximized wall and restored three-column wall with all visible posters intact: [return][accepted-return], [maximized][accepted-wide], [restored][accepted-restored]. The source audit confirmed all 188 recorded build/test/script inputs remained unchanged through final verification.

Normal Alt+F4 closed PID 11380; [application cleanup][app-cleanup] records its observed exit. The guarded [fixture cleanup][fixture-cleanup] stopped only this run's verified proxy/upstream processes and confirmed ports 18978/18979 released at 09:20 UTC. The final fixture snapshot recorded Start 6, Stop 6, Progress 133, 104 partial responses, and zero active image requests. The earlier fixture run was separately cleaned. No real account, media, or system preference was changed. The NotObserved items above remain explicit release/coverage gaps.

[accepted-process]: ../../artifacts/ui-v7-acceptance-20260915-072317/candidate-accepted-process.json
[summary]: ../../artifacts/ui-v7-acceptance-20260915-072317/acceptance-summary-draft.md
[checklist]: ../../artifacts/ui-v7-implementation-20260915/Native-Acceptance-Checklist.md
[c2-signin]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/01-sign-in-light.png
[c2-detail]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/04-movie-detail-light.png
[c2-info]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/05-media-information-expanded-light.png
[c2-dark-info]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/06-media-information-expanded-dark.png
[c2-person]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/09-return-to-person-dark.png
[c2-add-cancel]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-2/12-add-account-cancel-return.png
[c3-playback]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/08-native-playback-muted.png
[c3-settings]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/09-settings-paused.png
[c3-queue]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/10-queue-paused.png
[c3-next]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/12-next-episode-two.png
[c3-previous]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/13-previous-episode-one.png
[c3-draft]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/06b-retry-draft-on.png
[c3-cancel-draft]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-3/07-retry-draft-cancel-restored.png
[c4-auth]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/02-expired-synthetic-token-recovery.png
[c4-home]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/03-home-recovered.png
[c4-switch]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/10-switch-cancel-person-preserved.png
[c4-list]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/14-series-list-narrow.png
[c4-cards]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/15-series-cards-narrow.png
[c4-next]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/17-next-episode-queue-preserved.png
[c4-queue-empty]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/19-player-queue-cleared.png
[c4-seek]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/20-seek-near-end-paused.png
[c4-natural]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/21-natural-advance-episode-two.png
[c4-saved]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/23-diagnostics-saved.png
[c4-progress]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/24-series-progress-after-playback.png
[c4-error]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/25-large-library-error.png
[c4-loaded]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/26-large-library-loaded-narrow.png
[c4-page3]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/29-third-page-visible.png
[c4-search-draft]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/31-search-draft-separated.png
[c4-search-result]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/candidate-4/32-search-result.png
[pf-restored]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/04-restored-posters.png
[pf-cast-collapsed]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/07-cast-collapsed-four-complete.png
[pf-cast-expanded]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/09-cast-expanded-settled.png
[pf-queue-failed]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/13-queue-one-of-one-settled.png
[pf-fullscreen]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/14-fullscreen-playing.png
[pf-idle]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/15-fullscreen-idle.png
[pf-reveal]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/16-keyboard-reveal.png
[pf-events]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/17-diagnostics-accessible-events.png
[pf-events-expanded]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/19-events-expanded-settled.png
[pf-pause]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/posters-fixed/20-normal-restored-paused.png
[before-end]: ../../artifacts/ui-v7-acceptance-20260915-072317/before-natural-end.json
[after-end]: ../../artifacts/ui-v7-acceptance-20260915-072317/after-natural-end.json
[diagnostic-export]: ../../artifacts/ui-v7-acceptance-20260915-072317/playback-diagnostics.json
[layout-trace]: ../../artifacts/ui-v7-acceptance-20260915-072317/layout-trace.txt
[failed-build]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-logs/build-first-namespace-ambiguity.log
[accepted-build]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-logs/build.log
[accepted-publish]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-logs/publish.log
[accepted-logs]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-logs
[accepted-inputs]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-source-inputs.json
[accepted-package]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-logs/package.log
[package-review]: ../../artifacts/packaging/20260915-091421469-ef32a64f
[accepted-home]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/01-home.png
[accepted-queue]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/03-queue-one-of-one-passed.png
[accepted-player-queue]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/05-player-queue-one-of-one-passed.png
[accepted-empty]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/06-player-empty-focus-close.png
[accepted-return]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/07-return-detail-resume.png
[accepted-wide]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/08-maximized-wall.png
[accepted-restored]: ../../artifacts/ui-v7-acceptance-20260915-072317/screens/accepted/09-restored-wall.png
[app-cleanup]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-app-cleanup.json
[fixture-cleanup]: ../../artifacts/ui-v7-acceptance-20260915-072317/accepted-cleanup.json
