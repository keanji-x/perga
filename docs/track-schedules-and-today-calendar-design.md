# Track Schedules & Today Calendar Integration Design

## Outcome

Unifies dynamic Track schedules/reminders on top of PR #59's durable `track_wakes` outbox (`0014_track_wakes.sql`) and connects PR #58's `DailyCalendar` (`Today`) to real date navigation and server calendar/schedule projections—without introducing a separate calendar domain or external plugin subsystem into the Kernel.

Review Tier: **L2** (forward SQLite migration `0018_track_schedules.sql`, transactional schedule firing -> `track_wakes` outbox, crash/restart recovery, Kernel MCP tools, and Today HTTP/UI integration).

## Architecture & Layer Boundaries

1. **Storage (`Perga.Storage.Sqlite`, forward migration `0018_track_schedules.sql`)**
   - Preserves released migrations `0001`..`0017` byte-for-byte.
   - Adds `track_schedules` with Track-scoped composite primary key `PRIMARY KEY (track_id, id)`:
     - Columns: `id`, `track_id` (`REFERENCES tracks(id) ON DELETE CASCADE`), `target_session_id` (`REFERENCES agent_sessions(id) ON DELETE CASCADE`), `source_kind` (`'agent' | 'user' | 'plugin' | 'template'`), `schedule_kind` (`'once' | 'cron'`), `title`, `prompt`, `cron_expr`, `timezone`, `run_at_ms`, `next_fire_at_ms`, `last_fired_at_ms`, `max_occurrences`, `fired_count`, `coalesce_key`, `state` (`'active' | 'paused' | 'done' | 'canceled'`), `created_at_ms`, `updated_at_ms`.
     - Indexes: partial `idx_track_schedules_due(state, next_fire_at_ms) WHERE state = 'active' AND next_fire_at_ms IS NOT NULL`, and `idx_track_schedules_track(track_id, state, next_fire_at_ms)`.
   - Updating an active/paused schedule by `(track_id, id)` preserves `fired_count`, `last_fired_at_ms`, and `created_at_ms` (rejecting `maxOccurrences <= fired_count`), while re-arming a terminal `done`/`canceled` row resets `fired_count`.
   - Atomic firing (`FireDueAsync`): inside one single-writer transaction (`SqliteSingleWriter`), cancels any `active` rows whose track is now closed, selects up to 64 open-track rows with `next_fire_at_ms <= @NowMs`, advances `fired_count`/`last_fired_at_ms`/`next_fire_at_ms` (or marks `done` for `once` or exhausted `max_occurrences`), and inserts the corresponding `track_wakes(kind='schedule')` row via `EnqueueWakeInternalAsync` using the disjoint `track_schedule:<id_or_coalesce_key>` prefix. Because cron advancement computes the next occurrence strictly after `nowMs`, restart after downtime fires once cleanly instead of catch-up storming.
   - Cancellation (`CancelAsync` & `CancelActiveForTrackAsync`): cancels `active`/`paused` schedules and drops matching pending `track_schedule:*` wakes in the same writer transaction without affecting template `schedule:*` wakes. Track closure also cancels active/paused schedules.
   - Maintenance GC (`PruneTrackSchedulesAsync` in `SqliteTrackStore.Maintenance.cs`): expires active/paused rows on closed tracks and prunes terminal (`done`/`canceled`) rows older than 90 days by `rowid` in bounded batches.

