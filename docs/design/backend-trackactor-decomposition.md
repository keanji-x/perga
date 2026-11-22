# TrackActor collaborator decomposition

## Outcome and baseline

B2a preserves every TrackActor signature and scheduling/persistence behavior while
moving domain state and its transitions into private nested collaborators, following
SessionTurnScheduler and SessionTurnRunner. Baseline 15ec7d73: TrackActor.cs 3,491
lines; 22 TrackActor partial source files, 6,839 lines (7,026 including TrackActorRegistry).
This is an L2 lifecycle and orchestration refactor; two independent reviews and
`dotnet test Perga.sln` are required on the final tree.

## State inventory and boundaries

- Actor: hydrated/closing/deleting flags, Track DTO and execution target cache,
  mailbox/channel, shutdown CTS, disposal synchronization/task, background jobs,
  activity/external-operation counters. Session DTOs, turn indices, runtime
  registry, scheduler/runner, approval admission and workspace preparation remain
  with their existing lifecycle owners.
- TerminalLifecycle: terminal dictionary, hydration, ordered snapshots,
  open/idempotency, observe/input/resize/close and deletion teardown.
- TrackInteractions: asks and pending waiter dictionaries, hydration, creation,
  answering, deletion cancellation and cross-Track mail delivery/read/wake ordering.
- TrackReport: current report document, report reads/commits/block changes,
  authorization revalidation and transactional report/task synchronization.
- TrackTasks: task list and active run dictionary/count, run admission, scheduling,
  cancellation, verification, settlement and session/task fences. Other domains
  obtain snapshots or explicit queries, never mutable collection references.
- Workspace/policy compilation remains in the Actor for this pass: template
  generation, mounted workspace and preparation are coupled to approval admission,
  launch and teardown. Do not extract a writable property bag to pretend isolation.

Nested collaborators can call Actor lifecycle services without widening their
visibility to other Kernel components. Their own mutable fields are private.
Public Actor operations remain thin delegates. Session launch/compaction and
Track lifecycle orchestration are grouped into focused existing-style Actor
files where they still coordinate multiple owners; these file moves are distinct
from the actual state ownership extraction.

## Concurrency and ordering invariants

There is one channel reader, not a fixed OS thread. Async continuations may change
threads; domain writes occur only within an admitted mailbox operation. No new
locks, queues, Task.Run calls or nested mailbox waits are introduced. Existing
external work phases remain outside the mailbox, including provider shutdown,
gates, terminal I/O and background command execution. Their completion phases
re-enter the same mailbox and retain identity/generation checks.

Hydration retains its existing order and retry behavior. A collaborator must not
mark the Actor hydrated early. Report SaveReportAndTasks remains the transaction
boundary; state replacement and event publication keep their original order.
Ask answers commit before consuming/completing waiters, including publication
failure. Terminal open/close retain the existing memory/store ordering. Track
deletion cancels turns, cancels ask waiters, confirms task shutdown, kills
terminals, revokes MCP, closes runtimes, unmounts, deletes and publishes in the
same sequence. Dispose still closes admission/channel, cancels shutdown, drains
the mailbox, disposes runtimes and finally disposes the shutdown CTS.

## Verification

Use existing Kernel tests for task gates/retry/recovery, report conflicts,
approvals, mail, terminal mounting and deletion. Add focused observable regression
cases for collaborator hydration, concurrent terminal idempotency, interaction
settlement and teardown. Run the full solution, review both public signatures and
the moved bodies, and check path scope and whitespace before committing.

## Implemented structure and review

TrackActor.cs is now 525 lines (down 2,966, approximately 85%). The 22 original
TrackActor source files became 17 orchestration files plus four collaborator
files. The collaborator files use the existing nested-class/partial shell pattern;
the shell does not own their state. Six tiny fragments were merged into their
session/approval owners and one focused Track lifecycle file was introduced.
Including the four collaborators, the Actor implementation grew from 6,839 to
7,119 lines, primarily due to explicit delegates, owner-qualified calls, snapshots
and file headers. This is an ownership refactor, not a line deletion exercise.

