import { LayerDepthProvider } from '@astryxdesign/core/Layer';
import { useLayoutEffect, useMemo, useRef, type RefCallback } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { trackDisplayTitle } from '../../../../core/domain/track.ts';
import { LayerDismissal } from '../../ui/dialog/public.tsx';
import { EdgeSwipe } from '../../ui/edge-swipe/public.tsx';
import { floatingControlClassName } from '../../ui/floating-control/public.ts';
import { Icon } from '../../ui/icon/public.tsx';
import { OperationFeedback } from '../../ui/operation-feedback/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useVisibleViewport } from '../../ui/viewport/public.ts';
import { MobileWorkspaceHeader } from './mobile-header.tsx';
import { useMobileHistoryData } from './mobile-history-data.ts';
import { MobileHistory } from './mobile-history.tsx';
import { useMobileNavigationPresence } from './mobile-navigation-presence.ts';
import { MobilePages } from './mobile-pages.tsx';
import mobileStyles from './mobile-shell.module.css';
import { MobileTracks } from './mobile-tracks.tsx';
import type { ShellController } from './use-shell-controller.ts';

/** Routes can reopen an Area's Tracks or recent Pages; the primary entry starts at Areas. */
export type MobileSection =
  | Readonly<{ kind: 'areas'; areaId: string | undefined }>
  | Readonly<{ kind: 'pages' }>
  | Readonly<{ kind: 'tracks'; areaId: string | undefined; returnTo?: 'areas' | 'report' }>;

export type OpenMobileSection = (section: MobileSection, returnFocus?: 'opener' | 'workspace') => void;

export type MobileReturnIntent =
  | Readonly<{ kind: 'opener'; path: string; element: HTMLElement | null }>
  | Readonly<{ kind: 'workspace'; path: string }>
  | null;

