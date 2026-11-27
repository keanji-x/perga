// The layout shell every route renders inside: a shared headless controller plus
// explicit Desktop Web and Mobile shell chromes around one persistent route stage.

import { Outlet } from '@tanstack/react-router';
import {
  createContext, useCallback, useContext, useLayoutEffect, useMemo, useRef, type CSSProperties, type ReactNode, type RefCallback,
} from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
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

type ConversationCloser = Readonly<{ onClose: () => void }>;
const ScopeContext = createContext<Readonly<{ current: ConversationCloser | null; register: (scope: ConversationCloser | null) => void }> | null>(null);

export function MobileConversationProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [current, setCurrent] = useState<ConversationCloser | null>(null);
  const register = useCallback((scope: ConversationCloser | null) => { setCurrent(scope); }, []);
  const value = useMemo(() => ({ current, register }), [current, register]);
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

/** Registers the active mobile conversation close handler so shell navigation dismisses open chat sheets first. */
export function useMobileConversationOwner(trackId: string, onClose: () => void) {
  const register = useContext(ScopeContext)?.register;
  const committedClose = useCommittedCallback(trackId, onClose);
  const scope = useMemo<ConversationCloser>(() => ({ onClose: committedClose }), [committedClose]);
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
  onSetupAccount?: (() => void) | undefined;
  onOwnerSetupComplete?: (() => void) | undefined;

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

const MobileHeaderTitleContext = createContext<HTMLElement | null | undefined>(undefined);
/** `null` while the shell's title slot mounts, `undefined` when the shell has no header. */
export function useMobileHeaderTitleHost(): HTMLElement | null | undefined {
  return useContext(MobileHeaderTitleContext);
}

const MobileWorkspaceTriggerRefContext = createContext<RefCallback<HTMLButtonElement> | undefined>(undefined);
export function useMobileWorkspaceTriggerRef(): RefCallback<HTMLButtonElement> | undefined {
  return useContext(MobileWorkspaceTriggerRefContext);
}

function noOpenMobileSection(): void { /* no shell above this consumer */ }

/** Opens one of the shell's mobile workspace sheets. */
export function useOpenMobileSection(): OpenMobileSection {
  return useContext(MobileSectionContext) ?? noOpenMobileSection;
}

export function AppShell({
  transport, unauthorized, onOpenSettings, onOpenPlugins, onSignOut, onSetupAccount, onOwnerSetupComplete, nowMs, userLabel, shareScope = null,
}: AppShellProps) {
  const narrowRail = useCompactViewport();
  const visibleViewport = useVisibleViewport(narrowRail);
  const [mobileHeaderActionsHost, setMobileHeaderActionsHost] = useState<HTMLDivElement | null>(null);
  const [mobileHeaderTitleHost, setMobileHeaderTitleHost] = useState<HTMLDivElement | null>(null);
  const mobileWorkspaceTriggerRef = useRef<HTMLButtonElement | null>(null);
  const setMobileWorkspaceTriggerRef = useCallback((node: HTMLButtonElement | null) => {
    mobileWorkspaceTriggerRef.current = node;
  }, []);
  const conversationScope = useContext(ScopeContext)?.current ?? null;

  const resetMobileNavigationRef = useRef<() => void>(() => {});
  const controller = useShellController({
    transport,
    unauthorized,
    nowMs,
    onAreaCreated: () => {
      resetMobileNavigationRef.current();
    },
  });
  const mobileFullBleedRoute = controller.settingsOpen || controller.mobileOverlayRoute || controller.routeOwnsMobileChrome;

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
      className={`${styles.shell} ${narrowRail ? mobileFontClassName : ''} ${narrowRail && mobileFullBleedRoute ? styles.settingsPageShell : ''} ${railCollapsed ? styles.shellCollapsed : styles.shellExpanded}`}
      style={shellStyle}
    >
      {narrowRail ? (
        <MobileShellChrome
          workspace={{
            areas: controller.workspace.areas,
            visibleAreas: controller.areas,
            tracksByArea: controller.workspace.tracksByArea,
            activeArea: controller.activeArea,
            liveActiveArea: controller.liveActiveArea,
            newTrackArea: controller.newTrackArea,
            readError: controller.readError,
            readLoading: controller.readLoading,
            retryRead: controller.retryRead,
            isUnread: controller.isUnread,
            areaIdOf: controller.areaIdOf,
          }}
          route={{
            currentPath: controller.currentPath,
            showHeader: !mobileFullBleedRoute,
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
            onSetupAccount,
          }}
          navigation={mobileNavigation}
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
          onSetupAccount={onSetupAccount}
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
                <MobileHeaderTitleContext.Provider value={narrowRail && !mobileFullBleedRoute ? mobileHeaderTitleHost : undefined}>
                  <MobileWorkspaceTriggerRefContext.Provider value={narrowRail ? setMobileWorkspaceTriggerRef : undefined}>
                    <DrawerResizeProvider value={controller.drawerWidth.resize}><Outlet /></DrawerResizeProvider>
                  </MobileWorkspaceTriggerRefContext.Provider>
                </MobileHeaderTitleContext.Provider>
              </MobileHeaderActionsContext.Provider>
            </MobileSectionContext.Provider>
          </CreateAreaContext.Provider>
        </div>
        {/* Beside the persistent route stage so settings panes survive tab changes and resizing. */}
        <SettingsOverlay transport={transport} unauthorized={unauthorized} onOwnerSetupComplete={onOwnerSetupComplete} />
      </main>
      <AreaEditorDialog editor={controller.areaEditor} />
    </div>
  );
}
