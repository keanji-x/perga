import type { ComponentProps } from 'react';

import { Sidebar } from './sidebar.tsx';

type SidebarProps = ComponentProps<typeof Sidebar>;

export type DesktopShellChromeProps = Readonly<Pick<
  SidebarProps,
  | 'executionTargets'
  | 'trackGroups'
  | 'collapsed'
  | 'onToggleCollapsed'
  | 'areas'
  | 'tracksByArea'
  | 'tracks'
  | 'currentPath'
  | 'shareScope'
  | 'readError'
  | 'readLoading'
  | 'activityError'
  | 'onRetryRead'
  | 'onGo'
  | 'onRequestCreateArea'
  | 'onRequestEditArea'
  | 'onDeleteArea'
  | 'onNewTrack'
  | 'onSetPinned'
  | 'onDeleteTrack'
  | 'onOpenSettings'
  | 'onOpenPlugins'
  | 'onSignOut'
  | 'userLabel'
>>;

/** Desktop Web shell chrome: mounts only the workspace rail as the first grid column. */
export function DesktopShellChrome({
  shareScope = null,
  ...props
}: DesktopShellChromeProps) {
  return (
    <Sidebar
      {...props}
      shareScope={shareScope}
      onShareTrack={undefined}
      onShareArea={undefined}
    />
  );
}
