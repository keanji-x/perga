# Execution-scoped approvals and Stop

Baseline is main at `97677a4` with the phased session turn runner. This design authorizes updating the frozen
frontend API and event contracts together with the Host producers. Stop must reach the provider while SQLite is
busy. An approval expires when its execution stops, even when storage fails.

## Ownership

- A durable Ask is a question for the Track. Answering it retains the existing
  wake behavior. An Approval is a live request owned by a turn or a suspended
  policy operation. Its execution ID and approval ID identify one decision.
- The Kernel owns approval admission and execution state. The provider adapter
  owns protocol request IDs, cancellation replies and terminal evidence.
  The turn runner keeps consuming events while an approval is pending.
- Track and Worker views read pending approvals from the same live registry.
  Approval notifications invalidate the view. Reconnection fetches the
  current view. SQLite keeps turn outcomes and approval decisions for audit.
  A forward migration retires existing open Hold rows. Historical records
  keep their original identity. Audit records a durable decision before
  granting it, then records delivery evidence. A missing delivery record
  leaves the outcome unconfirmed after restart.

## Stop and answer ordering

- Stop identifies the observed turn. Its acceptance marks that execution
  stopping and expires its pending approvals in one serialized transition.
  Provider cancellation runs independently of SQLite writes and of the HTTP
  connection. Control observation can drain a terminal frame while the runner
  is persisting earlier events. A repeated Stop reuses the in-flight cancellation.
- Approval validates the execution and request, persists the human decision
  for audit, then rechecks the execution before releasing the decision. Stop
  wins against any decision that has yet to claim delivery. A reply already
  being sent can take effect. The provider cancellation then owns cleanup.
- Provider terminal evidence or confirmed scoped close proves quiescence.
  A lost response or timeout leaves the binding fenced until confirmed safe.
  The scheduler keeps the old execution slot until cancellation and turn
  settlement allow reuse. Worker pause validates the current attempt in the
  Track mailbox, where completion also claims the attempt. Resume records its
  recovery fence before it joins the same stopping transition.
- Policy AskHuman attaches to its suspended operation. A call made by a live
  turn also expires when that turn stops. MCP captures the originating turn
  at authenticated ingress before it reads or dispatches the request. A fresh Host has no pending approvals
  and reconciles previous execution outcomes using existing evidence.

## Verification

L2. Reproduce a Stop during approval against a terminal-stop provider before
changing the implementation. Exercise a held SQLite writer, Stop before and
during approval delivery, approval audit failure, late events, retry, restart,
and next-turn isolation. Cover provider and policy request paths, Worker
controls, and the browser flow. Review the persistence and execution boundaries
independently and run the full backend and frontend gates.
