/** One typed read contract over the existing transcript resource. Display identities are not native turns. */
import { z } from 'zod';
import { DEPLOYMENT_OWNER_USER_ID } from '../api/auth.js';
import type { ApiOperation } from '../api/types.js';
import {
  conversationCursorSchema,
  sessionItemsPageDtoSchema,
  type SessionItemsPageDto,
} from '../api/schemas.js';
import type { ConversationCursor, ConversationDisplayItem, ConversationPage, ConversationActiveInput, ConversationSubmissionDelivery, HarnessItem as TranscriptRow } from '../api/generated/wire.js';
import { messageStatusSchema, SYSTEM_PRESENTATION_LABELS, systemInputPresentationSchema, buildTranscript, retireFollowedThoughts, harnessItemSchema as transcriptRowSchema, HARNESS_ITEMS_PAGE_LIMIT as TRANSCRIPT_PAGE_LIMIT, type TranscriptEntry } from './conversation.js';

/** Native acknowledgement can arrive at END. Defer intervention for the exact
 * tracked execution without changing its delivery fact; other held inputs remain visible. */
export function heldConversationInputs(input: ConversationActiveInput | null, executionRunning: boolean): readonly ConversationSubmissionDelivery[] {
  return input?.submissions.filter(submission => {
    const attempt = submission.attempt;
    return attempt !== null && (attempt.delivery === 'crossing_unknown' || attempt.delivery === 'unknown')
      && (!executionRunning || submission.ordinal > 0 || attempt.resolution !== null || submission.logical_turn_id !== input.current_execution_id);
  }) ?? [];
}

const decimalU64 = z.string().regex(/^(0|[1-9]\d*)$/).pipe(z.string().refine(value => BigInt(value) <= 18446744073709551615n));
const keepResultSchema = z.object({ carrier_id:z.string(), submission_id:z.string(), attempt_id:z.string(),
  revision:decimalU64, waiting_for_settlement:z.boolean() }).strict();

export function keepConversationInputOperation(card: string, args: import('../api/generated/wire.js').KeepConversationInputRequest, key: string): ApiOperation<import('../api/generated/wire.js').KeepConversationInputResult> {
  return { method:'POST', path:`/api/cards/${encodeURIComponent(card)}/conversation/input/keep`,
    headers:{'Idempotency-Key':key}, body:args, responseSchema:keepResultSchema.refine(result =>
      result.carrier_id === args.carrier_id && result.submission_id === args.submission_id
      && result.attempt_id === args.attempt_id && result.revision === (BigInt(args.revision)+1n).toString(),
    'Keep receipt does not match its original CAS request') };
}

const retryResultSchema = z.object({...keepResultSchema.shape,retry_intent_id:z.string().min(1)}).strict();
export function retryConversationInputOperation(card: string, args: import('../api/generated/wire.js').RetryConversationInputRequest, key: string): ApiOperation<import('../api/generated/wire.js').RetryConversationInputResult> {
  return { method:'POST', path:`/api/cards/${encodeURIComponent(card)}/conversation/input/retry`,
    headers:{'Idempotency-Key':key}, body:args, responseSchema:retryResultSchema.refine(result =>
      result.carrier_id === args.carrier_id && result.submission_id === args.submission_id
      && result.attempt_id === args.attempt_id && result.revision === (BigInt(args.revision)+1n).toString(),
    'Retry receipt does not match its original CAS request') };
}
const attachment = z.object({ id: z.string(), size: z.number(), url: z.string(), contentType:z.string() });
const importedProvenance = z.object({kind:z.literal('unknown_legacy'),original_row_id:z.number().int().positive(),
  worker_session_id:z.string(),card_id:z.string(),track_id:z.string(),thread_id:z.string(),
  turn_id:z.string().nullable(),item_uuid:z.string().nullable(),item_type:z.string().nullable(),method:z.string(),
  created_at_ms:z.number(),original_digest:z.string().regex(/^sha256-v1:[0-9a-f]{64}$/),
  import_boundary_sequence:z.number().int().nonnegative()}).strict();
