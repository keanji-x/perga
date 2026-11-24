import { expect, it, vi } from 'vitest';
import { createConversationOwner } from './registry-owner.ts';
import type { ConversationDraft } from './registry-model.ts';
import type { Conversation } from '../../../../core/domain/conversation.ts';

import { EMPTY_COMPOSER, isComposerEmpty } from '../../../../core/domain/conversation-composer.ts';
import { createStorageKey } from '../../../../core/keys/storage.ts';
import { createMemoryStorage } from '../router/share-reuse-fixture.ts';

const card = 'opaque:card';
const draft: ConversationDraft = { scopeId: 'opaque:scope', provider: 'codex', model: { model: null, reasoning_effort: null },
  key: 'attempt', text: 'Hello', submitted: null, creating: false, error: null, remedy: null };
const conversation: Conversation = { id: card, trackId: 'track', title: null, kind: 'agent', isLead: false, provider: 'codex', state: 'idle', updatedAt: 1 };
it.each([false, true])('preserves unread first sends while queuing another track (consumed before recovery: %s)', (consumed) => {
  const storage = createMemoryStorage();
  const storageKey = createStorageKey('pending-track-initial-sends-v1');
  const saved = { first: { text: 'Saved message', key: 'first:init' } };
  const queued = { text: 'New message', key: 'second:init' };
  storage.setItem(storageKey, JSON.stringify(saved));
  const getItem = storage.getItem.bind(storage);
  const read = vi.spyOn(storage, 'getItem').mockImplementation(() => { throw new Error('Storage unavailable'); });
  const owner = createConversationOwner(() => storage);
  const registry = owner.commands;
  expect(registry.queueTrackInitialSend('second', queued.text, queued.key)).toBe(false);
  expect(owner.getSnapshot().pendingTrackInitialSends).toEqual({ second: queued });
  expect(getItem(storageKey)).toBe(JSON.stringify(saved));

  if (consumed) {
    registry.queueTrackInitialSend('first', saved.first.text, saved.first.key);
    registry.consumeTrackInitialSend('first');
    expect(getItem(storageKey)).toBe(JSON.stringify(saved));
  }
  read.mockRestore();
  expect(registry.hydratePendingTrackInitialSends()).toBe(true);
  const recovered = consumed ? { second: queued } : { ...saved, second: queued };
  expect(owner.getSnapshot().pendingTrackInitialSends).toEqual(recovered);
  expect(storage.getItem(storageKey)).toBe(JSON.stringify(recovered));
});

it('retries a failed initial-send write so a fresh owner can restore the entry', () => {
  const storage = createMemoryStorage();
  const storageKey = createStorageKey('pending-track-initial-sends-v1');
  const { commands } = createConversationOwner(() => storage);
  vi.spyOn(storage, 'setItem').mockImplementationOnce(() => { throw new Error('Storage unavailable'); });

  expect(commands.queueTrackInitialSend('track', 'First message', 'create:init')).toBe(false);
  expect(storage.getItem(storageKey)).toBeNull();
  expect(commands.hydratePendingTrackInitialSends()).toBe(true);
  expect(createConversationOwner(() => storage).getSnapshot().pendingTrackInitialSends).toEqual({
    track: { text: 'First message', key: 'create:init' },
  });
});

it('retries a failed write of the merged initial-send queue after hydration', () => {
  const storage = createMemoryStorage();
  const storageKey = createStorageKey('pending-track-initial-sends-v1');
  const saved = { first: { text: 'Saved message', key: 'first:init' } };
  storage.setItem(storageKey, JSON.stringify(saved));
  const read = vi.spyOn(storage, 'getItem').mockImplementation(() => { throw new Error('Storage unreadable'); });
  const owner = createConversationOwner(() => storage);
  expect(owner.commands.queueTrackInitialSend('second', 'New message', 'second:init')).toBe(false);
  read.mockRestore();
  vi.spyOn(storage, 'setItem').mockImplementationOnce(() => { throw new Error('Storage unavailable'); });

  const merged = { ...saved, second: { text: 'New message', key: 'second:init' } };
  expect(owner.commands.hydratePendingTrackInitialSends()).toBe(false);
  expect(owner.getSnapshot().pendingTrackInitialSends).toEqual(merged);
  expect(storage.getItem(storageKey)).toBe(JSON.stringify(saved));
  expect(owner.commands.hydratePendingTrackInitialSends()).toBe(true);
  expect(createConversationOwner(() => storage).getSnapshot().pendingTrackInitialSends).toEqual(merged);
});

