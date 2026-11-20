# Track Schedules & Today Calendar Integration Design

## Outcome

Unifies dynamic Track schedules/reminders on top of PR #59's durable `track_wakes` outbox (`0014_track_wakes.sql`) and connects PR #58's `DailyCalendar` (`Today`) to real date navigation and server calendar/schedule projections—without introducing a separate calendar domain or external plugin subsystem into the Kernel.

Review Tier: **L2** (forward SQLite migration `0018_track_schedules.sql`, transactional schedule firing -> `track_wakes` outbox, crash/restart recovery, Kernel MCP tools, and Today HTTP/UI integration).

## Revision outcome

Before migration 0018 is released, schedules belong only to their Track and use a fixed wake key per schedule. Idle durable polling reads the due index before entering the writer; committed batches poke Leads immediately and retain failed pokes for later ticks. Calendar history uses only recorded firing timestamps, including terminal schedules, with conservative bounded cron expansion. Regression checks cover storage isolation, delivery, HTTP bounds, wire contracts, and invalid-date navigation.

## Architecture & Layer Boundaries

1. **Storage (`Perga.Storage.Sqlite`, forward migration `0018_track_schedules.sql`)**
   - Preserves released migrations `0001`..`0017` byte-for-byte.
   - Adds `track_schedules` with Track-scoped composite primary key `PRIMARY KEY (track_id, id)`:
     - Columns: `id`, `track_id` (`REFERENCES tracks(id) ON DELETE CASCADE`), `source_kind` (`'agent' | 'user' | 'plugin' | 'template'`), `schedule_kind` (`'once' | 'cron'`), `title`, `prompt`, `cron_expr`, `timezone`, `run_at_ms`, `next_fire_at_ms`, `last_fired_at_ms`, `max_occurrences`, `fired_count`, `state` (`'active' | 'done' | 'canceled'`), `created_at_ms`, `updated_at_ms`.
     - Indexes: partial `idx_track_schedules_due(state, next_fire_at_ms) WHERE state = 'active' AND next_fire_at_ms IS NOT NULL`, and `idx_track_schedules_track(track_id, state, next_fire_at_ms)`.
   - Updating an active schedule by `(track_id, id)` preserves `fired_count`, `last_fired_at_ms`, and `created_at_ms` (rejecting `maxOccurrences <= fired_count`), while re-arming a terminal `done`/`canceled` row resets `fired_count`.
   - Atomic firing (`FireDueAsync`): first checks `SELECT EXISTS` against the partial due index on a read connection. An idle tick returns without taking the writer. Inside one single-writer transaction (`SqliteSingleWriter`), selects up to 64 due active rows joined with tracks, cancels selected rows whose track is closed, advances `fired_count`/`last_fired_at_ms`/`next_fire_at_ms` (or marks `done` for `once` or exhausted `max_occurrences`), and inserts the corresponding `track_wakes(kind='schedule')` row via `EnqueueWakeInternalAsync` using the disjoint `track_schedule:<id>` prefix. Because cron advancement computes the next occurrence strictly after `nowMs`, restart after downtime fires once cleanly instead of catch-up storming.
   - Each schedule maps 1:1 to `track_schedule:<id>`. Wakes always have `TargetSessionId = null`; the scheduler resolves the current Lead when claiming, so switching or deleting the old session cannot lose a reminder.
   - Cancellation (`CancelAsync` & `CancelActiveForTrackAsync`): cancels `active` schedules and drops matching pending `track_schedule:*` wakes in the same writer transaction without affecting template `schedule:*` wakes. Single-schedule cancellation matches Track, schedule kind, the exact coalescing key, `json_extract(payload_json, '$.scheduleId')`, and pending state. Track closure also cancels active schedules.
   - Maintenance GC (`PruneTrackSchedulesAsync` in `SqliteTrackStore.Maintenance.cs`): expires active rows on closed tracks and prunes terminal (`done`/`canceled`) rows older than 90 days by `rowid` in bounded batches.

2. **Kernel (`Perga.Kernel`)**
   - Port `ITrackScheduleStore` + `IScheduleCronEvaluator` (isolating `Cronos` parsing in `Perga.Host` while allowing deterministic evaluation in Kernel tests).
   - `PergaKernelService`: `SetTrackScheduleAsync`, `CancelTrackScheduleAsync`, `ListTrackSchedulesAsync`, `GetTodayCalendarAsync` (expands upcoming active `cron` schedules across the `[start, end]` calendar window, at most one occurrence per schedule per server-local day, respecting `maxOccurrences`). Historical items come only from the recorded `last_fired_at_ms`, for active or done schedules on open Tracks; missed cron times during downtime are never reconstructed. An upcoming item takes precedence over a recorded fire on the same day.
   - `ITodayReadStore.ListCalendarSchedulesInWindowAsync` is required (no silent empty defaults): it selects active schedules with a next fire and either a cron expression, an in-window next fire, or an in-window last fire; it also selects done schedules with an in-window last fire. `TodayScheduleEntry.NextFireAtMs` is nullable for terminal rows.
   - Cron budget computation skips next fires outside the window, handles one remaining occurrence directly, and takes at most 100,000 steps. It returns an in-window cursor as a conservative cap if the step budget is exhausted; only crossing the window means no in-window cap is needed.
   - `TodayCalendar.ParseRunAtMs` uses `DateTime.TryParse` with `NoCurrentDateDefault | RoundtripKind` and rejects missing dates/pre-1970 years. Explicit offsets use `DateTimeOffset.TryParse` with `RoundtripKind` after that validation (`DateTimeOffset` rejects `NoCurrentDateDefault` in .NET 11). Offset-less timestamps use the requested/server timezone, reject DST gaps, and choose the earlier instant for ambiguous local times. No regex inference is required.
   - `TrackActor.TrackWakes.cs`: `PrepareWakePromptAsync` routes explicitly on `TrackWakeRequests.IsTrackScheduleWake(wake)` (`track_schedule:` prefix) for durable schedules while preserving `_policyVm.BuildScheduleWakePrompt` / `StaticWakePrompt` for template-declared `schedule:` wakes.
   - `KernelMcpTools`: exposes `perga_schedule_ls` (shared on Track), `perga_schedule_set` and `perga_schedule_rm` (governance-only, `BoundAttemptId == null`), with strict unknown-key rejection and input validation (`cron` vs `runAt`/`runAtMs`, `timezone`, `maxOccurrences`, non-empty `title`/`prompt`).

