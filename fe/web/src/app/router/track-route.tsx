import { readErrorText } from '../../../../core/domain/read-failure.ts';

import { useMutation,useQuery } from '@tanstack/react-query';
import { useEffect,useMemo } from 'react';
import type { ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { TrackPending } from '../../features/track/page/pending.tsx';

import { Banner } from '@astryxdesign/core/Banner';
import { trackDtoToWire } from '../../../../core/api/track-projection.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import {
retryTrackWorkspaceOperation,
toTrack,trackActivityFromSnapshot
} from '../../../../core/domain/track.ts';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import {
useConversationCommands,
useConversationOpenRequest
} from '../conversations/public.tsx';
import { runOperation } from '../providers/query-operation.ts';
import {
type RecentFileHistory
} from '../providers/recent-files.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';
import {
useRouteParam
} from './navigation.ts';
import { PendingRoute } from './pending-route.tsx';

import type { CardRuntime } from './card-runtime.ts';
import { TrackRouteBody } from './track-route-body.tsx';
export function TrackRoute({ transport, unauthorized, cardRuntime, recentFiles, prepareReport, }: {
  prepareReport: ReportPreparationPort;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  cardRuntime: CardRuntime;
  recentFiles: RecentFileHistory;

}) {
  const trackId = useRouteParam('/track/');
  const registry = useConversationCommands();
  const openRequest = useConversationOpenRequest();
  const detail = useQuery({
    ...trackDetailQueryOptions(transport, trackId ?? '', unauthorized),
    enabled: trackId !== undefined,
    refetchInterval: (query) => query.state.data?.workspacePreparation === 'pending' && query.state.data.track.closedAtMs == null ? 1000 : false,
  });
  const retry = useMutation({
    mutationFn: (id: string) => runOperation(transport, retryTrackWorkspaceOperation(id), unauthorized),
    onSettled: () => detail.refetch(),
  });
  /* One `Track` per detail read, not per render: the body memoises on it. */
  const detailData = detail.data;
  const track = useMemo(
    () => detailData === undefined ? null : toTrack(trackDtoToWire(detailData.track), trackActivityFromSnapshot(detailData)),
    [detailData],
  );
  const requestedCard = detail.data?.sessions.find(session => session.id === openRequest?.id);
  const detailMatchesRoute = trackId !== undefined && detail.data?.track.id === trackId;
  useEffect(() => {
    if (openRequest === null || detail.isLoading || detail.isFetching) return;
    if (!detailMatchesRoute || requestedCard === undefined) registry.clearOpenRequest();
  }, [detail.isFetching, detail.isLoading, detailMatchesRoute, registry, requestedCard, openRequest]);

  if (!detail.data || track === null) {
    if (detail.isLoading || detail.isFetching) return <TrackPending />;
    if (detail.error instanceof Error) return <ErrorBox message={readErrorText(detail.error, 'This track could not be loaded.')} onRetry={() => { void detail.refetch(); }} />;
    return <PendingRoute label="Track" owner="features/track" missing />;
  }
  // `detail.data` can still be the previously-viewed track while this one
  // fetches; rendering it under this URL would show the wrong track.
  if (trackId !== undefined && detail.data.track.id !== trackId) return <TrackPending />;

  return (
    <TrackRouteBody
      key={track.id}
      prepareReport={prepareReport}
      transport={transport}
      unauthorized={unauthorized}
      snapshot={detail.data}
      workspaceWaiting={detail.data.workspacePreparation != null}
      workspaceNotice={detail.data.workspacePreparation === 'failed' && detail.data.track.closedAtMs != null
        ? <Banner status="error" title="Reopen this Track to retry workspace setup." />
        : detail.data.workspacePreparation === 'failed'
        ? <ErrorBox message={retry.isError
          ? readErrorText(retry.error, 'Workspace setup could not be restarted.')
          : 'Workspace setup failed. Work that needs the workspace is waiting.'}
          actionLabel="Retry workspace setup" pending={retry.isPending}
          onRetry={() => retry.mutate(track.id)} />
        : detail.data.workspacePreparation === 'unknown'
        ? <Banner status="error" title="The last workspace setup result could not be confirmed. Work that needs the workspace is waiting." />
        : detail.data.workspacePreparation === 'pending'
        ? <p role="status">Preparing the workspace.</p>
        : undefined}
      cardRuntime={cardRuntime}
      recentFiles={recentFiles}
    />
  );
}
