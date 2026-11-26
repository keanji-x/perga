import { LayerDepthProvider } from '@astryxdesign/core/Layer';
import { useLayoutEffect, useMemo, useRef, type RefCallback, type RefObject } from 'react';

import type { ApiTransportPort } from '../../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../../core/api/unauthorized.ts';
import type { Area } from '../../../../../core/domain/area.ts';
import type { Conversation } from '../../../../../core/domain/conversation.ts';
import { localDailyDate } from '../../../../../core/domain/daily-planner.ts';
import type { Track } from '../../../../../core/domain/track.ts';
import { trackDisplayTitle } from '../../../../../core/domain/track.ts';
import { type TrackActionsProps } from '../../../features/track/row/actions.tsx';
import { LayerDismissal, type DialogOrigin } from '../../../ui/dialog/public.tsx';
import { EdgeSwipe } from '../../../ui/edge-swipe/public.tsx';
import { OperationFeedback, type OperationFeedbackState } from '../../../ui/operation-feedback/public.tsx';
import type { NavTarget } from '../../router/navigation.ts';
import type { NewTrackDraftArea } from '../../router/new-track-drafts.tsx';
import { MobileWorkspaceHeader } from './header.tsx';
import { useMobileHistoryData } from './history-data.ts';
import { MobileHistory } from './history.tsx';
import { useMobileNavigationPresence } from './navigation-presence.ts';
import { MobilePages } from './pages.tsx';
import mobileStyles from './shell.module.css';
import { MobileTracks } from './tracks.tsx';
import type { SidebarTrackGroups } from '../sidebar-track-groups.ts';
import type { MobileNavigationController, MobileReturnIntent } from './use-navigation.ts';

