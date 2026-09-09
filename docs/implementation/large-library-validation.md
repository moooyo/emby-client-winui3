# Large-library Native AOT validation: limited results

Initial report date: 2026-09-09. The first five finite Native AOT UI captures each loaded six initial pages, or 288 records, from a 5,000-item synthetic library. **Targeted collection Reset cleanup was observed in an instrumented build, but private bytes still increased in the later normal AOT build.** That normal build completed five revisits and a separate Home-idle sampling period, during which private bytes rose from 294.13 to 302.65 MiB while image-request counters stayed unchanged. The 2026-09-10 normal 779B follow-up below also retains an unresolved memory result. The earlier cleanup evidence remains valid, and memory stability remains unverified. These observations do not establish a memory plateau, a leak-free implementation, full-library loading, or a realized-container count. Different input timing, starting states, and instrumentation prevent a controlled performance comparison across runs.

## Initial trial: identity and evidence boundaries

- Operator-provided AOT executable SHA-256: `313A94C3C7BE6821B489E49A2A7AC705617D47CC53EFEA3984641C262305D659`.
- App process: `EmbyClient.App`, PID `62256`.
- Data source: the synthetic large-library fixture on IPv4 loopback port `18962`, configured for 5,000 additional movies and a 100 ms image delay. No real Emby server or account was exercised by this trial.
- CSV: [memory-20260909-103129-6664310b.csv](../../artifacts/ui-validation/2026-09-09/large-library-313a/memory-20260909-103129-6664310b.csv), 36 samples, normally about five seconds apart.
- Screenshots: [first page](../../artifacts/ui-validation/2026-09-09/large-library-313a/first-page.png), [forward budget](../../artifacts/ui-validation/2026-09-09/large-library-313a/forward-budget.png), [revisit 1](../../artifacts/ui-validation/2026-09-09/large-library-313a/revisit-1.png), and [revisit 2](../../artifacts/ui-validation/2026-09-09/large-library-313a/revisit-2.png). These raw files are local, ignored validation artifacts; the measurements below make the report readable without them.

The configured sampler ran for 180 seconds, but its actual rows cover **02:31:29.872 through 02:34:25.563 UTC**, ending at elapsed `175.710` seconds. There is no row exactly at 180 seconds. The operator's second revisit read at 02:34:31.686 UTC is outside the configured sampling window as well as after the final row. Do not merge it into the CSV series. A separate, single read-only Home-idle sample was taken at 02:35:58.002 UTC for the initial analysis. No UI action, forced garbage collection, process restart, or repeated trial was performed during that analysis.

The exact Windows build, window dimensions, display scale, and true realized XAML-container count were not recorded in this CSV. They are not inferred from the screenshots or executable hash.

## Initial trial: what the UI actually covered

The first large-library request occurred at 02:31:51.027 UTC. The operator reported five complete cards plus five partially visible cards in the first screenshot. A UI Automation inspection reported 40 entries; that number is not a count of realized XAML containers, decoded bitmaps, or loaded pages.

The fixture recorded these six UI-phase page requests, each returning 48 items:

| UTC request time | Start index | Returned | Total library count |
| --- | ---: | ---: | ---: |
| 02:31:51.027 | 0 | 48 | 5,000 |
| 02:32:18.258 | 48 | 48 | 5,000 |
| 02:32:40.059 | 96 | 48 | 5,000 |
| 02:32:50.742 | 144 | 48 | 5,000 |
| 02:33:04.136 | 192 | 48 | 5,000 |
| 02:33:17.398 | 240 | 48 | 5,000 |

This covers **six page responses / 288 items**, not the proposed ten-page budget and not all 5,000 items. The forward screenshot reached visible synthetic items 226–235. The operator then returned to the top and scrolled to the region containing items 51–55 twice, using the same approximately 5,530 scroll position. No later large-library page request appears in the observed fixture history; the next query was Home/Resume at 02:34:50.943 UTC.

The initial CSV value `LastLargePageStart=5000` came from an earlier HTTP boundary probe at approximately 02:20:07 UTC, before this UI run. It is **not evidence that the UI loaded 5,000 items**. Fixture request/image counters are cumulative and include earlier probes. Compare timestamps and counter changes, rather than treating their starting values as zero.

The operator reported that posters ultimately matched their corresponding items on all captured screens and that there was no freeze or error. These observations support the narrow browsing/revisit result. They do not validate all search/filter combinations, all library pages, exact visual virtualization, or long-term resource stability. `InjectedPlaybackInfoFailures` was still zero in the final read, so this report does not claim a Retry/playback trial.

## Initial trial: memory and handles

These are OS process measurements in MiB, not managed-heap or GPU-allocation measurements.

| CSV checkpoint | UTC | Private MiB | Working-set MiB | Handles | Image requests |
| --- | --- | ---: | ---: | ---: | ---: |
| Initial Home sample | 02:31:29.872 | 116.46 | 143.46 | 1,148 | 2 |
| First sampled point after the first page | 02:31:54.997 | 139.89 | 166.46 | 1,231 | 42 |
| First sample reporting server peak 6 | 02:32:55.233 | 178.66 | 202.77 | 1,278 | 162 |
| Six-page phase | 02:33:20.318 | 191.95 | 215.98 | 1,283 | 257 |
| Near the first revisit read | 02:34:05.501 | 229.29 | 253.33 | 1,298 | 313 |
| Final CSV row | 02:34:25.563 | 242.94 | 266.74 | 1,319 | 313 |

