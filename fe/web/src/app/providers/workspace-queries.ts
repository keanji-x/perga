import { queryKeys } from './query-keys.ts';
import { useQueries,useQuery,type QueryClient,type UseQueryResult } from '@tanstack/react-query';
import { useMemo } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { areaListOperation,sortedAreas,toArea,visibleAreas,type Area } from '../../../../core/domain/area.ts';
import { NEUTRAL_ACTIVITY,sortAreaTracksByRecent,trackActivitiesFromOverlays,type Track } from '../../../../core/domain/track.ts';
import { runOperation } from './query-operation.ts';
import { trackOverlaysQueryOptions,tracksInAreaQueryOptions } from './track-queries.ts';
export function areaListQueryOptions(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return {
    // The shell owns an explicit Retry action; automatic retry backoff would
    // replace the known read error with an empty pending view on invalidation.
    retry: false,
    queryKey: queryKeys.areas(),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Area[]> =>
      sortedAreas(visibleAreas((await runOperation(transport, { ...areaListOperation(), signal }, unauthorized)).map(toArea))),
  };
}

export type Workspace = Readonly<{
  areas: readonly Area[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  tracks: readonly Track[];
  areasLoading: boolean;
  overlaysLoading: boolean;
  areasError: Error | null;
  overlaysError: Error | null;
  trackErrorsByArea: ReadonlyMap<string, Error>;
  tracksLoadingByArea: ReadonlyMap<string, boolean>;
  retryAreas: () => void;
  retryOverlays: () => void;
  retryTracks: (areaId: string) => void;
}>;

// Query structurally shares these plain arrays independently: read status can change
// without replacing unchanged data. The outer useQueries result is not a memo key.
function combineWorkspaceTracks(results: UseQueryResult<Track[]>[]) {
  return {
    data: results.map(query => query.data),
    reads: results.map(query => ({ pending: query.isPending, error: query.error, refetch: query.refetch })),
  };
}

const NO_AREAS: readonly Area[] = Object.freeze([]);

/**
 * The area → tracks fan-out is a page-level `useQueries`, never a route loader await: one slow area
 * must not block the calendar. The workspace-wide overlay read is folded in so every surface sees the same activity.
 */
export function useWorkspace(transport: ApiTransportPort, unauthorized: UnauthorizedChannel): Workspace {
  const areasQuery = useQuery(areaListQueryOptions(transport, unauthorized));
  const overlaysQuery = useQuery(trackOverlaysQueryOptions(transport, unauthorized));
  const areas = areasQuery.data ?? NO_AREAS;
  const trackQueries = useQueries({
    queries: areas.map((area) => tracksInAreaQueryOptions(transport, area.id, unauthorized)),
    combine: combineWorkspaceTracks,
  });
  const activity = useMemo(() => trackActivitiesFromOverlays(overlaysQuery.data ?? []), [overlaysQuery.data]);
  const { tracksByArea, tracks } = useMemo(() => {
    const tracksByArea = new Map<string, readonly Track[]>();
    const tracks: Track[] = [];
    for (const [index, area] of areas.entries()) {
      const data = trackQueries.data[index];
      if (data === undefined) continue;
      const rows = data.map(track => ({ ...track, ...(activity.get(track.id) ?? NEUTRAL_ACTIVITY) }));
      tracksByArea.set(area.id, sortAreaTracksByRecent(rows));
      tracks.push(...rows);
    }
    return { tracksByArea, tracks };
  }, [activity, areas, trackQueries.data]);
  const { trackErrorsByArea, tracksLoadingByArea } = useMemo(() => {
    const trackErrorsByArea = new Map<string, Error>();
    const tracksLoadingByArea = new Map<string, boolean>();
    for (const [index, area] of areas.entries()) {
      const query = trackQueries.reads[index];
      tracksLoadingByArea.set(area.id, query?.pending ?? false);
      if (query?.error instanceof Error) trackErrorsByArea.set(area.id, query.error);
    }
    return { trackErrorsByArea, tracksLoadingByArea };
  }, [areas, trackQueries.reads]);
  return {
    areas, tracksByArea, tracks, areasLoading: areasQuery.isPending,
    overlaysLoading: overlaysQuery.isLoading,
    areasError: areasQuery.error instanceof Error ? areasQuery.error : null,
    overlaysError: overlaysQuery.error instanceof Error ? overlaysQuery.error : null,
    trackErrorsByArea,
    tracksLoadingByArea,
    retryAreas: () => { void areasQuery.refetch(); },
    retryOverlays: () => { void overlaysQuery.refetch(); },
    retryTracks: (areaId) => {
      const index = areas.findIndex((area) => area.id === areaId);
      if (index >= 0) void trackQueries.reads[index]?.refetch();
    },
  };
}

/** Route loaders prime only this one list. */
export function prefetchAreaList(client: QueryClient, transport: ApiTransportPort, unauthorized: UnauthorizedChannel): Promise<void> {
  // Prefetch failures belong to the Areas query; rejecting a loader here would replace the entire app.
  void client.prefetchQuery(areaListQueryOptions(transport, unauthorized));
  // A paused offline query must not hold the route commit either.
  return Promise.resolve();
}
