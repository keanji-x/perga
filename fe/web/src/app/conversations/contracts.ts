import type { AgentProvider, PlannerAttachment } from '../../../../core/api/generated/wire.ts';
import type { PlannerPermissionMode } from '../../../../core/domain/planner-permission-mode.ts';
import type { Conversation, CreatedConversation, SideConversation, ConversationKind, ConversationState, ModelCatalog, ModelSelection, PendingQueueEntry, PlannerQueueEditOutcome, PlannerRunTokenUsage, PlannerQueueWriteOutcome, TranscriptEntry } from '../../../../core/domain/conversation.ts';
import type { FailedSendOp, PendingQueueSend, ReplacedTurn, QueueDisplayAccounting } from '../../../../core/domain/conversation-outbox.ts';
import type { ConversationStopFeedback } from '../../../../core/domain/conversation-stop.ts';
import type { RunningTurnAnchor } from '../../../../core/domain/conversation-meta.ts';
import type { UploadAttachment } from '../../features/planner/attachments.tsx';
import type { WorkerMessageMode } from '../../../../core/domain/worker-session.ts';
import type { OpenAsk } from '../../../../core/domain/ask.ts';
import type { AskAnswer } from '../../../../core/api/generated/wire.ts';

export type ConversationStore = Readonly<{
  worker?: import('../../../../core/api/perga-v1.ts').WorkerActivity | null;
  updateWorkerSettings: (settings: import('../../../../core/api/perga-v1.ts').WorkerSettings & { attemptId: string }, approvalKey?: string) => Promise<unknown>;
  answerWorkerAsk?: (ask: OpenAsk, answers: readonly AskAnswer[]) => Promise<void>;
  workerDelivery?: Readonly<{ mode: WorkerMessageMode; select: (mode: WorkerMessageMode) => void }> | null;
  unconfirmedWorkerSend?: Readonly<{ retry: () => void; dismiss: () => void }> | null;
  workerStatus?: string | null;
  conversations: readonly Conversation[];
  /** This selected scope's messages and actions, in canonical display order. */
  turns: readonly TranscriptEntry[];
  canQueueWhileRunning: boolean;
  pending: ReadonlySet<string>;
  working: boolean;
  canStop: boolean;
  /** Actual current session is terminal; display hold does not prevent saving new content. */
  executionHeld: boolean;
  stopping: boolean;
  stopFeedback: ConversationStopFeedback | null;
  sending: boolean;
  sendBlocked: boolean;
  /** Existing outbox reconciliation gate, with read errors left to their explicit retry surface. */
  awaitingConversationUpdate: boolean;
  /** The addressable page of the harness pending queue. */
  pendingQueue: readonly PendingQueueEntry[];
  pendingSends: readonly PendingQueueSend[];
  /** Versioned displayed carriers and intentional owner suppression; never suppresses an alive queue row. */
  queueDisplayAccounting: QueueDisplayAccounting;
  editQueuedEntry: (entry: PendingQueueEntry, text: string) => Promise<PlannerQueueEditOutcome>;
  deleteQueuedEntry: (entry: PendingQueueEntry) => Promise<PlannerQueueWriteOutcome>;
  /** A delete or steer of this card's queued entries is unanswered, whichever view made it. */
  queueWriteOut: boolean;
  /** Hand a queued entry to the running turn; `undefined` outside `turn_running`, and that is the whole gate. */
  steerQueuedEntry: ((entry: PendingQueueEntry) => Promise<PlannerQueueWriteOutcome>) | undefined;
  historyReady: boolean;
  historyLoading: boolean;
  hasEarlier: boolean;
  earlierCursor: number | null;
  loadingEarlier: boolean;
  historyError: string | null;
  /** The run read failed: phase, queue and model may be stale, and a send answered after an unknown attempt waits on it. */
  runError: string | null;
  runLoading: boolean;
  /** The current card has supplied a run response; an absent error alone does not establish this. */
  runReady: boolean;
  actionError: string | null;
  /** The send that gave up, held by this conversation's outbox until its Try again, Edit or Dismiss. */
  failedSend: FailedSendOp | null;
  /** Resume under the same key; true means local recovery took the intent, never server acceptance. */
  retrySend: (key: string) => boolean;
  /** Edit: a failed send that was not stored leaves the outbox, its words and images back in the composer. */
  discardFailedSend: (key: string) => void;
  /** Dismiss: the failed send leaves the outbox and nothing of it comes back, its composer images included. */
  dismissFailedSend: (key: string) => void;
  /**
   * What became of the send. `attachments` are ids already uploaded; naming one here is what makes it permanent.
   * `fromComposer`: they are the composer's own, so a delivery clears them (and the upload refusal) there.
   * `replaces`: the turn an Edit's Send replaces in the same request, or `null` for a new message.
   */
  send: (conversationId: string, text: string, attachments: readonly PlannerAttachment[], fromComposer: boolean,
    replaces: ReplacedTurn | null, idempotencyKey?: string) => Promise<void> | null;
  /** How full this conversation's context is; `null` when the harness has never said. */
  contextUsage: PlannerRunTokenUsage | null;
  /** Where the running turn's clock starts, anchored when the run response arrived; `null` when no turn is running. */
  runningAnchor: RunningTurnAnchor | null;
  uploadAttachment: UploadAttachment;
  interrupt: () => void;
  /** Declared by the bound Planner backend; false until the run read answers. */
  manualCompactSupported: boolean;
  compact: () => void;
  compacting: boolean;
  retryHistory: () => void;
  retryRun: () => void;
  /** False means newest reconciliation still owns the query; no input was spent. */
  loadEarlier: () => boolean;
  /** Why the queue is not draining, when the reader has to act; a standing condition of the conversation, unlike `actionError`. */
  blockedReason: string | null;
  providerRecoveryHoldsSend: boolean;
  providerResume: Readonly<{ state: 'available' | 'resuming' | 'paused'; start: () => void }> | null;
  abandonUnknownInput: (() => void) | null;
  /** What this conversation's turns run with. */
  model: ModelSelection;
  /** What may be chosen, or `null` until the catalog has answered once. */
  modelCatalog: ModelCatalog | null;
  /** Store a whole new selection. Its failure lands in `actionError`, and only while its conversation is shown. */
  setModel: (selection: ModelSelection) => void;
  /** Whether the Planner may pause a turn to ask first; `null` for a card that is not a Planner, and until the run read answers. */
  permissionMode: PlannerPermissionMode | null;
  /** Store a new mode for the next turn. Its failure lands in `actionError`, as a model change's does. */
  setPermissionMode: (mode: PlannerPermissionMode) => void;
}>;

/** A server-backed list and the real Track whose rows may enter the tab registry. */
export type ConversationRouteIntent = Readonly<{
  /** Open panes own their live rows; list refreshes must not overwrite them. */
  ownedCardIds?: readonly string[];
  visible?: boolean;
  rows: readonly Conversation[];
  rememberOn: string;
}>;

/**
 * The one conversation whose transcript is being read. `id` is the Track the card
 * hangs off; `state` is the row's server state as the baseline, and only the open
 * row also picks up the local phase and the name derived from its first message.
 */
export type PlannerConversationScope = Readonly<{
  id: string;
  /** The backend declared by the summary or Planner card, including dormant cards. */
  provider: AgentProvider;
  title?: string;
  cardId: string;
  cardTitle: string | null;
  updatedAt: number;
  kind?: ConversationKind;
  isLead?: boolean;
  state?: ConversationState | null;
}>;

/** First-message creation and authoritative session/input receipt replay. */
export type ConversationCreationSource = Readonly<{
  create: (text: string, idempotencyKey: string, provider: AgentProvider, selection: ModelSelection, side: SideConversation | undefined,
    answered: (result: CreatedConversation) => void, permissionMode?: string | null) => Promise<CreatedConversation>;
}>;
