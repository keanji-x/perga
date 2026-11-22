// The track page's preview resolver (#1780): one polled read of the track's registered
// previews, answered per `preview` block by its `key`. Only a report that has a preview
// block polls at all.

import { type UseQueryResult } from '@tanstack/react-query';
import { useMemo } from 'react';

import { readFailureOf } from '../../../../core/domain/read-failure.ts';
import type { PreviewResolution, TrackPreviews } from '../../../../core/domain/report-backlinks.ts';
import type { PreviewViewportStore } from '../../features/report/preview/public.tsx';
import { useUiPreferences } from '../providers/ui-preferences.tsx';

export type PreviewResolver = (key: string) => PreviewResolution;

/** Data wins while it exists: a failed poll after a good read keeps the last answer on screen. */
export function previewResolutionOf(
  result: Pick<UseQueryResult<TrackPreviews>, 'data' | 'isError' | 'error'>, key: string,
): PreviewResolution {
  if (result.data !== undefined) {
    const preview = result.data.previews.find((candidate) => candidate.key === key);
    return preview === undefined ? { status: 'missing' } : { status: 'registered', preview };
  }
  if (result.isError) {
    return { status: 'error', failure: readFailureOf(result.error) };
  }
  return { status: 'loading' };
}

/** The track's preview blocks remember the reader's device choice in the display preferences, per track and key. */
export function useReportPreviewViewports(trackId: string): PreviewViewportStore {
  const preferences = useUiPreferences();
  return useMemo(() => ({
    read: (key: string) => preferences.previewViewport(trackId, key),
    write: (key: string, value: string) => { preferences.setPreviewViewport(trackId, key, value); },
  }), [preferences, trackId]);
}
