import { expectTypeOf, it } from 'vitest';
import type { TrackDto } from './perga-v1.js';
import type { WireEvent } from './schemas.js';
it('shares the Host DTO between HTTP and event consumers', () => {
  expectTypeOf<Extract<WireEvent, { ev: 'track.updated' }>['data']>().toExtend<TrackDto>();
});
