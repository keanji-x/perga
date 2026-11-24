import { useRetireTrackConversations } from '../conversations/public.tsx';
import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import { useOnlineMutation } from './online-mutation.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createTrackOperation, deleteTrackOperation, toTrack, updateTrackOperation, type NewTrackBody, type TrackPatchBody } from '../../../../core/domain/track.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { deleteDone, runOperation } from './query-operation.ts';
export function useTrackLifecycleMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  const client = useQueryClient();
  const retireConversations = useRetireTrackConversations();
  const retireTrack = (trackId: string) => {
    retireConversations?.(trackId);
    client.removeQueries({ queryKey: queryKeys.trackDetail(trackId) });
  };
  const create = useOnlineMutation({
    mutationFn: (variables: { body: NewTrackBody; idempotencyKey: string }) =>
      runOperation(transport, createTrackOperation(variables.body, variables.idempotencyKey), unauthorized),
    onSuccess: (track) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(track.areaId) });

    },
  });
  const patch = useOnlineMutation({
    mutationFn: ({ trackId, body }: { trackId: string; areaId: string; body: TrackPatchBody }) =>
      runOperation(transport, updateTrackOperation(trackId, body), unauthorized),
    // A patch can move the track: the area the server just reported is refetched too.
    onSuccess: (track, variables) => {
      if (track.areaId !== variables.areaId) void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(track.areaId) });
    },
    // No write-through: the detail's Close / Reopen capabilities are the server's to derive, so the page keeps its last
    // detail until the refetch (or the `track.updated` event's) replaces it whole. `onSettled`: an unknown answer may
    // still follow a stored patch.
    onSettled: (_track, _error, variables) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(variables.areaId) });
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(variables.trackId) });
    },
  });
  const remove = useOnlineMutation({
    mutationFn: ({ trackId, signal }: { trackId: string; areaId: string; signal?: AbortSignal }) =>
      runOperation(transport, { ...deleteTrackOperation(trackId), signal }, unauthorized),
    onSuccess: (_result, variables) => {
      retireTrack(variables.trackId);
    },
    onError: (error, variables) => {
      if (deleteDone(error)) retireTrack(variables.trackId);
    },
    // Reconcile both list-derived surfaces even if abort raced a committed DELETE.
    onSettled: (_result, _error, variables) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(variables.areaId) });
      void cancelThenInvalidate(client, queryKeys.overlaysByKind('track'));
    },
  });
  const patchTrack = async (trackId: string, areaId: string, body: TrackPatchBody) => toTrack(await patch.mutateAsync({ trackId, areaId, body }));
  return {
    create: async (body: NewTrackBody, idempotencyKey: string) => toTrack(await create.mutateAsync({ body, idempotencyKey })),
    patch: patchTrack,
    setPinned: (trackId: string, areaId: string, pinned: boolean, nowMs: number) => {
      // The Host stamps pin time; callers still supply their display clock.
      void nowMs;
      return patchTrack(trackId, areaId, { pinned });
    },
    remove: async (trackId: string, areaId: string, signal?: AbortSignal) => { await remove.mutateAsync({ trackId, areaId, signal }); },
  };
}
