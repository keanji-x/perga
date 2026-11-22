import { templateViewsProjectionSchema, type TemplateViewsProjection } from './report-widget.js';
import { reportReadResultDtoSchema, type ReportBlockKind, type ReportReadResultDto } from '../api/perga-v1.js';

import type { ApiOperation } from '../api/types.js';

export function readTrackReportOperation(trackId: string): ApiOperation<ReportReadResultDto> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/report`,
    responseSchema: reportReadResultDtoSchema,
  };
}

export function readTrackViewsOperation(trackId: string): ApiOperation<TemplateViewsProjection> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/views`,
    responseSchema: templateViewsProjectionSchema,
  };
}

export type HumanReportBlockUpdateBody = Readonly<{
  docRev: number;
  knownBlockRev: number;
  kind?: ReportBlockKind;
  markdown?: string | null;
  payload?: unknown;
  summary?: string | null;
  commitMessage?: string | null;
}>;

export function updateTrackReportBlockOperation(
  trackId: string,
  blockId: string,
  body: HumanReportBlockUpdateBody,
): ApiOperation<ReportReadResultDto> {
  return {
    method: 'PUT',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/report/blocks/${encodeURIComponent(blockId)}`,
    body,
    responseSchema: reportReadResultDtoSchema,
  };
}
