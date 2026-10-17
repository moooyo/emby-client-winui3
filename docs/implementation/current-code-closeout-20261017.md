# Current code closeout: 2026-10-17

Updated: 2026-10-17, Asia/Shanghai. Final16 repairs Home/End navigation in the
large-library wall while preserving DataTemplate host pooling and
virtualization. Its build, package/runtime and offline schema checks passed.
The final formal run covers the original finite UI sequence, with one retained
tooltip observation limit. Resource attribution remains unresolved:
**InvestigateHighRetention / PerformanceNotAccepted**. Test execution, final
owned fixture cleanup and system restoration are complete within this scope.

| Scope | Current result |
| --- | --- |
| Final16 compilation, Native AOT, package/runtime and offline schema | **BuildPassed** |
| Short End/Home regression and next keyboard action | **BoundedNavigationObserved** |
| Formal06 finite UI/content, fixed idle periods and sign-out/normal close | **OriginalFiniteUiWorkloadCovered_WithObservationLimit** |
| Whole-App resource acceptance | **InvestigateHighRetention / PerformanceNotAccepted** |
| Final owned fixture cleanup and system restoration | Completed; no active owned App, fixture or proxy remains |
| LAN discovery, public Save and dependent account restoration | **LANDeferred**, as requested by the user |

The user renewed unattended continuation and asked to skip LAN work. The
earlier physical-Escape stop and the old goal-service blocked receipt remain
historical. This continuation does not claim a new goal-service status or
whole-project completion. Earlier candidate results retain their own identities.
The [final machine-readable status](../../artifacts/current-acceptance-20261017-continuation/FINAL-STATUS.json)
records the current candidate, resource disposition and completed owned cleanup.
The [formal acceptance review](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/ACCEPTANCE-REVIEW.md)
records the original requirement coverage and final disposition.

## Final16 candidate and source change

The [final verification summary](../../artifacts/current-acceptance-20261017-continuation/build-final16/verification/final-verification-summary-16.json)
binds the candidate, complete inputs, payload, original execution receipts and
the retained test closures.

| Identity | Recorded value |
| --- | --- |
| App EXE | 30,613,504 bytes; SHA256 `D72B62373A3E45A972C3F900AB8D9E92540B51823121F24EA58663AC9CFC4651` |
| Final summary SHA256 | `B92921B24E465505382F1FD579E53DC8807BB70E9F8263A3AA08E402EA78D310` |
| Production / test / native-runtime / payload file inventories | 675 / 260 / 24 / 595 |
| Actual execution | Release, Debug, fresh Native AOT, package/runtime and offline schema checks passed; all 13 recorded subprocesses and the original outer build tool have actual exit 0 and closed original handles |
| Warnings | Release and Debug each retain one existing generated-XAML `CS0618`; Native AOT has zero warnings |

Relative to final15, the production delta is limited to
`LumenLibraryView.WallControls.cs` and `LumenMediaCard.cs`. The repair removes
the stale temporary anchor left by Home's `GetOrCreate` path and restores the
scroller's keyboard-navigation eligibility. Recycling, DataTemplate host
pooling and virtualization remain in place.

All **2,430 product cases** retain matching complete compiled-closure evidence:
API 389, AppState 265, MediaTransport 209, Platform 1,149 and Playback 418,
with zero failures or skips. They were **reused, not rerun** for this UI-only
delta. The separately compiled NativeProbe's 114-input closure is also unchanged
and reused without recompilation. Native13 and ASS were not rebuilt for
final16; no later native-family runtime pass is inferred.

## Navigation and original finite UI coverage

The [short result](../../artifacts/current-acceptance-20261017-continuation/short-final16-01/SHORT-RESULT.json)
and [independent metadata review](../../artifacts/current-acceptance-20261017-continuation/short-final16-01/REVIEW.md)
record a 480-item warm cohort, three observed End/Home rounds with complete
`005000` and `000001` tooltips, the next Right action and normal App exit 0.
No blank wall was observed in that bounded sample. This short regression is
separate from the formal workload and does not establish a performance result.

The [independent UI evidence review](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/UI-EVIDENCE-REVIEW.md)
supports the original finite UI coverage within the declared observation
limit. The [formal06 plan](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/plan.json),
[initial handoff](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/initial-ui-handoff.json)
and [tail handoff](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/tail-ui-handoff.json)
belong to one unchanged App process: PID 37624, start ticks
`639278215219670360`. The sole Computer Use owner changed sequentially from
the initial actor to the tail actor and then the close actor; there was no App
restart or overlapping UI controller.

- The warm cohort contains the ten full 48-item pages at offsets 0 through
  432. Complete first `000001` and deep `000410` tooltips were observed, and
  three loaded-window revisits added no new page requests.
- The original first-viewport idle interval at 285-330 seconds had no Tab,
  focus preparation or other input. Tail warm reached the real `005000` end
  and returned to the visible first viewport at actual elapsed 374.970 seconds,
  before the 375-second input deadline. A repeated numeric first-item tooltip
  was omitted at that boundary. The returned first viewport and the repeated
  tooltip observation are distinct facts; no missing tooltip is fabricated and
  no new every-hover requirement is added to the original contract.
- All three tail/first cycles have complete endpoint tooltips, with no blank
  wall observed. The 570-630-second library idle interval had no input.
  The sole Home release input projects to elapsed 637.069 seconds and aligns
  with actual UTC elapsed 637.283 seconds. The final tail observation aligns
  with actual elapsed 681.032 seconds. The complete 690-870-second Home idle
  interval had no input.
