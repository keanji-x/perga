import type { KeepConversationInputRequest, AgentProvider, PlannerAttachment } from '../../../../core/api/generated/wire.ts';
import type { Conversation, CreatedConversation, ModelSelection, SideConversation, SubmittedConversationInput, TranscriptEntry } from '../../../../core/domain/conversation.ts';
import type { ConversationOutboxOp, SendOp } from '../../../../core/domain/conversation-outbox.ts';
import type { ComposerContent } from '../../../../core/domain/conversation-composer.ts';
import type { UploadState } from '../../features/planner/attachments.tsx';

export type ConversationOpenRequest = Readonly<{ id: string; focusComposer: boolean }>;

/**
 * A conversation being written. The key belongs to the draft for its whole lifetime: a failed create
 * keeps `key` and `submitted`, since retrying an ambiguous request under a new key would mint a second conversation.
 */
export type ConversationDraft = Readonly<{
  /** The Track this draft belongs to. */
  scopeId: string;
  /** Frozen source attached to this draft and every retry of it. */
  side?: SideConversation;
  /** `/side question` sends the prepared question once; recovery thereafter is explicit. */
  autoSend?: boolean;
  /** Chosen before sending and locked while delivery is unconfirmed. */
  provider: AgentProvider;
  model: ModelSelection;
  permissionMode?: string | null;
  /** Identifies the draft to the server; minted once, never once per send. */
  key: string;
  /** The words the drawer is holding after its composer clears on send. */
  text: string | null;
  /** Ready images submitted with this draft, held for echo and retry. */
  attachments?: readonly PlannerAttachment[];
  /** Original dispatched body and client display time; retries preserve this exact fact. */
  submitted: SubmittedConversationInput | null;
  /** The create/recovery chain for this key has not settled yet. */
  creating: boolean;
  error: string | null;
  remedy: 'retry' | 'new-conversation' | null;
}>;

export type ConversationDraftId = Readonly<Pick<ConversationDraft, 'scopeId' | 'key'>>;

/** One slot per Track: unfinished draft work, or the row that work became — mutually exclusive by construction. */
export type DraftSlot = Readonly<
  | { kind: 'held'; draft: ConversationDraft }
  | { kind: 'adopted'; conversationId: string }
>;

export type DraftSlots = Readonly<Record<string, DraftSlot>>;

export type DraftMove = Readonly<
  | { kind: 'start'; draft: ConversationDraft }
  | { kind: 'edit'; from: ConversationDraftId; next: (current: ConversationDraft) => ConversationDraft }
  | { kind: 'adopt'; from: ConversationDraftId; conversationId: string }
  | { kind: 'discard'; from: ConversationDraftId }
  | { kind: 'discard-unsent'; scopeId: string }
  | { kind: 'finish-adoption'; scopeId: string; conversationId: string }
>;

export const slotHolds = (slot: DraftSlot | undefined, id: ConversationDraftId): slot is Extract<DraftSlot, { kind: 'held' }> =>
  slot?.kind === 'held' && slot.draft.scopeId === id.scopeId && slot.draft.key === id.key;

function withoutSlot(slots: DraftSlots, scopeId: string): DraftSlots {
  const next = { ...slots };
  delete next[scopeId];
  return next;
}

export function moveDraft(slots: DraftSlots, move: DraftMove): DraftSlots {
  switch (move.kind) {
    case 'start':
      return { ...slots, [move.draft.scopeId]: { kind: 'held', draft: move.draft } };
    case 'edit': {
      const slot = slots[move.from.scopeId];
      if (!slotHolds(slot, move.from)) return slots;
      const draft = move.next(slot.draft);
      return draft === slot.draft ? slots : { ...slots, [move.from.scopeId]: { kind: 'held', draft } };
    }
    case 'adopt': {
      const slot = slots[move.from.scopeId];
      return slotHolds(slot, move.from)
        ? { ...slots, [move.from.scopeId]: { kind: 'adopted', conversationId: move.conversationId } }
        : slots;
    }
    case 'discard':
      return slotHolds(slots[move.from.scopeId], move.from)
        ? withoutSlot(slots, move.from.scopeId)
        : slots;
    case 'discard-unsent': {
      const slot = slots[move.scopeId];
      return slot?.kind === 'held' && slot.draft.submitted === null
        ? withoutSlot(slots, move.scopeId)
        : slots;
    }
    case 'finish-adoption': {
      const slot = slots[move.scopeId];
      return slot?.kind === 'adopted' && slot.conversationId === move.conversationId
        ? withoutSlot(slots, move.scopeId)
        : slots;
    }
  }
}

export type RememberedConversation = Readonly<{
  conversation: Conversation;
  turns: readonly TranscriptEntry[];
}>;

export type InputResolutionIntent = Readonly<{ decision: 'keep' | 'retry'; key: string; args: Readonly<KeepConversationInputRequest>; status: 'sending' | 'unknown' }>;

