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
changes are discarded because the new template initializes replacement state.
Existing plugins are retained and default plugins are merged without duplicates.
No schema migration or historical data rewrite is needed.

Verification covers MCP discovery and fences, catalog and contract inspection,
catalog/custom application, compile/init failures without state changes, actor
rehydration, and SQLite reopen plus rollback on event persistence failure. Run
the full solution gate. Review tier: L2 (persistence and governance surface).
