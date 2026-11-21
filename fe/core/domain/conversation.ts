import { z } from 'zod';

import {
  agentSessionDtoSchema,
  bootstrapDtoSchema,
  normalizeWireAgentProvider,
  toWireAgentProvider,
  type AgentSessionDto,
} from '../api/schemas.js';

import { sessionCreationReceiptSchema, sessionMessageReceiptSchema, sessionSummarySchema, type SessionInputReceipt } from '../api/perga-v1.js';

import type {
  AgentProvider, HarnessInputPresentation,
  HarnessInterruptionReason,
  HarnessPhaseTag,
  HarnessTimeoutEvidence,
  PlannerAttachment, SystemInputPresentation, UploadAttachmentResponse,
} from '../api/generated/wire.js';
import type { ApiFailure, ApiOperation } from '../api/types.js';

import { classifyFailure, failureReason, refusedText, type FailureTable, type WriteFailure, type WriteText } from './failure-class.js';

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

export type SubmittedConversationInput = Readonly<{ text: string; atMs: number }>;

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
  attachments?: readonly PlannerAttachment[];
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

/** A local message while its request is unanswered; receipts end this state. */
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

const reasoningEffortOptionSchema = z.object({
  reasoning_effort: z.string(),
  /* codex's own words for its own setting, shown verbatim; `null` for a Claude effort level, which the CLI declares without one. */
  description: z.string().nullable(),
});

const catalogModelSchema = z.object({
  /* A React key and nothing else — the value that travels to the server is `model`. */
  id: z.string(),
  model: z.string(),
  /* The model this entry runs where the provider says (the Claude CLI's `resolvedModel`); `null` for codex. */
  resolved_model: z.string().nullable(),
  display_name: z.string(),
  description: z.string(),
  /* Which entry codex's own picker highlights, NOT what this installation follows (that is `default` below). */
  is_default: z.boolean(),
  /* A Claude entry's are the CLI's own `supportedEffortLevels`: empty when it declares none. */
  supported_reasoning_efforts: z.array(reasoningEffortOptionSchema),
  /* `null` exactly when the provider declares no default effort (every Claude entry, #1822). */
  default_reasoning_effort: z.string().nullable(),
});

/**
 * `GET /api/models`. `source` and `default_source` are separate answers: otherwise an empty live
 * catalog would read like one the server could not produce. A Claude Planner's catalog is the Claude
 * CLI's own list (#1822), `unavailable` while Claude is not ready; its CLI `default` entry is not a row
 * but the `default` here (`claude_cli`): what it resolves to, and the efforts the `null` selection takes.
 */
export const modelCatalogSchema = z.object({
  models: z.array(catalogModelSchema),
  default: z.object({
    model: z.string().nullable(),
    reasoning_effort: z.string().nullable(),
    /* `null` for codex, whose default's efforts are those of the entry `model` names. */
    supported_reasoning_efforts: z.array(reasoningEffortOptionSchema).nullable(),
  }),
  default_source: z.enum(['config_read', 'config_toml', 'claude_cli', 'acp_session', 'unknown']),
  source: z.enum(['live', 'unavailable']),
  fetched_at_ms: z.number().nullable(),
});

export type ModelCatalog = z.infer<typeof modelCatalogSchema>;

/**
 * Whose catalog: an existing card's, or one for a conversation about to be created on `provider`.
 */
export type ModelCatalogScope =
  | Readonly<{ kind: 'card'; cardId: string; provider: AgentProvider }>
  | Readonly<{ kind: 'provider'; provider: AgentProvider }>;

