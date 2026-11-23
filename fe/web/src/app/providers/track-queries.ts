import { queryKeys } from './query-keys.ts';
import type { TrackOverlayItemDto } from '../../../../core/api/perga-v1.ts';
import { type TrackAggregateSnapshotDto } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { listTrackTerminalsOperation,toTrack,trackDetailOperation,trackOverlaysOperation,tracksInAreaOperation,type Track } from '../../../../core/domain/track.ts';
import { runOperation } from './query-operation.ts';
export function trackOverlaysQueryOptions(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.overlaysByKind('track'),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<TrackOverlayItemDto[]> => {
      try {
        return await runOperation(transport, { ...trackOverlaysOperation(), signal }, unauthorized);
      } catch (error) {
        if (isNotFound(error)) return [];
        throw error;
      }
    },
  };
}

export function tracksInAreaQueryOptions(transport: ApiTransportPort, areaId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.tracksInArea(areaId),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Track[]> =>
      (await runOperation(transport, { ...tracksInAreaOperation(areaId), signal }, unauthorized)).map((wire) => toTrack(wire)),
    // Preserve Query's browser default of three retries for transient reads. Only typed
    // session/access/missing failures are terminal; runOperation still notifies on 401.
    retry: (failureCount: number, error: unknown) => {
      if (error instanceof ApiError) {
        const failure = error.failure;
        if (failure.kind === 'unauthorized'
          || (failure.kind === 'http' && (failure.status === 403 || failure.status === 404))) return false;
      }
      return failureCount < 3;
    },
  };
}

export function trackDetailQueryOptions(transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.trackDetail(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TrackAggregateSnapshotDto> =>
      runOperation(transport, { ...trackDetailOperation(trackId), signal }, unauthorized),
  };
}

/** Workbench resources have their own cache and never contain agent sessions. */
export function trackToolsQueryOptions(transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.trackTools(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...listTrackTerminalsOperation(trackId), signal }, unauthorized),
  };
}

function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.failure.kind === 'http' && error.failure.status === 404;
}