| Collaborator | Lines | Private state / behavior |
| --- | ---: | --- |
| TerminalLifecycle | 262 | Terminal identities; hydration, lifecycle I/O and teardown |
| TrackInteractions | 296 | Ask records and waiter identity; ask/mail lifecycle |
| TrackReport | 502 | Report document; authorization, commits and task synchronization |
| TrackTasks | 962 | Declared tasks, active runs/count, pause/settlement transitions |

TrackTasks remains a cohesive run-lifecycle coordinator above the 800-line prompt:
its verification, cancel and settlement phases share run identity and must preserve
the existing external-work/mailbox reservation boundary. Splitting those phases
solely by line count would obscure that protocol. It is substantially smaller than
the former 3,491-line mixed Actor. Workspace/policy and approval admission remain
Actor orchestration in this pass; their authority/generation transitions were not
replaced with writable aliases or state bags.

Two independent, read-only L2 reviews found no introduced blockers:

- `ses_0f87bd1e01cf4dd09f420846bf751ec8`: moved-body comparison, 72 public/internal
  Actor signatures, mailbox concurrency, task identity/run fences, lifecycle order.
- `ses_362e49392f764eb18f9e9f9e90b42f48`: report/task transactions, interaction
  persistence/waiters, terminal identity, hydration, cancellation and callers.

Both checked `git diff --check`; neither ran competing builds or changed files.
The unchanged baseline behavior of terminal open/delete I/O inside mailbox
operations was explicitly distinguished from any introduced issue.

Four new production-entry regression tests use controlled fakes: 32 concurrent
terminal opens and cold rehydration; terminal close persistence/NotFound;
Track deletion canceling ask waiters while preserving another Track's terminal;
and two concurrent answers settling exactly one waiter after persistence.

## Executed validation (2026-11-22)

All commands ran from the dedicated `b2a-trackactor-split` worktree. The SDK is
`/home/kenji/.dotnet/dotnet` 11.0.100; it was not initially on PATH.

| Command (SDK executable abbreviated as dotnet) | Result |
| --- | --- |
| `dotnet build src/Perga.Kernel/Perga.Kernel.csproj --nologo` | Initial extraction exposed four compile errors, subsequently corrected |
| `dotnet build src/Perga.Kernel/Perga.Kernel.csproj --no-restore --nologo` | Passed, zero warnings/errors |
| `dotnet test tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj --nologo` | Original 398 tests passed |
| `dotnet test tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj --filter FullyQualifiedName~TrackCollaboratorTests --nologo` | Initial xUnit2031 assertion-style diagnostic corrected; all four new tests passed |
| `dotnet format style src/Perga.Kernel/Perga.Kernel.csproj --no-restore --diagnostics IDE0005 --include src/Perga.Kernel/Actors --verbosity minimal` | Completed, no source changes |
| Same format command with `--severity info --include src/Perga.Kernel/Actors/*.cs` | Completed, no source changes |
| `dotnet test Perga.sln --nologo` | Initial run: missing OAR bridge in Host smoke test, two Jint compilation timeouts under concurrent solution/Wave 1 load; no timeout policy changed |
| Final full solution command below | **1,009 passed, zero failed/skipped**, including 402 Kernel, 98 Storage, 315 Host integration tests |
| `git diff --check origin/main` and staged diff check | Passed |
| Staged changed-path allowlist check | Passed; all changes within the three authorized path groups |

The missing bridge was built into ignored **test output**, without writing to
`tools/**` or changing dependency manifests. Reproduction of environment setup:

```sh
npm install --prefix tests/Perga.Kernel.Tests/bin/b2a-validation-bridge \
  --no-save --no-package-lock esbuild@0.25.2 @botiverse/oar@0.124.0
NODE_PATH="$PWD/tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/node_modules" \
  tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/node_modules/.bin/esbuild \
  tools/perga-oar-bridge/src/index.ts --bundle --platform=node --format=esm \
  --target=node24 \
  --outfile=tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/perga-oar-bridge.mjs \
  --external:@cursor/sdk --external:@earendil-works/pi-durable \
  --external:@earendil-works/chord \
  '--banner:js=import { createRequire as __pergaCreateRequire } from "node:module"; const require = __pergaCreateRequire(import.meta.url);'
PERGA_OAR_BRIDGE_SCRIPT="$PWD/tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/perga-oar-bridge.mjs" \
  /home/kenji/.dotnet/dotnet test Perga.sln --no-restore --nologo -m:1
```

