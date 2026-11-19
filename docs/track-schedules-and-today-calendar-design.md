# Track Schedules & Today Calendar Integration Design

## Outcome

Unifies dynamic Track schedules/reminders on top of PR #59's durable `track_wakes` outbox (`0014_track_wakes.sql`) and connects PR #58's `DailyCalendar` (`Today`) to real date navigation and server calendar/schedule projections—without introducing a separate calendar domain or external plugin subsystem into the Kernel.

Review Tier: **L2** (forward SQLite migration `0018_track_schedules.sql`, transactional schedule firing -> `track_wakes` outbox, crash/restart recovery, Kernel MCP tools, and Today HTTP/UI integration).

## Architecture & Layer Boundaries

1. **Storage (`Perga.Storage.Sqlite`, forward migration `0018_track_schedules.sql`)**
   - Preserves released migrations `0001`..`0017` byte-for-byte.
   - Adds `track_schedules`:
     - Columns: `id` (PK), `track_id` (`REFERENCES tracks(id) ON DELETE CASCADE`), `target_session_id` (`REFERENCES agent_sessions(id) ON DELETE CASCADE`), `source_kind` (`'agent' | 'user' | 'plugin'`), `schedule_kind` (`'once' | 'cron'`), `title`, `prompt`, `cron_expr`, `timezone`, `run_at_ms`, `next_fire_at_ms`, `last_fired_at_ms`, `max_occurrences`, `fired_count`, `coalesce_key`, `state` (`'active' | 'done' | 'canceled'`), `created_at_ms`, `updated_at_ms`.
     - Indexes: partial `idx_track_schedules_due(state, next_fire_at_ms) WHERE state = 'active' AND next_fire_at_ms IS NOT NULL`, and `idx_track_schedules_track(track_id, state, next_fire_at_ms)`.
   - Atomic firing (`FireDueAsync`): inside one single-writer transaction (`SqliteSingleWriter`), cancels any `active` rows whose track is now closed, selects up to 64 open-track rows with `next_fire_at_ms <= @NowMs`, advances `fired_count`/`last_fired_at_ms`/`next_fire_at_ms` (or marks `done` for `once` or exhausted `max_occurrences`), and inserts the corresponding `track_wakes(kind='schedule')` row via `EnqueueWakeInternalAsync` with `coalesce_key` and `{ scheduleId, prompt, presentation: "system" }`. Because cron advancement computes the next occurrence strictly after `nowMs`, restart after downtime fires once cleanly instead of catch-up storming.
   - Cancellation (`CancelAsync` & `CancelActiveForTrackAsync`): cancels active schedules and drops matching pending `track_wakes` rows in the same writer transaction. Track closure also cancels active schedules.
   - Maintenance GC (`PruneTrackSchedulesAsync` in `SqliteTrackStore.Maintenance.cs`): expires active rows on closed tracks and prunes terminal (`done`/`canceled`) rows older than 90 days in bounded batches.

2. **Kernel (`Perga.Kernel`)**
   - Port `ITrackScheduleStore` + `IScheduleCronEvaluator` (isolating `Cronos` parsing in `Perga.Host` while allowing deterministic evaluation in Kernel tests).
   - `PergaKernelService`: `SetTrackScheduleAsync`, `CancelTrackScheduleAsync`, `ListTrackSchedulesAsync`, `GetTodayCalendarAsync`.
   - `TrackActor.TrackWakes.cs`: `PrepareWakePromptAsync` honors `payload.Prompt` for dynamic `schedule` wakes while preserving `_policyVm.BuildScheduleWakePrompt` / `StaticWakePrompt` for template-declared schedules when `payload.Prompt` is empty.
   - `KernelMcpTools`: exposes `perga_schedule_ls` (shared on Track), `perga_schedule_set` and `perga_schedule_rm` (governance-only, `BoundAttemptId == null`), with strict input validation (`cron` vs `runAt`/`runAtMs`, `timezone`, `maxOccurrences`, non-empty `title`/`prompt`).

3. **Host (`Perga.Host`)**
   - `CronosScheduleCronEvaluator` wraps `Cronos` (`Standard` 5-field & `IncludeSeconds` 6-field) using `TodayCalendar.TryResolveZone`.
   - `CronosScheduleHostedService`: preserves lightweight template schedule evaluation via `ITrackReadStore.ListOpenTrackScheduleSourcesAsync` and additionally fires durable `track_schedules` via `ITrackScheduleStore.FireDueAsync`, then calls `PokeLeadAsync` on affected tracks.
   - `TodayEndpoints`:
     - `GET /api/today/calendar?start=YYYY-MM-DD&end=YYYY-MM-DD` (strict bounds, max 62 days): returns `{ today, time_zone, start, end, days: [{ date, has_daily_track, has_report }], schedules: [{ id, date, track_id, track_title, title, schedule_kind, cron_expr, timezone, next_fire_at_ms, state }] }`.

4. **Frontend (`fe`)**
   - `fe/core/domain/daily-planner.ts`: schema & operation for `/api/today/calendar` plus shared UTC date helpers (`shiftDailyDate`, `startOfDailyWeek`, `localDailyDate`, `isDailyDate`).
   - `fe/web/src/app/router/navigation.ts` & `public.tsx`: `NavTarget` `today` carries optional `day?: string`, keeping `/` for server today and `/?day=YYYY-MM-DD` for other days.
   - `fe/web/src/features/today/daily-calendar.tsx`: accepts optional `selectedDate`, `today`, `mode`, `onModeChange`, `days`, `schedules`, `timeZone`, `errorNotice`, `onSelectDate`, `onVisibleRangeChange`, `onOpenTrack`. Derives per-day schedule counts and selected-day agenda directly from `schedules` (single source of truth).
   - `fe/web/src/app/router/daily-planner.tsx`: wires `DailyCalendar` in `pergaMode` across loaded, pending, error, invalid-date, and missing-date (`daily.data === null`) states while lifting `mode` so Week/Month survives route transitions.

## Verification Plan
- **Storage tests**: `0014 -> 0015` atomic migration & rollback test; `ITrackScheduleStore` CRUD, `once`/`cron` `FireDueAsync` atomicity with `track_wakes`, catch-up-once after downtime, cancel dropping pending wakes, closed-track cancellation, and maintenance pruning.
- **Kernel tests**: `perga_schedule_set` / `perga_schedule_ls` / `perga_schedule_rm` validation & authorization (governance vs contract worker), `PrepareWakePromptAsync` custom prompt vs template fallback, `GetTodayCalendarAsync` & `GetTodaySchedulesAsync`.
- **Host integration tests**: real `CronosScheduleHostedService` tick firing both one-shot and recurring `track_schedules` into `track_wakes` -> executed Lead turn, surviving host restart on the same SQLite DB; HTTP `/api/today/calendar` & `/api/today/schedules` validation and range/timezone boundaries.
- **Frontend unit & browser tests**: `daily-calendar.test.tsx` (indicators + scheduled items + callbacks), `daily-planner.browser.test.tsx` (Perga Today calendar navigation updating `?day=`, fetching target day track, missing-date empty state with persistent calendar, jump back to `Today`, and displaying day schedules).
- **Full suite & L2 reviews**: `dotnet test Perga.sln --no-restore`, `cd fe && npm run lint && npm run build && npm test`, `npx vitest run --project browser web/src/app/router/daily-planner.browser.test.tsx`, plus two independent read-only L2 reviews.
