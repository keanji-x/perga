import { describe, expect, expectTypeOf, it } from 'vitest';
import { performApiRequest } from '../api/client.js';
import { decodeWireEvent } from '../api/schemas.js';
import { trackScheduleDtoSchema, trackScheduleRunDtoSchema } from '../api/perga-v1.js';
import type { TrackScheduleDto, TrackScheduleRunDto } from '../api/generated/wire.js';
import type { z } from 'zod';
import { invalidationPlanFor } from '../events/invalidation-plan.js';
import {
  trackSchedulesOperation, setTrackScheduleOperation, cancelTrackScheduleOperation, triggerTrackScheduleOperation,
  trackTemplateOperation, updateTrackTemplateInputsOperation,
} from './track-schedules.js';

describe('template-protocol wire contracts', () => {
  it('keeps generated DTOs aligned with runtime decoders', () => {
    expectTypeOf<z.infer<typeof trackScheduleDtoSchema>>().toEqualTypeOf<TrackScheduleDto>();
    expectTypeOf<z.infer<typeof trackScheduleRunDtoSchema>>().toEqualTypeOf<TrackScheduleRunDto>();
  });
  it('decodes persisted schedule events and invalidates only their track schedules', () => {
    const decoded = decodeWireEvent({ eventId: 12, trackId: 't', kind: 'track.schedules.updated', payload: { trackId: 't' }, createdAtMs: 1 });
    expect(decoded).toEqual({ status: 'ready', value: { ev: 'track.schedules.updated', data: { trackId: 't' } } });
    if (decoded.status !== 'ready') throw new Error('Expected decoded event');
    expect(invalidationPlanFor(decoded.value)).toEqual({ invalidate: [['track-schedules', 't']], remove: [], writeThrough: [] });
    expect(decodeWireEvent({ kind: 'track.schedules.updated', payload: { trackId: 42 } }).status).toBe('failed');
  });
  it('builds escaped REST operations with the exact request bodies', () => {
    const request = { title: 'Check', prompt: 'Run', runAtMs: 100, sourceKind: 'user' };
    expect(trackSchedulesOperation('a/b')).toMatchObject({ method: 'GET', path: '/api/v1/tracks/a%2Fb/schedules?includeTerminal=true&runsLimit=20' });
    expect(setTrackScheduleOperation('t', request)).toMatchObject({ method: 'POST', path: '/api/v1/tracks/t/schedules', body: request });
    expect(cancelTrackScheduleOperation('t', 'a/b')).toMatchObject({ method: 'DELETE', path: '/api/v1/tracks/t/schedules/a%2Fb' });
    expect(triggerTrackScheduleOperation('t', 'a/b')).toMatchObject({ method: 'POST', path: '/api/v1/tracks/t/schedules/a%2Fb/trigger' });
    expect(trackTemplateOperation('t')).toMatchObject({ method: 'GET', path: '/api/v1/tracks/t/template' });
    expect(updateTrackTemplateInputsOperation('t', { count: 3 })).toMatchObject({ method: 'PATCH', path: '/api/v1/tracks/t/template/inputs', body: { count: 3 } });
  });
  it('accepts the bodyless cancellation and trigger responses', async () => {
    for (const [operation, status] of [[cancelTrackScheduleOperation('t', 's'), 204], [triggerTrackScheduleOperation('t', 's'), 202]] as const) {
      const result = await performApiRequest({ send: () => Promise.resolve({ status, statusText: '', body: null }) }, operation);
      expect(result.status).toBe('ready');
    }
  });
  it('requires the public recent-run fields and never depends on wake journal internals', () => {
    const operation = trackSchedulesOperation('t');
    expect(operation.responseSchema.safeParse({ schedules: [], recentRuns: [{ id: 'w', payloadJson: '{}' }] }).success).toBe(false);
    expect(operation.responseSchema.parse({ schedules: [], recentRuns: [{ wakeId: 'w', scheduleId: 's', state: 'done', reason: 'delivered', turnId: 'turn', createdAtMs: 1, updatedAtMs: 2 }] }).recentRuns[0].scheduleId).toBe('s');
  });
});
