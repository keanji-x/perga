# Stage 1 session kernel implementation

Outcome: every conversation has one persisted configuration and ownership tree,
one message admission path, and the same session operations. Existing HTTP/MCP
request and response shapes remain at their scheduled cutover boundary.

Review tier: L2. Formal independent reviews are arranged by the Planner.

## Authority and lifecycle

TrackActor remains the single writer. SendSessionMessageCoreAsync authorizes the
caller, resolves its author identity, checks receipt identity, and admits ordinary
and task-bound messages. Queueing, new steering and steering an existing message
share admission and delivery settlement. Ordinary immediate input retains its
durable admission transaction; Worker dispatch retains its attempt and lease.
The removed Worker send/steer implementations no longer make separate decisions.

Retained boundary adapters describe their released request hash shape to the same
admission implementation. This preserves historical receipts without a second
reader or hash fallback. Receipt replay still checks caller ownership and origin;
only fresh effects require an open, current session and attempt. A replay of a
promoted queue message retains its queue receipt shape.

Provider startup runs between mailbox claim and mailbox activation. Provider
opening/configuration runs outside the mailbox; session state, handle ownership,
policy generation and activation checks remain inside it. Both ordinary and Worker
startup can be stopped through ControlSessionAsync. Worker stop pauses the attempt;
confirmed provider shutdown permits another turn, while unconfirmed shutdown keeps
the recovery fence.

Task cancel/accept/reject use the task session's ownership tree: Human, its direct
parent, or the Track Lead. The MCP catalog no longer applies the old governance
role fence to these three tools. Self control remains forbidden.

Task-backed creation persists an initial message identity with the attempt/session
activation transaction. FirstInput describes that message and its observed turn,
including immediate startup; dependency/lease waiting still returns no input.
Setup failures and undelivered initial messages settle their delivery state.

## Migration and compatibility

Unreleased migration 0021 rewrites creation_receipts operation `session` to
`session-create` once. It copies worker_settings model and reasoning_effort directly
when a settings row exists, preserving explicit NULL resets. There is no dual-read
fallback. Released migrations 0001–0020 are unchanged.

OAR configuration assertions now check explicit null reset values. MCP integration
fixtures establish a running originating turn before invoking session mutations.
Worker resume fixtures wait for actual idle settlement after stop. Kernel
authorization checks remain enabled.

## Verification

Focused parameterized coverage includes:

- Ordinary/Worker message author identity, idempotency, queue steering by messageId
  and stale caller ExecutionOrigin fencing.
- Ordinary/Worker startup cancellation and waitMs activity long polling.
- Task governance for Human, parent, Lead, peer and self.
- notifyParent turn_end/task_settled/none and archived-parent suppression.
- Approval delivery aborted after the caller stops during the decision audit;
  the approval stays pending, remains answerable by Human, and projects through
  pendingInteractions with self-response disabled.
- Version-19 migration with existing/missing parent and explicit NULL settings,
  historical receipt operation rewriting and database constraints.

The final focused Kernel run passed all 300 tests. The affected Worker session,
shared message/replay and Track creation Host run passed all 24 tests. Storage
passed all 70 tests in the full convergence run. Subsequent source changes require
and are covered by the declared final solution/OAR gate.

Targeted mutation verification ran in an isolated source copy under /tmp: removing
the originating turn identity comparison caused both ordinary/Worker
QueueSteerAndAuthorReceiptsShareAdmission cases to fail with the expected missing
exception. Restoring the comparison passed both cases. The restored source matched
the working source; no mutation was applied to this checkout.

The official gate verdict is recorded with the Neige attempt, not duplicated here.
Local investigation evidence is in /tmp/stage1-baseline.log,
/tmp/stage1-kernel-final.log, /tmp/stage1-host-final-focused.log,
/tmp/stage1-mutation-origin.log and /tmp/stage1-mutation-restored.log. These local
logs are not portable delivery artifacts.
