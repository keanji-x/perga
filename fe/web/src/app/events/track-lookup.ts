import type { QueryClient } from '@tanstack/react-query';

import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import type { CardWire } from '../../../../core/domain/track.ts';
import type { InvalidationContext } from '../../../../core/events/invalidation-plan.ts';

/** Resolves card-scoped events from track details already present in the cache. */
export function trackLookupContext(client: Pick<QueryClient, 'getQueriesData'>): InvalidationContext {
  return {
    findTrackOwningCard(cardId) {
      for (const [key, detail] of client.getQueriesData<TrackAggregateSnapshotDto | CardWire[]>({ queryKey: ['track'] })) {
        if ((Array.isArray(detail) ? detail.some(card => card.id === cardId) : detail?.sessions.some(session => session.id === cardId))) {
          const trackId = key[1];
          return typeof trackId === 'string' ? trackId : null;
        }
      }
      return null;
    },
  };
}