Across the CSV, private bytes rose **126.48 MiB**, working set rose **123.28 MiB**, and handles rose **171** from the first to the last row. Between 02:34:05.501 and 02:34:25.563, the query count stayed at 12 and image counters stayed at 313 requested / 225 completed / 88 canceled, while private bytes rose another **13.65 MiB** and handles ended 21 higher. That interval did not fetch additional library pages or images according to the fixture counters.

The separately reported reads must remain distinguishable from those CSV points:

| Read source / phase | UTC | Private MiB | Handles | Boundary |
| --- | --- | ---: | ---: | --- |
| Operator, settled first page | 02:32:18.059 | 147.97 | 1,231 | 42 image requests, active 0, peak 4 |
| Operator, forward pass | Approximately 02:33:18 | 188.06 | 1,269 | 236 image requests, active 4, peak 6 |
| Operator, revisit 1 | 02:34:05.547 | 229.97 | 1,298 | Separate read near, but not identical to, the CSV point |
| Operator, revisit 2 | 02:34:31.686 | 245.60 | 1,309 | Outside the 180-second CSV sampling window |
| One analysis read, Home idle | 02:35:58.002 | 250.17 | 1,360 | Approximately 67 seconds after the Home/Resume query |

The last Home-idle read also measured working set **273.51 MiB**, peak working set **279.51 MiB**, and 59 threads. Private bytes were **133.71 MiB** above the first CSV point and **4.57 MiB** above the second manual revisit read. Handles fluctuated during the run; they were not monotonically increasing at every checkpoint.

Some growth while appending new pages is expected because loaded DTO/card models remain in the current collection. However, the continued growth with unchanged page/image counters and the elevated Home-idle result mean that this trial has **not demonstrated convergence**. These measurements do not identify which allocations remain live or distinguish a leak from allocator/cache retention, deferred cleanup, UI Automation/renderer activity, or another source of process allocations.

## Initial trial: image activity and the peak-of-six boundary

The changed-counter CSV points were:

| UTC | Requested | Completed | Canceled | Active at sample | Historical peak |
| --- | ---: | ---: | ---: | ---: | ---: |
| 02:31:29.872 | 2 | 2 | 0 | 0 | 1 |
| 02:31:54.997 | 42 | 42 | 0 | 0 | 4 |
| 02:32:30.122 | 77 | 77 | 0 | 0 | 4 |
| 02:32:55.233 | 162 | 126 | 36 | 0 | 6 |
| 02:33:20.318 | 257 | 171 | 86 | 0 | 6 |
| 02:33:50.433 | 302 | 214 | 88 | 0 | 6 |
| 02:34:05.501 | 313 | 225 | 88 | 0 | 6 |
| 02:35:58.002, separate final read | 314 | 226 | 88 | 0 | 6 |

All 36 CSV samples happened to observe zero active image requests. That does not mean transfers were never active: the operator's intermediate read observed four, and the server retained a peak of six. The five-second cadence cannot reconstruct short-lived request overlap. At the final read, `226 + 88 = 314`, with active zero, so no outstanding image-request backlog was represented by those counters.

The two concurrency measures have different boundaries:

| Measure | Counted lifetime | Scope |
| --- | --- | --- |
| [ImageCache semaphore](../../src/EmbyClient.App/Services/ImageCache.cs) | Permit acquisition through the client's image operation and its `finally` release, including completion/cancellation handling | One cache instance |
| [Fixture image activity](../../tools/EmbyClient.FixtureServer/FixtureRouter.cs) | Server route entry, the optional image delay, response writing or cancellation handling, through `ImageEnded()` in `finally` | All image requests reaching that fixture process |

A client operation can complete or observe cancellation and release its permit before the server's corresponding handler finishes its own cleanup. As a possible ordering, two retiring server handlers can overlap four newly admitted client requests and produce a server count of six while the cache holds no more than four permits. Completion/flush scheduling and other clients are also counter-boundary considerations. The first sampled rise to six coincided with the first observed cancellations, which is **consistent with cancellation overlap**, but does not establish that this was the exact cause.

There is no per-request client/server timeline or live cache-permit trace in this capture. Therefore the peak does not prove a client semaphore violation, and it must not be dismissed as proven harmless cleanup either. The existing mocked-transport concurrency test measures the client boundary; it does not instrument Kestrel handler teardown. The fixture's historical server peak should be retained as diagnostic evidence rather than used as a strict `<= 4` acceptance assertion.

## Control A: screenshots without requested UIA trees

### Identity and evidence

Control A used a fresh `EmbyClient.App` process, PID `64476`, with the same executable SHA-256 as the initial trial. The operator reports that every `sky.get_window_state` call from startup through shutdown used `include_text: false`. Interaction used screenshots, coordinates, and Tab input, with no requested UIA tree/text retrieval. This was still an automated interaction/capture session, not a session without observation or input tooling.

The [observation log](../../artifacts/ui-validation/2026-09-09/large-library-control-a/observations.json) records a consistent window size of **1268 x 834** and all page, scroll, revisit, and Home stages. Its screenshots were saved directly in their returned JPEG format, including [page 6](../../artifacts/ui-validation/2026-09-09/large-library-control-a/page-6.jpg), [forward 3](../../artifacts/ui-validation/2026-09-09/large-library-control-a/forward-3.jpg), [revisit 1](../../artifacts/ui-validation/2026-09-09/large-library-control-a/revisit-1.jpg), [revisit 2](../../artifacts/ui-validation/2026-09-09/large-library-control-a/revisit-2.jpg), and [final Home view](../../artifacts/ui-validation/2026-09-09/large-library-control-a/home-idle-end.jpg). The data remained the fixed synthetic fixture data. These local artifacts do not represent real Emby compatibility validation.

