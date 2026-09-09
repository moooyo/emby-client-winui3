# Conditional library lifetime observation

The [2026-09-10 session-triggered build](verification/session-triggered-20260910/build.json) is `257b0f462e2b0d45c34eb6632e2c29edf184310047905fc48bc069d5e3212cbe` (18,014,720 bytes). Ordinary Release and enabled Native AOT passed with unchanged source inputs. [Compile inputs](verification/session-triggered-20260910/compile-inputs.json) and [IL callsite inspection](verification/session-triggered-20260910/start-callsite-check.json) confirm the default build has no start hook and the enabled build calls it only from LibraryView.SetSessionAsync. The initial metadata-check helper failed to serialize its report under reflection-disabled JSON; after changing that helper to source generation, the read-only check passed. No application runtime result is supplied by this compilation checkpoint.

This tool adds numeric observation to the actual application's `LibraryView`. It does not create a replacement library, mock image cache, or decoder. The normal app defaults to `LibraryObservation=false`: the external partial implementation is absent from `Compile`, and unimplemented `partial void` hooks and their argument evaluation are removed by the C# compiler. There is no observation timer, weak registry, or log in that normal build.

The enabled build observes the existing item collection, poster dictionaries, and original image lifecycle. `NativePosterDecoder` is unchanged: `SetSourceAsync` completes, the original ownership check assigns `Image.Source` inside its apply callback, and the stream is released afterward. Schema 2 introduced a conditional async wrapper that counts the original helper's starts, completions, cancellations, and failures and rethrows every exception; schema 3 retains it unchanged. Its state machine is an additional observation cost; the default build excludes it through `LIBRARY_OBSERVATION`. Synchronous partial hooks count load attempts and actual presentation-clock/update callbacks. They do not close WinRT operations, change cancellation, add caching, assign other images, scroll, inspect automation peers, or force GC.

## Publish separately

Coordinate the build window with other app/probe work first. The script does not launch the app or operate the desktop.

```powershell
./tools/EmbyClient.LibraryObservation/Publish-Observation.ps1 -VerifyNormalBuild
```

An optional `-OutputDirectory` must select a new directory beneath this tool's `artifacts` directory. Defaults use a unique UTC timestamp and suffix. Separate intermediate directories isolate the ordinary Release build and the observation AOT publish from regular app/probe outputs. The existing `artifacts/aot` directory is not changed.

The script requires the repository's Windows/.NET toolchain and `rg`. It records source hashes before/after compilation, fails if they change, saves `compile-inputs.json` showing default exclusion and explicit inclusion, and records the executable hash and resolved packages. `library-observation-build.json` identifies an observation output and enables its runtime logger. Keep the entire publish folder together. Do not distribute it as a normal build or use its measurements to certify an uninstrumented artifact.

After publication, the operator may launch the separate executable and interact with the real app. No launch command is run by the script. Use the same window size, display scale, account fixture, scroll/revisit input, and external process sampler for comparisons. Do not add UIA traversal or forced GC to make results look stable.

## Runtime output and bounds

The sole runtime output is `library-observation.jsonl` beside the observation executable. All values are numeric; field names are fixed. No titles, item/server/account IDs, credentials, paths, URIs, exception text, or raw API data are recorded. Schema 3 starts observation at the first `LibraryView.SetSessionAsync` call, which the product invokes after authentication succeeds. The constructor creates no log or observation timer, so waiting for manual sign-in does not consume the sampling period. The first row precedes the library view-model's session loading; subsequent rows are sampled about every five seconds on the UI dispatcher. A numeric UTC anchor and process ID identify the run.

The first session makes one start attempt per LibraryView instance. This attempt is consumed before checking the build marker or opening the log: a missing marker, existing log, or I/O failure does not cause an automatic retry on another session. Once started, the observer stops after thirty minutes, before exceeding 1 MiB, when the view unloads, or on an observation I/O failure. Later `SetSessionAsync` calls cannot restart or truncate that observation, including after expiration or failure. The logger does not interfere with business error handling. The first scale can be zero if `XamlRoot` is not yet attached.

The file uses `CreateNew`: an existing log is never overwritten or combined with a subsequent launch. Archive that run explicitly or publish into a new directory before another observation. A missing/truncated log is not a pass. The build manifest records schema 3, `StartTrigger=FirstLibrarySetSessionAfterAuthentication`, `StartTriggerCode=1`, and `OneShot=true`; it contains build provenance, not evidence that a session was observed.

Process memory, CPU, GC counts, and cumulative allocation can already include a long pre-sign-in wait at the first row. Use that first session sample as their baseline rather than assuming process startup or zero cumulative activity. Poster and presentation callback totals count only the active observation interval; controls created earlier may enter a weak registry only on a later observed callback. Existing schema 2 recordings retain their constructor-based time origin and executable identities. Their elapsed clocks must not be spliced into a schema 3 session.

