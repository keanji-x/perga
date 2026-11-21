# MCP provider discovery and launch validation

Stage 1 adds provider discovery, now exposed as `provider_list`, and validates launch settings before session creation or configuration updates are persisted. Discovery reads the bootstrap provider cache and prefers an open Track session's ACP permission catalog when one is available.

The Kernel validates the selected provider and explicit launch options against runtime metadata. Creation checks for an existing receipt before probing and checks again before writing. Session mutations resolve defaults, recheck caller authority, and validate against the captured provider catalog on the Track mailbox. Provider probes for session mutations run outside that mailbox. The Worker path admits the task through the existing Report policy and transaction. A replay returns the durable receipt immediately and can wake scheduling for a pending Worker in the background. The scheduler leaves an eligible Worker pending if its provider is temporarily unavailable.

Cross-provider children and updates clear settings inherited from the old provider. Persisted configuration and existing receipt shapes require no migration.

Verify through MCP and Kernel tests covering catalog filtering, launch errors, persisted configuration, concurrent mailbox access, and idempotency.

With SessionRuntime, discovery observes a session's owned handle only when its provider matches the stored selection and ownership is stable. Configuration updates validate before persistence and defer same-provider rebinding to the next turn or compaction. Provider switches use SessionRuntime's idle close path. Creation rechecks its receipt and caller authority after probing and hands accepted input to the existing runtime dispatch path.

## Stage 2 MCP tool naming

The `perga` and `vcs` servers declare and dispatch unprefixed tool names such as `provider_list`, `report_read`, `status` and `pr_merge`. The server name provides the client namespace.

New frozen templates store the tool naming version in the existing snapshot field. Compilation applies the version of each effective hook's source, so inherited hooks can retain their own naming. Existing unversioned snapshots retain prefixed names regardless of how the JavaScript builds them. Template inspection still returns source. Stored conversation items retain their original names and the frontend recognizes them when rendering history. This requires no database or HTTP API schema change.

Inline `track_set` authoring defaults to canonical names. Unmarked catalog files and unversioned snapshots default to legacy names. A JavaScript or JSON template can declare `toolNameVersion: 1` for legacy names or `toolNameVersion: 2` for canonical names. Each inherited hook keeps the version of the module that defines it.

Use `.perga.js` or `.perga.json` for a template in the Host's configured catalog directory.

For legacy policies, MCP `template_read` returns versioned `jsSource` or `snapshot` for reapplication and `editableSource` for reading and editing. HTTP template inspection returns the underlying source.

An older Host cannot load a snapshot written by Stage 2. Those tracks fail closed on downgrade. Use the pre-upgrade database checkpoint if the downgrade must run those tracks on the earlier Host.

Verify canonical dispatch and rejection of legacy wire names through MCP, and verify guard decisions and after events using a rehydrated template snapshot.

## Stage 3 MCP verbs

The Perga catalog uses `list`, `read`, and `cancel` consistently. Legacy hooks still receive their original prefixed spellings, including `perga_mail_cat` for `mail_read`. Templates from the Stage 2 PR preview should use the final verbs and replace the preview comment with `toolNameVersion: 2` before use. Verify the wire catalog and both hook phases with a frozen policy, and check that historical task-list activity retains its conversation label.

Session creation trims explicit model IDs and rejects blank ones. For a declared Worker model, admission checks the model ID prepared for launch.
