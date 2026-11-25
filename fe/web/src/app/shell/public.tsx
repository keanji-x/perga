// The layout shell every route renders inside: a shared headless controller plus
// explicit Desktop Web and Mobile shell chromes around one persistent route stage.

import { Outlet } from '@tanstack/react-router';
import {
  createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, type ReactNode,
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
import { useCompactViewport } from '../../ui/viewport/public.ts';
import { AreaEditorDialog } from './area-editor-dialog.tsx';
import { DesktopShellChrome } from './desktop-shell-chrome.tsx';
import {
  MobileShellChrome, type MobileReturnIntent, type MobileSection, type OpenMobileSection,
} from './mobile-shell-chrome.tsx';
import { SettingsOverlay } from './settings-overlay.tsx';
import styles from './shell.module.css';
import { SystemUpgradeBannerHost } from './system-upgrade-banner.tsx';
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

function noOpenMobileSection(): void { /* no shell above this consumer */ }

/** Opens one of the shell's mobile workspace sheets. */
export function useOpenMobileSection(): OpenMobileSection {
  return useContext(MobileSectionContext) ?? noOpenMobileSection;
}

export function AppShell({
  transport, unauthorized, onOpenSettings, onSignOut, nowMs, userLabel, shareScope = null,
}: AppShellProps) {
  const shellRef = useRef<HTMLDivElement | null>(null);
  const narrowRail = useCompactViewport();
  const [mobileSection, setMobileSection] = useState<MobileSection | null>(null);
  const [mobileClosing, setMobileClosing] = useState(false);
  if (mobileSection === null && mobileClosing) setMobileClosing(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [mobileReturnIntent, setMobileReturnIntent] = useState<MobileReturnIntent>(null);
  const [mobileHeaderActionsHost, setMobileHeaderActionsHost] = useState<HTMLDivElement | null>(null);
  const [mobileHeaderTitleHost, setMobileHeaderTitleHost] = useState<HTMLDivElement | null>(null);
  const conversationScope = useMobileConversationScope();

  const closeMobileSection = useCallback(() => {
    setMobileClosing(mobileSection !== null);
  }, [mobileSection]);

  const finishMobileClose = useCallback(() => {
    setMobileSection(null);
    setMobileClosing(false);
    setMobileReturnIntent(null);
  }, []);

  const handleCloseMobileNavigation = useCallback((immediate = false) => {
    if (immediate) {
      setMobileSection(null);
      setMobileClosing(false);
      setMobileReturnIntent(null);
      return;
    }
    closeMobileSection();
  }, [closeMobileSection]);

  const controller = useShellController({
    transport,
    unauthorized,
    nowMs,
    onCloseMobileNavigation: handleCloseMobileNavigation,
  });

  useEffect(() => {
    if (!narrowRail && mobileSection !== null) {
      setMobileSection(null);
      setMobileClosing(false);
      setMobileReturnIntent(null);
    }
  }, [mobileSection, narrowRail]);

  const openMobileSection: OpenMobileSection = (section, returnFocus = 'opener') => {
    setMobileClosing(false);
    conversationScope?.onClose();
    setMobileReturnIntent(returnFocus === 'workspace'
      ? { kind: 'workspace', path: controller.currentPath }
      : { kind: 'opener', path: controller.currentPath, element: document.activeElement instanceof HTMLElement ? document.activeElement : null });
    // Daily/system Tracks have no visible workspace entry. Their switch starts in the active user Area.
    if (section.kind === 'tracks' && section.returnTo === 'report') {
      const areaId = controller.areas.find((area) => area.id === section.areaId)?.id ?? controller.activeArea?.id;
      setMobileSection(areaId === undefined ? { kind: 'areas', areaId: undefined } : { ...section, areaId });
    } else {
      setMobileSection(section);
    }
    controller.clearReportPanel();
  };

  const selectMobileSection = (section: MobileSection) => {
    setMobileClosing(false);
    setMobileSection(section);
  };

  const workspaceInteractive = narrowRail && mobileSection !== null && !mobileClosing;
  const mobileDialogActive = workspaceInteractive && !controller.areaEditorOpen;
  const railCollapsed = controller.manualRailCollapsed ?? narrowRail;

  return (
    <div
      ref={shellRef}
      className={`${styles.shell} ${narrowRail ? mobileFontClassName : ''} ${narrowRail && (controller.settingsOpen || controller.mobileOverlayRoute) ? styles.settingsPageShell : ''} ${railCollapsed ? styles.shellCollapsed : styles.shellExpanded}`}
    >
      {narrowRail ? (
        <MobileShellChrome
          shellRef={shellRef}
          transport={transport}
          unauthorized={unauthorized}
          controller={controller}
          conversationScope={conversationScope}
          historyOpen={historyOpen}
          onHistoryOpenChange={setHistoryOpen}
          mobileSection={mobileSection}
          workspaceInteractive={workspaceInteractive}
          mobileDialogActive={mobileDialogActive}
          mobileReturnIntent={mobileReturnIntent}
          onOpenMobileSection={openMobileSection}
          onSelectMobileSection={selectMobileSection}
          onCloseMobileSection={closeMobileSection}
          onFinishMobileClose={finishMobileClose}
          onActionsHostRef={setMobileHeaderActionsHost}
          onTitleHostRef={setMobileHeaderTitleHost}
          onOpenSettings={onOpenSettings}
          onSignOut={onSignOut}
          shareScope={shareScope}
          nowMs={nowMs}
          userLabel={userLabel}
        />
      ) : (
        <DesktopShellChrome
          controller={controller}
          railCollapsed={railCollapsed}
          onToggleCollapsed={() => controller.preferences.setRailCollapsed(!railCollapsed)}
          onOpenSettings={onOpenSettings}
          onSignOut={onSignOut}
          shareScope={shareScope}
          userLabel={userLabel}
        />
      )}
      <main
        ref={controller.drawerWidth.mainRef}
        className={styles.main}
        style={controller.drawerWidth.style}
        inert={mobileDialogActive}
        aria-hidden={mobileDialogActive ? true : undefined}
      >
        {narrowRail && <SystemUpgradeBannerHost variant="floating" />}
        {/* One flex item. Routes compose ErrorBox + page + Drawer as siblings;
            `:first-child` on `.main` would flex the banner, not the page. */}
        <div className={styles.stage} hidden={narrowRail && controller.settingsOpen}>
          <CreateAreaContext.Provider value={controller.requestCreateArea}>
            <MobileSectionContext.Provider value={openMobileSection}>
              <MobileHeaderActionsContext.Provider value={narrowRail ? mobileHeaderActionsHost : null}>
                <MobileHeaderTitleContext.Provider value={narrowRail ? mobileHeaderTitleHost : null}>
                  <DrawerResizeProvider value={controller.drawerWidth.resize}><Outlet /></DrawerResizeProvider>
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
