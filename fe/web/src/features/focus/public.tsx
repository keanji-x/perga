import { useEffect, useLayoutEffect, useMemo, useRef, type ReactNode, type Ref } from 'react';

import { areaOf, visibleAreas, type Area } from '../../../../core/domain/area.ts';
import {
  buildFocusFeed, FOCUS_TABS, type FocusTab,
} from '../../../../core/domain/track-groups.ts';
import {
  hasFailed, isClosed, isWorking, trackDisplayTitle, trackRecentAt, type Track,
} from '../../../../core/domain/track.ts';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { LoadingStatus } from '../../ui/loading-status/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { createFocusTimeFormatters, formatFocusTimestamp } from './format-time.ts';
import styles from './focus-home.module.css';

function tabLabel(tab: FocusTab): string {
  switch (tab) {
    case 'focus': return 'Focus';
    case 'unread': return 'Unread';
    case 'attention': return 'Attention';
    case 'pinned': return 'Pinned';
    case 'all': return 'All';
  }
}

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

export type MobileFocusHomeProps = Readonly<{
  areas: readonly Area[];
  tracks: readonly Track[];
  isUnread: (track: Track) => boolean;
  loading: boolean;
  error: string | null;
  onRetry: () => void;
  onOpenWorkspace: () => void;
  workspaceTriggerRef?: Ref<HTMLButtonElement>;
  onOpenTrack: (trackId: string) => void;
  onNewTrack?: (areaId?: string) => void;
  onMarkAllRead?: (tracks: readonly Track[]) => void;
  nowMs?: number;
}>;

