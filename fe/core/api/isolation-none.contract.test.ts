import { describe,expectTypeOf,it } from 'vitest';
import type { TrackWire,WorkspaceIsolation } from './generated/wire.ts';
import type { WireEvent } from './schemas.ts';

describe('workspace isolation wire contracts', () => {
  it('requires the projected isolation for track reads and events', () => {
    expectTypeOf<TrackWire['workspace']['isolation']>().toEqualTypeOf<WorkspaceIsolation>();
    expectTypeOf<Extract<WireEvent, { ev: 'track.updated' }>['data']['workspace']['isolation']>()
      .toEqualTypeOf<WorkspaceIsolation>();
  });
});
