import hostPayloads from '../api/fixtures/unified-host-payloads.json';
import { describe, expect, it } from 'vitest';
import { decodeWireEvent } from '../api/schemas.js';
import { invalidationPlanFor } from '../events/invalidation-plan.js';
import { trackTemplateOperation, updateTrackTemplateInputsOperation } from './track-template.js';

describe('template-protocol wire contracts', () => {
  it('decodes Flow clock events and refreshes the track views', () => {
    const decoded = decodeWireEvent({ eventId: 12, trackId: 't', kind: 'track.schedules.updated', payload: { trackId: 't' }, createdAtMs: 1 });
    expect(decoded).toEqual({ status: 'ready', value: { ev: 'track.schedules.updated', data: { trackId: 't' } } });
    if (decoded.status !== 'ready') throw new Error('Expected decoded event');
    expect(invalidationPlanFor(decoded.value)).toEqual({ invalidate: [['track-views', 't']], remove: [], writeThrough: [] });
    expect(decodeWireEvent({ kind: 'track.schedules.updated', payload: { trackId: 42 } }).status).toBe('failed');
  });

  it('builds escaped template operations with the exact input patch', () => {
    expect(trackTemplateOperation('a/b')).toMatchObject({ method: 'GET', path: '/api/v1/tracks/a%2Fb/template' });
    expect(updateTrackTemplateInputsOperation('a/b', { count: 3 })).toMatchObject({
      method: 'PATCH', path: '/api/v1/tracks/a%2Fb/template/inputs', body: { count: 3 },
    });
  });

  it('decodes the template Flow blueprint when the Host omits the header view', () => {
    const operation = trackTemplateOperation('research');
    const flow = [{ id: 'weekly-price-refresh', when: { cron: '30 8 * * 1' }, do: {
      kind: 'wake', session: 'lead', prompt: 'Refresh ${inputs.ticker}',
    } }];
    expect(operation.responseSchema.parse({
      snapshot: 'source', manifest: { id: 'equity-research', flow }, inputs: {}, data: {},
    })).toMatchObject({ manifest: { flow }, headerView: null });
  });
});

it('decodes template inspection inputs and data from the Host wire', () => {
  const inspection = trackTemplateOperation('research').responseSchema.parse(hostPayloads.currentTemplate);
  expect(inspection.inputs).toEqual({ ticker: 'SAMPLE' });
  expect(inspection.data).toEqual({ events: [] });
  expect(inspection).not.toHaveProperty('state');
});
