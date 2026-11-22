import { describe,expect,expectTypeOf,it } from 'vitest';

import { type WireEvent } from '../api/schemas.js';
import {
defineInvalidationPolicies,
invalidationPlanFor,
noop,
type EventKind,
type InvalidationPolicy
} from './invalidation-plan.js';

describe('invalidation plan contract', () => {
  it('requires a non-empty reason for explicit no-op policies', () => {
    expect(noop('consumed directly by its card subscriber')).toEqual({
      type: 'noop',
      reason: 'consumed directly by its card subscriber',
    });
    expect(() => noop('')).toThrow(TypeError);
  });

  it('refreshes the Track snapshot when a budget or admission setting changes', () => {
    const event = { ev: 'track.updated', data: { id: 'track-7', areaId: 'area-1' } } as Extract<
      WireEvent,
      { ev: 'track.updated' }
    >;
    expect(invalidationPlanFor(event).invalidate).toContainEqual(['track', 'track-7']);
  });

  it('refreshes Track overlays after an area disappears', () => {
    const event = { ev: 'area.deleted', data: { id: 'target-area' } } as Extract<
      WireEvent,
      { ev: 'area.deleted' }
    >;
    expect(invalidationPlanFor(event).invalidate).toContainEqual(['overlays', 'track']);
  });

  it('[type-only] rejects a policy map missing any wire event kind', () => {
    const compileOnly = false as boolean;
    if (compileOnly) {
      // @ts-expect-error -- GATE-APP-028: deleting this whole line must expose the missing-event error.
      defineInvalidationPolicies({ 'area.deleted': noop('fixture') });
    }
    expectTypeOf<EventKind>().toEqualTypeOf<WireEvent['ev']>();
    expectTypeOf<InvalidationPolicy>().toMatchTypeOf<{ type: string }>();
  });
});
