import { describe, expect, it } from 'vitest';

import {
  activeTracksOn, createCardOperation, createCodexCardOperation, createTerminalCardOperation,
  createTrackOperation, deleteCardOperation, hasFailed, isBlankForKernel, isClosed,
  isWorking, needsUserAttention, toTrack, trackActivityFromOverlay, trackActivitiesFromOverlays,
  sortAreaTracksByRecent, trackRecentAt, limitAreaTracks, railAreaTracks, AREA_TRACK_LIMIT,
  trackActivityState, updateTrackOperation, trackWorkspaceOptionsOperation,
  NEUTRAL_ACTIVITY, UNTITLED_TRACK_LABEL, trackDisplayTitle, trackWireSchema, tracksInAreaOperation,
  trackDtoToWire, userVisibleTracks, trackOverlayPayload,
  trackDetailOperation, terminalDtoToCardWire, type Track, type OverlayWire,
} from './track.js';
import { terminalPresentation } from './terminal-presentation.js';
import type { Area } from './area.js';
import { overlaySchema } from '../api/schemas.js';
import { trackDtoSchema, decodeWireEvent } from '../api/schemas.js';
import { performApiRequest } from '../api/client.js';
import type { TrackOverlayItemDto } from '../api/perga-v1.js';

const baseWire = { workspace: { isolation: 'none' as const, kind: 'attached' as const, path: '', frozen_at: null },
  id: 'w1', area_id: 'c1', title: 'Ship it', sort: 1,
  created_at: 1_000, updated_at: 1_000,
};

function track(overrides: Partial<Track>): Track {
  return {
    id: 'w', areaId: 'c', title: 't', sort: 1, cwd: '/tmp', isolation: 'none', agentCwd: '/tmp',
    pinnedAt: null, closedAt: null, createdAt: 0, updatedAt: 0,
    templateId: null, templateTitle: null, templateInputs: {}, templateHeaderView: null,
    ...NEUTRAL_ACTIVITY,
    ...overrides,
  };
}

const DAY = 24 * 60 * 60 * 1000;

describe('track aggregate HTTP decode', () => {
  it('returns the authoritative DTO without synthesizing cards or revision metadata', async () => {
    const body = { track: { id: 'w1', areaId: 'c1', createdAtMs: 1, updatedAtMs: 2 },
      report: { docRev: 7, blocks: [] }, sessions: [], tasks: [], openAsks: [] };
    const result = await performApiRequest({
      send: () => Promise.resolve({ status: 200, statusText: 'OK', body }),
    }, trackDetailOperation('w1'));
    expect(result).toMatchObject({ status: 'ready', value: {
      track: { id: 'w1', areaId: 'c1' }, report: { docRev: 7 }, sessions: [],
    } });
    expect(trackDetailOperation('w1').responseSchema.safeParse({ track: baseWire }).success).toBe(false);
  });
});

describe('independent tool and overlay decode', () => {
  const card = { id: 'card', track_id: 'w1', kind: 'note', sort: 0,
    payload: { arbitrary: ['kept'] }, created_at: 1, updated_at: 2 };

  it('defaults tool metadata and strips unknown fields on the create receipt', () => {
    expect(createCardOperation('w1', { kind: card.kind, payload: card.payload }, 'key')
      .responseSchema.parse({ ...card, future_field: 'ignored' }))
      .toEqual({ ...card, title: null, deletable: true });
  });

  it('preserves explicit tool titles and deletion policy', () => {
    for (const title of ['Named', null]) {
      const input = { ...card, title, deletable: false };
      expect(createCardOperation('w1', { kind: card.kind, payload: card.payload }, 'key')
        .responseSchema.parse(input)).toEqual(input);
    }
  });

  it('keeps opaque overlay payloads and open kinds while stripping unknown fields', () => {
    const overlay = { id: 'overlay', plugin_id: 'plugin', entity_kind: 'future-entity', entity_id: 'w1',
      kind: 'future-kind', payload: { arbitrary: ['kept'] }, updated_at: 2 };
    expect(overlaySchema.parse({ ...overlay, future_field: 'ignored' })).toEqual(overlay);
  });
});

