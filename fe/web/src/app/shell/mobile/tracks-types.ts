import type { Area } from '../../../../../core/domain/area.ts';
import type { Track } from '../../../../../core/domain/track.ts';
import type { TrackActionsProps } from '../../../features/track/row/actions.tsx';
import type { MobileNavigationActions } from './navigation-header.tsx';

export type MobileTracksProps = Readonly<{
  view: 'areas' | 'tracks';
  tracksBackLabel?: string;
  areas: readonly Area[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  areaId: string | undefined;
  currentTrackId: string | undefined;
  onSelectArea: (areaId: string) => void;
  onOpenTrack: (trackId: string) => void;
  onOpenToday?: () => void;
  onOpenMemory?: () => void;
  onOpenAreaMemory?: (areaId: string) => void;
  isUnread: (track: Track) => boolean;
  trackActions?: (track: Track) => Omit<TrackActionsProps, 'track' | 'className'>;
  readError: string | null;
  readLoading: boolean;
  onRetryRead: () => void;
  accountLabel?: string;
  accountInitial?: string;
  onSignOut?: () => void;
  shareMode?: boolean;
  nowMs?: number;
}> & MobileNavigationActions;
