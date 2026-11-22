# P2a: Provider and query composition

Baseline: `c5f3139` (P0 #119 and P1 #124). Review tier: L2.

Split app/providers/queries.ts into owning modules for operation execution,
conversation reads, planner mutations, track conversation mutations, workspace
reads, area mutations, track reads and writes, report reads, model catalogs,
execution targets and bootstrap. Callers import owners directly; no forwarding
barrel is retained. Existing directory inventory entries already cover these files.

Query keys remain byte-for-byte compatible and belong to the app queryKeys
factory (resource addresses continue to use core factories). Event invalidation
compares factory prefixes. Provider catalogs in New Track follow managed backend
registrations for the selected target; availability remains a separate read.
Provider presentation metadata belongs to core/domain, below both settings and chat.

Planner mutations use TanStack useMutation with networkMode always and retry false.
Admission remains at the call boundary, before serial queues. Already admitted send
transports remain authoritative: do not reacquire a recovery lease later. Model
writes keep card serialization; permission writes keep registry serialization across
remounts and guard ACK publication against newer reads. Refetch reconciliation must
not reject an acknowledged write or keep a send lease held. Track writes split by
lifecycle, tools and human input/report actions; narrow callers mount only their hooks.

PR #118 invariant: draft identity, confirmed Track ID and first-message storage are
unchanged. PR #120 invariant: managed/availability/catalog reads include the selected
execution target with existing local-target normalization. Late responses from one
target cannot populate another target's catalog. Server API contracts do not change.

Host exposes no /api/auth/logout. Remove that request and keep local credential,
query and cursor clearing plus reload through the existing recovery lifecycle.

Validation: focused planner permission/recovery and query contract tests while
iterating; final lint, unit tests, build and full browser suite, including Track reload
and area creation. Two independent reviews check cancellation, retries, admission,
cache keys, target isolation, dynamic provider membership and persistence.
