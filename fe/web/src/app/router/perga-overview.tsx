import { activityLabelOf } from '../../../../core/domain/activity.ts';
import { Button } from '@astryxdesign/core/Button';
import { useId, useMemo } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { visibleAreas } from '../../../../core/domain/area.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { trackDisplayTitle, trackRecentAt, userVisibleTracks, hasFailed, needsUserAttention, trackActivityState } from '../../../../core/domain/track.ts';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { ActivityIndicator } from '../../ui/activity-indicator/public.tsx';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import { useWorkspace } from '../providers/queries.ts';
import { useRequestCreateArea } from '../shell/public.tsx';
import { useGo } from './navigation.ts';
import styles from './perga-overview.module.css';

export function PergaOverviewRoute({
  transport,
  unauthorized,
}: Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
}>) {
  const activityDescriptionId = useId();
  const receipts = useReadReceipts();
  const workspace = useWorkspace(transport, unauthorized);
  const go = useGo();
  const requestCreateArea = useRequestCreateArea();

  const areas = useMemo(() => visibleAreas(workspace.areas), [workspace.areas]);
  const areaById = useMemo(() => new Map(areas.map((area) => [area.id, area])), [areas]);
  const tracks = useMemo(() => userVisibleTracks(workspace.tracks, workspace.areas), [workspace.tracks, workspace.areas]);

  const waitingTracks = useMemo(
    () => tracks.filter((track) => track.closedAt === null && (hasFailed(track) || needsUserAttention(track))),
    [tracks],
  );
  const openTracks = useMemo(
    () =>
      [...tracks]
        .filter((track) => track.closedAt === null)
        .sort((a, b) => trackRecentAt(b) - trackRecentAt(a)),
    [tracks],
  );

  const primaryArea = areas[0];

  return (
    <section className={styles.page} aria-label="Today overview" data-nc-perga-overview="">
      <div className={styles.container}>
        <header className={styles.header}>
          <div className={styles.titleGroup}>
            <h1 className={styles.heading}>Today</h1>
            <p className={styles.subheading}>
              Active tracks, pending questions, and project areas across your Perga workspace.
            </p>
          </div>
          <div className={styles.headerActions}>
            {primaryArea !== undefined && (
              <Button
                variant="primary"
                label="New track"
                onClick={() => go({ name: 'new-track', areaId: primaryArea.id })}
              >
                New track
              </Button>
            )}
            {requestCreateArea !== null && (
              <Button
                variant={primaryArea === undefined ? 'primary' : 'secondary'}
                label="New area"
                onClick={() => requestCreateArea({ kind: 'surface-center' })}
              >
                New area
              </Button>
            )}
          </div>
        </header>

        {workspace.areasError !== null && (
          <ErrorBox
            message={readErrorText(workspace.areasError, 'Could not load workspace areas.')}
            onRetry={workspace.retryAreas}
          />
        )}

        {waitingTracks.length > 0 && (
          <section className={styles.section} aria-label="Waiting on you">
            <h2 className={styles.sectionTitle}>Waiting on you ({waitingTracks.length})</h2>
            <div className={styles.grid}>
              {waitingTracks.map((track) => {
                const area = areaById.get(track.areaId);
                const activity = trackActivityState(track, receipts.isTrackUnread(track.id, track.notifiableSessions));
                const activityLabel = activityLabelOf(activity);
                const descriptionId = `${activityDescriptionId}-waiting-${track.id}`;
                return (
                  <button
                    key={track.id}
                    type="button"
                    className={styles.trackButton}
                    aria-describedby={activityLabel === null ? undefined : descriptionId}
                    onClick={() => go({ name: 'track', trackId: track.id, openPlanner: true })}
                  >
                    <span className={styles.trackTitle}>{trackDisplayTitle(track.title)}</span>
                    {activityLabel !== null && <span id={descriptionId} hidden>{activityLabel}</span>}
                    <span className={styles.trackMeta}>
                      <ActivityIndicator state={activity} />
                      {area !== undefined && <span>{area.name}</span>}
                    </span>
                  </button>
                );
              })}
            </div>
          </section>
        )}

        <section className={styles.section} aria-label="Active tracks">
          <h2 className={styles.sectionTitle}>Active tracks ({openTracks.length})</h2>
          {openTracks.length === 0 ? (
            <div className={styles.emptyCard}>
              <p className={styles.subheading}>
                {areas.length === 0
                  ? 'Create an Area first to organize your repository and start your first Track.'
                  : 'No active tracks yet. Start a new track in one of your Areas.'}
              </p>
              {primaryArea !== undefined ? (
                <Button
                  variant="primary"
                  label={`New track in ${primaryArea.name}`}
                  onClick={() => go({ name: 'new-track', areaId: primaryArea.id })}
                >
                  New track in {primaryArea.name}
                </Button>
              ) : requestCreateArea !== null ? (
                <Button
                  variant="primary"
                  label="Create your first area"
                  onClick={() => requestCreateArea({ kind: 'surface-center' })}
                >
                  Create your first area
                </Button>
              ) : null}
            </div>
          ) : (
            <div className={styles.grid}>
              {openTracks.map((track) => {
                const area = areaById.get(track.areaId);
                const activity = trackActivityState(track, receipts.isTrackUnread(track.id, track.notifiableSessions));
                const activityLabel = activityLabelOf(activity);
                const descriptionId = `${activityDescriptionId}-active-${track.id}`;
                return (
                  <button
                    key={track.id}
                    type="button"
                    className={styles.trackButton}
                    aria-describedby={activityLabel === null ? undefined : descriptionId}
                    onClick={() => go({ name: 'track', trackId: track.id })}
                  >
                    <span className={styles.trackTitle}>{trackDisplayTitle(track.title)}</span>
                    {activityLabel !== null && <span id={descriptionId} hidden>{activityLabel}</span>}
                    <span className={styles.trackMeta}>
                      <ActivityIndicator state={activity} />
                      {area !== undefined && <span>{area.name}</span>}
                    </span>
                  </button>
                );
              })}
            </div>
          )}
        </section>

        {areas.length > 0 && (
          <section className={styles.section} aria-label="Areas">
            <h2 className={styles.sectionTitle}>Areas ({areas.length})</h2>
            <div className={styles.grid}>
              {areas.map((area) => {
                const areaTracks = workspace.tracksByArea.get(area.id) ?? [];
                const activeCount = areaTracks.filter((track) => track.closedAt === null).length;
                return (
                  <div key={area.id} className={styles.card}>
                    <span className={styles.trackTitle}>{area.name}</span>
                    {area.defaultCwd !== null && (
                      <span className={styles.trackMeta}>{area.defaultCwd}</span>
                    )}
                    <div className={styles.areaFooter}>
                      <span className={styles.trackMeta}>
                        {activeCount === 1 ? '1 active track' : `${activeCount} active tracks`}
                      </span>
                      <Button
                        variant="secondary"
                        size="sm"
                        label={`New track in ${area.name}`}
                        onClick={() => go({ name: 'new-track', areaId: area.id })}
                      >
                        + New track
                      </Button>
                    </div>
                  </div>
                );
              })}
            </div>
          </section>
        )}
      </div>
    </section>
  );
}
