import { useQueries } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { byRecency, type Conversation } from '../../../../core/domain/conversation.ts';
import { trackDisplayTitle, type Track } from '../../../../core/domain/track.ts';
import { agentSessionDtoToConversation } from '../../../../core/domain/conversation.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';

/** Reads only the declared current Track while history is open, using the existing query caches. */
export function useMobileHistoryData(transport: ApiTransportPort, unauthorized: UnauthorizedChannel,
  tracks: readonly Pick<Track, 'id' | 'areaId' | 'title'>[], enabled: boolean) {
  const details = useQueries({ queries: tracks.map((track) => ({ ...trackDetailQueryOptions(transport, track.id, unauthorized), enabled })) });
  const rows = new Map<string, Conversation>();
  tracks.forEach((track, index) => {
    const detail = details[index].data;
    if (detail === undefined || detail.track.id !== track.id) return;
    for (const session of detail.sessions) {
      const row = { ...agentSessionDtoToConversation(session), trackTitle: trackDisplayTitle(track.title) };
      if (row.trackId === track.id) rows.set(row.id, row);
    }
  });
  const reads = details;
  return {
    conversations: [...rows.values()].toSorted(byRecency),
    loading: enabled && reads.some((read) => read.isPending),
    failed: reads.some((read) => read.isError),
    retry: () => { for (const read of reads) void read.refetch(); },
  };
}
