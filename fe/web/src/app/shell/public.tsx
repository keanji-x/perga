// The layout shell every route renders inside: a shared headless controller plus
// explicit Desktop Web and Mobile shell chromes around one persistent route stage.

import { Outlet } from '@tanstack/react-router';
import {
  createContext, useContext, type CSSProperties,
} from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { DialogOrigin } from '../../ui/dialog/public.tsx';
import type { DrawerResize } from '../../ui/drawer/public.tsx';
import { mobileFontClassName } from '../../ui/mobile-font/public.ts';
import { useState } from '../../ui/state/public.ts';
import { useCompactViewport, useVisibleViewport } from '../../ui/viewport/public.ts';
import { AreaEditorDialog } from './area-editor-dialog.tsx';
import { DesktopShellChrome } from './desktop/public.tsx';
import { MobileWorkspaceHeader } from './mobile/header.tsx';
import { MobileWorkspaceRoute, type WorkspaceRoutes } from './mobile/workspace-routes.tsx';
import { useRouteParam } from '../router/navigation.ts';
import { SettingsOverlay } from './settings-overlay.tsx';
import styles from './shell.module.css';
import { SystemUpgradeBannerHost } from './system-upgrade-banner.tsx';
import { useShellController } from './use-shell-controller.ts';

const CreateAreaContext = createContext<((origin: DialogOrigin) => void) | null>(null);
export function useRequestCreateArea() { return useContext(CreateAreaContext); }

const WorkspaceRoutesContext = createContext<WorkspaceRoutes | null>(null);

export function WorkspaceRoute() {
  const routes = useContext(WorkspaceRoutesContext);
  return <MobileWorkspaceRoute routes={routes} />;
}

export function AreaRoute() {
  const routes = useContext(WorkspaceRoutesContext);
  const areaId = useRouteParam('/area/');
  return <MobileWorkspaceRoute routes={routes} areaId={areaId} />;
}

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

export function AppShell({
  transport, unauthorized, onOpenSettings, onOpenPlugins, onSignOut, onSetupAccount, onOwnerSetupComplete, nowMs, userLabel, shareScope = null,
}: AppShellProps) {
  const narrowRail = useCompactViewport();
  const visibleViewport = useVisibleViewport(narrowRail);
  const [mobileHeaderActionsHost, setMobileHeaderActionsHost] = useState<HTMLDivElement | null>(null);
  const [mobileHeaderTitleHost, setMobileHeaderTitleHost] = useState<HTMLDivElement | null>(null);
  const controller = useShellController({ transport, unauthorized, nowMs });
  const mobileFullBleedRoute = controller.settingsOpen || controller.routeOwnsMobileChrome;

  const railCollapsed = controller.manualRailCollapsed ?? narrowRail;
  const shellStyle = narrowRail
    ? ({ '--mobile-keyboard-inset': `${visibleViewport.bottomInset}px` } as CSSProperties)
    : undefined;

  return (
    <div
      className={`${styles.shell} ${narrowRail ? mobileFontClassName : ''} ${narrowRail && mobileFullBleedRoute ? styles.mobileFullBleedShell : ''} ${railCollapsed ? styles.shellCollapsed : styles.shellExpanded}`}
      style={shellStyle}
    >
      {narrowRail && !mobileFullBleedRoute && (
        <MobileWorkspaceHeader
          areas={controller.areas}
          activeArea={controller.liveActiveArea ?? (controller.newTrackArea === undefined ? controller.areas[0] : undefined)}
          draftArea={controller.newTrackArea}
          shareMode={shareScope !== null}
          obscured={controller.routePanel !== null}
          onSelectArea={controller.openNewTrack}
          onCreateArea={controller.requestCreateArea}
          actionsHostRef={setMobileHeaderActionsHost}
          titleHostRef={setMobileHeaderTitleHost}
        />
      )}
      {!narrowRail && (
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
      >
        {narrowRail && <SystemUpgradeBannerHost variant="floating" />}
        {/* One flex item. Routes compose ErrorBox + page + Drawer as siblings;
            `:first-child` on `.main` would flex the banner, not the page. */}
        <div className={styles.stage} hidden={narrowRail && controller.settingsOpen}>
          <CreateAreaContext.Provider value={controller.requestCreateArea}>
            <MobileHeaderActionsContext.Provider value={narrowRail && !mobileFullBleedRoute ? mobileHeaderActionsHost : null}>
              <MobileHeaderTitleContext.Provider value={narrowRail && !mobileFullBleedRoute ? mobileHeaderTitleHost : undefined}>
                <WorkspaceRoutesContext.Provider value={{ controller, onOpenSettings, onSignOut, onSetupAccount, userLabel, nowMs }}>
                  <DrawerResizeProvider value={controller.drawerWidth.resize}><Outlet /></DrawerResizeProvider>
                </WorkspaceRoutesContext.Provider>
              </MobileHeaderTitleContext.Provider>
            </MobileHeaderActionsContext.Provider>
          </CreateAreaContext.Provider>
        </div>
        {/* Beside the persistent route stage so settings panes survive tab changes and resizing. */}
        <SettingsOverlay areas={controller.areas} transport={transport} unauthorized={unauthorized} onOwnerSetupComplete={onOwnerSetupComplete} />
      </main>
      <AreaEditorDialog editor={controller.areaEditor} />
    </div>
  );
}