const identity = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('human'), message_id: z.string() }).strict(),
  z.object({ kind: z.literal('system'), observation_id: z.string() }).strict(),
  z.object({ kind: z.literal('native'), thread_id: z.string(), item_id: z.string() }).strict(),
  z.object({ kind:z.literal('imported'),provenance:importedProvenance }).strict(),
]);
export const conversationPageSchema: z.ZodType<ConversationPage> = z.object({
  contract: z.literal('conversation_v1'), source: z.enum(['presentation', 'historical_raw']),
  items: z.array(z.object({ cursor: conversationCursorSchema, source: z.discriminatedUnion('kind', [
    z.object({ kind: z.literal('human'), message_id: z.string(), revision: z.number().int().positive(),
      author: z.object({ user_id: z.string(), display_name: z.string() }).strict(), text: z.string(),
      attachments: z.array(attachment), created_at_ms: z.number(), delivery_error: z.string().optional(), message_status: messageStatusSchema.optional(), supplemental: z.boolean().optional() }).strict(),
    z.object({ kind: z.literal('system'), observation_id: z.string(), presentation: systemInputPresentationSchema,
      text: z.string(), created_at_ms: z.number() }).strict(),
    z.object({ kind: z.literal('native'), item: transcriptRowSchema }).strict(),
    z.object({ kind:z.literal('imported_item'),item:transcriptRowSchema,provenance:importedProvenance }).strict(),
    z.object({ kind: z.literal('historical_raw'), item: transcriptRowSchema }).strict(),
    z.object({ kind: z.literal('removed'), identity }).strict(),
  ]) }).strict()), next_cursor: conversationCursorSchema.nullable(),
}).strict();

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
          ...(typeof payloadObj.deliveryError === 'string' ? { delivery_error: payloadObj.deliveryError } : {}),
          attachments: [],
          created_at_ms: item.createdAtMs,
        },
      }];
    }

    if (item.kind === 'assistant_text') {
      const text = typeof payloadObj.text === 'string' ? payloadObj.text : '';
      return [{
        cursor: itemCursor,
        source: {
          kind: 'native',
          item: {
            id: item.id,
            worker_session_id: item.sessionId,
            card_id: card,
            track_id: page.session.trackId,
            thread_id: threadId,
            turn_id: item.turnId || null,
            item_uuid: `item-${item.id}`,
            item_type: 'agentMessage',
            method: 'item/completed',
            params: JSON.stringify({
              completedAtMs: item.createdAtMs,
              item: { id: `item-${item.id}`, type: 'agentMessage', text },
            }),
            created_at_ms: item.createdAtMs,
            turn_error_text: null,
          },
        },
      }];
    }

    if (item.kind === 'thinking') {
      const text = typeof payloadObj.text === 'string' ? payloadObj.text : '';
      return [{
        cursor: itemCursor,
        source: {
          kind: 'native',
          item: {
            id: item.id,
            worker_session_id: item.sessionId,
            card_id: card,
            track_id: page.session.trackId,
            thread_id: threadId,
            turn_id: item.turnId || null,
            item_uuid: `thinking-${item.id}`,
            item_type: 'reasoning',
            method: 'item/completed',
            params: JSON.stringify({
              completedAtMs: item.createdAtMs,
              item: { type: 'reasoning', summary: [{ text }] },
            }),
            created_at_ms: item.createdAtMs,
            turn_error_text: null,
          },
        },
      }];
    }

    if (item.kind === 'tool_call') {
      const callId = typeof payloadObj.callId === 'string' ? payloadObj.callId : `call-${item.id}`;
      const toolName = typeof payloadObj.name === 'string' ? payloadObj.name : 'tool';
      const args = payloadObj.args ?? {};
      const completed = resultsByCallId.get(callId);
      return [{
        cursor: itemCursor,
        source: {
          kind: 'native',
          item: {
            id: item.id,
            worker_session_id: item.sessionId,
            card_id: card,
            track_id: page.session.trackId,
            thread_id: threadId,
            turn_id: item.turnId || null,
            item_uuid: callId,
            item_type: 'mcpToolCall',
            method: completed !== undefined ? 'item/completed' : 'item/started',
            params: JSON.stringify({
              ...(completed !== undefined ? { completedAtMs: completed.createdAtMs } : {}),
              item: {
                type: 'mcpToolCall',
                tool: toolName,
                arguments: args,
                ...(completed !== undefined
                  ? completed.isError
                    ? { status: 'failed', error: completed.output }
                    : { status: 'completed', result: completed.output }
                  : {}),
              },
            }),
            created_at_ms: item.createdAtMs,
            turn_error_text: null,
          },
        },
      }];
    }

    if (item.kind === 'turn_outcome' || item.kind === 'turn_error' || item.kind === 'error') {
      const status = typeof payloadObj.status === 'string' ? payloadObj.status : 'failed';
      const errorText = typeof payloadObj.error === 'string'
        ? payloadObj.error
        : typeof payloadObj.errorText === 'string'
          ? payloadObj.errorText
          : typeof payloadObj.message === 'string'
            ? payloadObj.message
            : null;
      const turnId = item.turnId || `turn-${item.id}`;
      return [{
        cursor: itemCursor,
        source: {
          kind: 'native',
          item: {
            id: item.id,
            worker_session_id: item.sessionId,
            card_id: card,
            track_id: page.session.trackId,
            thread_id: threadId,
            turn_id: turnId,
            item_uuid: `outcome-${item.id}`,
            item_type: 'turn/completed',
            method: 'turn/completed',
            params: JSON.stringify({
              id: turnId,
              status,
              ...(errorText !== null ? { error: { message: errorText } } : {}),
            }),
            created_at_ms: item.createdAtMs,
            turn_error_text: errorText,
          },
        },
      }];
    }

    return [];
  });

  if (
    cursor === null
    && page.activeTurn !== null
    && page.activeTurn !== undefined
    && (page.activeTurn.status === 'failed' || page.activeTurn.status === 'interrupted')
    && !sliced.some((item) =>
      (item.kind === 'turn_outcome' || item.kind === 'turn_error' || item.kind === 'error')
      && item.turnId === page.activeTurn!.id)
  ) {
    const activeTurn = page.activeTurn;
    const syntheticId = (sliced.at(-1)?.id ?? 0) + 1;
    const errorText = activeTurn.errorText ?? null;
    displayItems.push({
      cursor: { contract: 'conversation_v1', card_id: card, source: 'presentation', position: syntheticId },
      source: {
        kind: 'native',
        item: {
          id: syntheticId,
          worker_session_id: page.session.id,
          card_id: card,
          track_id: page.session.trackId,
          thread_id: threadId,
          turn_id: activeTurn.id,
          item_uuid: `outcome-turn-${activeTurn.id}`,
          item_type: 'turn/completed',
          method: 'turn/completed',
          params: JSON.stringify({
            id: activeTurn.id,
            status: activeTurn.status,
            ...(errorText !== null ? { error: { message: errorText } } : {}),
          }),
          created_at_ms: activeTurn.completedAtMs ?? activeTurn.startedAtMs ?? Date.now(),
          turn_error_text: errorText,
        },
      },
    });
  }

  const oldestPosition = sliced[0]?.id;
  return {
    contract: 'conversation_v1',
    source: 'presentation',
    items: displayItems,
    next_cursor: hasOlder && oldestPosition !== undefined
      ? { contract: 'conversation_v1', card_id: card, source: 'presentation', position: oldestPosition }
      : null,
  };
}

