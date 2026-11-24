import { expect, it } from 'vitest';

import { invalidationPlanFor } from '../events/invalidation-plan.js';
import { memoryMentionsOperation, memoryPlannerTrackOperation, memoryUpdatedPayloadSchema, parseMemoryMentionAddress } from './memory.js';

it('invalidates native Track queries and refreshes inherited counts on Global updates', () => {
  expect(memoryUpdatedPayloadSchema.safeParse({ scope: 'global', areaId: null }).success).toBe(false);
  const global = memoryUpdatedPayloadSchema.parse({ scope: 'global', areaId: null, trackId: 'trk_global' });
  const area = memoryUpdatedPayloadSchema.parse({ scope: 'area', areaId: 'a', trackId: 'trk_area' });
  const context = { findTrackOwningCard: () => null, memoryTrackIds: () => ['trk_global', 'trk_area'] };
  expect(invalidationPlanFor({ ev: 'memory.updated', data: global }, context).invalidate)
    .toEqual([['track', 'trk_global'], ['track-views', 'trk_global'], ['track', 'trk_area']]);
  expect(invalidationPlanFor({ ev: 'memory.updated', data: area }, context).invalidate)
    .toEqual([['track', 'trk_area'], ['track-views', 'trk_area']]);
});

it('ensures only the requested scope and preserves server mention order and insert text', () => {
  const binding = { areaId: null, scopeKey: 'global', trackId: 'trk_memory', leadSessionId: 'ses_memory' };
  const ensureGlobal = memoryPlannerTrackOperation(null);
  const ensureArea = memoryPlannerTrackOperation('a/b');
  expect(ensureGlobal).toMatchObject({ method: 'POST', path: '/api/v1/memory/global/track' });
  expect(ensureArea.path).toBe('/api/v1/memory/areas/a%2Fb/track');
  expect(ensureGlobal.body).toBeUndefined();
  expect(ensureGlobal.responseSchema.parse(binding)).toEqual(binding);
  expect(ensureArea.responseSchema.safeParse(binding).success).toBe(false);
  expect(ensureArea.responseSchema.parse({ ...binding, areaId: 'a/b', scopeKey: 'area:a/b' }).trackId).toBe('trk_memory');

  const mentions = memoryMentionsOperation('a/b', 'release notes');
  expect(mentions.path).toBe('/api/v1/memory/mentions?areaId=a%2Fb&q=release%20notes');
  expect(memoryMentionsOperation(null, '').path).toBe('/api/v1/memory/mentions?q=');
  const entries = [
    { ref: 'area/runbook', scope: 'area', key: 'runbook', title: 'Release runbook', summary: null, pinned: true, insert: '@`memory:area/runbook`' },
    { ref: 'global', scope: 'global', key: null, title: 'Global memory', summary: null, pinned: false, insert: '@`memory:global`' },
  ];
  expect(mentions.responseSchema.parse(entries)).toEqual(entries);
  expect(mentions.responseSchema.safeParse([{ ...entries[0], insert: '@`memory:global`' }]).success).toBe(false);
  expect(parseMemoryMentionAddress('memory:area/runbook')).toEqual({ scope: 'area', key: 'runbook' });
  expect(parseMemoryMentionAddress('memory:global')).toEqual({ scope: 'global', key: null });
  expect(parseMemoryMentionAddress('memory:area/../runbook')).toBeNull();
});
