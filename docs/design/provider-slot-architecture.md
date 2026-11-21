# Issue #105: Unified `SessionRuntime` Provider Ownership Architecture

- **Issue**: [#105 — Unify TrackActor session provider ownership into a single ProviderSlot state machine](https://github.com/keanji-x/perga/issues/105)
- **Related**: #100 (canceled startup locks conversation), #101 (subprocess shutdown & recovery error contract), #104 (unified 8-primitive session interop)
- **Design Principles**: Clean-slate (zero legacy compatibility shims), minimal moving parts, single authoritative source per concern, low long-term maintenance cost.

---

## 1. Problem Analysis & First-Principles Simplification

Today, per-session provider ownership in `TrackActor` is spread across **6 overlapping structures** and **3 concurrency models**:
- `_openSessionHandles` (mailbox-only `Dictionary`)
- `_pendingSessionLaunches` & `_sessionShutdownPending` (`ConcurrentDictionary`)
- `_sessionDisposals` (`lock`)
- `SessionTurnScheduler` (`StartupSync` / `Compacting` / `Reservation` / `Resume` (`SessionResumeHold`))
- `_sessionRecoveryJobs` (mailbox `List<Task>`)

Compounding defects:
1. **`Perga.AgentKit` subprocess disposal is not retryable**:
   - `StdioSubprocess.DisposeAsync()` sets `_disposed = 1` and calls `_process.Dispose()` even when `ShutdownConfirmed == false`, making later retries silent no-ops.
   - `JsonRpcStdioConnection.DisposeAsync()` cancels `_pumpCts` *before* `_process.DisposeAsync()`, truncating graceful EOF.
   - `OarAgentSession.OpenAsync()` combines process spawn and async handshake, losing unconfirmed handles on failure/cancellation unless smuggled via exceptions.
2. **Premises eliminated from first principles**:
   - **No separate `Draining` / `providerCleanup` UI state**: On startup cancellation, stay in `stopping` and perform bounded synchronous `Kill` + `WaitForExit` in place (~10–50ms on real OS). On confirmed exit, settle cleanly to `idle` (zero new API/UI state). Only in the extreme case where OS exit is genuinely unconfirmed after `Kill` timeout, retain the live handle and settle durable `session.status = "unknown"` (`unknownReason = "shutdown_unconfirmed"`), reusing the existing `unknown` recovery UI.
   - **Lazy configuration rebinding**: All providers run through OAR (`LaunchOnly`). `UpdateSessionAsync` (`PATCH /api/v1/sessions/{id}`) only persists SQLite config (`ConfigVersion++`, clears `ContextUsage` on model/effort/provider change) and never eagerly spawns a subprocess; if the provider itself changed and an idle handle exists, close it cleanly. Otherwise the next turn / compaction checks config match inside `SessionRuntime.EnsureOpenAsync` and reopens once. This deletes `RunSessionConfigurationAsync`, config `reservation`, and provisional-provider races completely.
   - **No `SessionResumeHold` / `ProviderSessionResumeFence`**: Durable `unknown` state lives solely in SQLite `agent_sessions`. Eliminates dead `Quiescent` field and fixes `_sessionResumeHoldCount` preventing idle `TrackActor` eviction.
   - **No `ProcessReaper` + `hadActiveTurn` split-brain matrix**: One small serialized `SessionRuntime` per `sessionId` owns at most one handle at a time.

---

## 2. Three-Part Architecture

### Part 1: `Perga.AgentKit` — Two-Phase Creation & Retryable Subprocess Shutdown

1. **`IAgentRuntime.CreateSession(HarnessLaunchSpec spec)` + `IAgentSession.InitializeAsync(CancellationToken ct)`**:
   - Replace `IAgentRuntime.OpenSessionAsync(spec, ct)` across all 3 implementations (`AgentRuntime`, `ScriptableAgentRuntime`, `ScriptableFakeAgentRuntime`).
   - `AgentRuntime.CreateSession(spec)` validates `spec`, resolves bridge/Node/env, starts `StdioSubprocess`, registers in `_activeSessions`, and returns `OarAgentSession` before running the async `session/open` RPC.
   - The caller (`SessionRuntime`) owns the `IAgentSession` handle from the moment the child process starts. No `LateLaunch`, no `needsCleanup` TCS, no `ProviderOpenShutdownUnconfirmedException`.
2. **`StdioSubprocess`**:
   - Once-only stdin closure (`TryCloseStdinOnce()`).
   - `DisposeAsync()` serializes via `_sync` + `_activeShutdownTask` (concurrent callers join the exact same shutdown attempt).
   - Sequence: close stdin once -> wait grace (`250ms`) with stdout/stderr open for graceful EOF -> if still alive, `Kill(entireProcessTree: true)` and wait up to `2s` -> check `_process.HasExited`.
   - **Only when `HasExited == true`**: set `ShutdownConfirmed = true`, drain stderr pump, dispose `_process` / `_stdinLock` / `_disposeCts`.
   - **When `HasExited == false`**: retain `_process` open so subsequent `DisposeAsync()` calls truly retry `Kill` + `WaitForExitAsync` on the exact same OS child.
3. **`JsonRpcStdioConnection`**:
   - First `DisposeAsync()`: mark `_closing = 1` (suppressing spurious `OnDisconnected`) and fail pending RPC requests once.
   - Await `_process.DisposeAsync()` *before* canceling `_pumpCts` so stdout reads cleanly to graceful EOF.
   - Only cancel/dispose `_pumpCts` after `_process.ShutdownConfirmed == true`.
4. **`OarAgentSession`**:
   - First `DisposeAsync()`: detach `_idleUsageHandler`, complete `_currentTurn` quietly, send best-effort `session/close` RPC (`1s` timeout) once.
   - Every `DisposeAsync()` delegates to `_rpc.DisposeAsync()`.
   - Unregister from `AgentRuntime._activeSessions` only when `ShutdownConfirmed == true`.
   - Do not dispose `_turnLock` (no OS handle; prevents `ObjectDisposedException` races with turn/compact `finally` blocks).

### Part 2: `Perga.Kernel` — `SessionRuntime` & `SessionRuntimeRegistry`

Replace `TrackActor.SessionOwnership.cs`, `_openSessionHandles`, `_liveContextUsage`, `_sessionRecoveryJobs`, `SessionResumeHold`, and `RunSessionConfigurationAsync` with a single small per-session owner (`SessionRuntime`) managed by `SessionRuntimeRegistry`:

```csharp
internal sealed class SessionRuntime
{
    private readonly SemaphoreSlim _gate = new(1, 1); // serializes Open / Close / Reconnect / Dispose
    private readonly object _sync = new();            // short non-awaited lock for snapshots & CancelLaunch
    private IAgentSession? _agent;                    // null or single live handle
    private HarnessLaunchSpec? _spec;
    private SessionConfiguration? _selection;
    private ConfirmedConfiguration? _confirmation;
    private LiveContextUsage? _contextUsage;
    private long _generation;
    private CancellationTokenSource? _launchCts;      // non-null only while Create/Initialize is in flight
    private string? _launchTurnId;
    private string? _busyKind;                        // null | "turn" | "compacting"
    private bool _recovering;
    private bool _shutdownUnconfirmed;                // true only when last Close failed to confirm OS exit
    private bool _disposing;
    private int _activeOps;                           // > 0 while any gate operation is running
}
```

Key invariants & flows:
1. **Non-blocking `CancelLaunch(turnId)`**: Under `_sync` (never waiting on `_gate`), cancels `_launchCts` immediately when `ControlSessionAsync("stop")` targets a launching turn.
2. **In-place shutdown on canceled/failed launch**: `EnsureOpenForTurnAsync` holds `_gate`, creates `_agent`, awaits `InitializeAsync(launchCt).WaitAsync(launchCt)`. On cancellation/timeout/error/validation mismatch, still under `_gate`, it immediately calls `CloseLockedAsync(shutdownTimeout)`:
   - If `ShutdownConfirmed == true`: `_agent = null`, `_shutdownUnconfirmed = false` -> Turn settles as `interrupted`, session settles as clean `idle` (no lingering guard!).
   - If `ShutdownConfirmed == false`: `_agent` remains retained, `_shutdownUnconfirmed = true` -> Turn settles as `interrupted` (input rejected), session settles as `status = "unknown"`, `unknownReason = "shutdown_unconfirmed"`.
3. **Config rebinding (lazy)**: `UpdateSessionAsync` writes SQLite (`ConfigVersion++`, clears `ContextUsage` on model/effort/provider change) and invalidates `_contextUsage`. If `providerChanged` and idle handle exists, closes it cleanly (`CloseIfIdleAsync`); otherwise never spawns a process. Next `EnsureOpenForTurnAsync` / `EnsureOpenForCompactionAsync` calls `ConfigureAsync(desired)` or closes old + opens fresh with `desired`.
4. **Recovery (`reconnect` / `reset_context`)**: Runs under `_gate` with `_recovering = true` (rejecting concurrent recovery/dispatch and surfacing `UnknownDetail.Recovering = true`):
   - `reset_context`: `CloseLockedAsync(scopedClose: false)` (retrying the exact retained `_agent` if `_shutdownUnconfirmed == true`). Only after `ShutdownConfirmed == true` does mailbox commit SQLite `CompleteSessionResumeAsync` (Epoch+1, `NativeThreadId = null`, archive queue, `epoch_boundary`).
   - `reconnect`: `CloseLockedAsync(scopedClose: true)` -> `CreateSession` + `InitializeAsync` -> validate `SupportsSessionResume`, `NativeThreadId == expected`, `ConfigurationState`. On any validation/commit failure, `CloseLockedAsync` cleans up the replacement (retaining it if unconfirmed) without mutating `Epoch` or `NativeThreadId`.
   - When `session.Status == "unknown"` and `unknownReason == "shutdown_unconfirmed"`, `ReadUnknownDetailCore` reports clear detail (`"The previous provider subprocess did not confirm shutdown."`) and enables `CanReconnect` (when `NativeThreadId` is present & resumable) and `CanResetContext`.
5. **All provider I/O outside mailbox**: `CompactSessionInternalAsync`, `CloseInterruptedWorkerAsync`, `StartTaskAttempt` previous-handle cleanup, and `DeleteTrackCoreAsync` all execute provider open/close outside `TrackActor.EnqueueAsync`.
6. **Unified teardown**: `TrackActor.DisposeCoreAsync` and `TrackActor.DeleteTrackCoreAsync` both call `await _runtimes.DisposeAllAsync()`, which cancels all `_launchCts`, awaits `_gate` (joining any in-flight open/close so `DisposeCalls == 1`), and disposes remaining handles.
7. **Actor idle eviction**: `TrackActor.HasActiveWork` checks `_runtimes.HasActiveWork` (`_activeOps > 0 || _busyKind != null || _recovering || _shutdownUnconfirmed`). Idle open handles (`Ready`) and durable `unknown` sessions with no live unconfirmed handle no longer block idle actor eviction.

### Part 3: Transient Event Push & `session_recovery_required` Error Contract

1. **Transient event `session.state_changed`** (`KernelEventKinds.SessionStateChanged`):
   - Registered in `TransientKinds` (`EventId == null`, never written to SQLite), payload `{ "sessionId": "<id>" }` (identical pattern to `approval.changed`).
   - Emitted via `_eventHub.PublishTransient(...)` on queue/steering updates (replacing `NotifyQueueChangedCore`'s SQLite `SessionUpdated` writes) and recovery start/finish transitions (replacing `RunSessionRecoveryAsync`'s redundant `finally` SQLite write).
   - `GetSessionItemsAsync` (`waitMs` long-poll) already matches any event with `payload.sessionId == sessionId` and wakes immediately.
   - `EventsWebSocketHandler` streams it verbatim; frontend `fe/core/api/perga-v1.ts`, `schemas.ts`, `wire.ts`, `openapi.json`, and `fe/core/events/invalidation-plan.ts` decode `session.state_changed` and invalidate `[overlaysByKind('track'), plannerRun(sessionId)]`.
2. **Dedicated error code `session_recovery_required`**:
   - Add `KernelErrorCode.SessionRecoveryRequired` mapped in `HttpErrorHandling.cs` to HTTP `409 Conflict` with `{ code: "session_recovery_required", error: "..." }`.
   - `RequireSessionDispatchAvailableCore` throws `SessionRecoveryRequired` whenever `session.Status == "unknown"` or `_runtimes.IsShutdownUnconfirmed(sessionId)` or `_runtimes.IsRecovering(sessionId)`.
   - `fe/core/domain/conversation-delivery.ts` adds `{ code: 'session_recovery_required', is: 'refused' as const }` to `SEND_FAILURES` so fresh sends fail fast with localized Chinese feedback (zero 5x blind retry) while preserving `'unknown'` if an earlier attempt of the same idempotency key had an unknown outcome.

---

## 3. Staged Delivery & Verification (L2)

- **Stage 1 (`Perga.AgentKit`)**: Two-phase `CreateSession` + `InitializeAsync`, retryable `StdioSubprocess`, pipe-safe `JsonRpcStdioConnection`, `OarAgentSession` cleanup + real-child shutdown retry & graceful EOF tests.
- **Stage 2 (`Perga.Kernel` + `Perga.Host`)**: `SessionRuntime` / `SessionRuntimeRegistry`, delete scattered ownership collections / `SessionResumeHold` / `RunSessionConfigurationAsync`, move all provider I/O out of mailbox, add `session.state_changed` transient event & `session_recovery_required` error code, update & add comprehensive Kernel + Host integration tests (including #100 startup-cancel-then-send, Worker + ordinary recovery, `waitMs` transient wake, zero spurious SQLite events).
- **Stage 3 (`OpenAPI` + `fe`)**: Wire `session.state_changed` and `session_recovery_required` across `openapi.json`, `wire.ts`, `perga-v1.ts`, `schemas.ts`, `invalidation-plan.ts`, `conversation-delivery.ts`, fixtures, and unit/DOM/browser tests.
- **L2 Review & Full Validation**: Two independent read-only L2 reviews (`codex` + `claude`) plus full `.NET`, `OAR bridge`, and `fe` (`lint`, `build`, `test`, `test:browser`) verification.
