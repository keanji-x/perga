import { describe,expect,it } from 'vitest';

import type { CardWire } from '../../../../../core/domain/track.ts';
import { createCardRegistry } from '../registry.js';
import { partitionTrackCards } from './headless-filter.js';
import { registerAvailableBuiltinCards } from './register.js';

function wire(overrides: Partial<CardWire> & Pick<CardWire, 'id' | 'kind'>): CardWire {
  return {
    track_id: 'w1', title: null, sort: 0, payload: null,
    deletable: true, created_at: 0, updated_at: 0, ...overrides,
  };
}

function bootedRegistry() {
  const registry = createCardRegistry();
  registerAvailableBuiltinCards(registry);
  return registry;
}

describe('headless card filtering', () => {
  it('[INV-CARD-226] filters raw track-report kinds out of the unknown branch defensively', () => {
    const bare = createCardRegistry();
    const { visible, unknown } = partitionTrackCards(bare, [
      wire({ id: 'report', kind: 'track-report' }),
      wire({ id: 'term-1', kind: 'terminal' }),
    ]);
    expect(visible).toEqual([]);
    expect(unknown.map((slot) => slot.wire.id)).toEqual(['term-1']);
    expect(unknown[0]?.originalIndex).toBe(1);
  });

  it('routes cards with a surface into the visible branch with their wire and index', () => {
    const registry = createCardRegistry();
    registerAvailableBuiltinCards(registry);
    registry.register({
      type: 'surface-fixture',
      component: () => null,
      defaultSize: { w: 4, h: 6, minW: 3, minH: 3 },
      title: () => 'fixture',
      accessibleName: () => 'fixture',
      create: { mode: 'kernel-minted-only' },
      fromKernel: (raw) => (raw.kind === 'surface' ? { type: 'surface-fixture', id: raw.id } : null),
    });
    const cards = [
      wire({ id: 'report', kind: 'track-report' }),
      wire({ id: 'surface-1', kind: 'surface' }),
      wire({ id: 'term-1', kind: 'terminal' }),
    ];
    const { visible, unknown } = partitionTrackCards(registry, cards);
    expect(visible.map((slot) => [slot.card.type, slot.wire.id, slot.originalIndex]))
      .toEqual([['surface-fixture', 'surface-1', 1], ['terminal', 'term-1', 2]]);
    expect(unknown).toEqual([]);
  });

  it('preserves arrival order and does not sort', () => {
    const cards = [
      wire({ id: 'b', kind: 'terminal', sort: 9 }),
      wire({ id: 'a', kind: 'terminal', sort: 1 }),
    ];
    const { visible } = partitionTrackCards(bootedRegistry(), cards);
    expect(visible.map((slot) => slot.wire.id)).toEqual(['b', 'a']);
  });

  it('returns empty branches for an empty track', () => {
    const { visible, unknown } = partitionTrackCards(bootedRegistry(), []);
    expect(visible).toEqual([]);
    expect(unknown).toEqual([]);
  });
});

declare module '../registry.js' {
  interface CardDataMap {
    surfaceFixture: { readonly type: 'surface-fixture'; readonly id: string };
  }
}
