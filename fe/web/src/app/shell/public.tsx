// The layout shell every route renders inside: a shared headless controller plus
// explicit Desktop Web and Mobile shell chromes around one persistent route stage.

import { Outlet } from '@tanstack/react-router';
import {
  createContext, useCallback, useContext, useLayoutEffect, useMemo, useRef, type CSSProperties, type ReactNode, type RefCallback,
} from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { Conversation } from '../../../../core/domain/conversation.ts';
import type { Track } from '../../../../core/domain/track.ts';
import type { DialogOrigin } from '../../ui/dialog/public.tsx';
import type { DrawerResize } from '../../ui/drawer/public.tsx';
import { mobileFontClassName } from '../../ui/mobile-font/public.ts';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport, useVisibleViewport } from '../../ui/viewport/public.ts';
import { AreaEditorDialog } from './area-editor-dialog.tsx';
import { DesktopShellChrome } from './desktop/public.tsx';
import { MobileShellChrome } from './mobile/public.tsx';
import { SettingsOverlay } from './settings-overlay.tsx';
import styles from './shell.module.css';
import { SystemUpgradeBannerHost } from './system-upgrade-banner.tsx';
import { useMobileNavigation, type OpenMobileSection } from './mobile/use-navigation.ts';
import { useShellController } from './use-shell-controller.ts';

