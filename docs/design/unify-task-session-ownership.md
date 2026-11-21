# Unify Task and Session Ownership (Retire `TaskAttempt` and Dual Control Planes)

## 1. Outcome & Scope

Make `Session` the single, uniform conversation execution kernel in Perga, and turn declarative/contract `Task` orchestration into a thin unidirectional layer (`Task -> SessionId`) with **no `TaskAttempt` abstraction** and **no dual-track worker control plane**.

- **In scope**:
  - Remove `TaskAttempt` (`TaskAttemptDto`, `task_attempts` table, `_activeAttempts`, `ActiveAttemptState`, `LatestAttemptId`, `/api/v1/tracks/{trackId}/tasks/{taskKey}/attempts`).
  - Remove no-op per-attempt workspace leases (`AttemptWorkspaceLease`, `AcquireAttemptLeaseAsync`, `SettleAttemptLeaseAsync`, `AttemptLeaseDisposition`); workspace isolation remains Track-scoped.
  - Remove reverse pointers `AgentSessionDto.BoundTaskId` and `AgentSessionDto.BoundAttemptId` (and DB columns `agent_sessions.bound_task_id`, `agent_sessions.bound_attempt_id`); `Task` owns `SessionId` unidirectionally (`tasks.session_id`).
  - Collapse `session_messages` onto a single queue model (drop `source` and `attempt_id` columns and dual CHECK constraints via forward migration `0023`).
  - Remove `WorkerMessageWorkItem`, `ScheduleWorkerMessageCoreAsync`, `RequireCurrentWorkerCore`, `CloseInterruptedWorkerAsync`, and `ActiveAttemptState.InterruptionUnconfirmed`.
  - Unify declarative task dispatch (`perga-block task` / `session_create(task)`) onto the shared `Session` creation, `SessionInputs`, `SessionTurnRunner`, and `SessionRuntime` pipeline.
  - Give all sessions full capability parity (`ContextUsage` including idle updates, `Compact`, `Reconnect` / `ResetContext`, `Steer`, ACP permission modes).
  - Replace `/api/v1/sessions/*` `expectedAttemptId` guards with generic `expectedEpoch` optimistic concurrency checks where applicable.
  - Update `PolicyVm`, templates, OpenAPI `/api/v1`, and `fe/` to consume `TaskDto` directly without `TaskAttempt` endpoints or reverse session fields.
- **Out of scope**:
  - P2 (Store atomic state-transition rewrite).
  - P3 (Frontend `store.ts` 1s polling removal).

## 2. Target Architecture

### 2.1 Unidirectional Ownership (`Task -> SessionId`)

```
Track (Workspace + Report + PolicyVm + Schedules + Mail)
 ├── AgentSession (uniform execution unit: Lead / free Subagent / task-owned Subagent)
 │    └── SessionRuntime + SessionTurnScheduler + SessionTurnRunner + session_messages
 └── Task (optional declarative contract & DAG shell)
      ├── SessionId? -> AgentSession.Id   (unidirectional; null for command tasks or pre-dispatch report blocks)
      ├── RunCount, Status, StatusDetail, SemanticVerdict, SemanticNote
      ├── Gate, WorkerResult, Meta, GateVerdict, StartedAtMs, FinishedAtMs
      └── TaskGraphScheduler (dependsOn + ReadOnly/ReadWrite concurrency)
```

- `AgentSessionDto` carries **zero** task/attempt references.
- Both imperative (`perga_session_create(prompt, task?)`) and declarative (`perga_report_commit` adding `perga-block task`) paths share the same `NewSubagentSessionCoreAsync` + `SessionInputs` + `SessionTurnRunner` + `SessionRuntime` kernel.
- Retry (`perga_task_reject(reQueue: true)`) reuses `Task.SessionId`, increments `Task.RunCount` and `Session.Epoch + 1`, clears `NativeThreadId`/`ContextUsage`, appends `epoch_boundary(reason: "new_run")`, and dispatches the new initial turn through the unified session queue.

