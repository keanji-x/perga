import { describe, expect, it } from 'vitest';

import {
  createRelativeTimeFormatters,
  formatCompactAge,
  formatRelativeTimestamp,
} from './relative-time.js';

describe('formatRelativeTimestamp', () => {
  const formatters = createRelativeTimeFormatters('UTC');
  const now = new Date(2026, 10, 26, 14, 30, 0).getTime();

  it('formats sub-minute and sub-hour differences concisely', () => {
    expect(formatRelativeTimestamp(now - 20_000, now, formatters)).toBe('Just now');
    expect(formatRelativeTimestamp(now + 5_000, now, formatters)).toBe('Just now');
    expect(formatRelativeTimestamp(now - 5 * 60_000, now, formatters)).toBe('5m');
  });

  it('formats same-day hours and yesterday', () => {
    expect(formatRelativeTimestamp(now - 3 * 3_600_000, now, formatters)).toBe('3h');
    const yesterday = new Date(2026, 10, 25, 22, 15, 0).getTime();
    expect(formatRelativeTimestamp(yesterday, now, formatters)).toBe('Yesterday');
  });

  it('formats earlier dates in the same year as short month/day', () => {
    const earlier = new Date(2026, 10, 10, 12, 0, 0).getTime();
    expect(formatRelativeTimestamp(earlier, now, formatters)).toBe('Nov 10');
  });

  it('formats previous-year dates as ISO YYYY-MM-DD', () => {
    const previousYear = new Date(2025, 0, 5, 12, 0, 0).getTime();
    expect(formatRelativeTimestamp(previousYear, now, formatters)).toBe('2025-01-05');
  });

  it('returns empty string for invalid timestamps', () => {
    expect(formatRelativeTimestamp(0, now, formatters)).toBe('');
    expect(formatRelativeTimestamp(Number.NaN, now, formatters)).toBe('');
  });
});

describe('formatCompactAge', () => {
  const now = Date.UTC(2026, 10, 26, 14, 30, 0);

  it('formats minutes, hours, and days concisely', () => {
    expect(formatCompactAge(now - 10_000, now)).toBe('Just now');
    expect(formatCompactAge(now - 12 * 60_000, now)).toBe('12m');
    expect(formatCompactAge(now - 4 * 3_600_000, now)).toBe('4h');
    expect(formatCompactAge(now - 3 * 86_400_000, now)).toBe('3d');
    expect(formatCompactAge(0, now)).toBe('');
  });
});
