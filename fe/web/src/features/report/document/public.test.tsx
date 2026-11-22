// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

import type { ReportFileLinkTarget } from '../../../../../core/domain/report-file.ts';
import type { ReportSourceLinkTarget } from '../../../../../core/domain/report-source.ts';
import { readTrackReport, type ReportBlock, type TrackReport } from '../../../../../core/domain/report.ts';
import { SOURCE_PANEL_COPY } from '../source/public.tsx';
import { initialBody, splitInitialBody } from './kernel-initial-body.ts';
import { ReportDocument, ReportTextLink } from './public.tsx';

afterEach(cleanup);

const EMPTY = <p>Nothing yet.</p>;

/** A v1 report: flat body, no blocks and therefore no anchors. */
function flat(body: string): TrackReport {
  return { summary: '', body, blocks: null };
}

function blocked(...blocks: ReportBlock[]): TrackReport {
  return { summary: '', body: '', blocks };
}

function prose(id: string, markdown: string): ReportBlock {
  return { id, kind: 'prose', payload: { markdown } };
}

describe('ReportDocument', () => {
  it('opens the complete Chinese inline-code file path while leaving directories literal', () => {
    const openFile = vi.fn();
    render(<ReportDocument report={flat('`tasks/green_shared/deliverables/productlist_pl_tianji1116/`，交付 md `_local/2026-11-17/交付-productlist-pl_tianji1116.md`，飞书 delivery 已发。')}
      fileRoot="/repo" onOpenFileLink={openFile} empty={EMPTY} />);
    expect(screen.queryByRole('button', { name: 'tasks/green_shared/deliverables/productlist_pl_tianji1116/' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '_local/2026-11-17/交付-productlist-pl_tianji1116.md' }));
    expect(openFile).toHaveBeenCalledWith({ path: '_local/2026-11-17/交付-productlist-pl_tianji1116.md' });
  });

  it('renders the empty state when there is no report', () => {
    render(<ReportDocument report={null} empty={EMPTY} />);
    expect(screen.getByText('Nothing yet.')).toBeTruthy();
  });

  it('renders a summary-only legacy report instead of the empty state', () => {
    render(<ReportDocument
      report={{ summary: 'Agent finished the migration.', body: '', blocks: null }}
      empty={EMPTY}
    />);
    expect(screen.getByText('Agent finished the migration.')).toBeTruthy();
    expect(screen.queryByText('Nothing yet.')).toBeNull();
  });

  it('renders H1 as a section heading below the page title, never as an h1', () => {
    const { container } = render(<ReportDocument report={flat('# Goal\n\nBody text.')} empty={EMPTY} />);
    expect(container.querySelectorAll('h1').length).toBe(0);
    expect(screen.getByRole('heading', { level: 2 }).textContent).toBe('Goal');
  });

  it('renders a tight list item inline, so its text sits on the marker line', () => {
    const { container } = render(<ReportDocument report={flat('- one\n- two')} empty={EMPTY} />);
    const items = container.querySelectorAll('li');
    expect(items.length).toBe(2);
    expect(items[0]?.querySelector('p')).toBeNull();
    expect(items[0]?.textContent).toBe('one');
  });

  it('keeps a task item checkbox disabled, because this surface does not write back', () => {
    render(<ReportDocument report={flat('- [x] done\n- [ ] open')} empty={EMPTY} />);
    const boxes = screen.getAllByRole('checkbox');
    expect(boxes.length).toBe(2);
    expect(boxes.every((box) => (box as HTMLInputElement).disabled)).toBe(true);
    expect((boxes[0] as HTMLInputElement).checked).toBe(true);
    expect((boxes[1] as HTMLInputElement).checked).toBe(false);
  });

  describe('INV-A11Y-061 — a report emits no native link', () => {
    it('keeps a link label and drops its destination', () => {
      const { container } = render(
        <ReportDocument report={flat('See [the planner](https://example.com/planner) for details.')} empty={EMPTY} />,
      );
      expect(container.querySelectorAll('a').length).toBe(0);
      expect(container.textContent).toContain('the planner');
      expect(container.innerHTML).not.toContain('example.com');
    });

    it('renders an image as its alt text and never requests the source', () => {
      const { container } = render(
        <ReportDocument report={flat('![a diagram](https://example.com/x.png)')} empty={EMPTY} />,
      );
      expect(container.querySelectorAll('img').length).toBe(0);
      expect(container.textContent).toContain('a diagram');
      expect(container.innerHTML).not.toContain('example.com');
    });

    it('routes a perga:// citation through a button and a callback, not an anchor', () => {
      const onOpenLink = vi.fn();
      const { container } = render(
        <ReportDocument
          report={flat('See [the model](perga://wave/w-2#b-3).')}
          empty={EMPTY}
          onOpenLink={onOpenLink}
        />,
      );
      expect(container.querySelectorAll('a').length).toBe(0);
      screen.getByRole('button', { name: 'the model' }).click();
      expect(onOpenLink).toHaveBeenCalledWith({ trackId: 'w-2', blockId: 'b-3' });
    });

    it('routes a workspace-relative file through a button and a typed callback', () => {
      const onOpenFileLink = vi.fn<(target: ReportFileLinkTarget) => void>();
      const { container } = render(
        <ReportDocument
          report={flat('Inspect [the transport](./fe/web/src/app/providers/transport.ts).')}
          empty={EMPTY}
          onOpenFileLink={onOpenFileLink}
          fileRoot="/repo"
        />,
      );
      expect(container.querySelectorAll('a').length).toBe(0);
      screen.getByRole('button', { name: 'the transport' }).click();
      expect(onOpenFileLink).toHaveBeenCalledWith({ path: 'fe/web/src/app/providers/transport.ts' });
    });

    it('does not turn an escaping relative path into a file control', () => {
      const onOpenFileLink = vi.fn();
      const { container } = render(
        <ReportDocument
          report={flat('Do not open [outside](../secret.txt).')}
          empty={EMPTY}
          onOpenFileLink={onOpenFileLink}
          fileRoot="/repo"
        />,
      );
      expect(container.querySelectorAll('button').length).toBe(0);
      expect(container.textContent).toContain('outside');
      expect(onOpenFileLink).not.toHaveBeenCalled();
    });

    it('accepts an absolute file link only when it is inside the injected workspace root', () => {
      const onOpenFileLink = vi.fn();
      render(<ReportDocument
        report={flat('[inside](/repo/src/main.rs:12) and [outside](/etc/passwd).')}
        empty={EMPTY}
        onOpenFileLink={onOpenFileLink}
        fileRoot="/repo"
      />);
      screen.getByRole('button', { name: 'inside' }).click();
      expect(onOpenFileLink).toHaveBeenCalledWith({ path: 'src/main.rs' });
      expect(screen.queryByRole('button', { name: 'outside' })).toBeNull();
    });

    it('resolves links relative to the Markdown file being rendered', () => {
      const onOpenFileLink = vi.fn<(target: ReportFileLinkTarget) => void>();
      render(<ReportDocument
        report={flat('[sibling](./spec.md) and [parent](../README.md).')}
        empty={EMPTY}
        onOpenFileLink={onOpenFileLink}
        fileRoot="/repo"
        fileBasePath="docs"
      />);
      screen.getByRole('button', { name: 'sibling' }).click();
      screen.getByRole('button', { name: 'parent' }).click();
      expect(onOpenFileLink.mock.calls.map(([target]) => target)).toEqual([
        { path: 'docs/spec.md' }, { path: 'README.md' },
      ]);
    });

    it('routes a source citation through a button and a typed callback, never an anchor', () => {
      const onOpenSourceLink = vi.fn<(target: ReportSourceLinkTarget) => void>();
      const { container } = render(
        <ReportDocument
          report={flat('据 [Mikko 日志](perga://source/src_2c9e0a1b#q1) 与 [年报](perga://source/src_0badf00d)。')}
          empty={EMPTY}
          onOpenSourceLink={onOpenSourceLink}
        />,
      );
      expect(container.querySelectorAll('a').length).toBe(0);
      expect(container.innerHTML).not.toContain('perga://source');
      screen.getByRole('button', { name: 'Mikko 日志' }).click();
      screen.getByRole('button', { name: '年报' }).click();
      expect(onOpenSourceLink.mock.calls.map(([target]) => target)).toEqual([
        { destination: 'perga://source/src_2c9e0a1b#q1', sourceId: 'src_2c9e0a1b', quoteId: 'q1' },
        { destination: 'perga://source/src_0badf00d', sourceId: 'src_0badf00d', quoteId: null },
      ]);
      expect(container.querySelectorAll('[data-nc-report-source-link]').length).toBe(2);
    });

    it('keeps a malformed source citation clickable so the panel can say it is missing', () => {
      const onOpenSourceLink = vi.fn<(target: ReportSourceLinkTarget) => void>();
      render(
        <ReportDocument
          report={flat('见 [坏链接](perga://source/src_dead#q0)。')}
          empty={EMPTY}
          onOpenSourceLink={onOpenSourceLink}
        />,
      );
      screen.getByRole('button', { name: '坏链接' }).click();
      expect(onOpenSourceLink).toHaveBeenCalledWith({
        destination: 'perga://source/src_dead#q0', sourceId: null, quoteId: null,
      });
    });

    it('does not hand a source citation to the track or file callbacks', () => {
      const onOpenLink = vi.fn();
      const onOpenFileLink = vi.fn();
      const onOpenSourceLink = vi.fn();
      render(
        <ReportDocument
          report={flat('[x](perga://source/src_2c9e0a1b#q1)')}
          empty={EMPTY}
          onOpenLink={onOpenLink}
          onOpenFileLink={onOpenFileLink}
          onOpenSourceLink={onOpenSourceLink}
          fileRoot="/repo"
        />,
      );
      screen.getByRole('button', { name: 'x' }).click();
      expect(onOpenSourceLink).toHaveBeenCalledTimes(1);
      expect(onOpenLink).not.toHaveBeenCalled();
      expect(onOpenFileLink).not.toHaveBeenCalled();
    });

    it('renders a source citation as a badge plus label, not a control, when no handler is injected', () => {
      const { container } = render(
        <ReportDocument report={flat('据 [Mikko 日志](perga://source/src_2c9e0a1b#q1)。')} empty={EMPTY} />,
      );
      expect(container.querySelectorAll('button, a').length).toBe(0);
      const citation = container.querySelector('[data-nc-report-source-citation]');
      expect(citation).not.toBeNull();
      expect(citation?.textContent).toBe(`${SOURCE_PANEL_COPY.citationBadge}Mikko 日志`);
      expect(container.innerHTML).not.toContain('perga://source');
    });

    it('hands a table cell citation the same source handler as the prose', () => {
      const onOpenSourceLink = vi.fn<(target: ReportSourceLinkTarget) => void>();
      const { container } = render(
        <ReportDocument
          report={blocked(
            prose('b-1', '据 [Mikko 日志](perga://source/src_2c9e0a1b#q1)。'),
            prose('b-2', '| 指标 | 来源 |\n| --- | --- |\n| 布伦特收盘 | [AP](perga://source/src_ddef99cc#q1) |'),
          )}
          empty={EMPTY}
          onOpenSourceLink={onOpenSourceLink}
        />,
      );
      expect(container.querySelectorAll('a').length).toBe(0);
      screen.getByRole('button', { name: 'AP' }).click();
      expect(onOpenSourceLink).toHaveBeenCalledWith({
        destination: 'perga://source/src_ddef99cc#q1', sourceId: 'src_ddef99cc', quoteId: 'q1',
      });
      expect(container.querySelectorAll('td [data-nc-report-source-link]').length).toBe(1);
    });

    it('renders a citation as plain text when no handler is injected', () => {
      const { container } = render(
        <ReportDocument report={flat('See [the model](perga://wave/w-2#b-3).')} empty={EMPTY} />,
      );
      expect(container.querySelectorAll('button').length).toBe(0);
      expect(container.textContent).toContain('the model');
    });
  });

  it('drops raw HTML rather than rendering it', () => {
    const { container } = render(
      <ReportDocument report={flat('<script>alert(1)</script>\n\nAfter.')} empty={EMPTY} />,
    );
    expect(container.querySelectorAll('script').length).toBe(0);
    expect(container.innerHTML).not.toContain('alert(1)');
    expect(container.textContent).toContain('After.');
  });

  it('falls back to the source when the markdown will not parse', () => {
    const body = `${'> '.repeat(80)}too deep`;
    const { container } = render(<ReportDocument report={flat(body)} empty={EMPTY} />);
    expect(container.querySelector('pre')?.textContent).toContain('too deep');
  });

  describe('typed blocks', () => {
    it('degrades a stored block of the retired view overlay kind to one line and keeps the report', () => {
      const retired = 'view.live';
      const report = readTrackReport([{
        id: 'report', kind: 'track-report', track_id: 't', title: null, sort: 0, deletable: false, created_at: 0, updated_at: 0,
        payload: { body: '', blocks: [
          { id: 'b-old', kind: retired, rev: 1, payload: { source: 'perga://plugin/operations/capacity', version: 1 } },
          { id: 'b-2', kind: 'prose', rev: 1, payload: { markdown: 'Still readable.' } },
        ] },
      }]);
      const { container } = render(<ReportDocument report={report} empty={EMPTY} />);
      expect(container.querySelector('#b-old')?.textContent).toBe(`unsupported block kind ${retired}`);
      expect(screen.getByText('Still readable.')).toBeTruthy();
    });

    it('gives each block its id, so a citation has something to land on', () => {
      const { container } = render(<ReportDocument report={blocked(
        prose('b-1', '# One'),
        { id: 'b-2', kind: 'widget', payload: { use: 'builtin:list', view: 'log' } },
      )} empty={EMPTY} />);
      expect(container.querySelector('#b-1')).toBeTruthy();
      expect(container.querySelector('#b-2')).toBeTruthy();
    });

    it('anchors headings on ids the outline can address', () => {
      const { container } = render(
        <ReportDocument report={blocked(prose('b-1', '# One\n\n## Two'))} empty={EMPTY} />,
      );
      expect(container.querySelector('#b-1-h1')?.textContent).toBe('One');
      expect(container.querySelector('#b-1-h2')?.textContent).toBe('Two');
    });

    it('degrades one unreadable block and keeps the rest of the document', () => {
      const { container } = render(<ReportDocument report={blocked(
        { id: 'b-1', kind: 'unsupported', declaredKind: 'chart.sankey' },
        prose('b-2', 'Still readable.'),
      )} empty={EMPTY} />);
      expect(container.textContent).toContain('unsupported block kind chart.sankey');
      expect(container.textContent).toContain('Still readable.');
    });

    it('marks a cited block in the sidenote, and only a cited one', () => {
      const { container } = render(<ReportDocument
        report={blocked(prose('b-1', 'Cited.'), prose('b-2', 'Not cited.'))}
        backlinkCounts={new Map([['b-1', 3]])}
        empty={EMPTY}
      />);
      expect(container.textContent).toContain('◂ 3');
      expect(container.textContent?.match(/◂/g)?.length).toBe(1);
    });
  });

  describe('a document that carries its own maintenance contract (#1185)', () => {
    /* Read in `beforeAll`, not at module scope: `kernel-initial-body.ts` must never touch the filesystem at import time. */
    let CONTRACT: string;
    let SECTIONS: string[];
    beforeAll(() => {
      [CONTRACT, ...SECTIONS] = splitInitialBody();
    });

    it('the fixture really is the kernel skeleton', () => {
      expect(CONTRACT.startsWith('<!-- neige:contract ')).toBe(true);
      expect(CONTRACT).toContain('<!-- 报告维护契约');
      expect(CONTRACT.endsWith('-->\n\n')).toBe(true);
      expect(CONTRACT).toContain('散文正文');
      expect(SECTIONS.map((s) => s.split('\n')[0]))
        .toEqual(['# 概要', '# 待你定', '# 已完成', '# 决策']);
    });

    it('renders neither the contract nor a row for its block', () => {
      const { container } = render(<ReportDocument
        report={blocked(prose('b_1', CONTRACT), prose('b_2', `${SECTIONS[0]}本轮结论。\n`))}
        empty={EMPTY}
      />);
      expect(container.textContent).not.toContain('报告维护契约');
      expect(container.innerHTML).not.toContain('报告维护契约');
      expect(container.innerHTML).not.toContain('neige:contract');
      expect(container.textContent).not.toContain('散文正文');
      expect(container.innerHTML).not.toContain('散文正文');
      expect(container.querySelector('#b_1')?.childNodes.length).toBe(0);
      expect(container.textContent).toContain('概要');
      expect(container.textContent).toContain('本轮结论。');
    });

    it('drops the contract on the v1 flat-body path too', () => {
      const { container } = render(<ReportDocument report={flat(initialBody())} empty={EMPTY} />);
      expect(container.textContent).not.toContain('报告维护契约');
      expect(container.innerHTML).not.toContain('报告维护契约');
      expect(container.innerHTML).not.toContain('neige:contract');
      expect(container.textContent).not.toContain('散文正文');
      expect(container.innerHTML).not.toContain('散文正文');
      expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent))
        .toEqual(['概要', '待你定', '已完成', '决策']);
    });
  });
});

