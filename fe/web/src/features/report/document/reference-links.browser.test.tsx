import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import '../../../styles/entry.css';
import { trackReportLinkUrl } from '../../../../../core/domain/report.ts';
import { ReportDocument } from './public.tsx';

afterEach(() => { cleanup(); document.getSelection()?.removeAllRanges(); });

it('previews a Chinese inline-code file through the existing report file resource', async () => {
  const path = '_local/2026-11-17/交付-productlist-pl_tianji1116.md';
  const report = { summary: '', body: '`tasks/green_shared/deliverables/productlist_pl_tianji1116/`，交付 md `' + path + '`，飞书 delivery 已发。', blocks: null };
  render(<ReportDocument report={report} fileRoot="/repo" empty={null} linkPreview={{ trackId: 't', report,
    files: {
      readFile: (requested) => requested === path
        ? Promise.resolve({ path, text: '# 中文交付内容', size: 20, truncated: false })
        : Promise.reject(new Error('Unexpected file path')),
      rawUrl: (value) => value,
    },
  }} />);
  expect(screen.queryByRole('button', { name: 'tasks/green_shared/deliverables/productlist_pl_tianji1116/' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: path }));
  expect(await screen.findByRole('heading', { name: '中文交付内容' })).toBeTruthy();
});

it.each([
  ['[Own repository](https://github.com/second/project/issues/1)\n\nissue #2', 'https://github.com/second/project/issues/2'],
  ['issue #2', null],
  ['[One](https://github.com/second/project/issues/1) [Two](https://github.com/third/project/issues/1)\n\nissue #2', null],
])('scopes independent file preview references to their own source: %s', async (markdown, expected) => {
  const report = { summary: '', body: '[Parent](https://github.com/first/project/issues/1)\n\n[Readme](./README.md)', blocks: null };
  render(<ReportDocument report={report} fileRoot="/repo" empty={null} linkPreview={{ trackId: 't', report,
    files: { readFile: (path) => Promise.resolve({ path, text: markdown, size: markdown.length, truncated: false }), rawUrl: (path) => path },
  }} />);
  await userEvent.click(screen.getByRole('button', { name: 'Readme' }));
  await screen.findByText('issue #2', { exact: true });
  if (expected === null) {
    expect(screen.queryByRole('button', { name: 'issue #2' })).toBeNull();
  } else {
    await userEvent.click(screen.getByRole('button', { name: 'issue #2' }));
    await waitFor(() => { expect(within(screen.getByRole('dialog', { name: 'Preview: issue #2' }))
      .getByRole('link', { name: 'Open in new tab ↗' }).getAttribute('href')).toBe(expected); });
  }
});

it('retains the owning report repository in a same-report block preview', async () => {
  const report = { summary: '', body: '', blocks: [
    { id: 'context', kind: 'prose' as const, payload: { markdown: `[Parent](https://github.com/first/project/issues/1) [Details](${trackReportLinkUrl('t')}#details)` } },
    { id: 'details', kind: 'prose' as const, payload: { markdown: 'issue #2' } },
  ] };
  render(<ReportDocument report={report} empty={null} linkPreview={{ trackId: 't', report, files: { readFile: () => Promise.reject(new Error('No file requested')), rawUrl: (path) => path } }} />);
  await userEvent.click(screen.getByRole('button', { name: 'Details' }));
  const preview = screen.getByRole('dialog', { name: 'Preview: Details' });
  await waitFor(() => { expect(within(preview).getByRole('button', { name: 'issue #2' })).toBeTruthy(); });
});
