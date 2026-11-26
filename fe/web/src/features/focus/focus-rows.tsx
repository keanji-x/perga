import type { ReactElement, ReactNode } from 'react';

import { areaOf, type Area } from '../../../../core/domain/area.ts';
import { formatRelativeTimestamp, type RelativeTimeFormatters } from '../../../../core/domain/relative-time.ts';
import { trackStatusTag, type FocusFeedProjection, type FocusTab } from '../../../../core/domain/track-groups.ts';
import {
  hasFailed, isWorking, trackDisplayTitle, trackRecentAt, type Track,
} from '../../../../core/domain/track.ts';
import { Icon } from '../../ui/icon/public.tsx';
import styles from './focus-home.module.css';

export function highlightMatch(text: string, query: string): ReactNode {
  const needle = query.trim();
  if (needle === '') return text;
  const lower = text.toLowerCase();
  const needleLower = needle.toLowerCase();
  const parts: ReactNode[] = [];
  let cursor = 0;
  let matchIndex = lower.indexOf(needleLower, cursor);
  if (matchIndex === -1) return text;
  while (matchIndex !== -1) {
    if (matchIndex > cursor) parts.push(text.slice(cursor, matchIndex));
    parts.push(
      <mark key={matchIndex} className={styles.matchMark}>
        {text.slice(matchIndex, matchIndex + needle.length)}
      </mark>,
    );
    cursor = matchIndex + needle.length;
    matchIndex = lower.indexOf(needleLower, cursor);
  }
  if (cursor < text.length) parts.push(text.slice(cursor));
  return parts;
}

export function FocusAttentionCard({
  track,
  areas,
  query,
  now,
  formatters,
  onOpenTrack,
}: Readonly<{
  track: Track;
  areas: readonly Area[];
  query: string;
  now: number;
  formatters: RelativeTimeFormatters;
  onOpenTrack: (trackId: string) => void;
}>) {
  const area = areaOf(track.areaId, areas);
  const failed = hasFailed(track);
  const time = formatRelativeTimestamp(trackRecentAt(track), now, formatters);
  return (
    <li>
      <button
        type="button"
        className={`${styles.attentionCard} ${failed ? styles.attentionCardFailed : ''}`}
        onClick={() => onOpenTrack(track.id)}
      >
        <span className={styles.feedTopRow}>
          <span className={styles.feedTitle}>{highlightMatch(trackDisplayTitle(track.title), query)}</span>
          {time !== '' && <span className={styles.feedTime}>{time}</span>}
        </span>
        <span className={styles.feedMetaRow}>
          <span className={styles.feedTags}>
            <span className={`${styles.tag} ${failed ? styles.tagError : styles.tagWarn}`}>
              {failed ? 'Needs repair' : 'Waiting on you'}
            </span>
            {area !== undefined && <span className={styles.tag}>{highlightMatch(area.name, query)}</span>}
          </span>
          <span className={styles.replyCue}>
            <span>{failed ? 'Inspect' : 'Reply'}</span>
            <Icon name="chevron-right" size="sm" />
          </span>
        </span>
      </button>
    </li>
  );
}

export function FocusTrackRow({
  track,
  mode,
  areas,
  query,
  now,
  unread,
  formatters,
  onOpenTrack,
}: Readonly<{
  track: Track;
  mode: 'unread' | 'pinned' | 'open' | 'closed';
  areas: readonly Area[];
  query: string;
  now: number;
  unread: boolean;
  formatters: RelativeTimeFormatters;
  onOpenTrack: (trackId: string) => void;
}>) {
  const area = areaOf(track.areaId, areas);
  const title = trackDisplayTitle(track.title);
  const time = formatRelativeTimestamp(trackRecentAt(track), now, formatters);
  const working = isWorking(track);
  const status = trackStatusTag(track, mode === 'unread' || unread);

  return (
    <li className={styles.groupItem}>
      <button type="button" className={styles.feedRow} onClick={() => onOpenTrack(track.id)}>
        <span className={styles.feedGutter} aria-hidden="true">
          {mode === 'pinned' ? (
            <Icon name="pin" size="sm" />
          ) : mode === 'unread' || unread ? (
            <span className={styles.unreadDot} />
          ) : working ? (
            <span className={styles.workingDot} />
          ) : (
            <Icon name="file" size="sm" />
          )}
        </span>
        <span className={styles.feedBody}>
          <span className={styles.feedTopRow}>
            <span className={`${styles.feedTitle} ${mode === 'pinned' || mode === 'closed' ? styles.feedTitleCalm : ''}`}>
              {highlightMatch(title, query)}
            </span>
            {time !== '' && <span className={styles.feedTime}>{time}</span>}
          </span>
          <span className={styles.feedTags}>
            {area !== undefined && <span className={styles.tag}>{highlightMatch(area.name, query)}</span>}
            {status !== null && <span className={styles.metaSubtle}>· {status.text}</span>}
          </span>
        </span>
      </button>
    </li>
  );
}

