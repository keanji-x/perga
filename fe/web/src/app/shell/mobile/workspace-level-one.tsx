// Level 1 of native 2-level mobile Workspace navigation:
// AREAS (with unread pills + New area) + LIBRARY (Memory, expandable Closed tracks) + sticky Account bar.

import { useMemo, useRef } from 'react';
import { visibleAreas, type Area } from '../../../../../core/domain/area.ts';
import { createRelativeTimeFormatters, formatRelativeTimestamp } from '../../../../../core/domain/relative-time.ts';
import { isClosed, trackRecentAt, type Track } from '../../../../../core/domain/track.ts';
import { MobileTrackFeedRow } from '../../../features/focus/public.tsx';
import { TrackTitle } from '../../../features/track/title/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import { LoadingStatus } from '../../../ui/loading-status/public.tsx';
import { useState } from '../../../ui/state/public.ts';
import type { MobileTracksProps } from './tracks-types.ts';
import styles from './navigation.module.css';

export function WorkspaceLevelOne({
  areas, tracksByArea, areaId, currentTrackId, onBack, onCreateArea, onSelectArea, onOpenTrack, onOpenSettings, onOpenToday, onOpenMemory,
  isUnread, readError, readLoading, onRetryRead, accountLabel = 'Account', accountInitial = 'A', onSignOut, onSetupAccount, shareMode = false, nowMs,
}: MobileTracksProps) {
  const shown = visibleAreas(areas);
  const selected = shown.find((candidate) => candidate.id === areaId);
  const newAreaRef = useRef<HTMLButtonElement | null>(null);
  const [closedExpanded, setClosedExpanded] = useState(false);
  const timeFormatters = useMemo(
    () => createRelativeTimeFormatters(Intl.DateTimeFormat().resolvedOptions().timeZone),
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
            <Icon name="close" />
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
              {shown.map((area: Area) => {
                const areaTracks = tracksByArea.get(area.id) ?? [];
                const openCount = areaTracks.filter((track) => !isClosed(track)).length;
                const unreadCount = areaTracks.filter((track) => !isClosed(track) && isUnread(track)).length;
                const latestAt = areaTracks.reduce((max, track) => Math.max(max, trackRecentAt(track)), area.updatedAt);
                const updatedText = formatRelativeTimestamp(latestAt, now, timeFormatters);
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
              {onOpenToday !== undefined && (
                <li className={styles.groupItem}>
                  <button type="button" className={styles.rowButton} onClick={onOpenToday}>
                    <span className={styles.rowIcon} aria-hidden="true">
                      <Icon name="calendar" />
                    </span>
                    <span className={styles.rowBody}>
                      <span className={styles.rowTitle}>Today</span>
                    </span>
                    <span className={styles.rowTrailing}>
                      <span className={styles.rowChevron} aria-hidden="true">
                        <Icon name="chevron-right" size="sm" />
                      </span>
                    </span>
                  </button>
                </li>
              )}
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
                {allClosedTracks.map(({ track, areaName }) => (
                  <MobileTrackFeedRow
                    key={track.id}
                    track={track}
                    areaName={areaName}
                    now={now}
                    unread={isUnread(track)}
                    formatters={timeFormatters}
                    onOpenTrack={onOpenTrack}
                    current={track.id === currentTrackId}
                    renderTitle={(track) => <TrackTitle track={track} />}
                    timestampMs={track.closedAt ?? track.updatedAt}
                    surface="card"
                  />
                ))}
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
            {onSetupAccount !== undefined ? (
              <button type="button" className={styles.signOutButton} onClick={onSetupAccount}>Set up password</button>
            ) : onSignOut !== undefined && (
              <button type="button" className={styles.signOutButton} onClick={onSignOut}>Sign out</button>
            )}
          </div>
        </footer>
      )}
    </div>
  );
}
