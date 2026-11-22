# Conversation assembly

`store.ts` is the shared read/operation assembly used by the Today and Track
conversation panes, including main and side panes. `contracts.ts` declares its
Scope, RouteIntent and Store interfaces. The router chooses a valid scope and
layout, then calls this one assembly; it contains no copy or re-export of it.

| Input or capability | Owner |
| --- | --- |
| Transport and unauthorized handling | app/providers and core/api |
| Scoped query facts and invalidation | QueryClient via app/providers/queries |
| Read numbering and live/history handoff | conversations/read-order and live-replies |
| Cross-mount input, drafts, local sends and accepted history | ConversationProvider |
| Outbox delivery and view projection | conversations/outbox and core/domain/conversation-outbox |
| Stop feedback and request lease | conversations/stop |
| Single action slot and command policy | core/domain/conversation-action |
| Summary/name/state projection | core/domain/conversation-summary |
| Queue revision visibility | core/domain/conversation-outbox |

Initialization and cleanup retain the existing React hook order: tab registry,
transcript/read tracking, history and run observations, model catalog and
view-local state, live handoff, stop lease, then local send state. One
mounted view observes one card. Changing scope disables/rekeys its queries and
resets view-local state under the existing hooks. Closing/unmounting does not
clear the provider's drafts, inputs or dispatched sends; late results remain
owned by the original card. Query collection, stop cleanup and live retirement
stay with their declared owners.

`history.ts` owns one transcript Query cache under the existing card key. Automatic
mount/focus/event/retry refresh reads only the latest server window. `History`
contains an explicit `latest` observation and precedence-ordered `retained` windows.
Each record keeps rows with typed coverage: a latest or exclusive-before upper
bound, and a from-row or read-to-start lower bound. Only an actual short/empty
HTTP response establishes read-to-start. Compaction combines observed coverage;
a retained window larger than the 60-row budget is not a network page or a new
exhaustion proof. Row ids are sparse and imply no continuity or server snapshot version.
New observations override edits and omissions inside their interval. Previously
loaded older rows remain readable and keep their DOM identity when they roll out
of latest. Overlapping retained windows compact without retaining duplicate snapshots.
A short latest response proves the complete current matching transcript.

The history owner exposes loaded reading rows (`historyRows` / `loadedRows`), the
actual latest-read carrier (`latestObservation` / `TranscriptReads.startOf`), and
the next exclusive-before reading position with derived pending/error state
(`earlierReadingPosition` / `earlier`). Store and live consumers never reconstruct window
layout. Loaded raw historical sources still provide positive delivery facts;
reading misses do not establish absence. Latest freshness remains an actual HTTP
read in the view's ledger, with unknown reads numbered 0. The provider maps typed
latest/before positions to the existing wire cursor, including cursor 0 only at
that transport boundary; it supplies no Infinite Query settings or continuation policy.

Explicit earlier reads use a transient mutation lifecycle with no retained result;
only the transcript Query stores rows. Reader input admits at most one page while
latest reconciliation is idle. A gap after rollover is filled before continuing
at the oldest cursor, using overlap or actual server bounds rather than assuming
contiguous row ids. An invalid cursor remains an error until explicit retry or
newest reconciliation. Earlier completion never advances the latest read fence;
an actual newer latest read, even with equal content or a remounted owner,
supersedes an older request. Earlier requests bind to their original cache snapshot;
public structural sharing preserves window/row identities while each publication
receives a new outer snapshot. This eligibility check never supplies a read number.
Admission, loading, error and result acceptance share the same current-window
snapshot/read/cursor check. Public mutation cache state serializes all observers
of that window; obsolete HTTP may finish but cannot lock or contaminate its successor.
Clear, rewind, restored-input deletion and replay boundaries reset retained windows. These invalidations
read the latest window rather than replay old cursors. Unknown older edits require
such an invalidation or an explicit read; a latest window cannot certify them.

