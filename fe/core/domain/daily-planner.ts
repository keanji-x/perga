// Daily identity is resolved by the server; the browser never creates a Track on page load.
import { z } from 'zod';
import type { ApiOperation } from '../api/types.ts';

export const dailyTrackSchema = z.object({ date: z.string().refine(isDailyDate), timeZone: z.string(), trackId: z.string() });
export type DailyTrack = z.infer<typeof dailyTrackSchema>;
export const reportChangeSchema = z.object({
  trackId: z.string(), trackTitle: z.string(), areaId: z.string(), areaName: z.string(),
  editCount: z.number(), firstEventId: z.number(), lastEventId: z.number(),
  summaryBefore: z.string(), summaryAfter: z.string(), patch: z.string(), patchTruncated: z.boolean(),
});
export type ReportChange = z.infer<typeof reportChangeSchema>;
export const reportChangesSchema = z.object({
  date: z.string(), timezone: z.string(), throughEventId: z.number(),
  changes: z.array(reportChangeSchema), nextCursor: z.string().nullable(),
});
export type ReportChangesPage = z.infer<typeof reportChangesSchema>;
export const reportEditsSchema = z.object({
  edits: z.array(z.object({ eventId: z.number(), at: z.number(), edit: z.object({
    trackId: z.string(), editId: z.string(), summaryBefore: z.string(), summaryAfter: z.string(),
    bodyBefore: z.string(), bodyAfter: z.string(),
  }) })), nextCursor: z.string().nullable(),
});
export type ReportEditsPage = z.infer<typeof reportEditsSchema>;
export type ReportEditEntry = ReportEditsPage['edits'][number];

export function dailyTrackOperation(date?: string): ApiOperation<DailyTrack | null> {
  return { method: 'GET', path: `/api/v1/today/daily${date === undefined ? '' : `?date=${encodeURIComponent(date)}`}`, responseSchema: dailyTrackSchema.nullable() };
}

export const todayCalendarDaySchema = z.object({
  date: z.string().refine(isDailyDate),
  hasDailyTrack: z.boolean(),
  hasReport: z.boolean(),
});
export type TodayCalendarDay = z.infer<typeof todayCalendarDaySchema>;

export const todayScheduleItemSchema = z.object({
  id: z.string(),
  date: z.string().refine(isDailyDate),
  trackId: z.string(),
  trackTitle: z.string(),
  title: z.string(),
  scheduleKind: z.enum(['once', 'cron']),
  cronExpr: z.string().nullable(),
  timezone: z.string(),
  nextFireAtMs: z.number(),
  state: z.string(),
});
export type TodayScheduleItem = z.infer<typeof todayScheduleItemSchema>;

export const todayCalendarSchema = z.object({
  today: z.string().refine(isDailyDate),
  timeZone: z.string(),
  start: z.string().refine(isDailyDate),
  end: z.string().refine(isDailyDate),
  days: z.array(todayCalendarDaySchema),
  schedules: z.array(todayScheduleItemSchema),
});
export type TodayCalendar = z.infer<typeof todayCalendarSchema>;

export function todayCalendarOperation(start: string, end: string): ApiOperation<TodayCalendar> {
  return {
    method: 'GET',
    path: `/api/v1/today/calendar?start=${encodeURIComponent(start)}&end=${encodeURIComponent(end)}`,
    responseSchema: todayCalendarSchema,
  };
}
export function reportChangesOperation(date: string, page?: Readonly<{ cursor: string; through: number }>): ApiOperation<ReportChangesPage> {
  const suffix = page === undefined ? '' : `&cursor=${encodeURIComponent(page.cursor)}&throughEventId=${page.through}`;
  return { method: 'GET', path: `/api/v1/today/report-changes?date=${encodeURIComponent(date)}${suffix}`, responseSchema: reportChangesSchema };
}
export function reportEditsOperation(date: string, trackId: string, through: number, cursor?: string): ApiOperation<ReportEditsPage> {
  const suffix = cursor === undefined ? '' : `&cursor=${encodeURIComponent(cursor)}`;
  return { method: 'GET', path: `/api/v1/today/report-edits?date=${encodeURIComponent(date)}&trackId=${encodeURIComponent(trackId)}&throughEventId=${through}${suffix}`, responseSchema: reportEditsSchema };
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