export function conversationItemsOperation(card: string, cursor: ConversationCursor | null): ApiOperation<ConversationPage> {
  const cursorSuffix = cursor === null ? '' : `&cursor=${encodeURIComponent(JSON.stringify(cursor))}&beforeId=${cursor.position}`;
  return {
    method: 'GET',
    path: `/api/v1/sessions/${encodeURIComponent(card)}/items?afterId=0&limit=500&latest=true${cursorSuffix}`,
    responseSchema: sessionItemsPageDtoSchema.transform(page => sessionItemsPageDtoToConversationPage(card, page, cursor)),
  };
}

/** Only real Native/raw observations may retire a streamed native item. */
export function nativeDisplayRows(items: readonly ConversationDisplayItem[]): readonly TranscriptRow[] {
  return items.flatMap(({ source }) => source.kind === 'native' || source.kind === 'historical_raw' ? [source.item] : []);
}
export function removedHumanIds(items: readonly ConversationDisplayItem[]): ReadonlySet<string> {
  return new Set(items.flatMap(({ source }) => source.kind === 'removed' && source.identity.kind === 'human'
    ? [source.identity.message_id] : []));
}

export function nativeTranscriptEntryId(threadId: string, itemId: string | null, index: number): string {
  return `native:${encodeURIComponent(JSON.stringify([threadId, itemId]))}:${index}`;
}

