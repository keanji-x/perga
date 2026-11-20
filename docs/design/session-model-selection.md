# Session model selection

The persisted session Model is the desired model. TrackActor serializes model
updates with session lifecycle operations and emits session_updated through the
existing transactional session store path. An active turn or compaction summary keeps its current model;
the next open/reuse reconciles the desired model. Idle cached sessions reconcile
immediately. A successful native switch records the bound model; unsupported
switches dispose the cached session and reopen using the persisted native thread
and new model. Failed switches leave the desired model persisted for retry.

`IAgentRuntime.ProbeProvidersAsync` publishes one `ProviderStatus.ModelCatalog`
shape for all providers. Bootstrap passes it through to the picker. The model
and effort IDs are opaque to the picker. Applying a
selection always takes a `SessionConfiguration` and returns a
`SessionConfigurationOutcome` through `IAgentSession.ConfigureAsync`.

The built-in OpenCode and Antigravity catalogs use read-only provider commands.
OpenCode queries `api model.list` and `api model.default` against standalone
servers, using the returned IDs and per-model variants. Antigravity uses JSON `models` and the
headless `/model` query. Antigravity lists exact model-and-effort selectors and
reports the effective fresh CLI choice, which may be a saved preference.
Availability and default are independent. If the default query fails or names
an unlisted model, the catalog retains the list and leaves the default unknown.
Explicit model overrides take precedence. A failed listing leaves an empty
catalog and does not fall back to creating a session.

Installation queries run outside project directories. The model picker uses the
existing installation catalog. It retains the draft effort when the selected
model row is clicked again and offers a distinct Default effort. At apply time,
OpenCode selections use the IDs and values advertised by the open ACP session.
A model switch must be confirmed before the adapter reads the new model's
effort options and applies an explicit effort. Where the OpenCode ACP runtime
advertises a `default` effort, the adapter uses it to reset the same model.
The adapter confirms the selection
before a turn can run.

Approval options previously came from the discarded probe session. The session
items read exposes the options of the real open conversation when available,
without starting a provider process.
Custom ACP providers still use the bounded `initialize` and `session/new`
handshake for installation discovery.
Null model resets by reopening because ACP `session/set_model` requires an ID.

Verification: mock ACP new/load model RPC order, discovery formats and failure,
Kernel switching/reopen and active-turn deferral, HTTP persistence across restart,
and the full solution test gate. Review tier: L2 (session items API contract).