it('treats malformed initial-send JSON as an empty queue', () => {
  const storage = createMemoryStorage();
  const storageKey = createStorageKey('pending-track-initial-sends-v1');
  storage.setItem(storageKey, '{');
  const registry = createConversationOwner(() => storage).commands;
  expect(registry.queueTrackInitialSend('track', 'New message', 'create:init')).toBe(true);
  expect(storage.getItem(storageKey)).toBe(JSON.stringify({ track: { text: 'New message', key: 'create:init' } }));
});
it('notifies only changed selected facts and preserves no-op, empty and summary snapshots', () => {
  const owner = createConversationOwner();
  const registry = owner.commands;
  const text = vi.fn(); const other = vi.fn(); const attachmentError = vi.fn(); const empty = vi.fn(); const summary = vi.fn();
  owner.subscribe(state => state.composers[card]?.text ?? '', text);
  owner.subscribe(state => state.composers.other?.text ?? '', other);
  owner.subscribe(state => state.composers[card]?.attachmentError, attachmentError);
  owner.subscribe(state => isComposerEmpty(state.composers[card] ?? EMPTY_COMPOSER), empty);
  owner.subscribe(state => state.conversations, summary);
  registry.editComposer(card, current => ({ ...current, text: 'First' }));
  registry.editComposer(card, current => ({ ...current, text: 'First words' }));
  expect(text).toHaveBeenCalledTimes(2); expect(empty).toHaveBeenCalledOnce();
  expect(other).not.toHaveBeenCalled(); expect(attachmentError).not.toHaveBeenCalled(); expect(summary).not.toHaveBeenCalled();
  const before = owner.getSnapshot();
  registry.editComposer(card, current => current); registry.nextRead();
  expect(owner.getSnapshot()).toBe(before);
  registry.remember(conversation, []);
  const rows = owner.getSnapshot().conversations;
  registry.remember({ ...conversation }, [{ id: 'message', author: 'agent', text: 'Reply', atMs: 2 }]);
  expect(owner.getSnapshot().conversations).toBe(rows);
  expect(summary).toHaveBeenCalledOnce();
  const turns = registry.rememberedTurnsOf(card);
  registry.remember({ ...conversation, title: 'Named' }, [...turns]);
  expect(registry.rememberedTurnsOf(card)).toBe(turns);
  expect(summary).toHaveBeenCalledTimes(2);
  registry.editComposer(card, current => ({ ...current, text: ' \n ' }));
  expect(empty).toHaveBeenCalledTimes(2);
});

it('preserves explicit repeated open intents and exact idempotent unsubscribe', () => {
  const owner = createConversationOwner(); const registry = owner.commands;
  const observe = vi.fn(); const unsubscribe = owner.subscribe(state => state.openRequest, observe);
  registry.requestOpen(card, { focusComposer: true });
  const first = owner.getSnapshot().openRequest;
  registry.requestOpen(card, { focusComposer: true });
  expect(owner.getSnapshot().openRequest).toEqual(first); expect(owner.getSnapshot().openRequest).not.toBe(first);
  expect(observe).toHaveBeenCalledTimes(2);
  unsubscribe(); unsubscribe();
  registry.clearOpenRequest();
  expect(observe).toHaveBeenCalledTimes(2);
});

