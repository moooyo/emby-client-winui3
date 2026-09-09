# Large-library Native AOT validation: limited result

Date: 2026-09-09. The actual AOT UI browsed six pages of a 5,000-item synthetic library and revisited part of the loaded range without a reported freeze, error, or final poster mismatch. **Memory behavior still requires investigation:** private bytes increased during equal-content revisits and remained elevated after returning Home. This run does not establish a memory plateau, a leak-free implementation, full-library loading, or a realized-container count.

## Identity and evidence boundaries

- Operator-provided AOT executable SHA-256: `313A94C3C7BE6821B489E49A2A7AC705617D47CC53EFEA3984641C262305D659`.
- App process: `EmbyClient.App`, PID `62256`.
- Data source: the synthetic large-library fixture on IPv4 loopback port `18962`, configured for 5,000 additional movies and a 100 ms image delay. No real Emby server or account was exercised by this trial.
- CSV: [memory-20260909-103129-6664310b.csv](../../artifacts/ui-validation/2026-09-09/large-library-313a/memory-20260909-103129-6664310b.csv), 36 samples, normally about five seconds apart.
- Screenshots: [first page](../../artifacts/ui-validation/2026-09-09/large-library-313a/first-page.png), [forward budget](../../artifacts/ui-validation/2026-09-09/large-library-313a/forward-budget.png), [revisit 1](../../artifacts/ui-validation/2026-09-09/large-library-313a/revisit-1.png), and [revisit 2](../../artifacts/ui-validation/2026-09-09/large-library-313a/revisit-2.png). These raw files are local, ignored validation artifacts; the measurements below make the report readable without them.

The configured sampler ran for 180 seconds, but its actual rows cover **02:31:29.872 through 02:34:25.563 UTC**, ending at elapsed `175.710` seconds. There is no row exactly at 180 seconds. The operator's second revisit read at 02:34:31.686 UTC is outside the configured sampling window as well as after the final row. Do not merge it into the CSV series. A separate, single read-only Home-idle sample was taken at 02:35:58.002 UTC for this analysis. No UI action, forced garbage collection, process restart, or repeated trial was performed during analysis.

The exact Windows build, window dimensions, display scale, and true realized XAML-container count were not recorded in this CSV. They are not inferred from the screenshots or executable hash.

## What the UI actually covered

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

## Memory and handles

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

## Image activity and the peak-of-six boundary

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

## Follow-up and status

The finite functional scrolling exercise is complete at the stated six-page scope. **A focused memory investigation remains warranted.** A subsequent bounded trial should retain passive process sampling through every revisit and a final idle period, use reproducible equal-content input, and correlate allocation/retention data for managed card models, decoded image/native resources, visual containers, and automation peers. It should also correlate client permit release with server request start/end/cancellation if the concurrency peak needs attribution.

Do not force garbage collection merely to make an acceptance graph drop, equate working set with live heap, infer realized-container counts from UIA entry counts, or label this result as “all 5,000 items tested” or “no memory growth.” No such follow-up experiment or product change was performed by this analysis.
