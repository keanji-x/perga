import { z } from 'zod';
import {
  trackDtoSchema, trackScheduleDtoSchema, trackSchedulesSnapshotSchema, trackTemplateSnapshotSchema,
  type SetTrackScheduleRequest,
} from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

function schedulesPath(trackId: string): string {
  return `/api/v1/tracks/${encodeURIComponent(trackId)}/schedules`;
}
export function trackSchedulesOperation(trackId: string): ApiOperation<z.infer<typeof trackSchedulesSnapshotSchema>> {
  return { method: 'GET', path: `${schedulesPath(trackId)}?includeTerminal=true&runsLimit=20`, responseSchema: trackSchedulesSnapshotSchema };
}
export function setTrackScheduleOperation(trackId: string, body: SetTrackScheduleRequest): ApiOperation<z.infer<typeof trackScheduleDtoSchema>> {
  return { method: 'POST', path: schedulesPath(trackId), body, responseSchema: trackScheduleDtoSchema };
}
export function cancelTrackScheduleOperation(trackId: string, scheduleId: string): ApiOperation<void> {
  return { method: 'DELETE', path: `${schedulesPath(trackId)}/${encodeURIComponent(scheduleId)}`, responseSchema: z.void() };
}
export function triggerTrackScheduleOperation(trackId: string, scheduleId: string): ApiOperation<unknown> {
  return { method: 'POST', path: `${schedulesPath(trackId)}/${encodeURIComponent(scheduleId)}/trigger`, responseSchema: z.unknown() };
}
export function trackTemplateOperation(trackId: string): ApiOperation<z.infer<typeof trackTemplateSnapshotSchema>> {
  return { method: 'GET', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template`, responseSchema: trackTemplateSnapshotSchema };
}
export function updateTrackTemplateInputsOperation(trackId: string, body: Readonly<Record<string, unknown>>): ApiOperation<z.infer<typeof trackDtoSchema>> {
  return { method: 'PATCH', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/template/inputs`, body, responseSchema: trackDtoSchema };
}