No database migration, external contract, deployment or operational change is
required. No frontend, Host, storage, library or tools source files were changed.

## PR #122 rebase integration

The original implementation and validation above describe `35c8043f` on
`15ec7d73`. PR #132 was first rebased onto **`3b4bc5a8` (Unified Track Flow v1,
PR #122)**. This section supersedes the original line counts and validation for
the rebased patch. The rebase has a behavioral integration delta and requires
fresh L2 review against both bases plus a new full solution run.

The six conflicts were resolved by applying the same ownership extraction to the
complete upstream Actor implementation, not by discarding upstream method bodies.
`TrackActor.Overlays.cs` and `TrackActor.SessionProjection.cs` are still folded
into SessionInterop, including their new active-lease lookup behavior. The
TrackTasks owner now contains `TryGetActiveOwningTaskCore`, outcome/summary
settlement, Flow-aware scheduling and interpolation, active command execution
handles and cancellation joins. Explicit register/remove/query methods let Flow
wake runners update active leases without exposing the run dictionary/count.

Preserved upstream integration points:

- Flow file hydration/projection/revision checks, dirty-draft hash fences and GC.
- `EvaluateFlowAfterTriggersCoreAsync`, settlement hooks, outcome event fields,
  watch workspace leases, canceled-result fences and default-wake suppression.
- NativeThreadId/epoch session reuse, active-versus-historical ownership,
  queued Flow session rescheduling and force-reopen after MCP token revocation.
- Flow views/kernel facts, `task_verifying`, overlay/session projections,
  read-only Flow discovery and dry-run MCP authorization.
- Active-watch activity checks and cancellation before runtime disposal.

Flow orchestration/watch state remains in its upstream Actor owners. Report,
interaction and terminal owners retain the original refactor's private state.
The main Actor is now **534 lines**; TrackTasks is **1,188 lines**, reflecting
upstream's additional scheduling/lease protocol. No extra task/Flow state bag was
introduced to bypass the collaborators. The B2a patch leaves upstream Flow tests, Host code,
storage/contracts/schema/migrations and non-Actor implementation unchanged;
subsequent unrelated main updates are inherited as described below.

The focused Kernel run selecting TrackFlowTests and TrackCollaboratorTests passed
**29/29**. Dual-base comparison used
`git range-diff 15ec7d73..35c8043f 3b4bc5a8..e680fdcc` and normalized moved-body
comparisons against upstream. All 92 top-level public/internal Actor-directory
method/constructor signatures matched upstream after ignoring async/whitespace.

Two new independent read-only L2 delta reviews found **no introduced blockers**:

- `ses_dd8fcb4657c944af81cb8f38df972068`: all six conflicts, active leases,
  watch/command cancellation joins, session reuse, runtime reopening, queue
  rescheduling and lifecycle order.
- `ses_e7a6db586a444985b98fb86490010a88`: Flow persistence/revision/hash/GC,
  report/task synchronization, outcome hooks, authorization, projections and
  register/remove adapters.

Both checked the range-diff and method bodies, confirmed both Flow test files
byte-identical to upstream and passed `git diff --check 3b4bc5a8`. Neither changed
files nor ran competing builds/tests. Final solution results follow below.

### Final upstream synchronization and validation

The full solution run on the PR #122 baseline passed **1,036/1,036** tests
(427 Kernel, 98 Storage, 317 Host integration, plus the five library suites),
including both upstream Flow suites. The 29-case focused selection above consists
of 25 upstream Flow cases plus the four original collaborator regressions.

While that run was in progress, main advanced to **`5faac7c6` (PR #131 Host
origin guard)**. Its files do not overlap the B2a patch. A second mechanical
`git rebase --autostash 5faac7c6` retained the documentation update and moved the
single commit onto that base. The following comparison passed byte-for-byte:

```sh
git diff 3b4bc5a8 e680fdcc -- src/Perga.Kernel/Actors tests/Perga.Kernel.Tests > reviewed.patch
git diff 5faac7c6 HEAD -- src/Perga.Kernel/Actors tests/Perga.Kernel.Tests > current.patch
cmp reviewed.patch current.patch
```