describe('track wire decode', () => {
  it('fills the kernel serde defaults so the decoded track has no optional fields', () => {
    const parsed = trackWireSchema.parse(baseWire);
    expect(parsed).toMatchObject({
      cwd: '', pinned_at: null, closed_at: null,
    });
  });

  it('keeps explicit wire values over the defaults', () => {
    const parsed = trackWireSchema.parse({ ...baseWire, cwd: '/srv', closed_at: 7 });
    expect(parsed.cwd).toBe('/srv');
    expect(parsed.closed_at).toBe(7);
  });

  it('drops server fields this slice does not model instead of failing the decode', () => {
    expect(trackWireSchema.safeParse({ ...baseWire, template_id: null, purpose: null }).success).toBe(true);
  });

  it('maps the wire row onto the camelCase domain shape', () => {
    expect(toTrack(trackWireSchema.parse({ ...baseWire, pinned_at: 42 }))).toEqual(track({
      id: 'w1', areaId: 'c1', title: 'Ship it', cwd: '', isolation: 'none', agentCwd: '', pinnedAt: 42, createdAt: 1_000,
      updatedAt: 1_000,
    }));
  });

  it('roots the agent at the track worktree when the kernel made one (#1830), else at the checkout', () => {
    const worktree = '/repo/.claude/worktrees/track-w1';
    const attached = toTrack(trackWireSchema.parse({
      ...baseWire, cwd: '/repo', workspace: { isolation: 'worktree', kind: 'attached', path: '/repo', frozen_at: 1, worktree },
    }));
    expect(attached.cwd).toBe('/repo');
    expect(attached.agentCwd).toBe(worktree);
    const managed = toTrack(trackWireSchema.parse({
      ...baseWire, cwd: '/w', workspace: { isolation: 'managed', kind: 'managed', path: '/w', frozen_at: null },
    }));
    expect(managed.agentCwd).toBe('/w');
  });

  it('percent-encodes the area id into the list path', () => {
    expect(tracksInAreaOperation('a/b').path).toBe('/api/v1/areas/a%2Fb/tracks?includeClosed=true');
  });
});

describe('track create operation', () => {
  const theme = { fg: [1, 2, 3], bg: [4, 5, 6] } as const;

  it('carries the draft key on every create', () => {
    const keyed = createTrackOperation(
      { area_id: 'area', planner_provider: 'codex', theme, permissionMode: 'live-mode' },
      'draft-key',
    );
    expect(keyed.headers).toEqual({ 'Idempotency-Key': 'draft-key' });
    expect(keyed.path).toBe('/api/v1/tracks');
    expect(keyed.body).toMatchObject({ permissionMode: 'live-mode' });
  });
});

describe('card operations', () => {
  const theme = { fg: [1, 2, 3], bg: [4, 5, 6] } as const;

  it('deletes a card by id on DELETE /api/cards/:id', () => {
    const operation = deleteCardOperation('card 1/2');
    expect(operation.method).toBe('DELETE');
    expect(operation.path).toBe('/api/cards/card%201%2F2');
    expect('body' in operation).toBe(false);
  });

  it('mints a codex card on the kind\'s own atomic endpoint, carrying the body verbatim', () => {
    const body = { theme, title: 'Codex', cwd: '/srv' };
    const operation = createCodexCardOperation('w/1', body, 'intent-key');
    expect(operation.method).toBe('POST');
    expect(operation.path).toBe('/api/tracks/w%2F1/codex-cards');
    expect(operation.body).toBe(body);
    expect(operation.headers).toEqual({ 'Idempotency-Key': 'intent-key' });
  });

  it('mints a terminal card on its own atomic endpoint, not the generic one', () => {
    const operation = createTerminalCardOperation('w1', { theme }, 'intent-key');
    expect(operation.method).toBe('POST');
    expect(operation.path).toBe('/api/v1/tracks/w1/terminals');
    expect(operation.headers).toEqual({ 'Idempotency-Key': 'intent-key' });
  });

  it('writes a runtime-less card through the generic create with its kind and payload', () => {
    const body = { kind: 'file-viewer', payload: { path: '/repo/notes.md' }, title: 'Notes' };
    const operation = createCardOperation('w/1', body, 'intent-key');
    expect(operation.method).toBe('POST');
    expect(operation.path).toBe('/api/tracks/w%2F1/cards');
    expect(operation.body).toBe(body);
    expect(operation.headers).toEqual({ 'Idempotency-Key': 'intent-key' });
  });
});