The [control CSV](../../artifacts/ui-validation/2026-09-09/large-library-control-a/memory-20260909-105338-7bea3187.csv) contains **60 samples**, covering **02:53:38.700 through 02:58:34.675 UTC**. The configured duration was 300 seconds; the final row has elapsed `295.996` seconds, rather than a row exactly at 300 seconds. Both revisits and approximately one minute after the Home transition are inside this CSV. The operator's later 03:02:15.236 UTC read is outside it.

### Loaded pages and input sequence

The [fixture snapshot](../../artifacts/ui-validation/2026-09-09/large-library-control-a/fixture-after.json) retains cumulative history from earlier work. Sequences 15 through 20 belong to the six large-library page requests in this control:

| Sequence | UTC request time | Start index | Returned | Total library count |
| ---: | --- | ---: | ---: | ---: |
| 15 | 02:54:03.694 | 0 | 48 | 5,000 |
| 16 | 02:54:17.509 | 48 | 48 | 5,000 |
| 17 | 02:54:32.338 | 96 | 48 | 5,000 |
| 18 | 02:54:43.779 | 144 | 48 | 5,000 |
| 19 | 02:54:56.681 | 192 | 48 | 5,000 |
| 20 | 02:55:14.464 | 240 | 48 | 5,000 |

All six pages were explicitly loaded before the forward scroll sequence. Three successive 5,530-unit scroll inputs reached regions beginning around items 51, 101, and 151. Two subsequent cycles used a -40,000 input to return to the top, followed by the same 5,530 input to revisit the region around item 51. The observation timestamps for the two revisits are 02:56:53.504 and 02:57:18.679 UTC. The Home observation starts at 02:57:33.273 UTC, following the fixture's Home/Resume request at 02:57:32.679 UTC, sequence 21. No additional large-library page request appears after sequence 20.

This is another **six-page / 288-record** exercise. The visible ranges do not establish a count of realized XAML containers or decoded images. No UIA entry count was collected for this control.

### Process measurements

Except for the baseline and final row, the table selects the first CSV sample after the corresponding recorded screenshot stage. These are separate process samples, not values read from the screenshots.

| CSV checkpoint | UTC | Private MiB | Working-set MiB | Handles | Cumulative image requests |
| --- | --- | ---: | ---: | ---: | ---: |
| Initial Home sample | 02:53:38.700 | 118.94 | 144.08 | 1,143 | 315 |
| After page 1 | 02:54:08.838 | 144.08 | 169.03 | 1,216 | 355 |
| After page 6 | 02:55:19.057 | 167.10 | 191.62 | 1,214 | 355 |
| After forward 3 | 02:56:34.288 | 209.13 | 231.47 | 1,261 | 490 |
| After revisit 1 | 02:56:54.343 | 243.66 | 265.30 | 1,302 | 544 |
| After revisit 2 | 02:57:19.433 | 250.40 | 272.10 | 1,312 | 544 |
| First Home-idle row | 02:57:34.480 | 257.74 | 279.45 | 1,427 | 545 |
| Final CSV row | 02:58:34.675 | 259.78 | 281.15 | 1,379 | 545 |

From the first to the last CSV row, private bytes increased **140.84 MiB**, working set increased **137.07 MiB**, and handles ended **236** higher. There are also intervals with no new page or image requests: from 02:55:19.057 to 02:55:54.167, the query count stayed at 20 and image requests at 355, while private bytes rose from 167.10 to 178.38 MiB, an increase of **11.28 MiB**.

The two sampled revisit endpoints differ by **6.74 MiB** with the same page and image counters. The path between them was not monotonic: private bytes reached **260.68 MiB** at 02:57:14.426, then fell **10.28 MiB** to 250.40 MiB at 02:57:19.433. A report that only lists increasing checkpoints would omit this observed reduction.

The **13 Home-idle rows** span approximately 60.194 seconds. Across them, private bytes increased **2.04 MiB**, working set increased **1.70 MiB**, and handles decreased **48**. Query and image counters remained unchanged at 21 queries and 545 requested / 455 completed / 90 canceled images, with active zero. This interval shows much smaller growth than the browsing phase, but is too short to establish a durable plateau.

The operator separately read **271.44 MiB private bytes, 1,298 handles, and zero active fixture image requests at 03:02:15.236 UTC**, just after the final Home screenshot at 03:02:14.945. This was approximately 282 seconds after the Home observation and 221 seconds after the last CSV row. Private bytes were **11.66 MiB** above the last CSV point and **152.50 MiB** above the baseline. Handles were 81 below the final CSV value. There are no intervening CSV samples, so the trajectory during this gap is unknown. The operator then signed out and closed the app; no post-sign-out memory result is claimed.

### Fixture counters and comparison limits

During the control CSV, image counters changed from **315 requested / 227 completed / 88 canceled** to **545 / 455 / 90**. The increments reconcile as **230 requests = 228 completions + 2 cancellations**. The fixture served an additional 2,040,962 compressed image bytes. Transfer byte totals do not measure decoded/native image memory. The final counters represent no outstanding image handlers.

The historical server peak was already **6 in the first control sample** and remained 6. It was inherited from the earlier fixture activity and does not establish that control A reached six concurrent handlers. The largest instantaneous value in this CSV is four. That sampling result does not establish an exact peak between samples either. The initial trial's client/server lifetime distinction still applies. `InjectedPlaybackInfoFailures` was already 1 at the control baseline and stayed 1, so the earlier playback/Retry events retained in the snapshot are not control A results.

