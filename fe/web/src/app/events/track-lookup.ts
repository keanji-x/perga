import { queryKeys } from '../providers/query-keys.ts';
import type { QueryClient } from '@tanstack/react-query';

import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";

import type { TerminalDto } from '../../../../core/api/perga-v1.ts';
import type { InvalidationContext } from '../../../../core/events/invalidation-plan.ts';

/** Resolves card-scoped events from track details already present in the cache. */
export function trackLookupContext(client: Pick<QueryClient, 'getQueriesData'>): InvalidationContext {
  return {
    findTrackOwningCard(cardId) {
      for (const [key, detail] of client.getQueriesData<TrackAggregateSnapshotDto | TerminalDto[]>({ queryKey: queryKeys.trackDetailPrefix() })) {
        if ((Array.isArray(detail) ? detail.some(card => card.id === cardId) : detail?.sessions.some(session => session.id === cardId))) {
          const trackId = key[1];
          return typeof trackId === 'string' ? trackId : null;
        }
      }
      return null;
    },
    memoryTrackIds() {
      return client.getQueriesData<TrackAggregateSnapshotDto | TerminalDto[]>({ queryKey: queryKeys.trackDetailPrefix() })
        .flatMap(([, detail]) => !Array.isArray(detail) && detail?.track?.areaId === 'sys_memory' ? [detail.track.id] : []);
    },
  };
}