describe('open and closed', () => {
  it('reads closed from closedAt alone', () => {
    expect(isClosed(track({ closedAt: null }))).toBe(false);
    expect(isClosed(track({ closedAt: 5 }))).toBe(true);
  });

  it('builds the reopen PATCH without a parallel endpoint', () => {
    const operation = updateTrackOperation('w/1', { closed: false });
    expect(operation).toMatchObject({
      method: 'PATCH', path: '/api/v1/tracks/w%2F1', body: { closed: false },
    });
  });

  it('falls back to a single untitled label', () => {
    expect(trackDisplayTitle('   ')).toBe(UNTITLED_TRACK_LABEL);
    expect(trackDisplayTitle(' Ship ')).toBe('Ship');
  });
});

describe('activity predicates read only the kernel activity overlay (INV-APP-118)', () => {
  it('does not derive attention or failure from the open/closed state', () => {
    const closed = track({ closedAt: 5, attention: 'none' });
    expect(needsUserAttention(closed)).toBe(false);
    expect(hasFailed(closed)).toBe(false);
    expect(trackActivityState(closed, false)).toBe('quiet');
  });

  it('does not derive working from an open track', () => {
    expect(isWorking(track({ closedAt: null, working: false }))).toBe(false);
    expect(trackActivityState(track({ closedAt: null, working: false }), false)).toBe('quiet');
    expect(isWorking(track({ closedAt: 5, working: true }))).toBe(true);
    expect(trackActivityState(track({ closedAt: 5, working: true }), false)).toBe('working');
  });

  it('reads attention and failure from the overlay verdict', () => {
    expect(needsUserAttention(track({ attention: 'input' }))).toBe(true);
    expect(hasFailed(track({ attention: 'input' }))).toBe(false);
    expect(hasFailed(track({ closedAt: 5, attention: 'failed' }))).toBe(true);
    expect(needsUserAttention(track({ closedAt: 5, attention: 'failed' }))).toBe(false);
    expect(trackActivityState(track({ attention: 'input', working: true }), true)).toBe('attention');
    expect(trackActivityState(track({ attention: 'failed', working: true }), true)).toBe('failed');
    expect(trackActivityState(track({ attention: 'none', working: false }), true)).toBe('unread');
  });
});

describe('trackActivityFromOverlay & trackActivitiesFromOverlays', () => {
  const item = (over: Partial<TrackOverlayItemDto> = {}): TrackOverlayItemDto => ({
    trackId: 't1',
    areaId: 'a1',
    isWorking: false,
    openAskCount: 0,
    pendingApprovalCount: 0,
    hasBlockedSession: false,
    workspaceFailed: false,
    notifiableSessions: [],
    updatedAtMs: 100,
    ...over,
  });

  it('maps blocked sessions and workspace failures to failed, asks/approvals to input, and otherwise none', () => {
    expect(trackActivityFromOverlay(item({ hasBlockedSession: true, openAskCount: 1, isWorking: true }))).toMatchObject({
      attention: 'failed', working: true, recentAt: 100,
    });
    expect(trackActivityFromOverlay(item({ workspaceFailed: true }))).toMatchObject({
      attention: 'failed', working: false,
    });
    expect(trackActivityFromOverlay(item({ pendingApprovalCount: 1 }))).toMatchObject({
      attention: 'input', working: false,
    });
    expect(trackActivityFromOverlay(item())).toMatchObject({
      attention: 'none', working: false,
    });
  });

  it('indexes overlays by trackId without mutating the input or sharing containers', () => {
    const rows = [
      item({ trackId: 't1', isWorking: true, updatedAtMs: 150, notifiableSessions: [{ sessionId: 's1', lastNotifiableTurnAtMs: 120 }] }),
      item({ trackId: 't2', updatedAtMs: Number.NaN }),
    ];
    const map = trackActivitiesFromOverlays(rows);
    expect(map.get('t1')).toMatchObject({
      working: true, attention: 'none', recentAt: 150,
      notifiableSessions: [{ sessionId: 's1', lastNotifiableTurnAtMs: 120 }],
    });
    expect(map.get('t2')).toEqual(NEUTRAL_ACTIVITY);
    expect(map.has('missing')).toBe(false);
  });
});