The comparison artifacts were kept outside the repository and are not committed.
The two L2 reviews therefore apply to the identical production/test patch on the
final base. PR #131 code is inherited from main, with no B2a edits to Host/tools
or its tests. A fresh full solution run on this final base uses:

```sh
PERGA_OAR_BRIDGE_SCRIPT="$PWD/tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/perga-oar-bridge.mjs" \
  /home/kenji/.dotnet/dotnet test Perga.sln --nologo -m:1
```

Final-base result: **1,093 passed, zero failed/skipped, eight test assemblies**,
including **427 Kernel**, **98 Storage** and **374 Host integration** tests.
Both TrackFlowTests and TrackFlowIntegrationTests are included without filters in
this full run and remain byte-identical to PR #122. The additional Host cases
come from inherited PR #131. No validation timeout policy or test assertions were
weakened. The final `git diff --check origin/main` and commit path allowlist checks
passed; delivery is one English commit on `5faac7c6`, with no push.

Commands additionally executed for this rebase:

- `git fetch origin main`, `git rebase origin/main`, explicit six-site conflict
  resolution and `GIT_EDITOR=true git rebase --continue`.
- Focused Kernel tests: `dotnet test tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj --filter 'FullyQualifiedName~TrackFlowTests|FullyQualifiedName~TrackCollaboratorTests' --nologo` (29 passed).
- `git range-diff 15ec7d73..35c8043f 3b4bc5a8..e680fdcc`, source signature
  comparison (92 unchanged), and unchanged upstream Flow-test blob checks.
- Full `dotnet test Perga.sln --nologo -m:1` on `3b4bc5a8` (1,036 passed),
  using the previously prepared test-output OAR bridge environment variable.
- `git rebase --autostash 5faac7c6`, exact production/test patch comparison, and
  the full command above on the final base (1,093 passed).
- Whitespace, path scope, single-commit and clean working-tree verification.

The first compile during re-extraction identified the expected unresolved Flow
wake dictionary/count accesses and shared output-tail constant; these were
replaced with explicit TrackTasks operations before the focused and full runs.
No changes outside the authorized B2a source/test/document paths were authored.

## CI overlay/idle-eviction investigation

PR #132 at `ba9e1a25` reported one failure in
`OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor(replace: false)`:
expected one evicted Actor, observed zero. Investigation preceded any CI retry.

### Attribution and causal window

`TrackActorRegistry`, the notification test and CreateDefaultTrackAsync fixture
were byte-identical between `5faac7c6` and `ba9e1a25`. The EnqueueAsync body,
LastActivityAtMs/TouchActivity, WaitForBackgroundWorkAsync and TrackBackgroundJob
were unchanged. HasActiveWork retains the same conditions; its task-count read
is only delegated to TrackTasks' identical volatile read. This fixture creates no
Flow nodes/runs/watches. Its asynchronous startup work is workspace preparation;
the Flow file hydration/projection path does not dispatch an empty Flow.

A request result is published with `tcs.TrySetResult` **before** the mailbox
finally calls TouchActivity and decrements `_queuedMailboxOps`. Waiting for an
overlay result therefore does not prove the Actor is idle. The failed test then
advances its manual clock two minutes and expects unconditional eviction. A late
mailbox epilogue can legitimately cause the registry to observe either:

1. HasActiveWork=true while the queued operation is still finalizing; or
2. A recently updated LastActivityAtMs after the test's time jump.

Background-job cleanup similarly removes the tracked job before its final
TouchActivity; awaiting only WaitForBackgroundWorkAsync does not fence the
mailbox epilogue. Eviction correctly returns zero in these situations. The CI
log did not capture its fields, so it cannot distinguish which skip branch fired.
This is an existing test-precondition race, not evidence of a B2a activity/lease
regression or a Flow test being lost during rebase.

### Comparative evidence

- Original `ba9e1a25`: 50 fresh filtered test processes, 100 cases, all passed.
- Exact `5faac7c6` baseline: 50 fresh filtered processes, 100 cases, all passed.
- Both runs used CPU affinity 0,1 and DOTNET_PROCESSOR_COUNT=2. Uncontrolled
  repetition did **not** reproduce the natural scheduling failure.
