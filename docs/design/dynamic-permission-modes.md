# Backend permission mode pass-through (#44)

Provider status owns the permission choices used by track, session, and Worker creation.
ACP status obtains those choices from a temporary live session. Config-option mode
selectors take precedence over legacy modes and retain the provider's config ID;
grouped choices are flattened without inventing mode IDs. The current choice is
the default. Selected IDs are sent through the corresponding ACP RPC on new,
load, and resume.

Kernel resolution probes lazily, using runtime caching even before bootstrap.
An advertised nonempty list is authoritative; absent choices fall back to native
static declarations. Existing explicit session choices are validated at launch.
The new-track draft stores the selected ID, clears it when the provider changes,
and sends it with the immutable create request. The frontend obtains choices
only from backend provider status.

Validation covers both ACP representations and transports, cold-start Kernel
resolution, HTTP create persistence, and the new-track draft and request flow.
Native defaults and Codex sandbox/approval mappings remain unchanged.
