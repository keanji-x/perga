import type { PlannerRunningTurn } from './conversation.js';

/** Timing evidence supplied by the owner; null explicitly means unmeasured. */
export type ConversationMetaClock = Readonly<{
  elapsedMs: number | null;
  timestamp: Readonly<{ kind: 'finished' | 'paused'; atMs: number }> | null;
}>;

export type ConfirmedTurnStatus = 'completed' | 'interrupted' | 'failed';

export function confirmedTurnStatus(status: string): ConfirmedTurnStatus | undefined {
  return status === 'completed' || status === 'interrupted' || status === 'failed' ? status : undefined;
}

/** Only confirmed terminal turns with finite recorded timestamps produce completion clocks. */
export function turnCompletedClock(
  turn: Readonly<{ status: string; startedAtMs: number | null; completedAtMs?: number | null }>,
): ConversationMetaClock {
  const terminal = confirmedTurnStatus(turn.status);
  if (terminal === undefined) return { elapsedMs: null, timestamp: null };
  const completedAtMs = turn.completedAtMs != null && Number.isFinite(turn.completedAtMs) ? turn.completedAtMs : null;
  const startedAtMs = turn.startedAtMs != null && Number.isFinite(turn.startedAtMs) ? turn.startedAtMs : null;
  return {
    elapsedMs: startedAtMs !== null && completedAtMs !== null ? Math.max(0, completedAtMs - startedAtMs) : null,
    timestamp: completedAtMs !== null ? { kind: 'finished', atMs: completedAtMs } : null,
  };
}

/** Format a recorded instant in the user's local timezone with full date, seconds, and UTC offset at that instant. */
export function formatTurnTimestamp(atMs: number): string | null {
  if (!Number.isFinite(atMs)) return null;
  const date = new Date(atMs);
  if (Number.isNaN(date.getTime())) return null;
  const pad = (value: number) => String(value).padStart(2, '0');
  const year = String(date.getFullYear()).padStart(4, '0');
  const month = pad(date.getMonth() + 1);
  const day = pad(date.getDate());
  const hours = pad(date.getHours());
  const minutes = pad(date.getMinutes());
  const seconds = pad(date.getSeconds());
  const offsetMinutes = -date.getTimezoneOffset();
  const sign = offsetMinutes >= 0 ? '+' : '-';
  const absOffset = Math.abs(offsetMinutes);
  const offsetHours = pad(Math.floor(absOffset / 60));
  const offsetMins = pad(absOffset % 60);
  return `${year}-${month}-${day} ${hours}:${minutes}:${seconds} (UTC${sign}${offsetHours}:${offsetMins})`;
}

/** Where a running turn's clock starts, on this client's clock. */
export type RunningTurnAnchor = Readonly<{ turnId: string; startMs: number }>;

/**
 * Fold one run response into the running clock's anchor: `start = receivedAtMs - elapsed_ms`.
 * The same turn keeps the EARLIER anchor (returning `previous` itself), so a refetch whose network
 * delay shifts the estimate later never moves the clock backwards; a new turn re-anchors and `null`
 * clears it.
 */
export function anchorRunningTurn(
  previous: RunningTurnAnchor | null, running: PlannerRunningTurn | null, receivedAtMs: number,
): RunningTurnAnchor | null {
  if (running === null) return null;
  const startMs = receivedAtMs - running.elapsed_ms;
  if (previous !== null && previous.turnId === running.turn_id && previous.startMs <= startMs) return previous;
  return { turnId: running.turn_id, startMs };
}
