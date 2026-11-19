# L2 wake and batch review fixes

Outcome: preserve session isolation and supported-release pending mail delivery,
while making restart and empty schedule behavior observable through regressions.

Batch promotion will bind each selected message by its ordinal and owning session,
requiring exactly one update in the existing transaction. Test storage will use
session/source/message identity as SQLite does.

Unreleased migration 0014 will transfer pending mail for open tracks into the
wake ledger using JSON construction, then clear the legacy flags in the same
migration transaction. Released migrations 0001–0013 remain unchanged. Closed
track mail stays readable without scheduling a wake.

Schedule preparation will preserve the fallback only when both outputs are null.
Explicit blank output will settle that wake without a provider turn; mixed batches
will retain their nonempty segments. Expected conflicts during non-durable setup
will not create failed history; wake setup failures retain their durable identity.
Scheduler reads will avoid creating state for sessions that have no scheduler work.

Validation: two-session identical-ID storage regression, v13 upgrade and fresh-host
mail dispatch, queued and interrupted multi-message restart tests, schedule blank
output and pre-stream conflict tests, then the full .NET and frontend gates.
Review tier: L2. Operational impact: upgrade preserves outstanding legacy mail;
no released migration or public API changes.

## Upstream main integration (L2)

Replay the entire notification refactor over main and preserve migrations 0001–0013 byte for byte. The unreleased wake migration becomes 0014. Keep admission in the scheduler and runner, including an inherited admission for Worker startup and automatic compaction. Import upgrade receipts before boot activates recovered consumers. Upgrade receipt mail and its wake must commit together; the outbox owns dispatch, while receipt status and the legacy mail flag remain notification projections. Retry failed upgrade wakes only on receipt import, after the previous turn has settled. Preserve native compaction, provider approval options, and Today/Calendar storage and APIs.

Bind every queued batch member by selected ordinal with a checked update, and derive wake metadata after blank prompt suppression. Verify with focused regressions, upstream feature tests, the full .NET suite, and frontend lint/build/tests.

Receipt imports always schedule tracked background work, including when inline wakes are configured, so a blocked provider cannot hold the receipt importer mutex or delay other notifications. Recovery repairs receipt/mail projections after outbox delivery commits. Regression checks cover nonblocking receipt import, acknowledgement recovery, atomic mail/wake insertion, drain/resume scheduling, and v13 upgrade notifications.

Verification evidence: the blank-schedule/mail regression failed with `system` instead of `wake` before the metadata fix. The primary-binding failure injection bypassed `RequireOne` before the binding fix; both primary and secondary failure cases pass afterward. In an isolated exported tree, deliberately bypassing the primary member's `RequireOne` makes only the primary failure-injection case fail again. The receipt-import regression timed out before background dispatch was forced and passes afterward. Both affected browser fixtures pass (7 tests).
