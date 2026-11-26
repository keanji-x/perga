import { describe, expect, it } from 'vitest';

import { createFocusTimeFormatters, formatFocusTimestamp } from './format-time.ts';

describe('formatFocusTimestamp', () => {
  const localTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const formatters = createFocusTimeFormatters(localTimeZone);
  const now = new Date(2026, 9, 15, 14, 30, 0).getTime();

  it('returns empty string for invalid or non-positive timestamps', () => {
    expect(formatFocusTimestamp(0, now, formatters)).toBe('');
    expect(formatFocusTimestamp(-1000, now, formatters)).toBe('');
    expect(formatFocusTimestamp(Number.NaN, now, formatters)).toBe('');
    expect(formatFocusTimestamp(Number.POSITIVE_INFINITY, now, formatters)).toBe('');
    expect(formatFocusTimestamp(now - 60_000, Number.NaN, formatters)).toBe('');
  });

  it('formats future skew and sub-minute differences as Just now', () => {
    expect(formatFocusTimestamp(now + 120_000, now, formatters)).toBe('Just now');
    expect(formatFocusTimestamp(now - 59_999, now, formatters)).toBe('Just now');
  });

  it('formats minutes and same-day hours', () => {
    expect(formatFocusTimestamp(now - 60_000, now, formatters)).toBe('1m');
    expect(formatFocusTimestamp(now - 59 * 60_000, now, formatters)).toBe('59m');
    expect(formatFocusTimestamp(now - 60 * 60_000, now, formatters)).toBe('1h');
    expect(formatFocusTimestamp(new Date(2026, 9, 15, 0, 0, 0).getTime(), now, formatters)).toBe('14h');
  });

  it('uses Yesterday across local midnight even when under 24 hours ago', () => {
    const afterMidnight = new Date(2026, 9, 15, 0, 30, 0).getTime();
    const beforeMidnight = new Date(2026, 9, 14, 23, 0, 0).getTime();
    expect(formatFocusTimestamp(beforeMidnight, afterMidnight, formatters)).toBe('Yesterday');

    const yesterdayMorning = new Date(2026, 9, 14, 9, 0, 0).getTime();
    expect(formatFocusTimestamp(yesterdayMorning, now, formatters)).toBe('Yesterday');
  });

  it('formats older same-year dates as short month/day and prior-year dates as ISO date', () => {
    const earlierThisYear = new Date(2026, 9, 1, 12, 0, 0).getTime();
    expect(formatFocusTimestamp(earlierThisYear, now, formatters)).toBe('Oct 1');

    const lastYear = new Date(2025, 11, 31, 12, 0, 0).getTime();
    expect(formatFocusTimestamp(lastYear, now, formatters)).toBe('2025-12-31');
  });
});
