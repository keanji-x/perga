import { createStorageKey } from '../../../../core/keys/storage.ts';
import type { TranscriptEntry, Conversation } from '../../../../core/domain/conversation.ts';
import { DEPLOYMENT_OWNER_USER_ID } from '../../../../core/api/auth.ts';
import { isOptimisticConversationTurn, TOO_MANY_IMAGES } from '../../../../core/domain/conversation.ts';
import { beginSendOp, type ConversationOutboxOp } from '../../../../core/domain/conversation-outbox.ts';
import { EMPTY_COMPOSER, tookEveryImage, withRefill, type ComposerContent } from '../../../../core/domain/conversation-composer.ts';
import { NO_UPLOAD, type UploadState } from '../../features/planner/attachments.tsx';
import {
  moveDraft, slotHolds,
  type ConversationOpenRequest,
  type ConversationRegistry,
  type DraftSlots, type InputResolutionIntent, type RememberedConversation
} from './registry-model.ts';

export const NO_SENDS: readonly ConversationOutboxOp[] = Object.freeze([]);
export const NO_TURNS: readonly TranscriptEntry[] = Object.freeze([]);
const NO_CONVERSATIONS: readonly Conversation[] = Object.freeze([]);

type ById<T> = Readonly<Record<string, T>>;
type PendingTrackInitialSend = Readonly<{ text: string; key: string }>;

export type ConversationSnapshot = Readonly<{
  entries: ById<RememberedConversation>;
  conversations: readonly Conversation[];
  drafts: DraftSlots;
  openRequest: ConversationOpenRequest | null;
  pendingTrackInitialSends: ById<PendingTrackInitialSend>;
  outboxes: ById<readonly ConversationOutboxOp[]>;
  acceptedTurns: ById<string>;
  composers: ById<ComposerContent>;
  newComposers: ById<string>;
  uploads: ById<UploadState>;
  queueWrites: ById<number>;
  restarts: ById<true>;
  resolutions: ById<InputResolutionIntent>;
}>;

function put<T>(map: ById<T>, id: string, value: T | undefined): ById<T> {
  if (map[id] === value) return map;
  const next = { ...map };
  if (value === undefined) delete next[id]; else next[id] = value;
  return next;
}

function equalRecord(left: Readonly<Record<string, unknown>>, right: Readonly<Record<string, unknown>>): boolean {
  const keys = Object.keys(left);
  return keys.length === Object.keys(right).length && keys.every(key => left[key] === right[key]);
}

function sameTurns(left: readonly TranscriptEntry[], right: readonly TranscriptEntry[]): boolean {
  return left === right || left.length === right.length && left.every((turn, index) => equalRecord(turn, right[index]));
}

function withComposer(state: ConversationSnapshot, id: string, value: ComposerContent): ConversationSnapshot {
  const composers = put(state.composers, id, value.text === '' && value.attachments.length === 0 ? undefined : value);
  return composers === state.composers ? state : { ...state, composers };
}

function withUpload(state: ConversationSnapshot, id: string, value: UploadState): ConversationSnapshot {
  const uploads = put(state.uploads, id, value.inFlight === 0 && value.refusal === null ? undefined : value);
  return uploads === state.uploads ? state : { ...state, uploads };
}

function withOutbox(state: ConversationSnapshot, id: string, value: readonly ConversationOutboxOp[]): ConversationSnapshot {
  const outboxes = put(state.outboxes, id, value.length === 0 ? undefined : value);
  return outboxes === state.outboxes ? state : { ...state, outboxes };
}

function withRefilledComposer(state: ConversationSnapshot, id: string, refill: ComposerContent): ConversationSnapshot {
  const merged = withRefill(state.composers[id] ?? EMPTY_COMPOSER, refill);
  const next = withComposer(state, id, merged);
  return tookEveryImage(merged, refill) ? next
    : withUpload(next, id, { ...next.uploads[id] ?? NO_UPLOAD, refusal: TOO_MANY_IMAGES });
}

