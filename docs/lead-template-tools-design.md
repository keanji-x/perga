# Implicit Lead template tools

Outcome: unbound governance sessions can discover, inspect, and apply a catalog
or authored template to an existing open track. Templates remain optional.

The catalog remains the authority for compiled manifests and snapshot compilation.
The Kernel serves the authoring contract from the configured templates directory,
with the repository contract embedded as a fallback. Application resolves and
initializes a candidate policy before any actor mutation. SQLite commits the new
snapshot, track fields, and track.updated event in one writer transaction. Only
then does the actor adopt the candidate policy and publish the persisted event.
The existing policy still authorizes template application, but its guard state
changes are discarded only for Allow because the new template initializes replacement
state. Deny and AskHuman retain guard state changes. Each guard captures the actor
policy epoch before evaluation; template application requires that epoch to still
match inside the mailbox. Hydration and successful policy adoption advance the
epoch, so human approval cannot authorize a replacement under a superseded policy.
The session bridge receives merged plugins after persisted policy adoption. Schedule
resolution prefers the persisted snapshot over catalog entries with the same ID.
Existing plugins are retained and default plugins are merged without duplicates.
No schema migration or historical data rewrite is needed.

Verification covers MCP discovery and fences, catalog and contract inspection,
catalog/custom application, compile/init failures without state changes, actor
rehydration, and SQLite reopen plus rollback on event persistence failure. Run
the affected Kernel, Storage, Host integration, PolicyVm, and ReportDoc test suites.
Review tier: L2 (persistence and governance surface).

L2 review fixes are verified with runtime AskHuman/policy-swap and stale epoch
regressions, denied guard state persistence, plugin addition/deduplication and
session bridge visibility, and scheduler snapshot/catalog ID clash coverage.
No migration or operational changes are required.
