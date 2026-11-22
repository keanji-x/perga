import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import { type TrackAggregateSnapshotDto } from '../../../../core/api/perga-v1.ts';
import { type AgentProvider } from '../../../../core/api/schemas.ts';
import { useOnlineMutation } from './online-mutation.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { CreatedConversation,SideConversation } from '../../../../core/domain/conversation.ts';
import { createTrackConversationOperation,setConversationArchiveOperation,type ModelSelection } from '../../../../core/domain/conversation.ts';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import { runOperation } from './query-operation.ts';

export type ConversationMutations = Readonly<{
  archive: (cardId: string, archived: boolean) => Promise<void>;
  /** The key identifies the DRAFT, not the attempt: a retry after a timeout must reuse it, or it mints a second conversation. */
  create: (text: string, idempotencyKey: string, provider: AgentProvider, selection: ModelSelection, side: SideConversation | undefined,
    answered: (result: CreatedConversation) => void, permissionMode?: string | null) => Promise<CreatedConversation>;
}>;

/** Session receipts and recovery reads update the Track's authoritative aggregate. */
export function useTrackConversationMutations(
  transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel,
): ConversationMutations {
  const client = useQueryClient();
  const create = useOnlineMutation({
    mutationFn: async ({ text, idempotencyKey, provider, selection, side, permissionMode }: { text: string; idempotencyKey: string; provider: AgentProvider; selection: ModelSelection; side?: SideConversation; answered: (result: CreatedConversation) => void; permissionMode?: string | null }) => {
      return runOperation(
        transport,
        createTrackConversationOperation(trackId, text, idempotencyKey, provider, selection, side, permissionMode),
        unauthorized,
      );
    },
    onSuccess: (result, { answered }) => {
      // Fence an older aggregate read before publishing the create receipt.
      // Query cancellation reverts synchronously, before its Promise settles. Keep
      // this cache update in the same turn, without an await boundary.
      void client.cancelQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
      client.setQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail(trackId), current => {
        if (current === undefined || current.track.id !== trackId) return current;
        if (current.sessions.some(session => session.id === result.session.id)) return current;
        return { ...current, sessions: [...current.sessions, result.session] };
      });
      answered(result);
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
    },
  });
  const archive = useOnlineMutation({
    mutationFn: ({ cardId, archived }: { cardId: string; archived: boolean }) =>
      runOperation(transport, setConversationArchiveOperation(trackId, cardId, archived), unauthorized),
    onSettled: async () => {
      await client.cancelQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
      await client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
    },
  });
  const changeArchive = useCommittedCallback(trackId,
    (cardId: string, archived: boolean) => archive.mutateAsync({ cardId, archived }));
  return {
    archive: changeArchive,
    create: (text, idempotencyKey, provider, selection, side, answered, permissionMode) =>
      create.mutateAsync({ text, idempotencyKey, provider, selection, side, answered, permissionMode }),
  };
}
