# Frontend run adapter cleanup

Outcome: read one frontend run-state contract from
`GET /api/v1/sessions/{id}/items?afterId=0&limit=1&view=run`, remove the
synthetic legacy run envelope, and preserve unset model effort without inventing
an explicit effort. This task writes the design only.

## Baseline and audit

Audited checkout on 2026-11-19: HEAD
`44cece81d09e2930e94f7b77bfbc429e15430e93`
(`Fix runtime reasoning effort switching and readback`), exactly the supplied
PR 72 head. The initial working tree was clean. Local `origin/main` is
`5337fcd85bb0a55e3dcfeb3759e674bf755be2a7` (#71); merge base is
`eb7b66d9876eebb6ece1797efc190acff60c198a`.
`git merge-base --is-ancestor 44cece81 HEAD` exits 0; the same check against
`origin/main` exits 1. Thus PR 72 is present in HEAD and absent from the local
remote-tracking main history. No fetch or network PR inspection was performed;
this does not establish the current server-side merge status.

The following inventory covers exact symbol references in `fe/core` and
`fe/web`; paths below are relative to `fe/`.

| Symbol | Definition and every caller/test |
| --- | --- |
| `plannerRunOperation` | `core/domain/conversation.ts:410`; only tests: `core/domain/v1-conversation-reads.test.ts` (state and usage loops), `planner-switch.test.ts` (capabilities loop), `planner-permission-mode.test.ts` (reject old response). No production caller. |
| `projectPlannerRun` | `core/domain/conversation.ts:387`; called by `plannerRunOperation` there and `conversationRunOperation` in `core/domain/conversation-display.ts:449`. No direct test caller. |
| `PlannerRun` | Defined in `core/domain/conversation.ts:274`; used by both functions there and imported/used for the return/transform intersection in `core/domain/conversation-display.ts:15,444,448`. No other exact type reference. |
| `conversationRunOperation` | `core/domain/conversation-display.ts:444`; sole production caller `web/src/app/providers/queries.ts:144` through `plannerRunQueryOptions`, consumed by `app/conversations/store.ts`. Tests: `core/api/perga-v1.test.ts:516,727` (usage/compact and nullable permission), `core/domain/v1-conversation-reads.test.ts:26,43,57` (states, usage, persisted effort), `planner-switch.test.ts:40`, `planner-permission-mode.test.ts:26`, `conversation-display.test.ts:246` (reject obsolete run-shaped inputs). |
| `conversationRunMetadataSchema` | `core/domain/conversation-display.ts:398`; used only as `z.infer` in the operation signature/transform, never parsed on its production result. `core/domain/conversation-display.test.ts:98–242` directly tests runtime-state requirements, pending input identity/order, decimal-u64 submission CAS, successor intent, and terminal outcome validity. No web caller. |

`conversationRunOperation` currently parses `SessionItemsPageDto`, calls the
base projector, then overrides queue, worker, interrupting phase and compact
capability. The base projection is therefore incomplete by itself.
`plannerRunOperation` uses `view=planner-run`; the real reader uses `view=run`.
Host `src/Perga.Host/Http/ApiV1Endpoints.cs` binds neither `view` value: both
return the same DTO via Kernel `TrackActor.GetSessionItemsAsync`. The query
suffix is a frontend request convention, not negotiated backend authority.

### Legacy field inventory and disposition

All production hardcoding is in `core/domain/conversation.ts:387–406` and
`core/domain/conversation-display.ts:444–470`. Their consumers are real code,
but the listed values cannot become live facts through this production reader.

| Field(s) | Current value, callers/tests, planned disposition |
| --- | --- |
| `pending_overflow` | Always 0. Type/comment/projector in `conversation.ts`; read in `web/src/app/conversations/store.ts:169`, forwarded as `pendingQueueOverflow` through `contracts.ts` to queue/composer/edit conditions in `app/router/public.tsx`. Remove field, stale overflow comment, and run-derived accounting. Preserve actual queue and pending-send accounting. |
| `attachments_supported` | Always false. Type/projector in `conversation.ts`; `store.ts:479` → contracts → router composer. `core/domain/planner-switch.test.ts:42` asserts it. Remove run field; keep attachment admission disabled explicitly at app composition until a real v1 capability exists. Do not enable attachments by inference. |
| `compact_outcome` | Always null. Type/projector in `conversation.ts`; `store.ts:483` → contracts → router completed notice. Fixtures in `planner-permission-mode.test.ts`, `conversation-display.test.ts`, `web/src/app/router/planner-asks.test.tsx`. Remove run field and unreachable notice; keep manual compact request, pending/error feedback and usage refresh. |
| `recovery` | Always null in this run. Type/projector in `conversation.ts`; `store.ts:108,214,440` → contracts, restart hook, router → thread outcome notice. Fixtures in `core/domain/v1-conversation-reads.test.ts`, `planner-permission-mode.test.ts`, `conversation-display.test.ts`, `web/src/app/router/planner-asks.test.tsx`. Remove run-owned legacy recovery/restart branch; retain v1 `resume` and `unknownInput` actions. |
| `input_contract`, `runtime_state` | Hardcoded canonical and running/idle (not actual session status). Schema/refinements and transform in `conversation-display.ts`; `store.ts` uses them for held/send/live/stop/restart and legacy queue mutation selection. Replace with unconditional v1 protocol and actual session/worker/active-turn facts. |
| `history_replacement_supported` | Always false. Schema/transform in `conversation-display.ts`; `store.ts:92` → contracts → router edit-message condition. Remove run field and disable this unsupported action explicitly; preserve queue editing. |
| `terminal_timeout`, `terminal_outcome` | Always null. Schema/transform in `conversation-display.ts`; `store.ts:206,541–542` → contracts → router timeout/current-response props. Remove run fields and dead run-derived notices; preserve transcript turn outcomes and stop acknowledgement checks. |
| `pending_inputs`, `active_input` | Always [] / null. Schema/transform in `conversation-display.ts`; `store.ts:86,452` → contracts → router pending accounting and held-input props. Remove from run/store contract and prune unreachable wiring. Queued messages and unresolved v1 input receipts remain independent active features. |
| `native_turn_id` | Derived from `running_turn.turn_id`, not an additional authority. Schema/transform in `conversation-display.ts`; `store.ts:198,219` uses it for live and steer targets. Replace with `activeTurn.id` only while that turn is running; retain worker attempt + turn target checks. |

Every additional exact snake_case occurrence in the audited directories:

- `core/domain/conversation-display.test.ts` exercises all eight metadata
  fields, including nested `active_input` attempt `target.native_turn_id`, and
  has a `compact_outcome`/`recovery` old-response fixture. Replace metadata-only
  tests with DTO-boundary behavior; retain independent held-input/CAS operation
  tests where their helpers still have callers.
- `web/src/app/router/conversation-read-data.ts:17–19` fabricates the eight
  metadata fields in `historicalConversationRun`; its only caller is
  `planner-asks.test.tsx:44`. Convert that run fixture to a real DTO and delete
  this helper. Other exports in the fixture module have separate callers and
  remain.
- `core/domain/conversation-restart.ts` and
  `core/domain/conversation-recovery.test.ts` use `pending_inputs` as a legacy
  recovery reason, not a v1 queue field. Keep their separate recovery contract
  tests unless a subsequent reachability cleanup removes the whole owner.
- `web/src/features/chat/thread/held-input.test.tsx` and
  `turn-outcome.browser.test.tsx` use nested `target.native_turn_id` for held
  submission identity. Retain these standalone component contracts/tests;
  removal of a top-level run alias does not erase a target identity.
- `core/api/generated/wire.ts` contains `ConversationInputTarget.native_turn_id`.
  `core/api/generated/openapi.json` publishes the legacy run fields under
  `GetPlannerRunResponse` / `PlannerRunReadResponse` and related input/target
  schemas. Retain these published artifacts. The exact `PlannerRun` symbol is
  distinct from generated names containing that substring.

Other uses of the word `recovery` concern transport admission, authentication,
read retry, task recovery, sidebar/settings recovery, and descriptive text;
none is evidence for retaining the null run field. In particular preserve
`core/api/{types,client}.ts`, `core/domain/recovery/`, task-recovery owners,
`web/src/systems/recovery/` and app auth/provider recovery infrastructure.

## One canonical read contract

Keep `conversationRunOperation(sessionId)` in the core domain as the only run
operation. Its result is `SessionItemsPageDto` (optionally named
`ConversationRun = SessionItemsPageDto`), parsed directly with
`sessionItemsPageDtoSchema` from `core/api/perga-v1.ts`. Delete
`plannerRunOperation`, `projectPlannerRun`, `PlannerRun`, and
`conversationRunMetadataSchema`; do not retain aliases, alternate views,
legacy response acceptance, or a second projection path. Reject old run
objects and raw row arrays at this boundary. Preserve current required/null
validation, especially `unknownInput` and `session.contextUsage`; do not relax
v1 schemas to accommodate old test fixtures.

Read server fields directly; use small core selectors only for display
calculations shared by consumers:

- `session.id/status/model/reasoningEffort/permissionMode` are authoritative.
  Preserve null effort independently of model. Never reduce runtime status to
  running/idle or manufacture an unavailable recovery declaration.
- `activeTurn` supplies running identity and elapsed anchor; keep the existing
  nonnegative elapsed calculation and response-arrival anchoring. Preserve
  queued → issuing and worker interrupting → interrupting display behavior.
- `queuedMessages`, `queueHandoffIds`, `canSteer` preserve queue order,
  handoff retirement, queue edits/removal, and steer admission. Keep the
  existing v1 mutation branches; removing `input_contract` must not select
  the older `/api/cards` CAS branch. Do not invent durable queue revisions.
- `worker` retains asks, settings, native steer, attempt identity, activity
  sequence/reset, and polling while running/interrupting. `unknownInput`
  retains held/unconfirmed receipt and resolution behavior; nullable presence
  no longer serves as a protocol detector because this reader is always v1.
- `resume` retains available/resuming/paused state, leases and fresh-start
  capability. This is distinct from the retired legacy `recovery` field.
- `session.contextUsage` still goes through `sessionContextUsage` for the
  meter, including null reading/window/ratio and observed timestamp.
- Keep model, reasoning effort and permission writes, compact, interrupt and
  their feedback/invalidation. Keep current compact admission (`worker ==
  null`), worker interrupt target `{attemptId, turnId}`, and v1 stop completion
  behavior. Do not derive terminal success from a null field or a failed read.

Retire the now-unconsumed `orderedPendingInputs` and terminal-outcome schema/
`currentConversationOutcome` helper if their only remaining callers are the
removed run tests. Keep `heldConversationInputs`, keep/retry receipt schemas,
`decimalU64`, and related helpers while independent thread/operation callers
remain. Remove only imports made unused by deleting metadata; transcript
schemas, timeout evidence parsing, generated target identities and standalone
recovery contracts have separate owners. This is frontend adaptation, with
no backend route, storage, migration, authority or published OpenAPI change.

Keep `fe/tools/architecture/openapi-contract.test.ts` intact, including its
negotiated `PlannerRunReadResponse` composition, required compact capability,
legacy metadata roster, v1 paths and nullable context usage assertions. Do not
remove published schema components to make source searches empty. `gen:api`
only prints a notice; no generated artifact change is needed here.

## ModelPill unset effort

Source inspection confirms the defect in
`web/src/features/chat/thread/model-pill.tsx`: `defaultStopIndex` becomes 0
when `effortDefault` is null, and `Math.max(0, findIndex(...))` also collapses
unknown explicit values to 0. The picker therefore labels unset effort as the
first advertised effort, often low. Save currently submits the draft unchanged;
the wrong label is not proof that an explicit low was persisted. No runtime
reproduction or failing regression test was run in this docs-only task.

Implement these exact semantics in ModelPill:

1. When supported efforts are nonempty and the resolved declared default is
   null, prepend `{value: null, label: 'Default'}` to the picker stops. Locate
   the null draft by equality against that stop. Display and announce Default,
   never low; explicit low now has its own subsequent index. Use this same
   stop array for slider value, labels, ARIA text, ticks and change callbacks.
2. With a declared default that matches an advertised effort, preserve the
   current default position and `medium (Default)`-style label for null draft;
   opening or saving untouched still sends null. Do not materialize that
   declared default into the draft. Model/provider switches still clear effort.
3. Remove index-0 coercion on failed lookup. An unadvertised explicit effort
   stays visible as its raw value with an unavailable indication and is
   preserved on untouched Save; offer the supported stops for an intentional
   replacement. A declared but unadvertised default follows the unset Default
   stop behavior rather than guessing an advertised value.
4. Keep the trigger's no-effort suffix when both selected effort and declared
   default are null. Empty supported efforts remain disabled N/A. For a model
   with one supported effort and no default, Default plus that effort gives
   two selectable stops. Explicitly selecting Default writes null; Cancel and
   reopening restore the saved selection and never emit a change.

`effortsFor` must preserve explicit null metadata: when following installation
model, use `catalog.default.reasoning_effort` if the catalog declares it, even
when null; fall back to the resolved model entry only if that metadata is
absent. An explicit model uses its own `default_reasoning_effort`. Share this
resolution between trigger and picker; keep catalog-reported effort order.

## File plan and verification

| File(s), relative to repository root | Planned change |
| --- | --- |
| `fe/core/domain/conversation.ts` | Remove dead run operation/type/projector and stale fields/comments; retain model selection, transcript and timing/usage contracts still used elsewhere. Remove `CompactOutcome` only after its app contract consumer is removed. |
| `fe/core/domain/conversation-display.ts` | Return the DTO directly from the sole run operation; remove metadata envelope/schema and exclusive helpers; preserve transcript and standalone input-operation contracts. |
| `fe/core/api/perga-v1.ts`, `fe/core/api/schemas.ts` | Reuse existing DTO schemas/exports; no wire widening or generated schema edits. |
| `fe/web/src/app/providers/queries.ts` | Keep one run query operation; update inferred result consumers. Preserve query invalidation and cancellation. Remove historical live-query selection from the v1 path rather than deriving it from a deleted flag. |
| `fe/web/src/app/conversations/store.ts`, `contracts.ts` | Read DTO fields and common selectors; remove synthetic store members/dead branches while preserving v1 queue/worker/unknown/resume, timing and mutation behavior. Update `live-replies.ts` and `restart.ts` wiring only where the removed run flags fed it. |
| `fe/web/src/app/router/public.tsx` | Remove unreachable run-driven notices/held metadata/history-edit/overflow props and conditions; retain v1 controls and explicit unsupported attachment/history-edit admission. Preserve transcript outcomes. |
| `fe/web/src/app/router/conversation-read-data.ts`, `planner-asks.test.tsx` | Replace the sole historical run fixture with `SessionItemsPageDto`; retain unrelated transcript fixture helpers. |
| `fe/core/domain/{v1-conversation-reads,planner-switch,planner-permission-mode,conversation-display}.test.ts`, `fe/core/api/perga-v1.test.ts` | Consolidate on the sole operation, remove synthetic-field expectations, preserve old-shape rejection, usage, capability, role, persisted effort and permission coverage. Add queue/worker/unknown/resume DTO cases. |
| `fe/web/src/features/chat/thread/model-pill.tsx`, `model-pill.test.tsx`, `model-pill-layout.browser.test.tsx` | Implement Default stop and null-safe lookup; add no-default multi/single-effort, declared-default, missing catalog, unknown value, Save/Cancel and keyboard/ARIA coverage. Keep availability/browser and role tests. |

Before implementing, obtain owner/orchestrator agreement for the core/app
contract changes under `fe/AGENTS.md`; this document proposes that change,
without modifying frozen interfaces. Scope any additional consumer edits to
this contract and rerun affected checks after fixes.

Verification for implementation, from repository root:

```sh
# Install only if dependencies are missing or the lockfile changed.
(cd fe && npm ci)
(cd fe && npm run lint && npm run build)
(cd fe && npm test)
(cd fe && npm run test:browser -- model-pill planner-switch conversation-compact conversation-steer provider-session-resume conversation-canonical conversation-recovery conversation-restart conversation-response-boundaries)
```

First add a regression through ModelPill's production component with null
selection, nonempty efforts and null default. Confirm it fails because picker
label/ARIA says low; then implement and confirm green. Cover untouched Save
retaining null, explicit effort selection, clearing to Default, Cancel/reopen,
provider/model changes, empty efforts and a declared medium default. Exercise
keyboard and pointer selection in a real browser. Extend `planner-switch`
browser coverage to an existing session and verify PUT/readback retains null.

Unit and affected browser coverage must also preserve queue/steer/worker
interrupt targeting, unknown-input resolution, resume/fresh-start, usage meter,
manual compact feedback and no duplicate dispatch. Run the unchanged OpenAPI
contract tests through `npm test`. Search the final tree for the audited
symbols: zero dead run operation/projector/type/schema references and no
synthetic snake_case run fields; remaining generated/standalone contract
occurrences must match the retained inventory above. If browser sandboxing
blocks execution, record command, cwd, exact error and unrun cases for Planner.

This design delivery: inspect links/commands, `test -s` and `git diff --check`;
application suites are planned, not claimed as run. Review tier L0 for this
docs-only delivery; implementation tier L2 because it replaces a shared
frontend schema/contract across core, providers, store and router. Planner
arranges two independent reviews covering callers, action admission, partial
failure/refetch and retained tests. No persistence migration or operational
rollout is required.