/** Full-bleed mobile Focus home feed: prioritized Attention -> Unread -> Pinned with inline search and filters. */
export function MobileFocusHome({
  areas,
  tracks,
  isUnread,
  loading,
  error,
  onRetry,
  onOpenWorkspace,
  workspaceTriggerRef,
  onOpenTrack,
  onNewTrack,
  onMarkAllRead,
  nowMs,
}: MobileFocusHomeProps) {
  const shownAreas = useMemo(() => visibleAreas(areas), [areas]);
  const [tab, setTab] = useState<FocusTab>('focus');
  const [searchOpen, setSearchOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [selectedAreaId, setSelectedAreaId] = useState<string | null>(null);

  const pageRef = useRef<HTMLDivElement | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const searchTriggerRef = useRef<HTMLButtonElement | null>(null);
  const pendingSearchReturnRef = useRef(false);
  const timeFormatters = useMemo(
    () => createFocusTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
    [],
  );
  const effectiveAreaId = selectedAreaId !== null && shownAreas.some((area) => area.id === selectedAreaId)
    ? selectedAreaId
    : null;

  useEffect(() => {
    if (searchOpen) searchInputRef.current?.focus({ preventScroll: true });
  }, [searchOpen]);

  useEffect(() => {
    if (!searchOpen) return;
    const container = pageRef.current;
    if (container === null) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      if (event.isComposing || event.keyCode === 229) return;
      event.preventDefault();
      pendingSearchReturnRef.current = true;
      setQuery('');
      setSearchOpen(false);
    };
    container.addEventListener('keydown', onKeyDown);
    return () => container.removeEventListener('keydown', onKeyDown);
  }, [searchOpen]);

  useLayoutEffect(() => {
    if (!searchOpen && pendingSearchReturnRef.current) {
      pendingSearchReturnRef.current = false;
      searchTriggerRef.current?.focus({ preventScroll: true });
    }
  }, [searchOpen]);

  const feed = useMemo(() => buildFocusFeed({
    tracks,
    areas: shownAreas,
    isUnread,
    tab,
    areaId: effectiveAreaId,
    query,
  }), [tracks, shownAreas, isUnread, tab, effectiveAreaId, query]);

  const now = nowMs ?? Date.now();
  const activeArea = effectiveAreaId === null ? undefined : areaOf(effectiveAreaId, shownAreas);

  const closeSearch = () => {
    pendingSearchReturnRef.current = true;
    setQuery('');
    setSearchOpen(false);
  };

  const resetFilters = () => {
    setQuery('');
    setSelectedAreaId(null);
    setTab('focus');
  };

  const renderTrackRow = (track: Track, mode: 'unread' | 'pinned' | 'open' | 'closed') => {
    const area = areaOf(track.areaId, shownAreas);
    const title = trackDisplayTitle(track.title);
    const time = formatFocusTimestamp(trackRecentAt(track), now, timeFormatters);
    const unread = isUnread(track);
    const working = isWorking(track);
    const failed = hasFailed(track);
    const subtitle = failed
      ? 'Needs repair'
      : track.attention === 'input'
        ? 'Waiting on you'
        : working
          ? 'Running'
          : isClosed(track)
            ? 'Closed'
            : mode === 'unread' || unread
              ? 'Unread update'
              : null;

    return (
      <li key={track.id} className={styles.groupItem}>
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
              {subtitle !== null && <span className={styles.metaSubtle}>· {subtitle}</span>}
            </span>
          </span>
        </button>
      </li>
    );
  };

  const emptyCopy = (() => {
    if (feed.hasActiveScope) {
      if (tab !== 'all' && feed.counts.all > 0) {
        const matchWord = feed.counts.all === 1 ? 'match' : 'matches';
        return {
          icon: <Icon name="search" />,
          title: `No matching tracks in ${tabLabel(tab)}`,
          description: activeArea !== undefined
            ? `${feed.counts.all} ${matchWord} available in ${activeArea.name} across all tracks.`
            : `${feed.counts.all} ${matchWord} available across all tracks.`,
          action: { label: `Show all ${feed.counts.all} ${matchWord}`, onClick: () => setTab('all') },
        };
      }
      return {
        icon: <Icon name="search" />,
        title: 'No matching tracks',
        description: activeArea !== undefined
          ? `No tracks matched in ${activeArea.name}.`
          : 'Try a different keyword or clear active filters.',
        action: { label: 'Reset filters', onClick: resetFilters },
      };
    }
    if (feed.totalVisibleTracks === 0) {
      return {
        icon: <Icon name="folder" />,
        title: 'No tracks yet',
        description: 'Start a track to organize your work and updates here.',
        action: onNewTrack === undefined ? undefined : { label: 'New track', onClick: () => onNewTrack() },
      };
    }
    switch (tab) {
      case 'focus':
        return {
          icon: <Icon name="check" />,
          title: 'All caught up',
          description: 'No tracks need attention or have unread updates right now.',
          action: { label: 'View all tracks', onClick: () => setTab('all') },
        };
      case 'unread':
        return {
          icon: <Icon name="check" />,
          title: 'No unread updates',
          description: 'New replies and updates from your tracks appear here.',
          action: { label: 'View all tracks', onClick: () => setTab('all') },
        };
      case 'attention':
        return {
          icon: <Icon name="check" />,
          title: 'Nothing waiting on you',
          description: 'Pending asks, approvals, and blocked runs appear here.',
          action: { label: 'Back to Focus', onClick: () => setTab('focus') },
        };
      case 'pinned':
        return {
          icon: <Icon name="pin" />,
          title: 'No pinned tracks',
          description: 'Pin important tracks to keep them one tap away.',
          action: { label: 'View all tracks', onClick: () => setTab('all') },
        };
      case 'all':
        return {
          icon: <Icon name="file" />,
          title: 'No open tracks',
          description: 'All visible tracks are currently closed.',
          action: onNewTrack === undefined ? undefined : { label: 'New track', onClick: () => onNewTrack() },
        };
    }
  })();

  const showAreaFilter = shownAreas.length > 1 && (searchOpen || effectiveAreaId !== null);
  const openSectionTitle = tab === 'focus' ? 'Other matches' : 'Open tracks';

  return (
    <div ref={pageRef} className={styles.page} data-nc-mobile-focus="">
      {searchOpen ? (
        <header className={styles.searchHeader} data-nc-mobile-header="">
          <div className={styles.searchBox}>
            <span className={styles.searchGlyph} aria-hidden="true">
              <Icon name="search" size="sm" />
            </span>
            <input
              ref={searchInputRef}
              type="search"
              className={styles.searchInput}
              aria-label="Search tracks"
              placeholder="Search tracks, areas…"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
            {query !== '' && (
              <button
                type="button"
                className={styles.searchClear}
                aria-label="Clear search"
                onClick={() => {
                  setQuery('');
                  searchInputRef.current?.focus({ preventScroll: true });
                }}
              >
                <Icon name="close" size="sm" />
              </button>
            )}
          </div>
          <button type="button" className={styles.searchCancel} onClick={closeSearch}>
            Cancel
          </button>
        </header>
      ) : (
        <header className={styles.header} data-nc-mobile-header="">
          <div className={styles.headerLeading}>
            <button
              ref={workspaceTriggerRef}
              type="button"
              className={styles.iconButton}
              aria-label="Open workspace"
              onClick={onOpenWorkspace}
            >
              <Icon name="menu" />
            </button>
          </div>
          <div className={styles.headerCenter}>
            <h1 className={styles.headerTitle}>Focus</h1>
          </div>
          <div className={styles.headerActions}>
            <button
              ref={searchTriggerRef}
              type="button"
              className={styles.iconButton}
              aria-label="Search tracks"
              onClick={() => setSearchOpen(true)}
            >
              <Icon name="search" />
            </button>
            {onNewTrack !== undefined && (
              <button
                type="button"
                className={`${styles.iconButton} ${styles.iconButtonAccent}`}
                aria-label="New track"
                onClick={() => onNewTrack(effectiveAreaId ?? undefined)}
              >
                <Icon name="plus" />
              </button>
            )}
          </div>
        </header>
      )}

      <nav className={styles.filterBar} aria-label="Focus filters">
        {showAreaFilter && (
          <div className={`${styles.areaSelectWrap} ${effectiveAreaId !== null ? styles.areaSelectWrapActive : ''}`}>
            <select
              aria-label="Filter by area"
              className={`${styles.areaSelect} ${effectiveAreaId !== null ? styles.areaSelectActive : ''}`}
              value={effectiveAreaId ?? ''}
              onChange={(event) => setSelectedAreaId(event.target.value === '' ? null : event.target.value)}
            >
              <option value="">All areas</option>
              {shownAreas.map((area) => (
                <option key={area.id} value={area.id}>{area.name}</option>
              ))}
            </select>
            <span className={styles.areaSelectChevron} aria-hidden="true">
              <Icon name="chevron-down" size="sm" />
            </span>
          </div>
        )}
        {FOCUS_TABS.map((candidate) => {
          const active = tab === candidate;
          const count = candidate === 'focus' || candidate === 'unread' || candidate === 'attention'
            ? feed.counts[candidate]
            : candidate === 'all' && feed.hasActiveScope
              ? feed.counts.all
              : 0;
          return (
            <button
              key={candidate}
              type="button"
              className={`${styles.filterPill} ${active ? styles.filterPillActive : ''}`}
              aria-pressed={active}
              onClick={() => setTab(candidate)}
            >
              <span>{tabLabel(candidate)}</span>
              {count > 0 && <span className={styles.filterCount}>{count}</span>}
            </button>
          );
        })}
      </nav>

      <div className={styles.feedScroll} data-nc-focus-feed="">
        {error !== null && <ErrorBox message={error} onRetry={onRetry} />}
        {loading && feed.totalShown === 0 && <LoadingStatus padded>Loading workspace…</LoadingStatus>}

        {feed.hasActiveScope && (
          <div className={styles.searchSummary}>
            <span>
              Found <span className={styles.searchSummaryCount}>{feed.totalShown}</span>{' '}
              {feed.totalShown === 1 ? 'track' : 'tracks'}
              {activeArea !== undefined ? ` in ${activeArea.name}` : ''}
            </span>
            <button type="button" className={styles.sectionAction} onClick={resetFilters}>
              Reset
            </button>
          </div>
        )}

        {feed.attention.length > 0 && (
          <section className={styles.section} aria-label="Needs attention">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Needs attention</h2>
              <span className={styles.sectionMeta}>{feed.attention.length} waiting</span>
            </div>
            <ul className={styles.attentionList}>
              {feed.attention.map((track) => {
                const area = areaOf(track.areaId, shownAreas);
                const failed = hasFailed(track);
                const time = formatFocusTimestamp(trackRecentAt(track), now, timeFormatters);
                return (
                  <li key={track.id}>
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
              })}
            </ul>
          </section>
        )}

        {feed.unread.length > 0 && (
          <section className={styles.section} aria-label="Unread updates">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Unread updates</h2>
              {onMarkAllRead !== undefined && feed.allUnreadInScope.length > 0 && (
                <button
                  type="button"
                  className={styles.sectionAction}
                  onClick={() => onMarkAllRead(feed.allUnreadInScope)}
                >
                  Mark all read
                </button>
              )}
            </div>
            <ul className={styles.groupCard}>
              {feed.unread.map((track) => renderTrackRow(track, 'unread'))}
            </ul>
          </section>
        )}

        {feed.pinned.length > 0 && (
          <section className={styles.section} aria-label="Pinned">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Pinned</h2>
              <span className={styles.sectionMeta}>{feed.pinned.length}</span>
            </div>
            <ul className={styles.groupCard}>
              {feed.pinned.map((track) => renderTrackRow(track, 'pinned'))}
            </ul>
          </section>
        )}

        {feed.open.length > 0 && (
          <section className={styles.section} aria-label={openSectionTitle}>
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>{openSectionTitle}</h2>
              <span className={styles.sectionMeta}>{feed.open.length}</span>
            </div>
            <ul className={styles.groupCard}>
              {feed.open.map((track) => renderTrackRow(track, 'open'))}
            </ul>
          </section>
        )}

        {feed.closed.length > 0 && (
          <section className={styles.section} aria-label="Closed tracks">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Closed tracks</h2>
              <span className={styles.sectionMeta}>{feed.closed.length}</span>
            </div>
            <ul className={styles.groupCard}>
              {feed.closed.map((track) => renderTrackRow(track, 'closed'))}
            </ul>
          </section>
        )}

        {!loading && error === null && feed.totalShown === 0 && (
          <div className={styles.emptyWrap}>
            <EmptyState icon={emptyCopy.icon} title={emptyCopy.title} description={emptyCopy.description} />
            {emptyCopy.action !== undefined && (
              <button type="button" className={styles.emptyAction} onClick={emptyCopy.action.onClick}>
                {emptyCopy.action.label}
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
