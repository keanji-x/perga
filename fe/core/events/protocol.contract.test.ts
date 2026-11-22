import { describe,expect,expectTypeOf,it } from 'vitest';

import type { EventFrame,EventMeta,EventSubscriptionFrame,Topic } from './protocol.js';
import { decodeEventFrame } from './protocol.js';

describe('event protocol contract', () => {
  it('pins envelope field names and both control-frame discriminators', () => {
    expect(decodeEventFrame({ ev: '_replay_complete', _id: 17 })).toEqual({
      status: 'ready',
      frame: { type: 'replay-complete', id: 17 },
    });
    expect(decodeEventFrame({ ev: '_snapshot_required' })).toEqual({
      status: 'ready',
      frame: { type: 'snapshot-required' },
    });
  });

  it('keeps cursor and version as envelope metadata, not payload fields', () => {
    const result = decodeEventFrame({
      ev: 'area.deleted',
      data: { id: 'area-1' },
      _id: 23,
      eventVersion: 4,
    });
    expect(result.status).toBe('ready');
    if (result.status === 'ready' && result.frame.type === 'event') {
      expect(result.frame.meta).toEqual({ id: 23, eventVersion: 4 });
      expect(result.frame.event).toEqual({ ev: 'area.deleted', data: { id: 'area-1' } });
      expect(result.frame.event).not.toHaveProperty('_id');
      expect(result.frame.event).not.toHaveProperty('eventVersion');
    }
  });

  it('freezes public frame, metadata, and topic shapes', () => {
    expectTypeOf<EventMeta>().toEqualTypeOf<Readonly<{ id: number; eventVersion: number }>>();
    expectTypeOf<EventSubscriptionFrame>().toEqualTypeOf<Readonly<{
      sub: readonly Topic[];
      since: number;
    }>>();
    expectTypeOf<Topic>().toEqualTypeOf<'*' | `area:${string}` | `track:${string}` | `card:${string}`>();
    expectTypeOf<EventFrame['type']>().toEqualTypeOf<
      'event' | 'malformed-event' | 'replay-complete' | 'snapshot-required'
    >();
  });
});
