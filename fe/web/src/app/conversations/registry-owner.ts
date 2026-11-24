import { PENDING_TRACK_INITIAL_SENDS_KEY } from '../../../../core/keys/storage.ts';
import type { TranscriptEntry, Conversation, ConversationAttachment } from '../../../../core/domain/conversation.ts';
import { DEPLOYMENT_OWNER_USER_ID } from '../../../../core/api/auth.ts';
import { isOptimisticConversationTurn } from '../../../../core/domain/conversation.ts';
import { beginSendOp, type ConversationOutboxOp } from '../../../../core/domain/conversation-outbox.ts';
import { EMPTY_COMPOSER, withRefill, type ComposerContent } from '../../../../core/domain/conversation-composer.ts';
import { isBlankForKernel } from '../../../../core/domain/track.ts';
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
type PendingTrackInitialSend = Readonly<{
  text: string;
  key: string;
  attachments?: readonly ConversationAttachment[];
}>;

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
  const composers = put(state.composers, id, value.text === '' && value.attachments.length === 0 && value.attachmentError == null ? undefined : value);
  return composers === state.composers ? state : { ...state, composers };
}

function withOutbox(state: ConversationSnapshot, id: string, value: readonly ConversationOutboxOp[]): ConversationSnapshot {
  const outboxes = put(state.outboxes, id, value.length === 0 ? undefined : value);
  return outboxes === state.outboxes ? state : { ...state, outboxes };
}

function withRefilledComposer(state: ConversationSnapshot, id: string, refill: ComposerContent): ConversationSnapshot {
  return withComposer(state, id, withRefill(state.composers[id] ?? EMPTY_COMPOSER, refill));
}

function previewUrls(state: ConversationSnapshot) {
  const urls = new Set<string>();
  const add = (items: readonly ConversationAttachment[] = []) => {
    for (const image of items) if (image.file !== undefined) urls.add(image.url);
  };
  for (const composer of Object.values(state.composers)) add(composer.attachments);
  for (const ops of Object.values(state.outboxes)) for (const op of ops) add(op.echo.attachments);
  for (const pending of Object.values(state.pendingTrackInitialSends)) add(pending.attachments);
  for (const slot of Object.values(state.drafts)) {
    if (slot.kind !== 'held') continue;
    add(slot.draft.attachments);
    add(slot.draft.submitted?.attachments);
  }
  return urls;
}

function withoutIds<T>(items: ById<T>, ids: ReadonlySet<string>): ById<T> {
  return Object.fromEntries(Object.entries(items).filter(([id]) => !ids.has(id)));
}

function isPendingTrackInitialSend(value: unknown): value is PendingTrackInitialSend {
  return typeof value === 'object' && value !== null
    && typeof (value as { text?: unknown }).text === 'string'
    && typeof (value as { key?: unknown }).key === 'string';
}

function readPendingTrackInitialSends(key: string, submittedStorage?: () => Storage):
  { ok: true; map: ById<PendingTrackInitialSend> } | { ok: false } {
  if (submittedStorage === undefined) return { ok: true, map: {} };
  let raw: string | null;
  try {
    raw = submittedStorage().getItem(key);
  } catch {
    return { ok: false };
  }
  if (raw === null) return { ok: true, map: {} };
  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return { ok: true, map: {} };
    const map = Object.fromEntries(Object.entries(parsed)
      .filter((entry): entry is [string, PendingTrackInitialSend] => isPendingTrackInitialSend(entry[1])));
    return { ok: true, map };
  } catch {
    return { ok: true, map: {} };
  }
}

function writePendingTrackInitialSends(key: string, map: ById<PendingTrackInitialSend>, submittedStorage?: () => Storage): boolean {
  if (submittedStorage === undefined) return true;
  try {
    const storage = submittedStorage();
    const serializable = Object.fromEntries(
      Object.entries(map)
        // Image-only initial sends hold non-serializable attachments; never persist a bare empty text replay.
        .filter(([, entry]) => !isBlankForKernel(entry.text))
        .map(([id, entry]) => [id, { text: entry.text, key: entry.key }]),
    );
    if (Object.keys(serializable).length === 0) storage.removeItem(key);
    else storage.setItem(key, JSON.stringify(serializable));
    return true;
  } catch {
    return false;
  }
}