export type MobileConversationScopeSnapshot = Readonly<{
  owner: object;
  track: Readonly<{ id: string; areaId: string; title: string }>;
  selectedConversationId: string | null;
  conversationOpen: boolean;
  onNew: () => void;
  onClose: () => void;
  onOpen: (conversation: Parameters<Parameters<typeof MobileHistory>[0]['onSelect']>[0]) => void;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>;

export type MobileShellChromeProps = Readonly<{
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  controller: ShellController;
  conversationScope: MobileConversationScopeSnapshot | null;
  mobileSection: MobileSection | null;
  workspaceInteractive: boolean;
  mobileDialogActive: boolean;
  mobileReturnIntent: MobileReturnIntent;
  onOpenMobileSection: OpenMobileSection;
  onSelectMobileSection: (section: MobileSection) => void;
  onCloseMobileSection: () => void;
  onFinishMobileClose: () => void;
  onActionsHostRef: RefCallback<HTMLDivElement>;
  onTitleHostRef: RefCallback<HTMLDivElement>;
  onOpenSettings: () => void;
  onSignOut?: (() => void) | undefined;
  shareScope?: unknown;
  nowMs?: number;
  userLabel?: string;
}>;

/** Mobile shell chrome: owns the mobile workspace header, floating opener, edge swipe, history drawer, and modal navigation stack. */
export function MobileShellChrome({
  transport, unauthorized, controller, conversationScope, mobileSection, workspaceInteractive,
  mobileDialogActive, mobileReturnIntent, onOpenMobileSection, onSelectMobileSection,
  onCloseMobileSection, onFinishMobileClose, onActionsHostRef, onTitleHostRef,
  onOpenSettings, onSignOut, shareScope = null, nowMs, userLabel,
}: MobileShellChromeProps) {
  const { workspace, areas, activeArea, liveActiveArea, newTrackArea, currentPath, settingsOpen, mobileOverlayRoute } = controller;
  const [historyOpen, setHistoryOpen] = useState(false);
  const scopeOwner = conversationScope?.owner ?? null;
  const historyOwner = useMemo(() => ({ path: currentPath, scopeOwner, token: {} }), [currentPath, scopeOwner]);
  const historyTracks = conversationScope === null ? [] : [conversationScope.track];
  const history = useMobileHistoryData(transport, unauthorized, historyTracks, historyOpen);
  const visibleViewport = useVisibleViewport(true);
  const mobileNavOpen = mobileSection !== null;
  const mobileSectionKind = mobileSection?.kind;

  const workspaceReturnRef = useRef<HTMLButtonElement | null>(null);
  const mobileNavigationFrameRef = useRef<HTMLDivElement | null>(null);
  const mobileNavigationRef = useRef<HTMLDivElement | null>(null);
  const mobileFocusScope = useRef<Readonly<{ path: string; host: HTMLElement | null; intent: MobileReturnIntent }> | null>(null);

  useMobileNavigationPresence(
    mobileNavigationRef,
    mobileNavigationFrameRef,
    onFinishMobileClose,
    workspaceInteractive,
    mobileNavOpen,
  );

  const dismissalLayer = (
    <LayerDismissal {...{
      isActive: mobileDialogActive,
      onDismiss: onCloseMobileSection,
      getContainer: () => mobileNavigationRef.current,
    }} />
  );

  useLayoutEffect(() => {
    if (!workspaceInteractive) {
      const scope = mobileFocusScope.current;
      mobileFocusScope.current = null;
      // DOM permissions have committed before focus returns. A new route or a
      // newer focus owner wins; paint completion never restores focus.
      if (scope !== null && scope.path === currentPath
        && (document.activeElement === document.body || scope.host?.contains(document.activeElement))) {
        const opener = scope.intent?.kind === 'workspace' ? workspaceReturnRef.current
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
    if (scope === null || scope.intent !== mobileReturnIntent) {
      mobileFocusScope.current = {
        path: mobileReturnIntent?.path ?? currentPath,
        host: mobileNavigationRef.current,
        intent: mobileReturnIntent,
      };
      if (scope === null) mobileNavigationRef.current?.focus({ preventScroll: true });
    }
    // Back requests navigation and workspace together. Either may commit first;
    // bind that return intent to its first destination, never later selections.
    if (mobileReturnIntent?.kind === 'workspace' && mobileFocusScope.current?.path === mobileReturnIntent.path
      && currentPath !== mobileReturnIntent.path) {
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
  }, [workspaceInteractive, currentPath, mobileReturnIntent]);

  useLayoutEffect(() => {
    if (workspaceInteractive && mobileSectionKind !== undefined) {
      const activeBack = mobileNavigationRef.current?.querySelector<HTMLElement>(
        '[data-nc-workspace-page]:not([aria-hidden="true"]) header button',
      );
      (activeBack ?? mobileNavigationRef.current)?.focus({ preventScroll: true });
    }
  }, [mobileSectionKind, workspaceInteractive]);

  // Opening while Areas is loading has no id yet. Follow the current Area
  // when the read recovers; an explicitly requested Area still stays exact.
  const navigationAreaId = mobileSection?.kind === 'tracks' || mobileSection?.kind === 'areas'
    ? mobileSection.areaId ?? activeArea?.id : activeArea?.id;
  const navigationArea = areas.find((area) => area.id === navigationAreaId);
  const mobileNavigationLabel = mobileSection?.kind === 'pages' ? 'Pages' : 'Tracks and settings';

  return (
    <>
      {dismissalLayer}
      {!settingsOpen && !mobileOverlayRoute && (
        <MobileWorkspaceHeader
          areas={areas}
          activeArea={liveActiveArea ?? (newTrackArea === undefined ? areas[0] : undefined)}
          draftArea={newTrackArea}
          navigationOpen={workspaceInteractive || historyOpen}
          historyTriggerRef={workspaceReturnRef}
          onBack={controller.newTrackBack === null || controller.routeAreaId === undefined ? undefined : () => {
            controller.newTrackBack?.();
            onOpenMobileSection({ kind: 'tracks', areaId: controller.routeAreaId }, 'workspace');
          }}
          onOpenNavigation={() => setHistoryOpen(true)}
          onSelectArea={controller.requestNewTrack}
          onCreateArea={controller.requestCreateArea}
          actionsHostRef={onActionsHostRef}
          titleHostRef={onTitleHostRef}
        />
      )}
      {!settingsOpen && !mobileOverlayRoute && (
        <button
          type="button"
          className={`${mobileStyles.workspaceOpener} ${floatingControlClassName}`}
          aria-label="Open workspace"
          hidden={workspaceInteractive || historyOpen || conversationScope?.conversationOpen === true}
          style={{ bottom: `calc(${visibleViewport.bottomInset}px + var(--space-4) + env(safe-area-inset-bottom))` }}
          onClick={() => onOpenMobileSection({ kind: 'areas', areaId: activeArea?.id })}
        >
          <Icon name="folder" />
        </button>
      )}
      <EdgeSwipe
        enabled={!settingsOpen && !mobileOverlayRoute && !mobileNavOpen && !historyOpen}
        onSwipe={() => setHistoryOpen(true)}
      />
      <MobileHistory
        owner={historyOwner.token}
        open={historyOpen}
        onOpenChange={setHistoryOpen}
        selectedConversationId={conversationScope?.selectedConversationId ?? null}
        trackGroups={controller.trackGroups}
        currentTrackId={conversationScope?.track.id}
        isUnread={controller.isUnread}
        tracksLoading={controller.readLoading || workspace.overlaysLoading}
        tracksError={controller.readError ?? controller.activityError}
        onRetryTracks={controller.retryRead}
        onOpenTrack={(trackId) => { conversationScope?.onClose(); controller.go({ name: 'track', trackId }); }}
        onArchive={conversationScope?.onArchive}
        conversations={history.conversations}
        loading={history.loading}
        failed={history.failed}
        onRetry={history.retry}
        onNew={conversationScope?.onNew ?? null}
        now={nowMs ?? Date.now()}
        onSelect={(row) => conversationScope?.onOpen(row)}
        onOpenSettings={onOpenSettings}
        scopeLabel={conversationScope === null ? 'Current track' : `Current track · ${trackDisplayTitle(conversationScope.track.title)}`}
        accountLabel={userLabel ?? 'Account'}
        accountInitial={userLabel?.slice(0, 1) ?? 'U'}
        onSignOut={onSignOut === undefined ? undefined : () => { onCloseMobileSection(); onSignOut(); }}
      />
      <div ref={mobileNavigationFrameRef} className={mobileStyles.navigationFrame}>
        <div
          ref={mobileNavigationRef}
          id="mobile-workspace-navigation"
          className={`${mobileStyles.navigation} ${mobileNavOpen && !controller.areaEditorOpen ? mobileStyles.navigationOpen : ''}`}
          role={mobileDialogActive ? 'dialog' : undefined}
          aria-modal={mobileDialogActive ? true : undefined}
          aria-label={mobileNavigationLabel}
          inert={!mobileDialogActive}
          aria-hidden={!mobileDialogActive ? true : undefined}
          data-nc-escape-layer={mobileDialogActive ? '' : undefined}
          tabIndex={-1}
        >
          <div className={mobileStyles.navigationPanel}>
            <div className={mobileStyles.navigationContent}>
              <LayerDepthProvider>
                {mobileSection?.kind === 'pages' ? (
                  <MobilePages
                    areaId={navigationArea?.id}
                    onEditArea={controller.requestEditArea}
                    onBack={onCloseMobileSection}
                    onNewTrack={controller.requestNewTrack}
                    onCreateArea={controller.requestCreateArea}
                    onOpenSettings={() => { onCloseMobileSection(); onOpenSettings(); }}
                    areas={workspace.areas}
                    tracks={workspace.tracks}
                    readError={controller.readError}
                    readLoading={controller.readLoading}
                    onRetryRead={controller.retryRead}
                    onOpenToday={() => controller.navigateFromRail({ name: 'today' })}
                    onOpenMemory={shareScope === null ? () => controller.navigateFromRail({ name: 'memory' }) : undefined}
                    onOpenTrack={(trackId) => {
                      onCloseMobileSection();
                      // The sheets are the only writers of `?from=`: the surface the reader returns to.
                      controller.go({ name: 'track', trackId, from: 'pages' });
                    }}
                  />
                ) : mobileSection !== null ? (
                  <MobileTracks
                    view={mobileSection.kind === 'tracks' ? 'tracks' : 'areas'}
                    onSelectArea={(areaId) => { onSelectMobileSection({ kind: 'tracks', areaId }); }}
                    onBack={mobileSection.kind === 'tracks' && mobileSection.returnTo !== 'report'
                      ? () => onSelectMobileSection({ kind: 'areas', areaId: navigationArea?.id })
                      : onCloseMobileSection}
                    tracksBackLabel={mobileSection.kind === 'tracks' && mobileSection.returnTo === 'report' ? 'Report' : 'Areas'}
                    onNewTrack={controller.requestNewTrack}
                    onOpenSettings={() => { onCloseMobileSection(); onOpenSettings(); }}
                    currentTrackId={controller.routeTrackId}
                    areas={workspace.areas}
                    tracksByArea={workspace.tracksByArea}
                    readError={controller.readError}
                    readLoading={controller.readLoading}
                    onRetryRead={controller.retryRead}
                    onOpenToday={() => controller.navigateFromRail({ name: 'today' })}
                    onOpenMemory={shareScope === null ? () => controller.navigateFromRail({ name: 'memory' }) : undefined}
                    onOpenAreaMemory={shareScope === null ? (areaId) => controller.navigateFromRail({ name: 'area-memory', areaId }) : undefined}
                    areaId={navigationArea?.id}
                    onCreateArea={controller.requestCreateArea}
                    onEditArea={controller.requestEditArea}
                    trackActions={controller.mobileTrackActions}
                    isUnread={controller.isUnread}
                    onOpenTrack={(trackId) => {
                      onCloseMobileSection();
                      controller.go({
                        name: 'track',
                        trackId,
                        from: controller.areaIdOf(trackId) === undefined ? 'pages' : 'area',
                      });
                    }}
                  />
                ) : null}
              </LayerDepthProvider>
            </div>
            <OperationFeedback feedback={controller.pinFeedback} />
          </div>
        </div>
      </div>
    </>
  );
}
