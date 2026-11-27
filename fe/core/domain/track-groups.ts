// One authoritative track-grouping and Focus feed projection shared by desktop sidebar,
// mobile shortcuts, and the mobile Focus home feed.

import { areaOf, visibleAreas, type Area } from './area.js';
import {
  hasFailed, isClosed, isWorking, needsUserAttention, sortAreaTracksByRecent,
  trackDisplayTitle, userVisibleTracks, type Track,
} from './track.js';

export type TrackGroups = Readonly<Record<'focus' | 'waiting' | 'pinned' | 'unread' | 'running', readonly Track[]>>;

/** One visible membership projection for desktop groups, mobile shortcuts, and Focus home. */
export function workspaceTrackGroups(
  rows: readonly Track[],
  areas: readonly Area[],
  isUnread: (track: Track) => boolean,
): TrackGroups {
  const tracks = userVisibleTracks(rows, areas);
  const unpinned = tracks.filter((track) => track.pinnedAt === null);
  const failed = sortAreaTracksByRecent(unpinned.filter(hasFailed));
  const attention = sortAreaTracksByRecent(unpinned.filter((track) => !hasFailed(track) && needsUserAttention(track)));
  const runningOnly = sortAreaTracksByRecent(unpinned.filter((track) => !hasFailed(track) && !needsUserAttention(track) && isWorking(track)));
  const waitingFailed = sortAreaTracksByRecent(tracks.filter(hasFailed));
  const waitingInput = sortAreaTracksByRecent(tracks.filter((track) => !hasFailed(track) && needsUserAttention(track)));
  return {
    focus: [...failed, ...attention, ...runningOnly],
    waiting: [...waitingFailed, ...waitingInput],
    pinned: tracks.filter((track) => track.pinnedAt !== null).toSorted((left, right) => (right.pinnedAt ?? 0) - (left.pinnedAt ?? 0)),
    unread: sortAreaTracksByRecent(tracks.filter(isUnread)),
    running: sortAreaTracksByRecent(tracks.filter(isWorking)),
  };
}

export type FocusTab = 'focus' | 'unread' | 'attention' | 'pinned' | 'all';

export const FOCUS_TABS: readonly FocusTab[] = Object.freeze(['focus', 'unread', 'attention', 'pinned', 'all'] as const);

export type FocusFeedCounts = Readonly<{
  focus: number;
  unread: number;
  attention: number;
  pinned: number;
  all: number;
}>;

export type FocusFeedProjection = Readonly<{
  counts: FocusFeedCounts;
  attention: readonly Track[];
  unread: readonly Track[];
  /** Every unread track matching the current area/query scope (used by Mark all read). */
  allUnreadInScope: readonly Track[];
  pinned: readonly Track[];
  open: readonly Track[];
  closed: readonly Track[];
  totalShown: number;
  totalVisibleTracks: number;
  hasActiveScope: boolean;
}>;

function matchesFocusQuery(track: Track, areas: readonly Area[], needle: string): boolean {
  if (needle === '') return true;
  if (trackDisplayTitle(track.title).toLowerCase().includes(needle)) return true;
  const area = areaOf(track.areaId, areas);
  return area !== undefined && area.name.toLowerCase().includes(needle);
}

export function buildFocusFeed({
  tracks,
  areas,
  isUnread,
  tab,
  areaId,
  query,
}: Readonly<{
  tracks: readonly Track[];
  areas: readonly Area[];
  isUnread: (track: Track) => boolean;
  tab: FocusTab;
  areaId: string | null;
  query: string;
}>): FocusFeedProjection {
  const shownAreas = visibleAreas(areas);
  const allVisible = userVisibleTracks(tracks, shownAreas);
  const needle = query.trim().toLowerCase();
  const hasActiveScope = areaId !== null || needle !== '';

  const scoped = allVisible.filter((track) => {
    if (areaId !== null && track.areaId !== areaId) return false;
    return matchesFocusQuery(track, shownAreas, needle);
  });

  const groups = workspaceTrackGroups(scoped, shownAreas, isUnread);
  const openTracks = sortAreaTracksByRecent(scoped.filter((track) => !isClosed(track)));
  const closedTracks = needle === '' ? [] : sortAreaTracksByRecent(scoped.filter(isClosed));

  const waitingIds = new Set(groups.waiting.map((track) => track.id));
  const unreadNotWaiting = groups.unread.filter((track) => !waitingIds.has(track.id));
  const unreadIds = new Set(groups.unread.map((track) => track.id));
  const pinnedOnly = groups.pinned.filter((track) => !waitingIds.has(track.id) && !unreadIds.has(track.id));
  const prioritizedIds = new Set([
    ...waitingIds,
    ...unreadIds,
    ...pinnedOnly.map((track) => track.id),
  ]);
  const otherOpenMatches = needle === '' ? [] : openTracks.filter((track) => !prioritizedIds.has(track.id));
  const otherClosedMatches = needle === '' ? [] : closedTracks.filter((track) => !prioritizedIds.has(track.id));

  const focusUnionCount = groups.waiting.length + unreadNotWaiting.length + pinnedOnly.length
    + otherOpenMatches.length + otherClosedMatches.length;
  const counts: FocusFeedCounts = {
    focus: focusUnionCount,
    unread: groups.unread.length,
    attention: groups.waiting.length,
    pinned: groups.pinned.length,
    all: openTracks.length + closedTracks.length,
  };

  let attention: readonly Track[] = [];
  let unread: readonly Track[] = [];
  let pinned: readonly Track[] = [];
  let open: readonly Track[] = [];
  let closed: readonly Track[] = [];

  switch (tab) {
    case 'focus':
      attention = groups.waiting;
      unread = unreadNotWaiting;
      pinned = pinnedOnly;
      // Searching from the default Focus tab still prioritizes Attention/Unread/Pinned
      // while surfacing matching quiet open and closed tracks below them.
      open = otherOpenMatches;
      closed = otherClosedMatches;
      break;
    case 'unread':
      unread = groups.unread;
      break;
    case 'attention':
      attention = groups.waiting;
      break;
    case 'pinned':
      pinned = groups.pinned;
      break;
    case 'all':
      open = openTracks;
      closed = closedTracks;
      break;
  }

  return {
    counts,
    attention,
    unread,
    allUnreadInScope: groups.unread,
    pinned,
    open,
    closed,
    totalShown: attention.length + unread.length + pinned.length + open.length + closed.length,
    totalVisibleTracks: allVisible.length,
    hasActiveScope,
  };
}
