# Default player-owner isolation checks

Run `--output-dir <new-directory> --lifecycle-isolation` only against the verified synthetic fixture on port 18961. `Start-SyntheticFixture.ps1` copies the existing fixture build into a fresh artifacts directory before starting it, so the server does not lock build outputs. No real credentials or arbitrary server URL are accepted by this mode.

The probe uses the default product player owner. It never enables either shared-player or native-HTTP construction controls. These checks are separate from the normal DirectStream and real-server HLS resource runs.

| Check | Count | Required evidence |
| --- | --- | --- |
| Actual native Opening cancellation | 3 | A warmed product owner's new source is bound, its actual state is Opening, and the open task is pending. The temporary native event handler is removed before cancellation. The open is canceled, the element detaches, and no start report is sent for that canceled session. A missed observation window fails instrumentation. |
| Actual decoder rejection and recovery | 3 | A one-shot request wrapper redirects only the injected request to an owned loopback fixture containing 4,096 deliberately invalid MP4 bytes. Authentication headers are cleared; the fixture rejects credentials. A real native Failed event must occur while a source is bound, followed by successful healthy playback. |
| Retired callback replay | 6 recovery sessions | Six actual captured old session handlers and the old session's failure entry point are deliberately invoked after replacement. The retired session must publish no new engine event or mutate the replacement source. This is explicit fault injection, not a claim that Windows naturally delivered these callbacks late. |
| Retired ID commands | 9 per recovery | Old engine pause/resume/seek/volume/stop and coordinator pause/resume/seek/stop commands must leave the replacement paused at the same position, with its original muted volume and source. Muting remains enabled even in the deliberately different stale volume command. |
| Concurrent Opening-time disposal | 1 independent engine | Two DisposeAsync calls begin while the source is bound and Opening. Both must initially remain pending. The second completion synchronously observes the shared disposal task complete and the player owner cleared; both calls finish, the open cancels, the element detaches, and no canceled-opening start is sent. |

Before callback/command injection, actual pause is observed and completed API activity must stay unchanged for 200 ms. This separates the legitimate asynchronous native pause report from the subsequent injection window. That window accepts only ordinary periodic TimeUpdate progress, with no new control/start/stop operation. Engine and API start indexes are retained in the report.

The synthetic fixture has no transcoder. A native UnsupportedFormat failure can therefore trigger the product's normal fallback attempt and end with NoCompatibleStream; the next explicitly requested healthy source must still recover. These expected fallback diagnostics are recorded. Native release errors or unrelated diagnostics fail the run.

Reports use source-generated JSON and hashed session/playback identifiers. They retain actual native states and positions, per-recovery assertions, API ordering/status, fault-fixture request counts, and final detachment/network checks. Raw media URLs and credentials are omitted. Captured old-session delegates are released after each recovery, and the entire process exits before either formal resource run starts.

An IsolationPassed result is bounded functional evidence. It does not replace the unchanged twenty-loop resource gate, prove every concurrency interleaving, certify HLS on the synthetic service, or establish audible/pixel behavior. Formal DirectStream and official-server HLS runs use fresh processes with no injection/control flags.
