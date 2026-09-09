# Committed native verification evidence

These sanitized records are reviewable from a checkout without access to ignored runtime artifacts. Each result retains its original scope and limits. Build manifests identify the executable and source inputs actually used for that run; later probe instrumentation additions do not retroactively change an earlier executable's hash.

## Default product owner isolation

`default-owner-isolation.json` records the actual default owner after the product repair. Three source-bound native Opening cancellations and three real native UnsupportedFormat decoder failures recovered. The fixture has no transcoder, so the failed source's normal fallback ended with NoCompatibleStream before explicit recovery. Six replacement sessions survived 42 deliberately replayed retired callbacks/failure entries and 54 old-ID commands, preserving the paused source, position, muted volume, and report ownership. A separate engine passed all concurrent Opening-time disposal assertions, including the second completion observing the shared drain task complete and the owner cleared. Only the three expected fallback diagnostics occurred. This is bounded isolation evidence, not a resource acceptance run.

The first isolation attempt used an API observation boundary before the legitimate native pause report had settled. Native cancellation, replacement source, zero paused drift, muted volume, and no retired engine events passed, but that pending Pause was counted in the injection window. The probe now requires 200 ms of completed-API quiet before recording the replay boundary. Product code and resource gates were unchanged; that earlier report remains an instrumentation failure in working artifacts.

## Default product DirectStream with SMTC

`default-owner-direct-nativeaot.json` records the complete normal product run after the owner repair, with no construction control or fault injection enabled. All twenty loops passed and all twenty SMTC retirement observations succeeded. The original gate passed at +17 median handles and +2,166,784 private bytes; all 177 upstream media requests were partial responses, and no diagnostic was emitted. Final idle handles were 1,419, 1,421, and 1,400. This is the current bounded synthetic DirectStream resource result. Its limitations remain separate from real-server HLS and pixel/audio observations.

## Earlier DirectStream with SMTC

`direct-smtc-nativeaot.json` records the complete final NativeAOT product lifecycle run against the generated 60-second synthetic H.264/AAC MP4. All 20 loops passed native clock/dimensions, muted playback, pause stability, source-timeline seek, resume, player retirement, API report ordering, range-request checks, and synchronous SMTC cleanup observation. Post-retirement SMTC was disabled and closed with metadata type `Unknown`, which is the valid result of `ClearAll`.

The original bounded resource gate passed: the last five-loop median versus loops 5-9 grew by 30 handles and 847,872 private bytes; limits were 32 handles and 64 MiB, with the additional last-eight-sample sustained-growth checks retained. Thirty seconds of final idle observation are included. This is a bounded regression result, not proof against every long-duration resource leak. All 178 observed upstream media requests returned partial content. Per-loop report-order assertions and counts are retained; the original raw fixture event arrays were not saved.

The record includes the media/metadata hashes, fixture identity, source SHA-256 hashes before/after publication, actual SDK and dependency versions, and the NativeAOT executable hash. No credential values, raw media URLs, or credentials-file paths are included.

This result does not certify real Emby Server, HLS, audible output/device switching, physical media keys, or presented frame/subtitle pixels. Real HLS evidence, when available, is separate.

## Official HLS lifecycle and resource investigation

`default-owner-real-hls-nativeaot.json` is the current normal product HLS result after the owner repair, with all construction controls and fault injection disabled. All twenty complete cycles, forty source/session graphs, and forty encoding cleanups passed on the identity-checked owned Emby 4.9.5.0 server/item 5. Initial zero/17-second positions and logical seeks to native 45.01 or 45.0299792 seconds were observed, with pause preserved. The original resource gate passed at -6 median handles and +720,896 private bytes; final idle handles were 1,254, 1,259, and 1,239. The product managed filter was present after all forty opens/reopens, and no diagnostic occurred. API ordering and quiet are observed; native adaptive packet/media-response counts, presented pixels, and audible output are not certified. SMTC update/release paths execute, while detailed SMTC property assertions reside in the separate DirectStream receipt. Earlier UI receipts use another executable and are not folded into this result.

All complete runs below retain twenty cycles, forty source/session graphs, and the original +32-handle/64-MiB gate. A passing isolated control is not a product pass.

| Run | Adaptive HTTP path | Player lifetime | Median handle growth | Result |
| --- | --- | --- | --- | --- |
| Logical reopen product | Managed scoped filter | New player per session | +52 | Functional cycles passed; resource gate failed |
| Creation-response ownership repair | Managed scoped filter | New player per session | +70 | Functional cycles passed; resource gate failed |
| Native HTTP control | Synchronous origin guard forwarding native operations | New player per session | +53 | Functional cycles passed; resource gate failed |
| Shared-player native HTTP control | Same native HTTP control | One player, forty source/session graphs | +3 | Isolated bounded control passed |
| Shared-player managed HTTP control | Original product managed scoped filter | One player, forty source/session graphs | -9 | Isolated bounded control passed |
| Default product after owner repair | Original product managed scoped filter | Product owner, forty source/session graphs | -6 | Normal bounded HLS lifecycle passed |

`hls-negotiation-rejection.json` records an authenticated request to the identity-checked official Emby 4.9.5.0 validation server for item 5. The factory rejected `UnknownTranscodeTimeline` before a native player started: 0 of 20 loops completed, and no native resource conclusion is possible. Sanitized diagnostics retain only route shape, query keys, protocol flags, safe duration/origin checks, and source/build hashes. The actual server returned a same-origin root-relative video route while the client recognized only the configured `emby` API prefix. Any later fix must have its own native verification result; this record remains a failure.

