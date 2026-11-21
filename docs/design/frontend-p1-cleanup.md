# Perga-only frontend contract

Outcome: production assembly and domain operations use the Host's published
Perga endpoints. Remove the retired Neige routes, transport operations and event
normalization, including their exclusive UI and fixtures. Keep SQLite read
receipts, unified sessions and their liveness recovery, template protocols,
schedules, Today calendar, system management and active report renderers.

The Host HTTP registrations and emitted events are authoritative. Remove legacy
OpenAPI paths and then prune schemas by reference reachability; callers import
Perga DTO schemas directly. Keep domain presentation adapters only where the UI
still consumes their shape. No persisted browser keys or backend data migrate.

System observations use TanStack Query with foreground polling and the existing
revision-aware upgrade store, which also accepts WebSocket observations. Removing
legacy sharing must remove its transport header and production assembly together.

Validation: frontend lint, unit/DOM tests, build, affected browser suites and two
independent L2 reviews. Verify preserved Host routes and consumers, retry/cancel
semantics, stale response handling and read-receipt synchronization.

Retained boundaries: read-receipt transport and SQLite synchronization are
unchanged; unified session history, liveness, approvals and cancellation remain
on the Host contract. Templates, schedules, Track data/views, Today calendar
and system management remain available. Paper-trading report components and
report-view conformance tests remain because active template views consume them.
Captured-source citations keep their missing-source presentation without making
requests to a nonexistent endpoint. Provider settings retain the local turn
budget editor; GitHub preview presentation ports remain reusable without legacy
production provider wiring. Browser persistence keys are unchanged.

Retired tests cover only removed sharing, enrollment, recipe/plugin settings,
Neige mention candidates and legacy Today Supervisor/report-change entry points.
Current Perga browser checks cover the calendar, conversation recovery, workspace
selection and terminal retention. Tests import the layer declaration before
component styles to match the production stylesheet order.

The directory preference measurement retains focus ownership only when collapsed
controls actually expand for measurement. Inline edits never leave a pending
restoration marker; asynchronous workspace-option labels still return focus to
the reachable control after the picker closes.

Previous verification on bfc940e (2026-11-22): full frontend lint, 344 unit/DOM files with
4,607 tests, production build, and 212 browser files with 1,969 tests passed.
The browser run includes Area creation, the complete directory ownership matrix,
and PR #118 Chromium reload persistence. Two independent L2 reviews and the final
focus correction review found no blockers. The 57 focused Host integration tests
for retained contracts passed. The additional full .NET attempt was not green:
the OAR bridge script was unconfigured and an unchanged Kernel liveness test
failed. No backend files or persistence migrations are part of this change.

PR #120 rebase (2026-11-22): the patch was resumed from its conflict checkpoint
and rebased onto origin/main 36ba52f. The nine conflict resolutions preserve
execution targets across Area create/edit, Track creation, directory reads,
provider availability, model catalogs, workspace options and Sidebar labels.
A structured comparison preserves all nine changed/new OpenAPI paths and five
changed/new schemas exactly; perga-v1.ts matches the upstream contract.
The two-base comparison is `git range-diff bfc940e..365c5db 36ba52f..HEAD`.

Two independent L2 delta reviews found no blockers. Both also reviewed the
additional remote-target regression tests: directory home/path encoding,
provider status/refresh decoding, and real QueryClient cache isolation for
availability, managed capabilities and model catalogs. The focused additions
passed 72 tests across three files. Existing read receipts, unified sessions,
PR #118 reload persistence, templates/schedules/Today, system observations and
active report rendering retain the previously reviewed P1 behavior.
No backend files, migrations, architectural gates or inventories changed during
the rebase. The Area WebSocket projection's missing execution target is an
upstream limitation already present in 36ba52f, outside this rebase delta.


The first post-rebase full unit run exposed four Area editor fixture failures:
the new targets GET incorrectly consumed the fixture's queued write replies.
The fixture now returns a local target for that exact read, leaving all write,
retry-identity and PATCH assertions intact. Both reviewers approved the fix;
the 17-case Area editor suite passed, including an independent rerun.
Full browser verification on the rebased production tree passed all 1,969 tests
across 212 files. The final frontend gate uses
`(cd fe && npm run lint && npm test && npm run build)`.

Final post-fix verification passed: full lint, 344 unit/DOM files with 4,616
tests, production build, and the second full browser run (212 files / 1,969
tests). The build reports its existing large-chunk warning. Final whitespace
and clean-worktree checks passed; no push or PR was performed.
