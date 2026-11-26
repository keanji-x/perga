const MINUTE_MS = 60_000;
const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;

export type RelativeTimeFormatters = Readonly<{
  sameYear: Intl.DateTimeFormat;
  otherYear: Intl.DateTimeFormat;
}>;

/** Creates reusable date formatters so list renders avoid per-row Intl allocations. */
export function createRelativeTimeFormatters(timeZone?: string): RelativeTimeFormatters {
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

/** Compact relative timestamp (`Just now`, `5m`, `2h`, `Yesterday`, `Nov 24`, `2025-11-24`). */
export function formatRelativeTimestamp(
  atMs: number,
  nowMs: number,
  formatters: RelativeTimeFormatters,
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

/** Ultra-compact elapsed age (`Just now`, `5m`, `2h`, `3d`) for tight secondary metadata. */
export function formatCompactAge(atMs: number, nowMs: number): string {
  if (!Number.isFinite(atMs) || !Number.isFinite(nowMs) || atMs <= 0) return '';
  const diff = Math.max(0, nowMs - atMs);
  if (diff < MINUTE_MS) return 'Just now';
  if (diff < HOUR_MS) return `${Math.floor(diff / MINUTE_MS)}m`;
  if (diff < DAY_MS) return `${Math.floor(diff / HOUR_MS)}h`;
  return `${Math.floor(diff / DAY_MS)}d`;
}
