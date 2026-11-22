// Read receipts (#2596): the kernel keeps what the one user has read, so a refresh or a second
// device reads the same state. This module is the one unread rule and the receipt operations.

import type { z } from 'zod';
import { readReceiptDtoSchema, readReceiptsResponseDtoSchema } from '../api/perga-v1.js';
import type { NotifiableSessionWatermark } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import type { FailureTable, WriteClass, WriteText } from './failure-class.js';
import type { ReadReceipt as ReadReceiptWire, ReadReceiptKind as ReadReceiptKindWire, ReadReceipts as ReadReceiptsWire } from './presentation-types.js';

export type ReadReceiptKind = ReadReceiptKindWire;
export type ReadReceipt = ReadReceiptWire;

export const readReceiptSchema: z.ZodType<ReadReceipt> = readReceiptDtoSchema;
export const readReceiptsSchema: z.ZodType<ReadReceiptsWire> = readReceiptsResponseDtoSchema;

/**
 * The reader's view of the kernel's receipts: the cutover baseline, and each receipt under
 * {@link receiptKey}. Immutable: every change returns a new state.
 */
export type ReadState = Readonly<{ baselineMs: number; receipts: Readonly<Record<string, ReadReceipt>> }>;

export function receiptKey(kind: ReadReceiptKind, targetId: string): string {
  return `${kind}:${targetId}`;
}

export function toReadState(wire: ReadReceiptsWire): ReadState {
  const receipts: Record<string, ReadReceipt> = {};
  for (const receipt of wire.receipts) receipts[receiptKey(receipt.kind, receipt.targetId)] = receipt;
  return { baselineMs: wire.baselineMs, receipts };
}

/**
 * The unread rule: marked unread by hand, or activity after both the baseline and what was read.
 * `activityAt` is the target's completion high-water mark (a track's `activityAt`, a conversation's
 * `lastTurnCompletedAt`); `null` means nothing has completed, which is never unread by itself.
 */
export function isUnread(state: ReadState, kind: ReadReceiptKind, targetId: string, activityAt: number | null): boolean {
  const receipt = state.receipts[receiptKey(kind, targetId)];
  if (receipt?.manualUnread === true) return true;
  return activityAt !== null && activityAt > Math.max(state.baselineMs, receipt?.readThroughMs ?? 0);
}

/** The state after a mark read, as the kernel stores it: the larger instant, and no manual unread. */
export function withMarkedRead(state: ReadState, kind: ReadReceiptKind, targetId: string, readThroughMs: number): ReadState {
  const previous = state.receipts[receiptKey(kind, targetId)];
  return withReceipt(state, {
    kind, targetId, manualUnread: false,
    readThroughMs: Math.max(previous?.readThroughMs ?? 0, readThroughMs),
  });
}

/** The state after a mark unread, as the kernel stores it: the instant read through is kept. */
export function withMarkedUnread(state: ReadState, kind: ReadReceiptKind, targetId: string): ReadState {
  const previous = state.receipts[receiptKey(kind, targetId)];
  return withReceipt(state, { kind, targetId, manualUnread: true, readThroughMs: previous?.readThroughMs ?? 0 });
}

/** The state with one receipt replaced, such as the one a write answered. */
export function withReceipt(state: ReadState, receipt: ReadReceipt): ReadState {
  return { ...state, receipts: { ...state.receipts, [receiptKey(receipt.kind, receipt.targetId)]: receipt } };
}

export function readReceiptsOperation(): ApiOperation<ReadReceiptsWire> {
  return { method: 'GET', path: '/api/v1/read-receipts', responseSchema: readReceiptsSchema };
}

const receiptPath = (kind: ReadReceiptKind, targetId: string) =>
  `/api/v1/read-receipts/${kind}/${encodeURIComponent(targetId)}`;

/** Idempotent; the kernel keeps the larger instant and answers the stored receipt. */
export function markReadOperation(kind: ReadReceiptKind, targetId: string, readThroughMs: number): ApiOperation<ReadReceipt> {
  return { method: 'POST', path: `${receiptPath(kind, targetId)}/read`, body: { readThroughMs }, responseSchema: readReceiptSchema };
}

/** Idempotent; the next mark read clears it. */
export function markUnreadOperation(kind: ReadReceiptKind, targetId: string): ApiOperation<ReadReceipt> {
  return { method: 'POST', path: `${receiptPath(kind, targetId)}/unread`, responseSchema: readReceiptSchema };
}

/**
 * What a failed receipt write says. 400 (an unknown kind or instant), 403 (not the user) and 422 refuse it before
 * anything is stored; anything else may have stored it. Idempotent, so a retry is always safe.
 */
export const READ_RECEIPT_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([400, 403, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const MARK_UNREAD_TEXT: WriteText = Object.freeze({
  refused: 'The track was not marked unread.', unknown: 'Marking the track unread is unconfirmed.',
});

/** Track receipts only supply manual unread; output belongs to each eligible conversation. */
export function isTrackUnread(state: ReadState, trackId: string, notifiableSessions: readonly NotifiableSessionWatermark[]): boolean {
  return state.receipts[receiptKey('track', trackId)]?.manualUnread === true
    || notifiableSessions.some(s => isUnread(state, 'conversation', s.sessionId, s.lastNotifiableTurnAtMs));
}
