import { useCallback } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { visibleAreas } from '../../../../core/domain/area.ts';
import type { Track } from '../../../../core/domain/track.ts';
import { MobileFocusHome } from '../../features/focus/public.tsx';
import { TrackTitle } from '../../features/track/title/public.tsx';
import { workspaceActivityErrorText, workspaceReadErrorText } from '../providers/query-read-feedback.ts';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import { useWorkspace } from '../providers/workspace-queries.ts';
import {
  useMobileWorkspaceTriggerRef, useOpenMobileSection, useRequestCreateArea,
} from '../shell/public.tsx';
import { useGo } from './navigation.ts';

/** Mobile home route: binds workspace reads, read receipts, and navigation to the full-bleed Focus feed. */
export function MobileFocusRoute({
  transport,
  unauthorized,
  shareScope = null,
}: Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  shareScope?: unknown;
}>) {
  const workspace = useWorkspace(transport, unauthorized);
  const receipts = useReadReceipts();
  const go = useGo();
  const openMobileSection = useOpenMobileSection();
  const requestCreateArea = useRequestCreateArea();
  const workspaceTriggerRef = useMobileWorkspaceTriggerRef();

  const shownAreas = visibleAreas(workspace.areas);
  const readError = workspaceReadErrorText(workspace)
    ?? workspaceActivityErrorText(workspace)
    ?? (receipts.failed ? 'Read status is unavailable.' : null);
  const readLoading = workspace.areasLoading
    || workspace.overlaysLoading
    || receipts.loading
    || [...workspace.tracksLoadingByArea.values()].some(Boolean);

  const retryRead = useCallback(() => {
    workspace.retryAreas();
    workspace.retryOverlays();
    receipts.retry();
    for (const area of workspace.areas) workspace.retryTracks(area.id);
  }, [workspace, receipts]);

  const isUnread = useCallback(
    (track: Track) => receipts.isTrackUnread(track.id, track.notifiableSessions),
    [receipts],
  );

  const handleMarkAllRead = useCallback((unreadTracks: readonly Track[]) => {
    for (const track of unreadTracks) {
      if (receipts.hasManualUnread('track', track.id)) {
        receipts.markRead('track', track.id, 0);
      }
      for (const session of track.notifiableSessions) {
        if (receipts.isUnread('conversation', session.sessionId, session.lastNotifiableTurnAtMs)) {
          receipts.markRead('conversation', session.sessionId, session.lastNotifiableTurnAtMs);
        }
      }
    }
  }, [receipts]);

  const handleNewTrack = shareScope !== null ? undefined : (areaId?: string) => {
    const targetAreaId = areaId ?? shownAreas[0]?.id;
    if (targetAreaId !== undefined) {
      go({ name: 'new-track', areaId: targetAreaId });
      return;
    }
    requestCreateArea?.({ kind: 'surface-center' });
  };

  return (
    <MobileFocusHome
      areas={shownAreas}
      tracks={workspace.tracks}
      isUnread={isUnread}
      loading={readLoading}
      error={readError}
      onRetry={retryRead}
      onOpenWorkspace={() => openMobileSection({ kind: 'areas', areaId: shownAreas[0]?.id }, 'workspace')}
      workspaceTriggerRef={workspaceTriggerRef}
      onOpenTrack={(trackId) => go({ name: 'track', trackId, from: 'pages' })}
      renderTrackTitle={(track, content) => <TrackTitle track={track}>{content}</TrackTitle>}
      onNewTrack={handleNewTrack}
      onMarkAllRead={shareScope === null ? handleMarkAllRead : undefined}
    />
  );
}