2. **Kernel (`Perga.Kernel`)**
   - Port `ITrackScheduleStore` + `IScheduleCronEvaluator` (isolating `Cronos` parsing in `Perga.Host` while allowing deterministic evaluation in Kernel tests).
   - `PergaKernelService`: `SetTrackScheduleAsync`, `CancelTrackScheduleAsync`, `ListTrackSchedulesAsync`, `GetTodayCalendarAsync` (expands active `cron` schedules across the `[start, end]` calendar window, at most one occurrence per server-local day, including today's already-fired occurrence and respecting `maxOccurrences`).
   - `TrackActor.TrackWakes.cs`: `PrepareWakePromptAsync` routes explicitly on `TrackWakeRequests.IsTrackScheduleWake(wake)` (`track_schedule:` prefix) for durable schedules while preserving `_policyVm.BuildScheduleWakePrompt` / `StaticWakePrompt` for template-declared `schedule:` wakes.
   - `KernelMcpTools`: exposes `perga_schedule_ls` (shared on Track), `perga_schedule_set` and `perga_schedule_rm` (governance-only, `BoundAttemptId == null`), with strict unknown-key rejection and input validation (`cron` vs `runAt`/`runAtMs`, `timezone`, `maxOccurrences`, non-empty `title`/`prompt`).

3. **Host (`Perga.Host`)**
   - `CronosScheduleCronEvaluator` wraps `Cronos` (`Standard` 5-field & `IncludeSeconds` 6-field) using `TodayCalendar.TryResolveZone`.
   - `CronosScheduleHostedService`: preserves lightweight template schedule evaluation via `ITrackReadStore.ListOpenTrackScheduleSourcesAsync` and additionally fires durable `track_schedules` via `ITrackScheduleStore.FireDueAsync`, then calls `PokeLeadAsync` on affected tracks.
   - `TodayEndpoints`:
     - `GET /api/today/calendar?start=YYYY-MM-DD&end=YYYY-MM-DD` (strict bounds, max 62 days; documented in `fe/core/api/generated/openapi.json`): returns `{ today, time_zone, start, end, days: [{ date, has_daily_track, has_report }], schedules: [{ id, date, track_id, track_title, title, schedule_kind, cron_expr, timezone, next_fire_at_ms, state }] }`.

4. **Frontend (`fe`)**
   - `fe/core/domain/daily-planner.ts`: schema & operation for `/api/today/calendar` plus shared UTC date helpers (`shiftDailyDate`, `startOfDailyWeek`, `localDailyDate`, `isDailyDate`).
   - `fe/web/src/app/router/navigation.ts` & `public.tsx`: `NavTarget` `today` carries optional `day?: string`, keeping `/` for server today and `/?day=YYYY-MM-DD` for other days.
   - `fe/web/src/features/today/daily-calendar.tsx`: accepts optional `selectedDate`, `today`, `mode`, `onModeChange`, `days`, `schedules`, `timeZone`, `errorNotice`, `onSelectDate`, `onVisibleRangeChange`, `onOpenTrack`. Derives per-day schedule counts and selected-day agenda directly from `schedules` (single source of truth).
   - `fe/web/src/app/router/daily-planner.tsx`: wires `DailyCalendar` in `pergaMode` across loaded, pending, error, invalid-date, and missing-date (`daily.data === null`) states while lifting `mode` so Week/Month survives route transitions.

## Verification Plan
- **Storage tests**: `0017 -> 0018` atomic migration & rollback test; Track-scoped `(track_id, id)` isolation across Tracks; `fired_count`/`last_fired_at_ms` preservation on update; `ITrackScheduleStore` CRUD, `once`/`cron` `FireDueAsync` atomicity with `track_wakes`, catch-up-once after downtime, cancel dropping only `track_schedule:*` wakes without touching template `schedule:*` wakes, closed-track cancellation, and `rowid`-based maintenance pruning.
- **Kernel tests**: `perga_schedule_set` / `perga_schedule_ls` / `perga_schedule_rm` validation (including schema-aligned error text and unknown-arg rejection) & authorization (governance vs contract worker), `PrepareWakePromptAsync` explicit `track_schedule:` vs template `schedule:` dispatch, `GetTodayCalendarAsync`.
- **Host integration tests**: real `CronosScheduleHostedService` tick firing both one-shot and recurring `track_schedules` into `track_wakes` -> executed Lead turn, surviving host restart on the same SQLite DB; HTTP `/api/today/calendar` validation, range/timezone boundaries, and multi-day cron occurrence expansion (including post-fire retention on Today and `maxOccurrences` cap).
- **Frontend unit & browser tests**: `daily-calendar.test.tsx`, `daily-planner.test.tsx`, `daily-planner.browser.test.tsx` (1440px/390px, Month persistence, Back/Forward history, missing-date shell, day schedules).
- **Full suite & L2 reviews**: `dotnet test Perga.sln --no-restore`, `cd fe && npm run lint && npm run build && npm test`, `npx vitest run --project browser web/src/app/router/daily-planner.browser.test.tsx`, plus two independent read-only L2 reviews.