/** Derives the conversation provider's catalog and availability from `/api/v1/bootstrap`. */
export function modelCatalogOperation(
  scope: ModelCatalogScope, options?: Readonly<{ provider?: AgentProvider; refresh?: boolean }>,
): ApiOperation<ModelCatalog> {
  const refreshSuffix = options?.refresh === true ? '&refresh=true' : '';
  return {
    method: 'GET',
    path: scope.kind === 'card'
      ? `/api/v1/bootstrap?card_id=${encodeURIComponent(scope.cardId)}${refreshSuffix}`
      : `/api/v1/bootstrap?provider=${scope.provider}${refreshSuffix}`,
    responseSchema: z.union([bootstrapDtoSchema.transform((bootstrap): ModelCatalog => {
      const providerStatus = bootstrap.providers.find((p) => normalizeWireAgentProvider(p.provider) === scope.provider);
      const richCatalog = providerStatus?.modelCatalog ?? [];
      if (richCatalog.length > 0) {
        const models = richCatalog.map((entry) => ({
          id: entry.model,
          model: entry.model,
          resolved_model: null,
          display_name: entry.displayName,
          description: entry.description,
          is_default: entry.isDefault,
          supported_reasoning_efforts: entry.supportedReasoningEfforts.map((effort) => ({
            reasoning_effort: effort.reasoningEffort,
            description: effort.description,
          })),
          default_reasoning_effort: entry.defaultReasoningEffort,
        }));
        const defaultEntry = models.find((entry) => entry.is_default);
        return {
          models,
          default: {
            model: defaultEntry?.model ?? null,
            reasoning_effort: defaultEntry?.default_reasoning_effort ?? null,
            supported_reasoning_efforts: defaultEntry?.supported_reasoning_efforts ?? null,
          },
          default_source: defaultEntry ? 'config_read' : 'unknown',
          source: providerStatus?.available ? 'live' : 'unavailable',
          fetched_at_ms: Date.now(),
        };
      }
      const providerModels = providerStatus?.models ?? [];
      const unique = [...new Set(providerModels)];
      return {
        models: unique.map((model, index) => ({
          id: model,
          model,
          resolved_model: null,
          display_name: model,
          description: '',
          is_default: index === 0,
          supported_reasoning_efforts: [],
          default_reasoning_effort: null,
        })),
        default: {
          model: unique[0] ?? null,
          reasoning_effort: null,
          supported_reasoning_efforts: null,
        },
        default_source: 'unknown',
        source: providerStatus?.available ? 'live' : 'unavailable',
        fetched_at_ms: Date.now(),
      };
    }), modelCatalogSchema]),
  };
}

export type ModelSelectionResult = AgentSessionDto;

/**
 * Run writes one at a time, keeping only the LATEST waiting intent while one is in flight;
 * a rejected write hands the queue on rather than stranding it. Orders one client's own
 * writes only.
 */
export function createSerialWriter<TArgs, TResult>(
  write: (args: TArgs) => Promise<TResult>,
): (args: TArgs) => Promise<TResult> {
  let inFlight: Promise<TResult> | null = null;
  let queued: TArgs | null = null;
  let queuedIsSet = false;

  const drain = async (args: TArgs): Promise<TResult> => {
    let current = args;
    for (;;) {
      let result: TResult;
      try {
        result = await write(current);
      } catch (error) {
        /* Nothing superseded this one, so its failure is the chain's answer. */
        if (!queuedIsSet) throw error;
        current = takeQueued();
        continue;
      }
      if (!queuedIsSet) return result;
      current = takeQueued();
    }
  };

  const takeQueued = (): TArgs => {
    const next = queued as TArgs;
    queuedIsSet = false;
    queued = null;
    return next;
  };

  return (args: TArgs): Promise<TResult> => {
    if (inFlight === null) {
      const run = drain(args).finally(() => { inFlight = null; });
      inFlight = run;
      return run;
    }
    /* Supersede rather than append: an intent nobody can still see the effect of is not worth a round trip. */
    queued = args;
    queuedIsSet = true;
    return inFlight;
  };
}

export function setPlannerModelOperation(cardId: string, selection: ModelSelection): ApiOperation<ModelSelectionResult> {
  return {
    method: 'PATCH',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}`,
    body: { model: selection.model, reasoningEffort: selection.reasoning_effort },
    responseSchema: agentSessionDtoSchema,
  };
}

/**
 * What a failed `PATCH /api/v1/sessions/{sessionId}` says. Each listed status is answered before anything is stored:
 * 400, 403, 404 and 422. A 5xx, a lost or an unreadable answer may follow a stored one.
 */
export const PLANNER_MODEL_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'precondition_failed', is: 'refused' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

/** What a failed model change says (`refusalText`): not changed, or, when it may have been stored, unconfirmed. */
export const MODEL_CHANGE_TEXT = Object.freeze({ refused: 'The model was not changed.', unknown: 'The model change is unconfirmed.' });

/** What a `POST /planner/input` answers, including where the text landed. */
export function steerQueuedSessionMessageOperation(sessionId: string, inputId: string, turnId: string) {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/messages`,
    body: { mode: 'steer', messageId: inputId, turnId },
    responseSchema: sessionMessageReceiptSchema,
  } satisfies ApiOperation<unknown>;
}