it('does not call an observer removed by an earlier callback in the same publication', () => {
  const owner = createConversationOwner();
  const removed = vi.fn();
  owner.subscribe(state => state.composers[card]?.text, () => { unsubscribe(); });
  const unsubscribe = owner.subscribe(state => state.composers[card]?.text, removed);
  owner.commands.editComposer(card, current => ({ ...current, text: 'Changed' }));
  expect(removed).not.toHaveBeenCalled();
});

it('baselines an observer added during publication at current state and notifies its next change', () => {
  const owner = createConversationOwner();
  const added = vi.fn();
  let subscribed = false;
  owner.subscribe(state => state.composers[card]?.text, () => {
    if (subscribed) return;
    subscribed = true;
    owner.subscribe(state => state.composers[card]?.text, added);
  });
  owner.commands.editComposer(card, current => ({ ...current, text: 'First' }));
  expect(added).not.toHaveBeenCalled();
  owner.commands.editComposer(card, current => ({ ...current, text: 'Second' }));
  expect(added).toHaveBeenCalledOnce();
});

it('reads a coherent latest state when an earlier observer completes a reentrant command', () => {
  const owner = createConversationOwner(); const registry = owner.commands;
  const observations: unknown[] = [];
  owner.subscribe(state => state.composers[card]?.text, () => {
    if (registry.composerOf(card).text === 'First') registry.refillComposer(card, { text: 'Refill', attachments: [] });
  });
  owner.subscribe(state => state, () => {
    observations.push(registry.composerOf(card));
  });
  registry.editComposer(card, current => ({ ...current, text: 'First' }));
  expect(observations).toEqual([{ text: 'First\n\nRefill', attachments: [] }]);
});

it('publishes accepted delivery and adoption together; stale keys and exceptions publish nothing', () => {
  const owner = createConversationOwner(); const registry = owner.commands;
  const submitted = { text: 'Hello', provider: 'codex', model: { model: null, reasoning_effort: null }, atMs: 2 } as const;
  const receipt = { inputId: 'first', sessionId: card, turnId: 'turn', deliveryState: 'accepted', turnStatus: 'queued' } as const;
  registry.startDraft(draft);
  const observations: unknown[] = [];
  owner.subscribe(state => state, () => { observations.push({ held: registry.draftOf(draft.scopeId), adopted: registry.adoptedDraftIdOf(draft.scopeId), delivery: registry.outboxOf(card) }); });
  const before = owner.getSnapshot();
  expect(() => registry.editDraft(draft, () => { throw new Error('bad updater'); })).toThrow('bad updater');
  expect(registry.acceptDraft({ ...draft, key: 'older' }, { conversation, sessionInput: receipt }, submitted)).toBe(false);
  expect(owner.getSnapshot()).toBe(before); expect(observations).toEqual([]);
  expect(registry.acceptDraft(draft, { conversation, sessionInput: receipt }, submitted)).toBe(true);
  expect(observations).toHaveLength(1);
  expect(observations[0]).toMatchObject({ held: null, adopted: card, delivery: [] });
});

it('keeps queue acquisition after its microtask, notifies only busy changes and fences synchronous restarts', async () => {
  const owner = createConversationOwner(); const registry = owner.commands;
  const busy = vi.fn(); owner.subscribe(state => (state.queueWrites[card] ?? 0) > 0, busy);
  let releaseFirst!: () => void; let releaseSecond!: () => void;
  const first = registry.holdQueueWrite(card, () => new Promise<void>(resolve => { releaseFirst = resolve; }));
  const second = registry.holdQueueWrite(card, () => new Promise<void>(resolve => { releaseSecond = resolve; }));
  expect(registry.queueWriteOutOf(card)).toBe(false);
  await Promise.resolve();
  expect(registry.queueWriteOutOf(card)).toBe(true); expect(busy).toHaveBeenCalledOnce();
  releaseFirst(); await first;
  expect(registry.queueWriteOutOf(card)).toBe(true); expect(busy).toHaveBeenCalledOnce();
  releaseSecond(); await second;
  expect(registry.queueWriteOutOf(card)).toBe(false); expect(busy).toHaveBeenCalledTimes(2);
  let releaseRestart!: () => void;
  const restart = registry.holdRestart(card, () => new Promise<void>(resolve => { releaseRestart = resolve; }));
  expect(registry.restartOutOf(card)).toBe(true);
  expect(registry.holdRestart(card, async () => {})).toBeNull();
  await Promise.resolve(); releaseRestart(); await restart;
  expect(registry.restartOutOf(card)).toBe(false);
});

