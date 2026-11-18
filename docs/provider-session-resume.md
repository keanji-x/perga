# Resume a provider session

The user can ask Perga to resume the original provider context for the
observed turn. The Kernel holds that session's execution slot and fences
other dispatch and provider changes while recovery runs. It asks the adapter
to cancel and waits for a terminal event. A negotiated, acknowledged
session close is also evidence of quiescence. Disconnecting an adapter or
timing out only proves that Perga lost contact. A provider without usable
stop evidence leaves the session paused. Perga does not terminate an
unverified shared process or treat root-process exit as proof of isolation.

Once the previous turn settles, Perga reuses the original native ID,
workspace, and provider configuration. The adapter handshakes outside the
actor. Successful recovery preserves the Perga session and its epoch,
resolves an existing unknown input without changing its delivery evidence,
and releases the queue. The interrupted prompt is never replayed by recovery.
A failed attempt can be retried with the same observed turn identity. The in-flight request is actor-local. Recovery safety survives restart.

This entry applies to ordinary conversations. Worker attempts retain their
scoped Continue and interruption controls. Claude is omitted until its
adapter can attest to restoring the original context.

ACP selects session/resume or session/load using capabilities from
initialize. A provider-scoped close uses the same capability negotiation.
Protocol cancel and recovery have bounded waits. Tests hold provider work
under a controllable local Host, simulate terminal and missing terminal
evidence, and check the resulting queue dispatch and provider identity.

Recovery admission persists a fence before requesting cancellation. The fence
survives Host restart and blocks dispatch and replacement for this session.
Only trusted stop evidence advances it to quiescent. Successful recovery
updates the session, resolves its old receipt when necessary, and clears
the fence in one write transaction. A previously unfenced unknown input
continues to use its existing recovery contract.
