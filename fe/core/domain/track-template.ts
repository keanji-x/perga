import type { z } from 'zod';
import { trackDtoSchema, trackTemplateSnapshotSchema } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

export function trackTemplateOperation(trackId: string): ApiOperation<z.infer<typeof trackTemplateSnapshotSchema>> {
  return { method: 'GET', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template`, responseSchema: trackTemplateSnapshotSchema };
}
export function updateTrackTemplateInputsOperation(trackId: string, body: Readonly<Record<string, unknown>>): ApiOperation<z.infer<typeof trackDtoSchema>> {
  return { method: 'PATCH', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template/inputs`, body, responseSchema: trackDtoSchema };
}
