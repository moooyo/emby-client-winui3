# Synthetic boundary controls

These opt-in controls make a small set of HTTP boundaries repeatable. They are development fixtures, not Emby behavior or proof of native rendering, client cleanup, credential deletion, or race handling. The default server behavior is unchanged when all controls are absent. Controls are fixed at process startup; there is no reset or mutation endpoint.

## Configuration

| Argument | Behavior |
| --- | --- |
| `--item-detail-failure <item-id>:<attempt>` | Return HTTP 503 for exactly that numbered valid item-detail request. |
| `--item-detail-delay <item-id>:<attempt>:<milliseconds>` | Delay exactly that numbered valid item-detail request before sending JSON or the configured failure. |
| `--first-media-delay-ms <milliseconds>` | Gate media responses for the first valid media session, starting at its first authenticated GET or HEAD. All concurrent GET, HEAD, and range requests for that same session share the original deadline. |
| `--stop-delay-ms <milliseconds>` | Delay every valid playback Stop before recording its state change. |
| `--logout-delay-ms <milliseconds>` | Delay every authenticated Logout before revoking the fixture token. |

Boundary delays are integers from 0 to 30000 milliseconds. Zero disables a media, Stop, or Logout delay. An item-detail delay must be positive. Item-detail attempts are integers from 1 to 10000. At most 32 distinct item-and-attempt pairs can be configured; repeated failure or delay flags for the same pair are rejected. One failure and one delay may be combined for a pair. Unknown item IDs are rejected before listening. Detail controls may name items from the optional large library when that catalog is enabled.

Only an authenticated, authorized `GET /emby/Users/{UserId}/Items/{ItemId}` for an existing item increments its configured per-item attempt counter. Library listings, image requests, negotiation, unauthorized requests, and unknown items do not consume it. Concurrent requests receive distinct attempt numbers in the order they enter the counter lock. An attempt is consumed when it enters, including when the client subsequently cancels it. Later requests remain independent of a delayed request; a canceled request does not re-arm its failure or delay.

The media delay is associated with the first valid media session rather than an individual TCP request, so canceling one HEAD or opening a second range cannot bypass its deadline. Canceling all waiting requests does not restart the bounded deadline. Later sessions are unaffected. After the deadline, requests for the original session continue immediately. A failed authentication or invalid play-session ID cannot consume this gate. No headers or media bytes are sent by the media route before the gate releases.

Stop and Logout delays happen before their synthetic state mutations. If the server observes request cancellation while waiting, the corresponding Stop is not recorded and the token is not revoked. Once the delay has released, cancellation is not a transactional rollback of an already applied operation. These semantics are deliberately stated fixture behavior; real servers can commit an operation even when a client does not receive its response.

Logout uses the same token selection as authentication: a nonempty `X-Emby-Token` header takes precedence, with `api_key` as the fallback. Both accepted token forms can be revoked, with or without a configured delay.

Use a fresh process and separate port for each acceptance scenario. Keep an existing instance and its artifacts intact. For example, after generating real color-and-tone media:

```powershell
dotnet run --project tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release -- --media-dir tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds --port 18964 --item-detail-failure 1001:2 --item-detail-delay 1002:2:15000
```

The example fails the second detail request for `1001` and holds the second detail request for `1002`. Inspect the actual first request before choosing an attempt for a UI scenario: entering a details page, starting playback, and preparing a queue entry can each request details, and client caching can affect that sequence. Do not infer which request occurred from the intended action alone.

For a separate opening/cleanup scenario:

```powershell
dotnet run --project tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release -- --media-dir tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds --port 18965 --first-media-delay-ms 20000 --stop-delay-ms 12000 --logout-delay-ms 12000
```

Choose independent runs when a first-media gate, Logout, or whole-window close would otherwise prevent a later scenario. A long delay may exceed a client timeout; record the resulting client state and actual server cancellation rather than treating the configured duration as an observed wait. The synthetic source is still direct MP4 only and does not exercise active-encoding cleanup, HLS, or subtitle delivery.

## Observe the boundary

