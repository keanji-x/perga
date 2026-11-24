// The `@` search port (#1881): `GET /api/areas/{area_id}/mentions`, read fresh on every call.
// No query cache: the server reads SQL per request so a new tag or a rename shows on the next
// keystroke, and a client cache would undo that.

import { useMemo } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { memoryMentionsOperation } from '../../../../core/domain/memory.ts';
import { memoryMentionSuggestion,mentionQueryOf,templateMentionSuggestions,type MentionSearch,type MentionSuggestion } from '../../../../core/domain/mentions.ts';
import { tracksInAreaOperation,trackTemplatesOperation,type TrackTemplate } from '../../../../core/domain/track.ts';
import { runOperation } from './query-operation.ts';

/** `trackId` is the track the message is written in, or `null` before there is one. */
export function mentionSearchOf(
  transport: ApiTransportPort, unauthorized: UnauthorizedChannel, areaId: string | null, trackId: string | null,
  onTemplates?: (templates: readonly TrackTemplate[]) => void,
  memoryAreaId: string | null | false = areaId,
): MentionSearch {
  return async (typed, signal) => {
    const query = mentionQueryOf(typed);
    const sources: Promise<readonly MentionSuggestion[]>[] = [];

      if (areaId !== null && (query.kind === null || query.kind === 'track')) {
        const needle = query.text.trim().toLocaleLowerCase();
        sources.push(
          runOperation(transport, { ...tracksInAreaOperation(areaId), signal }, unauthorized)
            .then((tracks) =>
              tracks
                .filter((t) => t.id !== trackId && `${t.title} ${t.id}`.toLocaleLowerCase().includes(needle))
                .slice(0, 8)
                .map((t): MentionSuggestion => {
                  const label = t.title.trim() !== '' ? t.title : t.id;
                  return {
                    id: `track:${t.id}`,
                    kind: 'track',
                    label,
                    detail: t.cwd ?? null,
                    chip: label,
                    insert: `@\`/~${encodeURIComponent(areaId)}/${encodeURIComponent(label)}/report.md\``,
                  };
                })),
        );
      }
      if (query.kind === null || query.kind === 'template') {
        sources.push(
          runOperation(transport, { ...trackTemplatesOperation(), signal }, unauthorized)
            .then((templates) => {
              if (!signal.aborted) onTemplates?.(templates);
              return templateMentionSuggestions(templates, typed);
            }),
        );
      }
      if (memoryAreaId !== false && (typed === '' || query.kind === 'memory')) {
        sources.push(runOperation(transport, { ...memoryMentionsOperation(memoryAreaId, query.text), signal }, unauthorized)
          .then(mentions => mentions.map(memoryMentionSuggestion)));
      }
      if (sources.length === 0) return [];
      const results = await Promise.allSettled(sources);
      return results.flatMap((result) => result.status === 'fulfilled' ? [...result.value] : []);

  };
}

export function useMentionSearch(
  transport: ApiTransportPort, unauthorized: UnauthorizedChannel, areaId: string | null, trackId: string | null,
  onTemplates?: (templates: readonly TrackTemplate[]) => void,
  memoryAreaId: string | null | false = areaId,
): MentionSearch {
  return useMemo(
    () => mentionSearchOf(transport, unauthorized, areaId, trackId, onTemplates, memoryAreaId),
    [transport, unauthorized, areaId, trackId, onTemplates, memoryAreaId],
  );
}