export function removeQueuedSessionMessageOperation(sessionId: string, inputId: string) {
  return {
    method: 'DELETE',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/messages/${encodeURIComponent(inputId)}`,
    responseSchema: z.undefined(),
  } satisfies ApiOperation<unknown>;
}

export type SentPlannerInput = Readonly<{
  card_id: string;
  worker_session_id: string;
  /** The queue entry the text is now sitting in, or `null` when it folded into an entry that has no id. */
  entry_id: string | null;
  queued?: boolean | undefined;
  delivery?: string;
  turnId?: string;
}>;

/**
 * One send, keyed: the same `idempotencyKey` on a retry is answered with the first request's answer
 * and queues nothing, so a send whose answer was lost can be retried safely.
 */
export function sendPlannerInputOperation(
  cardId: string,
  text: string,
  _attachments: readonly string[],
  idempotencyKey: string,
  steerIfRunning = false,
  queueIfBusy = false,
  expectedEpoch?: number,
): ApiOperation<SentPlannerInput> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/messages`,
    timeoutMs: 10 * 60_000,
    body: { content: text, mode: steerIfRunning ? 'steer' : queueIfBusy ? 'queue' : 'default',
      ...(expectedEpoch === undefined ? {} : { expectedEpoch }) },
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: sessionMessageReceiptSchema.refine(receipt => receipt.sessionId === cardId).transform(receipt => ({
      card_id: receipt.sessionId, worker_session_id: receipt.sessionId, entry_id: receipt.messageId, delivery: receipt.delivery,
      ...(receipt.turnId == null ? {} : { turnId: receipt.turnId }), queued: receipt.delivery === 'queued',
    })),
  };
}

export function recoverSessionOperation(sessionId: string, mode: 'reconnect' | 'reset_context') {
  return { method: 'POST', path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/control`,
    body: { action: mode }, timeoutMs: 30_000,
    responseSchema: agentSessionDtoSchema.refine(session => session.id === sessionId),
  } satisfies ApiOperation<unknown>;
}

/** The image formats the upload endpoint accepts; the server's magic-number sniff is the judgement. */
export const ATTACHABLE_IMAGE_TYPES = Object.freeze(
  ['image/png', 'image/jpeg', 'image/gif', 'image/webp'] as const,
);

/** Mirrors `MAX_ATTACHMENTS_PER_MESSAGE` in `planner_attachments::bind`. */
export const MAX_ATTACHMENTS_PER_MESSAGE = 8;

/** Why the composer took no more images: its one cap, for a pick and a refill alike. */
export const TOO_MANY_IMAGES = `A message can carry at most ${MAX_ATTACHMENTS_PER_MESSAGE} images.`;

/**
 * What a failed `POST /planner/attachments` says: 400 (not an accepted image, an attached workspace, the card's
 * budget spent, a body that stopped arriving), 403, 404 and 413 store nothing; anything else may have stored it.
 */
export const PLANNER_ATTACHMENT_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 404, 413]), is: 'refused' as const })]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

/** What a failed upload says (`refusalText`): not uploaded, or, when it may have been stored, could not be. */
export const UPLOAD_TEXT = Object.freeze({ refused: 'The image was not uploaded.', unknown: 'The image could not be uploaded.' });

/**
 * Upload one image as raw bytes; `content-type` here is merged *over* the `application/json` the
 * client adds for any body.
 */
export function uploadPlannerAttachmentOperation(
  cardId: string, bytes: Uint8Array, contentType: string,
): ApiOperation<UploadAttachmentResponse> {
  return {
    method: 'POST',
    path: `/api/cards/${encodeURIComponent(cardId)}/planner/attachments`,
    body: bytes,
    headers: { 'content-type': contentType },
    responseSchema: z.object({
      attachmentId: z.string(), contentType: z.string(), size: z.number(), url: z.string(),
    }),
  };
}

export type PlannerQueueEditOutcome =
  | Readonly<{ kind: 'done'; entry: PendingQueueEntry }>
  | Exclude<PlannerQueueWriteOutcome, { kind: 'done' }>;

const plannerInputStaleSchema = z.object({ text: z.string(), rev: z.number(), entry_id: z.string() });

/**
 * What one write to the pending queue turned into. `stale` and `gone` call for opposite next
 * moves: retry against the quoted revision, versus stop, the message is on its way.
 */
export type PlannerQueueWriteOutcome =
  | Readonly<{ kind: 'done' }>
  | Readonly<{ kind: 'stale'; entryId: string; text: string; rev: number }>
  | Readonly<{ kind: 'gone' }>
  | Readonly<{ kind: 'not_running' }>
  | Readonly<{ kind: 'unanswered' }>
  | Readonly<{ kind: 'failed'; message: string }>;

/**
 * What a failed delete or steer of a queued message means. `stale`: a lost compare-and-swap, whose
 * body quotes the text and revision the entry actually holds. `gone`: the queue drained or somebody
 * else deleted the entry, beyond editing. The steer's own 409s: `not_running`, no turn took the
 * message and it is still queued unchanged; `unanswered`, codex never answered, so "nothing
 * happened" cannot be claimed.
 */
export const PLANNER_QUEUE_WRITE_FAILURES: FailureTable<Exclude<PlannerQueueWriteOutcome['kind'], 'done'>> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), code: 'planner_input_stale', is: 'stale' as const }),
    Object.freeze({ status: Object.freeze([404]), is: 'gone' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'planner_steer_no_running_turn', is: 'not_running' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'planner_steer_unknown_outcome', is: 'unanswered' as const }),
  ]),
  unauthorized: 'failed',
  otherwise: 'failed',
});

/**
 * Classifies a rejected queue write. Never called for a success. `failed` shows the server's own
 * sentence when it gave one, else `fallback`; a `stale` whose body is unreadable is `failed` too.
 */
export function plannerQueueWriteFailure(
  failure: ApiFailure | null, fallback: string,
): Exclude<PlannerQueueWriteOutcome, { kind: 'done' }> {
  const kind = classifyFailure(failure, PLANNER_QUEUE_WRITE_FAILURES);
  const stale = kind === 'stale' && failure?.kind === 'http' ? plannerInputStaleSchema.safeParse(failure.body) : null;
  if (stale?.success === true) return { kind: 'stale', entryId: stale.data.entry_id, text: stale.data.text, rev: stale.data.rev };
  if (kind === 'failed' || kind === 'stale') {
    return { kind: 'failed', message: failure?.kind === 'http' && failure.message !== '' ? failureReason(failure) : fallback };
  }
  return { kind };
}

export const COMPACT_FAILURES: FailureTable<WriteFailure> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 404, 409, 422]), is: 'refused' as const })]),
  unauthorized: 'refused', otherwise: 'unknown',
});
export const COMPACT_TEXT = Object.freeze({ refused: 'Context was not compacted.', unknown: 'Context compaction is unconfirmed. Refresh before trying again.' });

export function compactPlannerOperation(cardId: string): ApiOperation<{ started: boolean }> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/control`,
    body: { action: 'compact' },
    timeoutMs: 10 * 60_000,
    responseSchema: agentSessionDtoSchema.transform(() => ({ started: true })),
  };
}

