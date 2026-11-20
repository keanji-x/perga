# Phase 1 session recovery implementation

The session row is the durable authority for the five runtime states and unknown metadata. Archive and Track closure remain orthogonal write gates. The latest persisted turn remains visible after settlement.

Recovery accepts only unknown sessions for reconnect, and idle or unknown non-Worker sessions for reset. An actor reservation serializes recovery while external shutdown and launch run outside the mailbox. Dispose stops the provider subprocess tree; ShutdownConfirmed must hold before clearing identity or launching a replacement. MCP is revoked before rebinding. Reconnect validates native identity and configuration before atomically clearing unknown metadata and resolving the outstanding input. Failure preserves unknown state and records the error. Reset atomically resolves any outstanding input, updates the epoch and identity, and appends the epoch boundary and session event.

Migration 0019 copies active legacy fences into session unknown metadata and removes the parallel table. Earlier migrations stay frozen. Restart marks in-flight sessions and turns unknown with host_restarted. Verification covers recovery, failed reconnect, restart, timeout, archive, and API projection; the full solution test gate verifies consumers. Review tier: L2. No wire compatibility is retained for removed HTTP recovery endpoints.


Phase 1 was replayed onto upstream f9d3dcf2, retaining upstream permission modes,
provider model discovery and their OpenAPI schemas. Frontend changes are limited
to the published OpenAPI JSON; the UI implementation remains Phase 2 work.

Liveness adds bounded startup and compaction, a scheduler startup cancellation
slot with observed-turn fencing, and a session_lost circuit breaker. Reset
captures opEpoch before external work and requires that epoch at the SQLite
transaction boundary. The transaction interrupts and archives turn-less queue
rows, drops conversation wakes, retains critical wake payloads, inserts the epoch
boundary, and advances the session. Scheduler slots clear after commit; old-epoch
cleanup cannot dispatch work into the new epoch. Compaction timeout fences the
session as unknown until explicit recovery.

Targeted verification covers startup timeout/Stop with a cancellation-ignoring
adapter, queued Stop while admission is draining, both lost-session startup modes,
withdrawal, compaction timeout, reset
rollback/epoch fencing/content preservation/routing, migration constraints, and
wake exhaustion on claim, runtime failure and restart. Isolated mutations that
removed startup cancellation, excluded unknown rows from reset, and allowed a
fourth wake attempt each failed their corresponding regression check for the
expected observable reason. Review tier remains L2; formal independent reviews
are arranged by the Planner. The task gates provide final whole-solution and
frontend ownership/lint verification.