- A controlled TimeProvider gate paused the next TouchActivity in a mailbox
  finally after its result was published. Advancing the clock and retaining the
  original expected-1 assertion produced the **same expected 1 / actual 0 failure
  on both revisions**, with HasActiveWork asserted true. Baseline Actor/registry,
  original test and fixture source were checked byte-for-byte before the probe.
- Baseline comparison used an exact git archive under ignored test output;
  templates and schema embedded resources were included before counting valid
  runs. Initial missing-resource build/launch attempts are not test evidence.
  Diagnostic probes and their logs remain uncommitted.

The unmodified baseline production code also passes the repaired test. In that
isolated baseline copy, deleting just the new helper's HasActiveWork wait made the
controlled regression fail at Assert.False(quiescence.IsCompleted); restoring the
wait passed all three targeted cases. This demonstrates why waiting for background
jobs alone is insufficient. No production files were mutated for this check.

### Focused test repair

The overlay concurrency test now drains tracked startup jobs and waits, with a
bounded cancellation token, for the Actor's public HasActiveWork flag to clear.
It keeps manual time fixed and explicitly invokes a zero-age eviction sweep.
This isolates the intended disposed-Actor lookup/fallback race from idle-age
measurement; it retains the one-eviction, two-lookups, stale-store/new-Actor and
watermark assertions. The separate ten-minute idle-timeout test is unchanged.

The new `ActorIdleEvictionTests` case holds mailbox finalization behind a targeted
clock gate: eviction must return zero and quiescence must remain incomplete while
that finalization is held; releasing it must allow one eviction. The gate is
armed for the synchronous callback's managed thread so unrelated background time
reads cannot steal it. There are no production changes relative to `ba9e1a25`.

The focused notification/Actor-hydration/eviction run passed **18/18**. Post-fix
stress, independent delta reviews and full-suite results follow below.

Post-fix pressure result: **50 fresh processes / 150 cases passed**, zero failed
rounds. Each process selected both overlay replacement variants plus the new
controlled finalization regression, under the same two-CPU affinity and runtime
processor-count setting as the original/baseline comparison. The restored fix
also passed all three targeted cases on unchanged baseline production.

Reproduction commands (SDK `/home/kenji/.dotnet/dotnet`, worktree root):

```sh
# Repeat 50 times separately for ba9e1a25 and an exact 5faac7c6 archive.
DOTNET_PROCESSOR_COUNT=2 taskset -c 0,1 /home/kenji/.dotnet/dotnet test \
  tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj --no-build --no-restore --nologo \
  --filter FullyQualifiedName~OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor

# After the test repair, repeat 50 times (three cases/process).
DOTNET_PROCESSOR_COUNT=2 taskset -c 0,1 /home/kenji/.dotnet/dotnet test \
  tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj --no-build --no-restore --nologo \
  --filter 'FullyQualifiedName~OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor|FullyQualifiedName~IdleSweepWaitsForMailboxFinalization'

# Affected consumers and the separate idle-age policy test: 18 passed.
/home/kenji/.dotnet/dotnet test tests/Perga.Kernel.Tests/Perga.Kernel.Tests.csproj \
  --filter 'FullyQualifiedName~NotificationWatermarkTests|FullyQualifiedName~ActorIdleEvictionTests|FullyQualifiedName~TrackActorAndHydrationTests' --nologo
```

Per-round logs and controlled expected-failure/mutation logs are outside the repo;
the baseline archive and temporary probes are under ignored test output. Neither
is part of the delivered patch. The only fix files are the notification test,
its fixture synchronization helper, the new controlled regression and this record.

Two independent read-only reviews of the CI repair found **no introduced
blockers**:

- `ses_7c5be02d03494384944220a83f82b429`: verified baseline attribution, equivalent
  activity conditions, synchronization precondition and retained assertions.
- `ses_5e0796530a3c43d590120163c0622b90`: verified low-core gate targeting,
  deadline/release ordering, zero-age isolation and unchanged production semantics.

