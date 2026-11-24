import { z } from 'zod';

import type { MemoryMentionDto, MemoryPlannerTrackDto } from '../api/generated/wire.js';
import type { ApiOperation } from '../api/types.js';

const keyPattern = /^[a-z0-9][a-z0-9_-]{0,63}$/;
const id = z.string().min(1);

export const memoryPlannerTrackSchema = z.object({
  areaId: id.nullable(),
  scopeKey: z.string(),
  trackId: id,
  leadSessionId: id,
}) satisfies z.ZodType<MemoryPlannerTrackDto>;
export type MemoryPlannerTrack = z.output<typeof memoryPlannerTrackSchema>;

export const memoryMentionSchema = z.object({
  ref: z.string(),
  scope: z.enum(['area', 'global']),
  key: z.string().regex(keyPattern).nullable(),
  title: z.string(),
  summary: z.string().nullable(),
  pinned: z.boolean(),
  insert: z.string(),
}).refine(({ ref, scope, key, insert }) => ref === (key === null ? scope : `${scope}/${key}`)
  && insert === `@\`memory:${ref}\``) satisfies z.ZodType<MemoryMentionDto>;
export type MemoryMention = z.output<typeof memoryMentionSchema>;

export function parseMemoryMentionAddress(address: string): Pick<MemoryMention, 'scope' | 'key'> | null {
  const match = /^memory:(area|global)(?:\/(.+))?$/.exec(address);
  const scope = match?.[1];
  if (scope !== 'area' && scope !== 'global') return null;
  const key = match?.[2] ?? null;
  return key === null || keyPattern.test(key) ? { scope, key } : null;
}

export const memoryUpdatedPayloadSchema = z.discriminatedUnion('scope', [
  z.object({ scope: z.literal('global'), areaId: z.null(), trackId: id }),
  z.object({ scope: z.literal('area'), areaId: id, trackId: id }),
]);

export function memoryPlannerTrackOperation(areaId: string | null): ApiOperation<MemoryPlannerTrack> {
  return {
    method: 'POST',
    path: areaId === null ? '/api/v1/memory/global/track' : `/api/v1/memory/areas/${encodeURIComponent(areaId)}/track`,
    responseSchema: memoryPlannerTrackSchema.refine(track => track.areaId === areaId
      && track.scopeKey === (areaId === null ? 'global' : `area:${areaId}`)),
  };
}

export function memoryMentionsOperation(areaId: string | null, q: string): ApiOperation<MemoryMention[]> {
  const area = areaId === null ? '' : `areaId=${encodeURIComponent(areaId)}&`;
  return {
    method: 'GET', path: `/api/v1/memory/mentions?${area}q=${encodeURIComponent(q)}`, responseSchema: z.array(memoryMentionSchema),
  };
}
