// Unit tests for the WS event zod schemas.

import { describe,expect,expectTypeOf,it } from 'vitest';
import type { z } from 'zod';
import { areaFolderWireSchema,folderConflictSchema } from '../domain/area.js';
import type { DecodeFailure } from '../state/types.js';
import type {
Area as GeneratedArea,
AreaFolder as GeneratedAreaFolder,
Card as GeneratedCard,
FolderConflict as GeneratedFolderConflict,
Overlay as GeneratedOverlay,
TrackWire as GeneratedTrack
} from './generated/wire.js';
import type { WireEventDecodeResult } from './schemas.js';
import {
areaSchema,
cardSchema,
overlaySchema,
trackSchema,
wireEventSchema,
} from './schemas.js';
import type { ApiDecodeFailure } from './types.js';

describe('wireEventSchema', () => {
  it('defaults area.updated kind to "user" when absent (legacy wire payload)', () => {
    const payload = {
      ev: 'area.updated',
      data: {
        id: 'area_legacy',
        name: 'Atlas',
        color: '#abc',
        sort: 0,
        created_at: 1000,
        updated_at: 2000,
      },
    };
    const parsed = wireEventSchema.parse(payload);
    if (parsed.ev === 'area.updated') {
      expect(parsed.data).toMatchObject({
        kind: 'user', default_template_id: null, default_cwd: null,
      });
    }
  });

  it('rejects an unknown ev string via safeParse', () => {
    const result = wireEventSchema.safeParse({
      ev: 'totally.made.up',
      data: { id: 'x' },
    });
    expect(result.success).toBe(false);
    // The exact issue code varies by zod version.
    if (!result.success) {
      expect(result.error.issues.length).toBeGreaterThan(0);
    }
  });

  it('defaults absent template metadata on track.updated payloads', () => {
    const parsed = wireEventSchema.parse({
      ev: 'track.updated',
      data: { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '/repo', frozen_at: null },
        id: 'track_1',
        area_id: 'area_1',
        title: 'hello',
        sort: 0,
        pinned_at: null,
        cwd: '/repo',
        template_id: null,
        closed_at: null,
        created_at: 1,
        updated_at: 2,
        agent_message: 'moving to dispatch',
      },
    });
    expect(parsed.ev).toBe('track.updated');
    if (parsed.ev === 'track.updated') {
      expect(parsed.data.template_input).toBeNull();
      expect(parsed.data.plugin_scope).toBeNull();
    }
  });
});

// The zod schemas are pinned to the TS types `ts-rs` emits from the Rust `Event` enum;
// drift in either direction fails `tsc -b` here.
describe('zod ↔ ts-rs conformance', () => {
  it('keeps API and state decode failures intentionally shape-equivalent', () => {
    expectTypeOf<ApiDecodeFailure>().toEqualTypeOf<DecodeFailure>();
    expectTypeOf<Extract<WireEventDecodeResult, { status: 'failed' }>['error']>()
      .toEqualTypeOf<ApiDecodeFailure>();
  });

  it('entity sub-schemas match their generated counterparts', () => {
    expectTypeOf<z.infer<typeof areaSchema>>().toEqualTypeOf<GeneratedArea>();
    expectTypeOf<z.infer<typeof trackSchema>>().toExtend<GeneratedTrack>();
    expectTypeOf<GeneratedTrack>().toExtend<z.infer<typeof trackSchema>>();
    expectTypeOf<z.infer<typeof cardSchema>>().toEqualTypeOf<GeneratedCard>();
    expectTypeOf<z.infer<typeof overlaySchema>>().toEqualTypeOf<GeneratedOverlay>();
  });

  it('REST-only sub-schemas match their generated counterparts', () => {
    expectTypeOf<z.infer<typeof areaFolderWireSchema>>().toEqualTypeOf<GeneratedAreaFolder>();
    expectTypeOf<z.infer<typeof folderConflictSchema>>().toEqualTypeOf<GeneratedFolderConflict>();
  });
});

