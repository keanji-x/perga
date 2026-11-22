# OAR runtime activity

Runtime status and provider tasks are ephemeral observations owned by the open
provider session. They are independent of Perga turn admission, task bindings,
and idle waits. AgentKit delivers observations through the turn stream and an
attached runtime observer; the Kernel consumes the observer once, through its
mailbox, fencing callbacks by provider identity and session epoch. Closing the
provider detaches the observer and discards its snapshot. Ended task retention
is bounded; running tasks remain visible until completion or session close.

REST session items expose the current snapshot. Transient runtime events trigger
snapshot refreshes in clients, including when no turn is active. Bridge status
deduplication ignores running activity timestamps and coalesces idle timestamps.

Verification covers bridge forwarding/coalescing, AgentKit pending and live
delivery, Kernel snapshot/event delivery and admission independence, and frontend
schema/event decoding. Existing mock bridges may omit the initial status.

## Implementation decisions

- Keep OAR 0.124.0. Read the initial `session.status()` and incrementally fold
  raw records with public `reduceStatus`, retaining its entire checkpoint.
  Replay startup task events with public `eventsOf` before live delivery.
- Emit structural status changes immediately; coalesce idle activity timestamps
  at one second. Normalize structured tool phases into phase/tool/call fields.
- Fence runtime observations by provider identity, snapshot identity, epoch,
  and an exit latch. Continue folding while archived, hiding archived snapshots.
  Detach on close and retain at most 64 ended tasks, ordered by completion.
- Preserve OAR task patch semantics and clear old results when tasks reopen.
  Publish transient events through the existing generic Host SSE/WebSocket
  serializers. No storage migration or change to turn/overlay idle semantics.
- Refresh the session-items query for runtime events and show runtime phase,
  recent idle activity, and active non-ambient task count in the session header.

## Verification

- Bridge (`tools/perga-oar-bridge`): `npm run typecheck`, `npm run build`
  (rebuilding `dist/perga-oar-bridge.mjs`), and `npm test` (including
  `test/runtime-activity.test.ts` covering startup task replay, in-turn and
  out-of-turn `task_*`, running phase changes, and 1s coalescing of
  `idle.activity`).
- .NET (`Perga.sln`): `Perga.AgentKit.Tests` (including startup exit, pending
  and live out-of-turn delivery in `OarBridgeTests.cs`), `Perga.Kernel.Tests`
  (including `SessionRuntimeActivityTests.cs`), `Perga.Host.IntegrationTests`,
  and supporting test suites pass.
- Frontend (`fe/`): `npm run lint`, `npm run build`, `npm test` (including
  `core/api/runtime-activity.test.ts` and `tools/architecture/openapi-contract.test.ts`),
  and real-browser conversation header checks (`conversation-liveness.browser.test.tsx`).

## Changed files

- `docs/oar-runtime-activity.md`
- `tools/perga-oar-bridge/src/bridge.ts`
- `tools/perga-oar-bridge/src/bridge-helpers.ts`
- `tools/perga-oar-bridge/test/runtime-activity.test.ts`
- `libs/Perga.AgentKit/Contracts.cs`
- `libs/Perga.AgentKit/OarAgentSession.cs`
- `src/Perga.Contracts/RuntimeActivity.cs`
- `src/Perga.Kernel/KernelModels.cs`
- `src/Perga.Kernel/Events/IKernelEventBus.cs`
- `src/Perga.Kernel/Actors/SessionRuntime.cs`
- `src/Perga.Kernel/Actors/TrackActor.cs`
- `src/Perga.Kernel/Actors/TrackActor.ContextUsage.cs`
- `src/Perga.Kernel/Actors/TrackActor.RuntimeActivity.cs`
- `tests/Perga.AgentKit.Tests/OarBridgeTests.cs`
- `tests/Perga.Kernel.Tests/Fakes/KernelTestHarness.cs`
- `tests/Perga.Kernel.Tests/SessionRuntimeActivityTests.cs`
- `fe/core/api/perga-v1.ts`
- `fe/core/api/schemas.ts`
- `fe/core/api/generated/wire.ts`
- `fe/core/api/generated/openapi.json`
- `fe/core/api/runtime-activity.test.ts`
- `fe/core/events/invalidation-plan.ts`
- `fe/web/src/app/conversations/contracts.ts`
- `fe/web/src/app/conversations/store.ts`
- `fe/web/src/app/router/public.tsx`
- `fe/web/src/features/chat/thread/session-header.tsx`
