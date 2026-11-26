// Native 2-level mobile Workspace drill-down:
// Level 1 (`areas`): AREAS + LIBRARY (Memory, Closed tracks) + bottom Account bar.
// Level 2 (`tracks`): full-screen Area detail with OPEN TRACKS and collapsible CLOSED IN <AREA>.

import { useId, useLayoutEffect, useMemo, useRef } from 'react';
import { visibleAreas, type Area } from '../../../../../core/domain/area.ts';
import { activityLabelOf, activityNameBit } from '../../../../../core/domain/activity.ts';
import {
  areaPinnedTracks, hasFailed, isClosed, isWorking, needsUserAttention,
  trackActivityState, trackDisplayTitle, trackRecentAt, type Track,
} from '../../../../../core/domain/track.ts';
import { createFocusTimeFormatters, formatFocusTimestamp } from '../../../features/focus/format-time.ts';
import { TrackActions, type TrackActionsProps } from '../../../features/track/row/actions.tsx';
import { TrackTitle } from '../../../features/track/title/public.tsx';
import { ActivityIndicator } from '../../../ui/activity-indicator/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { LoadingStatus } from '../../../ui/loading-status/public.tsx';
import { Menu } from '../../../ui/menu/public.tsx';
import { useEdgePresence } from '../../../ui/motion/edge-presence.ts';
import { readSurfaceFrame } from '../../../ui/motion/geometry.ts';
import { useState } from '../../../ui/state/public.ts';
import { useUiPreferences } from '../../providers/ui-preferences.tsx';
import menuStyles from './header.module.css';
import { type MobileNavigationActions } from './navigation-header.tsx';
import styles from './navigation.module.css';

