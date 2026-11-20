// Daily identity is resolved by the server; the browser never creates a Track on page load.
import { z } from 'zod';
import type { ApiOperation } from '../api/types.ts';

export const dailyTrackSchema = z.object({ date: z.string().refine(isDailyDate), time_zone: z.string(), track_id: z.string() });
export type DailyTrack = z.infer<typeof dailyTrackSchema>;
export const reportChangeSchema = z.object({
  track_id: z.string(), track_title: z.string(), area_id: z.string(), area_name: z.string(),
  edit_count: z.number(), first_event_id: z.number(), last_event_id: z.number(),
  summary_before: z.string(), summary_after: z.string(), patch: z.string(), patch_truncated: z.boolean(),
});
export type ReportChange = z.infer<typeof reportChangeSchema>;
const reportChangesSchema = z.object({
  date: z.string(), timezone: z.string(), through_event_id: z.number(),
  changes: z.array(reportChangeSchema), next_cursor: z.string().nullable(),
});
export type ReportChangesPage = z.infer<typeof reportChangesSchema>;
const reportEditsSchema = z.object({
  edits: z.array(z.object({ event_id: z.number(), at: z.number(), edit: z.object({
    track_id: z.string(), edit_id: z.string(), summary_before: z.string(), summary_after: z.string(),
    body_before: z.string(), body_after: z.string(),
  }) })), next_cursor: z.string().nullable(),
});
export type ReportEditsPage = z.infer<typeof reportEditsSchema>;
export type ReportEditEntry = ReportEditsPage['edits'][number];

export function dailyTrackOperation(date?: string): ApiOperation<DailyTrack | null> {
  return { method: 'GET', path: `/api/today/daily${date === undefined ? '' : `?date=${encodeURIComponent(date)}`}`, responseSchema: dailyTrackSchema.nullable() };
}

export const todayCalendarDaySchema = z.object({
  date: z.string().refine(isDailyDate),
  has_daily_track: z.boolean(),
  has_report: z.boolean(),
});
export type TodayCalendarDay = z.infer<typeof todayCalendarDaySchema>;

export const todayScheduleItemSchema = z.object({
  id: z.string(),
  date: z.string().refine(isDailyDate),
  track_id: z.string(),
  track_title: z.string(),
  title: z.string(),
  schedule_kind: z.enum(['once', 'cron']),
  cron_expr: z.string().nullable(),
  timezone: z.string(),
  next_fire_at_ms: z.number(),
  state: z.string(),
});
export type TodayScheduleItem = z.infer<typeof todayScheduleItemSchema>;

export const todayCalendarSchema = z.object({
  today: z.string().refine(isDailyDate),
  time_zone: z.string(),
  start: z.string().refine(isDailyDate),
  end: z.string().refine(isDailyDate),
  days: z.array(todayCalendarDaySchema),
  schedules: z.array(todayScheduleItemSchema),
});
export type TodayCalendar = z.infer<typeof todayCalendarSchema>;

export function todayCalendarOperation(start: string, end: string): ApiOperation<TodayCalendar> {
  return {
    method: 'GET',
    path: `/api/today/calendar?start=${encodeURIComponent(start)}&end=${encodeURIComponent(end)}`,
    responseSchema: todayCalendarSchema,
  };
}
export function reportChangesOperation(date: string, page?: Readonly<{ cursor: string; through: number }>): ApiOperation<ReportChangesPage> {
  const suffix = page === undefined ? '' : `&cursor=${encodeURIComponent(page.cursor)}&through_event_id=${page.through}`;
  return { method: 'GET', path: `/api/today/report-changes?date=${encodeURIComponent(date)}${suffix}`, responseSchema: reportChangesSchema };
}
export function reportEditsOperation(date: string, trackId: string, through: number, cursor?: string): ApiOperation<ReportEditsPage> {
  const suffix = cursor === undefined ? '' : `&cursor=${encodeURIComponent(cursor)}`;
  return { method: 'GET', path: `/api/today/report-edits?date=${encodeURIComponent(date)}&track_id=${encodeURIComponent(trackId)}&through_event_id=${through}${suffix}`, responseSchema: reportEditsSchema };
}

/** Date-only arithmetic, independent of the browser's local zone. */
export function shiftDailyDate(date: string, days: number): string {
  const value = new Date(`${date}T12:00:00Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}

export function localDailyDate(now: Date = new Date()): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

export function startOfDailyWeek(date: string): string {
  const parsed = new Date(`${date}T12:00:00Z`);
  return shiftDailyDate(date, -((parsed.getUTCDay() + 6) % 7));
}

export function isDailyDate(date: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return false;
  const parsed = new Date(`${date}T12:00:00Z`);
  return Number.isFinite(parsed.getTime()) && parsed.toISOString().slice(0, 10) === date;
}

const supervisorSnapshotSchema = z.object({
  observed_at_ms: z.number(),
  kernel_version: z.string(),
  build_sha: z.string().nullable(),
  settled_worker_cleanup_candidates: z.number().int().nonnegative().nullable(),
  write_bucket_ms: z.number().int().positive(),
  write_bucket_count: z.number().int().positive().max(120),
  database_writes: z.array(z.object({
    started_at_ms: z.number(),
    transactions: z.number().int().nonnegative(),
    failures: z.number().int().nonnegative(),
    retries: z.number().int().nonnegative(),
    mean_wait_ms: z.number().nonnegative(),
  })).nullable(),
  scheduler_boot_sweep_completed: z.boolean(),
  retired_workspaces: z.object({ count: z.number(), oldest_retired_at_ms: z.number().nullable() }).nullable(),
});

export type SupervisorSnapshot = z.infer<typeof supervisorSnapshotSchema>;

export function supervisorSnapshotOperation(): ApiOperation<SupervisorSnapshot> {
  return { method: 'GET', path: '/api/today/supervisor', responseSchema: supervisorSnapshotSchema };
}
