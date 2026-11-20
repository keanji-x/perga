import { createStorageKey } from '../../../../core/keys/storage.ts';
import type { TranscriptEntry, Conversation } from '../../../../core/domain/conversation.ts';
import { DEPLOYMENT_OWNER_USER_ID } from '../../../../core/api/auth.ts';
import { isOptimisticConversationTurn, TOO_MANY_IMAGES } from '../../../../core/domain/conversation.ts';
import { beginSendOp, withConfirmedSends, type ConversationOutboxOp } from '../../../../core/domain/conversation-outbox.ts';
import { EMPTY_COMPOSER, isSameComposer, tookEveryImage, withRefill, type ComposerContent } from '../../../../core/domain/conversation-composer.ts';
import { NO_UPLOAD, type UploadState } from '../../features/planner/attachments.tsx';
import { moveDraft, slotHolds, type ConversationRegistry, type ConversationEdit, type ConversationOpenRequest,
  type DraftSlots, type EditNotice, type InputResolutionIntent, type RememberedConversation } from './registry-model.ts';

export const NO_SENDS: readonly ConversationOutboxOp[] = Object.freeze([]);
export const NO_ROWS: readonly string[] = Object.freeze([]);
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
  spentRows: ById<readonly string[]>;
  composers: ById<ComposerContent>;
  newComposers: ById<string>;
  uploads: ById<UploadState>;
  edits: ById<ConversationEdit>;
  notices: ById<EditNotice>;
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
  const spentRows = value.length === 0 ? put(state.spentRows, id, undefined) : state.spentRows;
  return outboxes === state.outboxes && spentRows === state.spentRows ? state : { ...state, outboxes, spentRows };
}

function withRefilledComposer(state: ConversationSnapshot, id: string, refill: ComposerContent): ConversationSnapshot {
  const merged = withRefill(state.composers[id] ?? EMPTY_COMPOSER, refill);
  const next = withComposer(state, id, merged);
  return tookEveryImage(merged, refill) ? next
    : withUpload(next, id, { ...next.uploads[id] ?? NO_UPLOAD, refusal: TOO_MANY_IMAGES });
}

function withoutEdit(state: ConversationSnapshot, id: string): ConversationSnapshot {
  return { ...state, edits: put(state.edits, id, undefined), notices: put(state.notices, id, undefined) };
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
    entries: {}, conversations: NO_CONVERSATIONS, drafts: {}, openRequest: null, pendingTrackInitialSends: readPendingTrackInitialSends(pendingInitialSendsKey, submittedStorage), outboxes: {}, spentRows: {},
    composers: {}, newComposers: {}, uploads: {}, edits: {}, notices: {}, queueWrites: {}, restarts: {}, resolutions: {},
  };
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
    turnsOf: id => withConfirmedSends(state.entries[id]?.turns ?? NO_TURNS, state.outboxes[id] ?? NO_SENDS, state.spentRows[id] ?? NO_ROWS, id),
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
      const ops = state.outboxes[result.conversation.id] ?? NO_SENDS;
      // The creation ACK names the input; only session history can take over its bubble.
      const next = withOutbox(state, result.conversation.id, ops.some(op => op.key === from.key) ? ops
        : [...ops, { key: from.key, phase: 'confirmed', queueRevision: null, fromComposer: false, replaces: null,
          echo: { id: `initial-${from.key}`, author: 'you', text: submitted.text, atMs: submitted.atMs,
            serverHighWaterBefore: 0, queued: false, entryId: result.sessionInput.inputId,
            authorUserId: DEPLOYMENT_OWNER_USER_ID } }]);
      commit({ ...next, drafts: moveDraft(next.drafts, { kind: 'adopt', from, conversationId: result.conversation.id }) });
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
    outboxOf: id => state.outboxes[id] ?? NO_SENDS,
    spentRowsOf: id => state.spentRows[id] ?? NO_ROWS,
    beginSend: (id, op) => {
      const begun = beginSendOp(state.outboxes[id] ?? NO_SENDS, op);
      if (begun === null) return false;
      let next = withOutbox(state, id, begun);
      next = { ...next, notices: put(next.notices, id, undefined) };
      const edit = next.edits[id];
      if (op.replaces !== null && edit?.kind === 'turn' && edit.outcomeId === op.replaces.outcomeId) next = { ...next, edits: put(next.edits, id, undefined) };
      commit(next);
      return true;
    },
    editOutbox: (id, update) => {
      const before = state.outboxes[id] ?? NO_SENDS;
      const after = update(before);
      if (after !== before) commit(withOutbox(state, id, after));
      return after;
    },
    retireSends: (id, retired) => {
      const keys = new Set(retired.map(({ key }) => key));
      const before = state.outboxes[id] ?? NO_SENDS;
      const kept = before.filter(op => !keys.has(op.key));
      let next = kept.length === before.length ? state : withOutbox(state, id, kept);
      const rows = retired.flatMap(({ row }) => row === null ? [] : [row]);
      if (kept.length > 0 && rows.length > 0) next = { ...next, spentRows: put(next.spentRows, id, [...next.spentRows[id] ?? [], ...rows]) };
      commit(next);
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
    editOf: id => state.edits[id] ?? null,
    beginEdit: (id, edit) => {
      if (state.edits[id] !== undefined) return false;
      let next = { ...state, edits: put(state.edits, id, { ...edit, refill: withRefill(EMPTY_COMPOSER, edit.refill) }), notices: put(state.notices, id, undefined) };
      next = withRefilledComposer(next, id, edit.refill);
      commit(next);
      return true;
    },
    cancelEdit: id => {
      const edit = state.edits[id];
      if (edit === undefined) return;
      let next = withoutEdit(state, id);
      const upload = state.uploads[id] ?? NO_UPLOAD;
      if (upload.refusal === TOO_MANY_IMAGES) next = withUpload(next, id, { ...upload, refusal: null });
      if (upload.inFlight === 0 && isSameComposer(state.composers[id] ?? EMPTY_COMPOSER, edit.refill)) next = withComposer(next, id, EMPTY_COMPOSER);
      commit(next);
    },
    settleQueuedEdit: (id, edit, submitted, outcome) => {
      if (state.edits[id] !== edit) return;
      if (outcome.kind === 'done') {
        const next = withoutEdit(state, id);
        commit(isSameComposer(state.composers[id] ?? EMPTY_COMPOSER, submitted) ? withComposer(next, id, EMPTY_COMPOSER) : next);
      } else if (outcome.kind === 'stale') {
        commit({ ...state, edits: put(state.edits, id, { ...edit, entry: { ...edit.entry, text: outcome.text, rev: outcome.rev } }) });
      }
    },
    leaveEdit: (id, outcomeId) => {
      const edit = state.edits[id];
      if (edit?.kind !== 'turn' || edit.outcomeId !== outcomeId) return;
      commit({ ...state, edits: put(state.edits, id, undefined), notices: put(state.notices, id, { kind: 'stale' }) });
    },
    noteRefusedSend: (id, notice) => { commit({ ...state, notices: put(state.notices, id, notice) }); },
    noteRestarted: id => { commit({ ...state, notices: put(state.notices, id, { kind: 'restarted' }) }); },
    editNoticeOf: id => state.notices[id] ?? null,
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
