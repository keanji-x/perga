import { z } from 'zod';
import { agentSessionDtoSchema, MAX_ATTACHMENTS_PER_MESSAGE, multipartMessagePayloadSchema, sessionMessageReceiptSchema } from '../api/perga-v1.js';
import { imageMessageBody, type MultipartFile } from '../api/multipart.js';

import type { ApiFailure, ApiOperation } from '../api/types.js';
import { classifyFailure, failureReason, type FailureTable, type WriteFailure } from './failure-class.js';
import type { PendingQueueEntry } from './conversation.js';

/** What steering a queued message answers, including where the text landed. */
export function steerQueuedSessionMessageOperation(sessionId: string, inputId: string, turnId: string) {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/messages`,
    body: { mode: 'steer', messageId: inputId, turnId },
    responseSchema: sessionMessageReceiptSchema,
  } satisfies ApiOperation<unknown>;
}

export function removeQueuedSessionMessageOperation(sessionId: string, inputId: string) {
  return {
    method: 'DELETE',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/messages/${encodeURIComponent(inputId)}`,
    responseSchema: z.undefined(),
  } satisfies ApiOperation<unknown>;
}

export type SentPlannerInput = Readonly<{
  card_id: string;
  worker_session_id: string;
  /** The queue entry the text is now sitting in, or `null` when it folded into an entry that has no id. */
  entry_id: string | null;
  queued?: boolean | undefined;
  delivery?: string;
  turnId?: string;
}>;

/**
 * One send, keyed: the same `idempotencyKey` on a retry is answered with the first request's answer
 * and queues nothing, so a send whose answer was lost can be retried safely.
 */
export function sendPlannerInputOperation(
  cardId: string,
  text: string,
  attachments: readonly MultipartFile[],
  idempotencyKey: string,
  steerIfRunning = false,
  queueIfBusy = false,
  expectedEpoch?: number,
): ApiOperation<SentPlannerInput> {
  const payload = { content: text, mode: steerIfRunning ? 'steer' : queueIfBusy ? 'queue' : 'default',
    ...(expectedEpoch === undefined ? {} : { expectedEpoch }) } satisfies z.input<typeof multipartMessagePayloadSchema>;
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/messages`,
    timeoutMs: 10 * 60_000,
    body: imageMessageBody(payload, attachments),
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: sessionMessageReceiptSchema.refine(receipt => receipt.sessionId === cardId).transform(receipt => ({
      card_id: receipt.sessionId, worker_session_id: receipt.sessionId, entry_id: receipt.messageId, delivery: receipt.delivery,
      ...(receipt.turnId == null ? {} : { turnId: receipt.turnId }), queued: receipt.delivery === 'queued',
    })),
  };
}

export function recoverSessionOperation(sessionId: string, mode: 'reconnect' | 'reset_context') {
  return { method: 'POST', path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/control`,
    body: { action: mode }, timeoutMs: 30_000,
    responseSchema: agentSessionDtoSchema.refine(session => session.id === sessionId),
  } satisfies ApiOperation<unknown>;
}

export const TOO_MANY_IMAGES = `A message can carry at most ${MAX_ATTACHMENTS_PER_MESSAGE} images.`;
export const TOO_MANY_IMAGE_BYTES = 'Images in one message must total at most 25 MiB.';

export type PlannerQueueEditOutcome =
  | Readonly<{ kind: 'done'; entry: PendingQueueEntry }>
  | Exclude<PlannerQueueWriteOutcome, { kind: 'done' }>;

const plannerInputStaleSchema = z.object({ text: z.string(), rev: z.number(), entry_id: z.string() });

/**
 * What one write to the pending queue turned into. `stale` and `gone` call for opposite next
 * moves: retry against the quoted revision, versus stop, the message is on its way.
 */
export type PlannerQueueWriteOutcome =
  | Readonly<{ kind: 'done' }>
  | Readonly<{ kind: 'stale'; entryId: string; text: string; rev: number }>
  | Readonly<{ kind: 'gone' }>
  | Readonly<{ kind: 'not_running' }>
  | Readonly<{ kind: 'unanswered' }>
  | Readonly<{ kind: 'failed'; message: string }>;

/**
 * What a failed delete or steer of a queued message means. `stale`: a lost compare-and-swap, whose
 * body quotes the text and revision the entry actually holds. `gone`: the queue drained or somebody
 * else deleted the entry, beyond editing. The steer's own 409s: `not_running`, no turn took the
 * message and it is still queued unchanged; `unanswered`, codex never answered, so "nothing
 * happened" cannot be claimed.
 */
export const PLANNER_QUEUE_WRITE_FAILURES: FailureTable<Exclude<PlannerQueueWriteOutcome['kind'], 'done'>> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'planner_input_stale', is: 'stale' as const }),
    Object.freeze({ status: Object.freeze([404]), is: 'gone' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'planner_steer_no_running_turn', is: 'not_running' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'planner_steer_unknown_outcome', is: 'unanswered' as const }),
  ]),
  unauthorized: 'failed',
  otherwise: 'failed',
});

/**
 * Classifies a rejected queue write. Never called for a success. `failed` shows the server's own
 * sentence when it gave one, else `fallback`; a `stale` whose body is unreadable is `failed` too.
 */
export function plannerQueueWriteFailure(
  failure: ApiFailure | null, fallback: string,
): Exclude<PlannerQueueWriteOutcome, { kind: 'done' }> {
  const kind = classifyFailure(failure, PLANNER_QUEUE_WRITE_FAILURES);
  const stale = kind === 'stale' && failure?.kind === 'http' ? plannerInputStaleSchema.safeParse(failure.body) : null;
  if (stale?.success === true) return { kind: 'stale', entryId: stale.data.entry_id, text: stale.data.text, rev: stale.data.rev };
  if (kind === 'failed' || kind === 'stale') {
    return { kind: 'failed', message: failure?.kind === 'http' && failure.message !== '' ? failureReason(failure) : fallback };
  }
  return { kind };
}

export const COMPACT_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 404, 409, 422]), is: 'refused' as const })]),
  unauthorized: 'refused', otherwise: 'unknown',
});
export const COMPACT_TEXT = Object.freeze({ refused: 'Context was not compacted.', unknown: 'Context compaction is unconfirmed. Refresh before trying again.' });

export function compactPlannerOperation(cardId: string): ApiOperation<{ started: boolean }> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/control`,
    body: { action: 'compact' },
    timeoutMs: 10 * 60_000,
    responseSchema: agentSessionDtoSchema.transform(() => ({ started: true })),
  };
}

export function interruptPlannerOperation(cardId: string, worker?: { epoch?: number; turnId: string }): ApiOperation<{ stopped: boolean }> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/control`,
    body: { action: 'stop', turnId: worker?.turnId, expectedEpoch: worker?.epoch },
    responseSchema: agentSessionDtoSchema.transform(() => ({ stopped: true })),
  };
}
