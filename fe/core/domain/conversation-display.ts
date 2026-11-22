/** One typed read contract over the existing transcript resource. Display identities are not native turns. */

import { DEPLOYMENT_OWNER_USER_ID } from '../api/auth.js';
import type { ConversationCursor, ConversationDisplayItem as WireDisplayItem } from './presentation-types.js';
import { sessionItemsPageDtoSchema, type SessionItemsPageDto } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import { thinkingActivityContent,toolActivityContent } from './conversation-activity-content.js';
import type { LiveReplyTranscriptRow } from './conversation-live.js';
import { conversationToolQuestion,conversationToolShape } from './conversation-tool.js';
import { retireFollowedThoughts,SYSTEM_PRESENTATION_LABELS,systemInputPresentationSchema,type TranscriptEntry } from './conversation.js';

/** Authored fixtures and canonical human/system metadata use the same renderer contract. */
type AuthoredSource = Exclude<Extract<WireDisplayItem['source'], { kind: 'human' | 'system' | 'removed' }>, { kind: 'system' }>
  | (Extract<WireDisplayItem['source'], { kind: 'system' }> & { statusLabel?: string });
export type ConversationDisplayItem = Readonly<{ cursor: ConversationCursor; source: AuthoredSource
  | { kind: 'entry'; entry: TranscriptEntry; observation?: LiveReplyTranscriptRow & { id: number; outcome: boolean } } }>;
export type ConversationPage = Readonly<{ contract: 'conversation_v1'; source: 'presentation' | 'historical_raw'; items: ConversationDisplayItem[]; next_cursor: ConversationCursor | null }>;
const TRANSCRIPT_PAGE_LIMIT = 60;

export function nativeTranscriptEntryId(threadId: string, itemId: string | null, index: number): string {
  return `native:${encodeURIComponent(JSON.stringify([threadId, itemId]))}:${index}`;
}

