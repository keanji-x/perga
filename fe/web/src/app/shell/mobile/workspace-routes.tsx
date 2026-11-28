import { createContext, useContext, useEffect, type ReactNode } from 'react';
import { OperationFeedback } from '../../../ui/operation-feedback/public.tsx';
import { useCompactViewport } from '../../../ui/viewport/public.ts';
import { useAppHistory, useHistoryEntryIndex, useHistoryReturnFocus, useMobileHistoryView } from '../../router/app-history.tsx';
import { useMobileBack } from '../../router/mobile-back.ts';
import { routeParamFromPath, useGo, useRouteParam } from '../../router/navigation.ts';
import type { ShellController } from '../use-shell-controller.ts';
import { AreaLevelTwo } from './area-level-two.tsx';
import { WorkspaceLevelOne } from './workspace-level-one.tsx';

type WorkspaceRoutes = Readonly<{
  controller: ShellController;
  onOpenSettings: () => void;
  onSignOut?: () => void;
  onSetupAccount?: () => void;
  userLabel?: string;
  nowMs?: number;
}>;
const RoutesContext = createContext<WorkspaceRoutes | null>(null);

export function MobileWorkspaceRoutesProvider({ children, ...routes }: WorkspaceRoutes & { children: ReactNode }) {
  return <RoutesContext.Provider value={routes}>{children}</RoutesContext.Provider>;
}

export function WorkspaceRoute() { return <MobileWorkspaceRoute />; }

export function AreaRoute() {
  const areaId = useRouteParam('/area/');
  return <MobileWorkspaceRoute areaId={areaId} />;
}

function MobileWorkspaceRoute({ areaId }: { areaId?: string }) {
  const compact = useCompactViewport();
  const historyIndex = useHistoryEntryIndex();
  const go = useGo();
  const routes = useContext(RoutesContext);
  useEffect(() => {
    if (!compact) go({ name: 'today' }, { replace: true });
  }, [compact, go]);
  if (!compact || routes === null) return null;
  return <WorkspacePage key={`${areaId ?? 'workspace'}:${historyIndex}`} historyIndex={historyIndex} {...routes} areaId={areaId} />;
}

function WorkspacePage({ controller, areaId, historyIndex, onOpenSettings, onSignOut, onSetupAccount, userLabel, nowMs }: WorkspaceRoutes & {
  areaId?: string;
  historyIndex: number;
}) {
  const { go, areas, workspace, readError, readLoading, retryRead, isUnread } = controller;
  const back = useMobileBack({ name: 'today' }, 'Focus', areas);
  const { initialView, rememberView, scrollRef } = useMobileHistoryView<boolean>(historyIndex, !readLoading);
  const { rememberReturnFocus } = useHistoryReturnFocus(!readLoading);
  const { previousEntry } = useAppHistory();
  const props = {
    currentTrackId: routeParamFromPath(previousEntry?.pathname ?? '', '/track/'),
    ...back, scrollRef, initialClosedExpanded: initialView, onClosedExpandedChange: rememberView, areas, tracksByArea: workspace.tracksByArea, readError, readLoading,
    onRetryRead: retryRead, isUnread, nowMs,
    onOpenTrack: (trackId: string) => go({ name: 'track', trackId }),
  };
  return <>
    {areaId === undefined
      ? <WorkspaceLevelOne {...props} accountLabel={userLabel} onOpenSettings={onOpenSettings}
          onSignOut={onSignOut} onSetupAccount={onSetupAccount}
          onSelectArea={(id) => {
            rememberReturnFocus(`workspace-area:${id}`);
            go({ name: 'area', areaId: id });
          }}
          onCreateArea={controller.requestCreateArea}
          onOpenToday={() => go({ name: 'today', view: 'today' })}
          onOpenMemory={() => go({ name: 'memory' })} />
      : <AreaLevelTwo {...props} areaId={areaId}
          onEditArea={controller.requestEditArea} trackActions={controller.mobileTrackActions}
          onNewTrack={controller.openNewTrack}
          onOpenAreaMemory={(id) => go({ name: 'area-memory', areaId: id })} />}
    <OperationFeedback feedback={controller.pinFeedback} />
  </>;
}
