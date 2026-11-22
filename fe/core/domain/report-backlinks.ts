import { z } from 'zod';
import type { ApiFailure } from '../api/types.js';

/* Backlinks: the kernel resolves `neige://wave/<id>#<block>` links in other tracks' reports. */

export const backlinkQuoteSchema = z.object({
  before: z.string(),
  label: z.string(),
  after: z.string(),
  head_elided: z.boolean(),
  tail_elided: z.boolean(),
});

export const trackBacklinkSchema = z.object({
  src_track_id: z.string(),
  src_track_title: z.string(),
  src_block_id: z.string(),
  dst_block_id: z.string().nullish(),
  label: z.string(),
  quote: backlinkQuoteSchema.nullish(),
  updated_at: z.number(),
});

/** `truncated` and `skipped_sources` are read, not dropped: a knowingly incomplete list must say so. */
export const trackBacklinksSchema = z.object({
  backlinks: z.array(trackBacklinkSchema),
  truncated: z.boolean().default(false),
  skipped_sources: z.number().default(0),
});

export type BacklinkQuote = z.infer<typeof backlinkQuoteSchema>;
export type TrackBacklink = z.infer<typeof trackBacklinkSchema>;
export type TrackBacklinks = z.infer<typeof trackBacklinksSchema>;

/* A track's registered previews (`GET /api/tracks/{id}/previews`, #1780). In-memory on the
   kernel: a restart empties the list until the Planner registers again. */
export const trackPreviewSchema = z.object({
  key: z.string(),
  title: z.string(),
  /** The gateway pool port the browser loads. */
  port: z.number().int().min(1).max(65535),
  /** Whether the dev server behind it accepted a connection just now. */
  live: z.boolean(),
});

export const trackPreviewsSchema = z.object({ previews: z.array(trackPreviewSchema) });

export type TrackPreview = z.infer<typeof trackPreviewSchema>;
export type TrackPreviews = z.infer<typeof trackPreviewsSchema>;

/** What a `preview` block knows about its key: still loading, unreadable, not registered, or registered. */
export type PreviewResolution =
  | Readonly<{ status: 'loading' }>
  | Readonly<{ status: 'error'; failure: ApiFailure | null }>
  | Readonly<{ status: 'missing' }>
  | Readonly<{ status: 'registered'; preview: TrackPreview }>;

/** Group by source track, preserving server order. */
export function groupBacklinks(
  backlinks: readonly TrackBacklink[],
  currentTrackId: string,
): readonly Readonly<{ trackId: string; title: string; entries: readonly TrackBacklink[] }>[] {
  const groups = new Map<string, { trackId: string; title: string; entries: TrackBacklink[] }>();
  for (const backlink of backlinks) {
    const group = groups.get(backlink.src_track_id);
    if (group !== undefined) {
      group.entries.push(backlink);
      continue;
    }
    groups.set(backlink.src_track_id, {
      trackId: backlink.src_track_id,
      title: backlink.src_track_id === currentTrackId
        ? 'This track (self-reference)'
        : backlink.src_track_title,
      entries: [backlink],
    });
  }
  return [...groups.values()];
}

/** How many backlinks land on each block, for the sidenote markers. */
export function backlinkCountsByBlock(
  backlinks: readonly TrackBacklink[],
): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const backlink of backlinks) {
    const target = backlink.dst_block_id;
    if (target === null || target === undefined || target === '') continue;
    counts.set(target, (counts.get(target) ?? 0) + 1);
  }
  return counts;
}
