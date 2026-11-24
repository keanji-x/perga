import { describe, expect, it } from 'vitest';
import { anchorRunningTurn, confirmedTurnStatus, formatTurnTimestamp, turnCompletedClock } from './conversation-meta.js';

describe('anchorRunningTurn', () => {
  it('anchors the start at the receipt minus the elapsed time', () => {
    expect(anchorRunningTurn(null, { turn_id: 't1', elapsed_ms: 18_000 }, 100_000))
      .toEqual({ turnId: 't1', startMs: 82_000 });
  });

  it('keeps the earlier anchor when a later receipt of the same turn would move it later', () => {
    const first = anchorRunningTurn(null, { turn_id: 't1', elapsed_ms: 18_000 }, 100_000);
    // Two seconds of server time, but the response took three to arrive: the estimate shifts later.
    const later = anchorRunningTurn(first, { turn_id: 't1', elapsed_ms: 20_000 }, 103_000);
    expect(later).toBe(first);
  });

  it('takes an earlier anchor for the same turn, so the clock never runs backwards', () => {
    const first = anchorRunningTurn(null, { turn_id: 't1', elapsed_ms: 18_000 }, 100_000);
    expect(anchorRunningTurn(first, { turn_id: 't1', elapsed_ms: 21_000 }, 102_000))
      .toEqual({ turnId: 't1', startMs: 81_000 });
  });

  it('re-anchors on a new turn, even a later one', () => {
    const first = anchorRunningTurn(null, { turn_id: 't1', elapsed_ms: 18_000 }, 100_000);
    expect(anchorRunningTurn(first, { turn_id: 't2', elapsed_ms: 1_000 }, 200_000))
      .toEqual({ turnId: 't2', startMs: 199_000 });
  });

  it('clears when no turn is running', () => {
    const first = anchorRunningTurn(null, { turn_id: 't1', elapsed_ms: 18_000 }, 100_000);
    expect(anchorRunningTurn(first, null, 101_000)).toBeNull();
  });
});

describe('turnCompletedClock', () => {
  it('returns both duration and completion timestamp for confirmed terminal turns', () => {
    for (const status of ['completed', 'interrupted', 'failed'] as const) {
      expect(confirmedTurnStatus(status)).toBe(status);
      expect(turnCompletedClock({ status, startedAtMs: 10_000, completedAtMs: 93_000 })).toEqual({
        elapsedMs: 83_000,
        timestamp: { kind: 'finished', atMs: 93_000 },
      });
    }
  });

  it('omits duration when startedAtMs is missing while preserving recorded completion time', () => {
    expect(turnCompletedClock({ status: 'interrupted', startedAtMs: null, completedAtMs: 93_000 })).toEqual({
      elapsedMs: null,
      timestamp: { kind: 'finished', atMs: 93_000 },
    });
  });

  it('never fabricates completion timestamps for running, queued, unknown, or missing-end turns', () => {
    expect(turnCompletedClock({ status: 'running', startedAtMs: 10_000, completedAtMs: 93_000 })).toEqual({
      elapsedMs: null, timestamp: null,
    });
    expect(turnCompletedClock({ status: 'queued', startedAtMs: null, completedAtMs: 93_000 })).toEqual({
      elapsedMs: null, timestamp: null,
    });
    // Boot recovery can stamp completedAtMs on an unknown turn; UI must still suppress it.
    expect(turnCompletedClock({ status: 'unknown', startedAtMs: 10_000, completedAtMs: 93_000 })).toEqual({
      elapsedMs: null, timestamp: null,
    });
    expect(turnCompletedClock({ status: 'completed', startedAtMs: 10_000, completedAtMs: null })).toEqual({
      elapsedMs: null, timestamp: null,
    });
    expect(turnCompletedClock({ status: 'completed', startedAtMs: 10_000, completedAtMs: Number.NaN })).toEqual({
      elapsedMs: null, timestamp: null,
    });
  });
});

describe('formatTurnTimestamp', () => {
  it('formats full local date, seconds, and explicit UTC offset and distinguishes cross-day instants', () => {
    const dayOne = Date.UTC(2026, 10, 24, 6, 32, 8);
    const dayTwo = Date.UTC(2026, 10, 25, 6, 32, 8);
    const first = formatTurnTimestamp(dayOne);
    const second = formatTurnTimestamp(dayTwo);
    expect(first).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC[+-]\d{2}:\d{2}\)$/);
    expect(second).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \(UTC[+-]\d{2}:\d{2}\)$/);
    expect(first?.slice(0, 10)).not.toBe(second?.slice(0, 10));
    expect(formatTurnTimestamp(Number.NaN)).toBeNull();
    expect(formatTurnTimestamp(Number.POSITIVE_INFINITY)).toBeNull();
  });
});
