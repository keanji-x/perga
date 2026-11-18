import { decodeWireEvent, type WireEvent } from '../api/schemas.js';
import type { ApiDecodeFailure } from '../api/types.js';

export type Topic = '*' | `area:${string}` | `track:${string}` | `card:${string}`;

export type EventSubscriptionFrame = Readonly<{ sub: readonly Topic[]; since: number }>;

export type EventMeta = Readonly<{ id: number; eventVersion: number }>;

export type EventFrame =
  | Readonly<{ type: 'event'; event: WireEvent; meta: EventMeta }>
  | Readonly<{ type: 'malformed-event'; id: number | null; eventVersion: number | null; error: ApiDecodeFailure }>
  | Readonly<{ type: 'replay-complete'; id: number | null }>
  | Readonly<{ type: 'snapshot-required'; tipId?: number }>;

export type EventFrameDecodeResult =
  | Readonly<{ status: 'ready'; frame: EventFrame }>
  | Readonly<{ status: 'failed'; error: ApiDecodeFailure }>;

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function normalizeLegacyEvent(input: Record<string, unknown>): Record<string, unknown> {
  if (input.ev === 'codex.job_requested') return { ...input, ev: 'codex.worker_requested' };
  if (input.ev === 'terminal.job_requested') return { ...input, ev: 'terminal.worker_requested' };
  return input;
}

export function eventSubscriptionFrame(
  topics: readonly Topic[],
  cursor: number | null,
): EventSubscriptionFrame {
  return { sub: topics, since: cursor ?? 0 };
}

export function decodeEventFrame(input: unknown): EventFrameDecodeResult {
  if (typeof input !== 'object' || input === null || Array.isArray(input)) {
    return {
      status: 'failed',
      error: { kind: 'decode', message: 'Event frame must be an object', cause: input },
    };
  }
  const envelope = input as Record<string, unknown>;
  // Perga /api/v1/events control frames (`kind: '_replay_complete'` with `tipId`, `kind: '_snapshot_required'`)
  // as well as legacy `ev` discriminators.
  if (envelope.kind === '_replay_complete' || envelope.ev === '_replay_complete') {
    return {
      status: 'ready',
      frame: { type: 'replay-complete', id: finiteNumber(envelope.tipId) ?? finiteNumber(envelope._id) },
    };
  }
  if (envelope.kind === '_snapshot_required' || envelope.ev === '_snapshot_required') {
    if ('tipId' in envelope) {
      const tipId = envelope.tipId;
      if (typeof tipId !== 'number' || !Number.isSafeInteger(tipId) || tipId < 0) {
        return { status: 'failed', error: { kind: 'decode', message: 'Invalid snapshot cursor', cause: tipId } };
      }
      return { status: 'ready', frame: { type: 'snapshot-required', tipId } };
    }
    return { status: 'ready', frame: { type: 'snapshot-required' } };
  }

  const normalized = normalizeLegacyEvent(envelope);
  const decoded = decodeWireEvent(normalized);
  const id = finiteNumber(envelope.eventId) ?? finiteNumber(envelope._id);
  const eventVersion = finiteNumber(envelope.eventVersion);
  if (decoded.status === 'failed') {
    return {
      status: 'ready',
      frame: { type: 'malformed-event', id, eventVersion, error: decoded.error },
    };
  }
  return {
    status: 'ready',
    frame: {
      type: 'event',
      event: decoded.value,
      meta: { id: id ?? 0, eventVersion: eventVersion ?? 0 },
    },
  };
}
