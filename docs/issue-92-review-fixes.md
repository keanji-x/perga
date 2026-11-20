# Issue 92 review corrections

Outcome: drain queued work after automatic handover, retain provider ownership until confirmed shutdown, and keep UI startup cancellation and reset boundaries effective.

The turn runner reports the epoch returned by its own automatic compaction to dispatch cleanup. Reset never updates that expected epoch. Failed cleanup retains the handle; timed-out launches retain their session-associated launch task. Dispatch remains fenced until explicit recovery verifies shutdown.

The conversation registry owns accepted startup identity and a send generation across route remounts. Reset invalidates sends before its request; observing a new epoch also invalidates older sends. Every deferred attempt checks that generation and late completions cannot clear restored drafts. Command capability checks apply to discovery and execution.

Validation: focused Kernel liveness and compaction regressions, frontend unit and real-browser flow tests, full backend and frontend gates. L2 review remains arranged by the Planner. No migrations or docs/design changes.

Focused validation reproduced the original backend defects, then passed 15 liveness/compaction cases. The affected browser set passes 31 cases across startup cancellation, reset, compaction, permission ordering, and Worker controls. An isolated source copy verified two mutations: omitting the expected-epoch update strands the followup/wake (2 provider calls instead of 4), and omitting the send-generation check fails both permission-deferred cases and the retry case (6 attempts instead of 1). The production checkout contains neither mutation.

The frozen protocol assertion in `fe/core/api/perga-v1.test.ts` now expects the retained accepted turn identity. Its commit trailer is `OWNERSHIP-CHANGE: fe/core/api/perga-v1.test.ts — verify accepted turn identity survives send decoding (#92)`. All other changed frontend paths are writable in the ownership inventory.
