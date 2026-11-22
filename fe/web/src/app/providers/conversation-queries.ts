import type { ConversationCursor, ConversationLiveReplies } from '../../../../core/domain/presentation-types.ts';
import { conversationItemsOperation,conversationRunOperation } from '../../../../core/domain/conversation-display.ts';
import { conversationLiveOperation } from '../../../../core/domain/conversation-live.ts';
import { queryKeys } from './query-keys.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { runOperation } from './query-operation.ts';
export type TranscriptPosition = Readonly<{ kind: 'latest' } | { kind: 'before'; cursor: ConversationCursor }>;

export function conversationHistoryQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.conversationHistory(cardId),
    queryFn: async ({ position, signal }: { position: TranscriptPosition; signal?: AbortSignal }) => {
      const cursor = position.kind === 'latest' ? null : position.cursor;
      const page = await runOperation(transport, { ...conversationItemsOperation(cardId, cursor), signal }, unauthorized);
      if (cursor !== null && (page.items.some(item => item.cursor.position >= cursor.position)
        || (page.next_cursor !== null && page.next_cursor.position >= cursor.position))) {
        throw new Error('Conversation history did not advance to an earlier page.');
      }
      return page;
    },
  };
}

export function harnessLiveQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.conversationLive(cardId),
    queryFn: async ({ signal }: { signal: AbortSignal }):Promise<ConversationLiveReplies> => runOperation(transport,{...conversationLiveOperation(cardId),signal},unauthorized),
  };
}

export function plannerRunQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.conversationRun(cardId),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...conversationRunOperation(cardId), signal }, unauthorized),
  };
}
