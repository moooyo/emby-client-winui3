# Bounded large-library acceptance trial

This is a finite development trial against synthetic data, using the actual published Native AOT UI. It supplements unit tests; it does not certify real Emby performance, measure GPU allocations, or prove the absence of memory leaks.

## Fixture configuration

The normal fixture still has its original two libraries and eight catalog objects. Optional `--large-library-items 5000` adds a separate `large-movies` library with exactly 5,000 numbered movies. The mode has a distinct synthetic server identity so that it does not replace the normal fixture's remembered-account record. The count accepts 1 through 10,000. Each item has a unique ID and artwork URL. Twenty-four shared 480 by 720 PNG payloads keep the fixture's own startup and memory bounded; client image-cache entries remain distinct by item/URL. This exercises paging, container recycling, image decoding, and entry eviction, but not worst-case high-entropy poster compression.

Large-mode list responses omit raw media-source details; the details endpoint still supplies the generated playable source. This avoids adding artificial per-card playback payloads to the browsing test. Default small-mode payloads are unchanged.

Publish into an isolated artifact tree so existing fixture services do not lock build outputs:

The fixture project excludes its `artifacts`, `bin`, and `obj` trees from all default SDK items. This is required because changing `--artifacts-path` changes the SDK's automatic output exclusions; otherwise generated C# files from another output layout can enter the next normal solution build. Keep these exclusions when changing artifact locations. The running artifact directory can remain in place while `scripts/Build.ps1` builds the normal project outputs.

```powershell
dotnet publish tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release --artifacts-path tools/EmbyClient.FixtureServer/artifacts/large-library-build --output tools/EmbyClient.FixtureServer/artifacts/large-library-server -p:RestoreLockedMode=true
dotnet tools/EmbyClient.FixtureServer/artifacts/large-library-server/EmbyClient.FixtureServer.dll --media-dir tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds --port 18962 --large-library-items 5000 --image-delay-ms 100 --fail-first-playback-info
```

Use `http://127.0.0.1:18962` with the synthetic `demo` / `demo` account. Do not stop or reconfigure services on 18961 or 19096 for this trial. Image delay is optional, defaults to zero, and is bounded to 0–1,000 ms; a small delay makes cancellation/backlog behavior observable without changing global networking.

`GET /_fixture/stats` adds bounded query history (last 200 requests), page offsets/counts, total image requests, active/peak image requests, completions, cancellations, and bytes served. It never records search text, credentials, raw request headers, or arbitrary parent IDs. These are server observations; they cannot reveal the client's managed-object or visual-tree counts.

## Finite UI and memory procedure

1. Use one freshly launched published AOT process at a fixed window size and display scale. Record its build/commit, PID, Windows build, window dimensions, and scale. Confirm the visible account/server is the synthetic large fixture. Keep playback stopped during the scrolling trial.
2. Start the bounded process sampler below for 180 seconds (hard maximum 300). Leave the home page idle for approximately 10 seconds to obtain a baseline.
3. Open **Synthetic Large Movies (5000)**. Wait for the first page and posters to settle. Capture a screenshot or accessibility snapshot, the displayed total, the visible media-card count, and a UTC timestamp.
4. Scroll forward until ten pages have been requested, or until 90 seconds have elapsed, whichever comes first. With the current client page size, ten pages cover 480 items and end at offset 432. Check the fixture's `Queries` for the observed page size instead of assuming it. Capture another screenshot/visible-card count at the same window geometry.
5. Return to the top, then revisit the already loaded section twice. Do not intentionally fetch farther pages. Capture the same evidence after each pass. Stop this phase after another 45 seconds, even if a UI automation round trip is slow.
6. Return home or switch to another library, then leave the UI idle for the remaining sampler duration. Record final memory/handle values and whether active image requests settle to zero. Stop after the bounded sampling window; record incomplete checkpoints instead of continuing indefinitely.

Run the sampler in a managed terminal session while the UI owner performs those actions:

```powershell
./tools/EmbyClient.FixtureServer/Measure-LargeLibrary.ps1 -ProcessId <actual-AOT-process-id> -DurationSeconds 180 -IntervalSeconds 5
```

The sampler accepts only an `EmbyClient.App` process and the synthetic large fixture on 18962. It records OS private bytes, working set, peak working set, handle/thread counts, and fixture counters to a timestamped CSV under this tool's ignored artifacts directory. It does not read the process command line, media title, server URL, credentials, or UI; it neither forces garbage collection nor modifies the app.

Use a small observation table alongside the CSV:

| Checkpoint | UTC time | Last page offset / returned count | Visible cards at fixed geometry | Private MiB | Working-set MiB | Handles | Observation |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Idle home | | | | | | | |
| First page | | | | | | | |
| Ten-page budget or timeout | | | | | | | |
| Revisit 1 | | | | | | | |
| Revisit 2 | | | | | | | |
| Home and idle | | | | | | | |

Visible accessibility nodes are not an authoritative count of realized XAML containers or native bitmap objects. Some UI Automation providers expose logical/unrealized items. If a trusted visual-tree inspection tool cannot provide a true realized-container count, mark that measurement unavailable; do not rename the accessibility-node count as a visual-tree count. Adding product instrumentation for that measurement is outside this fixture-only change.

## Assessment

- Page IDs must be stable, ordered, and nonoverlapping. The first page must not contain the entire 5,000-item library. The endpoint returns an empty array when `StartIndex` reaches the library size.
- The UI should remain responsive, preserve navigation/search intent, and show appropriate placeholders while artwork loads. Visible cards should correspond to the current viewport, with no stale artwork after scrolling or navigation.
- The fixture's active count spans each server handler through response/cancellation cleanup, while the client's four permits span its own cache operations. These are not synchronized counters: client completion or cancellation can release a permit before the old server handler exits, allowing a transient server peak above four. Treat that peak as diagnostic evidence, not a strict client-concurrency assertion or an automatically explained-away result. Active server requests should drain after settling. Exclude separate manual image probes, and use a request-level trace if attribution is needed. A canceled transfer may finish before the server notices cancellation, so a zero cancellation counter is not itself a failure.
- Some private-memory growth while loading new pages is expected because the current view retains loaded DTO/card models. Compare the two equal-content revisit passes to the warmed state. Continued growth on every identical pass or a growing handle count merits investigation; one retained working-set plateau is not proof of a leak. A further 32 MiB rise on each identical pass can be used as a triage signal, not a universal release threshold.
- Image requests on revisits are expected once more than 128 distinct entries have been seen. Do not require the whole 480-item set to remain cached, and do not infer client cache occupancy from the server's PNG palette size.
- Keep the UI/codec result separate from the synthetic endpoint result. Record actual observations rather than claiming automated acceptance from a successful HTTP probe or unit test alone.

## One-shot Retry trial

`--fail-first-playback-info` is off by default. When enabled, the first valid `POST /Items/{Id}/PlaybackInfo` with `IsPlayback=true` receives HTTP 503 with an empty body. Ordinary item-details GETs and informational negotiation (`IsPlayback=false` or omitted) do not consume the injection. Unknown/nonplayable item IDs do not consume it. Later playback negotiations use normal fixture behavior.

After the scrolling trial, play a synthetic movie once. Record the actual error/Retry UI, click Retry once, and confirm that native playback recovers. Use `InjectedPlaybackInfoFailures=1` and the subsequent `PlaybackInfoCount` / playback events as supporting evidence. The flag resets only when that fixture process restarts; no global network or real server is changed. If the client retries internally before exposing a Retry action, record that actual behavior instead of claiming a manual Retry test passed.
