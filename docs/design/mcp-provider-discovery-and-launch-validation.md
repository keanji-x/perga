# MCP provider discovery and launch validation

Stage 1 adds `perga_provider_ls` and validates launch settings before session creation or configuration updates are persisted. Discovery reads the bootstrap provider cache and prefers an open Track session's ACP permission catalog when one is available.

The Kernel validates the selected provider and explicit launch options against runtime metadata. Creation checks for an existing receipt before probing and checks again before writing. Session mutations resolve defaults, recheck caller authority, and validate against the captured provider catalog on the Track mailbox. Provider probes for session mutations run outside that mailbox. The Worker path admits the task through the existing Report policy and transaction. A replay returns the durable receipt immediately and can wake scheduling for a pending Worker in the background. The scheduler leaves an eligible Worker pending if its provider is temporarily unavailable.

Cross-provider children and updates clear settings inherited from the old provider. Persisted configuration and existing receipt shapes require no migration.

Verify through MCP and Kernel tests covering catalog filtering, launch errors, persisted configuration, concurrent mailbox access, and idempotency.

With SessionRuntime, discovery observes a session's owned handle only when its provider matches the stored selection and ownership is stable. Configuration updates validate before persistence and defer same-provider rebinding to the next turn or compaction. Provider switches use SessionRuntime's idle close path. Creation rechecks its receipt and caller authority after probing and hands accepted input to the existing runtime dispatch path.
