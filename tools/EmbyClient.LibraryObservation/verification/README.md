# Collection Reset observation receipts

These receipts preserve the actual instrumented application runs on 2026-09-09. They are not distribution builds or long-duration memory acceptance results.

- `before-build.json` and `after-build.json` retain executable and source-input hashes, conditional compilation results, package versions, and build isolation checks.
- `before-reset.jsonl` and `after-reset.jsonl` are complete numeric logs, copied without rewriting samples. Each observer has an independent elapsed-time origin and ten-minute bound; neither log supplies a UTC initialization anchor.
- `after-ui-observations.json` records screenshot stage UTC times and executable identity. It does not establish a process-sampling timestamp for each stage.
- `home-after-library.jpg` and `back-to-library.jpg` are the original computer-use screenshots. Only generated fixture images and a dedicated test account appear.

In the after log, six Home rows at 551,036–576,061 ms have one item, 46 subscriptions, zero requests, and one bound source. The before log did not have the bound-source field; its prior value must not be reconstructed from weak references. The later detail and library rows demonstrate normal rebinding after cleanup.

The [full report](../../../docs/implementation/large-library-validation.md) retains the short process CSV results and their coverage limits. The after CSV ends during the first revisit, before Home; no after-Home private-memory result is claimed. Natural GC, cached controls, weak CLR wrappers, and native texture allocations remain different observations. No forced GC was used.
