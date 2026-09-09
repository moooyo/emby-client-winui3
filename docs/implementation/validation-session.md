# Consolidated validation session

The current work mode was requested on 2026-09-10: finish the code changes first, then validate the resulting candidate together. The user subsequently authorized one consolidated local acceptance batch and manually completed saved sign-in. That batch has ended and the test application has exited. Desktop control is paused again; the plan below does not schedule another interruption.

The [consolidated command-line batch](verification/code-batch-20260910.json) is complete: 440 tests, Release build, Native AOT, and unsigned package checks passed. Candidate `779B23ED8D28B845860AB72ABE1764D60FCDA7D9B2F6A089EFCFEA912BCBA93C` in `artifacts/verification/20260910-code-batch-2/publish` now has its own [interactive receipt](verification/ui-779b-20260910/summary.json) and [scoped results](ui-validation.md#consolidated-normal-app-checkpoint-779b23ed). Its executable hash was unchanged after the batch.

The actual order put library/memory sampling before any playback to preserve a no-video baseline. Six page loads and five visual revisits completed; the separate Home sample still grew 13.95 MiB in 175.55 sampled seconds. DirectStream pause/mouse seeking, fullscreen and minimize/restore, natural episode continuation, diagnostics saving, adding/viewing a queue entry, ordinary sign-out, and signed-out window closure were observed. No code was changed during the batch. The failure, delayed-cleanup, and proven Opening-close cases remain open because the fixture used for that batch could not establish their triggering conditions; no quick-launch/close loop was used. No new complex-subtitle or official-server runtime result was obtained.

The subsequent code/tooling stage prepares [opt-in numeric attribution](../../tools/EmbyClient.LibraryObservation/README.md) and [deterministic synthetic boundary controls](../../tools/EmbyClient.FixtureServer/Boundary-Controls.md). Their [build and 40-check HTTP receipt](verification/attribution-boundaries-20260910.json) does not reopen the desktop session. A later explicitly authorized batch can use a freshly selected fixture process and the source-audited observation build. Keep the first-media gate, failed-detail sequence, and delayed shutdown scenarios separate when one would consume the other's trigger. Record actual pending control events and client state; no configured delay alone proves that Opening or cleanup overlap occurred.

The [auxiliary NativeProbe compilation](verification/native-probe-build-20260910.json) also succeeds with the repaired transport/coordinator sources. Its separate source-audited executable remains unlaunched; compilation neither removes the previous launch restriction nor supplies new native playback evidence.

## Complete the code before using the desktop

1. Finish and review the current account-exit, playback preparation, timeline ownership, image lifecycle, and asynchronous cleanup changes.
2. Freeze the source revision and run the existing command-line build/tests as one batch. Publish to a new directory so a running development application is not overwritten or closed.
3. Record the actual candidate path, SHA-256, source revision, test results, and remaining failures. A later code change invalidates only the affected candidate observations; retain the previous evidence.
4. Prepare all required synthetic media, account bindings, and evidence directories before the user makes a desktop verification window available. Do not start a player, change account state, sign out, close an existing window, or change system settings during this preparation.

These steps use the existing scripts and CI. They do not introduce another desktop-launch mechanism or bypass an approval rejection. The earlier rejected NativeProbe launch and official-server public-info command must not be retried through a different tool, process, URL, or agent to evade that restriction.

## One planned interactive batch

Use a fixed candidate throughout the batch. If an interaction reveals a code defect, record it, stop that affected case, and gather the remaining independent observations before returning to implementation. Do not alternate small code edits and repeated desktop takeovers.

A physical Escape or explicit user stop ends the entire interactive batch immediately. Resume only after a new user instruction; do not treat this plan or an automatic goal continuation as permission to resume desktop control.

| Order | Scope | Evidence to retain |
| --- | --- | --- |
| 1 | User completes any required authentication handoff; verify the intended test account/library | Candidate identity and actual connected library. No passwords or tokens in screenshots or logs. |
| 2 | Core playback and ownership | Direct playback, pause, seek, full screen, minimize/restore, and return to library. Verify the new mouse-drag cancellation/late-release boundary and detail-loading error recovery separately. |
| 3 | Queue and episode completion | Start episode one with automatic continuation enabled; allow a real Ended event to open episode two. Also verify queued-item loading failure or an edited queue head returns usable controls without consuming the wrong entry. Do not count a manual Next click as automatic completion. |
| 4 | Tracks and subtitles, when the specific runtime lane is available | Paused audio/version changes, subtitle enable/disable, separate ASS/font and PGS screenshots, progressive tail and cleanup where applicable. Preserve the default-profile versus profile-control distinction. |
| 5 | Library resources | One agreed large-library sequence and a separately timed Home-idle sample, with no concurrent build, other native probe, forced GC, or unrecorded workload. Report private bytes and handles independently; no plateau claim from a single sample. |
| 6 | Final shutdown/account lifecycle | Exercise sign-out with delayed playback cleanup, and a separately observable close-during-opening case. Retain local-account persistence and ordered cleanup evidence. A close after Playing begins is not an Opening result. |

The fixture used for 779B had no media-opening delay. The new optional shared first-media gate can prepare a bounded interval in a separate process, but still requires actual client-state observation. A fast opening is not a reason to repeatedly launch and close windows hoping to catch it; record that case as inconclusive unless the actual Opening interval can be established.

Physical media keys, Narrator, sustained physical key holds, multi-monitor DPI, suspend/resume, output-device changes, HDR/audio hardware, and clean-machine signed installation require their respective facilities. They are not implicitly completed by this desktop batch. License, signing identity, release channel, and trusted installation decisions remain separate owner inputs.

## Interrupted-session evidence

The [2026-09-10 5C3C observations](verification/ui-5c3c-20260910/observations.json) retain eight previously captured screenshots, before the subsequent code repairs. They establish connected synthetic-library display, DirectStream video, paused seek, full-screen transitions, minimized-window restoration, and return to the updated detail view. The physical Escape stop occurred while navigating toward the test episodes; no new automatic-continuation result was obtained. No screenshots, server requests, playback probes, or desktop actions are performed by this document or by archiving that existing evidence.