**Private-byte growth was reproduced without requested UIA tree/text retrieval.** Requested tree inspection is therefore not required for the growth observed in this control, and the evidence does not support assigning UIA inspection as its sole cause. This result does not quantify any additional UIA cost or exclude screenshot/input activity, ordinary WinUI automation-peer behavior, rendering, image decoding, allocator retention, or other sources of process allocations.

The original trial interleaved page loading and scrolling, reached items 226–235, included UIA inspection at a different pace, and used a shorter sampling window. Control A loaded six pages first, then followed fixed forward and revisit inputs, and recorded a longer Home interval. Its window dimensions are recorded, whereas the original report lacks that measurement. These differences prevent a strict timing-matched A/B comparison. Comparing their final memory deltas cannot isolate the effect of UIA, and no comparative improvement or regression is claimed. The planned repeated UIA control B was canceled; it is not a missing completed result.

## LibraryObservation: before and after collection Reset cleanup

### Build identity and observer scope

The next two captures used the actual app with [conditional LibraryObservation instrumentation](../../tools/EmbyClient.LibraryObservation/README.md). They are separate Native AOT executables, not the earlier `313A` executable or an uninstrumented distribution build:

- Before Reset cleanup: PID `42488`, SHA-256 `f6635f5d9125ca37fc8353980cc95316658b3cc9eb9c1173b38a41ba400683c2`, with [stage observations](../../artifacts/ui-validation/2026-09-09/large-library-observed-f663/observations.json), [numeric observer log](../../artifacts/ui-validation/2026-09-09/large-library-observed-f663/library-observation-before.jsonl), and [fixture snapshot](../../artifacts/ui-validation/2026-09-09/large-library-observed-f663/fixture-after.json).
- After Reset cleanup: PID `27732`, SHA-256 `c28f7cfb20006d7fc7f5e44a7072ebcbd6f0e28643091b94cf04aad6eba87b3f`, with [stage observations](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/observations.json), [numeric observer log](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/library-observation-after.jsonl), and [fixture snapshot](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/fixture-after.json).

Both stage logs record `includeText: false` and a 1268 x 834 window. The observer reports a rasterization scale of 1.5 once attached. The source fixture remains synthetic. No forced GC was used.

The [tracked evidence directory](../../tools/EmbyClient.LibraryObservation/verification/README.md) preserves both build manifests and complete numeric logs, the after-build UI timestamps, and selected original screenshots. The larger process CSV and fixture snapshots linked above remain local artifacts.

The [observer implementation](../../tools/EmbyClient.LibraryObservation/LibraryView.Observation.cs) samples the actual view dictionaries, item count, callback totals, natural collection counts, and a bounded weak-reference cohort about every five seconds. It uses `GC.GetTotalMemory(false)`, not a request to collect or drain finalizers. The enabled timer, weak references, serialization buffers, and file writes add their own allocations. Its results must not be presented as a zero-overhead profile of a normal build.

`PosterSubscriptionsCount` counts subscribed Image controls, not visible cards or all realized XAML containers. `PosterRequestsCount` covers the view's asynchronous poster pipeline, not the ImageCache semaphore or only HTTP requests. `BoundPosterSourcesCount`, added in the after log, counts non-null `Image.Source` properties among those subscribed controls. Weak bitmap entries cover completed decoder callbacks; they do not measure native textures, and cleared sources can still have uncollected CLR wrappers. Both logs report zero weak-ring evictions, so observed drops in their live counts were not caused by that ring evicting entries.

### UTC sampling windows and observer clocks

The process CSV and observer JSONL have different origins. CSV rows contain UTC timestamps and sampler elapsed time. JSONL `ElapsedMilliseconds` starts when the observer initializes inside the view and contains no UTC initialization timestamp. The supplied stage logs do not supply that missing anchor. The JSONL tables below therefore retain their own elapsed values and identify item-state transitions; they are not assigned fabricated UTC timestamps or aligned to CSV elapsed zero.

| Capture | Process CSV rows and UTC window | Final CSV elapsed | Observer rows and elapsed window |
| --- | --- | ---: | --- |
| Before | [60 rows](../../artifacts/ui-validation/2026-09-09/large-library-observed-f663/memory-20260909-113206-6e759837.csv), 03:32:06.209–03:37:02.220 | 296.028 s | 120 rows, 0–596,150 ms |
| After | [60 rows](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/memory-20260909-114920-4dbc022a.csv), 03:49:20.634–03:54:16.608 | 295.985 s | 120 rows, 0–596,127 ms |

Both process samplers were configured for 300 seconds. Neither has a row exactly at 300 seconds. The observer has a separate ten-minute bound; its approximately 596-second final row is not evidence of a ten-minute process-memory CSV.

The before CSV includes both revisit screenshots, at 03:35:58.761 and 03:36:32.831 UTC, and the Home transition at 03:36:53.454. It ends only **8.77 seconds after that Home observation**, with just two subsequent rows, at 03:36:57.208 and 03:37:02.220. The later Home screenshot at 03:41:58.061 has no corresponding process sample in this CSV.

The after capture has an operator context-restoration gap of **186.989 seconds** between the page-1 and page-2 screenshots, at 03:49:45.058 and 03:52:52.047 UTC. This gap is not a measured page-loading latency. The following after-build stages show exactly which UI work falls outside its process CSV:

| After-build screenshot stage | UTC | Process CSV coverage |
| --- | --- | --- |
| Page 6 | 03:53:24.403 | Inside |
| Forward 3, around item 151 | 03:53:56.280 | Inside |
| Revisit 1, around item 51 | 03:54:16.091 | Inside; final row follows 0.517 s later |
| Revisit 2, around item 51 | 03:54:45.701 | Outside |
| Home after library | 03:54:53.293 | Outside |
| Library reopened | 03:55:20.443 | Outside |
| Detail opened | 03:55:27.605 | Outside |
| Returned to library | 03:55:36.742 | Outside |
| Signed out | 03:55:43.800 | Outside |

The after fixture history independently records six initial page responses, sequences 31–36, at offsets 0, 48, 96, 144, 192, and 240, each returning 48 of 5,000 records. The corresponding first/last request times are 03:49:44.205 and 03:53:24.052 UTC. It then records Home at 03:54:51.717, a reopened first page at 03:55:18.737, and another first page on return at 03:55:35.144. These later 48-item responses do not expand the initial traversal to all 5,000 items. The before history likewise confirms six initial pages, sequences 23–28, followed by Home, sequence 29.

The operator completed three forward scrolls to approximately items 51, 101, and 151 and two top-to-51 revisits in the after build. Posters were reported normal through [Home](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/home-after-library.jpg), [library reopen](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/library-reopened.jpg), [detail](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/detail-reopened.jpg), and [return](../../artifacts/ui-validation/2026-09-09/large-library-reset-c28f/back-to-library.jpg). That is a finite functional observation, not a process-memory measurement for those stages.

### What the before log establishes

While 288 items remained loaded, the before log shows natural collection counts advancing and live weak bitmap targets falling to 46 despite additional guarded assignments:

| Observer elapsed ms | Assigned total | Live weak bitmap targets | GC counts, G0/G1/G2 | Managed bytes |
| ---: | ---: | ---: | --- | ---: |
| 290,472 | 76 | 75 | 1 / 1 / 1 | 9,302,976 |
| 295,472 | 125 | 46 | 2 / 2 / 2 | 6,391,568 |
| 400,778 | 293 | 214 | 2 / 2 / 2 | 15,860,224 |
| 405,797 | 322 | 46 | 3 / 3 / 3 | 4,490,200 |

This shows that many observed bitmap wrappers were collectible under natural runtime activity. It does not establish that all native resources were released, explain the process-private-byte trend, or turn the earlier memory-growth result into a pass.

In the before log's subsequent one-item Home state, elapsed 445,889–596,150 ms, subscriptions remained 46, pending poster requests were zero, and live weak bitmap targets remained 77 with all GC counts still at 3. `UnloadedTotal` remained zero. This is consistent with containers remaining loaded across navigation, but subscription and weak counts alone cannot count their bound sources. **The before log has no `BoundPosterSourcesCount` field; no retrospective bound-source value is inferred.**

### What Reset cleanup now establishes

The current [LibraryView](../../src/EmbyClient.App/Views/LibraryView.xaml.cs) handles collection `Reset` by canceling poster work and clearing sources across subscribed/requested images, including cached containers that have not raised `Unloaded`. The after log directly observes the resulting source state:

| Observer state | Elapsed ms | Items | Subscriptions | Pending poster requests | Bound poster sources | Live weak bitmap targets |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Six initial pages | 465,810 | 288 | 40 | 0 | 40 | 40 |
| Later loaded-range sample after natural GC | 531,003 | 288 | 46 | 0 | 46 | 46 |
| Home, first of six matching samples | 551,036 | 1 | 46 | 0 | 1 | 77 |
| Home, last matching sample | 576,061 | 1 | 46 | 0 | 1 | 77 |
| Library reopened | 581,090 | 48 | 46 | 0 | 40 | 117 |
| Detail state | 586,104 | 0 | 47 | 0 | 1 | 118 |
| Returned library, final observer row | 596,127 | 48 | 47 | 0 | 40 | 158 |

All six Home samples between 551,036 and 576,061 ms have one item, 46 subscriptions, zero pending requests, and **one bound source**. Thus 45 of the observed subscribed images have a null source at each of those Home samples. Combined with the Reset code path and subsequent normal poster display, this verifies the targeted old-source cleanup behavior in this instrumented run. Retained subscriptions do not mean those controls still retain their old bitmaps through `Image.Source`.

Live weak targets remain 77 at Home, and later reach 158 after new decodes, while GC counts stay at 3. Source clearing does not require every CLR wrapper to disappear before the next natural collection. Conversely, these observations cannot certify native bitmap/texture deallocation or resolve the full process-memory increase. The observer ends with the returned 48-item library state, so it supplies no zero-resource result after the later sign-out screenshot.

### Process memory remains an open result

The endpoint measurements below retain the observed growth. They terminate at different UI stages and must not be compared as an effect-size estimate for Reset cleanup:

| Capture and endpoint | Baseline private MiB | Final private MiB | Private change MiB | Baseline / final handles |
| --- | ---: | ---: | ---: | --- |
| Before, shortly after Home | 116.27 | 267.19 | +150.92 | 1,152 / 1,434 |
| After, first revisit | 113.12 | 265.85 | +152.73 | 1,163 / 1,322 |