describe('entity sub-schema compatibility', () => {
  it('areaSchema fills kind and Area defaults when absent from a legacy event', () => {
    const parsed = areaSchema.parse({
      id: 'c1',
      name: 'n',
      color: '#fff',
      sort: 0,
      created_at: 1,
      updated_at: 2,
    });
    expect(parsed).toMatchObject({
      kind: 'user', default_template_id: null, default_cwd: null,
    });
  });
});

describe('entity sub-schemas', () => {
  it('trackSchema defaults `closed_at` to null when the field is missing', () => {
    const w = { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '', frozen_at: null },
      id: 'w1',
      area_id: 'c1',
      title: 't',
      sort: 0,
      created_at: 1,
      updated_at: 2,
    };
    expect(trackSchema.parse(w).closed_at).toBeNull();
  });

  it('trackSchema requires and preserves `workspace` (#1147 S1)', () => {
    // An undeclared field is *stripped* by zod, so the "present key" case pins that
    // the field actually survives parsing.
    const base = { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '', frozen_at: null },
      id: 'w1',
      area_id: 'c1',
      title: 't',
      sort: 0,
      created_at: 1,
      updated_at: 2,
    };
    expect(trackSchema.parse(base).workspace).toEqual({
      isolation: 'none',
      kind: 'attached',
      path: '',
      frozen_at: null,
    });
    const live = trackSchema.parse({
      ...base,
      cwd: '/srv/neige-workspaces/c1/w1',
      workspace: {
        isolation: 'managed',
        kind: 'managed',
        path: '/srv/neige-workspaces/c1/w1',
        frozen_at: 4242,
      },
    });
    expect(live.workspace).toEqual({
      isolation: 'managed',
      kind: 'managed',
      path: '/srv/neige-workspaces/c1/w1',
      frozen_at: 4242,
    });
    // No `live.workspace.path === live.cwd` assertion: zod has no cross-field constraint,
    // so it would be true no matter what the schema said.
    // #1830: an attached track's worktree rides along; serde omits it when there is none.
    const worktree = '/repo/.claude/worktrees/track-w1';
    expect(
      trackSchema.parse({
        ...base,
        workspace: { isolation: 'worktree', kind: 'attached', path: '/repo', frozen_at: 1, worktree },
      }).workspace.worktree,
    ).toBe(worktree);
    expect(live.workspace).not.toHaveProperty('worktree');
  });

  it('trackSchema rejects a present-but-incomplete `workspace` (#1147 S1)', () => {
    // Workspace and every required projection field must be present.
    const base = { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '', frozen_at: null },
      id: 'w1',
      area_id: 'c1',
      title: 't',
      sort: 0,
      created_at: 1,
      updated_at: 2,
    };
    for (const bad of [
      {},
      { kind: 'managed' },
      { path: '/p', frozen_at: null },
      { kind: 'managed', path: '/p' },
      { kind: 'bogus', path: '/p', frozen_at: null },
    ]) {
      expect(trackSchema.safeParse({ ...base, workspace: bad }).success).toBe(
        false,
      );
    }
    expect(
      trackSchema.safeParse({
        ...base,
        workspace: { isolation: 'managed', kind: 'managed', path: '/p', frozen_at: null },
      }).success,
    ).toBe(true);
  });

});

// Historical rows spell the template fields with the pre-rename keys; this one-way
// normalize is deliberately one of three independent copies, not a shared helper.
describe('#1209 pre-rename template keys on the track shape', () => {
  const legacyTrack = { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '', frozen_at: null },
    id: 'w1',
    area_id: 'c1',
    title: 't',
    sort: 0,
    workflow_id: 'small-change',
    workflow_input: { issue: 1209 },
    created_at: 1,
    updated_at: 2,
  };

  it('does not let a legacy key overwrite a present new key', () => {
    const parsed = trackSchema.parse({
      ...legacyTrack,
      template_id: 'investigation',
      template_input: { issue: 1 },
    });
    expect(parsed.template_id).toBe('investigation');
    expect(parsed.template_input).toEqual({ issue: 1 });
  });
});