export type FocusEmptyCopy = Readonly<{
  icon: ReactElement;
  title: string;
  description: string;
  action?: Readonly<{ label: string; onClick: () => void }>;
}>;

export function focusEmptyCopy({
  feed,
  tab,
  tabName,
  activeArea,
  onSelectTab,
  onResetFilters,
  onNewTrack,
}: Readonly<{
  feed: FocusFeedProjection;
  tab: FocusTab;
  tabName: string;
  activeArea: Area | undefined;
  onSelectTab: (next: FocusTab) => void;
  onResetFilters: () => void;
  onNewTrack?: () => void;
}>): FocusEmptyCopy {
  if (feed.hasActiveScope) {
    if (tab !== 'all' && feed.counts.all > 0) {
      const matchWord = feed.counts.all === 1 ? 'match' : 'matches';
      return {
        icon: <Icon name="search" />,
        title: `No matching tracks in ${tabName}`,
        description: activeArea !== undefined
          ? `${feed.counts.all} ${matchWord} available in ${activeArea.name} across all tracks.`
          : `${feed.counts.all} ${matchWord} available across all tracks.`,
        action: { label: `Show all ${feed.counts.all} ${matchWord}`, onClick: () => onSelectTab('all') },
      };
    }
    return {
      icon: <Icon name="search" />,
      title: 'No matching tracks',
      description: activeArea !== undefined
        ? `No tracks matched in ${activeArea.name}.`
        : 'Try a different keyword or clear active filters.',
      action: { label: 'Reset filters', onClick: onResetFilters },
    };
  }
  if (feed.totalVisibleTracks === 0) {
    return {
      icon: <Icon name="folder" />,
      title: 'No tracks yet',
      description: 'Start a track to organize your work and updates here.',
      action: onNewTrack === undefined ? undefined : { label: 'New track', onClick: onNewTrack },
    };
  }
  switch (tab) {
    case 'focus':
      return {
        icon: <Icon name="check" />,
        title: 'All caught up',
        description: 'No tracks need attention or have unread updates right now.',
        action: { label: 'View all tracks', onClick: () => onSelectTab('all') },
      };
    case 'unread':
      return {
        icon: <Icon name="check" />,
        title: 'No unread updates',
        description: 'New replies and updates from your tracks appear here.',
        action: { label: 'View all tracks', onClick: () => onSelectTab('all') },
      };
    case 'attention':
      return {
        icon: <Icon name="check" />,
        title: 'Nothing waiting on you',
        description: 'Pending asks, approvals, and blocked runs appear here.',
        action: { label: 'Back to Focus', onClick: () => onSelectTab('focus') },
      };
    case 'pinned':
      return {
        icon: <Icon name="pin" />,
        title: 'No pinned tracks',
        description: 'Pin important tracks to keep them one tap away.',
        action: { label: 'View all tracks', onClick: () => onSelectTab('all') },
      };
    case 'all':
      return {
        icon: <Icon name="file" />,
        title: 'No open tracks',
        description: 'All visible tracks are currently closed.',
        action: onNewTrack === undefined ? undefined : { label: 'New track', onClick: onNewTrack },
      };
  }
}
