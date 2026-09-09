# Conditional library lifetime observation

This tool adds numeric observation to the actual application's `LibraryView`. It does not create a replacement library, mock image cache, or decoder. The normal app defaults to `LibraryObservation=false`: the external partial implementation is absent from `Compile`, and unimplemented `partial void` hooks and their argument evaluation are removed by the C# compiler. There is no observation timer, weak registry, or log in that normal build.

The enabled build observes the existing item collection, poster dictionaries, and original image lifecycle. `NativePosterDecoder` is unchanged: `SetSourceAsync` completes, the original ownership check assigns `Image.Source` inside its apply callback, and the stream is released afterward. Added hooks are synchronous counters/weak tracking only. They do not close WinRT operations, change cancellation, add caching, assign other images, scroll, inspect automation peers, or force GC.

## Publish separately

Coordinate the build window with other app/probe work first. The script does not launch the app or operate the desktop.

```powershell
./tools/EmbyClient.LibraryObservation/Publish-Observation.ps1 -VerifyNormalBuild
```

An optional `-OutputDirectory` must select a new directory beneath this tool's `artifacts` directory. Defaults use a unique UTC timestamp and suffix. Separate intermediate directories isolate the ordinary Release build and the observation AOT publish from regular app/probe outputs. The existing `artifacts/aot` directory is not changed.

The script requires the repository's Windows/.NET toolchain and `rg`. It records source hashes before/after compilation, fails if they change, saves `compile-inputs.json` showing default exclusion and explicit inclusion, and records the executable hash and resolved packages. `library-observation-build.json` identifies an observation output and enables its runtime logger. Keep the entire publish folder together. Do not distribute it as a normal build or use its measurements to certify an uninstrumented artifact.

After publication, the operator may launch the separate executable and interact with the real app. No launch command is run by the script. Use the same window size, display scale, account fixture, scroll/revisit input, and external process sampler for comparisons. Do not add UIA traversal or forced GC to make results look stable.

## Runtime output and bounds

The sole runtime output is `library-observation.jsonl` beside the observation executable. All values are numeric; field names are fixed. No titles, item/server/account IDs, credentials, paths, URIs, exception text, or raw API data are recorded. A row is sampled initially and about every five seconds on the UI dispatcher. The observer stops after ten minutes, before exceeding 1 MiB, when the view unloads, or on an observation I/O failure. It does not interfere with business error handling. The first scale can be zero if `XamlRoot` is not yet attached.

The file uses `CreateNew`: an existing log is never overwritten or combined with a subsequent launch. Archive that run explicitly or publish into a new directory before another observation. A missing/truncated log is not a pass. The build manifest contains build provenance only; it is separate from runtime numeric records.

| Field group | Meaning |
| --- | --- |
| `ElapsedMilliseconds` | Monotonic time since observer initialization; correlate with a separately recorded launch time and process sampler |
| `ItemsCount` | Actual `ViewModel.Items.Count`; not total server library size |
| `PosterSubscriptionsCount`, `PosterRequestsCount` | Actual private dictionaries; requests cover the full view pipeline, not only server HTTP activity |
| `BoundPosterSourcesCount` | Subscribed Image controls whose Source is non-null at sampling time; not a native allocation count |
| `LoadedTotal`, `UnloadedTotal` | Poster event callback totals; repeated load/unload of the same control increments these counters |
| `TagChangedTotal` | Received registered Image Tag-change callbacks, including detail notifications that the existing handler does not reload |
| `AssignedTotal` | Original guarded `image.Source = bitmap` assignments that actually executed |
| `ClearedTotal` | Original `Source = null` assignments, including assignments when Source was already null; not a count of native deallocations |
| `WeakImageTrackedCount`, `WeakImageAliveCount`, `WeakImageEvictions` | A 2,048-slot weak ring of distinct currently tracked CLR Image wrappers; eviction limits the observed cohort |
| `WeakBitmapTrackedCount`, `WeakBitmapAliveCount`, `WeakBitmapEvictions` | A 2,048-slot weak ring of bitmaps that reached the completed decoder's apply callback, before the ownership check; failed/cancelled decodes that never call apply are outside this cohort |
| `Gen0Collections`, `Gen1Collections`, `Gen2Collections`, `ManagedBytes` | Natural runtime GC counters and `GC.GetTotalMemory(false)`; no collection or finalizer drainage is requested |
| `RasterizationScale` | Current XamlRoot scale, or zero when unavailable |

Weak registries store only `WeakReference<T>`, never persistent Image/BitmapImage targets. Counting temporarily tests those references synchronously. A live CLR wrapper is not a measured native pixel/texture allocation; a dead wrapper does not prove that all corresponding native resources have been released. Ring entries may be dead, and evicted still-live objects are no longer counted. These counts must not be presented as the total native image population.

The enabled observer itself allocates weak references and small JSON buffers and adds a dispatcher timer/file writes. Those costs appear in managed/process measurements and can affect natural collection timing. This is an instrumented comparison, not a zero-overhead profile. There is no memory-growth threshold, pass judgment, automatic capture, forced cleanup, or UI assertion in this tool.

## Build checkpoint, not runtime evidence

On 2026-09-09, `Publish-Observation.ps1 -VerifyNormalBuild` completed the ordinary Release build and the enabled Native AOT publish using .NET SDK 10.0.301. The ordinary build had zero errors and the existing generated WinUIEx `Icon` warning (`CS0618`); the observation publish introduced no additional reported warning. Source hashes remained unchanged during verification.

- [Compile input receipt](artifacts/build-publish-20260909-032512117-618b4637/compile-inputs.json): the default property was `false` with zero observation source entries; the enabled property was `true` with exactly one entry and `PublishAot=true`.
- [Observation publish and manifest](artifacts/publish-20260909-032512117-618b4637/library-observation-build.json): `EmbyClient.App.exe` SHA-256 is `f6635f5d9125ca37fc8353980cc95316658b3cc9eb9c1173b38a41ba400683c2`.
- [Separate build evidence directory](artifacts/build-publish-20260909-032512117-618b4637/): normal and observation build logs and intermediate outputs remain isolated from the usual application output.

The observation executable was not launched during this checkpoint, and no runtime JSONL had been produced. Build success verifies the conditional code paths and publication, not the counter values or a library-memory conclusion. These linked build files are ignored local artifacts; the hash and stated compile results above preserve the checkpoint identity in this document.

## Subsequent Reset cleanup verification

The later `c28f7cfb20006d7fc7f5e44a7072ebcbd6f0e28643091b94cf04aad6eba87b3f` observation build includes collection Reset cleanup and `BoundPosterSourcesCount`. Ordinary Release and enabled Native AOT publication both succeeded, with no new warning beyond CS0618. The [retained build manifests, numeric logs, and selected screenshots](verification/README.md) identify the before/after builds.

Actual desktop interaction loaded six pages (288 of 5,000 items), scrolled and revisited content, returned Home, reopened the library, opened details, and returned. Six Home samples recorded one bound source and zero pending poster requests despite 46 retained subscriptions. Posters displayed normally after navigation. This establishes the targeted Source cleanup; the unequal process-sampling windows do not establish a memory improvement or a long-duration pass. See the [full observation report](../../docs/implementation/large-library-validation.md).
