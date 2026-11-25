import { Sidebar } from './sidebar.tsx';
import type { ShellController } from './use-shell-controller.ts';

export type DesktopShellChromeProps = Readonly<{
  controller: ShellController;
  railCollapsed: boolean;
  onToggleCollapsed: () => void;
  onOpenSettings: () => void;
  onSignOut?: (() => void) | undefined;
  shareScope?: unknown;
  userLabel?: string;
}>;

/** Desktop Web shell chrome: mounts only the workspace rail as the first grid column. */
export function DesktopShellChrome({
  controller, railCollapsed, onToggleCollapsed, onOpenSettings, onSignOut, shareScope = null, userLabel,
}: DesktopShellChromeProps) {
  const { workspace } = controller;
  return (
    <Sidebar
      executionTargets={controller.executionTargets}
      trackGroups={controller.trackGroups}
      collapsed={railCollapsed}
      onToggleCollapsed={onToggleCollapsed}
      areas={workspace.areas}
      tracksByArea={workspace.tracksByArea}
      tracks={workspace.tracks}
      currentPath={controller.currentPath}
      shareScope={shareScope}
      readError={controller.readError}
      readLoading={controller.readLoading || workspace.overlaysLoading}
      activityError={controller.activityError}
      onRetryRead={controller.retryRead}
      onGo={controller.navigateFromRail}
      onShareTrack={undefined}
      onShareArea={undefined}
      onRequestCreateArea={controller.requestCreateArea}
      onRequestEditArea={controller.requestEditArea}
      onDeleteArea={controller.deleteArea}
      onNewTrack={controller.requestNewTrack}
      onSetPinned={controller.setTrackPinned}
      onDeleteTrack={controller.deleteTrack}
      onOpenSettings={onOpenSettings}
      onOpenPlugins={undefined}
      onSignOut={onSignOut}
      userLabel={userLabel}
    />
  );
}
