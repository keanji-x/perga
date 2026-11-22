import { z } from 'zod';
import type { ReportDocumentDto } from '../api/perga-v1.js';
import { extractOutline, markdownExcerpt, parse, REPORT_MAX_DEPTH, reportHeadingIdPolicy } from '../markdown/public.js';
import { widgetBlockPayloadSchema, type WidgetBlockPayload } from './report-widget.js';

/** Client registry kind for the headless report presentation. */
export const TRACK_REPORT_CARD_KIND = 'track-report';

export type ReportBlock =
  | Readonly<{ id: string; kind: 'prose'; payload: { markdown: string } }>
  | Readonly<{ id: string; kind: 'widget'; payload: WidgetBlockPayload }>
  | Readonly<{ id: string; kind: 'unsupported'; declaredKind: string }>;

const blockWireSchema = z.object({
  id: z.string().min(1),
  kind: z.string(),
  rev: z.number().int().nonnegative().optional(),
  markdown: z.string().nullish(),
  payload: z.unknown().optional(),
});

const proseBlockPayloadSchema = z.object({ markdown: z.string() });

function toReportBlock(wire: z.infer<typeof blockWireSchema>): ReportBlock {
  const { id, kind, markdown } = wire;
  if (kind === 'prose') {
    const parsed = proseBlockPayloadSchema.safeParse(wire.payload ?? { markdown });
    if (parsed.success) return { id, kind, payload: parsed.data };
  }
  if (kind === 'widget') {
    let payload = wire.payload;
    if (payload == null && markdown != null) {
      try { payload = JSON.parse(markdown); } catch { /* unreadable payload */ }
    }
    const parsed = widgetBlockPayloadSchema.safeParse(payload);
    if (parsed.success) return { id, kind, payload: parsed.data };
  }
  return { id, kind: 'unsupported', declaredKind: kind };
}

/** A deliberately narrow read: `schemaVersion` stays unparsed so a newer payload stays readable. */
export const trackReportPayloadSchema = z.object({
  pergaSnapshot: z.boolean().optional(),
  docRev: z.number().int().nonnegative().optional(),
  summary: z.string().default(''),
  body: z.string().default(''),
  blocks: z.unknown().nullish(),
});

export type TrackReport = Readonly<{
  docRev?: number;
  blockRevs?: Readonly<Record<string, number>>;
  summary: string;
  /** Markdown source — the flat projection, and the only content a v1 row has. */
  body: string;
  /** `null` on a v1 row: blocks were not persisted, so `body` is all there is. */
  blocks: readonly ReportBlock[] | null;
}>;

/** The aggregate report is a document, independent of Workbench cards. */
export function readReportDocument(document: ReportDocumentDto): TrackReport | null {
  return readTrackReportPayload({ ...document, pergaSnapshot: true });
}

/** Shared resource and owner cards consume the same block parser. */
export function readTrackReportPayload(payload: unknown): TrackReport | null {
  const parsed = trackReportPayloadSchema.safeParse(payload);
  if (!parsed.success) return null;
  const summary = parsed.data.summary.trim();
  const body = parsed.data.body.trim();
  const blockRevs: Record<string, number> = {};
  // One malformed block must cost only that block.
  const blocks = Array.isArray(parsed.data.blocks)
    ? parsed.data.blocks.flatMap((candidate) => {
      const wire = blockWireSchema.safeParse(candidate);
      if (!wire.success) return [];
      if (wire.data.rev !== undefined) blockRevs[wire.data.id] = wire.data.rev;
      return [toReportBlock(wire.data)];
    })
    : null;
  const isPerga = parsed.data.pergaSnapshot === true;
  const allBlankProse = blocks !== null
    && blocks.length > 0
    && blocks.every((block) => block.kind === 'prose' && block.payload.markdown.trim() === '');
  // An empty blocks array (or untouched blank Perga seed prose) is the same emptiness as a blank body.
  if (summary === '' && body === '' && (blocks === null || blocks.length === 0 || (isPerga && allBlankProse))) {
    return null;
  }
  return {
    ...(isPerga && parsed.data.docRev !== undefined ? { docRev: parsed.data.docRev, blockRevs } : {}),
    summary,
    body,
    blocks,
  };
}

/* Outline: the shape is decided by the blocks, not by the headings. */

