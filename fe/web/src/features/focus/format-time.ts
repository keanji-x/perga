const MINUTE_MS = 60_000;
const HOUR_MS = 3_600_000;

export type FocusTimeFormatters = Readonly<{
  sameYear: Intl.DateTimeFormat;
  otherYear: Intl.DateTimeFormat;
}>;

/** Creates reusable date formatters for Focus feed cards so renders avoid per-row Intl allocations. */
export function createFocusTimeFormatters(timeZone?: string): FocusTimeFormatters {
  return {
    sameYear: new Intl.DateTimeFormat(
      'en-US',
      timeZone === undefined ? { month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric', timeZone },
    ),
    otherYear: new Intl.DateTimeFormat(
      'en-CA',
      timeZone === undefined
        ? { year: 'numeric', month: '2-digit', day: '2-digit' }
        : { year: 'numeric', month: '2-digit', day: '2-digit', timeZone },
    ),
  };
}

function localDayStart(ms: number): number {
  const date = new Date(ms);
  date.setHours(0, 0, 0, 0);
  return date.getTime();
}

/** Compact relative timestamp for mobile Focus feed cards. */
export function formatFocusTimestamp(
  atMs: number,
  nowMs: number,
  formatters: FocusTimeFormatters,
): string {
  if (!Number.isFinite(atMs) || !Number.isFinite(nowMs) || atMs <= 0) return '';
  const diff = nowMs - atMs;
  if (diff < MINUTE_MS) return 'Just now';
  if (diff < HOUR_MS) return `${Math.floor(diff / MINUTE_MS)}m`;

  const todayStart = localDayStart(nowMs);
  if (atMs >= todayStart) return `${Math.floor(diff / HOUR_MS)}h`;

  const yesterday = new Date(todayStart);
  yesterday.setDate(yesterday.getDate() - 1);
  if (atMs >= yesterday.getTime()) return 'Yesterday';

  const date = new Date(atMs);
  const nowDate = new Date(nowMs);
  if (date.getFullYear() === nowDate.getFullYear()) {
    return formatters.sameYear.format(date);
  }
  return formatters.otherYear.format(date);
}
