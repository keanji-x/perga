import type { ReactElement, ReactNode } from 'react';

import { notificationPlainText, type ActivityState } from '../../../../core/domain/activity.ts';
import type { Area } from '../../../../core/domain/area.ts';
import { formatRelativeTimestamp, type RelativeTimeFormatters } from '../../../../core/domain/relative-time.ts';
import type { FocusFeedProjection, FocusTab } from '../../../../core/domain/track-groups.ts';
import {
  isClosed, trackActivityState, trackDisplayTitle, trackRecentAt, type Track, type TrackPreviewKind,
} from '../../../../core/domain/track.ts';
import { Icon } from '../../ui/icon/public.tsx';
import styles from './focus-home.module.css';

function highlightMatch(text: string, query: string): ReactNode {
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

type RowLabel = Readonly<{ text: string; tone?: string; pill?: true }>;

function activityLabel(track: Track, activity: ActivityState): RowLabel | null {
  switch (activity) {
    case 'failed': return contentLabel('error');
    case 'attention': return contentLabel('ask');
    case 'working': return { text: 'Running', tone: styles.statusWorking };
    case 'unread': return isClosed(track) ? { text: 'Closed' } : null;
    case 'quiet':
      if (isClosed(track)) return { text: 'Closed' };
      return track.pinnedAt !== null ? { text: 'Pinned' } : null;
  }
}

function contentLabel(kind: TrackPreviewKind | null): RowLabel | null {
  switch (kind) {
    case 'ask': return { text: 'Waiting on you', tone: styles.statusWarn };
    case 'error': return { text: 'Needs repair', tone: styles.statusError, pill: true };
    case 'reply': return { text: 'Reply', tone: styles.statusReply };
    case 'summary': return { text: 'Summary' };
    case null: return null;
  }
}

/** The mobile feed row used in Focus and Workspace track lists. */
export function MobileTrackFeedRow({
  track,
  areaName,
  query = '',
  now,
  unread,
  formatters,
  onOpenTrack,
  onOpenArea,
  timestampMs = trackRecentAt(track),
  current = false,
  surface = 'edge',
  trailingAction,
  renderTitle,
}: Readonly<{
  track: Track;
  areaName?: string;
  query?: string;
  now: number;
  unread: boolean;
  formatters: RelativeTimeFormatters;
  onOpenTrack: (trackId: string) => void;
  onOpenArea?: (areaId: string) => void;
  timestampMs?: number;
  current?: boolean;
  surface?: 'edge' | 'card';
  trailingAction?: ReactNode;
  renderTitle?: (track: Track, content: ReactNode) => ReactNode;
}>) {
  const title = highlightMatch(trackDisplayTitle(track.title), query);
  const time = formatRelativeTimestamp(timestampMs, now, formatters);
  const closed = isClosed(track);
  const activity = trackActivityState(track, unread);
  const preview = track.previewText === null ? '' : notificationPlainText(track.previewText).trim();
  const status = activityLabel(track, activity);
  const content = preview === '' ? null : contentLabel(track.previewKind);
  const prefix = content ?? status;
  const failed = activity === 'failed';
  const showUnreadUpdate = prefix === null && activity === 'unread' && preview === '';
  const announceUnread = unread && !showUnreadUpdate;
  const itemClass = [
    styles.feedItem,
    surface === 'card' ? styles.feedItemCard : '',
    current ? styles.feedItemCurrent : '',
    failed ? styles.feedItemFailed : '',
  ].join(' ');

  const area = areaName === undefined ? null : <span className={styles.feedArea}>{highlightMatch(areaName, query)}</span>;
  const hasAreaAction = areaName !== undefined && onOpenArea !== undefined;
  const hasControls = hasAreaAction || trailingAction != null;
  const timeLabel = time === '' ? null : <span className={styles.feedTime}>{areaName === undefined ? time : `· ${time}`}</span>;

  return (
    <li className={itemClass}>
      <button
        type="button"
        className={styles.feedRow}
        aria-current={current ? 'page' : undefined}
        onClick={() => onOpenTrack(track.id)}
      >
        <span
          className={styles.feedTitle}
          data-nc-active={current || unread || (!closed && activity !== 'quiet') || undefined}
        >
          {renderTitle === undefined ? title : renderTitle(track, title)}
        </span>
        {' '}
        <span className={styles.feedPreview}>
          {status !== null && content !== null && status.text !== content.text && (
            <><span className={status.tone ?? styles.statusMuted}>{status.text}</span>{status.pill || content.pill ? ' ' : ' · '}</>
          )}
          {prefix !== null && (
            <span className={prefix.tone ?? styles.statusMuted}>{prefix.text}{preview !== '' && !prefix.pill ? ': ' : ''}</span>
          )}
          {prefix?.pill && preview !== '' && ' '}
          {preview || (prefix === null && (
            <span className={showUnreadUpdate ? styles.statusReply : styles.statusMuted}>
              {showUnreadUpdate ? 'Unread update' : 'No updates yet'}
            </span>
          ))}
        </span>
        {' '}
        {!hasControls && (area !== null || timeLabel !== null) && (
          <span className={styles.feedMeta}>{area}{' '}{timeLabel}</span>
        )}
        {' '}
        {closed && status?.text !== 'Closed' && <span className={styles.srOnly}>Closed</span>}
        {announceUnread && <span className={styles.srOnly}>Unread</span>}
      </button>
      {hasControls && (
        <div className={`${styles.feedMeta} ${styles.feedControls}`}>
          {hasAreaAction ? (
            <button type="button" className={styles.feedAreaButton} aria-label={`Open area ${areaName}`}
              onClick={(event) => {
                event.currentTarget.focus({ preventScroll: true });
                onOpenArea(track.areaId);
              }}>
              <span className={styles.feedAreaLabel}>{area}<Icon name="chevron-right" size="sm" /></span>
            </button>
          ) : area}
          {timeLabel}
          {trailingAction}
        </div>
      )}
    </li>
  );
}

type FocusEmptyCopy = Readonly<{
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
