import { queryKeys } from './query-keys.ts';
import { keepPreviousData } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type TrackSourceDetail } from '../../../../core/domain/report-source.ts';
import { readTrackViewsOperation } from '../../../../core/domain/report-operations.ts';
import { type TemplateViewsProjection } from '../../../../core/domain/report-widget.ts';
import { runOperation } from './query-operation.ts';
export function trackViewsQueryOptions(
  transport: ApiTransportPort,
  trackId: string,
  unauthorized: UnauthorizedChannel,
  options?: Readonly<{ enabled?: boolean }>,
) {
  return {
    queryKey: queryKeys.trackViews(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TemplateViewsProjection> =>
      runOperation(transport, { ...readTrackViewsOperation(trackId), signal }, unauthorized),
    staleTime: 5_000,
    placeholderData: keepPreviousData,
    ...(options?.enabled !== undefined ? { enabled: options.enabled } : {}),
  };
}

/** The row, or the 404 turned into a value: a dangling citation is a state the design admits, not an error to retry. */
export type SourceRead =
  | Readonly<{ status: 'found'; source: TrackSourceDetail }>
  | Readonly<{ status: 'missing' }>;
