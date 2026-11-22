# Unified runtime session configuration state

Review tier: **L2** (provider state, recovery, and durable execution identity).
Outcome: every dispatched prompt uses the complete requested configuration or
fails before dispatch; desired settings never masquerade as confirmed settings.
This is a design only. Implementation needs two independent L2 reviews.

## Verified baseline

Read-only audit on 2026-11-19; no fetch, rebase, branch switch, or provider turn.
The initial working tree was clean on
`neige/track-048640d6c8df4b53b5a108e9957127c3`, HEAD
`44cece81d09e2930e94f7b77bfbc429e15430e93`. Local `origin/main` and
`git ls-remote origin refs/heads/main` both reported
`5337fcd85bb0a55e3dcfeb3759e674bf755be2a7`. The merge base is
`eb7b66d9876eebb6ece1797efc190acff60c198a`; HEAD is one commit ahead and two
behind. Those main-only commits are `97677a4` (turn runner phases) and `5337fcd`
(executing-turn approvals/Stop). Audit locations below describe HEAD; subsequent
implementation must account for those upstream changes without undoing them.

`gh pr view 72 --repo keanji-x/perga --json number,state,mergedAt,mergeCommit,headRefOid,baseRefName,url`
reported [PR 72](https://github.com/keanji-x/perga/pull/72) **OPEN**, base `main`,
head `44cece81…`, `mergedAt: null`, `mergeCommit: null`. Its head is not an
ancestor of `origin/main`: PR 72 is not already merged.

`which claude` / `command -v claude` resolves to
`/home/kenji/.local/bin/claude`; `claude --version` reports
`2.1.280 (Claude Code)`. `claude --help` explicitly advertises:

```text
--effort <level>  Effort level for the current session
                  (low, medium, high, xhigh, max)
```

This verifies the installed CLI argument contract, not model entitlement or a
successful remote execution. No inference about older installations is needed.

## Current paths and gaps

| Path / owner | Observed behavior at HEAD |
| --- | --- |
| `Contracts.cs`, `IAgentSession` | Launch and turn inputs carry independent nullable model/effort; `Task<bool> TrySetModelAsync` conflates inability to switch, invalid selection, and partial failure. No ordinary-session configuration capability or confirmed-state contract exists. |
| `AgentRuntime.cs` | Normalizes launch specs, resolves commands/environment and isolation, routes open to three adapters. Provider catalogs describe choices, not switching guarantees. ACP workspace permission discovery uses an isolated open. |
| `AcpAgentSession.cs` open/load | Builds `AcpModelCatalog` and captures defaults. Calls the switch method for explicit model **or effort**, but can return an opened session after `false`; matching the loaded model alone can confirm binding without confirming effort. Loaded current effort is deliberately not an original default. |
| ACP switch | Uses exact catalog variants or separate `thought_level`; tries `session/set_model`, then model `set_config_option` only on method-not-found. `ConfirmsModel` rejects nonobjects, `success:false`, and contradictory model readback. Thought-level callback ignores its response body and marks effort applied. A model success followed by effort failure is non-atomic. |
| `Internal/AcpModelCatalog.cs`, `AcpSessionOptions.cs` | One catalog maps grouped selections to exact wire IDs; separate thought-level choices apply where no suffix variants exist. Options parse advertised select values/current values. An empty catalog is not an authoritative rejection; raw model IDs retain provider validation. |
| `CodexAgentSession.cs` | Switch always returns false. Thread start/resume receives launch effort; turn/start also sends configured effort and Worker overrides. Model/effort can be configured despite no live-switch primitive. Worker negotiation and guarded default-model fallback are separate existing behavior. |
| `ClaudeAgentSession.cs` | Launch emits `--model`, `--resume`, and `--permission-mode`, but drops launch effort. Switch always returns false; ordinary turn inputs do not repair the dropped setting. CLI support and adapter support currently differ. |
| `TrackActor.cs` open/switch | Persists desired model/effort before idle reconciliation; active turns, compaction, resume holds, and Drain defer it. Cache reuse compares requested tuple and permission. True records the tuple, false disposes/reopens, exceptions dispose/rethrow. Permission changes reopen. Launch carries model/effort/mode and existing native ID. |
| `SetSessionPermissionModeAsync` | Rejects task-bound changes, resolves the requested mode, persists/emits it, then evicts an idle cache for next-turn launch; active/compacting instances retain their mode until reconciliation. Provider changes reset mode through the new provider's defaults. |
| `TrackActor.ContextUsage.cs` | Registration labels the cache with the requested tuple unconditionally. Usage already checks instance, epoch, native identity, revision, and bound selection; an adapter's false confirmation undermines these guards. |
| `TrackActor.SessionResume.cs` | Confirms stop/close, opens the held native ID, checks resume support and identity, then completes durable recovery and registers the launch tuple. Configuration must be confirmed before recovery is completed. |
| `TrackActor.WorkerSessions.cs` | Versioned Worker settings have their own authorization, catalogs/defaults, store transaction, and approval policy. Resolves per-turn effective values; preserves previously known defaults only when still advertised. Ordinary model updates reject Workers. |
| `SessionTurnRunner.cs` | Ordinary durable input overrides provider/model/effort together from acceptance, while current permission remains policy. Worker settings resolve separately and enter `TurnInput`. Preparation failure settles accepted input without prompting. |
| Test fakes | Kernel `Fakes/KernelTestHarness.cs` returns a switch boolean and records only model. Host `Fixtures/PergaHostTestCluster.cs` returns its runtime boolean without applying effort. Both can hide configuration/readback defects. |

Preserve desired-state persistence and acceptance snapshots; refine runtime
confirmation rather than changing HTTP semantics.

## Internal contract and state

Replace the boolean with `ConfigureAsync(SessionConfiguration, CancellationToken)`
returning a closed `SessionConfigurationOutcome` union:

- `Applied(ConfirmedConfiguration)`: complete request satisfied, including any
  explicit effort with default model; includes context revision and resolved
  native values when known. A proven no-op is Applied.
- `RequiresReopen(reason)`: this instance cannot apply it live, but the adapter
  explicitly supports applying the complete request on reopen with the same
  native identity. No speculative reopen to discover whether effort is ignored.
- `Unsupported(dimension, reason)`: no supported way to express the request in
  this lifecycle, including an unresolvable default reset. Never means Applied.
- `Rejected(code, reason, observedState)`: invalid value, contradictory/malformed
  acknowledgement, or attempted operation failure. Include whether the instance
  remains usable and which values are known after a partial effect.

Cancellation remains cancellation; transport exceptions retain their cause and
invalidate uncertain state. They must not become RequiresReopen implicitly.
Use typed failure codes internally and existing Kernel error mappings externally.

Add explicit provider capabilities for preflight and authoritative, negotiated
session capabilities after handshake. Per model, effort, and permission dimension
declare open, live change, resume override, and default reset support; distinguish
Unknown from Unsupported and carry advertised values/default evidence. Catalogs
alone do not imply support. Capabilities describe operations, not authorization;
permission changes still obey Kernel policy and isolation. Keep provider details
in AgentKit and avoid provider-name conditionals in TrackActor.

`SessionConfiguration` is a complete selection, not a patch: represent each field
as `ProviderDefault` or `Explicit(value)`; map existing normalized nulls at the
boundary. Keep desired selection, negotiated defaults, and observed/applied
configuration separate. Never infer an original default from a resumed current
value or a synthesized catalog choice. Default model plus explicit effort must
resolve/apply both dimensions on new, switch, and resume through one adapter
resolver. If resume cannot establish the requested default, fail explicitly;
do not silently retain the loaded explicit value or create a different thread.

Expose configuration state on the returned `IAgentSession`. Open/load must either
satisfy the complete launch configuration or throw a typed configuration failure
and dispose the candidate. RequiresReopen is only a live-instance result: it
cannot cause recursive open retries. Separate launch-contract acceptance from
provider-observed native values so missing readback is never fabricated.

## TrackActor reconciliation and durable boundaries

Use one reconciliation helper for idle reuse, fresh open, compaction reopening,
and explicit resume. Compare the complete selection and permission binding. Keep the agent, launch
specification, selection and confirmation together in one `OpenSessionHandle`.

| Outcome | Required actor behavior |
| --- | --- |
| Applied | Bind only the satisfied request plus adapter confirmation/revision; invalidate stale context usage. Reuse the instance. |
| RequiresReopen | Evict/detach/dispose the old instance, open once with complete selection, current permission and original native ID; register only after launch confirmation. Failure leaves no falsely bound cache. |
| Unsupported | Surface an explicit existing invalid-argument/precondition error as appropriate; no reopen or prompt. Preserve desired state already committed. |
| Rejected | Surface the reason, invalidate binding/usage, evict uncertain or partially changed instances, and fail preparation. Never convert rejection into reopen or dispatch. |

Known unsupported input may be rejected before a new write; discovery after the
existing desired-state transaction must not roll that transaction back. Report
failure honestly and allow a later explicit retry/correction. Do not tight-loop
retry rejected configuration. Retain serialization and dispatch admission; a
desired update during active turn/compaction/resume cannot mutate that execution.

After integrating accepted session settings, reconcile the complete configuration
at every idle reuse boundary, including unchanged selections. ACP ordinary modes
may apply in place after validating the current catalogue and acknowledging the
mode operation. Launch-argument transitions and launch-only adapters require reopen
using the full configuration; never weaken permission to make launch work.
Preserve ACP mode RPC/Antigravity launch handling, Codex policy mapping, Claude
launch mode, sandbox mounts, and Worker approval authorization. Mode negotiation
failure also prevents registration/dispatch.

Register only confirmed complete launch selections in `ContextUsage`; uncertain
partial effects clear reusable binding/revision before any idle usage callback
can publish a new window. Preserve existing instance/epoch/prompt/revision guards.
Resume checks original identity **and** configuration before `CompleteSessionResumeAsync`;
failure disposes the candidate and retains the recovery hold/paused state.

Worker settings retain their version, transaction and authorization boundaries.
Reuse resolution/confirmation primitives in adapters, but preserve strict Worker
option readback and ordered callbacks before prompt. Never route Worker changes
through the ordinary model endpoint or overwrite accepted Worker settings.

No wire or schema migration: session DTOs remain desired state, normalization and
omitted-effort semantics stay unchanged, `session_updated` uses the existing
store/event transaction, and released migrations remain frozen. Ordinary accepted
input retains its provider/model/effort snapshot, including historical null effort;
permission remains current policy. Restart re-resolves ephemeral capabilities and
confirmation instead of persisting an invented applied state. Failure between
desired write and external effect leaves desired state retryable; failure after
an external partial effect never dispatches under that effect or rewrites native
identity. Preserve receipts, deduplication and unresolved-input recovery.

## ACP extraction and acknowledgement rules

Make `AcpAgentSession` partial; move ordinary model/effort fields, catalog/default
initialization, resolution, switching RPCs, extraction and confirmation helpers
into `AcpAgentSession.ModelSelection.cs`. Keep transport, prompts, permission and
usage publication in their existing owners. Share parsers with Worker selection
without weakening its stricter full-options verification or callback ordering.

Add `ConfirmsThoughtLevel(result, optionId, requested)` symmetric to
`ConfirmsModel`: require an object, reject `success:false`; if option readback is
present require the requested option/current value and reject contradictions or
malformed relevant entries. Preserve the model contract's empty-object positive
acknowledgement compatibility when readback is absent; do not mistake a rejected
or malformed relevant readback for absence just because `AcpSessionOptions.Read`
filters it out. Worker still requires its advertised readback. Apply this predicate
inside the ordered RPC response callback and to the returned result, exactly as
model confirmation does, before changing applied effort/explicit flags/revision.

Model selection can reset thought-level. After model success, record only actual
model/effort evidence and mark the complete target unconfirmed until effort is
satisfied. If effort then fails, return Rejected with partial observed state;
never claim the old pair, the target pair, or attempt an unverified rollback.
Method-not-found may choose another documented model operation; any reopen result
requires demonstrated launch/resume support for the full target. Open must not
rescue an effort failure merely because the loaded model matches.

## Claude and Codex decisions

Wire Claude launch effort as separate `--effort`, value arguments for both new
and `--resume` launches, including null model plus explicit effort. Declare live
changes RequiresReopen when the full target is expressible at launch. AgentRuntime
owns capability detection for the resolved command (including overrides), with
bounded help probing/caching; ClaudeAgentSession owns argument construction.
For a command without advertised effort support, declare Unsupported and fail
explicit effort rather than dropping it. Advertised levels do not prove every
model accepts them; propagate CLI rejection. Do not add `ultra` or infer a minimum
CLI version from this single verified installation. Launch-only confirmation must
be labeled as such and checked against startup errors before prompt acceptance.

Codex declares its existing open/turn effort support and RequiresReopen for
ordinary live changes; retain explicit Worker turn configuration and native
identity. Audit its existing guarded default-model fallback separately: explicit
model/effort rejection must never silently select another value. No new generic
fallback policy belongs in Kernel.

## Red/green verification plan

No regression was executed for this documentation task; findings above are static
audit evidence. Implementation starts by making each regression fail through the
smallest production open/switch/resume entry point, before changing production.

1. **AgentKit:** extend `AcpProviderTests`, `IndependentSelectionReviewTests` and
   `MockCliFixture`: cross new/load/live, null model + explicit effort, default
   reset with/without original evidence, suffix/raw IDs, method-not-found,
   empty acknowledgement, nonobject, `success:false`, malformed/mismatched
   thought readback, and model success followed by effort failure/timeout.
   Assert outcome, native ID, revision, idle usage rejection and zero prompts
   after incomplete configuration. Retain Worker response-order tests. Extend
   `ClaudeProviderTests` to capture argv for open/resume, omitted effort, all
   advertised levels, unsupported help and startup rejection; extend
   `CodexProviderTests` for open/resume effort and explicit rejection behavior.
2. **Kernel:** replace boolean fakes with scripted capabilities/outcomes, complete
   request recording and observed state. Extend `SessionStreamingAndCompactionTests`
   and `SessionContextUsageTests` for every outcome, exact open/dispose/prompt
   counts, permission+effort reopen, active/compaction/Drain deferral, stale idle
   callbacks, reopen failure and partial rejection. Test resume confirmation
   before durable completion and preserved recovery holds on failure. Verify
   accepted A/low executes after desired B/high without rewriting B/high.
3. **Host integration:** update `PergaHostTestCluster` fake; extend `SessionModelTests`,
   `ProviderSessionResumeTests`, `DurableSessionInputTests`,
   `SessionInputBatchRecoveryTests` and `WorkerSessionIntegrationTests`. Use a temp
   database and fresh host on the same database for restart. Assert normalized
   default-model/explicit-effort persistence, existing error envelopes/events,
   no prompt on rejection, historical nulls, receipts/replay, unchanged native
   identity, Worker version/approval boundaries and recovery after committed
   acceptance or desired write but before external effect.

Run affected projects while iterating, then `dotnet test Perga.sln` from root for
the shared internal contract change. Use controlled providers; no real provider
turns on shared hosts. Mutation-check acknowledgement rejection and no-dispatch
guards in an isolated checkout, restore production and confirm green. This task's
checks are document paths, diff whitespace, and the declared design-doc gate;
implementation test results must be reported separately.

## Round-2 identity and activation corrections

A requested Claude resume ID is intent, not provider evidence. Keep observed
identity empty until a stdout frame supplies it. Resumed launch confirmation
requires both initialize acknowledgement and matching observed identity within
one handshake timeout; a fresh launch may discover identity on its first turn.
A contradictory identity or unexpected stdout closure permanently fences that
session, clears confirmation and stops the child. Later turns return a terminal
error before writing another prompt.

Dispatch preparation can persist the identity of a replacement context created
by compaction. Build the active running session from the latest actor session
after preparation, preserving its identity and usage, and regenerate the running
event from that same value. Durable input claims retain their existing atomic
claim ordering; activation publishes the refreshed snapshot. SQLite integration
regressions hold both wake and durable-input streams open to check identity
before settlement can repair it.