describe('Area recent-activity order', () => {
  it('orders by the newest finite row or activity time, then sort and bytewise id', () => {
    const rows = [
      track({ id: 'b', sort: 1, updatedAt: 20, recentAt: 100 }),
      track({ id: 'a', sort: 1, updatedAt: 100, recentAt: 30 }),
      track({ id: 'z', sort: 0, updatedAt: 100, recentAt: null }),
      track({ id: 'bad', sort: 0, updatedAt: Number.NaN, createdAt: 5, recentAt: Number.POSITIVE_INFINITY }),
    ];
    expect(sortAreaTracksByRecent(rows).map((row) => row.id)).toEqual(['z', 'a', 'b', 'bad']);
    expect(trackRecentAt(rows[3])).toBe(5);
  });

  it('returns a new array without mutating its source or Track objects', () => {
    const old = track({ id: 'old', sort: 1, updatedAt: 1 });
    const recent = track({ id: 'recent', sort: 2, updatedAt: 2 });
    const source = [old, recent];
    const sorted = sortAreaTracksByRecent(source);
    expect(sorted.map((row) => row.id)).toEqual(['recent', 'old']);
    expect(source).toEqual([old, recent]);
    expect(sorted[0]).toBe(recent);
  });
});

describe('limitAreaTracks', () => {
  const ids = (count: number) => Array.from({ length: count }, (_, index) => track({ id: `t${index + 1}` }));
  const shown = (rows: readonly Track[]) => rows.map((row) => row.id);

  it('keeps the first five in order and counts the rest as hidden', () => {
    expect(AREA_TRACK_LIMIT).toBe(5);
    const limited = limitAreaTracks(ids(7), AREA_TRACK_LIMIT, null);
    expect(shown(limited.rows)).toEqual(['t1', 't2', 't3', 't4', 't5']);
    expect(limited.hiddenCount).toBe(2);
  });

  it('hides nothing at or under the limit', () => {
    for (const count of [0, 1, 5]) {
      const limited = limitAreaTracks(ids(count), AREA_TRACK_LIMIT, null);
      expect(shown(limited.rows), `${count}`).toEqual(shown(ids(count)));
      expect(limited.hiddenCount, `${count}`).toBe(0);
    }
  });

  it('does not repeat an open Track that is already among the first five', () => {
    const limited = limitAreaTracks(ids(7), AREA_TRACK_LIMIT, 't3');
    expect(shown(limited.rows)).toEqual(['t1', 't2', 't3', 't4', 't5']);
    expect(limited.hiddenCount).toBe(2);
  });

  it('keeps an open sixth Track, leaving nothing hidden', () => {
    const limited = limitAreaTracks(ids(6), AREA_TRACK_LIMIT, 't6');
    expect(shown(limited.rows)).toEqual(['t1', 't2', 't3', 't4', 't5', 't6']);
    expect(limited.hiddenCount).toBe(0);
  });

  it('keeps an open seventh Track at its own position, last, without counting it as hidden', () => {
    const limited = limitAreaTracks(ids(8), AREA_TRACK_LIMIT, 't7');
    expect(shown(limited.rows)).toEqual(['t1', 't2', 't3', 't4', 't5', 't7']);
    expect(limited.hiddenCount).toBe(2);
    const seven = limitAreaTracks(ids(7), AREA_TRACK_LIMIT, 't7');
    expect(shown(seven.rows)).toEqual(['t1', 't2', 't3', 't4', 't5', 't7']);
    expect(seven.hiddenCount).toBe(1);
  });

  it('ignores an open Track that is not in this Area', () => {
    const limited = limitAreaTracks(ids(7), AREA_TRACK_LIMIT, 'elsewhere');
    expect(shown(limited.rows)).toEqual(['t1', 't2', 't3', 't4', 't5']);
    expect(limited.hiddenCount).toBe(2);
  });

  it('leaves its input untouched', () => {
    const source = ids(7);
    const before = [...source];
    const limited = limitAreaTracks(source, AREA_TRACK_LIMIT, 't7');
    expect(source).toEqual(before);
    expect(limited.rows).not.toBe(source);
    expect(limited.rows[0]).toBe(source[0]);
  });
});