it('retains Keep key and immutable CAS through response loss, remount and card switch', () => {
  const owner = createConversationOwner();
  const registry = owner.commands;
  const args = { carrier_id: 'carrier', submission_id: 'submission', attempt_id: 'attempt', revision: '1' };
  const first = registry.beginResolution(card, 'keep', args, 'key');
  expect(first?.key).toBe('key');
  expect(registry.beginResolution(card, 'keep', args, 'new-key')).toBeNull();
  registry.finishResolution(card, 'key', 'unknown');
  registry.beginResolution('other-card', 'keep', { ...args, carrier_id: 'other' }, 'other-key');
  const retry = registry.beginResolution(card, 'keep', { ...args, revision: '2' }, 'discarded-key');
  expect(retry?.key).toBe('key');
  expect(retry?.args).toEqual(args);
  registry.finishResolution(card, 'key', 'done');
  expect(registry.beginResolution(card, 'keep', { ...args, revision: '2' }, 'fresh')?.key).toBe('fresh');
});


it('retry response loss reuses the original decision key', () => {
  const registry = createConversationOwner().commands;
  const args = { carrier_id:'carrier', submission_id:'submission', attempt_id:'attempt', revision:'7' };
  expect(registry.beginResolution(card, 'retry', args, 'original')).toMatchObject({decision:'retry', key:'original', args});
  registry.finishResolution(card, 'original', 'unknown');
  expect(registry.beginResolution(card, 'retry', {...args, revision:'8'}, 'discarded')).toMatchObject({decision:'retry',key:'original',args});
});
it('keep and retry share one attempt intent', () => {
  const registry = createConversationOwner().commands;
  const args = { carrier_id:'carrier', submission_id:'submission', attempt_id:'attempt', revision:'7' };
  registry.beginResolution(card, 'retry', args, 'original');
  expect(registry.beginResolution(card, 'keep', args, 'competing')).toBeNull();
  registry.finishResolution(card, 'original', 'unknown');
  expect(registry.beginResolution(card, 'keep', args, 'competing')).toMatchObject({decision:'retry',key:'original'});
  registry.finishResolution(card, 'original', 'done');
  expect(registry.beginResolution(card, 'keep', {...args,revision:'8'}, 'fresh')).toMatchObject({decision:'keep',key:'fresh'});
});

it('retains the three-generation permission barrier until every real write settles', async () => {
  const registry = createConversationOwner().commands;
  let fail!: (error: Error) => void;
  let finish!: () => void;
  const first = registry.holdPermissionWrite('a', new Promise<void>((_resolve, reject) => { fail = reject; }));
  void first.catch(() => undefined);
  const second = registry.holdPermissionWrite('a', new Promise<void>(resolve => { finish = resolve; }));
  void second.catch(() => undefined);
  const third = registry.holdPermissionWrite('a', Promise.resolve());
  void third.catch(() => undefined);
  fail(new Error('first failed'));
  for (let i = 0; i < 10; i++) await Promise.resolve();
  expect(registry.permissionWriteOf('a')).toBeDefined();
  finish();
  await Promise.allSettled([first, second, third]);
  await Promise.resolve();
  expect(registry.permissionWriteOf('a')).toBeUndefined();
});