it('shares destination admission through the public link capability and preserves inert labels', () => {
  const open = vi.fn();
  const { rerender } = render(<ReportTextLink href="./notes.md" label="Notes" referenceRepository={null}
    fileRoot="/workspace" onOpenFileLink={open}><strong>Notes</strong></ReportTextLink>);
  screen.getByRole('button', { name: 'Notes' }).click();
  expect(open).toHaveBeenCalledExactlyOnceWith({ path: 'notes.md' });
  rerender(<ReportTextLink href="/outside/private" label="Notes" referenceRepository={null}
    fileRoot="/workspace" onOpenFileLink={open}><strong>Notes</strong></ReportTextLink>);
  expect(screen.queryByRole('button')).toBeNull();
  expect(screen.queryByRole('link')).toBeNull();
  rerender(<ReportTextLink href="./notes.md" label="Notes" referenceRepository={null}
    destinationMode="inert" fileRoot="/workspace" onOpenFileLink={open}><strong>Notes</strong></ReportTextLink>);
  expect(screen.queryByRole('button')).toBeNull();
  expect(screen.getByText('Notes').tagName).toBe('STRONG');
  expect(open).toHaveBeenCalledTimes(1);
});

it('renders inline and display formulas in report prose while keeping code literal', () => {
  const { container } = render(<ReportDocument report={flat('Inline $x^2$.\n\n$$\n\\frac{1}{2}\n$$\n\n`$x$`')} empty={EMPTY} />);
  expect(container.querySelectorAll('math')).toHaveLength(2);
  expect(container.querySelector('code')?.textContent).toBe('$x$');
});

it('renders prose blocks strictly read-only without any Edit button', () => {
  render(<ReportDocument report={{ ...blocked(prose('readonly', 'Read-only publication prose')), docRev: 1 }} empty={EMPTY} />);
  expect(screen.getByText('Read-only publication prose')).toBeTruthy();
  expect(screen.queryByRole('button', { name: /Edit/i })).toBeNull();
});
