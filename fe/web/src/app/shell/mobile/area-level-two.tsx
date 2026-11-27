// Level 2 of native 2-level mobile Workspace navigation:
// Full-screen Area detail with OPEN TRACKS and collapsible CLOSED IN <AREA>.

import { useMemo, useRef } from 'react';
import { visibleAreas } from '../../../../../core/domain/area.ts';
import { createRelativeTimeFormatters } from '../../../../../core/domain/relative-time.ts';
import { areaPinnedTracks, isClosed } from '../../../../../core/domain/track.ts';
import { MobileTrackFeedRow } from '../../../features/focus/public.tsx';
import { TrackActions } from '../../../features/track/row/actions.tsx';
import { TrackTitle } from '../../../features/track/title/public.tsx';
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

      <div className={styles.areaBody}>
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

        {readError !== null && <div className={styles.areaNotice}><ErrorBox message={readError} onRetry={onRetryRead} /></div>}
        {readLoading && orderedTracks.length === 0 && (
          <div className={styles.areaNotice}><LoadingStatus>Loading tracks…</LoadingStatus></div>
        )}

        <section className={styles.areaSection} aria-label="Open tracks">
          <div className={styles.areaSectionHeader}>
            <h2 className={styles.sectionTitle}>Open tracks</h2>
          </div>
          {openTracks.length > 0 ? (
            <ul className={styles.areaFeed}>
              {openTracks.map((track) => (
                <MobileTrackFeedRow
                  key={track.id}
                  track={track}
                  now={now}
                  unread={isUnread(track)}
                  formatters={timeFormatters}
                  onOpenTrack={onOpenTrack}
                  current={track.id === currentTrackId}
                  renderTitle={(track) => <TrackTitle track={track} />}
                  trailingAction={trackActions === undefined ? undefined : (
                    <TrackActions track={track} {...trackActions(track)} className={styles.trackActions} />
                  )}
                />
              ))}
            </ul>
          ) : (
            !readLoading && readError === null && (
              <div className={styles.areaNotice}>
                <EmptyState icon={<Icon name="file" />} title="No tracks in this area" description="Tracks created in this area appear here." />
              </div>
            )
          )}
        </section>

        {closedTracks.length > 0 && (
          <section className={styles.areaSection} aria-label="Closed tracks">
            <div className={styles.areaSectionHeader}>
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
              <ul className={styles.areaFeed}>
                {closedTracks.map((track) => (
                  <MobileTrackFeedRow
                    key={track.id}
                    track={track}
                    now={now}
                    unread={isUnread(track)}
                    formatters={timeFormatters}
                    onOpenTrack={onOpenTrack}
                    current={track.id === currentTrackId}
                    renderTitle={(track) => <TrackTitle track={track} />}
                    timestampMs={track.closedAt ?? track.updatedAt}
                    previewLines={2}
                  />
                ))}
              </ul>
            )}
          </section>
        )}
      </div>
    </div>
  );
}