type Scope = Readonly<{
  track: Pick<Track, 'id' | 'areaId' | 'title'>;
  conversations: readonly Conversation[];
  selectedConversationId: string | null;
  onNew: () => void;
  onClose: () => void;
  onOpen: (conversation: Conversation) => void;
  conversationOpen: boolean;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>;
type RegisteredScope = Scope & Readonly<{ owner: object }>;
const ScopeContext = createContext<Readonly<{ current: RegisteredScope | null; register: (scope: RegisteredScope | null) => void }> | null>(null);

export function MobileConversationProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [current, setCurrent] = useState<RegisteredScope | null>(null);
  const register = useCallback((scope: RegisteredScope | null) => { setCurrent(scope); }, []);
  const value = useMemo(() => ({ current, register }), [current, register]);
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

export function useMobileConversationScope() { return useContext(ScopeContext)?.current ?? null; }

/** The rendered route declares its owner; the shell never infers a daily/system Track from the URL. */
export function useMobileConversationOwner(owner: Scope) {
  const register = useContext(ScopeContext)?.register;
  const { track, conversations, selectedConversationId, conversationOpen } = owner;
  // One registration lifetime, including a fresh mount at the same pathname.
  const [identity, setIdentity] = useState(() => ({ trackId: track.id, token: {} }));
  if (identity.trackId !== track.id) setIdentity({ trackId: track.id, token: {} });
  const onNew = useCommittedCallback(track.id, owner.onNew);
  const onClose = useCommittedCallback(track.id, owner.onClose);
  const onOpen = useCommittedCallback(track.id, owner.onOpen);
  const onArchive = useCommittedCallback(track.id, async (cardId: string, archived: boolean) => { await owner.onArchive?.(cardId, archived); });
  const canArchive = owner.onArchive !== undefined;
  const scope = useMemo<RegisteredScope>(() => ({
    owner: identity.token, track: { id: track.id, areaId: track.areaId, title: track.title }, conversations, selectedConversationId, conversationOpen,
    onNew, onClose, onOpen, onArchive: canArchive ? onArchive : undefined,
  }), [identity.token, track.id, track.areaId, track.title, conversations, selectedConversationId, conversationOpen, onNew, onClose, onOpen, canArchive, onArchive]);
  useLayoutEffect(() => {
    register?.(scope);
    return () => { register?.(null); };
  }, [register, scope]);
}

const CreateAreaContext = createContext<((origin: DialogOrigin) => void) | null>(null);
export function useRequestCreateArea() { return useContext(CreateAreaContext); }

export type AppShellProps = Readonly<{
  shareScope?: unknown;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  onOpenSettings: () => void;
  /** Settings › Plugins, from the rail's account menu. */
  onOpenPlugins?: (() => void) | undefined;
  onSignOut?: (() => void) | undefined;

  /** Pinned by tests so `pinned_at` assertions are stable. */
  nowMs?: number;
  userLabel?: string;
}>;

const MobileSectionContext = createContext<OpenMobileSection | null>(null);

/** App owns header geometry; the feature owns the existing action menu and focus. */
const MobileHeaderActionsContext = createContext<HTMLElement | null>(null);
export function useMobileHeaderActionsHost(): HTMLElement | null {
  return useContext(MobileHeaderActionsContext);
}

const DrawerResizeContext = createContext<DrawerResize | undefined>(undefined);
export const DrawerResizeProvider = DrawerResizeContext.Provider;
/** The resize contract for the route's conversation drawer (`./drawer-width.tsx`); `undefined` outside the shell, which means no handle. */
export function useConversationDrawerResize(): DrawerResize | undefined { return useContext(DrawerResizeContext); }

const MobileHeaderTitleContext = createContext<HTMLElement | null>(null);
export function useMobileHeaderTitleHost(): HTMLElement | null {
  return useContext(MobileHeaderTitleContext);
}

const MobileWorkspaceTriggerRefContext = createContext<RefCallback<HTMLButtonElement> | undefined>(undefined);
export function useMobileWorkspaceTriggerRef(): RefCallback<HTMLButtonElement> | undefined {
  return useContext(MobileWorkspaceTriggerRefContext);
}

const MobileRouteChromeOwnerContext = createContext<((token: object | null, previous?: object) => void) | null>(null);
/** Declares that the active mobile route renders its own top header and workspace opener. */
export function useMobileRouteChromeOwner(owned = true) {
  const register = useContext(MobileRouteChromeOwnerContext);
  const [token] = useState(() => ({}));
  useLayoutEffect(() => {
    if (!owned) return;
    register?.(token);
    return () => { register?.(null, token); };
  }, [owned, register, token]);
}

function noOpenMobileSection(): void { /* no shell above this consumer */ }

/** Opens one of the shell's mobile workspace sheets. */
export function useOpenMobileSection(): OpenMobileSection {
  return useContext(MobileSectionContext) ?? noOpenMobileSection;
}

export function AppShell({
  transport, unauthorized, onOpenSettings, onOpenPlugins, onSignOut, nowMs, userLabel, shareScope = null,
}: AppShellProps) {
  const narrowRail = useCompactViewport();
  const visibleViewport = useVisibleViewport(narrowRail);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [mobileHeaderActionsHost, setMobileHeaderActionsHost] = useState<HTMLDivElement | null>(null);
  const [mobileHeaderTitleHost, setMobileHeaderTitleHost] = useState<HTMLDivElement | null>(null);
  const [mobileChromeOwner, setMobileChromeOwner] = useState<object | null>(null);
  const registerMobileChromeOwner = useCallback((next: object | null, previous?: object) => {
    setMobileChromeOwner((current) => {
      if (next !== null) return next;
      return previous === undefined || current === previous ? null : current;
    });
  }, []);
  const routeOwnsMobileChrome = mobileChromeOwner !== null;
  const mobileWorkspaceTriggerRef = useRef<HTMLButtonElement | null>(null);
  const setMobileWorkspaceTriggerRef = useCallback((node: HTMLButtonElement | null) => {
    mobileWorkspaceTriggerRef.current = node;
  }, []);
  const conversationScope = useMobileConversationScope();

  const resetMobileNavigationRef = useRef<() => void>(() => {});
  const controller = useShellController({
    transport,
    unauthorized,
    nowMs,
    onAreaCreated: () => {
      resetMobileNavigationRef.current();
    },
  });

  const mobileNavigation = useMobileNavigation({
    compact: narrowRail,
    suppressed: controller.areaEditorOpen,
    currentPath: controller.currentPath,
    areas: controller.areas,
    activeAreaId: controller.activeArea?.id,
    onBeforeOpen: () => {
      conversationScope?.onClose();
      controller.clearReportPanel();
    },
  });
  useLayoutEffect(() => {
    resetMobileNavigationRef.current = mobileNavigation.resetImmediate;
  }, [mobileNavigation.resetImmediate]);

  const railCollapsed = controller.manualRailCollapsed ?? narrowRail;
  const shellStyle = narrowRail
    ? ({ '--mobile-keyboard-inset': `${visibleViewport.bottomInset}px` } as CSSProperties)
    : undefined;

  return (
    <div
      className={`${styles.shell} ${narrowRail ? mobileFontClassName : ''} ${narrowRail && (controller.settingsOpen || controller.mobileOverlayRoute || routeOwnsMobileChrome) ? styles.settingsPageShell : ''} ${railCollapsed ? styles.shellCollapsed : styles.shellExpanded}`}
      style={shellStyle}
    >
      {narrowRail ? (
        <MobileShellChrome
          transport={transport}
          unauthorized={unauthorized}
          workspace={{
            areas: controller.workspace.areas,
            visibleAreas: controller.areas,
            tracks: controller.workspace.tracks,
            tracksByArea: controller.workspace.tracksByArea,
            trackGroups: controller.trackGroups,
            activeArea: controller.activeArea,
            liveActiveArea: controller.liveActiveArea,
            newTrackArea: controller.newTrackArea,
            readError: controller.readError,
            activityError: controller.activityError,
            readLoading: controller.readLoading,
            overlaysLoading: controller.workspace.overlaysLoading,
            retryRead: controller.retryRead,
            isUnread: controller.isUnread,
            areaIdOf: controller.areaIdOf,
          }}
          route={{
            currentPath: controller.currentPath,
            routeOwnsMobileChrome,
            settingsOpen: controller.settingsOpen,
            mobileOverlayRoute: controller.mobileOverlayRoute,
            routeTrackId: controller.routeTrackId,
            routeAreaId: controller.routeAreaId,
            newTrackBack: controller.newTrackBack,
            go: controller.go,
          }}
          actions={{
            areaEditorOpen: controller.areaEditorOpen,
            pinFeedback: controller.pinFeedback,
            mobileTrackActions: controller.mobileTrackActions,
            onRequestCreateArea: controller.requestCreateArea,
            onRequestEditArea: controller.requestEditArea,
            onOpenSettings,
            onSignOut,
          }}
          navigation={mobileNavigation}
          conversationScope={conversationScope}
          historyOpen={historyOpen}
          onHistoryOpenChange={setHistoryOpen}
          onActionsHostRef={setMobileHeaderActionsHost}
          onTitleHostRef={setMobileHeaderTitleHost}
          workspaceReturnRef={mobileWorkspaceTriggerRef}
          shareScope={shareScope}
          nowMs={nowMs}
          userLabel={userLabel}
        />
      ) : (
        <DesktopShellChrome
          executionTargets={controller.executionTargets}
          trackGroups={controller.trackGroups}
          collapsed={railCollapsed}
          onToggleCollapsed={() => controller.preferences.setRailCollapsed(!railCollapsed)}
          areas={controller.workspace.areas}
          tracksByArea={controller.workspace.tracksByArea}
          tracks={controller.workspace.tracks}
          currentPath={controller.currentPath}
          shareScope={shareScope}
          readError={controller.readError}
          readLoading={controller.readLoading || controller.workspace.overlaysLoading}
          activityError={controller.activityError}
          onRetryRead={controller.retryRead}
          onGo={controller.go}
          onRequestCreateArea={controller.requestCreateArea}
          onRequestEditArea={controller.requestEditArea}
          onDeleteArea={controller.deleteArea}
          onNewTrack={controller.openNewTrack}
          onSetPinned={controller.setTrackPinned}
          onDeleteTrack={controller.deleteTrack}
          onOpenSettings={onOpenSettings}
          onOpenPlugins={onOpenPlugins}
          onSignOut={onSignOut}
          userLabel={userLabel}
        />
      )}
      <main
        ref={controller.drawerWidth.mainRef}
        className={styles.main}
        style={controller.drawerWidth.style}
        inert={mobileNavigation.dialogActive}
        aria-hidden={mobileNavigation.dialogActive ? true : undefined}
      >
        {narrowRail && <SystemUpgradeBannerHost variant="floating" />}
        {/* One flex item. Routes compose ErrorBox + page + Drawer as siblings;
            `:first-child` on `.main` would flex the banner, not the page. */}
        <div className={styles.stage} hidden={narrowRail && controller.settingsOpen}>
          <CreateAreaContext.Provider value={controller.requestCreateArea}>
            <MobileSectionContext.Provider value={mobileNavigation.open}>
              <MobileHeaderActionsContext.Provider value={narrowRail ? mobileHeaderActionsHost : null}>
                <MobileHeaderTitleContext.Provider value={narrowRail ? mobileHeaderTitleHost : null}>
                  <MobileWorkspaceTriggerRefContext.Provider value={narrowRail ? setMobileWorkspaceTriggerRef : undefined}>
                    <MobileRouteChromeOwnerContext.Provider value={narrowRail ? registerMobileChromeOwner : null}>
                      <DrawerResizeProvider value={controller.drawerWidth.resize}><Outlet /></DrawerResizeProvider>
                    </MobileRouteChromeOwnerContext.Provider>
                  </MobileWorkspaceTriggerRefContext.Provider>
                </MobileHeaderTitleContext.Provider>
              </MobileHeaderActionsContext.Provider>
            </MobileSectionContext.Provider>
          </CreateAreaContext.Provider>
        </div>
        {/* Beside the persistent route stage so settings panes survive tab changes and resizing. */}
        <SettingsOverlay transport={transport} unauthorized={unauthorized} />
      </main>
      <AreaEditorDialog editor={controller.areaEditor} />
    </div>
  );
}
