import { describe,expect,it } from 'vitest';

import { backlinkCountsByBlock, groupBacklinks, type TrackBacklink } from './report-backlinks.js';
import { deriveReportOutline, parseReportLink, readTrackReport, TRACK_REPORT_CARD_KIND } from './report.js';
import type { CardWire } from './track.js';

function card(overrides: Partial<CardWire> = {}): CardWire {
  return {
    id: 'c1', track_id: 'w1', kind: TRACK_REPORT_CARD_KIND, title: null, sort: 0,
    payload: {}, deletable: false, created_at: 0, updated_at: 0,
    ...overrides,
  };
}

function prose(id: string, markdown: string) {
  return { id, kind: 'prose', rev: 1, payload: { markdown } };
}

describe('readTrackReport', () => {
  it('reads summary and body out of the track-report card', () => {
    const cards = [
      card({ id: 'other', kind: 'codex', payload: { body: 'not this one' } }),
      card({ payload: { schemaVersion: 3, docRev: 7, summary: 'One line', body: '# Goal\n\nDo the thing.' } }),
    ];
    expect(readTrackReport(cards)).toEqual({ summary: 'One line', body: '# Goal\n\nDo the thing.', blocks: null });
  });

  it('keeps a report whose summary is written while its body is blank', () => {
    expect(readTrackReport([card({ payload: { summary: 'Agent finished the migration.', body: '  ' } })]))
      .toEqual({ summary: 'Agent finished the migration.', body: '', blocks: null });
  });

  it('ignores fields it does not render, including ones it has never seen', () => {
    const cards = [card({ payload: { schemaVersion: 9, docRev: 1, summary: 's', body: 'b', future: {} } })];
    expect(readTrackReport(cards)?.body).toBe('b');
  });

  it.each([
    ['no report card at all', [card({ kind: 'codex' })]],
    ['a payload that is not an object', [card({ payload: 'nope' })]],
    ['the untouched payload a fresh track carries', [card({ payload: {} })]],
    ['a body that is only whitespace', [card({ payload: { body: '   \n  ' } })]],
    ['a blank body next to an empty blocks array', [card({ payload: { body: '', blocks: [] } })]],
  ])('reads null for %s', (_label, cards) => {
    expect(readTrackReport(cards as readonly CardWire[])).toBeNull();
  });

  it('does not distinguish a fresh track from an unreadable payload', () => {
    expect(readTrackReport([card({ payload: {} })]))
      .toEqual(readTrackReport([card({ payload: 42 })]));
  });

  it('reads the typed blocks, which are what carries each block id', () => {
    const report = readTrackReport([card({
      payload: {
        body: '# Goal',
        blocks: [
          prose('b-1', '# Goal'),
          { id: 'b-2', kind: 'widget', rev: 3, payload: { use: 'builtin:flow', view: 'flow' } },
        ],
      },
    })]);
    expect(report?.blocks?.map((block) => [block.id, block.kind]))
      .toEqual([['b-1', 'prose'], ['b-2', 'widget']]);
  });

  it('drops one wire-invalid block while keeping the other blocks', () => {
    const report = readTrackReport([card({
      payload: {
        body: '# Goal',
        blocks: [prose('b-1', '# Goal'), { id: '', kind: 'prose', payload: {} }, prose('b-2', 'Still here.')],
      },
    })]);
    expect(report?.blocks?.map((block) => block.id)).toEqual(['b-1', 'b-2']);
  });

  it('falls back to body when blocks is not an array', () => {
    expect(readTrackReport([card({ payload: { body: '# Goal', blocks: 'nope' } })]))
      .toEqual({ summary: '', body: '# Goal', blocks: null });
  });

  it.each(['checkpoints', 'chart.candles', 'chart.series', 'view', 'app', 'preview', 'objective', 'table', 'html', 'task'])(
    'preserves the anchor of a historical %s block',
    kind => {
      const report = readTrackReport([card({ payload: { blocks: [
        { id: 'old', kind, rev: 3, payload: { title: 'Old block' } },
        prose('current', '# Current'),
      ] } })]);
      expect(report?.blocks).toEqual([
        { id: 'old', kind: 'unsupported', declaredKind: kind },
        { id: 'current', kind: 'prose', payload: { markdown: '# Current' } },
      ]);
    },
  );

  it('decodes a widget from the Host markdown field when payload is absent', () => {
    const payload = { use: 'builtin:flow', view: 'flow' };
    const report = readTrackReport([card({ payload: { blocks: [{ id: 'flow', kind: 'widget', markdown: JSON.stringify(payload) }] } })]);
    expect(report?.blocks).toEqual([{ id: 'flow', kind: 'widget', payload }]);
  });
});