export type ReportOutlineChild = Readonly<{ blockId: string; label: string }>;
export type ReportOutlineItem = Readonly<{
  /** The anchor to scroll to: a heading id inside a prose block, or a block id. */
  blockId: string;
  label: string;
  /** Visible prose owned by this section; empty when it has no body text. */
  excerpt: string;
  /** Continuous across blocks; `null` for a non-prose block with no section above it. */
  number: number | null;
  children: readonly ReportOutlineChild[];
}>;

/**
 * Derive the outline: every H1 is a numbered top-level item, an H2 hangs beneath the H1 above
 * it (or stays top-level with no preceding H1), and a non-prose block hangs under the numbered
 * section above it or becomes an unnumbered top-level item.
 */
export function deriveReportOutline(blocks: readonly ReportBlock[] | null, readMarkdown: typeof parse = parse): ReportOutlineItem[] {
  if (blocks === null) return [];
  const outline: ReportOutlineItem[] = [];
  let sectionNumber = 0;
  let lastNumbered: { children: ReportOutlineChild[] } | null = null;
  let lastH1: { children: ReportOutlineChild[] } | null = null;

  for (const block of blocks) {
    if (block.kind === 'prose') {
      const parsed = readMarkdown(block.payload.markdown);
      if (parsed.status === 'failed') continue;
      const headings = extractOutline([{ context: { blockId: block.id }, ast: parsed.value }], {
        maxDepth: REPORT_MAX_DEPTH,
        headingId: reportHeadingIdPolicy,
        textPolicy: 'non-empty-heading-label',
        referenceText: 'visible',
        traversal: 'recursive',
      });
      const boundary = headings.find(heading => heading.depth === 1 || lastH1 === null);
      const continuation = markdownExcerpt(block.payload.markdown.slice(0, boundary?.position.start.offset), readMarkdown);
      const previous = outline.at(-1);
      if (lastNumbered !== null && previous !== undefined && continuation !== '') {
        outline[outline.length - 1] = { ...previous, excerpt: `${previous.excerpt} ${continuation}`.trim() };
      }
      for (const [index, heading] of headings.entries()) {
        const next = headings.slice(index + 1).find(candidate => candidate.depth <= heading.depth);
        const section = block.payload.markdown.slice(heading.position.end.offset, next?.position.start.offset);
        const excerpt = markdownExcerpt(section, readMarkdown);
        if (heading.depth === 2 && lastH1 !== null) {
          lastH1.children.push({ blockId: heading.id, label: heading.text });
          continue;
        }
        sectionNumber += 1;
        const children: ReportOutlineChild[] = [];
        outline.push({ blockId: heading.id, label: heading.text, excerpt, number: sectionNumber, children });
        lastNumbered = { children };
        if (heading.depth === 1) lastH1 = { children };
      }
      continue;
    }
    const label = block.kind === 'widget' ? block.payload.title || block.payload.use : block.declaredKind;
    const child: ReportOutlineChild = { blockId: block.id, label };
    if (lastNumbered === null) {
      outline.push({ blockId: block.id, label: child.label, excerpt: '', number: null, children: [] });
      continue;
    }
    lastNumbered.children.push(child);
  }
  return outline;
}

const TRACK_REPORT_LINK_PREFIX = 'perga://wave/';
const TRACK_REPORT_LINK_TARGET = /^([^/?#]+)(?:#([^#]+))?$/;

export function trackReportLinkUrl(trackId: string): string {
  return `${TRACK_REPORT_LINK_PREFIX}${encodeURIComponent(trackId)}`;
}

/** Block ids the kernel mints. A link whose fragment is not one of these keeps the track and drops the fragment. */
const BLOCK_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

export type ReportLinkTarget = Readonly<{ trackId: string; blockId: string | null }>;

export function parseReportLink(destination: string): ReportLinkTarget | null {
  if (!destination.startsWith(TRACK_REPORT_LINK_PREFIX)) return null;
  const match = TRACK_REPORT_LINK_TARGET.exec(destination.slice(TRACK_REPORT_LINK_PREFIX.length));
  if (match === null) return null;
  const trackId = match[1] ?? '';
  const blockId = match[2];
  if (trackId === '') return null;
  let decodedTrackId = trackId;
  try {
    decodedTrackId = decodeURIComponent(trackId);
  } catch {
    // Agent-written links must remain navigable even when an escape is malformed.
  }
  return {
    trackId: decodedTrackId,
    blockId: blockId !== undefined && BLOCK_ID_PATTERN.test(blockId) ? blockId : null,
  };
}
