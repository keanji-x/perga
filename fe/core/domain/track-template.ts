import type { z } from 'zod';
import type { ApplyTrackTemplateRequest } from '../api/generated/wire.js';
import { trackDtoSchema, trackTemplateSnapshotSchema } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

export type ApplyTrackTemplateBody = Readonly<ApplyTrackTemplateRequest>;

export function trackTemplateOperation(trackId: string): ApiOperation<z.infer<typeof trackTemplateSnapshotSchema>> {
  return { method: 'GET', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template`, responseSchema: trackTemplateSnapshotSchema };
}
export function applyTrackTemplateOperation(trackId: string, body: ApplyTrackTemplateBody): ApiOperation<z.infer<typeof trackDtoSchema>> {
  return { method: 'POST', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template`, body, responseSchema: trackDtoSchema };
}
export function updateTrackTemplateInputsOperation(trackId: string, body: Readonly<Record<string, unknown>>): ApiOperation<z.infer<typeof trackDtoSchema>> {
  return { method: 'PATCH', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template/inputs`, body, responseSchema: trackDtoSchema };
}