### 2.2 Push-Mode Session Control Gate

Instead of `Session` reaching back into `_activeAttempts`, the `Task` lifecycle pushes generic control states onto `Session`:

- **Pre-allocated / Pending task** (`Task.Status == Pending`): session gate is **frozen** (`readonlyReason = "waiting_task"`). Configuration updates remain available before activation.
- **Running task** (`Task.Status == Running`): session gate is **open**; initial prompt runs as a standard session turn; follow-up messages, steer, compact, and recovery all use the unified session pipeline.
- **Paused task** (user/parent calls `control(action: "stop")` on a running task session):
  - `RequestTurnStop` cancels the turn, revokes MCP token, and sets `Paused = true` on the active task execution state.
  - On turn settlement, `SessionRuntime.CloseAsync` closes the provider process so a fresh MCP token is issued on resume. If shutdown cannot be confirmed, `SessionRuntime.IsShutdownUnconfirmed` drives `session.Status = "unknown"` (`UnknownReason = "shutdown_unconfirmed"`). There is no `InterruptionUnconfirmed` side flag.
  - While `Paused`, queued messages stay queued and do not auto-drain; sending a `mode: "default"` message clears `Paused` and resumes via the unified `SessionInputs` / `PromoteQueuedBatchAsync` path.
- **Verifying / Settled task** (`Verifying` / `Done` / `Failed` / `Canceled`):
  - Task controller immediately freezes the session (`readonlyReason = "task_finished"` during verification and after settlement) and revokes its MCP token. On settlement, it interrupts any remaining `queued` messages in `session_messages` for `session_id`.

### 2.3 Gate Execution & `PolicyVm`

- Gate execution remains owned by Kernel (`_commandRunner.RunAsync` in Track workspace CWD) when `perga_task_done` is invoked: runs optional `AfterWorkerDoneCmd` + each `GateSpecDto` step, persists `WorkerResult`, `Meta`, `GateVerdict`, and final `Status` directly on `TaskDto`.
- `PolicyVm`:
  - `PolicyTurnContext` carries `string? BoundTaskId` (projected from the unidirectional `Task -> SessionId` lookup) instead of `BoundAttemptId`.
  - `task.settled` event carries `status` (`"done" | "failed" | "canceled"`), `taskKey`, `accessMode`, `workerResult`, `meta`, `gateVerdict`; `dev.perga.js` continues enforcing `ciVerified` for `vcs_pr_merge`.

### 2.4 MCP Authority Boundary

- `McpSessionPrincipal` (short-lived in-memory Bearer token minted in `BuildAgentLaunchSpecCoreAsync`) carries `(TrackId, SessionId, IsLead, BoundTaskId?, TaskRunCount?, EnabledPlugins)`.
- `KernelMcpCatalog.GetToolsForSession(boundTaskId, isLead)`:
  - Active task-bound session (`boundTaskId != null`): shared tools + `perga_task_done`, `perga_task_fail`; governance tools and `task` block report mutations remain forbidden.
  - Unbound session (`boundTaskId == null`): shared tools + governance tools + `session_*` tools (subject to `SessionPolicy`).
- `perga_task_done` / `perga_task_fail` validate `caller.BoundTaskId`, `caller.TaskRunCount == activeTask.RunCount`, `activeTask.SessionId == caller.SessionId`, and the originating active turn (`WorkerCompletionOrigin`), preventing stale turns or stale runs from settling a newer run.

## 3. Core Safety Invariants

1. **Frozen gate isolation**: A task-owned session in `Pending` (`waiting_task`) or verifying/terminal (`Verifying`/`Done`/`Failed`/`Canceled` → `task_finished`) rejects send/steer/compact/recovery/launch. Pending sessions permit configuration updates before activation; settled sessions also reject updates.
2. **Confirmed shutdown before cancellation/retry**:
   - `CancelTaskAsync` / `CancelAllInFlightTasksAsync` freezes the task, revokes MCP token, cancels launch / aborts active turn, and waits for `SessionRuntime.CloseAsync` + turn settlement before finalizing cancellation.
   - Retrying a task (`StartTaskRunCoreAsync`) confirms any open `SessionRuntime` is closed before bumping `Epoch + 1` and starting the next run.