3. **Host (`Perga.Host`)**
   - `CronosScheduleCronEvaluator` wraps `Cronos` (`Standard` 5-field & `IncludeSeconds` 6-field) using `TodayCalendar.TryResolveZone`.
   - `CronosScheduleHostedService`: preserves lightweight template schedule evaluation via `ITrackReadStore.ListOpenTrackScheduleSourcesAsync` and additionally fires durable `track_schedules` via `ITrackScheduleStore.FireDueAsync`, then calls `PokeLeadAsync` immediately after each committed batch. A HashSet guarded by the evaluation lock retains failed or draining pokes and retries them on later ticks, including ticks with no due schedules. The service has one constructor with optional durable store/evaluator dependencies.
   - `TodayEndpoints`:
     - `GET /api/today/calendar?start=YYYY-MM-DD&end=YYYY-MM-DD` (inclusive bounds, at most 62 days, `end=9999-12-31` rejected with 400 before iteration; documented in `fe/core/api/generated/openapi.json`): returns `{ today, time_zone, start, end, days: [{ date, has_daily_track, has_report }], schedules: [{ id, date, track_id, track_title, title, schedule_kind, cron_expr, timezone, next_fire_at_ms, state }] }`.

4. **Frontend (`fe`)**
   - `fe/core/domain/daily-planner.ts`: schema & operation for `/api/today/calendar` and matching `TodayCalendar`, `TodayCalendarDay`, `TodayScheduleItem` wire types in `fe/core/api/generated/wire.ts`, plus shared UTC date helpers (`shiftDailyDate`, `startOfDailyWeek`, `localDailyDate`, `isDailyDate`).
   - `fe/web/src/app/router/navigation.ts` & `public.tsx`: `NavTarget` `today` carries optional `day?: string`, keeping `/` for server today and `/?day=YYYY-MM-DD` for other days.
   - `fe/web/src/features/today/daily-calendar.tsx`: accepts optional `selectedDate` (`null` means no selected day, so invalid `?day=` can escape through Today or any date), `today`, `mode`, `onModeChange`, `days`, `schedules`, `timeZone`, `errorNotice`, `onSelectDate`, `onVisibleRangeChange`, `onOpenTrack`. Agenda items expose `data-nc-schedule-state`. Derives per-day schedule counts and selected-day agenda directly from `schedules` (single source of truth).
   - `fe/web/src/app/router/daily-planner.tsx`: wires `DailyCalendar` in `pergaMode` across loaded, pending, error, invalid-date, and missing-date (`daily.data === null`) states while lifting `mode` so Week/Month survives route transitions.

## Verification Plan
- **Storage tests**: `0017 -> 0018` atomic migration & rollback test; Track-scoped `(track_id, id)` isolation across Tracks; `fired_count`/`last_fired_at_ms` preservation on update; `ITrackScheduleStore` CRUD, `once`/`cron` `FireDueAsync` atomicity with `track_wakes`, catch-up-once after downtime, cancel dropping only `track_schedule:*` wakes without touching template `schedule:*` wakes, closed-track cancellation, and `rowid`-based maintenance pruning.
- **Kernel tests**: `perga_schedule_set` / `perga_schedule_ls` / `perga_schedule_rm` validation (including schema-aligned error text and unknown-arg rejection) & authorization (governance vs contract worker), `PrepareWakePromptAsync` explicit `track_schedule:` vs template `schedule:` dispatch, `GetTodayCalendarAsync`.
- **Host integration tests**: real `CronosScheduleHostedService` tick firing both one-shot and recurring `track_schedules` into `track_wakes` -> executed Lead turn, surviving host restart on the same SQLite DB; HTTP `/api/today/calendar` validation, range/timezone boundaries, and multi-day cron occurrence expansion (including post-fire retention on Today and `maxOccurrences` cap).
- **Frontend unit & browser tests**: `daily-calendar.test.tsx`, `daily-planner.test.tsx`, `daily-planner.browser.test.tsx` (1440px/390px, Month persistence, Back/Forward history, missing-date shell, day schedules).
- **Full suite & L2 reviews**: `dotnet test Perga.sln --no-restore`, `cd fe && npm run lint && npm run build && npm test`, `npx vitest run --project browser web/src/app/router/daily-planner.browser.test.tsx`, plus two independent read-only L2 reviews.
