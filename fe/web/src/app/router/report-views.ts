// Live template view projection query for Scheme B `widget` blocks (`GET /api/v1/tracks/{trackId}/views`).
// Only fetches when the report actually contains at least one `widget` block; keyed stably by `trackId`
// and refreshed via server event invalidation (`track.updated`, `report.updated`, `task.updated`).

import { useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { readFailureOf, readFailureText } from '../../../../core/domain/read-failure.ts';
import type { ReportBlock, TemplateViewsState } from '../../../../core/domain/report.ts';
import { trackViewsQueryOptions } from '../providers/report-queries.ts';

export function useTrackTemplateViews(
  transport: ApiTransportPort,
  trackId: string,
  blocks: readonly ReportBlock[] | null,
  unauthorized: UnauthorizedChannel,
  enabled = true,
): TemplateViewsState | undefined {
  const hasWidget = useMemo(
    () => enabled && (blocks ?? []).some((block) => block.kind === 'widget'),
    [blocks, enabled],
  );

  const query = useQuery(trackViewsQueryOptions(transport, trackId, unauthorized, { enabled: hasWidget }));

  if (!hasWidget) return undefined;
  if (query.data !== undefined) return { status: 'ready', projection: query.data };
  if (query.isError) {
    return {
      status: 'error',
      message: readFailureText(readFailureOf(query.error), '无法加载模板视图，请稍后重试。'),
    };
  }
  return { status: 'loading' };
}
