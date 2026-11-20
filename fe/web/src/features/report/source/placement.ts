import type { ReactNode } from 'react';

export type ReportSourcePosition = Readonly<{ blockId: string; markdownBlock: number | null }>;
export type ReportSourceInsertion = Readonly<{ position: ReportSourcePosition | null; content: ReactNode }>;

export function reportSourcePosition(opener: HTMLElement | null): ReportSourcePosition | null {
  const blockId = opener?.closest('[data-nc-report-source-block]')?.getAttribute('data-nc-report-source-block');
  if (blockId == null) return null;
  const markdownBlock = opener?.closest('[data-nc-report-markdown-block]')?.getAttribute('data-nc-report-markdown-block');
  return { blockId, markdownBlock: markdownBlock == null ? null : Number(markdownBlock) };
}
