import { expect, it } from 'vitest';

import type { TranscriptEntry } from './conversation.js';
import { beginSendOp, outboxView, presentPendingMessages, queueDisplayAccounting, queueEntryIsAccounted, settleSendOp, wasUnknown, type SendOp } from './conversation-outbox.js';

const send = (key = 'key'): SendOp => ({ key, phase: 'sending', unknown: false, fromComposer: true,
  echo: { id: `echo-${key}`, author: 'you', text: 'Exact\n  message', atMs: 1, queued: false, localSend: true } });
const failed = (delivery: 'refused' | 'unknown'): SendOp => ({ ...send(), phase: 'failed', delivery, message: 'Failed' });
const view = (ops: readonly SendOp[], entries: readonly TranscriptEntry[] = []) => outboxView({ serverEntries: entries, stalled: false, ops, liveReplies: [] });

it('has only idle, sending and failed local state; a receipt removes the operation', () => {
  expect(view([])).toMatchObject({ sending: false, blocked: false, failed: null });
  const op = send();
  const sending = beginSendOp([], op)!;
  expect(view(sending)).toMatchObject({ sending: true, blocked: true, transcript: [op.echo] });
  expect(beginSendOp(sending, send('other'))).toBeNull();
  expect(view(settleSendOp(sending, op.key, null))).toMatchObject({ sending: false, blocked: false, transcript: [] });
});

it('retains the exact body and key across unknown retry and refuses a second send until recovery', () => {
  const held = failed('unknown');
  expect(wasUnknown(held)).toBe(true);
  expect(view([held]).failed).toBe(held);
  expect(beginSendOp([held], send('another'))).toBeNull();
  const retry: SendOp = { ...held, phase: 'sending', unknown: true };
  expect(beginSendOp([held], retry)).toEqual([retry]);
  expect(retry.echo).toBe(held.echo);
  expect(settleSendOp([], held.key, retry)).toEqual([]);
});

it('retains refused messages for explicit draft restore and clears them without tombstones', () => {
  const held = failed('refused');
  expect(wasUnknown(held)).toBe(false);
  expect(view([held])).toMatchObject({ blocked: true, failed: held });
  expect(settleSendOp([held], held.key, null)).toEqual([]);
  expect(beginSendOp([], send('new'))).toEqual([send('new')]);
});

it('does not claim queue acceptance for a send that has no receipt', () => {
  const op = { ...send(), echo: { ...send().echo, queued: true, waitForIdentity: true } };
  expect(view([op]).transcript).toEqual([]);
  const queue = [{ entry_id: 'server-message', text: 'Accepted', rev: 1, queued_at_ms: 1 }];
  expect(presentPendingMessages([], queue, [op])).toEqual({ transcript: [], queue, sending: [] });
});

it('keeps ordinary pre-receipt waiting feedback outside the thread and uses only server identities for queue accounting', () => {
  const op = { ...send(), echo: { ...send().echo, queued: true } };
  expect(presentPendingMessages(view([op]).transcript, [], [op])).toMatchObject({ transcript: [], sending: [{ delivery: 'sending' }] });
  const human: TranscriptEntry = { id: 'human:a', author: 'you', text: 'Accepted', atMs: 1,
    human: { userId: 'owner', displayName: 'Owner', messageId: 'a', revision: 2 } };
  const accounting = queueDisplayAccounting([op.echo, human], []);
  expect(accounting.displayed).toEqual([{ id: 'a', rev: 2 }]);
  expect(queueEntryIsAccounted(accounting, { entry_id: 'a', text: 'Accepted', rev: 2, queued_at_ms: 1 })).toBe(true);
  expect(queueEntryIsAccounted(accounting, { entry_id: 'a', text: 'New', rev: 3, queued_at_ms: 1 })).toBe(false);
});

it('completes a persisted running tool in place with the live result, preserving its place and identity', () => {
  const running: TranscriptEntry = { id: 'tool', author: 'activity', verb: 'Calling', target: 'tool', state: 'running', durationMs: null, detail: null, tool: 'tool', atMs: 1 };
  const done: TranscriptEntry = { ...running, verb: 'Called', state: 'done', content: 'Output\nresult', atMs: 2 };
  expect(outboxView({ serverEntries: [running], stalled: false, ops: [], liveReplies: [done] }).transcript)
    .toEqual([{ ...done, atMs: 1 }]);
});