Working set increased 146.45 MiB in the before CSV and 149.04 MiB in the after CSV. The after gap and shorter coverage of the completed UI sequence prevent a timing-matched performance A/B. In particular, **the supplied after-build CSV has no private-byte, working-set, or handle sample for the observed Home cleanup, second revisit, reopened library, detail, or return**. The observer's separate managed-byte readings do not fill this gap. The counters show a specific retention path was cleared; they do not show that all private-byte growth was solved or that the normal uninstrumented app meets a long-term memory criterion.

The fixture counters remain cumulative. The before CSV adds 232 image requests, reconciled as 229 completions and 3 cancellations. The after CSV adds 229 requests, 227 completions and 2 cancellations. Its later fixture snapshot includes another two completed requests, outside that CSV window. Historical server peak 6 and the earlier injected playback failure were already present at both baselines. Neither is a new concurrency peak or Retry result for these captures.

## Independent poster-decoder baseline

The separate [80-cycle NativePosterDecoder receipt](../../tools/EmbyClient.NativeProbe/verification/poster-decoder-baseline.json) is now available. It uses the linked product helper, one Image, and the same synthetic PNG after the single download and HTTP/account cleanup. All 80 decode/bind/render-boundary/clear cycles completed without forced GC. Late median handle growth and the last-forty-cycle handle slope were zero; the receipt's result is `BaselineCompleted`.

This baseline has no library paging, card collection, cache, or concurrent poster population. It also records increasing private/managed bytes, no natural GC collections, and allocations from its own cumulative reporting. It does not reproduce the library's handle climb or establish a library-memory root cause. **An isolated one-Image baseline is not large-library acceptance** and does not replace any failed or incomplete memory result above.

## Normal Native AOT B1CB: five revisits and separate Home sampling

### Identity and completed scope

The operator identifies this as the normal Native AOT app, **without the LibraryObservation observer**, using PID `28856` and executable SHA-256 `B1CBF350968A64456C975932591CDD62FC08D3ED72BDD6892925CEF76F9AD7A5`. The [preserved stage log](../../tools/EmbyClient.LibraryObservation/verification/normal-b1cb/observations.json) records `includeText: false` and a 1268 x 834 window. This capture has no observer weak-target, bound-source, or GC counters. The prior instrumented source-cleanup result must remain associated with its own executable.

The operator reports no forced GC, build, media conversion, or native probe during these samples. Other concurrent work was limited to lightweight reading or source edits. Window observations used screenshots without requested UIA tree/text retrieval.

All **six pages / 288 records were preloaded before the main process sampler started**. The [preserved fixture snapshot](../../tools/EmbyClient.LibraryObservation/verification/normal-b1cb/fixture-after.json) records page sequences 2–7, at offsets 0 through 240, each returning 48 of 5,000 records. The final page request was at 04:14:55.492 UTC and its screenshot at 04:14:55.886. Thus the first process sample at 04:14:56.266, already **165.00 MiB private bytes**, is a loaded-library baseline, not a cold Home baseline. Setup/loading memory growth is outside this CSV.

After setup, the operator completed three forward scrolls to approximately items 51, 101, and 151, then **five** top-to-51 revisits. Their screenshot timestamps were 04:16:49.485, 04:17:12.950, 04:17:37.376, 04:18:23.703, and 04:18:50.981 UTC. Home followed at 04:19:21.450, after the fixture's Home/Resume query at 04:19:20.573. No further library pages appear in the fixture history. The final Home screenshot is at 04:22:55.064.

### Two preserved CSV files with independent clocks

The following are exact copies of the supplied CSV files. They remain separate and retain their original `ElapsedSeconds` values:

| File and purpose | Rows | Configured duration | Actual UTC row window | First / last elapsed seconds |
| --- | ---: | ---: | --- | --- |
| [Main scrolling/revisit CSV](../../tools/EmbyClient.LibraryObservation/verification/normal-b1cb/memory-20260909-121456-8d41aa92.csv) | 60 | 300 s | 04:14:56.266–04:19:52.206 | 0.009 / 295.948 |
| [Separate Home-idle CSV](../../tools/EmbyClient.LibraryObservation/verification/normal-b1cb/home-idle/memory-20260909-121940-73242413.csv) | 36 | 180 s | 04:19:40.291–04:22:35.930 | 0.015 / 175.653 |

Their UTC windows **overlap by 11.915 seconds**, from 04:19:40.291 through 04:19:52.206. Each file has three rows in that overlap, taken at different instants. The tail begins with its own elapsed zero approximately 18.841 seconds after the Home screenshot. Do not concatenate the elapsed columns, add the configured durations as non-overlapping coverage, or describe the main 300-second file as covering the entire later idle period.

Both configured sampling runs completed, but neither has a row exactly at its duration boundary. The last tail row is approximately 194.480 seconds after the Home screenshot. The final Home screenshot occurs another 19.134 seconds after that row and supplies no additional process-memory reading.

### Main capture and repeated content

The following main-CSV checkpoints use the first row after each listed screenshot stage, except for the already-loaded baseline:

| Main CSV checkpoint | UTC | Private MiB | Handles | Image requests |
| --- | --- | ---: | ---: | ---: |
| Six pages already loaded | 04:14:56.266 | 165.00 | 1,194 | 41 |
| After forward 3 | 04:16:21.592 | 204.83 | 1,242 | 176 |
| After revisit 1 | 04:16:51.687 | 246.99 | 1,310 | 222 |
| After revisit 2 | 04:17:16.740 | 244.56 | 1,320 | 222 |
| After revisit 3 | 04:17:41.819 | 259.18 | 1,306 | 222 |
| After revisit 4 | 04:18:26.958 | 274.61 | 1,312 | 222 |
| After revisit 5 | 04:18:52.018 | 283.24 | 1,279 | 222 |
| First row after Home | 04:19:22.110 | 294.13 | 1,390 | 223 |