| Field group | Meaning |
| --- | --- |
| `ElapsedMilliseconds` | Monotonic time since the first authenticated library-session start attempt opened its log; use the UTC anchor to match external process samples, not the app launch time |
| `SchemaVersion`, `StartTrigger`, `UtcUnixTimeMilliseconds`, `ProcessId` | Schema 3; numeric StartTrigger 1 means the first LibraryView.SetSessionAsync call after authentication. UTC Unix milliseconds and process ID match external samples; fields in one row are sequential reads, not an atomic snapshot |
| `ItemsCount` | Actual `ViewModel.Items.Count`; not total server library size |
| `PosterSubscriptionsCount`, `PosterRequestsCount` | Actual private dictionaries; requests cover the full view pipeline, not only server HTTP activity |
| `BoundPosterSourcesCount` | Subscribed Image controls whose Source is non-null at sampling time; not a native allocation count |
| `LoadedPosterControlsCount`, `AttachedPosterControlsCount`, `RealizedPosterControlsCount` | Subsets of the subscription dictionary: IsLoaded, a GridViewItem ancestor, and an ancestor still present in the current realization table. DetailPoster is excluded from the last two. These are not a census of every XAML container or a guarantee that a particular item binding is current. |
| `DetailPosterBound`, `LibraryIsHome`, `LibraryHasDetails`, `LibraryIsBusy`, `LibraryVisibility` | Numeric view state; booleans use 0/1 and Visibility uses its enum value. Visibility does not describe ancestor visibility. |
| `LoadedTotal`, `UnloadedTotal` | Poster event callback totals; repeated load/unload of the same control increments these counters |
| `TagChangedTotal` | Received registered Image Tag-change callbacks, including detail notifications that the existing handler does not reload |
| `AssignedTotal` | Original guarded `image.Source = bitmap` assignments that actually executed |
| `ClearedTotal` | Original `Source = null` assignments, including assignments when Source was already null; not a count of native deallocations |
| `PosterLoadRequestedTotal`, `PosterLoadRejectedTotal`, `PosterLoadDeduplicatedTotal` | Load method entries, unusable binding returns, and existing-load/source deduplication returns. These do not equal HTTP requests. |
| `DecodeStartedTotal`, `DecodeCompletedTotal`, `DecodeCanceledTotal`, `DecodeFailedTotal`, `DecodeActive`, `DecodePeak` | Full original decoder-helper invocations, including stream setup and the guarded apply callback. Active counts wrapper calls. A completed helper may discard stale decoded output. Canceled means the wrapper received an OperationCanceledException, not confirmed native-operation cancellation. Failure counters include helper or callback exceptions and do not identify a native decoder error category. While the observer is active, started equals completed + canceled + failed + active. |
| `PresentationClockEnabled`, `PresentationClockTicks`, `PositionUpdateCalls` | Actual PlayerView timer transitions/callbacks and position-update method entries while observation is active. Method entries can return early; these are not decoded-frame counts or total dispatcher activity. |
| `WeakImageTrackedCount`, `WeakImageAliveCount`, `WeakImageEvictions` | A 2,048-slot weak ring of distinct currently tracked CLR Image wrappers; eviction limits the observed cohort |
| `WeakBitmapTrackedCount`, `WeakBitmapAliveCount`, `WeakBitmapEvictions` | A 2,048-slot weak ring of bitmaps that reached the completed decoder's apply callback, before the ownership check; failed/cancelled decodes that never call apply are outside this cohort |
| `Gen0Collections`, `Gen1Collections`, `Gen2Collections`, `ManagedBytes` | Natural runtime GC counters and `GC.GetTotalMemory(false)`; no collection or finalizer drainage is requested |
| `ManagedAllocatedBytesApproximate` | Process-wide cumulative `GC.GetTotalAllocatedBytes(false)`; approximate allocation volume, not retained bytes |
| `LastGcIndex`, `LastGcHeapSizeBytes`, `LastGcFragmentedBytes`, `LastGcCommittedBytes` | Information from the latest reported GC, which can predate the sample. An unchanged GC index means these values do not describe a new collection; none are current native-heap or GPU bytes. |
| `ProcessPrivateBytes`, `ProcessWorkingSetBytes`, `ProcessHandleCount`, `ProcessCpuMilliseconds` | Current process counters, including the observer's own cost; cumulative CPU time is not instantaneous CPU percentage. Working set and private bytes must remain distinct. |
| `ObserverCompletedSamples`, `ObserverCompletedSampleAllocatedBytes` | Previous completed synchronous samples and their measured current-thread allocations. The current row is accounted for in a later row. This excludes callback weak-reference allocation, async-wrapper costs, other threads, and native work, so it is not a complete overhead subtraction. |
| `RasterizationScale` | Current XamlRoot scale, or zero when unavailable |

