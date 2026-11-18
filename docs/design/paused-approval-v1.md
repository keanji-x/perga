# Publish paused approval identity through API v1

Outcome: a pending approval remains a hold in snapshots, answers and ask.changed
payloads, including after reopening the database. The Planner gives it priority
and uses its existing option-answer and stale-request UI.

The Kernel chooses identity at creation, never from question text. Policy
AskHuman and provider ApprovalRequested create holds; perga_user_ask creates a
wake question (even when its text resembles an approval). Persist delivery,
caller session ID and, when there is an active turn, that exact turn ID on the
ask row. These values are immutable on answer. SQLite writes them and the event
in one transaction. New migration 0010 adds the columns; released SQL is frozen.
Existing rows keep their previously published wake behavior: no historical
session/turn identity can be recovered or invented. No development-db import or
old HTTP route fallback is added.

Durable identity describes the request, not permission to execute. A hold can
only be answered while its original live waiter exists, is pending, and any
recorded turn still owns that live turn; a closed track also rejects it. After
restart or cancellation return conflict (409), leaving identity intact and never
waking a new Lead turn. A wake question retains ordinary answer/wake behavior.
A live hold resolves only its suspended operation; it never wakes Lead as a
side effect. Existing AskAnswerEvaluator remains the authority for approval vs
denial and its supported answer shapes. A caller without a session may ask a
wake question; it cannot create a hold. Persist before adopting in-memory state
so failed storage cannot consume an approval or publish a partial answer.

The public AskDto, frontend schema and checked-in OpenAPI gain delivery,
sessionId and turnId. Snapshot and event consumers use the same DTO conversion.
The browser fixture supplies these actual public fields; no UI transient or
wording is an identity source. Model/effort behavior is outside this change.

Review tier: L2. Planner arranges independent reviews. Verify with the existing
Chromium approval flow, controlled Host/Kernel approval entry points, restart,
cancelled and duplicate answers, anonymous/cross-track boundaries, provider
approve/deny responses and policy guard execution counts. Run the full .NET and
frontend gates on the final tree.

Initial red evidence: the existing Chromium test `shows a paused turn` fails
at the missing `Turn paused, waiting for your approval` text on the unchanged
production tree. The previous fixture task records 34/40 passes (six approval
failures); its historical gate log is unavailable in the current Track view.

## Contract change and validation evidence

The task explicitly authorizes publishing the missing v1 identity and updating
its frontend consumers. This is the change request for frozen `fe/core/api`:
add required delivery plus nullable sessionId/turnId; update its OpenAPI and
contract fixtures together. The delivery commit must retain the corresponding
OWNERSHIP-CHANGE trailer referencing the #38 follow-up.

Controlled red: adding the delivery assertion to the real Host MCP policy guard
flow fails with KeyNotFoundException for `delivery` at the ask.changed payload.
After implementation that same approval/denial/upstream-count test passes.
The existing Chromium approval file passes 20/20 with actual public DTO identity.
The frontend API contract file passes 9/9, covering wake versus hold and event
identity without deriving either from the question's wording.

Seven new Host tests pass: four provider answer shapes (approve, deny, boolean,
and mismatched option echo), reopened hold, interrupted hold plus anonymous wake,
and injected answer-event failure with rollback and retry. Two forward-migration
cases pass, including a failed v10 receipt, rollback, retry and repeated startup.
No v1-v9 SQL is edited. Historical asks retain wake presentation with unknown
ownership; no historical hold backfill is claimed.

Targeted mutation verification ran in `/tmp/perga-approval-mutation-6efjxw8q`.
Removing the hold expiry check makes exactly the two selected restart/interruption
cases fail with expected 409 versus actual 200. Restoring the production file
makes both pass. The working checkout contains no mutation.

An early full frontend unit run was interrupted after the old contract fixture
failed and the run continued emitting scrollTo notices. The fixture was corrected
and its focused contract test passed. Final task gate results are reported by the
worker with the gate log; the early run is not a passing full-suite claim.

Default `npm run lint` reaches the ownership audit, then fails on historical
commits c163d245 and 80a57304 already present in the task baseline, whose frozen
API/event changes lack trailers. No enforcement rule is changed. A scoped audit
using this attempt's original HEAD as OWNERSHIP_BASE_SHA is the applicable final
change-range check; its actual result is recorded in the worker report.

The first formal gate caught three hardcoded v9 expectations; they now explicitly
expect v10. Its other projects passed, including Kernel 59 and Host 80 tests.
A further replay regression first failed both migration cases because historical
ask.changed events lacked delivery. Migration 0010 now adds their previously
published wake meaning in the same transaction, without session/turn backfill,
new event IDs, or a runtime/API fallback. Receipt failure also rolls back this
payload normalization. Current hold events retain their authoritative identity.

## Committed answer recovery (PR #42 L2 follow-up)

The successful SQLite ask/event transaction is the acceptance boundary. Before
it returns, failures leave the actor and original waiter unchanged. After it
returns, adopt Answered and remove the waiter inside the serialized actor turn,
then attempt the existing event synchronization without the HTTP cancellation
token. Complete the removed waiter in a finally block inside that same actor
operation. EnqueueAsync's caller may already have stopped waiting on cancellation;
completion therefore cannot depend on its outer continuation. Keep synchronous
event catch-up before waiter completion on the normal path; if reading fails,
the unchanged event cursor lets the next synchronization replay the durable
answer before subsequent turn events. Propagate the synchronization error while
keeping the committed decision final. No schema, migration or public contract
changes are needed. Hold ownership checks and ordinary wake dispatch remain in
their existing paths. Review tier remains L2, with independent reviews arranged
by the Planner.

Controlled follow-up evidence (2026-11-19): the unchanged Track tree matches
PR head 23b64e8 (tree 4d404ebd61a32e567c2557feef1485db01f9533f).
Reused the authority review's real Host/Kernel/SQLite store proxy approach in
`AnswerPublicationTests`, injecting a single event-read IOException, cancellation
immediately after commit, and cancellation while synchronous catch-up is blocked.
All three tests first failed because the actor snapshot still exposed Open
(`/tmp/pr42-fix-red.log`). The final tests additionally require the original
provider waiter to consume false exactly once, opposite HTTP retry to return 409,
one durable answered event, unchanged false answer, and broadcast using that
SQLite event ID before turn.completed. The new three plus the existing seven
paused-approval cases pass 10/10 (`/tmp/pr42-fix-green.log`), including the real
SQLite trigger rollback/retry case. An intermediate compile caught an xUnit
assertion-style analyzer error; it was corrected before the passing run.

Mutation verification in `/tmp/pr42-answer-mutation-7izfcla5` removed only the
committed waiter completion. Predicted: all three new cases fail on missing
provider response. Actual: 3/3 fail at that assertion (`/tmp/pr42-fix-mutation.log`).
Restoring the production file passes 3/3
(`/tmp/pr42-fix-mutation-restored.log`). No mutation remains in the working tree.

The frontend subtree has no changes and retains the exact PR head tree
3204e76f9de9adfd40e190179edec53d28f8d788. The independent migration/contract review
recorded API 9/9, Chromium 20/20 and tsc success on that tree; no fresh full
frontend lint/build/unit pass is claimed here. This follow-up's formal task gate
runs the complete .NET solution and the approval Chromium file on its candidate
commit; consult its gate log and worker result for the final verdict. There is
no migration or operational rollout change, public DTO change, or real provider
execution in this follow-up.