The revisit endpoints include a reduction from 246.99 to 244.56 MiB, followed by higher later readings. They are not monotonically increasing, and handles fluctuate independently. Image requests remain at 222 across all five sampled revisit endpoints and increase to 223 at Home. Repeated-content process growth therefore remains visible without additional library pages or additional image HTTP requests between those revisit endpoints; these counters do not identify which managed/native allocations caused it.

The main file ends at **294.61 MiB private bytes, 316.70 MiB working set, and 1,382 handles**. Relative to its loaded baseline, those changes are **+129.61 MiB private bytes, +125.80 MiB working set, and +188 handles**. These are main-file deltas only, not an app-startup delta or a sum with the overlapping tail.

### Home tail: memory growth with unchanged network counters

| Separate tail checkpoint | UTC | Private MiB | Working-set MiB | Handles |
| --- | --- | ---: | ---: | ---: |
| First row | 04:19:40.291 | 294.13 | 316.66 | 1,390 |
| Last row | 04:22:35.930 | 302.65 | 325.12 | 1,311 |
| Tail-only change | Approximately 175.639 s between rows | +8.52 | +8.46 | -79 |

All 36 tail rows have **223 image requests, 219 completions, 4 cancellations, zero active images, and 8 queries**. The unchanged completed/canceled totals reconcile with the request total. These are fixture image-route counters, not a count of every HTTP request made by the app. The declining handle count does not establish stable private memory: **private bytes still increase by 8.52 MiB during the separately sampled Home tail**.

In the main capture, the historical server image peak advances from 4 to 5, first sampled at 04:16:06.537. All main and tail instantaneous active-image samples are zero. As in the earlier runs, the server handler lifetime differs from the client's four-permit cache boundary; this sparse data does not attribute the transient peak. The fixture records no playback negotiation, media request, or injected failure in this run.

This normal-build result adds complete five-revisit coverage and a longer sampled Home period without the conditional observer. It does **not** show that private-byte growth has stabilized or that the earlier Reset repair resolved every resource issue. It also is not a strict performance comparison with the earlier two-revisit, cold-Home-baseline, or instrumented captures. No native allocation/GC trace was collected here, so the cause and retained allocation types remain open.

## Playback presentation clock follow-up

Source inspection found that `PlayerView.SetSessionAsync` started the 500 ms presentation timer as soon as an account connected, including on the library page without any playback. The view now polls only while Opening, Playing, Paused, Buffering, or Seeking, or while a display-request release needs another attempt. It recomputes this policy after accepted coordinator/native state callbacks, completed UI operations, and ticks. Negotiating and terminal states already receive their UI updates from events. Active playback, including pause and hidden-view system-media updates, retains its existing polling behavior; this change does not introduce a background-playback or visibility policy.

Before stopping the clock in a terminal state, the view reconciles display ownership against one current context. This matters when a queued callback carries a retired playback ID: that old notification can be correctly ignored while a display request is still active and has never attempted release. A failed live reconciliation keeps the retry signal set; a successful one lets the timer stop. Five new lifecycle cases, plus assertions in existing failure tests, cover missing-context cleanup and this retired-notification sequence. The Platform Release project passes all 93 cases.

The change removes a known unnecessary idle polling path. It does not establish that this path caused the measured private-memory growth. At that source-review checkpoint, no desktop action, player run, timer-count measurement, or new memory sample had been taken for this change. The earlier B1CB observations and the uncompleted isolated clock comparison retain their original limits. The later 779B measurements below are a separate integrated-app result, not an isolated timer comparison.

## Normal Native AOT 779B: 2026-09-10 follow-up

The normal candidate identified by executable SHA-256 prefix `779B23ED` used PID `19652` and the synthetic 5,000-item fixture. Six page responses, at offsets 0, 48, 96, 144, 192, and 240, each returned 48 items: **288 of 5,000 records**. Their actual request timestamps span 2026-09-09 20:12:33.8129459 through 20:14:45.8236384 UTC. The operator completed five visual top-to-middle revisits with the same posters present. These observations do not count realized containers or establish full-library coverage.

The [observation log](verification/ui-779b-20260910/observations.json) records screenshots without requested UIA text/tree retrieval during the sampling windows. Its initial connected-Home observation requested text and was archived before the first sample. `ArchivedAtUtc` is the evidence save time, not an exact screenshot or action timestamp. No video was opened in this process until after all three samplers. The operator reports no builds, native probes, or forced GC during sampling, and no UI operations during the Home-idle sampler. The process later exited after ordinary sign-out; that exit is not a sampled post-sign-out memory result.

The three completed files retain independent elapsed clocks. All timestamps in this table are **2026-09-09 UTC**, despite the local 2026-09-10 directory names:

| CSV | Samples | Actual first / last UTC | Covered seconds |
| --- | ---: | --- | ---: |
| [Library loading/scrolling](verification/ui-779b-20260910/library-memory/memory-20260910-041123-fdfb1c4d.csv) | 60 | 20:11:23.3655986 / 20:16:19.2588669 | 295.8932683 |
| [Forward scrolling/revisits](verification/ui-779b-20260910/revisit-memory/memory-20260910-041744-078fc810.csv) | 60 | 20:17:44.6835000 / 20:22:40.5642511 | 295.8807511 |
| [Separate Home idle](verification/ui-779b-20260910/home-idle/memory-20260910-042609-5a095554.csv) | 36 | 20:26:09.1042115 / 20:29:04.6585347 | 175.5543232 |