`hls-native-seek-rejection.json` records the subsequent normal HLS run after route recognition was fixed. One of twenty loops completed. The second loop opened at the correct native 17-second position and paused without drift, but native in-place seeking to 45 seconds timed out. Passive observations recorded no `SeekCompleted`, seekable range 0-63 seconds, and buffered range 15-45.0159444 seconds before disposal. Reading a position of 45 seconds did not make this a successful seek. API stop and encoding-cleanup responses were retained as 204, with session identifiers hashed. No extra playlist requests were made in this failing normal run.

`hls-logical-reopen-resource-failure.json` records all 20 complete HLS cycles after seeking was routed through a new negotiated player. Actual initial zero/17-second positions, 45-second replacement-player positions, restored pause state, API report ordering, and 40 successful encoding cleanups all passed. The unchanged resource gate failed: median handles increased by 52 (limit 32) and private bytes by 4,509,696. Final idle handles fell from 1,326 to 1,310 but do not override the failed per-loop criterion. This is explicitly a functional success with a resource failure, not completed HLS acceptance.

`hls-creation-response-resource-failure.json` records the next single-change experiment: the product retained and explicitly disposed the adaptive creation response. Probe-only booleans confirmed that all 40 initial/replacement graphs had a response. All 20 functional cycles and 40 encoding cleanups passed, but the unchanged resource gate failed at +70 median handles and +3,338,240 private bytes. The final eight handle samples all increased; final idle handles were 1,350, 1,350, and 1,332. This repair did not eliminate the observed growth. Differences between separate runs do not establish that the repair itself caused the numerical increase.

`hls-native-http-control-resource-failure.json` records an isolated control that replaced only adaptive HTTP construction with a synchronous origin guard forwarding native Windows HTTP operations. The same 20 cycles and 40 graphs completed, all 40 filters were explicitly disposed, and 637 requests were accepted with none rejected. The original resource gate still failed at +53 median handles and +3,178,496 private bytes; final idle handles were 1,383, 1,383, and 1,354. This control does not identify managed async operations, response buffering, or manufactured responses as the sole cause. It does not override either failed product run, and accepted-request counters do not certify packet-level quiescence.

`hls-shared-player-native-http-control.json` changes only player lifetime relative to that native HTTP control: one player serves forty distinct source/session graphs and is explicitly closed after coordinator disposal. All twenty cycles passed the original gate at +3 median handles and +2,322,432 private bytes. Forty unique playback IDs, forty empty-source checks before rebinding, all open/seek bindings and stop source clears, forty filter disposals, and forty encoding cleanups were verified. Final idle handles were 1,292, 1,292, and 1,272. This identifies repeated player creation/retirement as a relevant boundary in the tested sequential native HTTP path. It does not validate the product's managed HLS transport with reuse, concurrent cancellation/fallback, stale-callback isolation, or a production reuse design.

`hls-shared-player-managed-http-control.json` restores the original managed scoped HTTP filter while preserving the same shared player and forty source/session graphs. All forty opens/reopens positively observed the product filter; native HTTP control counters were zero because that construction override was disabled. All twenty cycles and forty encoding cleanups passed the original gate at -9 median handles and +3,555,328 private bytes. The final two loop samples of 1,573 and 1,581 handles are retained; the existing median and sustained-growth rules were not changed. Final idle handles were 1,254, 1,254, and 1,234. This sequential control supports investigating a player-lifetime repair while preserving current HTTP semantics. It still does not prove concurrent cancellation/fallback, stale-callback isolation, or final product acceptance.

## Investigation history

The following earlier observations guided the replacement of the managed media-stream bridge. They are not final product passes and are not generalized beyond the recorded controls.

| Experiment | Native operation loops | Resource observation |
| --- | --- | --- |
| Original product managed stream bridge | 20 completed | Failed; median handle growth +179, then +153 with corrected probe lifetime |
| Explicit native graph cleanup | 20 completed | Failed; median handle growth +176 |
| Owned read-operation cleanup | 20 completed | Failed; median handle growth +76 |
| Completion-handler move semantics | 20 completed | Failed; median handle growth +68 |
| Pure MediaPlayer construction/disposal | 20 completed | Bounded control passed; median handles -3 |
| MediaPlayer with product configuration | 20 completed | Bounded control passed; median handles +1 |
| Product HTTP range transport without native stream adaptation | 20 completed | Bounded control passed; median handles -4; 60 partial responses |
| Native file playback | 20 completed | Bounded control passed; median handles +17 |
| Native Windows random-access file stream | 20 completed | Bounded control passed; median handles +18 |
| Microsoft managed FileStream random-access adapter | 20 completed | Failed; median handles +250 |
| No-inflight-GC causality experiment | 5 completed | Instrumentation failed on loop 6; the fixed budget was exceeded or interrupted |
| Windows native HTTP source | 20 completed | Bounded control passed; median handles +25; 60 partial responses |
| Product session HTTP relay before SMTC expansion | 20 completed | Product gate passed; median handles +21; 179 partial responses |
| Initial SMTC probe checks | 0 completed | Probe assertions were invalid after COM disposal or after metadata became Unknown; corrected without keeping product objects alive |

The final direct result above includes the cleanup and SMTC implementation, using corrected getter timing and metadata-type checks. Historical failures remain failures; no threshold was relaxed.
