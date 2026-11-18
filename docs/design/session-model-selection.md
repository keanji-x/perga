# Session model selection

The persisted session Model is the desired model. TrackActor serializes model
updates with session lifecycle operations and emits session_updated through the
existing transactional session store path. An active turn or compaction summary keeps its current model;
the next open/reuse reconciles the desired model. Idle cached sessions reconcile
immediately. A successful native switch records the bound model; unsupported
switches dispose the cached session and reopen using the persisted native thread
and new model. Failed switches leave the desired model persisted for retry.

ACP discovery uses the normal initialize/session/new handshake with no tools or
prompt, a bounded timeout, and disposal. Explicit Models overrides bypass discovery.
Only IDs advertised by availableModels or model configOptions are returned; probe
failure leaves an available provider with an empty model list. No schema migration
is needed. Null model resets by reopening because ACP set_model requires an ID.

Verification: mock ACP new/load model RPC order, discovery formats and failure,
Kernel switching/reopen and active-turn deferral, HTTP persistence across restart,
and the full solution test gate. Review tier: L2 (persisted API contract).
