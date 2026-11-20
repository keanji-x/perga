import { mergeTranscript, type ConversationActivity, type ConversationTurn, type OptimisticConversationTurn, type PendingQueueEntry, type TranscriptEntry } from './conversation.js';
import type { SendFailureKind } from './conversation-delivery.js';

import type { HarnessQueueEntryRef } from '../api/generated/wire.js';

/** A task submission keeps its attempt and delivery choice across retries. */
export type ConversationSendTarget = Readonly<{ attemptId: string; queue: boolean }>;

/** No operation means idle. A receipt ends local send state; session reads own accepted messages. */
export type SendOp = Readonly<{
  key: string;
  target?: ConversationSendTarget;
  echo: OptimisticConversationTurn;
  fromComposer: boolean;
} & (
  | { phase: 'sending'; unknown: boolean }
  | { phase: 'failed'; delivery: SendFailureKind; message: string }
)>;
export type ConversationOutboxOp = SendOp;
export type FailedSendOp = Extract<SendOp, { phase: 'failed' }>;

export function wasUnknown(op: SendOp): boolean {
  return op.phase === 'sending' ? op.unknown : op.delivery === 'unknown';
}

/** Only the held failure can be retried, under its original idempotency key. */
export function beginSendOp(ops: readonly SendOp[], op: SendOp): readonly SendOp[] | null {
  if (ops.some(held => held.phase === 'sending' || held.key !== op.key)) return null;
  return [op];
}

export function settleSendOp(ops: readonly SendOp[], key: string, next: SendOp | null): readonly SendOp[] {
  if (!ops.some(held => held.key === key)) return ops;
  return next === null ? ops.filter(held => held.key !== key) : ops.map(held => held.key === key ? next : held);
}

export type QueueDisplayAccounting = Readonly<{
  displayed: readonly Readonly<HarnessQueueEntryRef>[];
  suppressed: readonly Readonly<HarnessQueueEntryRef>[];
}>;

export function queueEntryIsAccounted(accounting: QueueDisplayAccounting, entry: PendingQueueEntry): boolean {
  return [...accounting.displayed, ...accounting.suppressed].some(ref => ref.id === entry.entry_id && ref.rev >= entry.rev);
}

/** Only a server message identity can account for a queued message. */
export function queueDisplayAccounting(entries: readonly TranscriptEntry[], suppressed: QueueDisplayAccounting['suppressed']): QueueDisplayAccounting {
  return { displayed: entries.flatMap(entry => {
    if ((entry.author === 'you' || entry.author === 'human') && entry.human !== undefined)
      return [{ id: entry.human.messageId, rev: entry.human.revision }];
    return [];
  }), suppressed };
}

export type OutboxView = Readonly<{
  transcript: readonly TranscriptEntry[];
  shown: readonly ConversationTurn[];
  sending: boolean;
  blocked: boolean;
  failed: FailedSendOp | null;
}>;
export type OutboxFacts = Omit<OutboxView, 'transcript'> & Readonly<{
  entries: readonly TranscriptEntry[];
  echoes: readonly ConversationTurn[];
}>;

export function outboxFacts({ serverEntries, stalled, ops }: {
  serverEntries: readonly TranscriptEntry[];
  stalled: boolean;
  ops: readonly SendOp[];
}): OutboxFacts {
  const failed = ops.find((op): op is FailedSendOp => op.phase === 'failed') ?? null;
  const sending = ops.some(op => op.phase === 'sending');
  // A lost answer cannot give an echo an accepted identity or promise queue delivery.
  const echoes = ops.filter(op => op.echo.waitForIdentity !== true || (op.phase === 'failed' && op.delivery === 'refused'))
    .map(op => ({ ...op.echo, queued: false }));
  return { entries: serverEntries, echoes, shown: echoes.filter(echo => failed?.echo.id !== echo.id),
    sending, blocked: stalled || sending || failed !== null, failed };
}

/** Complete a stored running tool in place when its live result arrives. */
export function withOutboxLiveReplies(facts: OutboxFacts, liveReplies: readonly TranscriptEntry[]): OutboxView {
  const { entries, echoes, ...state } = facts;
  const updates = new Map(liveReplies.flatMap(entry => entry.author === 'activity' && entry.state !== 'running'
    ? [[entry.id, entry] as const] : []));
  const merged = new Set<string>();
  const current = updates.size === 0 ? entries : entries.map(entry => {
    if (entry.author !== 'activity') return entry;
    const update: ConversationActivity | undefined = updates.get(entry.id);
    if (update === undefined) return entry;
    merged.add(entry.id);
    return entry.state === 'running' ? { ...entry, ...update, atMs: entry.atMs,
      target: update.target ?? entry.target, tool: update.tool ?? entry.tool } : entry;
  });
  return { ...state, transcript: mergeTranscript(mergeTranscript(current, liveReplies.filter(entry => !merged.has(entry.id))), echoes) };
}

export function outboxView(input: Parameters<typeof outboxFacts>[0] & { liveReplies: readonly TranscriptEntry[] }): OutboxView {
  return withOutboxLiveReplies(outboxFacts(input), input.liveReplies);
}

export type PendingQueueSend = ConversationTurn & Readonly<{ delivery: 'sending' | 'failed' }>;

/** Accepted queue entries belong to the run response. Only pre-receipt sends remain local. */
export function presentPendingMessages(entries: readonly TranscriptEntry[], queue: readonly PendingQueueEntry[], ops: readonly SendOp[]) {
  const waiting = ops.filter(op => op.echo.queued && op.echo.waitForIdentity !== true);
  const hidden = new Set(waiting.map(op => op.echo.id));
  return { transcript: entries.filter(entry => !hidden.has(entry.id)), queue,
    sending: waiting.map((op): PendingQueueSend => ({ ...op.echo, delivery: op.phase === 'sending' ? 'sending' : 'failed' })) };
}
