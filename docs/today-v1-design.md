# Today v1 preview

Outcome: the home route resolves one daily Track per server-local calendar date,
with the normal Report and Lead conversation, compact Perga triage, and a dated
Report change feed. Supervisor and launchpad are excluded in Perga mode.

The Kernel reuses normal track creation. SQLite checks the daily mapping and
creates the hidden system Area, Track, Report, Lead and mapping in one writer
transaction. A date primary key serializes concurrent ensures; explicit date
reads never create anything. Workspace preparation follows the existing lifecycle.
The Host adapts strict date and cursor inputs and returns the existing snake-case
Today wire contract. Calendar boundaries use the server's IANA local timezone.

Migration 0012 (`0012_daily_tracks.sql`) adds the system flag, daily mapping, and nullable revision metadata
(event identity, summary, leading comment and the preceding document). Released migrations stay frozen.
New report saves record metadata with the report event in the same transaction.
Before snapshots survive pruning of the preceding revision and event rows.
Historical event payloads can identify older revisions; unavailable historical
summary/comment fields remain empty, without inventing historical facts.
Pagination is bounded by a captured event tip and uses ordered identities.
Report changes exclude system Areas. Markdown uses the Report engine renderer;
unified patches replace the entire changed body in one valid hunk.

Verification covers concurrent ensure, explicit missing and malformed dates,
system hiding, revision bodies and summaries, event boundaries and pagination,
plus Perga routing and absence of Supervisor requests in DOM/browser tests.
Review tier: L2 (forward schema and atomic identity persistence). Formal reviews
are arranged by the Planner. No data rewrite or operational migration is needed.

L2 review correction: use the existing transactional `sqlite_sequence` entry for
`events` as the committed event tip. AUTOINCREMENT advances only on committed
inserts and survives row deletion and reopening, so maintenance cannot invalidate
a captured boundary. Event replay still reads only retained event rows; no schema
or data migration is needed. Verify through TodayReportService before maintenance,
after all events are pruned, and after reopening the database, including a captured
tip beyond the newest report revision.

Changes cursors are opaque canonical Base64 encodings of ordered Track identities.
Decode and validate every supplied cursor before reading the boundary, without
assuming a Track naming convention. Test rejected empty, whitespace and malformed
cursors through the Host with an explicit through_event_id, and exercise successful
pagination through TodayReportService. Explicit null empty content suppresses the
Today report placeholder; the centered Perga view omits an empty report region.