3. **No cross-run message leakage**: Settling (`Done` / `Failed` / `Canceled`) a task and boot recovery (`MarkInFlightInterruptedOnBootAsync`) atomically transition all remaining `delivery = 'queued'` rows in `session_messages` for `task.SessionId` to `'interrupted'`.
4. **No stale completion**: `perga_task_done` / `perga_task_fail` checks `(Token, TurnId, BoundTaskId, TaskRunCount)` and rejects paused, verifying, settling, or interrupted runs.

## 4. Forward SQLite Migration (`0023_unify_task_session_ownership.sql`)

Released migrations `0001..0022` remain byte-frozen. Migration `0023` runs with leading `PRAGMA foreign_keys = OFF;` (verified by `SqliteMigrator`'s automatic `PRAGMA foreign_key_check;`):

1. Add `session_id`, `run_count`, `worker_result_json`, `meta_json`, `gate_verdict_json`, `started_at_ms`, `finished_at_ms` to `tasks`; backfill from `agent_sessions.bound_task_id` and `task_attempts`; drop `tasks.latest_attempt_id`; add `idx_tasks_session` and `idx_tasks_gate_blob_ref`.
2. Drop `agent_sessions.bound_task_id` and `agent_sessions.bound_attempt_id`.
3. Replace `terminals.attempt_id` with `terminals.session_id` (backfilled from `task_attempts.session_id` when present).
4. Drop `task_attempts` (and legacy `worker_messages` if present — verify references first).
5. Rebuild `session_messages` preserving `ordinal` and `sqlite_sequence`: remove `source` and `attempt_id` columns and dual-track CHECK constraints; preserve `kind = 'worker'` alongside `'input'`, `'steer'`, and `'queue'` (`CHECK (kind IN ('input', 'queue', 'steer', 'worker'))`) so initial task prompts remain distinguishable in `ListSessionItemsAsync` without `source`; enforce `UNIQUE (session_id, id)` and `UNIQUE (session_id, author_id, request_key)`.

## 5. Verification & Review Plan (L2)

- Full `.NET` test suite: `dotnet test Perga.sln` (including migration upgrade tests from v1..v22 -> v23, boot recovery, task dispatch/gate/cancel/retry, concurrent read/write scheduling, steer, context usage, compact, recovery, and MCP fence tests).
- Frontend checks: `(cd fe && npm run lint && npm run build && npm test)` + affected browser tests (`worker-controls`, `task-recovery`, `conversation-liveness`).
- Two independent L2 reviews checking ownership boundaries, partial failure / restart recovery, migration integrity, and callers/tests.

## 6. L2 Settlement and Upgrade Corrections

Verification freezes dispatch and revokes session credentials as soon as completion
origin validation succeeds. Conclusive initial provider failures settle only the
same active, unpaused run; interrupted and unknown turns retain recovery semantics.
Resuming an idle paused worker persists its resume message before clearing pause,
appending behind existing queued messages for one ordered batch. Migration 0023
preserves original message identifiers where unique and probes ordinal-qualified
suffixes against the complete original set for duplicates. Different generated
identifiers end in different ordinals, so they cannot collide with one another.
Regression checks cover slow gates, credential revocation, failed writer release,
paused queue resume, and a real v22 upgrade with adversarial identifiers.

## 7. Integration onto Template Package v2.1

Migration 0023 follows the released 0022 Track.Data migration, leaving migrations
0001..0022 unchanged. Template views derive worker sessions and gate verdicts from
Task.SessionId and Task.GateVerdict. Provider discovery, launch validation, canonical
MCP tool names, Track.Data authorization, unified schedules and widget projections
remain owned by their existing layers.