Weak registries store only `WeakReference<T>`, never persistent Image/BitmapImage targets. Counting temporarily tests those references synchronously. A live CLR wrapper is not a measured native pixel/texture allocation; a dead wrapper does not prove that all corresponding native resources have been released. Ring entries may be dead, and evicted still-live objects are no longer counted. These counts must not be presented as the total native image population.

The enabled observer itself allocates weak references, async wrappers, process-query objects, and JSON buffers and adds a dispatcher timer/file writes. Those costs appear in managed/process measurements and can affect natural collection timing. Its counts stop at the observation boundary; no final row or zero-active-decoder result is guaranteed after unloading, expiration, a byte cap, or I/O failure. This is an instrumented comparison, not a zero-overhead profile. There is no memory-growth threshold, pass judgment, automatic capture, forced cleanup, or UI assertion in this tool.

Schema 3 retains the schema 2 attribution counters and moves their observation boundary to the first authenticated library session. It is intended to distinguish ongoing product callbacks/decodes from managed allocation growth and process-only growth during an explicitly authorized observation session. A build receipt alone supplies none of those runtime conclusions. The existing 779B normal-build acceptance, schema 2 recordings, and older schema-less observation files retain their own executable identities and evidence limits.

## Schema 2 build checkpoint: 2026-09-10

The [retained build manifest](verification/attribution-20260910/build.json) records an ordinary Release build and the separate schema 2 Native AOT publication, with source inputs unchanged throughout and still matching at archival. The [compile inputs](verification/attribution-20260910/compile-inputs.json) show zero observation sources and no observation symbol by default, versus one source and the symbol when enabled. [Read-only IL metadata inspection](verification/attribution-20260910/il-observation-exclusion.json) found zero observation methods/types in the normal assembly and 18 methods/3 types in the enabled intermediate assembly. It did not load or run either application.

The observation executable is `b4c41de8bffe5c2d7add38960b9507673b2077781ec55501f96e585586cc8fb9`, 18,012,672 bytes. Both compilation paths retain only the existing generated WinUIEx CS0618 warning. The observation application was not launched, no runtime JSONL existed at archival, and the normal product AOT was not republished by this stage. These results do not resolve the preceding private-memory trend.

The subsequent [startup-safe observation build](verification/startup-attribution-20260910/build.json) is `ba6afb8d18e86e33db8cb4c9bd1edf1f5146ab47182c97ec1b0fc8d57752940f`. It includes the later settings-initialization repair and has its own [compile inputs](verification/startup-attribution-20260910/compile-inputs.json). Its manifest's normal-build flag is false because this invocation published only the observation variant; the [separate normal AOT receipt](../../docs/implementation/verification/startup-initialization-aot-20260910.json) records 0E84F458. Both source audits completed before the newly authorized runtime batch.

## Build checkpoint, not runtime evidence

On 2026-09-09, `Publish-Observation.ps1 -VerifyNormalBuild` completed the ordinary Release build and the enabled Native AOT publish using .NET SDK 10.0.301. The ordinary build had zero errors and the existing generated WinUIEx `Icon` warning (`CS0618`); the observation publish introduced no additional reported warning. Source hashes remained unchanged during verification.

- [Compile input receipt](artifacts/build-publish-20260909-032512117-618b4637/compile-inputs.json): the default property was `false` with zero observation source entries; the enabled property was `true` with exactly one entry and `PublishAot=true`.
- [Observation publish and manifest](artifacts/publish-20260909-032512117-618b4637/library-observation-build.json): `EmbyClient.App.exe` SHA-256 is `f6635f5d9125ca37fc8353980cc95316658b3cc9eb9c1173b38a41ba400683c2`.
- [Separate build evidence directory](artifacts/build-publish-20260909-032512117-618b4637/): normal and observation build logs and intermediate outputs remain isolated from the usual application output.

The observation executable was not launched during this checkpoint, and no runtime JSONL had been produced. Build success verifies the conditional code paths and publication, not the counter values or a library-memory conclusion. These linked build files are ignored local artifacts; the hash and stated compile results above preserve the checkpoint identity in this document.

## Subsequent Reset cleanup verification

The later `c28f7cfb20006d7fc7f5e44a7072ebcbd6f0e28643091b94cf04aad6eba87b3f` observation build includes collection Reset cleanup and `BoundPosterSourcesCount`. Ordinary Release and enabled Native AOT publication both succeeded, with no new warning beyond CS0618. The [retained build manifests, numeric logs, and selected screenshots](verification/README.md) identify the before/after builds.

Actual desktop interaction loaded six pages (288 of 5,000 items), scrolled and revisited content, returned Home, reopened the library, opened details, and returned. Six Home samples recorded one bound source and zero pending poster requests despite 46 retained subscriptions. Posters displayed normally after navigation. This establishes the targeted Source cleanup; the unequal process-sampling windows do not establish a memory improvement or a long-duration pass. See the [full observation report](../../docs/implementation/large-library-validation.md).
