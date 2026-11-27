import { describe, expect, it } from 'vitest';

import type { Area } from './area.js';
import { NEUTRAL_ACTIVITY, type Track } from './track.js';
import { buildFocusFeed, workspaceTrackGroups } from './track-groups.js';

function makeArea(id: string, name: string, overrides: Partial<Area> = {}): Area {
  return {
    id,
    name,
    color: '#5B8DEF',
    sort: 1,
    kind: 'user',
    defaultTemplateId: null,
    defaultCwd: null,
    executionTargetId: 'local',
    createdAt: 1,
    updatedAt: 1,
    ...overrides,
  };
}

function makeTrack(id: string, areaId: string, title: string, overrides: Partial<Track> = {}): Track {
  return {
    id,
    areaId,
    title,
    sort: 1,
    cwd: '/work',
    agentCwd: '/work',
    isolation: 'none',
    pinnedAt: null,
    closedAt: null,
    createdAt: 100,
    updatedAt: 100,
    ...NEUTRAL_ACTIVITY,
    ...overrides,
  };
}

describe('workspaceTrackGroups & buildFocusFeed', () => {
  const areas: readonly Area[] = [
    makeArea('a-prod', 'Product'),
    makeArea('a-back', 'Backend'),
    makeArea('a-sys', 'System', { kind: 'system' }),
  ];

  const tracks: readonly Track[] = [
    makeTrack('t-failed', 'a-prod', 'Broken workspace', {
      attention: 'failed', updatedAt: 400, pinnedAt: 500,
    }),
    makeTrack('t-ask', 'a-prod', 'Mobile layout review', {
      attention: 'input',
      updatedAt: 500,
      attentionItems: [{
        source: 'ask',
        key: 'ask:1',
        text: 'Confirm Focus home layout',
        atMs: 500,
        askId: '1',
        questions: [],
        delivery: 'wake',
      }],
    }),
    makeTrack('t-unread', 'a-back', 'SQLite WAL guard', { updatedAt: 450 }),
    makeTrack('t-pinned', 'a-prod', 'Q4 Roadmap', { pinnedAt: 300, updatedAt: 200 }),
    makeTrack('t-quiet', 'a-back', 'Routine cleanup', { updatedAt: 150 }),
    makeTrack('t-closed', 'a-prod', 'Old mobile shell notes', { closedAt: 120, updatedAt: 120 }),
    makeTrack('t-daily', 'a-sys', '2026-11-26', { attention: 'input', updatedAt: 999 }),
  ];

  const unreadIds = new Set(['t-ask', 't-unread', 't-daily']);
  const isUnread = (track: Track) => unreadIds.has(track.id);

  it('excludes system-area tracks and orders failed before input in waiting', () => {
    const groups = workspaceTrackGroups(tracks, areas, isUnread);
    expect(groups.waiting.map((t) => t.id)).toEqual(['t-failed', 't-ask']);
    expect(groups.unread.map((t) => t.id)).toEqual(['t-ask', 't-unread']);
    expect(groups.pinned.map((t) => t.id)).toEqual(['t-failed', 't-pinned']);
  });

  it('preserves a deduplicated Focus stream and all unread tracks for Mark all read', () => {
    const feed = buildFocusFeed({
      tracks, areas, isUnread, tab: 'focus', areaId: null, query: '',
    });

    const rows = [...feed.attention, ...feed.unread, ...feed.pinned, ...feed.open, ...feed.closed];
    expect(rows.map((t) => t.id)).toEqual(['t-failed', 't-ask', 't-unread', 't-pinned']);
    expect(feed.allUnreadInScope.map((t) => t.id)).toEqual(['t-ask', 't-unread']);
    expect(feed.counts).toEqual({
      focus: 4,
      unread: 2,
      attention: 2,
      pinned: 2,
      all: 5,
    });
  });

  it('filters by area and query across title, area name, quiet open, and closed tracks from Focus and All tabs', () => {
    const byQueryOnAll = buildFocusFeed({
      tracks, areas, isUnread, tab: 'all', areaId: 'a-prod', query: 'mobile',
    });
    expect(byQueryOnAll.open.map((t) => t.id)).toEqual(['t-ask']);
    expect(byQueryOnAll.closed.map((t) => t.id)).toEqual(['t-closed']);
    expect(byQueryOnAll.counts.all).toBe(2);

    // Searching from the default Focus tab still prioritizes Attention/Unread/Pinned
    // and surfaces matching quiet open & closed tracks underneath.
    const byQuietQueryOnFocus = buildFocusFeed({
      tracks, areas, isUnread, tab: 'focus', areaId: null, query: 'cleanup',
    });
    expect(byQuietQueryOnFocus.open.map((t) => t.id)).toEqual(['t-quiet']);
    expect(byQuietQueryOnFocus.counts.focus).toBe(1);
    expect(byQuietQueryOnFocus.totalShown).toBe(1);

    const byClosedQueryOnFocus = buildFocusFeed({
      tracks, areas, isUnread, tab: 'focus', areaId: null, query: 'old mobile',
    });
    expect(byClosedQueryOnFocus.closed.map((t) => t.id)).toEqual(['t-closed']);
    expect(byClosedQueryOnFocus.counts.focus).toBe(1);
    expect(byClosedQueryOnFocus.totalShown).toBe(1);

    const byAreaName = buildFocusFeed({
      tracks, areas, isUnread, tab: 'all', areaId: null, query: 'backend',
    });
    expect(byAreaName.open.map((t) => t.id)).toEqual(['t-unread', 't-quiet']);
  });

  it('shows every unread track on the Unread tab even if it also needs attention', () => {
    const feed = buildFocusFeed({
      tracks, areas, isUnread, tab: 'unread', areaId: null, query: '',
    });
    expect(feed.unread.map((t) => t.id)).toEqual(['t-ask', 't-unread']);
    expect(feed.attention).toEqual([]);
  });
});
