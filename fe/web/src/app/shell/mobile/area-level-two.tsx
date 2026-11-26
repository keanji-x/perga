// Level 2 of native 2-level mobile Workspace navigation:
// Full-screen Area detail with OPEN TRACKS and collapsible CLOSED IN <AREA>.

import { useId, useMemo, useRef } from 'react';
import { activityLabelOf, activityNameBit } from '../../../../../core/domain/activity.ts';
import { visibleAreas } from '../../../../../core/domain/area.ts';
import { createRelativeTimeFormatters, formatRelativeTimestamp } from '../../../../../core/domain/relative-time.ts';
import { trackStatusTag } from '../../../../../core/domain/track-groups.ts';
import {
  areaPinnedTracks, isClosed, trackActivityState, trackDisplayTitle, trackRecentAt,
} from '../../../../../core/domain/track.ts';
import { TrackActions } from '../../../features/track/row/actions.tsx';
import { TrackTitle } from '../../../features/track/title/public.tsx';
import { ActivityIndicator } from '../../../ui/activity-indicator/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { LoadingStatus } from '../../../ui/loading-status/public.tsx';
import { Menu } from '../../../ui/menu/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import { useUiPreferences } from '../../providers/ui-preferences.tsx';
import menuStyles from './header.module.css';
import type { MobileTracksProps } from './tracks-types.ts';
import styles from './navigation.module.css';

export function AreaLevelTwo({
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
    () => createRelativeTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
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
                const pinned = track.pinnedAt !== null;
                const timeText = formatRelativeTimestamp(trackRecentAt(track), now, timeFormatters);
                const statusTag = trackStatusTag(track, unread, { includePinned: true });

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
                  const closedTime = formatRelativeTimestamp(track.closedAt ?? track.updatedAt, now, timeFormatters);
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
