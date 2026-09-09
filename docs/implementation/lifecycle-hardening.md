# Lifecycle fixes before consolidated validation

The 2026-09-10 code review identified concrete failure and ownership paths independently of desktop observations. The following repairs belong to the new candidate; the earlier 5C3C screenshots do not validate their UI integration.

## Account exit and window shutdown

Local remembered credentials are removed before waiting for playback cleanup. The authenticated in-memory API context remains available for the final playback Stop and encoding cleanup requests; remote logout follows that cleanup. A window closing during an existing account-exit operation waits for that operation instead of taking over its disconnect and disposing the shared HTTP client early.

The public connection-service sign-out operation also persists local removal before remote logout. A delayed older logout never performs another account mutation after its remote wait, preserving a newer sign-in. Cancellation, transport failure, and an already disposed transport cannot skip the earlier local removal.

The existing eight-second window-close deadline remains. This change removes slow playback/network cleanup from the path before local persistence; it does not promise that an indefinitely blocked filesystem or every server cleanup can finish before that deadline. Storage errors remain visible rather than being reported as successful local sign-out.

## Item preparation and pointer ownership

Loading item details can fail before the coordinator accepts a new playback. A queued head may also be edited while its details are loading. The preparation policy now retires the previous request and restores a usable Failed or Idle presentation only while the same session, coordinator, and user intent still own the operation. Failed details have a retry-loading action. Old Ended notifications remain inadmissible, and late cleanup cannot reset a newer playback's controls.

Pointer drags now carry their pointer identity, original playback ID, and gesture generation. Release submits a seek only against that original playback. Capture loss, cancellation, loss of focus, unloading, playback replacement, Stop, and disconnect invalidate the pending drag. Deferred capture-loss handling cannot cancel a later drag that happens to reuse the same mouse pointer and playback.

## Image work and playback cleanup

Recycled library containers must cancel and clear their old image work, then explicitly resume loading when reused, including reuse of the same card object. Same-key image-cache misses share temporary download coordination, with independent caller cancellation and generation-aware cache writes. The existing four-request, 128-entry, and 32 MiB limits remain. These remove identifiable extra work; they do not establish a private-memory plateau or prove that all previously observed growth was a leak.

A progressive relay failure now has a single commitment point. Cancellation observed before that point consumes no notification; once committed, a subsequent connection cancellation cannot swallow the failure and permanently suppress later reporting. This is a managed control contract, not a new native Ended/dispatcher timing measurement.

Concurrent coordinator disposal callers share the same completion and failure. Engine disposal failure or timeout is reported as `EngineDisposeFailed` after coordinator shutdown processing instead of returning a false successful drain. Other best-effort server-cleanup behavior retains its existing policy.

## Verification scope

The repair tests use deterministic task gates and synthetic handlers for the relevant concurrency boundaries. They do not operate the desktop. Run the source-complete candidate through the normal command-line and CI checks together, then use the [consolidated interactive session](validation-session.md) for the affected UI, native-player, and shutdown cases. Report compilation, pure contract tests, actual native rendering, and installed-package acceptance separately.

The [completed local batch](verification/code-batch-20260910.json) passes 440 tests (47 API, 125 transport, 132 platform, 136 playback), Release compilation, Native AOT, SBOM generation/readback, and unsigned MSIX structural checks. Its candidate is `779B23ED8D28B845860AB72ABE1764D60FCDA7D9B2F6A089EFCFEA912BCBA93C`. That command-line batch ran no player or desktop action. The first batch failed the new pre-cancelled logout case: local persistence succeeded, but remote revocation lacked an explicit pre-cancellation check. The service now checks before reaching the transport. That failed receipt remains preserved rather than being relabeled as passed.

The subsequent [779B desktop batch](ui-validation.md#consolidated-normal-app-checkpoint-779b23ed) confirms visible poster restoration, ordinary paused mouse drags, natural episode continuation, and ordinary sign-out persistence on the repaired candidate. It does not establish capture-loss/late-release timing, failed detail loading, queue edits while loading, delayed cleanup overlap, or whole-app closure during Opening. The separate Home measurement still grows 13.95 MiB across 175.55 sampled seconds; the image repairs do not yet establish process-memory convergence. The test client exited and desktop control ended after this batch.
