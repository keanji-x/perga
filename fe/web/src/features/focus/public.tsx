import { useEffect, useLayoutEffect, useMemo, useRef, type Ref } from 'react';

import { areaOf, visibleAreas, type Area } from '../../../../core/domain/area.ts';
import { createRelativeTimeFormatters } from '../../../../core/domain/relative-time.ts';
import { buildFocusFeed, FOCUS_TABS, type FocusTab } from '../../../../core/domain/track-groups.ts';
import type { Track } from '../../../../core/domain/track.ts';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import { LoadingStatus } from '../../ui/loading-status/public.tsx';
import { useState } from '../../ui/state/public.ts';
import styles from './focus-home.module.css';
import { focusEmptyCopy, FocusTrackRow } from './focus-rows.tsx';

function tabLabel(tab: FocusTab): string {
  switch (tab) {
    case 'focus': return 'Focus';
    case 'unread': return 'Unread';
    case 'attention': return 'Attention';
    case 'pinned': return 'Pinned';
    case 'all': return 'All';
  }
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

/** Full-bleed mobile Focus home feed with inline search and filters. */
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
    () => createRelativeTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
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

  const emptyCopy = focusEmptyCopy({
    feed,
    tab,
    tabName: tabLabel(tab),
    activeArea,
    onSelectTab: setTab,
    onResetFilters: resetFilters,
    onNewTrack: onNewTrack === undefined ? undefined : () => onNewTrack(),
  });

  const showAreaFilter = shownAreas.length > 1 && (searchOpen || effectiveAreaId !== null);
  const rows = [...feed.attention, ...feed.unread, ...feed.pinned, ...feed.open, ...feed.closed];

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
        {error !== null && (
          <div className={styles.feedNotice}>
            <ErrorBox message={error} onRetry={onRetry} />
          </div>
        )}
        {loading && feed.totalShown === 0 && (
          <div className={styles.feedNotice}>
            <LoadingStatus padded>Loading workspace…</LoadingStatus>
          </div>
        )}

        {feed.hasActiveScope && (
          <div className={styles.utilityBar}>
            <span>
              Found <span className={styles.searchSummaryCount}>{feed.totalShown}</span>{' '}
              {feed.totalShown === 1 ? 'track' : 'tracks'}
              {activeArea !== undefined ? ` in ${activeArea.name}` : ''}
            </span>
            <button type="button" className={styles.utilityAction} onClick={resetFilters}>
              Reset
            </button>
          </div>
        )}

        {tab === 'unread' && feed.allUnreadInScope.length > 0 && (
          <div className={styles.utilityBar}>
            <span>{feed.allUnreadInScope.length} unread</span>
            {onMarkAllRead !== undefined && (
              <button
                type="button"
                className={styles.utilityAction}
                onClick={() => onMarkAllRead(feed.allUnreadInScope)}
              >
                Mark all read
              </button>
            )}
          </div>
        )}

        {rows.length > 0 && (
          <ul className={styles.feed} aria-label="Tracks">
            {rows.map((track) => (
              <FocusTrackRow
                key={track.id}
                track={track}
                areas={shownAreas}
                query={query}
                now={now}
                unread={isUnread(track)}
                formatters={timeFormatters}
                onOpenTrack={onOpenTrack}
              />
            ))}
          </ul>
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