describe('deriveReportOutline', () => {
  it('previews section prose without markdown syntax or content from the next section', () => {
    const blocks = readTrackReport([card({ payload: { body: 'x', blocks: [
      prose('b-1', '# One\n\nThe **first** [answer](https://example.com).\n\n# Two\n\nSecond answer.'),
    ] } })])?.blocks ?? null;
    expect(deriveReportOutline(blocks).map(item => item.excerpt)).toEqual([
      'The first answer.', 'Second answer.',
    ]);
  });

  it('assembles a section across continuation blocks and stops before the next heading', () => {
    const blocks = readTrackReport([card({ payload: { body: 'x', blocks: [
      prose('b-1', '# Findings'),
      prose('b-2', 'The actual findings live in this continuation block.'),
      prose('b-3', 'More **evidence** before the next section.\n\n# Next section\n\nUnrelated details.'),
    ] } })])?.blocks ?? null;
    expect(deriveReportOutline(blocks).map(item => item.excerpt)).toEqual([
      'The actual findings live in this continuation block. More evidence before the next section.',
      'Unrelated details.',
    ]);
  });

  it('leaves an empty section empty instead of borrowing the next section', () => {
    const blocks = readTrackReport([card({ payload: { body: 'x', blocks: [
      prose('b-1', '# Empty\n\n# Filled\n\nIts own answer.'),
    ] } })])?.blocks ?? null;
    expect(deriveReportOutline(blocks).map(item => item.excerpt)).toEqual(['', 'Its own answer.']);
  });

  it('keeps H1 sections at the top level and hangs H2 headings beneath them', () => {
    const outline = deriveReportOutline(readTrackReport([card({
      payload: {
        body: 'x',
        blocks: [prose('b-1', '# One\n\ntext\n\n## Two\n'), prose('b-2', '# Three\n')],
      },
    })])?.blocks ?? null);
    expect(outline.map((item) => [item.number, item.label, item.blockId])).toEqual([
      [1, 'One', 'b-1-h1'],
      [2, 'Three', 'b-2-h1'],
    ]);
    expect(outline[0]?.children).toEqual([{ blockId: 'b-1-h2', label: 'Two' }]);
  });

  it('hangs a non-prose block under the section above it, as evidence rather than a section', () => {
    const outline = deriveReportOutline(readTrackReport([card({
      payload: {
        body: 'x',
        blocks: [
          prose('b-1', '# Valuation\n'),
          { id: 'b-2', kind: 'widget', rev: 1, payload: { use: 'builtin:list', view: 'prices', title: '600519' } },
        ],
      },
    })])?.blocks ?? null);
    expect(outline).toHaveLength(1);
    expect(outline[0]?.children).toEqual([{ blockId: 'b-2', label: '600519' }]);
  });

  it('promotes a leading non-prose block to an unnumbered top-level item', () => {
    const outline = deriveReportOutline(readTrackReport([card({
      payload: {
        body: 'x',
        blocks: [
          { id: 'b-1', kind: 'widget', rev: 1, payload: { use: 'builtin:table', view: 'comparables', title: 'Comparables' } },
          prose('b-2', '# After\n'),
        ],
      },
    })])?.blocks ?? null);
    expect(outline.map((item) => [item.number, item.label])).toEqual([
      [null, 'Comparables'],
      [1, 'After'],
    ]);
  });

  it('keeps H2 headings navigable when the report has no preceding H1 and ignores H3', () => {
    const outline = deriveReportOutline(readTrackReport([card({
      payload: { body: 'x', blocks: [prose('b-1', '## One\n\n### Deep\n\n## Two\n')] },
    })])?.blocks ?? null);
    expect(outline.map((item) => item.label)).toEqual(['One', 'Two']);
  });

  it('is empty for a v1 report, which has no block ids to anchor to', () => {
    expect(deriveReportOutline(null)).toEqual([]);
  });
});

describe('backlinks', () => {
  const backlink = (overrides: Partial<TrackBacklink> = {}): TrackBacklink => ({
    src_track_id: 'w-2', src_track_title: 'Cash flow model', src_block_id: 'b-9',
    dst_block_id: 'b-1', label: 'valuation', quote: null, updated_at: 0, ...overrides,
  });

  it('groups by source track and names a self-reference as such', () => {
    const groups = groupBacklinks(
      [backlink(), backlink({ src_block_id: 'b-10' }), backlink({ src_track_id: 'w-1' })],
      'w-1',
    );
    expect(groups.map((group) => [group.trackId, group.title, group.entries.length])).toEqual([
      ['w-2', 'Cash flow model', 2],
      ['w-1', 'This track (self-reference)', 1],
    ]);
  });

  it('counts backlinks per target block and ignores whole-track citations', () => {
    const counts = backlinkCountsByBlock([
      backlink(), backlink({ src_block_id: 'b-11' }), backlink({ dst_block_id: null }),
    ]);
    expect([...counts]).toEqual([['b-1', 2]]);
  });
});

describe('parseReportLink', () => {
  it('resolves a track link with a block fragment', () => {
    expect(parseReportLink('perga://wave/w-2#b-1')).toEqual({ trackId: 'w-2', blockId: 'b-1' });
  });

  it('resolves a track link without a fragment', () => {
    expect(parseReportLink('perga://wave/w-2')).toEqual({ trackId: 'w-2', blockId: null });
  });

  it('keeps a malformed percent escape as a usable raw track target', () => {
    expect(() => parseReportLink('perga://wave/%E0%A4%A')).not.toThrow();
    expect(parseReportLink('perga://wave/%E0%A4%A'))
      .toEqual({ trackId: '%E0%A4%A', blockId: null });
  });

  it('drops a malformed fragment but keeps the track', () => {
    expect(parseReportLink('perga://wave/w-2#../../etc/passwd'))
      .toEqual({ trackId: 'w-2', blockId: null });
  });

  it.each([
    ['a plain http url', 'https://example.com'],
    ['another neige noun', 'perga://area/c-1'],
    ['a javascript url', 'javascript:alert(1)'],
  ])('reads null for %s, which the renderer then shows as plain text', (_label, url) => {
    expect(parseReportLink(url)).toBeNull();
  });
});
