import { describe,expect,it } from 'vitest';

import type { PanelInput } from '../registry.js';
import { createCardRegistry } from '../registry.js';
import { FILE_VIEWER_CARD_ENTRY } from './file-viewer.tsx';
import { partitionTrackCards } from './headless-filter.js';
import type { BuiltinCardType } from './register.js';
import { BUILTIN_CARD_ORDER,registerAvailableBuiltinCards } from './register.js';
import { TERMINAL_CARD_ENTRY } from './terminal.js';
import { TRACK_REPORT_CARD_ENTRY } from './track-report.js';

declare module '../registry.js' {
  interface CardDataMap {
    declaredHeadlessFixture: { readonly type: 'declared-headless-fixture'; readonly id: string };
  }
}

const LANDED = Object.freeze([
  'terminal', 'track-report', 'file-viewer',
] as const);

describe('builtin card composition contract', () => {
  it('[INV-CARD-225] pins the seven-item order tuple', () => {
    // Insertion order is the fallback-scan order, so this literal is the resolution semantics.
    expect([...BUILTIN_CARD_ORDER]).toEqual([
      'terminal', 'track-report',
      'file-viewer',
    ]);
    expect(BUILTIN_CARD_ORDER).toHaveLength(3);
    expect(new Set(BUILTIN_CARD_ORDER).size).toBe(3);
  });

  it('registers only the entries that exist, with no placeholders for the two that do not', () => {
    const registry = createCardRegistry();
    registerAvailableBuiltinCards(registry);
    expect(registry.entries().map((entry) => entry.type)).toEqual([...LANDED]);
    for (const type of BUILTIN_CARD_ORDER) {
      if ((LANDED as readonly string[]).includes(type)) expect(registry.get(type)).toBeDefined();
      else expect(registry.get(type), `${type} must be absent, not a placeholder`).toBeUndefined();
    }
    expect(registry.get('terminal')).toBe(TERMINAL_CARD_ENTRY);
    expect(registry.get('track-report')).toBe(TRACK_REPORT_CARD_ENTRY);
    expect(registry.get('file-viewer')).toBe(FILE_VIEWER_CARD_ENTRY);
  });

  it('[INV-CARD-225] keeps the landed entries in tuple-relative order across the skipped holes', () => {
    const registry = createCardRegistry();
    registerAvailableBuiltinCards(registry);
    const registered = registry.entries().map((entry) => entry.type);
    const tupleIndexes = registered.map((type) => BUILTIN_CARD_ORDER.indexOf(type as never));
    expect(tupleIndexes).not.toContain(-1);
    expect([...tupleIndexes]).toEqual([...tupleIndexes].sort((left, right) => left - right));
    expect(tupleIndexes).toEqual([0, 1, 2]);
    const skipped = BUILTIN_CARD_ORDER
      .map((type, index) => ({ type, index }))
      .filter(({ type }) => !(registered as readonly string[]).includes(type));
    expect(skipped.map(({ type }) => type)).toEqual([]);
  });

  // `headless` is optional on the interface; a spurious declaration deletes every card of that type, a missing one shows an empty card.
  describe('headless is declared on the entry, and the declaration is what filters', () => {
    const HEADLESS_BY_TYPE: Readonly<Record<BuiltinCardType, boolean>> = Object.freeze({
      terminal: false,
      'track-report': true, 'file-viewer': false,
    });
    const bootedProductionRegistry = () => {
      const registry = createCardRegistry();
      registerAvailableBuiltinCards(registry);
      return registry;
    };

    it('[INV-CARD-225] decides headlessness for every type in the order tuple, and only those', () => {
      expect(Object.keys(HEADLESS_BY_TYPE).sort()).toEqual([...BUILTIN_CARD_ORDER].sort());
    });

    it('[INV-CARD-226] declares headless on exactly the entries that are headless', () => {
      const entries = bootedProductionRegistry().entries();
      expect(entries.length).toBeGreaterThan(0);
      for (const entry of entries) {
        const expected = HEADLESS_BY_TYPE[entry.type as BuiltinCardType];
        expect(expected, `${entry.type} is registered but not in the headless decision table`).toBeTypeOf('boolean');
        // `undefined` means nobody decided; an omitted declaration plus a `false` row would otherwise pass.
        expect(
          entry.headless,
          `${entry.type} must state its headlessness explicitly; absent is the fail-open default`,
        ).toBeTypeOf('boolean');
        expect(
          entry.headless === true,
          expected
            ? `${entry.type} is headless but does not declare it — it would occupy an empty slot`
            : `${entry.type} declares headless but owns a surface — it would vanish from the track`,
        ).toBe(expected);
      }
    });

    it('[INV-CARD-226] filters on that declaration, not on the type name', () => {
      const registry = createCardRegistry();
      registry.register({
        type: 'declared-headless-fixture',
        component: () => null,
        headless: true,
        defaultSize: { w: 4, h: 6, minW: 3, minH: 3 },
        title: () => 'fixture',
        accessibleName: () => 'fixture',
        create: { mode: 'kernel-minted-only' },
        fromPanel: (raw) => (raw.kind === 'declared' ? { type: 'declared-headless-fixture', id: raw.id } : null),
      });
      const wire = { panelType: 'custom' as const,
        id: 'd1', kind: 'declared', trackId: 'w1', title: null, sort: 0, payload: null,
        removable: true, createdAtMs: 0, updatedAtMs: 0,
      };
      const { visible, unknown } = partitionTrackCards(registry, [wire]);
      expect(visible).toEqual([]);
      expect(unknown).toEqual([]);
    });
  });

  // `partitionTrackCards` looks the entry back up by the resolved card's type, which is only sound while every `fromPanel` mints its own type.
  describe('every registered entry mints cards of its own type', () => {
    const PROBE_BY_TYPE: Readonly<Record<(typeof LANDED)[number], PanelInput>> = Object.freeze({
      terminal: { id: 'probe-term', kind: 'terminal', payload: { terminal_id: 't1' } },
      'track-report': { id: 'probe-report', kind: 'track-report', payload: null },
      'file-viewer': { id: 'probe-file', kind: 'file-viewer', payload: { path: '/tmp/probe.txt' } },
    });

    it('probes every entry the production boot registers, and only those', () => {
      const registry = createCardRegistry();
      registerAvailableBuiltinCards(registry);
      expect(Object.keys(PROBE_BY_TYPE).sort()).toEqual(registry.entries().map((entry) => entry.type).sort());
    });

    it('[INV-CARD-226] resolves each probe back to the entry that owns it', () => {
      const registry = createCardRegistry();
      registerAvailableBuiltinCards(registry);
      const entries = registry.entries();
      expect(entries.length).toBeGreaterThan(0);
      for (const entry of entries) {
        const probe = PROBE_BY_TYPE[entry.type as (typeof LANDED)[number]];
        expect(
          entry.fromPanel?.(probe)?.type,
          `${entry.type} must mint its own type, or the headless lookup reads another entry`,
        ).toBe(entry.type);
        const card = registry.resolve(probe);
        expect(card?.type, `${entry.type} probe must resolve through the registry`).toBe(entry.type);
        if (card !== null) expect(registry.get(card.type)).toBe(entry);
      }
    });
  });

  it('takes no entries and keeps no state: two registries boot independently', () => {
    const first = createCardRegistry();
    const second = createCardRegistry();
    registerAvailableBuiltinCards(first);
    registerAvailableBuiltinCards(second);
    expect(second.entries().map((entry) => entry.type)).toEqual([...LANDED]);
    expect(registerAvailableBuiltinCards).toHaveLength(1);
  });
});
