import { describe,expect,it } from 'vitest';

import { decodeWireEvent,wireEventSchema,type WireEvent } from '../api/schemas.js';
import { invalidationPlanFor } from './invalidation-plan.js';

function event(value: unknown): WireEvent {
  return value as WireEvent;
}

describe('invalidation plan behavior', () => {

  it('carries confirmed Area deletion before dependent invalidations', () => {
    expect(invalidationPlanFor({ ev: 'area.deleted', data: { id: 'c1' } })).toEqual({
      invalidate: [['areas'], ['overlays', 'track'], ['track-report']], remove: [],
      writeThrough: [{ mode: 'area-deleted', areaId: 'c1' }],
    });
  });

  it('write-through updates only an existing area before invalidating the list', () => {
    const value = event({ ev: 'area.updated', data: { id: 'c1', name: 'new' } });
    expect(invalidationPlanFor(value)).toEqual({
      invalidate: [['areas']],
      remove: [],
      writeThrough: [{ key: ['areas'], mode: 'replace-existing-area', value: value.data }],
    });
  });

  it('invalidates track projections and pending diagnostics for track updates', () => {
    expect(invalidationPlanFor(event({ ev: 'track.updated', data: { id: 'w1', area_id: 'c1' } }))).toEqual({
      invalidate: [
        ['overlays', 'track'], ['tracks', 'c1'], ['track', 'w1'], ['track-template', 'w1'],
        ['track-report'],
        ['track-views', 'w1'],
      ],
      remove: [],
      writeThrough: [],
    });
  });

  it('removes deleted track detail after invalidating its remaining projections', () => {
    expect(invalidationPlanFor(event({ ev: 'track.deleted', data: { id: 'w1', area_id: 'c1' } }))).toEqual({
      invalidate: [
        ['tracks', 'c1'], ['overlays', 'track'], ['track-report'],
      ],
      remove: [['track', 'w1'], ['track-report', 'w1'], ['track-views', 'w1']],
      writeThrough: [],
    });
  });

  it('invalidates track-views on report.updated, task.updated, and track.schedules.updated', () => {
    expect(invalidationPlanFor(event({ ev: 'report.updated', data: { track_id: 'w1' } })).invalidate)
      .toContainEqual(['track-views', 'w1']);
    expect(invalidationPlanFor(event({ ev: 'task.updated', data: { track_id: 'w1' } })).invalidate)
      .toContainEqual(['track-views', 'w1']);
    expect(invalidationPlanFor(event({ ev: 'track.schedules.updated', data: { trackId: 'w1' } })).invalidate)
      .toContainEqual(['track-views', 'w1']);
  });

  it('silently ignores an unknown event kind received across versions', () => {
    expect(invalidationPlanFor(event({ ev: 'zzz.unknown', data: {} }))).toEqual({
      invalidate: [],
      remove: [],
      writeThrough: [],
    });
  });
});

it.each([
  { ev: 'track.updated', data: { id: 't', area_id: 'a' } },
  { ev: 'session.updated', data: { track_id: 't', session: { id: 's' } } },
  { ev: 'turn.completed', data: { track_id: 't', turn: { id: 'turn', sessionId: 's' } } },
  { ev: 'ask.changed', data: { track_id: 't' } },
  { ev: 'approval.changed', data: { trackId: 't', sessionId: 's' } },
  { ev: 'task.updated', data: { track_id: 't' } },
])('refreshes track overlays on $ev', value => {
  expect(invalidationPlanFor(event(value)).invalidate).toContainEqual(['overlays', 'track']);
});
it('refreshes cross-device receipts on a transient receipt update', () => {
  const value = wireEventSchema.parse({ ev: 'read_receipt.updated', data: {
    kind: 'conversation', targetId: 's', readThroughMs: 200, manualUnread: false,
  } });
  expect(invalidationPlanFor(value).invalidate).toEqual([['read-receipts']]);
});

it('decodes receipt events with no persistent event ID and rejects malformed receipts', () => {
  const data = { kind: 'track', targetId: 't', readThroughMs: 0, manualUnread: true };
  expect(decodeWireEvent({ kind: 'read_receipt.updated', eventId: null, payload: data, createdAtMs: 1 }))
    .toEqual({ status: 'ready', value: { ev: 'read_receipt.updated', data } });
  expect(decodeWireEvent({ kind: 'read_receipt.updated', payload: { ...data, kind: 'area' } }).status).toBe('failed');
});

it('decodes transient session state changes and refreshes overlays and the session run', () => {
  const decoded = decodeWireEvent({ kind: 'session.state_changed', eventId: null, trackId: 't', payload: { sessionId: 's' } });
  expect(decoded).toEqual({ status: 'ready', value: { ev: 'session.state_changed', data: { trackId: 't', sessionId: 's' } } });
  if (decoded.status !== 'ready') throw new Error('Expected a decoded event');
  expect(wireEventSchema.parse(decoded.value)).toEqual(decoded.value);
  expect(invalidationPlanFor(decoded.value).invalidate).toEqual([['overlays', 'track'], ['planner-run', 's']]);
});

it.each([
  { trackId: 't', payload: {} },
  { trackId: 't', payload: { sessionId: 1 } },
  { payload: { sessionId: 's' } },
])('rejects malformed transient session state changes: %j', data => {
  expect(decodeWireEvent({ kind: 'session.state_changed', eventId: null, ...data }).status).toBe('failed');
});
