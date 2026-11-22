import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import { useRecoveryMutation } from './recovery-mutation.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createTerminalCardOperation, deleteTerminalOperation, type CardWire, type NewTerminalCardBody } from '../../../../core/domain/track.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { deleteDone, runOperation } from './query-operation.ts';
export function useTrackToolMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  const client = useQueryClient();
  /* Tool receipts go to the independent inventory. Cancellation synchronously fences
   * older reads; publishing stays inside the recovery-admitted callback. */
  const addToolToCache = (card: CardWire): void => {
    void client.cancelQueries({ queryKey: queryKeys.trackTools(card.track_id), exact: true });
    client.setQueryData(queryKeys.trackTools(card.track_id), (previous: CardWire[] | undefined) => {
      if (previous?.some((existing) => existing.id === card.id)) return previous;
      return [...(previous ?? []), card];
    });
    void client.invalidateQueries({ queryKey: queryKeys.trackTools(card.track_id), exact: true });
  };
  const createTerminal = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, body, key }: { trackId: string; body: NewTerminalCardBody; key: string }, transport: ApiTransportPort) =>
      runOperation(transport, createTerminalCardOperation(trackId, body, key), unauthorized),
    onSuccess: addToolToCache,
  });
  /* Delete drops the row from the tool cache before the refetch lands: leaving it on screen would keep
   * a PTY attached to a card the kernel has torn down. A delete answered done (404, `DELETE_FAILURES`) is gone the same
   * way. `onSettled`: an aborted wait says nothing about commit. */
  const dropCard = (_result: unknown, { trackId, cardId }: { trackId: string; cardId: string }) => {
    void client.cancelQueries({ queryKey: queryKeys.trackTools(trackId), exact: true });
    client.setQueryData(queryKeys.trackTools(trackId), (previous: CardWire[] | undefined) => {
      if (previous === undefined) return previous;
      const cards = previous.filter((existing) => existing.id !== cardId);
      return cards.length === previous.length ? previous : cards;
    });
  };
  const removeCard = useRecoveryMutation(transport, {
    mutationFn: async ({ trackId, cardId, signal }: { trackId: string; cardId: string; signal?: AbortSignal }, transport: ApiTransportPort) => {
      if (cardId.startsWith('file-local-')) {
        return undefined;
      }
      return runOperation(transport, { ...deleteTerminalOperation(trackId, cardId), signal }, unauthorized);
    },
    onSuccess: dropCard,
    onError: (error, variables) => { if (deleteDone(error)) dropCard(undefined, variables); },
    onSettled: (_result, _error, { trackId }) => {
      void cancelThenInvalidate(client, queryKeys.trackTools(trackId));
      void cancelThenInvalidate(client, queryKeys.overlaysByKind('track'));
    },
  });
  return {
    createTerminal: (trackId: string, body: NewTerminalCardBody, key: string) => createTerminal.mutateAsync({ trackId, body, key }),
    removeCard: async (trackId: string, cardId: string, signal?: AbortSignal) => { await removeCard.mutateAsync({ trackId, cardId, signal }); },
  };
}
