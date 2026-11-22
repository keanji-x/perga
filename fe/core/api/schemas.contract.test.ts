import { describe, expect, expectTypeOf, it } from 'vitest';
import type { AreaDto, TrackDto } from './perga-v1.js';
import { decodeWireEvent, wireEventSchema, type WireEvent, type WireEventDecodeResult } from './schemas.js';
import type { ApiDecodeFailure } from './types.js';
import type { DecodeFailure } from '../state/types.js';

describe('HTTP/event DTO contract', () => {
  it('shares DTO types and decode failures', () => {
    expectTypeOf<Extract<WireEvent, { ev: 'area.updated' }>['data']>().toEqualTypeOf<AreaDto>();
    expectTypeOf<Extract<WireEvent, { ev: 'track.updated' }>['data']>().toExtend<TrackDto>();
    expectTypeOf<ApiDecodeFailure>().toEqualTypeOf<DecodeFailure>();
    expectTypeOf<Extract<WireEventDecodeResult, { status: 'failed' }>['error']>().toEqualTypeOf<ApiDecodeFailure>();
  });
  it('retains the runtime Area DTO fields without a snake_case projection', () => {
    const payload = { id: 'a', name: 'Work', color: '#fff', sortOrder: 2, folders: ['/repo'], defaultCwd: '/repo', executionTargetId: 'ssh', createdAtMs: 1, updatedAtMs: 2 };
    const decoded = decodeWireEvent({ kind: 'area.updated', eventId: 1, createdAtMs: 2, payload });
    expect(decoded).toEqual({ status: 'ready', value: { ev: 'area.updated', data: payload } });
  });
  it('rejects unknown events and old entity carriers', () => {
    expect(wireEventSchema.safeParse({ ev: 'unknown', data: {} }).success).toBe(false);
    expect(wireEventSchema.safeParse({ ev: 'track.updated', data: { id: 't', area_id: 'a', created_at: 1, updated_at: 2 } }).success).toBe(false);
  });
});