type MobileTracksProps = Readonly<{
  view: 'areas' | 'tracks';
  tracksBackLabel?: string;
  areas: readonly Area[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  areaId: string | undefined;
  currentTrackId: string | undefined;
  onSelectArea: (areaId: string) => void;
  onOpenTrack: (trackId: string) => void;
  onOpenToday?: () => void;
  onOpenMemory?: () => void;
  onOpenAreaMemory?: (areaId: string) => void;
  isUnread: (track: Track) => boolean;
  trackActions?: (track: Track) => Omit<TrackActionsProps, 'track' | 'className'>;
  readError: string | null;
  readLoading: boolean;
  onRetryRead: () => void;
  accountLabel?: string;
  accountInitial?: string;
  onSignOut?: () => void;
  shareMode?: boolean;
  nowMs?: number;
}> & MobileNavigationActions;

/** Stable page shells preserve the Workspace Level 1 scroll position across Level 2 push/pop transitions. */
export function MobileTracks(props: MobileTracksProps) {
  const tracksFrameRef = useRef<HTMLDivElement | null>(null);
  const tracksPageRef = useRef<HTMLDivElement | null>(null);
  const { view, areaId } = props;
  const [tracksPresented, setTracksPresented] = useState(view === 'tracks');
  if (view === 'tracks' && !tracksPresented) setTracksPresented(true);
  useEdgePresence({
    frame: { ref: tracksFrameRef },
    part: { element: tracksPageRef, readFrame: () => readSurfaceFrame(tracksFrameRef.current) },
    edge: 'inline-end',
    open: view === 'tracks',
    present: tracksPresented,
    enabled: true,
    onExited: () => { setTracksPresented(false); },
  });
  useLayoutEffect(() => {
    if (view === 'tracks' && tracksPageRef.current !== null) tracksPageRef.current.scrollTop = 0;
  }, [areaId, view]);

  return (
    <div ref={tracksFrameRef} className={styles.pages}>
      <div
        data-nc-workspace-page="areas"
        className={`${styles.pageFrame} ${view === 'tracks' ? styles.coveredPage : ''}`}
        inert={view !== 'areas'}
        aria-hidden={view !== 'areas' || undefined}
      >
        <WorkspaceLevelOne {...props} />
      </div>
      <div
        ref={tracksPageRef}
        data-nc-workspace-page="tracks"
        className={`${styles.pageFrame} ${view === 'tracks' ? '' : tracksPresented ? styles.coveredPage : styles.inactivePage}`}
        inert={view !== 'tracks'}
        aria-hidden={view !== 'tracks' || undefined}
      >
        <AreaLevelTwo {...props} />
      </div>
    </div>
  );
}

function WorkspaceLevelOne({
  areas, tracksByArea, areaId, onBack, onCreateArea, onSelectArea, onOpenTrack, onOpenSettings, onOpenMemory,
  isUnread, readError, readLoading, onRetryRead, accountLabel = 'Account', accountInitial = 'A', onSignOut, shareMode = false, nowMs,
}: MobileTracksProps) {
  const shown = visibleAreas(areas);
  const selected = shown.find((candidate) => candidate.id === areaId);
  const newAreaRef = useRef<HTMLButtonElement | null>(null);
  const [closedExpanded, setClosedExpanded] = useState(false);
  const timeFormatters = useMemo(
    () => createFocusTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
    [],
  );
  const now = nowMs ?? Date.now();

  const allClosedTracks = useMemo(() => {
    const rows: Array<Readonly<{ track: Track; areaName: string }>> = [];
    for (const area of shown) {
      for (const track of tracksByArea.get(area.id) ?? []) {
        if (isClosed(track)) rows.push({ track, areaName: area.name });
      }
    }
    return rows.toSorted((left, right) => trackRecentAt(right.track) - trackRecentAt(left.track));
  }, [shown, tracksByArea]);

  return (
    <div className={styles.page}>
      <header className={styles.header} data-nc-mobile-header="">
        <div className={styles.headerLeading}>
          <button type="button" className={styles.iconButton} aria-label="Back to workspace" onClick={onBack}>
            <Icon name="chevron-left" />
          </button>
        </div>
        <div className={styles.headerCenter}>
          <h1 className={styles.headerTitle}>Workspace</h1>
        </div>
        <div className={styles.headerActions}>
          {!shareMode && (
            <button type="button" className={styles.iconButton} aria-label="Settings" onClick={onOpenSettings}>
              <Icon name="settings" />
            </button>
          )}
        </div>
      </header>

      <div className={styles.scrollBody}>
        {readError !== null && <ErrorBox message={readError} onRetry={onRetryRead} />}
        {readLoading && shown.length === 0 && <LoadingStatus>Loading workspace…</LoadingStatus>}

        <section className={styles.section} aria-label="Areas">
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>Areas</h2>
            {!shareMode && (
              <button
                ref={newAreaRef}
                type="button"
                className={styles.sectionAction}
                aria-label="New area"
                onClick={() => onCreateArea({ kind: 'anchor', anchor: { ref: newAreaRef }, unavailable: 'surface-center' })}
              >
                + New area
              </button>
            )}
          </div>
          {shown.length > 0 ? (
            <ul className={styles.groupCard}>
              {shown.map((area) => {
                const areaTracks = tracksByArea.get(area.id) ?? [];
                const openCount = areaTracks.filter((track) => !isClosed(track)).length;
                const unreadCount = areaTracks.filter((track) => !isClosed(track) && isUnread(track)).length;
                const latestAt = areaTracks.reduce((max, track) => Math.max(max, trackRecentAt(track)), area.updatedAt);
                const updatedText = formatFocusTimestamp(latestAt, now, timeFormatters);
                return (
                  <li key={area.id} className={styles.groupItem}>
                    <button
                      type="button"
                      className={styles.rowButton}
                      aria-label={area.name}
                      aria-current={area.id === selected?.id ? 'location' : undefined}
                      onClick={() => onSelectArea(area.id)}
                    >
                      <span className={styles.rowIcon} aria-hidden="true">
                        <Icon name="folder" />
                      </span>
                      <span className={styles.rowBody}>
                        <span className={styles.rowTitle}>{area.name}</span>
                        <span className={styles.rowMeta}>
                          {openCount} {openCount === 1 ? 'open track' : 'open tracks'}
                          {updatedText !== '' ? ` · Updated ${updatedText}` : ''}
                        </span>
                      </span>
                      <span className={styles.rowTrailing}>
                        {unreadCount > 0 && (
                          <span className={styles.unreadBadge}>
                            {unreadCount === 1 ? '1' : `${unreadCount} unread`}
                          </span>
                        )}
                        <span className={styles.rowChevron} aria-hidden="true">
                          <Icon name="chevron-right" size="sm" />
                        </span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : (
            !readLoading && readError === null && (
              <EmptyState icon={<Icon name="folder" />} title="No areas yet" description="Projects and tracks are organized by area." />
            )
          )}
        </section>

        {!shareMode && (
          <section className={styles.section} aria-label="Library">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>Library</h2>
            </div>
            <ul className={styles.groupCard}>
              {onOpenMemory !== undefined && (
                <li className={styles.groupItem}>
                  <button type="button" className={styles.rowButton} onClick={onOpenMemory}>
                    <span className={styles.rowIcon} aria-hidden="true">
                      <Icon name="book-open" />
                    </span>
                    <span className={styles.rowBody}>
                      <span className={styles.rowTitle}>Memory</span>
                    </span>
                    <span className={styles.rowTrailing}>
                      <span className={styles.rowChevron} aria-hidden="true">
                        <Icon name="chevron-right" size="sm" />
                      </span>
                    </span>
                  </button>
                </li>
              )}
              <li className={styles.groupItem}>
                <button
                  type="button"
                  className={styles.rowButton}
                  aria-expanded={closedExpanded}
                  onClick={() => setClosedExpanded((open) => !open)}
                >
                  <span className={styles.rowIcon} aria-hidden="true">
                    <Icon name="archive" />
                  </span>
                  <span className={styles.rowBody}>
                    <span className={styles.rowTitle}>Closed tracks</span>
                  </span>
                  <span className={styles.rowTrailing}>
                    <span className={styles.countBadge}>{allClosedTracks.length}</span>
                    <span className={styles.rowChevron} aria-hidden="true">
                      <Icon name={closedExpanded ? 'chevron-down' : 'chevron-right'} size="sm" />
                    </span>
                  </span>
                </button>
              </li>
            </ul>

            {closedExpanded && allClosedTracks.length > 0 && (
              <ul className={`${styles.groupCard} ${styles.closedSubCard}`}>
                {allClosedTracks.map(({ track, areaName }) => {
                  const closedTime = formatFocusTimestamp(track.closedAt ?? track.updatedAt, now, timeFormatters);
                  return (
                    <li key={track.id} className={styles.groupItem}>
                      <button type="button" className={styles.rowButton} onClick={() => onOpenTrack(track.id)}>
                        <span className={styles.rowIcon} aria-hidden="true">
                          <Icon name="archive" size="sm" />
                        </span>
                        <span className={styles.rowBody}>
                          <span className={styles.rowTitleCalm}><TrackTitle track={track} /></span>
                          <span className={styles.rowMeta}>
                            {areaName}{closedTime !== '' ? ` · Closed ${closedTime}` : ' · Closed'}
                          </span>
                        </span>
                        <span className={styles.rowTrailing}>
                          <span className={styles.rowChevron} aria-hidden="true">
                            <Icon name="chevron-right" size="sm" />
                          </span>
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </section>
        )}
      </div>

      {!shareMode && (
        <footer className={styles.accountFooter}>
          <div className={styles.accountBar}>
            <div className={styles.accountIdentity}>
              <span className={styles.accountAvatar} aria-hidden="true">{accountInitial}</span>
              <span className={styles.accountName}>{accountLabel}</span>
            </div>
            {onSignOut !== undefined && (
              <button type="button" className={styles.signOutButton} onClick={onSignOut}>
                Sign out
              </button>
            )}
          </div>
        </footer>
      )}
    </div>
  );
}

function AreaLevelTwo({
  tracksBackLabel = 'Workspace', areas, tracksByArea, areaId, currentTrackId,
  onBack, onEditArea, onOpenTrack, onNewTrack, onOpenAreaMemory,
  isUnread, trackActions, readError, readLoading, onRetryRead, shareMode = false, nowMs,
}: MobileTracksProps) {
  const listId = useId();
  const shown = visibleAreas(areas);
  const selected = shown.find((candidate) => candidate.id === areaId);
  const preferences = useUiPreferences();
  const moreRef = useRef<HTMLSpanElement | null>(null);
  const [showClosed, setShowClosed] = useState<boolean | null>(null);
  const timeFormatters = useMemo(
    () => createFocusTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
    [],
  );
  const now = nowMs ?? Date.now();

  const orderedTracks = areaPinnedTracks(
    selected === undefined ? [] : tracksByArea.get(selected.id) ?? [],
    (track) => preferences.areaTrackPinned(track.areaId, track.id),
  );
  const openTracks = orderedTracks.filter((track) => !isClosed(track));
  const closedTracks = orderedTracks.filter(isClosed);
  const closedExpanded = showClosed ?? openTracks.length === 0;
  const unreadCount = openTracks.filter(isUnread).length;
  const showMemory = selected !== undefined && preferences.areaShowsMemory(selected.id);

  return (
    <div className={styles.page}>
      <header className={styles.headerLevelTwo} data-nc-mobile-header="">
        <div className={styles.headerLeading}>
          <button type="button" className={styles.backButton} aria-label={`Back to ${tracksBackLabel}`} onClick={onBack}>
            <Icon name="chevron-left" size="sm" />
            <span>{tracksBackLabel}</span>
          </button>
        </div>
        <div className={styles.headerCenter}>
          <h1 className={styles.headerTitle}>{selected?.name ?? 'Tracks'}</h1>
        </div>
        <div className={styles.headerActions}>
          {!shareMode && (
            <>
              <button
                type="button"
                className={`${styles.iconButton} ${styles.iconButtonAccent}`}
                aria-label="New track"
                disabled={selected === undefined}
                onClick={() => { if (selected !== undefined) onNewTrack(selected.id); }}
              >
                <Icon name="plus" />
              </button>
              {selected !== undefined && (
                <span ref={moreRef} className={styles.menuHost}>
                  <Menu
                    wrapClassName={menuStyles.toolsMenuWrap}
                    menuClassName={menuStyles.toolsMenu}
                    itemClassName={menuStyles.menuItem}
                    items={[
                      {
                        label: `Edit area ${selected.name}`,
                        onSelect: () => onEditArea(selected, { kind: 'anchor', anchor: { ref: moreRef }, unavailable: 'surface-center' }),
                      },
                      ...(onOpenAreaMemory === undefined ? [] : [{
                        label: showMemory ? 'Hide memory' : 'Show memory',
                        onSelect: () => preferences.setAreaShowsMemory(selected.id, !showMemory),
                      }]),
                    ]}
                    trigger={(triggerProps) => (
                      <button
                        {...triggerProps}
                        type="button"
                        className={styles.iconButton}
                        aria-label="Area actions"
                      >
                        <Icon name="more" />
                      </button>
                    )}
                  />
                </span>
              )}
            </>
          )}
        </div>
      </header>

      <div className={styles.scrollBody}>
        <div className={styles.areaSummaryStrip}>
          <span className={styles.areaSummaryMeta}>
            {openTracks.length} {openTracks.length === 1 ? 'open track' : 'open tracks'}
            {unreadCount > 0 ? ` · ${unreadCount} unread` : ''}
          </span>
          {selected !== undefined && onOpenAreaMemory !== undefined && (
            <button
              type="button"
              className={styles.areaMemoryLink}
              onClick={() => onOpenAreaMemory(selected.id)}
            >
              <span>Area memory</span>
              <Icon name="chevron-right" size="sm" />
            </button>
          )}
        </div>

        {readError !== null && <ErrorBox message={readError} onRetry={onRetryRead} />}
        {readLoading && orderedTracks.length === 0 && <LoadingStatus>Loading tracks…</LoadingStatus>}

        <section className={styles.section} aria-label="Open tracks">
          <div className={styles.sectionHeader}>
            <h2 className={styles.sectionTitle}>Open tracks</h2>
          </div>
          {openTracks.length > 0 ? (
            <ul className={styles.groupCard}>
              {openTracks.map((track) => {
                const unread = isUnread(track);
                const activity = trackActivityState(track, unread);
                const activityBit = activityNameBit(activity);
                const descriptionId = `${listId}-track-${track.id}-unread`;
                const failed = hasFailed(track);
                const waiting = needsUserAttention(track);
                const working = isWorking(track);
                const pinned = track.pinnedAt !== null;
                const timeText = formatFocusTimestamp(trackRecentAt(track), now, timeFormatters);
                const statusTag = failed
                  ? { text: 'Needs repair', tone: 'error' as const }
                  : waiting
                    ? { text: 'Waiting on you', tone: 'warn' as const }
                    : working
                      ? { text: 'Running', tone: 'subtle' as const }
                      : pinned
                        ? { text: 'Pinned', tone: 'subtle' as const }
                        : unread
                          ? { text: 'Unread update', tone: 'subtle' as const }
                          : null;

                return (
                  <li
                    key={track.id}
                    className={`${styles.groupItem} ${track.id === currentTrackId ? styles.groupItemCurrent : ''}`}
                  >
                    <div className={styles.trackItemRow}>
                      <button
                        type="button"
                        className={styles.trackCardButton}
                        aria-label={`${trackDisplayTitle(track.title)}${activityBit ? `, ${activityBit}` : ''}`}
                        aria-describedby={activity === 'unread' ? descriptionId : undefined}
                        aria-current={track.id === currentTrackId ? 'page' : undefined}
                        onClick={() => onOpenTrack(track.id)}
                      >
                        <span className={styles.trackGutter} aria-hidden="true">
                          {activity !== 'quiet' ? (
                            <ActivityIndicator state={activity} />
                          ) : pinned ? (
                            <Icon name="pin" size="sm" />
                          ) : (
                            <span className={styles.quietDot} />
                          )}
                        </span>
                        <span className={styles.trackBody}>
                          <span className={styles.trackTopLine}>
                            <span className={`${styles.trackTitleText} ${track.id === currentTrackId ? styles.trackTitleCurrent : ''}`}>
                              <TrackTitle track={track} />
                            </span>
                            {timeText !== '' && <span className={styles.trackTime}>{timeText}</span>}
                          </span>
                          {statusTag !== null && (
                            <span className={styles.trackSubLine}>
                              <span
                                className={
                                  statusTag.tone === 'error'
                                    ? `${styles.statusPill} ${styles.statusPillError}`
                                    : statusTag.tone === 'warn'
                                      ? `${styles.statusPill} ${styles.statusPillWarn}`
                                      : styles.statusSubtle
                                }
                              >
                                {statusTag.text}
                              </span>
                            </span>
                          )}
                        </span>
                      </button>
                      {trackActions !== undefined && (
                        <TrackActions track={track} {...trackActions(track)} className={styles.trackActions} />
                      )}
                      {activity === 'unread' && <span hidden id={descriptionId}>{activityLabelOf('unread')}</span>}
                    </div>
                  </li>
                );
              })}
            </ul>
          ) : (
            !readLoading && readError === null && (
              <EmptyState icon={<Icon name="file" />} title="No tracks in this area" description="Tracks created in this area appear here." />
            )
          )}
        </section>

        {closedTracks.length > 0 && (
          <section className={styles.section} aria-label="Closed tracks">
            <div className={styles.sectionHeader}>
              <h2 className={styles.sectionTitle}>
                Closed in {selected?.name ?? 'area'} ({closedTracks.length})
              </h2>
              <button
                type="button"
                className={styles.collapseToggle}
                aria-expanded={closedExpanded}
                onClick={() => setShowClosed(!closedExpanded)}
              >
                <span>{closedExpanded ? 'Hide' : 'Show'}</span>
                <Icon name={closedExpanded ? 'chevron-down' : 'chevron-right'} size="sm" />
              </button>
            </div>
            {closedExpanded && (
              <ul className={styles.groupCard}>
                {closedTracks.map((track) => {
                  const closedActivity = trackActivityState(track, isUnread(track));
                  const closedActivityBit = activityNameBit(closedActivity);
                  const closedDescId = `${listId}-closed-${track.id}-unread`;
                  const closedTime = formatFocusTimestamp(track.closedAt ?? track.updatedAt, now, timeFormatters);
                  return (
                    <li key={track.id} className={styles.groupItem}>
                      <button
                        type="button"
                        className={styles.rowButton}
                        aria-label={`${trackDisplayTitle(track.title)}${closedActivityBit ? `, ${closedActivityBit}` : ''}, closed`}
                        aria-describedby={closedActivity === 'unread' ? closedDescId : undefined}
                        onClick={() => onOpenTrack(track.id)}
                      >
                        <span className={styles.rowIcon} aria-hidden="true">
                          {closedActivity !== 'quiet'
                            ? <ActivityIndicator state={closedActivity} />
                            : <Icon name="archive" size="sm" />}
                        </span>
                        <span className={styles.rowBody}>
                          <span className={styles.rowTitleCalm}><TrackTitle track={track} /></span>
                          <span className={styles.rowMeta}>
                            Closed{closedTime !== '' ? ` · ${closedTime}` : ''}
                          </span>
                        </span>
                        <span className={styles.rowTrailing}>
                          <span className={styles.rowChevron} aria-hidden="true">
                            <Icon name="chevron-right" size="sm" />
                          </span>
                        </span>
                      </button>
                      {closedActivity === 'unread' && <span hidden id={closedDescId}>{activityLabelOf('unread')}</span>}
                    </li>
                  );
                })}
              </ul>
            )}
          </section>
        )}
      </div>
    </div>
  );
}
