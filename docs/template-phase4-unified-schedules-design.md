# Phase 4: unified track schedules (L2)

`track_schedules` is the sole schedule authority. Templates declare cron rows with
stable IDs, optional titles, timezones and string prompts. Compilation rejects
legacy wake functions, duplicate IDs, invalid cron/timezones and invalid views.
Inputs share one schema validator at creation, binding and patching; prompt
interpolation resolves `${inputs.key}` using supplied values or schema defaults.
No migrations or historical snapshot compatibility adapters are introduced.

## Transactions and ownership

The Kernel prepares template schedule projections before storage. Creation and
template replacement/clear write the track, snapshot, template rows and events in
one SQLite writer transaction. Storage rejects collisions with active non-template
IDs, cancels removed template rows and drops their pending wakes. Non-template
rows remain independent; a terminal non-template ID may be reclaimed by a new
template declaration. Matching active template rows retain occurrence counters
and timestamps when timing is unchanged; input patches refresh prompts without
re-arming schedules. Clearing a template preserves user/agent/plugin schedules.

Firing updates counters/next occurrence, enqueues a wake and records
`track.schedules.updated` in one transaction. Cancel and manual trigger likewise
write wakes and events atomically. Manual trigger queues an immediate wake without
advancing the scheduled occurrence. The hosted service pumps only durable rows;
restart recovery uses persisted schedule timestamps and the existing wake journal.
Closing a track cancels its active schedules and drops outstanding wakes.

## Reactive policy and APIs

Every schedule wake uses `track_schedule:<id>` and carries `scheduleId`,
`sourceKind` and the materialized prompt. During wake preparation the actor calls
`after({type: "schedule.fired", scheduleId, sourceKind, prompt}, ctx)`, persists
changed state and emits `track.updated`. A returned string overrides the prompt
(including blank strings, which suppress delivery); null/undefined uses the
persisted prompt. Provider dispatch remains outside storage transactions.

The `/api/v1/tracks/{trackId}/schedules` HTTP APIs provide GET
(`includeTerminal`, `runsLimit` 0–200, default 20), POST
upsert/re-arm (default source `user`), DELETE by ID, and POST `/{id}/trigger`.
GET returns `{ schedules, recentRuns }`; recent runs read the schedule wake
journal (including pending/delivering/done/dropped states), without a new table.
PATCH `/api/v1/tracks/{trackId}/template/inputs` accepts an input object, merges it
and preserves policy state. MCP and HTTP share Kernel validation. Track listings
read only stored track projections, with no aggregate loads or policy evaluation.
Detailed track reads retain header-view evaluation.

## Verification and delivery

Verify policy compilation, input validation and built-in hooks; transactional
schedule synchronization, conflict rollback and cancellation; firing/restart,
manual triggering, wake suppression and state persistence; HTTP and MCP callers
and event replay. Run `git diff --check` and `dotnet test Perga.sln` on the final
tree. Preserve migrations 0001–0018, frontend files and the architecture diagram.
This is a backend contract change; frontend schema/UI updates are a separate step.
Independent L2 reviews are arranged by the Planner.
