import { Icon } from '../../ui/icon/public.tsx';
import type { EmptyStateSchema } from '../../ui/empty-state/public.tsx';
import type { SidebarTrackGroups } from './sidebar-track-groups.ts';

/** Shared shortcut wording for desktop and mobile; membership stays in its owner. */
export function trackEmptyContent(view: Exclude<keyof SidebarTrackGroups, 'focus'>): EmptyStateSchema {
  switch (view) {
    case 'pinned': return { icon: <Icon name="pin" />, title: 'No pinned tracks yet.', description: 'Pinned tracks appear here.' };
    case 'unread': return { icon: <Icon name="chat" />, title: 'No unread tracks.', description: 'Tracks with new updates appear here.' };
    case 'waiting': return { icon: <Icon name="status-waiting" />, title: 'No tracks need attention.', description: 'Tracks waiting on you appear here.' };
    case 'running': return { icon: <Icon name="status-running" />, title: 'No running tracks.', description: 'Active tracks appear here.' };
  }
}
