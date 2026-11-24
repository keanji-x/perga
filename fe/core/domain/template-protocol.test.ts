import hostPayloads from '../api/fixtures/unified-host-payloads.json';
import { describe, expect, it } from 'vitest';
import { trackTemplateSnapshotSchema } from '../api/perga-v1.js';
import { decodeWireEvent } from '../api/schemas.js';
import { invalidationPlanFor } from '../events/invalidation-plan.js';

describe('template-protocol wire contracts', () => {
  it('decodes Flow clock events and refreshes the track views', () => {
    const decoded = decodeWireEvent({ eventId: 12, trackId: 't', kind: 'track.schedules.updated', payload: { trackId: 't' }, createdAtMs: 1 });
    expect(decoded).toEqual({ status: 'ready', value: { ev: 'track.schedules.updated', data: { trackId: 't' } } });
    if (decoded.status !== 'ready') throw new Error('Expected decoded event');
    expect(invalidationPlanFor(decoded.value)).toEqual({ invalidate: [['track-views', 't']], remove: [], writeThrough: [] });
    expect(decodeWireEvent({ kind: 'track.schedules.updated', payload: { trackId: 42 } }).status).toBe('failed');
  });

  it('decodes the template Flow blueprint when the Host omits the header view', () => {
    const flow = [{ id: 'weekly-price-refresh', when: { cron: '30 8 * * 1' }, do: {
      kind: 'wake', session: 'lead', prompt: 'Refresh ${inputs.ticker}',
    } }];
    expect(trackTemplateSnapshotSchema.parse({
      snapshot: 'source', manifest: { id: 'equity-research', flow }, inputs: {}, data: {},
    })).toMatchObject({ manifest: { flow }, headerView: null });
  });
});

it('decodes template inspection inputs and data from the Host wire', () => {
  const inspection = trackTemplateSnapshotSchema.parse(hostPayloads.currentTemplate);
  expect(inspection.inputs).toEqual({ ticker: 'SAMPLE' });
  expect(inspection.data).toEqual({ events: [] });
  expect(inspection).not.toHaveProperty('state');
});