export function buildConversationTranscript(items: readonly ConversationDisplayItem[], viewerId: string | null): readonly TranscriptEntry[] {
  const ordered = [...items].sort((a,b) => a.cursor.position-b.cursor.position);
  if (ordered.every(item => item.cursor.source === 'historical_raw')) {
    return buildTranscript(nativeDisplayRows(ordered)).map(entry => entry.author === 'you'
      ? { ...entry, author:'unknown' as const } : entry);
  }
  const entries=ordered.flatMap(({ source, cursor }): readonly TranscriptEntry[] => {
    if (source.kind === 'removed') return [];
    if (source.kind === 'system') return [{
      id:`system:${source.observation_id}`, author:'system', label:SYSTEM_PRESENTATION_LABELS[source.presentation],
      text:source.text, atMs:source.created_at_ms, displayPosition:cursor.position,
    }];
    if (source.kind === 'human') return [{
      id:`human:${source.message_id}`, author:source.author.user_id === viewerId ? 'you' : 'human',
      human:{ userId:source.author.user_id, displayName:source.author.display_name, messageId:source.message_id, revision:source.revision },
      text:source.text, attachments:source.attachments, atMs:source.created_at_ms, displayPosition:cursor.position,
      ...(source.supplemental === true ? { supplemental: true } : {}),
      ...(source.message_status === undefined ? {} : { messageStatus: source.message_status }),
      ...(source.delivery_error === undefined ? {} : { deliveryError: source.delivery_error }),
    }];
    if (source.kind === 'imported_item') return buildTranscript([source.item]).map((entry,index) => {
      const position={id:`imported:${source.provenance.original_row_id}:${index}`,displayPosition:cursor.position};
      if(entry.author === 'system')return {...entry,...position,label:`Imported history · ${entry.label}`};
      if(entry.author === 'activity')return {...entry,...position,verb:`Imported history · ${entry.verb}`};
      if(entry.author === 'turn')return {...entry,...position,imported:true};
      // Native folded segments can carry queue-looking hints, never application acceptance.
      return { text:entry.text,atMs:entry.atMs,attachments:entry.attachments, author:entry.author === 'you' || entry.author === 'human' ? 'unknown' as const : entry.author,
        imported:true,...position };
    });
    if (source.kind !== 'native') throw new Error('Mixed historical/native display contract');
    // The native parser owns provider shapes. A presentation has already paired started/completed;
    // never re-sort by physical row ID or pair equal item IDs from different native threads.
    return buildTranscript([source.item]).map((entry,index) => ({ ...entry,
      id:nativeTranscriptEntryId(source.item.thread_id, source.item.item_uuid, index),
      displayPosition:cursor.position,
      ...(source.item.turn_id === null ? {} : { nativeTurn: { threadId: source.item.thread_id, turnId: source.item.turn_id } }),
    }));
  });
  return retireFollowedThoughts(entries);
}

/** Current response metadata has no transcript author, item identity or pagination position. */
export type CurrentConversationOutcome = Omit<import('./conversation.js').ConversationTurnOutcome, 'id' | 'author' | 'imported'>;
export type ConversationRun = SessionItemsPageDto;

export function conversationRunOperation(sessionId: string): ApiOperation<ConversationRun> {
  return {
    method: 'GET',
    path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/items?afterId=0&limit=1&view=run`,
    responseSchema: sessionItemsPageDtoSchema,
  };
}

export function conversationRunPhase(run: ConversationRun): import('../api/generated/wire.js').HarnessPhaseTag {
  return run.worker?.phase === 'interrupting' ? 'issuing_interrupt'
    : run.activeTurn?.status === 'running' ? 'turn_running'
    : run.session.status === 'queued' ? 'issuing_turn' : 'idle';
}

export function conversationRunningTurn(run: ConversationRun, now: number): import('./conversation.js').PlannerRunningTurn | null {
  const turn = run.activeTurn;
  return turn?.status === 'running'
    ? { turn_id: turn.id, elapsed_ms: Math.max(0, now - (turn.startedAtMs ?? now)) } : null;
}

export function conversationBlockedReason(run: ConversationRun): string | null {
  return run.session.status === 'unknown' ? 'The previous message has an unknown outcome.' : null;
}

export function conversationPendingQueue(run: ConversationRun): readonly import('./conversation.js').PendingQueueEntry[] {
  return run.queuedMessages?.map(message => ({ entry_id: message.id, text: message.text,
    rev: 1, queued_at_ms: message.queuedAtMs, state: message.state })) ?? [];
}
