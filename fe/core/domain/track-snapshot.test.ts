import { expect, it } from 'vitest';
import { trackDetailOperation } from './track.js';
import { trackAggregateSnapshotDtoSchema } from '../api/schemas.js';

it('returns the aggregate and all session identities without synthesizing cards', () => {
  const snapshot = trackAggregateSnapshotDtoSchema.parse({
    track: { id: 'track', areaId: 'area', createdAtMs: 1, updatedAtMs: 2 },
    report: { trackId: 'track', docRev: 0, summary: '', blocks: [] },
    sessions: [{ id: 'lead', trackId: 'track', provider: 'codex', isLead: true, contextUsage: null, createdAtMs: 1, updatedAtMs: 2 }],
  });
  expect(trackDetailOperation('track').responseSchema.parse(snapshot)).toEqual(snapshot);
});
