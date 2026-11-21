// Live template view & Track Flow projection query (`GET /api/v1/tracks/{trackId}/views`).
// Fetches whenever enabled for a Perga track (even without explicit `widget` blocks) so
// template-less Tracks with a `flow.json` automatically render `builtin:flow`.
// Keyed stably by `trackId` and refreshed via server event invalidation (`track.updated`,
// `report.updated`, `task.updated`).

import { useQuery } from '@tanstack/react-query';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { readFailureOf, readFailureText } from '../../../../core/domain/read-failure.ts';
import type { TemplateViewsState } from '../../../../core/domain/report.ts';
import { trackViewsQueryOptions } from '../providers/queries.ts';

export function useTrackTemplateViews(
  transport: ApiTransportPort,
  trackId: string,
  unauthorized: UnauthorizedChannel,
  enabled = true,
): TemplateViewsState | undefined {
  const active = enabled && trackId !== '';
  const query = useQuery(trackViewsQueryOptions(transport, trackId, unauthorized, { enabled: active }));

  if (!active) return undefined;
  if (query.data !== undefined) return { status: 'ready', projection: query.data };
  if (query.isError) {
    return {
      status: 'error',
      message: readFailureText(readFailureOf(query.error), '无法加载模板视图，请稍后重试。'),
    };
  }
  return { status: 'loading' };
}
