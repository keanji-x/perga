import { z } from 'zod';

import { agentSessionDtoSchema, type AgentSessionDto } from '../api/perga-v1.js';

import { sessionCreationReceiptSchema, sessionSummarySchema, type SessionInputReceipt } from '../api/perga-v1.js';

import type { AgentProvider, HarnessInputPresentation, HarnessInterruptionReason, HarnessPhaseTag, HarnessTimeoutEvidence, PlannerAttachment, SystemInputPresentation } from './presentation-types.js';
import type { ApiFailure, ApiOperation } from '../api/types.js';
import { imageMessageBody, type MultipartFile } from '../api/multipart.js';

import { classifyFailure, refusedText, type FailureTable, type WriteText } from './failure-class.js';

/** A frozen discussion source; does not name a provider session. */
export type SideConversation = Readonly<{ source_card_id: string; context: string }>;

/** What kind of thing the conversation is; `'agent'` is derived server-side from the card's own marker. */
export type ConversationKind =
  | 'terminal' | 'codex' | 'claude' | 'agent';

/** Mirrors `WorkerSessionState`. */
export type ConversationState =
  | 'starting' | 'running' | 'idle' | 'turn_pending' | 'exited' | 'failed' | 'superseded';

export type Conversation = Readonly<{
  id: string;
  isLead?: boolean;
  trackId: string;
  /** Assistant archive metadata; other conversation kinds have no archive lifecycle. */
  archivedAt?: number | null;
  /** Saved parent of a discussion branch; absent for ordinary conversations. */
  sourceCardId?: string;
  /**
   * The track's title; absent when a per-Track list does not repeat it, so surfaces that name
   * tracks must resolve it.
   */
  trackTitle?: string;
  /** The conversation's own name, or null; never the track's title. */
  title: string | null;
  kind: ConversationKind;
  provider: AgentProvider;
  /** The live session's state, or `null` when there is no live session to read — a fact, not a gap. */
  state: ConversationState | null;
  /** Last turn, or the session's own update time when it has no turns yet. */
  updatedAt: number;
  /**
   * When the last non-interrupted turn ended, `null` before any has; `updatedAt` also moves when
   * the reader queues a message.
   */
  lastTurnCompletedAt?: number | null;
  /** Turn count, or absent when the surface that produced the row cannot count. */
  turns?: number;
}>;

/** A compose-time image can carry its original file alongside its preview URL. */
export type ConversationAttachment = Readonly<PlannerAttachment & { file?: MultipartFile }>;

export type SubmittedConversationInput = Readonly<{
  text: string; atMs: number; attachments?: readonly ConversationAttachment[];
}>;

export type CreatedConversation = Readonly<{
  conversation: Conversation;
  sessionInput: SessionInputReceipt;
}>;

/** What a session is called when it has no name of its own. */
export const CONVERSATION_KIND_LABEL: Readonly<Record<ConversationKind, string>> = Object.freeze({
  terminal: 'Terminal',
  codex: 'Codex',
  claude: 'Claude',
  agent: 'Assistant',
});

/** The one name a conversation shows, wherever it is shown. */
export function conversationName(conversation: Conversation): string {
  return conversation.title ?? CONVERSATION_KIND_LABEL[conversation.kind];
}

/** A name taken from the first line said; roughly what `--panel-w` fits at `--text-base`. */
export const CONVERSATION_NAME_MAX = 48;

export function conversationNameFrom(text: string): string | null {
  const line = text.trim().split('\n', 1)[0]?.trim() ?? '';
  if (line === '') return null;
  return line.length <= CONVERSATION_NAME_MAX
    ? line
    : `${line.slice(0, CONVERSATION_NAME_MAX - 1).trimEnd()}…`;
}

/** Newest first. Sorting is a display rule, but "which is newest" is not. */
export function byRecency(left: Conversation, right: Conversation): number {
  return right.updatedAt - left.updatedAt;
}

/** Who wrote a turn. Tool calls, shell runs and the like are not speech; they arrive as `ConversationActivity`. */
export type TurnAuthor = 'you' | 'agent' | 'human' | 'unknown';

/** Known native execution identity; absent for application inputs and unknown historical sources. */
export type NativeTurnContext = Readonly<{
  displayPosition?: number;
  nativeTurn?: Readonly<{ threadId: string; turnId: string }>;
}>;

