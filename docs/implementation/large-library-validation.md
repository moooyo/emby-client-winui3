# Large-library Native AOT validation: limited result

Date: 2026-09-09. Two actual Native AOT UI trials each loaded six pages, or 288 records, from a 5,000-item synthetic library. The initial trial completed browsing and revisits without a reported freeze, error, or final poster mismatch. A subsequent fresh-process control completed its scroll/revisit sequence without requesting UI Automation tree/text retrieval. **Memory behavior still requires investigation:** private bytes increased and remained elevated after returning Home in both captures. These finite observations do not establish a memory plateau, a leak-free implementation, full-library loading, or a realized-container count. The two input sequences differ, so their memory deltas are not a controlled estimate of UI Automation overhead.

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

## Follow-up and status

The two finite scrolling exercises are complete at their stated six-page scopes. **A focused memory investigation remains warranted.** Control A includes both revisits and a final sampled Home interval, but does not identify the retained allocations or establish convergence. The next scoped investigation is an independent image-decoder lifecycle control that reuses the product decoder; that work is being handled separately, and no result from it is included here. Further attribution may require allocation/retention data for managed card models, decoded image/native resources, visual containers, and automation peers. A concurrency investigation would need client permit-release and server request start/end/cancellation timelines.

Do not force garbage collection merely to make an acceptance graph drop, equate working set with live heap, infer realized-container counts from UIA entry counts, or label these results as “all 5,000 items tested” or “no memory growth.” The control A analysis only read the supplied artifacts and updated this report; it did not change product code, run probes, request further UI actions, or take additional process samples.