Read `/_fixture/stats` on the selected port. The additive `BoundaryControls` object contains:

- `Configuration`: enabled flags and bounded controls, containing only validated synthetic item IDs.
- `ActiveDelays`: controlled HTTP requests currently awaiting their delay, not a count of open media sockets or all server requests.
- `RequestCount` and `EventCount`: cumulative monotonic counts for controlled requests and their observations.
- `FirstMediaSessionId`: the synthetic playback session that consumed the media gate, when enabled and consumed.
- `Events`: the most recent 200 control events. Older observations are evicted without resetting cumulative counters.

Each event has a monotonic `Sequence`, a per-request `RequestSequence`, `Operation`, `Phase`, UTC `Timestamp`, configured delay, and applicable synthetic item/session IDs or detail attempt. `StatusCode` appears only on a completed handler. Operations are `ItemDetail`, `MediaOpening`, `Stop`, and `Logout`. Phases are:

| Phase | Meaning |
| --- | --- |
| `Entered` | A valid controlled request reached the boundary. |
| `DelayReleased` | This request may proceed to its response or mutation; the configured wait completed. Zero-delay detail attempts also emit this phase. |
| `Completed` | The response/mutation handler returned normally, with its final HTTP status. This is not proof that the client received every byte or rendered media. |
| `Canceled` | The request observed cancellation before the handler completed. For cancellation after `DelayReleased`, inspect other state before concluding no mutation occurred. |
| `Faulted` | The controlled handler threw another exception. Exception text and request content are not recorded. |

`ConfiguredDelayMilliseconds` is the configuration, not measured wait time. In particular, later requests sharing the media gate can wait less than that duration or zero. Compare event timestamps for a particular `RequestSequence`. A pending `Entered` event is evidence of a server request, while an actual client screenshot/status is still required to establish that the product was in Opening or detail preparation at the same time.

All detail requests for an item with any configured detail control are observed, including untargeted attempts that return normally. Other item details produce no control events. All media requests for the gated first session are observed, even after its delay expires. Request bodies, raw URLs, tokens, passwords, arbitrary item names, file paths, and exception messages are absent. Existing playback events and Stop counts remain separate and retain their existing bounded-history semantics.

## Focused command-line checks

The package-free harness is intentionally independent of the product test projects and the solution. It starts only its own loopback fixture processes on newly allocated ephemeral ports, uses in-memory synthetic credentials, and cleans up only those processes and its unique temporary directory. It never connects to a configured Emby server, accesses saved accounts, or operates the desktop. Existing fixture instances are not restarted.

Build into a new artifact directory, then run:

```powershell
dotnet build tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release --output tools/EmbyClient.FixtureServer/artifacts/boundary-check-build
dotnet run --project tools/EmbyClient.FixtureServer/Tests/FixtureBoundaryChecks.csproj --configuration Release -- D:\Code\emby-client-winui3\tools\EmbyClient.FixtureServer\artifacts\boundary-check-build\EmbyClient.FixtureServer.dll
```

Adjust the absolute DLL path for the checkout location. The harness generates 4096 test-only bytes and minimal synthetic metadata solely to check byte delivery, range processing, and delayed headers. They are not playable MP4, never passed to a decoder, and must not be reused for product/native acceptance.

Checks cover default behavior, detail-attempt scoping, one-shot failure, combined failure/delay, concurrent request independence, cancellation observations, the shared media gate across HEAD and late range requests, independent sessions, Stop and Logout mutation timing, header/query token revocation, token-free statistics/logs, the 200-event bound, and invalid configuration rejection. They validate the fixture controls; product boundary acceptance remains separate.

The [2026-09-10 receipt](verification/boundary-checks-20260910.json) retains all 40 successful assertions and source hashes. The separate Release server build has zero warnings/errors and DLL SHA-256 `0ceb41558f534424d6e2d71051a7c1d8930ed6d738ac97452b4f3547baff99c9`. These 40 harness checks are not added to the product's earlier 440-test total. The reviewed implementation also fixes the fixture's previously inconsistent query-token Logout behavior. No existing fixture instance or saved product account was changed during these checks.