export function sessionItemsPageDtoToConversationPage(
  card: string,
  page: SessionItemsPageDto,
  cursor: ConversationCursor | null,
): ConversationPage {
  const threadId = page.session.nativeThreadId || `epoch-${page.session.epoch}`;
  const resultsByCallId = new Map<string, { output: unknown; isError: boolean; createdAtMs: number }>();
  for (const item of page.items) {
    if (item.kind !== 'tool_result') continue;
    const payloadObj = typeof item.payload === 'object' && item.payload !== null
      ? (item.payload as Record<string, unknown>)
      : {};
    if (typeof payloadObj.callId === 'string') {
      resultsByCallId.set(payloadObj.callId, {
        output: payloadObj.output ?? null,
        isError: payloadObj.isError === true,
        createdAtMs: item.createdAtMs,
      });
    }
  }

  const windowItems = cursor === null
    ? page.items
    : page.items.filter((item) => item.id < cursor.position);
  const sliced = windowItems.slice(-TRANSCRIPT_PAGE_LIMIT);
  const hasOlder = page.hasMoreBefore === true || windowItems.length > sliced.length;

  const displayItems: ConversationDisplayItem[] = sliced.flatMap((item): ConversationDisplayItem[] => {
    const itemCursor: ConversationCursor = { contract: 'conversation_v1', card_id: card, source: 'presentation', position: item.id };
    const payloadObj = typeof item.payload === 'object' && item.payload !== null
      ? (item.payload as Record<string, unknown>)
      : {};

    if (item.kind === 'epoch_boundary') return [{ cursor: itemCursor, source: {
      kind: 'system', observation_id: `epoch-${item.id}`, presentation: 'system',
      text: `${payloadObj.reason === 'compacted' || payloadObj.reason === 'compact' ? '已压缩上下文' : '已重置上下文'} · Epoch ${String(payloadObj.epoch)}`,
      created_at_ms: item.createdAtMs,
    } }];

    const interrupted = item.turnId === null && payloadObj.archived === true
      && payloadObj.status === 'interrupted' && payloadObj.reason === 'session_reset';
    if (item.kind === 'system') return [{ cursor: itemCursor, source: {
      kind: 'system', observation_id: typeof payloadObj.wakeId === 'string' ? payloadObj.wakeId : String(item.id),
      presentation: 'system', text: typeof payloadObj.content === 'string' ? payloadObj.content
        : typeof payloadObj.message === 'string' ? payloadObj.message : JSON.stringify(item.payload),
      ...(interrupted ? { statusLabel: 'Interrupted · Unsent' }
        : payloadObj.reason === 'max_attempts_exceeded' ? { statusLabel: 'Wake delivery failed · Retry limit reached' } : {}),
      created_at_ms: item.createdAtMs,
    } }];

    if (item.kind === 'user') {
      const content = typeof payloadObj.content === 'string' ? payloadObj.content : '';
      const triggerKind = typeof payloadObj.triggerKind === 'string' ? payloadObj.triggerKind : 'user';
      if (triggerKind === 'wake' || triggerKind === 'scheduler' || triggerKind === 'system') {
        const presentation = systemInputPresentationSchema.safeParse(payloadObj.presentation);
        return [{
          cursor: itemCursor,
          source: {
            kind: 'system',
            observation_id: String(item.id),
            presentation: presentation.success ? presentation.data : 'system',
            text: content,
            created_at_ms: item.createdAtMs,
          },
        }];
      }
      return [{
        cursor: itemCursor,
        source: {
          kind: 'human',
          message_id: typeof payloadObj.inputId === 'string' ? payloadObj.inputId : `msg-${item.id}`,
          revision: 1,
          author: { user_id: DEPLOYMENT_OWNER_USER_ID, display_name: 'Owner' },
          text: content,
          ...(payloadObj.steered === true ? { supplemental: true } : {}),
          ...(item.messageStatus == null ? {} : { message_status: item.messageStatus }),
          ...(interrupted ? { delivery_error: 'Interrupted · Unsent' }
            : typeof payloadObj.deliveryError === 'string' ? { delivery_error: payloadObj.deliveryError } : {}),
          attachments: [],
          created_at_ms: item.createdAtMs,
        },
      }];
    }

    const context = item.turnId === null ? {} : { nativeTurn: { threadId, turnId: item.turnId } };
    const outcome = item.kind === 'turn_outcome' || item.kind === 'turn_error' || item.kind === 'error';
    const callId = typeof payloadObj.callId === 'string' ? payloadObj.callId : `call-${item.id}`;
    const itemId = item.kind === 'tool_call' ? callId : `${item.kind === 'thinking' ? 'thinking' : 'item'}-${item.id}`;
    const completed = item.kind === 'tool_call' ? resultsByCallId.get(callId) : undefined;
    const observation = { id: item.id, outcome, itemId, threadId, turnId: item.turnId,
      kind: item.kind, completed: item.kind !== 'tool_call' || completed !== undefined };
    const base = { id: nativeTranscriptEntryId(threadId, outcome ? `outcome-${item.id}` : itemId, 0), atMs: item.createdAtMs, ...context };
    let entry: TranscriptEntry;
    if (item.kind === 'assistant_text') {
      const text = typeof payloadObj.text === 'string' ? payloadObj.text : '';
      entry = { ...base, author: 'agent', text };
    } else if (item.kind === 'thinking') {
      const text = typeof payloadObj.text === 'string' ? payloadObj.text : '';
      entry = { ...base, author: 'activity', verb: 'Thought', target: null, state: 'done', durationMs: null,
        detail: null, tool: null, ...thinkingActivityContent(text) };
    } else if (item.kind === 'tool_call') {
      const name = typeof payloadObj.name === 'string' ? payloadObj.name : 'tool';
      const args = payloadObj.args ?? {};
      const shape = conversationToolShape(name, args);
      const failed = completed?.isError === true;
      const error = completed?.output;
      const detail = typeof error === 'string' ? error : typeof error === 'object' && error !== null
        && 'message' in error && typeof error.message === 'string' ? error.message : null;
      const question = completed !== undefined && !failed ? conversationToolQuestion(name, args) : null;
      entry = question === null ? { ...base, author: 'activity', verb: completed === undefined ? shape.running : shape.done, target: shape.target,
        state: failed ? 'failed' : completed === undefined ? 'running' : 'done', durationMs: null,
        detail: failed ? detail : null, tool: name,
        ...toolActivityContent(args, completed?.output, failed),
        atMs: completed?.createdAtMs ?? item.createdAtMs }
        : { ...base, author: 'agent', text: question, origin: 'ask', atMs: completed?.createdAtMs ?? item.createdAtMs };
    } else if (outcome) {
      const status = typeof payloadObj.status === 'string' ? payloadObj.status : 'failed';
      const error = typeof payloadObj.error === 'string' ? payloadObj.error : typeof payloadObj.errorText === 'string'
        ? payloadObj.errorText : typeof payloadObj.message === 'string' ? payloadObj.message : null;
      entry = { ...base, author: 'turn', turnId: item.turnId ?? `turn-${item.id}`,
        status: status === 'completed' || status === 'interrupted' ? status : 'failed',
        ...(status === 'completed' || status === 'interrupted' || status === 'failed' ? {} : { rawStatus: status }),
        ...(error === null ? {} : { message: error, text: error }), elapsedMs: null };
    } else return [];
    return [{ cursor: itemCursor, source: { kind: 'entry', entry, observation } }];
  });

  const activeTurn = page.activeTurn;
  if (cursor === null && activeTurn != null && (activeTurn.status === 'failed' || activeTurn.status === 'interrupted')
    && !sliced.some(item => (item.kind === 'turn_outcome' || item.kind === 'turn_error' || item.kind === 'error') && item.turnId === activeTurn.id)) {
    const position = (sliced.at(-1)?.id ?? 0) + 1;
    displayItems.push({ cursor: { contract: 'conversation_v1', card_id: card, source: 'presentation', position },
      source: { kind: 'entry', entry: { id: `outcome-turn-${activeTurn.id}`, author: 'turn', turnId: activeTurn.id,
        status: activeTurn.status, elapsedMs: null,
        nativeTurn: { threadId, turnId: activeTurn.id },
        ...(activeTurn.errorText == null ? {} : { message: activeTurn.errorText, text: activeTurn.errorText }),
        atMs: activeTurn.completedAtMs ?? activeTurn.startedAtMs ?? page.session.updatedAtMs } } });
  }
  const oldestPosition = sliced[0]?.id;
  return { contract: 'conversation_v1', source: 'presentation', items: displayItems,
    next_cursor: hasOlder && oldestPosition !== undefined
      ? { contract: 'conversation_v1', card_id: card, source: 'presentation', position: oldestPosition } : null };
}