it('revokes all previews for a permanently deleted Track before notifying readers', () => {
  const revoke = vi.fn();
  const original = Object.getOwnPropertyDescriptor(URL, 'revokeObjectURL');
  Object.defineProperty(URL, 'revokeObjectURL', { value: revoke, configurable: true });
  try {
    const owner = createConversationOwner();
    const registry = owner.commands;
    const local = (id: string) => ({ id, contentType: 'image/png', size: 1, url: `blob:${id}`,
      file: new File(['x'], `${id}.png`, { type: 'image/png' }) });
    const compose = local('composer');
    const outbox = local('outbox');
    const side = local('side-draft');
    const elsewhere = local('another-track');
    registry.remember(conversation, []);
    registry.bindConversation('another-session', conversation.trackId);
    registry.bindConversation('retained-session', 'retained-track');
    registry.bindDraftScope(draft.scopeId, conversation.trackId);
    registry.startDraft({ ...draft, attachments: [side] });
    registry.editComposer(card, current => ({ ...current, attachments: [compose] }));
    registry.editComposer('another-session', current => ({ ...current, attachments: [outbox] }));
    registry.editComposer('retained-session', current => ({ ...current, attachments: [elsewhere] }));
    const op = { key: 'key', fromComposer: true, phase: 'sending', unknown: false,
      echo: { id: 'echo', author: 'you', text: '', atMs: 0, queued: false, localSend: true, attachments: [outbox] } } as const;
    expect(registry.beginSend('another-session', op)).toBe(true);
    expect(revoke).not.toHaveBeenCalled();
    const generation = registry.sendGenerationOf('another-session');
    const observer = vi.fn(() => expect(revoke).toHaveBeenCalledTimes(3));
    owner.subscribe(snapshot => snapshot.outboxes, observer);

    registry.retireTrack(conversation.trackId);

    expect(observer).toHaveBeenCalledOnce();
    expect(revoke.mock.calls.flat().sort()).toEqual([compose.url, outbox.url, side.url].sort());
    expect(registry.composerOf(card).attachments).toEqual([]);
    expect(registry.outboxOf('another-session')).toEqual([]);
    expect(registry.draftOf(draft.scopeId)).toBeNull();
    expect(registry.composerOf('retained-session').attachments).toEqual([elsewhere]);
    expect(registry.sendGenerationOf('another-session')).not.toBe(generation);
    expect(registry.beginSend('another-session', op)).toBe(false);
  } finally {
    if (original === undefined) Reflect.deleteProperty(URL, 'revokeObjectURL');
    else Object.defineProperty(URL, 'revokeObjectURL', original);
  }
});


it('transfers a created conversation’s preview to its accepted outbox row before revoking it', () => {
  const revoke = vi.fn();
  const original = Object.getOwnPropertyDescriptor(URL, 'revokeObjectURL');
  Object.defineProperty(URL, 'revokeObjectURL', { value: revoke, configurable: true });
  try {
    const registry = createConversationOwner().commands;
    const image = { id: 'local', contentType: 'image/png', size: 1, url: 'blob:first-image',
      file: new File(['x'], 'image.png', { type: 'image/png' }) };
    const submitted = { text: '', attachments: [image], atMs: 2 };
    const held = { ...draft, attachments: [image], submitted };
    const receipt = { inputId: 'first', sessionId: card, turnId: 'turn', deliveryState: 'accepted', turnStatus: 'queued' } as const;
    registry.startDraft(held);
    expect(registry.acceptDraft(held, { conversation, sessionInput: receipt }, submitted)).toBe(true);
    expect(revoke).not.toHaveBeenCalled();
    expect(registry.rememberedTurnsOf(card)).toEqual([]);
    expect(registry.outboxOf(card)).toMatchObject([{ key: held.key, phase: 'accepted',
      messageId: 'first', echo: { attachments: [image] } }]);
    registry.removeSend(card, held.key);
    expect(revoke).toHaveBeenCalledExactlyOnceWith(image.url);
  } finally {
    if (original === undefined) Reflect.deleteProperty(URL, 'revokeObjectURL');
    else Object.defineProperty(URL, 'revokeObjectURL', original);
  }
});
