# Development and main integration

Outcome: retain main queue, recovery, workspace and Worker operations alongside
implicit Lead templates and dynamic model/reasoning selection. New conversations
have no template selector; Lead tools optionally adopt a template after creation.
Review tier: L2; Planner arranges two independent reviews.

Authority stays in Kernel actors: preserve exhaustive Allow/Deny/AskHuman decisions,
policy generation fences for template adoption, and main Worker operator/approval
checks. Host only adapts published contracts. Desired model and reasoning effort
persist independently; active turns keep captured settings, idle sessions reconcile
or reopen, and main verified resume/queue semantics remain authoritative.

Published main migrations 0001–0007 remain byte-for-byte unchanged. The only new
migration is normal sequential 0008_agent_session_reasoning_effort.sql. Fresh
databases run all eight scripts; released main databases upgrade from v7 without
rewriting receipts, queued messages, permission modes or workspace intents.
Old development database histories are outside the requested scope. The migrator
uses main's single sequential plan, integrity checks, backups and transactions.

Verification covers fresh creation and released main upgrade, injected v8 receipt
failure with rollback/retry, retained data and repeated startup. Combine Host,
frontend schemas, fixtures and OpenAPI. Exercise model selection, Worker,
queue/recovery, provider permissions and workspace Chromium flows. Run the full
.NET and frontend build/unit gate. Planner arranges independent L2 reviews and
publication after the updated candidate passes CI.