export function conversationItemsOperation(card: string, cursor: ConversationCursor | null): ApiOperation<ConversationPage> {
  const cursorSuffix = cursor === null ? '' : `&beforeId=${cursor.position}`;
  return {
    method: 'GET',
    path: `/api/v1/sessions/${encodeURIComponent(card)}/items?afterId=0&limit=60&latest=true${cursorSuffix}`,
    responseSchema: sessionItemsPageDtoSchema.transform(page => sessionItemsPageDtoToConversationPage(card, page, cursor)),
  };
}

/** Persisted identity and completion facts for live reply retirement, without provider methods or params. */
export function nativeDisplayRows(items: readonly ConversationDisplayItem[]) {
  return items.flatMap(({ source }) => source.kind === 'entry' && source.observation !== undefined ? [source.observation] : []);
}

export function buildConversationTranscript(
  items: readonly ConversationDisplayItem[], viewerId: string | null, viewerIsOwner = false,
): readonly TranscriptEntry[] {
  return retireFollowedThoughts([...items].sort((a, b) => a.cursor.position - b.cursor.position).flatMap(({ source, cursor }): TranscriptEntry[] => {
    if (source.kind === 'removed') return [];
    if (source.kind === 'entry') return source.entry.author === 'agent' && source.entry.text.trim() === '' ? []
      : [{ ...source.entry, displayPosition: cursor.position }];
    if (source.kind === 'system') return [{ id: `system:${source.observation_id}`, author: 'system',
      label: source.statusLabel ?? SYSTEM_PRESENTATION_LABELS[source.presentation], text: source.text,
      atMs: source.created_at_ms, displayPosition: cursor.position }];
    const authorId = source.author.user_id;
    const authoredByViewer = authorId === viewerId || (viewerIsOwner && authorId === DEPLOYMENT_OWNER_USER_ID);
    return [{ id: `human:${source.message_id}`, author: authoredByViewer ? 'you' : 'human',
      human: { userId: source.author.user_id, displayName: source.author.display_name, messageId: source.message_id, revision: source.revision },
      text: source.text, attachments: source.attachments, atMs: source.created_at_ms, displayPosition: cursor.position,
      ...(source.supplemental === true ? { supplemental: true } : {}),
      ...(source.message_status === undefined ? {} : { messageStatus: source.message_status }),
      ...(source.delivery_error === undefined ? {} : { deliveryError: source.delivery_error }) }];
  }));
}

/** Current response metadata has no transcript author, item identity or pagination position. */
export type CurrentConversationOutcome = Omit<import('./conversation.js').ConversationTurnOutcome, 'id' | 'author' | 'imported'>;
export type ConversationRun = SessionItemsPageDto;

export function conversationRunOperation(sessionId: string): ApiOperation<ConversationRun> {
  return {
    method: 'GET',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/items?afterId=0&limit=1`,
    responseSchema: sessionItemsPageDtoSchema,
  };
}

export function conversationRunPhase(run: ConversationRun): import('./presentation-types.js').HarnessPhaseTag {
  return run.state === 'running' ? 'turn_running' : run.state === 'stopping' ? 'issuing_interrupt'
    : run.state === 'compacting' ? 'compacting' : 'idle';
}

export function conversationRunningTurn(run: ConversationRun, now: number): import('./conversation.js').PlannerRunningTurn | null {
  const turn = run.activeTurn;
  return turn?.status === 'running'
    ? { turn_id: turn.id, elapsed_ms: Math.max(0, now - (turn.startedAtMs ?? now)) } : null;
}

export function conversationPendingQueue(run: ConversationRun): readonly import('./conversation.js').PendingQueueEntry[] {
  return run.queuedMessages?.map(message => ({ entry_id: message.id, text: message.text,
    rev: 1, queued_at_ms: message.queuedAtMs, state: message.state })) ?? [];
}
