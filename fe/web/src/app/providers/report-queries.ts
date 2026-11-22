import { queryKeys } from './query-keys.ts';
import { keepPreviousData } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type ResolvedSeries } from '../../../../core/domain/report-series.ts';
import { type TrackSourceDetail } from '../../../../core/domain/report-source.ts';
import { deriveReportTasks,hasLiveTaskRun,readTrackViewsOperation,trackTaskVerdictsOperation,type ReportBlock,type TaskVerdict,type TemplateViewsProjection } from '../../../../core/domain/report.ts';
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

/**
 * The track's task verdicts. Its own cache entry: these change on every dispatch and gate result without
 * any card being written. Events alone do not keep it live — `scheduler::mark_running` stamps
 * `worker_card_id` without emitting anything — hence the timer.
 */
export function trackTaskVerdictsQueryOptions(
  transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel,
  /* The declarations this report has: the timer is about the ROWS the panel draws, and a verdict is not a row. */
  blocks: readonly ReportBlock[] | null,
) {
  return {
    queryKey: queryKeys.trackReport(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TaskVerdict[]> =>
      runOperation(transport, { ...trackTaskVerdictsOperation(trackId), signal }, unauthorized),
    /* 3 s is priced off the endpoint (p50 14.8 ms for a small report, 104 ms for a 24-task one): ~3.5% of
     * one core for one open track, only while something is running, and O(1) in the number of workers. */
    refetchInterval: taskVerdictsRefetchInterval(blocks),
  };
}

/** The live poll, once the read has landed at least once. */
const TASK_VERDICT_POLL_MS = 3000;
/** The recovery poll, while the read has never landed at all; far slower, since nothing is being tracked. */
const TASK_VERDICT_RECOVERY_POLL_MS = 15_000;
/**
 * Bounded: the report route also fails permanently (deleted track 404s, missing `track-report` card
 * 500s), and an unconditional poll on "no data" would hit a dead route forever.
 */
const TASK_VERDICT_RECOVERY_ATTEMPTS = 4;

/** With data: poll only while the panel holds a live row. With no data: a bounded recovery poll read off `errorUpdateCount` (`failureCount` resets on success).
 * Curried on the declarations because verdicts for deleted declarations (`blockId: ''`) produce no row, so a timer keyed on raw verdicts would refetch with nothing on screen. */
export function taskVerdictsRefetchInterval(blocks: readonly ReportBlock[] | null) {
  return (query: { state: { data?: TaskVerdict[]; errorUpdateCount: number } }): number | false => {
    const { data, errorUpdateCount } = query.state;
    if (data !== undefined) {
      return hasLiveTaskRun(deriveReportTasks(blocks, data)) ? TASK_VERDICT_POLL_MS : false;
    }
    return errorUpdateCount > 0 && errorUpdateCount <= TASK_VERDICT_RECOVERY_ATTEMPTS
      ? TASK_VERDICT_RECOVERY_POLL_MS
      : false;
  };
}

/** The wire, or the 409 turned into a value: `stale-rev` is a condition the next `['track', id]` refetch fixes, not an error. */
export type SeriesRead = ResolvedSeries | Readonly<{ status: 'stale-rev'; current_rev: number }>;

/** The row, or the 404 turned into a value: a dangling citation is a state the design admits, not an error to retry. */
export type SourceRead =
  | Readonly<{ status: 'found'; source: TrackSourceDetail }>
  | Readonly<{ status: 'missing' }>;
