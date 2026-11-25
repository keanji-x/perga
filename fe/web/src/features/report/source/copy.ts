/** The source panel's words, declared once. */

import type { SourceProvenance } from '../../../../../core/domain/report-source.ts';

export const SOURCE_PROVENANCE_COPY = Object.freeze({
  full_text: 'Full text',
  summary: 'Summary (not original source)',
  web_page: 'Web page',
  manual: 'Manual entry (unverified)',
} satisfies Record<SourceProvenance, string>);

export const SOURCE_PANEL_COPY = Object.freeze({
  /** The source's name before the row has arrived. */
  panelTitle: 'Source',
  viewLabel: 'Source view',
  reading: 'Reading',
  original: 'Original',
  closeLabel: 'Close source',
  loading: 'Loading source…',
  /** A citation the track cannot answer: dangling id, or a link that will not parse. */
  missingTitle: 'Source not found',
  missingDangling: 'This source is not in this track. It may have been removed or belong to another track.',
  missingMalformed: 'This citation link is malformed and cannot be resolved.',
  destinationLabel: 'Link',
  /** The source exists but the anchor does not: the source is still shown underneath, but the notice is the missing state. */
  anchorMissingTitle: 'Citation anchor missing',
  anchorMissingDetail: 'The cited anchor was not found in this source. The full source text is shown below.',
  publishedAt: 'Published',
  capturedAt: 'Captured',
  origin: 'Origin',
  originPlugin: 'Plugin',
  originTool: 'Tool',
  contentId: 'Content ID',
  url: 'URL',
  /** The inline citation on surfaces without a Track or source-panel handler. */
  citationBadge: 'Source',
} as const);
