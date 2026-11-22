import { templateViewsProjectionSchema, type TemplateViewsProjection } from './report-widget.js';
import { reportReadResultDtoSchema, type ReportReadResultDto } from '../api/perga-v1.js';

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