Scoped owner change request (PR #2900): the approved cached-history-return task
requires latest-only refresh, preserved loaded bodies and correct rewrite
invalidation. `core/events/invalidation-plan.ts` therefore extends the existing
`CacheWrite` contract with `reset-query` for destructive transcript events; the
app adapter invokes public Query reset and the existing typed callers consume
the declared effect. The task's approved refresh/read-model and typed-caller
scope authorizes this narrow extension. Event wire formats, backend and persistent
storage remain unchanged. Preserve its exact-path ownership trailer in the PR
and squash body. This adds no event roster or per-test CI inventory; ordinary
new tests and file renames use existing discovery. Page-size boundary fixtures
live in the history owner and real-router switch targets.

Production contracts are exercised through `app/router/track-conversation`,
`today-conversation`, `planner-conversation`, `live-replies`, the side-pane browser
paths and `chat-performance` browser probe. Direct store consumers in tests
import the assembly here; query/route integration remains covered by the router.
The history model is verified through these production consumers and the
behavioral contracts below.

`draft-actions.ts` assembles first-message commands from a required draft
snapshot, the Registry edit/start/accept/discard port, the typed v1 create/replay port, capability booleans and open/close/onGone
signals. Construction performs no IO or Hook initialization. Dispatched attempts
retain their original scope/key/text and settle through the Registry, including
unknown-result receipt replays and refused-before-dispatch restoration. Navigation
destinations remain chosen by the route through its injected signals.

`pane-lifecycle.ts` owns the mounted pane's draft retention cleanup, selected-target
resolution, requested-open consumption, scoped Escape interruption and one-shot
side-draft auto-send. Main and side panels resolve their existing view target here
before deriving parent/child ownership and presentation gates. Registry remains
the owner of cross-mount drafts and open requests. Adoption and
open requests wait until their row belongs to this pane, inline panes do not
consume a shared open request, and auto-send consumes its intent before delivery.
Escape belongs to the focused conversation region and yields to source panels,
composer menus and native overlays. Cleanup removes only undispatched drafts.

Pane still observes capabilities and holds layout/focus. Those remaining
responsibilities are tracked in #2083; loaded DOM and streaming display work remain
under #2235.

The router uses UI's generic `useContentAdmission` only for first mounting eligible
content. It keeps the selected shell/composer available while a rendering opportunity
precedes the expensive thread. Eligibility comes from this store's existing history,
remembered transcript and status facts; it is not another history readiness or send
authorization. Read acknowledgement additionally requires that presentation boundary.
Ordinary updates after mounting are not deferred, so visible actions keep their
existing transcript and command alignment. See the
[admission contract](../../ui/content-presentation/README.md), the Chat thread README
for feature integration and the production `conversation-switch` browser regression.

History derivation and accepted-history effects depend on the registry's stable
history/remember operations and relevant rows, rather than its whole context
value. Typing still updates the one retained composer; it does not invalidate
unchanged history. Outbox operations remain authoritative for dispatch, keyed
retry, queue visibility, refusal/unknown restoration and attachments. The app
keeps local Sending feedback separate from run/activity evidence of execution.

Local sends have three states: idle (no operation), sending and failed. The exact
body, task target and idempotency key survive retries, including uncertain delivery.
A successful receipt removes the operation immediately; refetch errors cannot turn
an acknowledged write into a failed send. The session run response owns accepted
queue entries, and history owns delivered messages. No replay fences, spent-row
claims, placement memory or queue tombstones are retained in the outbox.

Recovery methods return whether the local owner took the recovery intent, never
whether the server accepted it. Only a taken action requests composer focus.
The existing parking contract stops restoration when the user moves elsewhere.
Draft adoption probes current field/parking ownership through a UI-only handle
bound to that draft key. It never requests focus from cleanup, a capability read's
completion or a late result in another view. A selected adopted draft resolves its
real row directly for both panel ownership and rendering; there is no intermediate
closed drawer or second selection state. Normalizing the target
is conditional on that draft still being selected, while Registry adoption and
the created row remain authoritative even after the user closes or switches.

A legal retry rekey reports its actual scope/key at adoption. The scope's callback
checks that identity against the current draft before probing its current UI
handle; an old attempt cannot hand focus to another draft. Initial composer submit
keeps the original void handoff into the held draft, including local refusals.
Only recovery commands consume the local-takeover boolean, so a private retry
cannot leave an extra restored copy in the next new-conversation composer.
An offline refusal keeps the submitted text in the held draft.

First-message creation uses the v1 SessionCreationReceipt path. Its required
`sessionInput` identifies the accepted message remembered at draft adoption.
Adoption and the accepted entry publish together. Unknown results retry the frozen
POST under its original key; an explicit new-conversation choice starts a new key.

The Registry exposes raw `rememberedTurnsOf` separately from display `turnsOf`.
Only raw source enters the store's history fallback and memory writes: a local
outbox projection must never feed itself back into canonical memory. Before content
admission the router selects one renderable outgoing text from the same final,
queue-filtered display and uses the existing pure `OutgoingMessage`; `ChatThread`
replaces it in the admission commit. This adds no second transcript or presentation
acknowledgement state, and grants no read receipt, source snapshot or history action.

Queue display accounting derives versioned references only from final displayed
canonical sources. A pre-receipt local send cannot claim a queue identity or suppress
a live server queue entry. Queue steer and withdraw refetch the owning run response.

SessionItemDto projects directly into typed thread entries. Speech, thinking, tool
results, outcomes, archived inputs and epoch boundaries share this projection;
provider JSON-RPC methods and params are absent. Live retirement consumes explicit
persisted identity and completion facts from the same projection.

History model verification uses the real history hook's HTTP paths, sparse overlap,
omission, short/empty response, compaction and request-lifecycle cases in
`history.test.tsx`, plus the actual read ledger in `read-order.test.ts`. Router
`live-replies`, `first-message-continuity`, `track-conversation` and
`conversation-input-response` tests exercise source handoff, local send recovery,
stop and unchanged derivation. `conversation-switch.browser.test.tsx` asserts zero
implicit earlier HTTP, latest visibility and retained DOM through real routing;
`chat-performance.browser.test.tsx` counts rows through the owner selector. These
are behavioral oracles, not mirrored reducers or a permanent test inventory.

Ordinary coverage/compaction changes now touch `history.ts` and its adjacent tests;
transport changes belong in providers and source-evidence policy changes in the
existing read/live/outbox owners. Changing the row budget still belongs to the
core constant and boundary fixtures. No window change requires a parallel array
migration in store/live, a second cache, or a new registry. Projection and DOM
costs still grow with loaded content; arbitrary unseen old edits and a complete
navigation directory remain outside the current wire contract.
