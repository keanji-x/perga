import { retireAreaQueries } from './area-query-lifecycle.ts';
import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import { useOnlineMutation } from './online-mutation.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { asFolderConflict,createAreaOperation,deleteAreaOperation,newestArea,sortedAreas,toArea,updateAreaOperation,type Area,type AreaPatchBody,type FolderConflict,type NewAreaBody } from '../../../../core/domain/area.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { deleteDone,runOperation } from './query-operation.ts';
/** A failed capability preflight guarantees no Area POST was submitted. */
export class AreaCreatePreflightError extends Error {
  constructor(message: string) {
    super(`${message} No new create request was sent.`);
    this.name = 'AreaCreatePreflightError';
  }
}

/** The structured folder clash inside a rejected mutation, or `null`; the decode and wording are `core/domain/area.ts`. */
export function folderConflictOf(error: unknown): FolderConflict | null {
  if (!(error instanceof ApiError)) return null;
  return 'body' in error.failure ? asFolderConflict(error.failure.body) : null;
}

export type AreaMutations = Readonly<{
  create: (body: NewAreaBody, idempotencyKey: string) => Promise<Area>;
  update: (areaId: string, body: AreaPatchBody) => Promise<Area>;
  remove: (areaId: string, signal?: AbortSignal) => Promise<void>;
}>;

export function useAreaMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel): AreaMutations {
  const client = useQueryClient();
  const create = useOnlineMutation({
    mutationFn: async ({ body, idempotencyKey }: { body: NewAreaBody; idempotencyKey: string }) => {
      return runOperation(transport, createAreaOperation(body, idempotencyKey), unauthorized);
    },
    onSuccess: (wire) => {
      const created = toArea(wire);
      client.setQueryData<Area[]>(queryKeys.areas(), (current) => {
        if (current === undefined) return current;
        const existing = current.find((area) => area.id === created.id);
        return sortedAreas([
          ...current.filter((area) => area.id !== created.id),
          existing === undefined ? created : newestArea(existing, created),
        ]);
      });
      void cancelThenInvalidate(client, queryKeys.areas());
    },
    // A lost response can follow a committed creation; the retained creation key makes the next POST safe.
    onError: () => client.invalidateQueries({ queryKey: queryKeys.areas() }),
  });
  const update = useOnlineMutation({
    mutationFn: ({ areaId, body }: { areaId: string; body: AreaPatchBody }) =>
      runOperation(transport, updateAreaOperation(areaId, body), unauthorized),
    onSuccess: (wire) => {
      const updated = toArea(wire);
      // The Area editor closes as soon as mutateAsync resolves; without the write-through a click in the
      // refetch window snapshots stale defaults into NewTrackForm's local state.
      client.setQueryData<Area[]>(queryKeys.areas(), (current) => current?.map(
        (area) => area.id === updated.id
          ? newestArea(area, updated)
          : area,
      ));
    },
    // A transport failure may still follow a committed write, so failure cannot leave cached defaults authoritative.
    onSettled: () => client.invalidateQueries({ queryKey: queryKeys.areas() }),
  });
  const remove = useOnlineMutation({
    mutationFn: ({ areaId, signal }: { areaId: string; signal?: AbortSignal }) =>
      runOperation(transport, { ...deleteAreaOperation(areaId), signal }, unauthorized),
    // The area is gone, also when the delete was answered done; its track list can never resolve again.
    onSuccess: (_result, { areaId }) => { retireAreaQueries(client, areaId); },
    onError: (error, { areaId }) => {
      if (deleteDone(error)) retireAreaQueries(client, areaId);
    },
    // Abort only ends the client wait: the server may already have committed.
    onSettled: () => { void client.invalidateQueries({ queryKey: queryKeys.areas() }); },
  });
  return {
    create: async (body, idempotencyKey) => {
      const created = toArea(await create.mutateAsync({ body, idempotencyKey }));
      const current = client.getQueryData<Area[]>(queryKeys.areas())?.find(area => area.id === created.id);
      // Route drafts must receive the same newest defaults as the shared list.
      return current === undefined ? created : newestArea(created, current);
    },
    update: async (areaId, body) => toArea(await update.mutateAsync({ areaId, body })),
    remove: async (areaId, signal) => { await remove.mutateAsync({ areaId, signal }); },
  };
}
