import { workspaceTrackGroups, type TrackGroups } from '../../../../core/domain/track-groups.ts';
import type { Area } from '../../../../core/domain/area.ts';
import type { Track } from '../../../../core/domain/track.ts';

export type SidebarTrackGroups = TrackGroups;

/** One visible membership projection for desktop groups and mobile shortcuts; receipts stay with their owner. */
export function sidebarTrackGroups(
  rows: readonly Track[],
  areas: readonly Area[],
  isUnread: (track: Track) => boolean,
): SidebarTrackGroups {
  return workspaceTrackGroups(rows, areas, isUnread);
}
