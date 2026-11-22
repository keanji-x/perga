import { describe,expect,it } from 'vitest';


import type { PanelItem } from '../../../../../core/domain/panel-item.ts';
import { createCardRegistry } from '../registry.js';
import { partitionTrackCards } from './headless-filter.js';
import { registerAvailableBuiltinCards } from './register.js';

function item(overrides: Partial<PanelItem> & Pick<PanelItem, 'id' | 'kind'>): PanelItem {
  return {
    trackId: 'w1', title: null, sort: 0, payload: null, presentation: { source: 'manual', inWorkbench: true },
    removable: true, createdAtMs: 0, updatedAtMs: 0, ...overrides, panelType: 'custom',
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
      item({ id: 'report', kind: 'track-report' }),
      item({ id: 'term-1', kind: 'terminal' }),
    ]);
    expect(visible).toEqual([]);
    expect(unknown.map((slot) => slot.item.id)).toEqual(['term-1']);
    expect(unknown[0]?.originalIndex).toBe(1);
  });

  it('routes cards with a surface into the visible branch with their item and index', () => {
    const registry = createCardRegistry();
    registerAvailableBuiltinCards(registry);
    registry.register({
      type: 'surface-fixture',
      component: () => null,
      title: () => 'fixture',
      accessibleName: () => 'fixture',
      create: { mode: 'kernel-minted-only' },
      fromPanel: (raw) => (raw.kind === 'surface' ? { type: 'surface-fixture', id: raw.id } : null),
    });
    const cards = [
      item({ id: 'report', kind: 'track-report' }),
      item({ id: 'surface-1', kind: 'surface' }),
      item({ id: 'term-1', kind: 'terminal' }),
    ];
    const { visible, unknown } = partitionTrackCards(registry, cards);
    expect(visible.map((slot) => [slot.card.type, slot.item.id, slot.originalIndex]))
      .toEqual([['surface-fixture', 'surface-1', 1], ['terminal', 'term-1', 2]]);
    expect(unknown).toEqual([]);
  });

  it('preserves arrival order and does not sort', () => {
    const cards = [
      item({ id: 'b', kind: 'terminal', sort: 9 }),
      item({ id: 'a', kind: 'terminal', sort: 1 }),
    ];
    const { visible } = partitionTrackCards(bootedRegistry(), cards);
    expect(visible.map((slot) => slot.item.id)).toEqual(['b', 'a']);
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