/** The only local conversation authority. Commands finish before any observer is notified. */
export function createConversationOwner(submittedStorage?: () => Storage) {
  const pendingInitialSendsKey = PENDING_TRACK_INITIAL_SENDS_KEY;
  const initialRead = readPendingTrackInitialSends(pendingInitialSendsKey, submittedStorage);
  let pendingInitialSendsHydrated = initialRead.ok;
  let pendingInitialSendsDirty = false;
  const consumedBeforeHydration = new Map<string, string>();
  let state: ConversationSnapshot = {
    entries: {}, conversations: NO_CONVERSATIONS, drafts: {}, openRequest: null,
    pendingTrackInitialSends: initialRead.ok ? initialRead.map : {}, outboxes: {}, acceptedTurns: {},
    composers: {}, newComposers: {}, queueWrites: {}, restarts: {}, resolutions: {},
  };
  const sendGenerations = new Map<string, number>();
  const epochs = new Map<string, number>();
  const conversationTracks = new Map<string, string>();
  const draftTracks = new Map<string, string>();
  const retiredTracks = new Set<string>();
  const permissionWrites = new Map<string, Promise<unknown>>();
  let read = 0;
  const listeners = new Set<() => void>();
  const commit = (next: ConversationSnapshot) => {
    if (next === state) return;
    const previews = next.composers !== state.composers
      || next.outboxes !== state.outboxes
      || next.drafts !== state.drafts
      || next.pendingTrackInitialSends !== state.pendingTrackInitialSends
      ? previewUrls(state) : undefined;
    state = next;
    if (previews && previews.size !== 0) {
      for (const url of previewUrls(next)) previews.delete(url);
      for (const url of previews) URL.revokeObjectURL(url);
    }
    for (const listener of listeners) listener();
  };
  const savePendingTrackInitialSends = (pendingTrackInitialSends: ById<PendingTrackInitialSend>) => {
    pendingInitialSendsDirty = !writePendingTrackInitialSends(pendingInitialSendsKey, pendingTrackInitialSends, submittedStorage);
    return !pendingInitialSendsDirty;
  };
  const hydratePendingTrackInitialSends = () => {
    if (pendingInitialSendsHydrated) {
      return !pendingInitialSendsDirty || savePendingTrackInitialSends(state.pendingTrackInitialSends);
    }
    const loaded = readPendingTrackInitialSends(pendingInitialSendsKey, submittedStorage);
    if (!loaded.ok) return false;
    pendingInitialSendsHydrated = true;
    const current = state.pendingTrackInitialSends;
    const recovered = { ...loaded.map };
    // A replay can finish in this tab while its original queue entry is still unreadable.
    for (const [trackId, key] of consumedBeforeHydration) {
      if (recovered[trackId]?.key === key) delete recovered[trackId];
    }
    const pendingTrackInitialSends = { ...recovered, ...current };
    if (!equalRecord(loaded.map, pendingTrackInitialSends)) {
      savePendingTrackInitialSends(pendingTrackInitialSends);
    }
    consumedBeforeHydration.clear();
    if (!equalRecord(current, pendingTrackInitialSends)) commit({ ...state, pendingTrackInitialSends });
    return !pendingInitialSendsDirty;
  };
  const rememberEntry = (id: string, conversation: Conversation, given: readonly TranscriptEntry[]) => {
    if (retiredTracks.has(conversation.trackId)) return;
    conversationTracks.set(id, conversation.trackId);
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
    bindConversation: (id, trackId) => { conversationTracks.set(id, trackId); },
    bindDraftScope: (scopeId, trackId) => { draftTracks.set(scopeId, trackId); },
    retireTrack: trackId => {
      if (retiredTracks.has(trackId)) return;
      retiredTracks.add(trackId);
      const ids = new Set([...conversationTracks].filter(([, owner]) => owner === trackId).map(([id]) => id));
      const scopes = new Set([trackId, ...[...draftTracks].filter(([, owner]) => owner === trackId).map(([id]) => id)]);
      for (const id of ids) {
        sendGenerations.delete(id);
        epochs.delete(id);
        permissionWrites.delete(id);
      }
      const entries = withoutIds(state.entries, ids);
      const pendingTrackInitialSends = put(state.pendingTrackInitialSends, trackId, undefined);
      if (pendingTrackInitialSends !== state.pendingTrackInitialSends && pendingInitialSendsHydrated)
        savePendingTrackInitialSends(pendingTrackInitialSends);
      commit({ ...state, entries, conversations: state.conversations.filter(row => !ids.has(row.id)),
        drafts: withoutIds(state.drafts, scopes), newComposers: withoutIds(state.newComposers, scopes),
        composers: withoutIds(state.composers, ids), outboxes: withoutIds(state.outboxes, ids),
        acceptedTurns: withoutIds(state.acceptedTurns, ids), pendingTrackInitialSends,
        openRequest: state.openRequest !== null && ids.has(state.openRequest.id) ? null : state.openRequest });
    },
    hydratePendingTrackInitialSends,
    hasPendingTrackInitialSend: (trackId) => Object.hasOwn(state.pendingTrackInitialSends, trackId),
    queueTrackInitialSend: (trackId, text, key, attachments) => {
      if (!pendingInitialSendsHydrated) hydratePendingTrackInitialSends();
      const pending: PendingTrackInitialSend = attachments !== undefined && attachments.length > 0
        ? { text, key, attachments }
        : { text, key };
      const pendingTrackInitialSends = put(state.pendingTrackInitialSends, trackId, pending);
      const saved = pendingInitialSendsHydrated && savePendingTrackInitialSends(pendingTrackInitialSends);
      commit({ ...state, pendingTrackInitialSends });
      return saved;
    },
    consumeTrackInitialSend: (trackId) => {
      if (!pendingInitialSendsHydrated) hydratePendingTrackInitialSends();
      const pending = state.pendingTrackInitialSends[trackId];
      if (pending === undefined) return;
      const pendingTrackInitialSends = put(state.pendingTrackInitialSends, trackId, undefined);
      if (pendingInitialSendsHydrated) savePendingTrackInitialSends(pendingTrackInitialSends);
      else consumedBeforeHydration.set(trackId, pending.key);
      commit({ ...state, pendingTrackInitialSends });
    },
    draftOf: scope => { const slot = state.drafts[scope]; return slot?.kind === 'held' ? slot.draft : null; },
    startDraft: draft => {
      if (!retiredTracks.has(draftTracks.get(draft.scopeId) ?? draft.scopeId))
        commit({ ...state, drafts: moveDraft(state.drafts, { kind: 'start', draft }) });
    },
    editDraft: (from, next) => {
      const drafts = moveDraft(state.drafts, { kind: 'edit', from, next });
      if (drafts !== state.drafts) commit({ ...state, drafts });
    },
    acceptDraft: (from, result, submitted) => {
      if (!slotHolds(state.drafts[from.scopeId], from)) return false;
      conversationTracks.set(result.conversation.id, result.conversation.trackId);
      const id = result.conversation.id;
      const inputId = result.sessionInput.inputId;
      const firstTurn = { id: `human:${inputId}`, author: 'you' as const, text: submitted.text, atMs: submitted.atMs };
      const attachments = submitted.attachments;
      const acceptedImage = attachments?.length ? [{
        key: from.key, phase: 'accepted', messageId: inputId, queued: false, fromComposer: false,
        echo: { ...firstTurn, id: `echo-${from.key}`, attachments, queued: false, localSend: true },
      } as const] : [];
      const turns = acceptedImage.length > 0 ? NO_TURNS : [{ ...firstTurn,
        human: { userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', messageId: inputId, revision: 1 },
      }];
      const entries = put(state.entries, id, { conversation: result.conversation, turns });
      const next = acceptedImage.length === 0 ? state : withOutbox(state, id, [...state.outboxes[id] ?? NO_SENDS, ...acceptedImage]);
      commit({ ...next, entries, conversations: Object.values(entries).map(entry => entry.conversation),
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
    rememberAcceptedTurn: (id, turnId) => { if (!retiredTracks.has(conversationTracks.get(id) ?? '')) commit({ ...state, acceptedTurns: put(state.acceptedTurns, id, turnId ?? undefined) }); },
    sendGenerationOf: id => retiredTracks.has(conversationTracks.get(id) ?? '') ? -1 : sendGenerations.get(id) ?? 0,
    invalidateSends: id => {
      sendGenerations.set(id, (sendGenerations.get(id) ?? 0) + 1);
      let next = state;
      for (const op of state.outboxes[id] ?? []) {
        if (op.phase !== 'accepted' && op.fromComposer) next = withRefilledComposer(next, id, { text: op.echo.text, attachments: op.echo.attachments ?? [] });
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
      if (retiredTracks.has(conversationTracks.get(id) ?? '')) return false;
      const begun = beginSendOp(state.outboxes[id] ?? NO_SENDS, op);
      if (begun === null) return false;
      let next = withOutbox(state, id, begun);
      if (op.fromComposer && op.echo.attachments?.length) {
        const composer = state.composers[id] ?? EMPTY_COMPOSER;
        const sent = new Set(op.echo.attachments.map(image => image.id));
        next = withComposer(next, id, { ...composer, attachments: composer.attachments.filter(image => !sent.has(image.id)), attachmentError: null });
      }
      commit(next);
      return true;
    },
    removeSend: (id, key, restore) => {
      const ops = state.outboxes[id] ?? NO_SENDS;
      if (!ops.some(op => op.key === key)) return;
      const next = withOutbox(state, id, ops.filter(op => op.key !== key));
      commit(restore === undefined ? next : withRefilledComposer(next, id, restore));
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
      if (retiredTracks.has(conversationTracks.get(id) ?? '')) return EMPTY_COMPOSER;
      const after = update(state.composers[id] ?? EMPTY_COMPOSER);
      commit(withComposer(state, id, after));
      return after;
    },
    refillComposer: (id, refill) => { if (!retiredTracks.has(conversationTracks.get(id) ?? '')) commit(withRefilledComposer(state, id, refill)); },
    newConversationComposerOf: scope => state.newComposers[scope] ?? '',
    editNewConversationComposer: (scope, update) => {
      if (retiredTracks.has(draftTracks.get(scope) ?? scope)) return;
      const value = update(state.newComposers[scope] ?? '');
      const newComposers = put(state.newComposers, scope, value === '' ? undefined : value);
      if (newComposers !== state.newComposers) commit({ ...state, newComposers });
    },
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
  let mounted = 0;
  return {
    retain() {
      mounted += 1;
      return () => {
        mounted -= 1;
        // Strict Mode remounts effects on the same owner before this microtask runs.
        queueMicrotask(() => {
          if (mounted === 0) for (const url of previewUrls(state)) URL.revokeObjectURL(url);
        });
      };
    },
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
