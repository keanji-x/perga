# Stage 2 implementation

Outcome: one `SessionRuntime` per session serializes provider lifecycle work outside
TrackActor's mailbox. SQLite `agent_sessions` remains the durable recovery authority.
The scheduler retains only turn admission and dispatch decisions.

The runtime owns startup cancellation, the single provider handle, configuration,
context usage binding, and recovery/shutdown state. A short snapshot lock never
awaits provider work; a semaphore serializes open, configure, close, and teardown.
Recovery commits retain epoch and native identity until shutdown and replacement
validation succeed. Failed shutdown retains the exact handle for retry.

Configuration patches persist immediately and rebind lazily on the next operation.
Queue and recovery progress publish transient invalidations without SQLite events.
Verification covers cancellation followed by send, shutdown retry, failed reconnect,
mailbox responsiveness, context binding, and the full solution gate. Review tier: L2.