export type ConversationTurn = NativeTurnContext & Readonly<{
  id: string;
  author: TurnAuthor;
  /** Present only for actual canonical Human identity; native user echoes have none. */
  human?: Readonly<{ userId: string; displayName: string; messageId: string; revision: number }>;
  displayPosition?: number;
  imported?: boolean;
  /** Verbatim. Line breaks are the author's and are preserved on render. */
  text: string;
  atMs: number;
  /** Images this turn carried; absent and empty mean the same thing. */
  attachments?: readonly ConversationAttachment[];
  requestKey?: string;
  /** Set when the agent asked this through `neige_user_ask`; the quiet-sync fold keeps it outside the fold. */
  origin?: 'ask';
  /** Exact input provenance, including a known acceptance receipt; only inputSource names raw stored source. */
  queueEntryId?: string | null;
  deliveryError?: string;
  messageStatus?: MessageStatus;
  supplemental?: boolean;
  /** Canonical structured-input source; rendered ids remain the renderer's separate identity. */
  inputSource?: Readonly<{ cardId: string; threadId: string; itemId: number; segmentIndex: number; turnId: string | null; entryRev: number | null }>;
}>;

/** A local message shown while awaiting a server history or queue row. */
export type OptimisticConversationTurn = ConversationTurn & Readonly<{
  localSend: true;
  queued: boolean;
  /** Queue-only sends wait for the server to publish an accepted identity. */
  waitForIdentity?: boolean;
}>;

/** A kernel observation delivered through Codex's user-message transport; nobody in the conversation authored it. */
export type ConversationSystemEntry = Readonly<{
  id: string;
  author: 'system';
  /** Present for a canonical source's actual conversation cursor. */
  displayPosition?: number;
  imported?: boolean;
  /** Stable short label selected from structured kernel metadata. */
  label: string;
  /** Full rendered observation, retained as disclosure/title context. */
  text: string;
  atMs: number;
  /**
   * Set on every segment of a report-only batch, including its regenerable budget context.
   * A budget alone is not a background sync. Never `false`.
   */
  quiet?: true;
}>;

export type ConversationMessage = ConversationTurn | ConversationSystemEntry;

const harnessInputPresentationSchema: z.ZodType<HarnessInputPresentation> = z.enum([
  'user',
  'system',
  'system_worker_turn_finished',
  'system_report_edited',
  'system_task_completed',
  'system_task_failed',
  'system_mail',
  'system_planner_turn_budget',
  'system_planner_context_usage',
]);

/** The presentation owner supplies the closed set; System cannot acquire User presentation. */
export const systemInputPresentationSchema = z.custom<SystemInputPresentation>(value =>
  harnessInputPresentationSchema.safeParse(value).success && value !== 'user');

/**
 * Whether a message posted *now* goes on the harness pending queue rather than straight into
 * a turn: `HarnessState::can_issue_turn()` accepts only `Idle` and `TurnCompleted`.
 * `null` (unknown) is deliberately read as queueing: guessing *issued* on a conversation that
 * really queued is a dead composer with no round trip that ends it.
 */
export function kernelQueuesInput(phase: HarnessPhaseTag | null): boolean {
  return !(phase === 'idle' || phase === 'turn_completed');
}

/**
 * One addressable message waiting in the harness pending queue. `rev` is the compare-and-swap
 * token.
 */
export const messageStatusSchema = z.enum(['queued', 'sending', 'processing', 'completed', 'failed', 'unconfirmed']);
export type MessageStatus = z.infer<typeof messageStatusSchema>;

export function messageStatusLabel(status: MessageStatus): string {
  switch (status) {
    case 'queued': return 'Queued';
    case 'sending': return 'Sending…';
    case 'processing': return 'Processing';
    case 'completed': return 'Completed';
    case 'failed': return 'Failed';
    case 'unconfirmed': return 'Delivery not confirmed';
  }
}

export type PendingQueueEntry = Readonly<{
  entry_id: string;
  text: string;
  rev: number;
  queued_at_ms: number;
  state?: 'waiting' | 'steering' | 'sending' | 'processing' | 'unconfirmed';
  attachments?: readonly ConversationAttachment[];
}>;

/**
 * How long the harness has run its current turn, by its own monotonic clock since it accepted the
 * turn's start. Not a wall-clock time: anchor it to when the response arrived (`anchorRunningTurn`).
 */
export type PlannerRunningTurn = Readonly<{ turn_id: string; elapsed_ms: number }>;

/**
 * The context-occupancy reading. V1 readings derive percent only within a known window;
 * overflow and unknown windows leave the meter unavailable.
 */
export type PlannerRunTokenUsage = Readonly<{
  /** Tokens in the model's context as of its most recent response. */
  used_tokens: number;
  /** The model's context window, or `null` when codex has never named one. */
  context_window: number | null;
  /** Context occupancy in `0..=100`, or `null` — see above. */
  percent: number | null;
  /** Wall clock of the reading; null when occupancy was reset without a new sample. */
  at_ms: number | null;
}>;

