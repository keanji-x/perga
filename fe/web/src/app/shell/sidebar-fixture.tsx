import { useReadReceipts } from '../providers/read-receipts.tsx';
import { sidebarTrackGroups } from './sidebar-track-groups.ts';
import { Sidebar as SidebarView, type SidebarProps as ViewProps } from './sidebar.tsx';

export type SidebarFixtureProps = Omit<ViewProps, 'trackGroups'>;

/** Standalone view tests compose the real membership owner and live receipts, as AppShell does. */
export function SidebarFixture(props: SidebarFixtureProps) {
  const receipts = useReadReceipts();
  const trackGroups = sidebarTrackGroups(props.tracks, props.areas,
    track => receipts.isTrackUnread(track.id, track.notifiableSessions));
  return <SidebarView {...props} trackGroups={trackGroups} />;
}