Both inspected the baseline/head source and saved original stress/controlled
failure evidence, and passed `git diff --check ba9e1a25`. They correctly noted that
the helper is not a general guarantee of all cleanup continuations finishing;
fixed time and zero-age eviction make that final timestamp-only window irrelevant
in this specific test. Neither reviewer ran builds/tests or modified the tree.

Final CI-repair validation: **1,094 passed, zero failed/skipped** across eight
assemblies, including **428 Kernel**, **98 Storage** and **374 Host integration**
tests. This used the final source tree and the same full, unfiltered
`PERGA_OAR_BRIDGE_SCRIPT=... /home/kenji/.dotnet/dotnet test Perga.sln --nologo -m:1`
command documented above. Production source is byte-unchanged from `ba9e1a25`.
Whitespace, authored-path scope and clean-tree checks passed before delivery;
the test-only repair is amended into the single B2a commit, with no push or CI
rerun performed by this worker.

## Final mechanical rebase onto `6417308d`

After the CI repair at `97f70682` (base `5faac7c6`), main incorporated PR #133
(remote SSH execution), PR #129 (OAR runtime activity) and PR #134 (frontend P3).
`git fetch origin main` resolved main to **`6417308d`**. Rebase had exactly one
conflicted file, TrackActor.cs, because the upstream session-read method had been
moved to TrackActor.SessionOperations.cs by B2a.

The resolution retains the extracted Actor main file and adds precisely the two
upstream SessionItemsPageDto arguments to the owning ReadSessionItemsAsync body:

```cs
ReadRuntimeActivityCore(session)?.Status,
ReadRuntimeActivityCore(session)?.Tasks.Values.ToArray()
```

`git range-diff 5faac7c6..97f70682 6417308d..HEAD` showed only these arguments
moving with the method from TrackActor.cs to SessionOperations. Comparing every
previously authored B2a file with `97f70682` found only SessionOperations changed,
before updating this document. In particular, the CI-race repair and all four
collaborators are unchanged. The upstream RuntimeActivity, SessionRuntime,
ContextUsage, SessionRuntimeActivityTests and KernelTestHarness files were
separately checked byte-identical to `6417308d`; no upstream events, task merging,
post-turn activity, epoch/reset fences or tests were discarded.

This is a mechanical move of an already implemented upstream projection, with
no new behavioral delta requiring another L2 review. The prior independent L2
reviews remain applicable. Fresh full-solution validation is required and its
result is recorded below. No additional production adaptation or scope expansion
was introduced, and delivery remains one English commit without a push.

Final `6417308d` validation: **1,109 passed, zero failed/skipped**, eight test
assemblies. Counts include **429 Kernel** (the new runtime activity test plus
ActorIdleEvictionTests and NotificationWatermarkTests), **23 AgentKit**, **98
Storage** and **386 Host integration** tests. The other library suites passed
unchanged. The full command was
`PERGA_OAR_BRIDGE_SCRIPT=... /home/kenji/.dotnet/dotnet test Perga.sln --nologo -m:1`.

Final whitespace/path-scope checks passed. Against `97f70682`, the only authored
file changes are the exact upstream DTO argument adaptation and this validation
record. The working tree is clean after amending; branch delivery is one English
commit on `6417308d`, with no push.

## PR #136 retirement rebase onto `ef5bae33`

The next rebase starts with green `bffc6535` on `6417308d` and targets
**`ef5bae33`**, which retires legacy task/schedule/human-report-edit entry points
and migrates template declarations to Flow. This is a behavioral integration
delta requiring two new independent L2 reviews, not a mechanical conflict choice.

The four conflicts were resolved against complete upstream method bodies and the
existing B2a ownership boundaries. No compatibility wrappers were added:

- WorkerDispatch.cs stays deleted. CreateSessionAsync's worker-dispatch entry,
  WorkerDispatchIntent, dispatch replay/preallocation and report task-admission
  helpers are absent.
- UpdateReportBlockByHumanAsync, AcceptTaskAsync, RejectTaskAsync and the old
  CommitReportInternalAsync are removed from both Actor facade and collaborators.