describe('railAreaTracks', () => {
  // t1 open; t2 closed and unread; t3 closed and open in the view; t4 closed, read and elsewhere.
  const source = [
    track({ id: 't1' }),
    track({ id: 't2', closedAt: 5 }),
    track({ id: 't3', closedAt: 5 }),
    track({ id: 't4', closedAt: 5 }),
  ];
  const unread = (row: Track) => row.id === 't2';
  const shown = (rows: readonly Track[]) => rows.map((row) => row.id);

  it('railAreaTracks keeps open, unread and active tracks', () => {
    expect(shown(railAreaTracks(source, 't3', unread, false))).toEqual(['t1', 't2', 't3']);
    expect(shown(railAreaTracks(source, null, unread, false))).toEqual(['t1', 't2']);
    expect(shown(railAreaTracks(source, null, () => false, false))).toEqual(['t1']);
  });

  it('keeps every track, in order, when the Area shows closed ones', () => {
    expect(shown(railAreaTracks(source, null, () => false, true))).toEqual(['t1', 't2', 't3', 't4']);
  });

  it('leaves its input untouched', () => {
    const before = [...source];
    const rows = railAreaTracks(source, null, () => false, true);
    expect(source).toEqual(before);
    expect(rows).not.toBe(source);
  });
});

describe('activeTracksOn', () => {
  const day = new Date(2026, 7, 10, 12, 0, 0);
  const dayStart = new Date(2026, 7, 10, 0, 0, 0).getTime();
  const dayEnd = new Date(2026, 7, 10, 23, 59, 59, 999).getTime();
  const now = day.getTime();

  it('includes an open track created before the day and still running', () => {
    const open = track({ id: 'open', createdAt: dayStart - DAY, closedAt: null });
    expect(activeTracksOn([open], day, now).map((w) => w.id)).toEqual(['open']);
  });

  it('includes a track created in the last millisecond of the day', () => {
    const late = track({ id: 'late', createdAt: dayEnd, closedAt: null });
    expect(activeTracksOn([late], day, now).map((w) => w.id)).toEqual(['late']);
  });

  it('includes a track that ended exactly at the start of the day', () => {
    const edge = track({ id: 'edge', createdAt: dayStart - DAY, closedAt: dayStart });
    expect(activeTracksOn([edge], day, now).map((w) => w.id)).toEqual(['edge']);
  });

  it('excludes a track that ended before the day and one created after it', () => {
    const before = track({ id: 'before', createdAt: dayStart - 2 * DAY, closedAt: dayStart - 1 });
    const after = track({ id: 'after', createdAt: dayEnd + 1, closedAt: null });
    expect(activeTracksOn([before, after], day, now)).toEqual([]);
  });

  it('orders oldest first and breaks ties by id', () => {
    const b = track({ id: 'b', createdAt: dayStart + 10 });
    const a = track({ id: 'a', createdAt: dayStart + 10 });
    const older = track({ id: 'z', createdAt: dayStart + 1 });
    expect(activeTracksOn([b, a, older], day, now).map((w) => w.id)).toEqual(['z', 'a', 'b']);
  });

  it('does not mutate the input list', () => {
    const list = [track({ id: 'b', createdAt: dayStart + 2 }), track({ id: 'a', createdAt: dayStart + 1 })];
    activeTracksOn(list, day, now);
    expect(list.map((w) => w.id)).toEqual(['b', 'a']);
  });
});

describe('userVisibleTracks', () => {
  const userArea: Area = {
    id: 'c1', name: 'Work', color: '#123456', sort: 1, kind: 'user',
    defaultTemplateId: null, defaultCwd: null, createdAt: 0, updatedAt: 0,
  };
  const systemArea: Area = {
    id: 'sys', name: 'Kernel', color: '#000000', sort: 0, kind: 'system',
    defaultTemplateId: null, defaultCwd: null, createdAt: 0, updatedAt: 0,
  };
  const mine = track({ id: 'w1', areaId: 'c1' });
  const scaffolding = track({ id: 'w-sys', areaId: 'sys' });
  const closed = track({ id: 'w2', areaId: 'c1', closedAt: 1 });

  it('[E2E-INV-SHELL-003] drops tracks hosted by the system area', () => {
    expect(userVisibleTracks([mine, scaffolding], [userArea, systemArea]).map((w) => w.id))
      .toEqual(['w1']);
  });

  it('keeps closed tracks and drops tracks whose area is absent from the list', () => {
    expect(userVisibleTracks([mine, closed], [userArea]).map((w) => w.id)).toEqual(['w1', 'w2']);
    expect(userVisibleTracks([mine], [])).toEqual([]);
  });
});