/** Reversible retained-DOM commit budget, shared by requests and cursor termination. */
export const HARNESS_ITEMS_PAGE_LIMIT = 60;

/** Composer selection; `null` follows the provider default. Session updates persist model and reasoning effort. */
export type ModelSelection = Readonly<{ model: string | null; reasoning_effort: string | null }>;

export const FOLLOW_INSTALLATION_DEFAULT: ModelSelection = Object.freeze({
  model: null, reasoning_effort: null,
});

const conversationStateSchema = z.enum([
  'starting', 'running', 'idle', 'turn_pending', 'exited', 'failed', 'superseded',
]);

/** The longest first message the server accepts, checked before it is sent. */
export const CONVERSATION_TEXT_MAX = 32768;

/** Idempotent assignment: a retry cannot toggle a conversation back. */
export function setConversationArchiveOperation(
  _trackId: string, cardId: string, archived: boolean,
): ApiOperation<undefined> {
  return {
    method: 'PATCH',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}`,
    body: { archived },
    responseSchema: z.union([agentSessionDtoSchema.transform(() => undefined), z.undefined()]),
  };
}

export function agentSessionDtoToConversation(dto: AgentSessionDto): Conversation {
  const provider = dto.provider;
  const stateParsed = conversationStateSchema.safeParse(dto.status === 'queued' ? 'turn_pending' : dto.status);
  return {
    id: dto.id,
    trackId: dto.trackId,
    archivedAt: dto.archivedAtMs ?? null,
    title: dto.title ?? null,
    kind: 'agent',
    isLead: dto.isLead,
    ...(dto.sourceSessionId == null ? {} : { sourceCardId: dto.sourceSessionId }),
    provider,
    state: stateParsed.success ? stateParsed.data : 'idle',
    updatedAt: dto.updatedAtMs,
    lastTurnCompletedAt: dto.lastNotifiableTurnAtMs ?? null,
  };
}

/**
 * `idempotencyKey` identifies the draft, so a key minted per call could create a second
 * conversation after a timeout.
 */
export function listSessionsOperation(trackId: string, includeArchived = false, parentSessionId?: string) {
  return { method: 'GET', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/sessions?includeArchived=${includeArchived}${parentSessionId === undefined ? '' : `&parentSessionId=${encodeURIComponent(parentSessionId)}`}`,
    responseSchema: z.array(sessionSummarySchema) } satisfies ApiOperation<unknown>;
}

