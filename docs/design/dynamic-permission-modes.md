# Backend permission mode pass-through (#44)

Provider status owns the permission choices used by track, session, and Worker creation.
ACP status obtains those choices from a temporary live session. Config-option mode
selectors take precedence over legacy modes and retain the provider's config ID;
grouped choices are flattened without inventing mode IDs. The current choice is
the default. Selected IDs are sent through the corresponding ACP RPC on new,
load, and resume.

Kernel validates explicit choices at the request boundary. ACP validation opens
an unconfigured session in the request/track workspace when available, because
project configuration can change the catalogue. Provider status uses a temporary
session for display metadata only; its default must not be persisted as the
workspace default. Nonempty catalogues are authoritative for explicit requests.
Failed or empty ACP discovery returns a retryable `PreconditionFailed`, never an
unsupported-mode `InvalidArgument`. Callers can retry discovery; cancellation
still propagates. Native providers retain their static fallback declarations.

Omitted ACP choices remain null on track, Assistant and Worker creation and on
provider changes. The actual workspace session chooses its own default (for
example OpenCode `default_agent=plan`); null launches send no mode-selection RPC.
Codex and Claude retain their native defaults (`never` and `dontAsk`). Claude's
redundant `manual` alias is omitted; `default` is the interactive approval mode.

Persisted choices pass through unchanged on launch, resume, model updates and
compaction. Healthy cached sessions are reused without discovery, including
in-place model switching. Discovery loss cannot invalidate stored intent or
interrupt a healthy session. Actual session-open failures remain retryable through
the existing session lifecycle, without rewriting the persisted choice.

For non-worker conversations with a null choice, the frontend displays the
backend catalogue's default in the permission pill when metadata is available;
this display fallback does not write a mode to the session.
The new-track draft stores the selected ID, clears it when the provider changes,
and sends it with the immutable create request. The frontend obtains choices
only from backend provider status.

Validation covers both ACP representations and transports, workspace-specific
catalogues, discovery loss during reuse/reopen/compaction, retryable discovery
errors, null ACP defaults through creation and launch, HTTP create persistence,
and browser coverage of new-track requests and null-mode Lead/Assistant pills.
Native defaults and Codex sandbox/approval mappings remain unchanged.
