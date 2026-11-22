# SQLite storage decomposition

## Outcome and review tier

B2b is an L2 persistence refactor based on `ef5bae33`. Keep all public
constructors, methods, explicit port implementations and capability properties
of `SqliteTrackStore`. Only storage implementation, storage tests and this note
change. All 25 released migrations remain byte-for-byte frozen.

## Existing authority and transaction boundaries

The store owns one options object, migrator, single writer, cold blob store,
JSON upcaster, time provider, read connection string and initialization gate.
Initialization migrates before starting the writer and resolves the database
identity sidecar. Disposal drains the writer before clearing its reader pool.
Readers open independently in read-only/query-only mode; aggregate reads keep
one snapshot transaction. No collaborator may create another writer.

`SqliteSingleWriter` controls batching, transaction commit and failed-batch
replay. Each existing enqueue callback remains intact. Track creation writes
area (daily creation), track, report/revision, session, tasks, first input,
workspace intent, receipt and ordered events within that callback. Track/Flow
updates keep task/schedule changes and events together. Report commits keep the
previous revision, current report, template state, event ID and wake together.
Task settlement keeps session/items, task, messages, template state, wakes and
events together. Input delivery, message steering and session resume preserve
CAS checks, item insertion and events within their original transaction.

Cold blobs are prepared/hydrated by the existing `ColdBlobStore`; transaction
SQL and maintenance retain the existing reference handling, compression and
post-commit directory cleanup order. Upcasting remains in the same read/mapping
paths. This refactor does not change blob formats, GC policy, SQL, cancellation,
error codes or writer retry behavior.

## Composition

A private-lifetime storage context owns initialization, readers and shared
infrastructure. Domain repositories receive that context and explicit repository
dependencies. They own SQL and row mapping, with internal transaction helpers
that accept the caller's connection/transaction and never enqueue nested work.
The public store is a composition facade; it retains the original port identity
(including `SessionInputs`, `SessionMessages`, `TrackSchedules`, `TrackWakes`).

Domains: areas; track creation and workspace preparation; track reads and
aggregate snapshot; track updates and Flow commits; reports/revisions; tasks;
sessions/turns/items; session inputs; session messages; resume; asks/mail;
terminals; event ledger; overlays; maintenance/boot recovery; schedules; wakes;
Today; execution targets; read receipts; system upgrade. Shared transaction
helpers stay with their owning repository, not in a new SQL god object.

## Verification

Run storage tests while extracting, then the full `dotnet test Perga.sln` on the
final tree. Inspect public signature equivalence and method/SQL move deltas.
Add fault-injection rollback/reopen checks at cross-repository transaction
boundaries where coverage is thin. Obtain two independent read-only L2 reviews,
resolve blockers, and repeat affected checks. Verify migration hashes, allowed
paths, whitespace and a clean committed worktree. No Host/Kernel/API changes
or data migration are required.

## Implemented boundaries

All repositories are internal sealed objects with explicit constructor
injection. The context has no repository backreferences. Calls across domains
use either immutable mapping helpers or transaction-local helpers on the
injected repository; none of those helpers starts a transaction or enqueues.

