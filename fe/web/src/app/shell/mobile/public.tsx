import { LayerDepthProvider } from '@astryxdesign/core/Layer';
import { useLayoutEffect, useRef, type RefCallback, type RefObject } from 'react';

import type { Area } from '../../../../../core/domain/area.ts';
import type { Track } from '../../../../../core/domain/track.ts';
import { type TrackActionsProps } from '../../../features/track/row/actions.tsx';
import { LayerDismissal, type DialogOrigin } from '../../../ui/dialog/public.tsx';
import { EdgeSwipe } from '../../../ui/edge-swipe/public.tsx';
import { OperationFeedback, type OperationFeedbackState } from '../../../ui/operation-feedback/public.tsx';
import type { NavTarget } from '../../router/navigation.ts';
import type { NewTrackDraftArea } from '../../router/new-track-drafts.tsx';
import { MobileWorkspaceHeader } from './header.tsx';
import { useMobileNavigationPresence } from './navigation-presence.ts';
import mobileStyles from './shell.module.css';
import { MobileTracks } from './tracks.tsx';
import type { MobileNavigationController, MobileReturnIntent } from './use-navigation.ts';

type MobileShellWorkspace = Readonly<{
  areas: readonly Area[];
  visibleAreas: readonly Area[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  activeArea: Area | NewTrackDraftArea | undefined;
  liveActiveArea: Area | undefined;
  newTrackArea: NewTrackDraftArea | undefined;
  readError: string | null;
  readLoading: boolean;
  retryRead: () => void;
  isUnread: (track: Track) => boolean;
  areaIdOf: (trackId: string) => string | undefined;
}>;

type MobileShellRoute = Readonly<{
  currentPath: string;
  showHeader: boolean;
  settingsOpen: boolean;
  mobileOverlayRoute: boolean;
  routeTrackId: string | undefined;
  routeAreaId: string | undefined;
  newTrackBack: (() => void) | null;
  go: (target: NavTarget) => void;
}>;

type MobileShellActions = Readonly<{
  areaEditorOpen: boolean;
  pinFeedback: OperationFeedbackState;
  mobileTrackActions: (track: Track) => Omit<TrackActionsProps, 'track' | 'className'>;
  onRequestCreateArea: (origin: DialogOrigin) => void;
  onRequestEditArea: (area: Area, origin: DialogOrigin) => void;
  onOpenSettings: () => void;
  onSignOut?: (() => void) | undefined;
  onSetupAccount?: (() => void) | undefined;
}>;

type MobileShellChromeProps = Readonly<{
  workspace: MobileShellWorkspace;
  route: MobileShellRoute;
  actions: MobileShellActions;
  navigation: MobileNavigationController;
  onActionsHostRef: RefCallback<HTMLDivElement>;
  onTitleHostRef: RefCallback<HTMLDivElement>;
  workspaceReturnRef?: RefObject<HTMLButtonElement | null>;
  shareScope?: unknown;
  nowMs?: number;
  userLabel?: string;
}>;

/** Mobile shell chrome: owns the mobile workspace header, edge swipe, and modal 2-level navigation stack. */
export function MobileShellChrome({
  workspace, route, actions, navigation,
  onActionsHostRef, onTitleHostRef, workspaceReturnRef: routeWorkspaceReturnRef,
  shareScope = null, nowMs, userLabel,
}: MobileShellChromeProps) {
  const { currentPath, showHeader, settingsOpen, mobileOverlayRoute } = route;
  const mobileSectionKind = navigation.section?.kind;

  const workspaceReturnRef = useRef<HTMLButtonElement | null>(null);
  const mobileNavigationFrameRef = useRef<HTMLDivElement | null>(null);
  const mobileNavigationRef = useRef<HTMLDivElement | null>(null);
  const mobileFocusScope = useRef<Readonly<{ path: string; host: HTMLElement | null; intent: MobileReturnIntent }> | null>(null);

  useMobileNavigationPresence(
    mobileNavigationRef,
    mobileNavigationFrameRef,
    navigation.finishClose,
    navigation.interactive,
    navigation.isOpen,
  );

  const navigateAndClose = (target: NavTarget, immediate = false) => {
    navigation.close(immediate);
    route.go(target);
  };

  const openNewTrack = (areaId: string) => {
    navigateAndClose({ name: 'new-track', areaId }, actions.areaEditorOpen);
  };

  const dismissalLayer = (
    <LayerDismissal {...{
      isActive: navigation.dialogActive,
      onDismiss: () => navigation.close(),
      getContainer: () => mobileNavigationRef.current,
    }} />
  );

  useLayoutEffect(() => {
    if (!navigation.interactive) {
      const scope = mobileFocusScope.current;
      mobileFocusScope.current = null;
      // DOM permissions have committed before focus returns. A new route or a
      // newer focus owner wins; paint completion never restores focus.
      if (scope !== null && scope.path === currentPath
        && (document.activeElement === document.body || scope.host?.contains(document.activeElement))) {
        const opener = scope.intent?.kind === 'workspace'
          ? (workspaceReturnRef.current ?? routeWorkspaceReturnRef?.current ?? null)
          : scope.intent?.element;
        if (opener?.isConnected
          && opener.closest('[inert], [hidden], [aria-hidden="true"], :disabled, [aria-disabled="true"]') === null
          && opener.getClientRects().length > 0) {
          opener.focus({ preventScroll: true });
        }
      }
      return;
    }
    const scope = mobileFocusScope.current;
    if (scope === null || scope.intent !== navigation.returnIntent) {
      mobileFocusScope.current = {
        path: navigation.returnIntent?.path ?? currentPath,
        host: mobileNavigationRef.current,
        intent: navigation.returnIntent,
      };
      if (scope === null) mobileNavigationRef.current?.focus({ preventScroll: true });
    }
    // Back requests navigation and workspace together. Either may commit first;
    // bind that return intent to its first destination, never later selections.
    if (navigation.returnIntent?.kind === 'workspace' && mobileFocusScope.current?.path === navigation.returnIntent.path
      && currentPath !== navigation.returnIntent.path) {
      mobileFocusScope.current = { ...mobileFocusScope.current, path: currentPath };
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') return;
      const focusable = Array.from(mobileNavigationRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ) ?? []).filter((element) => element.closest('[inert], [hidden], [aria-hidden="true"]') === null);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (document.activeElement === mobileNavigationRef.current) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
        return;
      }
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => { document.removeEventListener('keydown', onKeyDown); };
  }, [navigation.interactive, currentPath, navigation.returnIntent, routeWorkspaceReturnRef]);

  useLayoutEffect(() => {
    if (navigation.interactive && mobileSectionKind !== undefined) {
      const activeBack = mobileNavigationRef.current?.querySelector<HTMLElement>(
        '[data-nc-workspace-page]:not([aria-hidden="true"]) header button',
      );
      (activeBack ?? mobileNavigationRef.current)?.focus({ preventScroll: true });
    }
  }, [mobileSectionKind, navigation.interactive]);

  // Opening while Areas is loading has no id yet. Follow the current Area
  // when the read recovers; an explicitly requested Area still stays exact.
  const navigationAreaId = navigation.section?.areaId ?? workspace.activeArea?.id;
  const navigationArea = workspace.visibleAreas.find((area) => area.id === navigationAreaId);

  return (
    <>
      {dismissalLayer}
      {showHeader && (
        <MobileWorkspaceHeader
          shareMode={shareScope !== null}
          areas={workspace.visibleAreas}
          activeArea={workspace.liveActiveArea ?? (workspace.newTrackArea === undefined ? workspace.visibleAreas[0] : undefined)}
          draftArea={workspace.newTrackArea}
          navigationOpen={navigation.interactive}
          historyTriggerRef={workspaceReturnRef}
          onBack={route.newTrackBack !== null && route.routeAreaId !== undefined
            ? () => {
              route.newTrackBack?.();
              navigation.open({ kind: 'tracks', areaId: route.routeAreaId }, 'workspace');
            }
            : route.routeTrackId !== undefined
              ? () => { route.go({ name: 'today' }); }
              : undefined}
          backLabel={route.routeTrackId !== undefined ? 'Focus' : 'Tracks'}
          onOpenNavigation={() => navigation.open({ kind: 'areas', areaId: workspace.activeArea?.id }, 'workspace')}
          onSelectArea={openNewTrack}
          onCreateArea={actions.onRequestCreateArea}
          actionsHostRef={onActionsHostRef}
          titleHostRef={onTitleHostRef}
        />
      )}
      <EdgeSwipe
        enabled={!settingsOpen && !mobileOverlayRoute && !navigation.isOpen}
        onSwipe={() => {
          navigation.open({ kind: 'areas', areaId: workspace.activeArea?.id }, 'workspace');
        }}
      />
      <div ref={mobileNavigationFrameRef} className={mobileStyles.navigationFrame}>
        <div
          ref={mobileNavigationRef}
          id="mobile-workspace-navigation"
          className={`${mobileStyles.navigation} ${navigation.isOpen && !actions.areaEditorOpen ? mobileStyles.navigationOpen : ''}`}
          role={navigation.dialogActive ? 'dialog' : undefined}
          aria-modal={navigation.dialogActive ? true : undefined}
          aria-label="Tracks and settings"
          inert={!navigation.dialogActive}
          aria-hidden={!navigation.dialogActive ? true : undefined}
          data-nc-escape-layer={navigation.dialogActive ? '' : undefined}
          tabIndex={-1}
        >
          <div className={mobileStyles.navigationPanel}>
            <div className={mobileStyles.navigationContent}>
              <LayerDepthProvider>
                {navigation.section !== null && (
                  <MobileTracks
                    view={navigation.section.kind === 'tracks' ? 'tracks' : 'areas'}
                    onSelectArea={(areaId) => { navigation.select({ kind: 'tracks', areaId }); }}
                    onBack={navigation.section.kind === 'tracks' && navigation.section.returnTo !== 'report'
                      ? () => navigation.select({ kind: 'areas', areaId: navigationArea?.id })
                      : () => navigation.close()}
                    tracksBackLabel={navigation.section.kind === 'tracks' && navigation.section.returnTo === 'report' ? 'Report' : 'Workspace'}
                    onNewTrack={openNewTrack}
                    onOpenSettings={() => { navigation.close(); actions.onOpenSettings(); }}
                    currentTrackId={route.routeTrackId}
                    areas={workspace.areas}
                    tracksByArea={workspace.tracksByArea}
                    readError={workspace.readError}
                    readLoading={workspace.readLoading}
                    onRetryRead={workspace.retryRead}
                    onOpenToday={() => navigateAndClose({ name: 'today', view: 'today' })}
                    onOpenMemory={shareScope === null ? () => navigateAndClose({ name: 'memory' }) : undefined}
                    onOpenAreaMemory={shareScope === null ? (areaId) => navigateAndClose({ name: 'area-memory', areaId }) : undefined}
                    areaId={navigationArea?.id}
                    onCreateArea={actions.onRequestCreateArea}
                    onEditArea={actions.onRequestEditArea}
                    trackActions={shareScope === null ? actions.mobileTrackActions : undefined}
                    isUnread={workspace.isUnread}
                    accountLabel={userLabel ?? 'Account'}
                    accountInitial={userLabel?.slice(0, 1) ?? 'A'}
                    onSignOut={actions.onSignOut === undefined ? undefined : () => { navigation.close(); actions.onSignOut?.(); }}
                    onSetupAccount={actions.onSetupAccount === undefined ? undefined : () => { navigation.close(); actions.onSetupAccount?.(); }}
                    shareMode={shareScope !== null}
                    nowMs={nowMs}
                    onOpenTrack={(trackId) => {
                      navigation.close();
                      route.go({
                        name: 'track',
                        trackId,
                        from: workspace.areaIdOf(trackId) === undefined ? 'pages' : 'area',
                      });
                    }}
                  />
                )}
              </LayerDepthProvider>
            </div>
            <OperationFeedback feedback={actions.pinFeedback} />
          </div>
        </div>
      </div>
    </>
  );
}
