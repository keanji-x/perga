# Conversation permission ordering regressions

Outcome: permission prerequisites preserve unknown-send identity, survive failed
ancestors until every real write settles, and serialize permission PUTs for a
card across pane lifetimes. Review tier: L1; independent review is arranged by
the planner. There is no migration or operational change.

The tab's existing ConversationRegistry remains the owner. Its aggregate waits
for both the preceding barrier and the current write with allSettled before
propagating a failure. The mutation adapter captures transport admission at the
press, registers the write synchronously, and dispatches after that card's prior
barrier settles. An ACK publishes only while its barrier is current and the
plannerRun query's dataUpdateCount has not advanced since dispatch. Cancellation
still precedes publication and reconciliation. An unknown outbox retry retains
its original delivery classification when this prerequisite fails; a fresh
send remains refused and restores its draft and attachments.

No frozen frontend contract, module-level mutable state, backend, OpenAPI, wire
schema, or package-lock change is required. Mutation consumers now explicitly
inject the registry; test consumers inject a stable test-tab owner.

## Red evidence

Before fixing production code, ran from fe:

- `npx vitest run --project web-dom web/src/app/providers/permission-ordering.contract.test.tsx web/src/app/conversations/registry-owner.test.ts`
  reproduced an unknown retry becoming null instead of failed/unknown, and a
  three-generation barrier becoming undefined while a real write remained out.
- A focused repeat of the permission contract file, holding reconciliation GETs,
  reproduced both old-ACK cases: permission_mode was full instead of the newer
  GET-confirmed never, with and without another A write.

After completing the browser scenarios, temporarily restored the four touched
production implementations to HEAD (c92c7f8), ran the following command, and
restored the fixes:

`npx vitest run --project browser web/src/app/router/permission-ordering.browser.test.tsx`

The baseline had three expected failures: the unknown Try again button vanished
after permission failure; the Never permission disappeared after an old ACK;
and A-B-A dispatched two PUTs where serialization required one. The separate
registry unit regression isolates the three-generation fail-fast cleanup bug.
The browser unknown case seeds an unconfirmed op through the production registry
and then uses the actual router's retry buttons; the contract case verifies the
same original key reaches transport on the next retry.

## Green evidence

The focused unit/DOM command passed 22 tests after repair. The affected Chromium
router file also passed its original and added scenarios. The task gate runs
full lint/build, all unit/DOM tests, and the eight specified affected browser
files; its exact final outcome is supplied in the task result.

Targeted mutation verification used an isolated tracked-source copy under
`/tmp/perga-permission-mutation.EQUIn0`. Replacing the registry aggregate with
fail-fast Promise.all made exactly the three-generation test fail (undefined
barrier; 14 other tests passed). Restoring the implementation passed all 15
registry tests. The working checkout contains no mutation residue.

Gate run 2 passed lint/build, 5,785 unit/DOM tests (one existing skip), and all
36 browser tests in the eight affected files. A final browser assertion then
explicitly checked that a three-generation send stays Sending with an empty
composer while the last PUT is pending, distinguishing waiting from an early
refusal. The updated browser file passed all seven tests before the final gate.