| Owner | Responsibility |
| --- | --- |
| `SqliteStoreContext` | Shared infrastructure, migration/init gate, reader factory, database identity, disposal |
| `AreaRepository` | Area queries/upsert and cascade deletion |
| `CreationRepository` | Creation receipts, Area creation and workspace preparation CAS/recovery |
| `TrackCreationRepository` | One atomic initial Track aggregate including inputs, clocks, receipts and events |
| `TrackReadRepository` | Track reads and the existing aggregate snapshot transaction |
| `TrackUpdateRepository` | Track/template/data updates and Flow commit orchestration |
| `TrackDeletionRepository` | Track removal followed by blob directory cleanup |
| `ReportRepository` | Report/revision/template-state/event/wake commit |
| `TaskRepository` | Task mapping/upsert, diagnostic blobs and settlement effects |
| `SessionRepository` | Sessions, turns, items and item blob hydration |
| `SessionInputsRepository` | Admission, idempotency, dispatch CAS, settlement and recovery |
| `SessionMessagesRepository` | Queues and steering claims/settlement |
| `SessionMessageSql` | Shared transaction-only message insertion, CAS and ordinal SQL |
| `SessionResumeRepository` | Epoch CAS, archival sweep, boundary item and event |
| `CorrespondenceRepository` | Asks, mail and associated wakes |
| `TerminalRepository` | Terminal rows and reverse lookup |
| `EventRepository` | Ledger reads and same-transaction event append |
| `OverlayRepository` | Activity and notification watermark projections |
| `TrackSchedulesRepository` | Flow clocks and atomic firing |
| `TrackWakesRepository` | Coalescing, claiming, completion and recovery |
| `MaintenanceRepository` / `BootRecoveryRepository` | Retention/blob GC/checkpointing / interrupted-work reconciliation |
| `TodayRepository` | Calendar, daily identity and report-change projections |
| `ExecutionTargetsRepository` | Target persistence and reference checks |
| `ReadReceiptsRepository` | Monotonic receipts and unread markers |
| `SystemUpgradeRepository` | Upgrade receipts and notification retries |

The facade remains the object returned by the four capability properties and
still implements all original interfaces. Transaction callbacks and SQL were
moved without changing their bodies apart from receivers and helper visibility.
No attempt is made to add the removed legacy task/schedule APIs from earlier
baselines.

The root storage C# implementation changes from 18 files / 6,171 lines to
33 files / 6,953 lines. The main facade shrinks from 2,682 to 501 lines; the
lifetime context is 184 lines; 24 repositories range from 41 to 500 lines.
The line increase is explicit composition/delegation overhead, not new SQL or
behavior. The 11 former store partial files are removed. A focused `SessionMessageSql`
helper owns shared transaction-only message primitives; the full repository and
helper type dependency graph is acyclic, including static references.

## Regression evidence

`RepositoryTransactionTests` injects SQLite triggers to fail the last creation
event and a report wake after its event write. It checks rollback of earlier
rows/events, receipt/input identity, retry after failure and reopening the same
database. Existing tests cover template-event rollback, Flow clocks, session
input batches, resume sweeps, cold blobs, concurrent readers and maintenance.

`ContractSmokeTests.DisposedStoreRetainsPublicExceptionIdentity` reproduced an
extraction defect: a string passed to `ObjectDisposedException.ThrowIf` selected
the object overload and reported `System.String`. Both context guards now use
`typeof(SqliteTrackStore)`, preserving the original exception object name. The
new test failed with the expected mismatch before the fix and passes afterward.

A Roslyn-based scratch audit compared all 162 domain method bodies to baseline
(after receiver/visibility normalization) and all 109 public/explicit method
signatures (excluding the implementation-only async modifier). All matched.
The scratch audit and build tools live only in ignored test output directories.

## Validation commands and environment

Commands ran from the B2b worktree with `/home/kenji/.dotnet/dotnet` (SDK
11.0.100; the shell PATH initially did not include dotnet):

- `dotnet test tests/Perga.Storage.Tests/Perga.Storage.Tests.csproj`: initial
  92 tests passed; after regression additions and final cleanup, 95 passed.
- `dotnet test tests/Perga.Storage.Tests/Perga.Storage.Tests.csproj --no-restore --filter FullyQualifiedName~RepositoryTransactionTests`:
  both fault-injection tests passed.
- `dotnet test tests/Perga.Storage.Tests/Perga.Storage.Tests.csproj --no-restore --filter FullyQualifiedName~DisposedStoreRetainsPublicExceptionIdentity`:
  failed for the expected `System.String` object name before the fix.
