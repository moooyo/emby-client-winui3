# Execution-policy rejection: attribution correction

The reviewed boundary-fixture startup on `127.0.0.1:18964` was explicitly authorized by the user. The execution tool nevertheless returned `CreateProcess` rejection with `blocked by policy` before the shell command ran. The proposed run directory remained absent. These facts are retained in the [attempt receipt](verification/ui-257b-20260910/controlled-fixture-rejection-after-specific-approval.json).

Earlier reports called this an automatic approval review rejection. That attribution was not established by the returned error and is withdrawn. The accurate status is **execution-tool policy rejection; issuing component and matched rule unknown**. User authorization was present. This was neither a fixture crash nor an executed test failure.

OpenAI's [Auto-review documentation](https://learn.chatgpt.com/docs/sandboxing/auto-review) distinguishes interactive approval review from other execution controls and states that Auto-review does not run when the approval policy is `never`. A generic policy-rejection string is therefore insufficient to identify a reviewer agent as its source. The available result includes no reviewer rationale, rule identifier, or override operation.

The previously archived JSON receipts and their hashes remain unchanged as historical records. This clarification supersedes their causal labels referring specifically to automatic approval review; their command-not-executed and test-not-run facts remain valid.

The existing user authorization does not need to be requested again. Further diagnosis needs evidence identifying the rejecting control or an applicable, supported resolution. No permission setting was changed, rejected command retried, alternate launcher used, or desktop control performed while preparing this clarification.