export type MobileConversationScopeSnapshot = Readonly<{
  owner: object;
  track: Readonly<{ id: string; areaId: string; title: string }>;
  selectedConversationId: string | null;
  conversationOpen: boolean;
  onNew: () => void;
  onClose: () => void;
  onOpen: (conversation: Conversation) => void;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>;

export type MobileShellWorkspace = Readonly<{
  areas: readonly Area[];
  visibleAreas: readonly Area[];
  tracks: readonly Track[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  trackGroups: Pick<SidebarTrackGroups, 'pinned' | 'unread' | 'waiting'>;
  activeArea: Area | NewTrackDraftArea | undefined;
  liveActiveArea: Area | undefined;
  newTrackArea: NewTrackDraftArea | undefined;
  readError: string | null;
  activityError: string | null;
  readLoading: boolean;
  overlaysLoading: boolean;
  retryRead: () => void;
  isUnread: (track: Track) => boolean;
  areaIdOf: (trackId: string) => string | undefined;
}>;

export type MobileShellRoute = Readonly<{
  currentPath: string;
  routeOwnsMobileChrome: boolean;
  settingsOpen: boolean;
  mobileOverlayRoute: boolean;
  routeTrackId: string | undefined;
  routeAreaId: string | undefined;
  newTrackBack: (() => void) | null;
  go: (target: NavTarget) => void;
}>;

export type MobileShellActions = Readonly<{
  areaEditorOpen: boolean;
  pinFeedback: OperationFeedbackState;
  mobileTrackActions: (track: Track) => Omit<TrackActionsProps, 'track' | 'className'>;
  onRequestCreateArea: (origin: DialogOrigin) => void;
  onRequestEditArea: (area: Area, origin: DialogOrigin) => void;
  onOpenSettings: () => void;
  onSignOut?: (() => void) | undefined;
}>;

export type MobileShellChromeProps = Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  workspace: MobileShellWorkspace;
  route: MobileShellRoute;
  actions: MobileShellActions;
  navigation: MobileNavigationController;
  conversationScope: MobileConversationScopeSnapshot | null;
  historyOpen: boolean;
  onHistoryOpenChange: (open: boolean) => void;
  onActionsHostRef: RefCallback<HTMLDivElement>;
  onTitleHostRef: RefCallback<HTMLDivElement>;
  workspaceReturnRef?: RefObject<HTMLButtonElement | null>;
  shareScope?: unknown;
  nowMs?: number;
  userLabel?: string;
}>;

/** Mobile shell chrome: owns the mobile workspace header, floating opener, edge swipe, history drawer, and modal navigation stack. */
export function MobileShellChrome({
  transport, unauthorized, workspace, route, actions, navigation, conversationScope,
  historyOpen, onHistoryOpenChange, onActionsHostRef, onTitleHostRef, workspaceReturnRef: routeWorkspaceReturnRef,
  shareScope = null, nowMs, userLabel,
}: MobileShellChromeProps) {
  const { currentPath, routeOwnsMobileChrome, settingsOpen, mobileOverlayRoute } = route;
  const scopeOwner = conversationScope?.owner ?? null;
  const historyOwner = useMemo(() => ({ path: currentPath, scopeOwner, token: {} }), [currentPath, scopeOwner]);
  const historyTracks = conversationScope === null ? [] : [conversationScope.track];
  const history = useMobileHistoryData(transport, unauthorized, historyTracks, historyOpen);
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
  const navigationAreaId = navigation.section?.kind === 'tracks' || navigation.section?.kind === 'areas'
    ? navigation.section.areaId ?? workspace.activeArea?.id : workspace.activeArea?.id;
  const navigationArea = workspace.visibleAreas.find((area) => area.id === navigationAreaId);
  const mobileNavigationLabel = navigation.section?.kind === 'pages' ? 'Pages' : 'Tracks and settings';

  return (
    <>
      {dismissalLayer}
      {!settingsOpen && !mobileOverlayRoute && !routeOwnsMobileChrome && (
        <MobileWorkspaceHeader
          shareMode={shareScope !== null}
          areas={workspace.visibleAreas}
          activeArea={workspace.liveActiveArea ?? (workspace.newTrackArea === undefined ? workspace.visibleAreas[0] : undefined)}
          draftArea={workspace.newTrackArea}
          navigationOpen={navigation.interactive || historyOpen}
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
        enabled={!settingsOpen && !mobileOverlayRoute && !navigation.isOpen && !historyOpen}
        onSwipe={() => {
          navigation.open({ kind: 'areas', areaId: workspace.activeArea?.id }, 'workspace');
        }}
      />
      <MobileHistory
        owner={historyOwner.token}
        open={historyOpen}
        onOpenChange={onHistoryOpenChange}
        selectedConversationId={conversationScope?.selectedConversationId ?? null}
        trackGroups={workspace.trackGroups}
        currentTrackId={conversationScope?.track.id}
        isUnread={workspace.isUnread}
        tracksLoading={workspace.readLoading || workspace.overlaysLoading}
        tracksError={workspace.readError ?? workspace.activityError}
        onRetryTracks={workspace.retryRead}
        onOpenTrack={(trackId) => { conversationScope?.onClose(); route.go({ name: 'track', trackId }); }}
        onArchive={conversationScope?.onArchive}
        conversations={history.conversations}
        loading={history.loading}
        failed={history.failed}
        onRetry={history.retry}
        onNew={conversationScope?.onNew ?? null}
        now={nowMs ?? Date.now()}
        onSelect={(row) => conversationScope?.onOpen(row)}
        onOpenSettings={actions.onOpenSettings}
        scopeLabel={conversationScope === null ? 'Current track' : `Current track · ${trackDisplayTitle(conversationScope.track.title)}`}
        accountLabel={userLabel ?? 'Account'}
        accountInitial={userLabel?.slice(0, 1) ?? 'U'}
        onSignOut={actions.onSignOut === undefined ? undefined : () => { navigation.close(); actions.onSignOut?.(); }}
      />
      <div ref={mobileNavigationFrameRef} className={mobileStyles.navigationFrame}>
        <div
          ref={mobileNavigationRef}
          id="mobile-workspace-navigation"
          className={`${mobileStyles.navigation} ${navigation.isOpen && !actions.areaEditorOpen ? mobileStyles.navigationOpen : ''}`}
          role={navigation.dialogActive ? 'dialog' : undefined}
          aria-modal={navigation.dialogActive ? true : undefined}
          aria-label={mobileNavigationLabel}
          inert={!navigation.dialogActive}
          aria-hidden={!navigation.dialogActive ? true : undefined}
          data-nc-escape-layer={navigation.dialogActive ? '' : undefined}
          tabIndex={-1}
        >
          <div className={mobileStyles.navigationPanel}>
            <div className={mobileStyles.navigationContent}>
              <LayerDepthProvider>
                {navigation.section?.kind === 'pages' ? (
                  <MobilePages
                    areaId={navigationArea?.id}
                    onEditArea={actions.onRequestEditArea}
                    onBack={() => navigation.close()}
                    onNewTrack={openNewTrack}
                    onCreateArea={actions.onRequestCreateArea}
                    onOpenSettings={() => { navigation.close(); actions.onOpenSettings(); }}
                    areas={workspace.areas}
                    tracks={workspace.tracks}
                    readError={workspace.readError}
                    readLoading={workspace.readLoading}
                    onRetryRead={workspace.retryRead}
                    onOpenToday={() => navigateAndClose({ name: 'today', day: localDailyDate() })}
                    onOpenMemory={shareScope === null ? () => navigateAndClose({ name: 'memory' }) : undefined}
                    onOpenTrack={(trackId) => {
                      navigation.close();
                      // The sheets are the only writers of `?from=`: the surface the reader returns to.
                      route.go({ name: 'track', trackId, from: 'pages' });
                    }}
                  />
                ) : navigation.section !== null ? (
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
                    onOpenToday={() => navigateAndClose({ name: 'today', day: localDailyDate() })}
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
                ) : null}
              </LayerDepthProvider>
            </div>
            <OperationFeedback feedback={actions.pinFeedback} />
          </div>
        </div>
      </div>
    </>
  );
}