export function createTrackConversationOperation(
  trackId: string, text: string, idempotencyKey: string, provider: AgentProvider, selection: ModelSelection, side?: SideConversation, permissionMode?: string | null,
  attachments: readonly MultipartFile[] = [],
): ApiOperation<CreatedConversation & Readonly<{ session: AgentSessionDto }>> {
  const derivedTitle = conversationNameFrom(text);
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/sessions`,
    headers: { 'Idempotency-Key': idempotencyKey },
    body: imageMessageBody({
      ...(derivedTitle === null ? {} : { title: derivedTitle }),
      prompt: text,
      ...(side?.context ? { sourceExcerpt: side.context } : {}),
      provider,
      ...(side === undefined ? {} : { sourceSessionId: side.source_card_id }),
      ...(selection.model === null ? {} : { model: selection.model }),
      ...(selection.reasoning_effort === null ? {} : { reasoningEffort: selection.reasoning_effort }),
      ...(permissionMode == null ? {} : { permissionMode }),
    }, attachments),
    responseSchema: sessionCreationReceiptSchema.refine(result => result.firstInput != null && result.session.trackId === trackId
      && (side === undefined || result.session.sourceSessionId === side.source_card_id))
        .transform((result) => ({
          session: result.session,
          conversation: agentSessionDtoToConversation(result.session),
          sessionInput: result.firstInput!,
        })),
  };
}

/** What a failed create means for the draft; a 409 here is four distinguishable situations. */
export type ConversationCreateFailure = Readonly<
  | {
    /**
     * Ambiguous: the attempt may have committed. Keep the key and the text, re-read the list. A 503 is one too: on this
     * endpoint every 503 comes after the card was minted.
     */
    kind: 'retry';
    message: string;
  }
  | {
    /** The derived card already exists — the list is behind, not the draft. */
    kind: 'exists';
    message: string;
  }
  | {
    /** Refused before anything could commit, so the key is unspent; a 400 is a refusal of the body itself. */
    kind: 'blocked';
    message: string;
  }
  | {
    /** This key was already spent on a different first message. */
    kind: 'stale-payload';
    message: string;
  }
  | {
    /** The key used up its retry slots or was refused as invalid; only a new key can go anywhere. */
    kind: 'exhausted';
    message: string;
  }
  | {
    /** The Track is gone; there is nowhere to put the draft. */
    kind: 'gone';
    message: string;
  }
>;

/**
 * What a failed create means for the draft; a 409 is told apart by its `code`. A lost or unreadable
 * answer may have been served, so it is `retry`, as is a concurrent create under the same key and a 503.
 */
export const CONVERSATION_CREATE_FAILURES: FailureTable<ConversationCreateFailure['kind']> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ code: 'idempotency_key_exhausted', is: 'exhausted' as const }),
    Object.freeze({ code: 'idempotency_key_invalid', is: 'exhausted' as const }),
    Object.freeze({ code: 'idempotency_key_reused', is: 'stale-payload' as const }),
    Object.freeze({ code: 'idempotency_key_concurrent', is: 'retry' as const }),
    Object.freeze({ status: Object.freeze([404]), is: 'gone' as const }),
    Object.freeze({ code: 'attachments_unsupported', is: 'blocked' as const }),
    Object.freeze({ status: Object.freeze([400, 413]), is: 'blocked' as const }),
    Object.freeze({ status: Object.freeze([409]), is: 'exists' as const }),
  ]),
  unauthorized: 'retry',
  otherwise: 'retry',
});

/** A create refused (when the server gave no reason) and one whose outcome is unknown; the draft keeps its words either way. */
export const CONVERSATION_CREATE_TEXT: WriteText = Object.freeze({
  refused: 'The conversation was not started.',
  unknown: 'Starting the conversation is unconfirmed. Try again to check the same conversation.',
});

/**
 * What a failed create (`null`: no answer was read) means for the draft, with its sentence: `retry` says only that it is
 * unconfirmed, every other kind the server's reason. No transport text shows; the connection is the global indicator's.
 */
export function conversationCreateFailure(failure: ApiFailure | null): ConversationCreateFailure {
  const kind = classifyFailure(failure, CONVERSATION_CREATE_FAILURES);
  return { kind, message: kind === 'retry' ? CONVERSATION_CREATE_TEXT.unknown : refusedText(failure, CONVERSATION_CREATE_TEXT.refused) };
}

export const SYSTEM_PRESENTATION_LABELS: Readonly<
Record<SystemInputPresentation, string>
> = Object.freeze({
  system: 'System update',
  system_worker_turn_finished: 'Worker turn finished',
  system_report_edited: 'Report edited',
  system_task_completed: 'Task completed',
  system_task_failed: 'Task failed',
  system_mail: 'Mail',
  system_planner_turn_budget: 'Planner turn budget',
  system_planner_context_usage: 'Planner context usage',
});

/** Legacy event spelling, used only by event invalidation. */
export function isUserMessage(itemType: string | null): boolean {
  return itemType === 'userMessage' || itemType === 'user_message';
}

export function isPlannerTurnContext(entry: TranscriptEntry): entry is ConversationSystemEntry {
  return entry.author === 'system' && (entry.label === SYSTEM_PRESENTATION_LABELS.system_planner_turn_budget
    || entry.label === SYSTEM_PRESENTATION_LABELS.system_planner_context_usage);
}

/* What the agent did between two things it said: one line each, a verb and its target.
   The kernel persists both `item/started` and `item/completed`, so the running state is real data. */
export type ActivityState = 'running' | 'done' | 'failed';

export type ConversationActivity = Readonly<{
  id: string;
  /** Discriminates against `ConversationTurn` inside one sorted transcript. */
  author: 'activity';
  /** Present tense while running, past tense once done: `Running` / `Ran`. */
  verb: string;
  /** What it acted on, already trimmed to something readable. Never a payload. */
  target: string | null;
  state: ActivityState;
  /**
   * Straight off `item/completed`'s own `durationMs`; pairing `started` with `completed` would
   * measure our poll, not the action.
   */
  durationMs: number | null;
  /** Why it failed, in one clipped line; `null` on anything that did not fail. */
  detail: string | null;
  /** Optional provider-supplied thinking or tool body, read through the activity disclosure. */
  content?: string;
  /** The wire name of the tool on an `mcpToolCall` row, verbatim; `null` on every other item type. */
  tool: string | null;
  atMs: number;
}>;

/** How a turn ended. `completed` entries are kept but render as nothing; only `interrupted` and `failed` are drawn. */
export type TurnOutcomeStatus = 'completed' | 'interrupted' | 'failed';

export type ConversationTurnOutcome = Readonly<{
  id: string;
  /** Discriminates against the speakers and the activity line. */
  author: 'turn';
  /** Historical fact; cannot describe or authorize the current execution. */
  imported?: boolean;
  turnId: string;
  status: TurnOutcomeStatus;
  /** The recorded `error.message`, verbatim; interrupted turns may also carry a reason. */
  message?: string;
  /** The kernel's readable reason (`turn_error_text`) for an interrupted or failed turn. */
  text?: string;
  /** `error.codexErrorInfo` as one token: the bare enum string or, for the object form, its single key. */
  code?: string;
  /** Protected kernel metadata, separate from provider error/code. Unknown shapes are ignored without losing the outcome. */
  harnessInterruption?: HarnessInterruptionReason | HarnessTimeoutEvidence;
  /**
   * The wire `status` when it was not one of the three above; such a row is surfaced as `failed`
   * rather than dropped.
   */
  rawStatus?: string;
  /** The row's own `durationMs`; `null` when it carries none, meaning unknown — never derived from message or tool times. */
  elapsedMs: number | null;
  atMs: number;
}>;

export type TranscriptEntry = NativeTurnContext & (ConversationMessage | ConversationActivity | ConversationTurnOutcome);

/** The speakers; neither an activity line nor a turn outcome is one. */
export function isConversationMessage(entry: TranscriptEntry): entry is ConversationMessage {
  return entry.author === 'you' || entry.author === 'human' || entry.author === 'unknown' || entry.author === 'agent' || entry.author === 'system';
}

/** `bash -lc '…'` is how codex spells every command; the line shows what was actually run. */
const SHELL_WRAPPER = /^(?:\S*\/)?(?:ba|z|)sh\s+-l?c\s+(['"])([\s\S]*)\1$/;

export function readableCommand(command: string): string {
  const match = SHELL_WRAPPER.exec(command.trim());
  return (match?.[2] ?? command).trim();
}

/**
 * Readable thoughts remain available; an empty `Thought` status survives only until later activity. Shared by
 * stored projection and live display so they agree on which thought is followed.
 */
export function retireFollowedThoughts(entries: readonly TranscriptEntry[]): readonly TranscriptEntry[] {
  return entries.filter((entry, index) => {
    if (entry.author !== 'activity' || entry.verb !== 'Thought' || entry.content !== undefined) return true;
    return !entries.slice(index + 1).some((later) => later.author !== 'turn');
  });
}

/** Append entries the server has not stored yet (optimistic user echoes, live replies), retiring the thought they now follow. */
export function mergeTranscript(
  serverEntries: readonly TranscriptEntry[],
  echoes: readonly TranscriptEntry[],
): readonly TranscriptEntry[] {
  return echoes.length === 0 ? serverEntries : retireFollowedThoughts([...serverEntries, ...echoes]);
}

export function isOptimisticConversationTurn(
  entry: TranscriptEntry,
): entry is OptimisticConversationTurn {
  return entry.author === 'you' && 'localSend' in entry && entry.localSend === true;
}

/**
 * Whether this entry is a message the kernel queued; asked of a `TranscriptEntry` because the
 * renderer holds the merged transcript.
 */
export function isQueuedConversationTurn(entry: TranscriptEntry): boolean {
  return isOptimisticConversationTurn(entry) && entry.queued;
}

/** User-role display data includes unknown historical authors; this is never an ownership test. */
export function isUserConversationTurn(entry:TranscriptEntry|undefined):entry is ConversationTurn & Readonly<{ author: 'you' | 'human' | 'unknown' }> {
  return entry?.author === 'you' || entry?.author === 'human' || entry?.author === 'unknown';
}

/** An exchange opens at a user-role message after an agent/activity boundary. */
export function opensExchange(turns: readonly TranscriptEntry[], index: number): boolean {
  const turn = turns[index];
  if (turn === undefined) return false;
  return isUserConversationTurn(turn) && turn.supplemental !== true && !isUserConversationTurn(turns[index-1]);
}

/** The gap after which a transcript is worth stamping with a time; the time is a separator, not a label. */
export const CONVERSATION_GAP_MS = 10 * 60 * 1000;

export function opensAfterGap(turns: readonly TranscriptEntry[], index: number): boolean {
  const turn = turns[index];
  const previous = turns[index - 1];
  if (turn === undefined) return false;
  if (previous === undefined) return true;
  return turn.atMs - previous.atMs >= CONVERSATION_GAP_MS;
}