export function interruptPlannerOperation(cardId: string, worker?: { epoch?: number; turnId: string }): ApiOperation<{ stopped: boolean }> {
  return {
    method: 'POST',
    path: `/api/v1/sessions/${encodeURIComponent(cardId)}/control`,
    body: { action: 'stop', turnId: worker?.turnId, expectedEpoch: worker?.epoch },
    responseSchema: agentSessionDtoSchema.transform(() => ({ stopped: true })),
  };
}

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
  const provider = normalizeWireAgentProvider(dto.provider) ?? 'codex';
  const stateParsed = conversationStateSchema.safeParse(dto.status === 'queued' ? 'turn_pending' : dto.status);
  return {
    id: dto.id,
    trackId: dto.trackId,
    archivedAt: dto.archivedAtMs ?? null,
    title: dto.title ?? null,
    kind: 'agent',
    isLead: dto.isLead,
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
): ApiOperation<CreatedConversation & Readonly<{ session: AgentSessionDto }>> {
  const derivedTitle = conversationNameFrom(text);
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/sessions`,
    headers: { 'Idempotency-Key': idempotencyKey },
    body: {
      ...(derivedTitle === null ? {} : { title: derivedTitle }),
      prompt: text,
      provider: toWireAgentProvider(provider),
      ...(side === undefined ? {} : { systemPrompt: side.context }),
      ...(selection.model === null ? {} : { model: selection.model }),
      ...(selection.reasoning_effort === null ? {} : { reasoningEffort: selection.reasoning_effort }),
      ...(permissionMode == null ? {} : { permissionMode }),
    },
    responseSchema: sessionCreationReceiptSchema.refine(result => result.firstInput != null && result.session.trackId === trackId)
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
    Object.freeze({ status: Object.freeze([400]), is: 'blocked' as const }),
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
