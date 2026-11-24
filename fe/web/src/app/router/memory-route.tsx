import { useQuery } from '@tanstack/react-query';
import type { ReactNode } from 'react';

import type { TrackAggregateSnapshotDto } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { memoryPlannerTrackOperation } from '../../../../core/domain/memory.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { TrackPending } from '../../features/track/page/pending.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { runOperation } from '../providers/query-operation.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';

export function MemoryRoute({ transport, unauthorized, areaId, renderTrack }: Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  areaId: string | null;
  renderTrack: (snapshot: TrackAggregateSnapshotDto, areaId: string | null) => ReactNode;
}>) {
  const binding = useQuery({
    queryKey: ['memory-planner-track', areaId],
    queryFn: ({ signal }) => runOperation(transport, { ...memoryPlannerTrackOperation(areaId), signal }, unauthorized),
    retry: false,
    staleTime: Infinity,
  });
  const trackId = binding.data?.trackId;
  const detail = useQuery({
    ...trackDetailQueryOptions(transport, trackId ?? '', unauthorized),
    enabled: trackId !== undefined,
    retry: false,
    refetchInterval: query => query.state.data?.workspacePreparation === 'pending' ? 1000 : false,
  });
  if (binding.isError || detail.isError) {
    return <ErrorBox message={readErrorText(binding.error ?? detail.error, 'Memory could not be loaded.')}
      pending={binding.isFetching || detail.isFetching}
      onRetry={() => {
        void binding.refetch().then(({ data }) => {
          if (data?.trackId === trackId && trackId !== undefined) void detail.refetch();
        });
      }} />;
  }
  return detail.data === undefined ? <TrackPending /> : renderTrack(detail.data, areaId);
}