function isPendingTrackInitialSend(value: unknown): value is PendingTrackInitialSend {
  return typeof value === 'object' && value !== null
    && typeof (value as { text?: unknown }).text === 'string'
    && typeof (value as { key?: unknown }).key === 'string';
}

function readPendingTrackInitialSends(key: string, submittedStorage?: () => Storage): ById<PendingTrackInitialSend> {
  if (submittedStorage === undefined) return {};
  try {
    const raw = submittedStorage().getItem(key);
    if (raw === null) return {};
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return {};
    return Object.fromEntries(Object.entries(parsed).filter((entry): entry is [string, PendingTrackInitialSend] => isPendingTrackInitialSend(entry[1])));
  } catch {
    return {};
  }
}

function writePendingTrackInitialSends(key: string, map: ById<PendingTrackInitialSend>, submittedStorage?: () => Storage): void {
  if (submittedStorage === undefined) return;
  try {
    const storage = submittedStorage();
    if (Object.keys(map).length === 0) storage.removeItem(key);
    else storage.setItem(key, JSON.stringify(map));
  } catch { /* Ignore storage quota/availability failures. */ }
}

/** The only local conversation authority. Commands finish before any observer is notified. */
export function createConversationOwner(submittedStorage?: () => Storage) {
  const pendingInitialSendsKey = createStorageKey('pending-track-initial-sends-v1');
  let state: ConversationSnapshot = {
    entries: {}, conversations: NO_CONVERSATIONS, drafts: {}, openRequest: null, pendingTrackInitialSends: readPendingTrackInitialSends(pendingInitialSendsKey, submittedStorage), outboxes: {}, acceptedTurns: {},
    composers: {}, newComposers: {}, uploads: {},  queueWrites: {}, restarts: {}, resolutions: {},
  };
  const sendGenerations = new Map<string, number>();
  const epochs = new Map<string, number>();
  const permissionWrites = new Map<string, Promise<unknown>>();
  let read = 0;
  const listeners = new Set<() => void>();
  const commit = (next: ConversationSnapshot) => {
    if (next === state) return;
    state = next;
    for (const listener of listeners) listener();
  };
  const rememberEntry = (id: string, conversation: Conversation, given: readonly TranscriptEntry[]) => {
    const canonical = given.some(isOptimisticConversationTurn) ? given.filter(turn => !isOptimisticConversationTurn(turn)) : given;
    const previous = state.entries[id];
    const summary = previous !== undefined && equalRecord(previous.conversation, conversation) ? previous.conversation : conversation;
    const turns = previous !== undefined && sameTurns(previous.turns, canonical) ? previous.turns : canonical;
    if (previous?.conversation === summary && previous.turns === turns) return;
    const entries = put(state.entries, id, { conversation: summary, turns });
    // Canonical history changes do not invalidate summary/list readers.
    const conversations = previous?.conversation === summary ? state.conversations : Object.values(entries).map(entry => entry.conversation);
    commit({ ...state, entries, conversations });
  };
  const commands: ConversationRegistry = {
    permissionWriteOf: id => permissionWrites.get(id),
    holdPermissionWrite: (id, write) => {
      const previous = permissionWrites.get(id);
      // A rejected ancestor must not settle a later barrier while another real PUT is still out.
      const pending = previous === undefined ? write : Promise.allSettled([previous, write]).then(results => {
        for (const result of results) if (result.status === 'rejected') throw result.reason;
        return write;
      });
      permissionWrites.set(id, pending);
      const settled = () => { if (permissionWrites.get(id) === pending) permissionWrites.delete(id); };
      void Promise.allSettled([previous, write]).then(settled);
      return pending;
    },
    beginResolution: (id, decision, args, key) => {
      const slot = `${id}\u0000${args.attempt_id}`;
      const previous = state.resolutions[slot];
      if (previous?.status === 'sending') return null;
      const intent: InputResolutionIntent = Object.freeze({ decision: previous?.decision ?? decision, key: previous?.key ?? key,
        args: previous?.args ?? Object.freeze({ ...args }), status: 'sending' });
      commit({ ...state, resolutions: put(state.resolutions, slot, intent) });
      return intent;
    },
    finishResolution: (id, key, status) => {
      const slot = Object.keys(state.resolutions).find(slot => slot.startsWith(`${id}\u0000`) && state.resolutions[slot]?.key === key);
      if (slot === undefined) return;
      const intent = state.resolutions[slot];
      if (intent === undefined) return;
      commit({ ...state, resolutions: put(state.resolutions, slot, status === 'done' ? undefined : { ...intent, status }) });
    },
    rememberedTurnsOf: id => state.entries[id]?.turns ?? NO_TURNS,
    turnsOf: id => state.entries[id]?.turns ?? NO_TURNS,
    remember: (conversation, turns) => { rememberEntry(conversation.id, conversation, turns); },
    updateExisting: (id, amend) => {
      const entry = state.entries[id];
      if (entry === undefined) return;
      const next = amend(entry);
      rememberEntry(id, next.conversation, next.turns);
    },
    requestOpen: (id, options) => { commit({ ...state, openRequest: { id, focusComposer: options?.focusComposer ?? false } }); },
    clearOpenRequest: () => { if (state.openRequest !== null) commit({ ...state, openRequest: null }); },
    queueTrackInitialSend: (trackId, text, key) => {
      const pendingTrackInitialSends = put(state.pendingTrackInitialSends, trackId, { text, key });
      writePendingTrackInitialSends(pendingInitialSendsKey, pendingTrackInitialSends, submittedStorage);
      commit({ ...state, pendingTrackInitialSends });
    },
    consumeTrackInitialSend: (trackId) => {
      if (state.pendingTrackInitialSends[trackId] === undefined) return;
      const pendingTrackInitialSends = put(state.pendingTrackInitialSends, trackId, undefined);
      writePendingTrackInitialSends(pendingInitialSendsKey, pendingTrackInitialSends, submittedStorage);
      commit({ ...state, pendingTrackInitialSends });
    },
    draftOf: scope => { const slot = state.drafts[scope]; return slot?.kind === 'held' ? slot.draft : null; },
    startDraft: draft => { commit({ ...state, drafts: moveDraft(state.drafts, { kind: 'start', draft }) }); },
    editDraft: (from, next) => {
      const drafts = moveDraft(state.drafts, { kind: 'edit', from, next });
      if (drafts !== state.drafts) commit({ ...state, drafts });
    },
    acceptDraft: (from, result, submitted) => {
      if (!slotHolds(state.drafts[from.scopeId], from)) return false;
      const accepted = { conversation: result.conversation, turns: [{
        id: `human:${result.sessionInput.inputId}`, author: 'you' as const, text: submitted.text, atMs: submitted.atMs,
        human: { userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', messageId: result.sessionInput.inputId, revision: 1 },
      }] };
      const entries = put(state.entries, result.conversation.id, accepted);
      commit({ ...state, entries, conversations: Object.values(entries).map(entry => entry.conversation),
        acceptedTurns: put(state.acceptedTurns, result.conversation.id, result.sessionInput.turnId),
        drafts: moveDraft(state.drafts, { kind: 'adopt', from, conversationId: result.conversation.id }) });
      return true;
    },
    discardDraft: from => {
      const drafts = moveDraft(state.drafts, { kind: 'discard', from });
      if (drafts !== state.drafts) commit({ ...state, drafts });
    },
    discardUnsentDraft: scopeId => {
      const drafts = moveDraft(state.drafts, { kind: 'discard-unsent', scopeId });
      if (drafts !== state.drafts) commit({ ...state, drafts });
    },
    adoptedDraftIdOf: scope => { const slot = state.drafts[scope]; return slot?.kind === 'adopted' ? slot.conversationId : null; },
    finishDraftAdoption: (scopeId, conversationId) => {
      const drafts = moveDraft(state.drafts, { kind: 'finish-adoption', scopeId, conversationId });
      if (drafts !== state.drafts) commit({ ...state, drafts });
    },
    acceptedTurnOf: id => state.acceptedTurns[id] ?? null,
    rememberAcceptedTurn: (id, turnId) => { commit({ ...state, acceptedTurns: put(state.acceptedTurns, id, turnId ?? undefined) }); },
    sendGenerationOf: id => sendGenerations.get(id) ?? 0,
    invalidateSends: id => {
      sendGenerations.set(id, (sendGenerations.get(id) ?? 0) + 1);
      let next = state;
      for (const op of state.outboxes[id] ?? []) {
        if (op.fromComposer) next = withRefilledComposer(next, id, { text: op.echo.text, attachments: op.echo.attachments ?? [] });
      }
      commit({ ...withOutbox(next, id, NO_SENDS), acceptedTurns: put(next.acceptedTurns, id, undefined) });
    },
    observeEpoch: (id, epoch) => {
      const previous = epochs.get(id);
      if (previous !== undefined && epoch > previous) commands.invalidateSends(id);
      if (previous === undefined || epoch > previous) epochs.set(id, epoch);
    },
    outboxOf: id => state.outboxes[id] ?? NO_SENDS,
    beginSend: (id, op) => {
      const begun = beginSendOp(state.outboxes[id] ?? NO_SENDS, op);
      if (begun === null) return false;
      const next = withOutbox(state, id, begun);
      commit(next);
      return true;
    },
    editOutbox: (id, update) => {
      const before = state.outboxes[id] ?? NO_SENDS;
      const after = update(before);
      if (after !== before) commit(withOutbox(state, id, after));
      return after;
    },
    nextRead: () => ++read,
    composerOf: id => state.composers[id] ?? EMPTY_COMPOSER,
    editComposer: (id, update) => {
      const after = update(state.composers[id] ?? EMPTY_COMPOSER);
      commit(withComposer(state, id, after));
      return after;
    },
    refillComposer: (id, refill) => { commit(withRefilledComposer(state, id, refill)); },
    newConversationComposerOf: scope => state.newComposers[scope] ?? '',
    editNewConversationComposer: (scope, update) => {
      const value = update(state.newComposers[scope] ?? '');
      const newComposers = put(state.newComposers, scope, value === '' ? undefined : value);
      if (newComposers !== state.newComposers) commit({ ...state, newComposers });
    },
    uploadOf: id => state.uploads[id] ?? NO_UPLOAD,
    editUpload: (id, update) => { commit(withUpload(state, id, update(state.uploads[id] ?? NO_UPLOAD))); },
    queueWriteOutOf: id => (state.queueWrites[id] ?? 0) > 0,
    holdQueueWrite: async (id, write) => {
      // Retain the existing clickAction async-transition boundary.
      await Promise.resolve();
      const count = (step: 1 | -1) => {
        const value = (state.queueWrites[id] ?? 0) + step;
        commit({ ...state, queueWrites: put(state.queueWrites, id, value === 0 ? undefined : value) });
      };
      count(1);
      try { return await write(); } finally { count(-1); }
    },
    restartOutOf: id => state.restarts[id] === true,
    holdRestart: (id, restart) => {
      if (state.restarts[id]) return null;
      commit({ ...state, restarts: put(state.restarts, id, true) });
      return Promise.resolve().then(restart).finally(() => { commit({ ...state, restarts: put(state.restarts, id, undefined) }); });
    },
  };
  return {
    commands,
    getSnapshot: () => state,
    subscribe<T>(select: (snapshot: ConversationSnapshot) => T, listener: () => void) {
      let previous = select(state);
      const observe = () => {
        const next = select(state);
        if (Object.is(previous, next)) return;
        previous = next;
        listener();
      };
      listeners.add(observe);
      return () => { listeners.delete(observe); };
    },
  };
}
