# Poster decoder lifecycle baseline

This independent NativeAOT mode isolates the actual product poster decode block from library paging, cards, image caching, media playback, and repeated HTTP requests. It links `src/EmbyClient.App/Services/NativePosterDecoder.cs`; the publication manifest hashes that helper explicitly. It is not a library acceptance test or a substitute for the playback lifecycle receipts.

The fixed large synthetic fixture must already be running on loopback port 18962. The probe verifies the exact synthetic server ID, name, and version before authenticating with the fixture account. It downloads only item `large-000001`'s PNG once, verifies its PNG signature and 480 by 720 dimensions, saves its bytes and SHA-256, logs out, and disposes the HTTP client before measurement. Every cycle uses the same in-memory bytes. No real server or media playback is involved.

```powershell
$publishDirectory = Join-Path (Get-Location) ('artifacts/probes/native-build-poster-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& tools/EmbyClient.NativeProbe/Publish-Probe.ps1 -OutputDirectory $publishDirectory
$runDirectory = Join-Path (Get-Location) ('artifacts/probes/poster-baseline-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'build-manifest.json') -Destination $runDirectory
$probe = Start-Process -FilePath (Join-Path $publishDirectory 'EmbyClient.NativeProbe.exe') -ArgumentList @('--output-dir', $runDirectory, '--poster-lifecycle') -WindowStyle Hidden -PassThru
$probe.Id
```

The probe opens its own Image-only window without requesting activation. After ten seconds of idle, eighty cycles call the linked helper using the same rasterization-scale calculation as the library. The helper preserves the baseline `DataWriter.StoreAsync().AsTask(token)` and `BitmapImage.SetSourceAsync(...).AsTask(token)` operations. The synchronous callback assigns `Image.Source` while the stream is still inside its original using scope. The caller observes two `CompositionTarget.Rendering` callbacks, clears the source, and observes two further callbacks. These are render-boundary observations, not screenshot pixels or compositor presentation evidence. A missing render callback times out explicitly.

Resources are sampled after each cycle method has returned and its source has been cleared. The report contains process handles, threads, private bytes, working set, managed bytes, total managed allocations, and natural collection counts for all three generations. There is no `GC.Collect`, finalizer-drain request, low-latency/no-GC region, explicit WinRT async-operation `Close`, or bitmap reuse. Final idle samples occur at two, ten, and thirty seconds after the last cycle.

`BaselineCompleted` means that all eighty bounded decode/bind/render/clear observations and the final idle sampling completed. It is deliberately not `Passed`. Raw samples, late-ten versus cycles 11-20 medians, and the last-forty slopes support investigation; no playback resource gate is applied to this distinct experiment. Natural collection counts and startup decoder work must be considered when interpreting growth. A one-Image baseline does not model hundreds of simultaneously realized library cards, network caching, scrolling, navigation cancellation, or long-duration stability.

Any subsequent lifetime control must have a separate mode, build/run identity, clearly stated changed variable, and fresh report. A control must not overwrite this baseline or silently modify the product helper.
