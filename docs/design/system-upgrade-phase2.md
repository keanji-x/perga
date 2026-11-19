# System upgrade backend implementation

Outcome: the deployment controller can observe resident identities, drain provider
admission, activate verified web/template snapshots, and notify the initiating Lead
without keeping its MCP call open. Review tier: L2.

Kernel owns a shared admission gate around provider launches and streamed turns.
Closing admission leaves accepted inputs in their existing durable queues. Schedule
polling and task scheduling pause; reopening admission resumes pending work.

Host resolves `current` once per activation and verifies all four component hashes
before publishing one immutable web/template snapshot. Existing tracks continue to
compile their stored template source. Old static providers remain available for
hashed assets. Resident Host and bridge identities are separate from active release
identity. Management writes require a bearer capability stored in the owner-only
`runtime/system-management-token` file. perga-ctl reads it for local HTTP requests.
Remote/proxied requests require HTTPS, and Origin-bearing requests must match the
request origin. An optional configured SHA-256 verifier supports owner provisioning.

Upgrade sources are configured IDs, mapped to trusted tarball/repo/release values
outside MCP. Kernel checks persisted track and session membership on every call.
Host invokes argument arrays with a minimal environment. Apply uses the controller's
transient service handoff; rollback runs inside its own transient service.

Migration 0011 records job IDs and validated receipt JSON. Receipt import and a
system mail wake are one SQLite transaction. The existing durable mail wake port
provides restart recovery and acknowledgement; a missing/closed recipient remains
recorded for administrative inspection. Boot imports after interrupted-attempt
reconciliation. A poller imports late receipts and retries pending system wakes
without awaiting provider execution during startup.

Validation: focused admission, authorization, snapshot and receipt tests, Host
endpoint integration tests, then the full solution and perga-ctl suites.

The bridge protocol currently has no build identity response. Host reports an
explicitly configured resident bridge ID, otherwise `unknown`, and never substitutes
the active manifest's bridge hash after a Host-only restart. Rollback handoff IDs
identify the transient launcher; the controller generates the terminal receipt job ID.

Checks completed during implementation: controlled Host flows; SQLite suite;
frontend core type-check and focused ESLint; 35 isolated perga-ctl tests. An isolated
mutation that bypassed Drain admission failed both admission tests; restoring the
production source passed both. The full task gate supplies delivery verification.

L2 reconciliation: idle model updates persist their configuration first and use a
nonblocking admission lease for immediate provider reconciliation. During Drain,
they defer reconciliation to the next admitted turn, keeping the actor responsive.
Receipt recovery awaits a completion signal carried with the individual mail wake,
including when queued behind a busy Lead. The importer's background dispatcher
releases its in-memory guard in a finally block after that signal settles. Durable
wake_pending remains the retry authority after an unacknowledged provider failure.
Regression checks exercise cached model changes during Drain, an IOException before
the first receipt-wake event followed by a retry, and fractional receipt timestamps.
