import { z } from 'zod';

/* Backlinks: the kernel resolves `perga://wave/<id>#<block>` links in other tracks' reports. */

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