export type ConversationRegistry = Readonly<{
  beginResolution: (card: string, decision: 'keep' | 'retry', args: KeepConversationInputRequest, key: string) => InputResolutionIntent | null;
  finishResolution: (card: string, key: string, status: 'unknown' | 'done') => void;
  turnsOf: (conversationId: string) => readonly TranscriptEntry[];
  /** Stored canonical source only. Never feed the display's outbox fold back into memory. */
  rememberedTurnsOf: (conversationId: string) => readonly TranscriptEntry[];
  remember: (conversation: Conversation, turns: readonly TranscriptEntry[]) => void;
  /**
   * Amend an existing entry using the owner's current canonical fact, never an async caller's captured render.
   * `amend` must be pure. Creates nothing that `rememberOn` declined.
   */
  updateExisting: (
    conversationId: string, amend: (entry: RememberedConversation) => RememberedConversation,
  ) => void;
  requestOpen: (conversationId: string, options?: { focusComposer?: boolean }) => void;
  clearOpenRequest: () => void;
  /** Retries reading and saving the initial-send queue. Returns true once synced. */
  hydratePendingTrackInitialSends: () => boolean;
  /** Checks the current in-memory queue, including sends queued during a storage failure. */
  hasPendingTrackInitialSend: (trackId: string) => boolean;
  /** Queues in memory and reports whether configured storage accepted the write. */
  queueTrackInitialSend: (trackId: string, text: string, key: string, attachments?: readonly PlannerAttachment[]) => boolean;
  consumeTrackInitialSend: (trackId: string) => void;
  /** Failed first-message attempts live here, above every route remount. */
  draftOf: (scopeId: string) => ConversationDraft | null;
  startDraft: (draft: ConversationDraft) => void;
  editDraft: (
    from: ConversationDraftId, next: (current: ConversationDraft) => ConversationDraft,
  ) => void;
  /** Import original acceptance and adopt only this still-held scope/key before list publication. */
  acceptDraft: (from: ConversationDraftId, result: CreatedConversation, submitted: SubmittedConversationInput) => boolean;
  discardDraft: (from: ConversationDraftId) => void;
  discardUnsentDraft: (scopeId: string) => void;
  adoptedDraftIdOf: (scopeId: string) => string | null;
  finishDraftAdoption: (scopeId: string, conversationId: string) => void;
  /** Each conversation's keyed sends not yet shown by the server, above every route remount. */
  acceptedTurnOf: (id: string) => string | null;
  rememberAcceptedTurn: (id: string, turnId: string | null) => void;
  sendGenerationOf: (id: string) => number;
  invalidateSends: (id: string) => void;
  observeEpoch: (id: string, epoch: number) => void;
  outboxOf: (conversationId: string) => readonly ConversationOutboxOp[];
  /** Start a send there (`beginSendOp`): false while that conversation cannot take it. One active operation per conversation. */
  beginSend: (conversationId: string, op: SendOp) => boolean;
  /** Change one conversation's outbox, whichever is shown; returns it as written. */
  editOutbox: (conversationId: string, next: (current: readonly ConversationOutboxOp[]) => readonly ConversationOutboxOp[]) => readonly ConversationOutboxOp[];
  /** The next number in the tab's one read order: a read numbered later started later, whichever view started it. */
  nextRead: () => number;
  /** Pending permission writes live with the conversation, across pane remounts. */
  holdPermissionWrite: <T>(id: string, write: Promise<T>) => Promise<T>;
  permissionWriteOf: (id: string) => Promise<unknown> | undefined;
  /** Each existing conversation's unsent words and images, kept across closing, switching and remounts. */
  composerOf: (conversationId: string) => ComposerContent;
  /** Change one conversation's composer, whichever conversation is shown; returns it as written. */
  editComposer: (conversationId: string, next: (current: ComposerContent) => ComposerContent) => ComposerContent;
  /** Give words and images back to one conversation's composer, added to what it holds (`withRefill`); the images the
   * cap left out are said there, whatever the composer already held (#2068). */
  refillComposer: (conversationId: string, refill: ComposerContent) => void;
  /** Each Track's unsent words for a conversation not created yet, kept across closing, `+` and remounts.
   * Not `ConversationDraft.text`, which holds the words after the composer clears on send. */
  newConversationComposerOf: (scopeId: string) => string;
  editNewConversationComposer: (scopeId: string, next: (current: string) => string) => void;
  /** Each Track's ready staged images for a conversation not created yet. */
  newConversationAttachmentsOf: (scopeId: string) => readonly PlannerAttachment[];
  editNewConversationAttachments: (
    scopeId: string,
    next: (current: readonly PlannerAttachment[]) => readonly PlannerAttachment[],
  ) => void;
  newConversationUploadOf: (scopeId: string) => UploadState;
  editNewConversationUpload: (scopeId: string, next: (current: UploadState) => UploadState) => void;
  /** One card's image uploads, held here so a remount or another route sees an upload still in flight. */
  uploadOf: (cardId: string) => UploadState;
  editUpload: (cardId: string, next: (current: UploadState) => UploadState) => void;
  /** Whether a write to one card's queued entries (a delete or a steer) is unanswered, held here so a remount or another
   * route sees it still out (#2068). */
  queueWriteOutOf: (cardId: string) => boolean;
  /** Run one write to that card's queued entries, counted as out until it settles. */
  holdQueueWrite: <T>(cardId: string, write: () => Promise<T>) => Promise<T>;
  /** Whether a restart of one card's session is unanswered, held here so a remount or another route sees it still out
   * (#2192). */
  restartOutOf: (cardId: string) => boolean;
  /** Run one restart of that card's session, or `null` while one is out: each restart replaces the session the one
   * before it started. */
  holdRestart: (cardId: string, restart: () => Promise<void>) => Promise<void> | null;
  /* Deliberately no "open the planner conversation of track W" slot: the track being left is still
       mounted when a create states it, so that intent travels in the history entry instead. */
}>;