/* The kernel trims on the Unicode `White_Space` property, which JS `trim()` does not match exactly. */
describe('isBlankForKernel', () => {
  it('is true for the empty string and for ordinary JS whitespace', () => {
    expect(isBlankForKernel('')).toBe(true);
    expect(isBlankForKernel('   ')).toBe(true);
    expect(isBlankForKernel('\t\n\r ')).toBe(true);
  });

  it('is true for U+00A0 NO-BREAK SPACE, which both sides call whitespace', () => {
    expect(isBlankForKernel('\u00A0')).toBe(true);
  });

  it('is true for U+0085 NEXT LINE, which JS trim() leaves standing', () => {
    expect(isBlankForKernel('\u0085')).toBe(true);
    expect('\u0085'.trim()).not.toBe('');
  });

  it('is false as soon as there is anything to say', () => {
    expect(isBlankForKernel('hi')).toBe(false);
    expect(isBlankForKernel('  keep indentation  ')).toBe(false);
    expect(isBlankForKernel('\u0085x\u0085')).toBe(false);
  });
});

describe('trackOverlayPayload', () => {
  const overlay = (over: Partial<OverlayWire>): OverlayWire => ({
    id: 'o1',
    plugin_id: 'dev-neige-binance',
    entity_kind: 'track',
    entity_id: 't1',
    kind: 'portfolio.holdings',
    payload: { columns: [], rows: [] },
    updated_at: 1,
    ...over,
  });
  const SOURCE = 'neige://plugin/dev-neige-binance/portfolio.holdings';

  it('returns the payload of the overlay the source addresses', () => {
    expect(trackOverlayPayload('t1', [overlay({})], SOURCE))
      .toEqual({ columns: [], rows: [] });
  });

  it('does not cross tracks, plugins, kinds or entity kinds', () => {
    // Each of these differs from the addressed overlay in exactly one field.
    const wrong = [
      overlay({ entity_id: 't2' }),
      overlay({ plugin_id: 'other-plugin' }),
      overlay({ kind: 'portfolio.history' }),
      overlay({ entity_kind: 'card' }),
    ];
    for (const row of wrong) {
      expect(trackOverlayPayload('t1', [row], SOURCE)).toBeUndefined();
    }
    expect(trackOverlayPayload('t1', [...wrong, overlay({})], SOURCE))
      .toEqual({ columns: [], rows: [] });
  });

  it('returns undefined for a source that is not a plugin overlay reference', () => {
    for (const source of [
      'neige://track/t1#b_x',
      'https://example.com/x',
      'neige://plugin/only-one',
      'neige://plugin/a/b/c',
      'neige://plugin//portfolio.holdings',
      'neige://plugin/dev-neige-binance/',
    ]) {
      expect(trackOverlayPayload('t1', [overlay({})], source)).toBeUndefined();
    }
  });
});

it.each([null, 'session-1'])('makes a v1 terminal consumable by the Track workbench (session=%s)', sessionId => {
  const card = terminalDtoToCardWire({ id: 'terminal-1', trackId: 'w1', sessionId, title: 'Shell', cwd: '/tmp', status: 'exited', createdAtMs: 1 });
  expect(terminalPresentation(card)).toEqual({ source: sessionId === null ? 'unrecorded' : 'agent', in_workbench: true });
});

describe('template report projection', () => {
  const template = {
    templateId: 'dev', templateTitle: 'Dev', templateInputs: { repository: 'perga' },
    templateHeaderView: { badges: [{ label: 'Ready', tone: 'success' }], rules: [{ label: 'Review', text: 'Check tests' }] },
  };
  const dto = () => trackDtoSchema.parse({ id: 'w1', areaId: 'c1', createdAtMs: 1, updatedAtMs: 2, ...template });

  it('preserves every template field through the production HTTP projection', () => {
    expect(toTrack(trackDtoToWire(dto()))).toMatchObject(template);
  });

  it('preserves every template field on track.updated events', () => {
    const event = decodeWireEvent({ eventId: 1, createdAtMs: 2, kind: 'track.updated', payload: dto() });
    expect(event).toMatchObject({ status: 'ready', value: { data: template } });
  });

  it('defaults omitted template fields for historical rows', () => {
    expect(toTrack(trackWireSchema.parse(baseWire))).toMatchObject({
      templateId: null, templateTitle: null, templateInputs: {}, templateHeaderView: null,
    });
  });
});

it('preserves an explicit local workspace target', () => {
  expect(trackWorkspaceOptionsOperation('area-1', '/tmp/x', undefined, 'local').path).toContain('targetId=local');
});