- Actual UTC close inputs were Account at 08:28:58.957, Sign out at
  08:29:04.483, confirmation at 08:29:09.755 and Alt+F4 at 08:29:15.352.
  These are actual elapsed 870.259, 875.785, 881.057 and 886.654 seconds.
  The [original App observer](../../artifacts/lumen-acceptance/current-code-20261015/app-final16-continuation-formal-06/app-exit.json)
  records actual exit 0 and a closed retained handle at
  08:29:15.7308461 UTC, actual elapsed 887.033 seconds.

The [collector completion](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/run-01/collector-completion.json)
records 178 continuous five-second samples, zero statistics/marker failures,
unchanged complete App/fixture payloads and `OwnedApplicationExited` at
887.533 seconds. The [coordinator terminal](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/coordinator-run-01/coordinator-terminal.json)
records all 14 phase markers. Root collected actual exit 0 from the original
App, collector and coordinator tool streams (terminal chunks `030a9d`,
`5a7c96` and `d65de8`). Normal exit inside the declared close window permits
178 or 179 samples; no new 180-sample minimum is imposed.

The original Computer Use images were inspected in the actual tool outputs.
Saved UI evidence contains metadata, not pixel files. Provider focus fields,
request cohorts and collector completion are not substitutes for that pixel
authority or for resource adjudication.

## Resource result: investigate retained memory

The [resource observations](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt06/RESOURCE-OBSERVATIONS.md)
retain the complete fixed windows and triage findings. The finite UI sequence
is covered within the observation limit above, but the resource result is not
accepted. The comparison checkpoints retain these
private-memory values and cycle handle counts:

| Checkpoint | Private memory (MiB) | Change from preceding checkpoint (MiB) | Handles |
| --- | ---: | ---: | ---: |
| Warm | 609.875 | -- | -- |
| Cycle 1 | 647.094 | +37.219 | 2,416 |
| Cycle 2 | 631.559 | -15.535 | 2,434 |
| Cycle 3 | 708.836 | +77.277 | 2,436 |

Two private-memory increments exceed the unchanged 32 MiB investigation
threshold, and the library-cycle handles rise monotonically. The threshold
opens investigation; it is not an automatic hard failure or a requirement for
an unlimited leak-free run. Attribution of these signals remains unresolved,
so **PerformanceNotAccepted** remains the result.

The actual Home transition releases 52.855 MiB of private memory and 790
handles. Across the complete 37-point Home interval, private memory moves from
656.141 to 658.273 MiB (+2.133 MiB), while handles fall from 1,638 to 1,607.
The query count remains 38, image count remains 785 and active images remain
zero; measured CPU use is 22.464% of one core. These bounded Home observations
do not explain the earlier library retention or establish general convergence.

## Preserved failures and helper repair

Earlier attempts retain their outcomes: formal01 aborted after the 150-second
warm deadline with only 240 items (192.363 seconds / 39 samples retained);
formal02's first 680 seconds remain useful evidence, but its
coordinator failed on a dynamic-state IO32 read; formal03 warmed only 432
items; formal04 exceeded the confirmation/close cap; formal05 retains its
third-row WarmLibraryIdle viewport deviation and cycle-3 duplicate-tooltip
gap despite in-cap normal exit 0. Neither formal02's 180 samples/14 tool
records nor any later recovery relabels an earlier attempt as a whole pass.

The test helper repair is limited to publication I/O. Dynamic-state reads use
`FileStream` with `FileShare.ReadWrite | FileShare.Delete` and bounded retries
only for IO32/33. `Save-CollectorState` uses a unique temporary file and four
publication attempts with 25/50/100 ms waits only for IO5/32/33. Sampling,
timing, thresholds, sample fields and validation text remain equivalent
outside the repaired publisher. The [publisher repair receipt](../../artifacts/current-acceptance-20261017-continuation/formal-final16-attempt03/PUBLISHER-REPAIR-RESULT-01.json)
retains the original failed pressure attempt and the later actual 30-second
Windows production publisher/reader stress and negative-case pass. These
helper results do not change a product resource outcome.

## Remaining boundaries

The [final cleanup receipt](../../artifacts/current-acceptance-20261017-continuation/resources/final-cleanup/final-cleanup.json)
and [cleanup report](../../artifacts/current-acceptance-20261017-continuation/resources/final-cleanup/RESULTS.md)
record final owned cleanup and restoration comparison. App37624 has actual
normal exit 0 and a closed original handle. Canonical fixture Stop terminated
fixture26332 with exit -1; its original observer completed with actual tool
exit 0 and a closed handle, and owned port 19010 has no listeners. No active
owned App, fixture or proxy remains. The complete Displays, Audio and Narrator
comparison has zero differences; stored text scale is 100 and client-area
animation remains enabled. These read-only comparisons do not establish
physical HDR/audio output or independently measured effective WinUI text scale.

LAN Find, public Save and dependent account restoration remain user-deferred
and unaccepted. Actual Shell notification-card/button and JumpList clicks
retain the isolated-profile boundary. AMD hardware is absent; exact IEC61937
formats are rejected by the recorded endpoints; official Emby 4.9.5.0 ASS-Off
still has its recorded HTTP500 route failure; and its progressive PGS response
omits the display set required by the original 41-second contract. These
specific hardware/server limits remain scoped as recorded in the
[historical closeout](current-code-closeout-20261015.md#significant-external-blockers).

Earlier playback, subtitles, PiP, HDR, accessibility and DPI results retain
their named candidates and original limits. The
[historical 2026-10-15/16 closeout](current-code-closeout-20261015.md) is preserved
in full. CI, release, licensing/signing, installation/distribution and actual
PR/issue writes remain outside this acceptance scope. The user separately
authorized committing and pushing the current source snapshot to `main`;
source publication does not change the recorded acceptance results.
