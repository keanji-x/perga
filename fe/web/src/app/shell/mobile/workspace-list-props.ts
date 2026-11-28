import type { Ref } from 'react';
import type { Area } from '../../../../../core/domain/area.ts';
import type { Track } from '../../../../../core/domain/track.ts';

export type WorkspaceListProps = Readonly<{
  areas: readonly Area[];
  currentTrackId?: string;
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  backLabel: string;
  onBack: () => void;
  scrollRef: Ref<HTMLDivElement>;
  initialClosedExpanded?: boolean;
  onClosedExpandedChange: (expanded: boolean) => void;
  onOpenTrack: (trackId: string) => void;
  isUnread: (track: Track) => boolean;
  readError: string | null;
  readLoading: boolean;
  onRetryRead: () => void;
  nowMs?: number;
}>;