The gaps between files are **85.4246331** and **208.5399604 seconds**. Configured durations of 300, 300, and 180 seconds are not continuous measured coverage. In particular, the second CSV does not cover the complete five-revisit sequence. The [separate fifth-revisit checkpoint](verification/ui-779b-20260910/after-fifth-revisit.json), at 20:23:29.3329350 UTC, follows its final row by **48.7686839 seconds** and records **393.55078125 MiB private bytes, 409.51953125 MiB working set, and 1,264 handles**. It must not be merged into the CSV as another scheduled row.

| Window | Private MiB: first / last / change | Working-set MiB: first / last / change | Handles: first / last; sampled range |
| --- | --- | --- | --- |
| Library | 128.38 / 243.71 / +115.33 | 150.98 / 262.84 / +111.86 | 1,118 / 1,233; 1,114–1,233 |
| Revisit | 269.69 / 380.15 / +110.46 | 288.02 / 396.72 / +108.70 | 1,241 / 1,264; 1,241–1,324 |
| Home idle | 402.12 / 416.07 / +13.95 | 418.77 / 432.25 / +13.48 | 1,294 / 1,270; 1,264–1,301 |

For private bytes, each window's first and last values are also its sampled minimum and maximum. Working-set extrema likewise match those endpoints except at Home, where the sampled maximum is 432.27 MiB. These OS process values do not measure live managed heap, native bitmap ownership, or GPU allocations.

The final 60-second lookbacks each contain 12 actual samples spanning 55.151, 55.127, and 55.162 seconds respectively, without interpolation. Their private-byte changes are **+29.56, +18.31, and +4.20 MiB**; ordinary least-squares slopes using actual timestamps are **+27.78, +19.04, and +4.89 MiB/minute**. These finite-window slopes are not forecasts or evidence of a plateau.

During the revisit file's final **165.4283144 seconds**, image counters remain at 252 requests / 238 completions / 14 cancellations / zero active, and QueryCount remains 10. Private bytes nevertheless increase **60.58 MiB**, working set **60.13 MiB**, while handles fall 46. All 36 Home samples have 253 image requests, 239 completions, 14 cancellations, zero active images, and QueryCount 13: **zero new image requests accompany the 13.95 MiB private-byte increase over 175.55 seconds**. Sparse samples cannot reconstruct every short-lived handler, and image counters do not describe every client allocation or every HTTP route.

Memory stability remains **unresolved**. Home growth is smaller within its own window but still positive; two equal final private-byte samples do not establish convergence. These are sequential windows in one process with different work, cache state, and unmeasured gaps, not a strict A/B comparison with each other or earlier builds. No allocation trace, live-object census, or timer-count trace identifies a leak root cause or attributes the trend to the idle-clock or poster-lifecycle repairs. The prior cleanup evidence and native resource receipts retain their separate scopes.

## Follow-up and status

The subsequent [schema 2 attribution tool](../../tools/EmbyClient.LibraryObservation/README.md#schema-2-build-checkpoint-2026-09-10) adds numeric UTC/process anchors, full decoder-helper invocation counts, load attempts/deduplication, actual player clock callbacks, managed allocation/last-GC data, and process CPU/private bytes. It also estimates allocations made by completed synchronous sampling calls, without presenting that partial estimate as all observer overhead. Ordinary Release and an explicitly instrumented AOT build passed; normal IL excludes the observation implementation. No new application run or allocation sample was taken in that code stage. The next runtime sample must distinguish steady counters from continuing decode/timer work before attributing the existing growth; the observer cannot by itself prove retained native objects or a leak root cause.

The 2026-09-10 [code review](lifecycle-hardening.md) separately repairs concrete extra-work paths: containers now invalidate old poster work on recycling, same-card reuse explicitly resumes loading, duplicate Loaded/Tag/phase callbacks share a load, and concurrent same-key cache misses share temporary HTTP coordination. Seven cache concurrency cases and six pure binding-lifetime cases pass in the 440-test consolidated batch. Cache limits and Clear generation isolation are unchanged. At that code-review checkpoint, the new normal AOT candidate had not been launched. The subsequent 779B browsing and process-memory evidence above does not isolate the effect of these repairs or supply a timer-count, native-allocation, or memory-plateau result.

The first five finite scrolling captures cover six initial pages each. The instrumented Reset run confirms normal posters after Home, reopen, detail, and return, and directly confirms the targeted source cleanup. The later normal B1CB run completes five revisits and a separate Home tail, closing that coverage gap for its own executable while still showing increasing private bytes. The subsequent 779B run also completes five visual revisits, with the fifth checkpoint outside its revisit CSV, and retains increasing private bytes in its separate Home sampler. **A focused memory investigation remains warranted.** The independent decoder baseline and natural-GC weak-target reductions narrow observations but do not identify every retained allocation or establish convergence. Further attribution may require managed/native allocation data and the lifecycle of cached visual containers. A concurrency investigation would need client permit-release and server request start/end/cancellation timelines.

Do not force garbage collection merely to make an acceptance graph drop, equate working set with live heap, infer realized-container counts from UIA or subscription counts, or label these results as “all 5,000 items tested” or “no memory growth.” The preceding B1CB archival work used existing evidence and preserved the two CSVs and their stage/fixture JSON files without new measurements. The later presentation-clock code change above has its own source and automated-check scope; it does not change those archived results.