- TrackReport owns the simplified supported session report commit and
  PreviewReportCommitCore. It retains upstream approval-generation revalidation
  and the guard against new/modified task report blocks. Finalization calls
  SaveReportAsync; it no longer synchronizes task declarations or emits the old
  report-driven task/dispatch effects.
- TrackTasks retains the supported Flow cancellation/completion/scheduler paths.
  Flow cancellation follows the authoritative Flow spec update, joins cleanup
  outside the mailbox, and preserves the legacy persisted-task cleanup case
  explicitly retained by upstream.
- TemplateFlow seed preview, known/archived ID preservation, atomic template/Flow
  update, input validation, active-clock text refresh and model normalization
  are retained in the owning template/Flow orchestration and task launch paths.
- Flow wake runs now retain their Execution handle, as upstream requires for
  cancellation joins; FlowSyntax duration/default handling is retained.

The main Actor is **517 lines**, TrackReport **261**, TrackTasks **1,123**.
The terminal and interaction collaborators retain their previous ownership.
Mailbox admission, activity bookkeeping and Dispose order are unchanged from the
reviewed B2a patch; template/Flow/report/task effects follow the new upstream
ordering. This section supersedes historical descriptions of now-retired APIs
and report/task synchronization earlier in this document.

Checks before L2 review:

- Kernel project builds and the full Kernel test project passed **351/351**.
  Intermediate extraction/layout compile errors were corrected before that run.
- All **89** top-level Actor-directory public/internal method/constructor
  signatures match `ef5bae33` exactly after whitespace/async normalization.
- Retired method/admission symbols and WorkerDispatch.cs are absent.
- ActorIdleEvictionTests, NotificationWatermarkTests, their SessionInputs fixture
  helper and TrackCollaboratorTests are byte-identical to `bffc6535`.
- The dual-base comparison is
  `git range-diff 6417308d..bffc6535 ef5bae33..HEAD`.

Full solution validation and the two independent review results follow below.

Both new independent read-only L2 reviews found **no introduced blockers**:

- `ses_a354e28682c84c47ab66144bdcff64ed`: compared fresh upstream method bodies,
  retired APIs, report/template/task effect ordering and approval/retry behavior.
  Verified all 57 retained PR136-changed test files match upstream byte-for-byte,
  both upstream-deleted tests remain absent, and the four B2a regression/helper
  files are unchanged.
- `ses_96c03c96c7614ea6966d674848618308`: checked mailbox reservations, activity,
  hydration/disposal, Flow cancellation joins, wake Execution retention and
  private state adapters; found no upstream divergence or revived legacy APIs.

Both performed the required range-diff and upstream comparisons and passed
`git diff --check ef5bae33`; neither edited files nor ran competing builds/tests.

The first full solution attempt hit an environment failure before Actor startup:
all **355** failed Host cases reported the same FileSystemWatcher inotify-instance
limit error while WebApplication.CreateBuilder loaded default configuration.
`/proc/sys/fs/inotify/max_user_instances` was 128, while the descriptor limit was
1,048,576. The other seven suites passed. This was not a failed business assertion.
A fresh full run uses the installed .NET physical file provider's supported
polling mode, without changing production code or skipping any tests:

```sh
DOTNET_USE_POLLING_FILE_WATCHER=1 \
PERGA_OAR_BRIDGE_SCRIPT="$PWD/tests/Perga.Kernel.Tests/bin/b2a-validation-bridge/perga-oar-bridge.mjs" \
  /home/kenji/.dotnet/dotnet test Perga.sln --nologo -m:1
```

Final results for this environment-adjusted full run follow below.

Final `ef5bae33` result with polling file monitoring: **1,012 passed, zero
failed/skipped**, eight assemblies. This includes **351 Kernel**, **376 Host
integration**, **92 Storage**, **127 PolicyVm**, **23 AgentKit**, **27 VcsKit**,
**10 ReportDoc** and **6 TermClient** tests. The reduced count versus earlier
baselines reflects upstream's intentional retirement of legacy tests; no B2a
regression test was removed or bypassed.

The final whitespace check against origin/main, authored-path allowlist, exact
upstream public/internal API comparison, absence of retired symbols and preserved
CI-regression blobs passed. Delivery is one English commit on `ef5bae33`, clean
working tree, with no push.