- The combined lifecycle/transaction filter passed all three tests after the fix.
- Initial `dotnet test Perga.sln` failed Host startup because the shared host's
  128 inotify-instance limit was exhausted. `DOTNET_USE_POLLING_FILE_WATCHER=1`
  avoids inotify for test file watchers without changing application code.
- The polling rerun passed 375/376 Host tests; the bootstrap probe lacked the
  ignored OAR bridge bundle in this fresh worktree. A real bundled bridge from
  B2a's ignored test output was copied into
  `tests/Perga.Storage.Tests/bin/b2b-validation-bridge/`. Its source directory
  and package lock were compared with this baseline and matched. No tools
  sources, lockfiles or provider implementation were changed. The focused
  bootstrap probe passed with `PERGA_OAR_BRIDGE_SCRIPT` pointing to that bundle.
- The full solution then passed all 8 projects / 1,010 tests with those two
  environment variables. The final structural cleanup received another full
  solution run with the same environment (recorded below).
- `dotnet format style` was tried with `IDE0005` on the storage project and
  produced no changes. A scratch Roslyn compilation removed only imports
  reported as unused; all scratch files remain ignored.
- `git diff --check origin/main`, baseline migration byte comparisons, the
  repository/helper dependency graph check and an allowed-path audit are part
  of the final delivery checks. No frontend checks are applicable.

## Review and final delta

Two independent read-only L2 reviews checked all public ports and forwarding,
method/SQL equivalence against `ef5bae33`, transaction ordering, reader disposal,
writer replay and blob maintenance. Both reported no remaining blockers after
the disposed-exception fix.

The review also suggested ownership polish, now implemented: shared message
insertion/transition/ordinal/CAS helpers live in the 44-line `SessionMessageSql`;
`TodayPreviousReport` belongs to `ReportRepository`; mail acknowledgement uses
`TrackWakeRequests.SystemUpgradeMailIdPrefix` directly. This removes repository
static dependency cycles. Two independent read-only delta reviews cover these
moves. The context's `typeof(SqliteTrackStore)` reference is exception metadata,
not a facade instance or operational backreference.

The final parallel solution rerun had one intermittent failure in
`NotificationWatermarkTests.OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor(false)`
(expected one evicted actor, observed zero). That test uses
`KernelTestContext.Store`, an `InMemoryTrackStore`, with no SQLite path. Its two
branches passed on an immediate focused rerun. No Kernel code or tests were
changed. A final solution run uses `-m:1` to avoid concurrent test projects and
records the complete result below rather than treating the focused rerun as a
full-suite success.

Both independent L2 delta reviews concluded **no blockers**, including the
acyclic operational graph and exact transaction-helper/caller comparison.

Final-tree verification completed successfully:

```sh
DOTNET_USE_POLLING_FILE_WATCHER=1 \
PERGA_OAR_BRIDGE_SCRIPT="$PWD/tests/Perga.Storage.Tests/bin/b2b-validation-bridge/perga-oar-bridge.mjs" \
/home/kenji/.dotnet/dotnet test Perga.sln --no-restore -m:1
```

| Test project | Passed | Failed / skipped |
| --- | ---: | ---: |
| Perga.ReportDoc.Tests | 10 | 0 / 0 |
| Perga.PolicyVm.Tests | 127 | 0 / 0 |
| Perga.AgentKit.Tests | 23 | 0 / 0 |
| Perga.VcsKit.Tests | 27 | 0 / 0 |
| Perga.TermClient.Tests | 6 | 0 / 0 |
| Perga.Storage.Tests | 95 | 0 / 0 |
| Perga.Kernel.Tests | 346 | 0 / 0 |
| Perga.Host.IntegrationTests | 376 | 0 / 0 |
| **Total** | **1,010** | **0 / 0** |

Final migration byte audit: 25/25 unchanged. Allowed-path audit and
`git diff --check origin/main` passed. No migration, data-format, API or runtime
configuration changes are required. Test watcher/bridge environment adjustments
are validation-only; scratch files and the bridge bundle are ignored artifacts.
