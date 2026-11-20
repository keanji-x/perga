import { fixtureTrack, fixtureSession, fixtureSnapshot, fixtureReport } from './session-contract-fixture.ts';
import type { SessionItemsPageDto } from '../../../../core/api/perga-v1.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import '../../styles/entry.css';
import { page, userEvent } from 'vitest/browser';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiFailure, ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { SOURCE_PANEL_COPY } from '../../features/report/source/public.tsx';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { sourceResolutionOf } from './report-source.tsx';
import { trackReportLinkUrl } from '../../../../core/domain/report.ts';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';

const BOOM: ApiFailure = Object.freeze({ kind: 'http', status: 500, code: 'internal', message: 'boom' });

const AREA = { id: 'c1', name: 'Work', color: '#000', sort: 1, kind: 'user', created_at: 1, updated_at: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Rates', cwd: '/tmp' });
const PLANNER_CARD = fixtureSession('card-1', 'w1', { title: 'Planner chat', isLead: true });
const REPORT = { docRev: 1, summary: '', leadingComment: '', blocks: [{ id: 'b_1', rev: 1, kind: 'prose', markdown: '央行加息在即（[Mikko 日志](neige://source/src_2c9e0a1b#q1)），另见[旧引用](neige://source/src_0badf00d)与[未追加的锚点](neige://source/src_2c9e0a1b#q7)。', payload: null }] };
const SOURCE_ROW = {
  source_id: 'src_2c9e0a1b', provenance: 'summary',
  origin: { kind: 'plugin', plugin_id: 'mcp-wisburg', tool: 'get_report_detail', args_sha256: 'ab', args_canon: 'v1', content_id: '752972' },
  title: 'Mikko 全球市场日志 9-13', published_at: '2026-09-13', content_id: '752972',
  body_bytes: 60, body_sha256: 'cd', captured_at: '2026-09-14T08:00:00Z',
  quotes: [{ id: 'q1', text: '9月加息概率接近九成', start: 15, end: 45 }],
  body: '央行表示，9月加息概率接近九成。\n\n市场普遍同意。',
};
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });

function sessionPage(running = false): SessionItemsPageDto {
  return { session: { ...PLANNER_CARD, status: running ? 'running' : 'idle' }, items: [], state: running ? 'running' : 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
    activeTurn: running ? { id: 'source-turn', sessionId: PLANNER_CARD.id, turnIndex: 0, epoch: 1,
      triggerKind: 'user', status: 'running', startedAtMs: Date.now() } : null };
}

function ok(body: unknown): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body };
}

type Reply = (request: ApiRequest) => ApiTransportResponse | undefined | Promise<ApiTransportResponse | undefined>;

function setup(reply?: Reply) {
  const requests: ApiRequest[] = [];
  const transport: ApiTransportPort = {
    send(request) {
      requests.push(request);
      return Promise.resolve(answer(request));
    },
  };
  async function answer(request: ApiRequest): Promise<ApiTransportResponse> {
    const scripted = await reply?.(request);
    if (scripted) return scripted;
    if (request.path === '/api/v1/areas') return ok([AREA]);
    if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([TRACK]);
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD], REPORT));
    if (request.path === '/api/tracks/w1/sources/src_2c9e0a1b') return ok(SOURCE_ROW);
    if (request.path.startsWith('/api/tracks/w1/sources/')) {
      return { status: 404, statusText: 'Not Found', body: { error: 'source not found', code: 'not_found' } };
    }
    if (request.path.startsWith('/api/v1/sessions/card-1/items?')) return ok(sessionPage());
    if (request.path === '/api/settings') return ok({});
    return ok([]);
  }
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), unauthorized, client, cards: bootTestCardRuntime(), onSignOut: vi.fn() });
  router.update({ history: createMemoryHistory({ initialEntries: ['/track/w1'] }) });
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>);
  return { requests };
}

const scrollIntoView = vi.fn();
beforeEach(async () => {
  await page.viewport(1400, 900);
  Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: scrollIntoView });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); scrollIntoView.mockReset(); });

describe('sourceResolutionOf', () => {
  it('reads data first — found or missing — then the error, then loading', () => {
    expect(sourceResolutionOf({ data: { status: 'missing' }, isError: false, error: null })).toEqual({ status: 'missing' });
    expect(sourceResolutionOf({ data: { status: 'found', source: SOURCE_ROW as never }, isError: true, error: new Error('later') }))
      .toEqual({ status: 'ok', source: SOURCE_ROW });
    expect(sourceResolutionOf({
      data: undefined, isError: true,
      error: new ApiError(BOOM),
    })).toEqual({ status: 'error', failure: BOOM });
    expect(sourceResolutionOf({ data: undefined, isError: false, error: null })).toEqual({ status: 'loading' });
  });
});

describe('the source panel on the track page', () => {
  it('retries an unreadable referenced report inside the hover card', async () => {
    let reads = 0;
    setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: `[Report](${trackReportLinkUrl('w2')})`, payload: null }] }));
      if (request.path !== '/api/v1/tracks/w2') return undefined;
      reads++;
      return reads === 1 ? { status: 500, statusText: 'Error', body: { error: 'Report read failed' } }
        : ok(fixtureSnapshot({ ...TRACK, id: 'w2' }, [], REPORT));
    });
    const link = await screen.findByRole('button', { name: 'Report' }, { timeout: 5_000 });
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Report' });
    await within(preview).findByRole('alert');
    fireEvent.click(within(preview).getByRole('button', { name: 'Retry' }));
    await within(preview).findByRole('button', { name: 'Mikko 日志' });
    expect(reads).toBe(2);
  });

  it('previews and opens another track file using its declared worktree root', async () => {
    const { requests } = setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: `[Other report](${trackReportLinkUrl('w2')})`, payload: null }] }));
      if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot({ ...TRACK, id: 'w2', cwd: '/base', workspaceDriver: 'git-worktree', workspaceSpec: { worktreePath: '/owned-worktree' } }, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: '[Scoped notes](/owned-worktree/docs/notes.md)', payload: null }] }));
      if (request.path === '/api/v1/tracks/w2/files?path=docs%2Fnotes.md') return ok({
        relativePath: 'docs/notes.md', isDirectory: false, entries: [], truncated: false, content: '# Other workspace file',
      });
    });
    const link = await screen.findByRole('button', { name: 'Other report' }, { timeout: 5_000 });
    fireEvent.pointerEnter(link.parentElement!);
    const report = await screen.findByRole('dialog', { name: 'Preview: Other report' });
    const fileLink = await within(report).findByRole('button', { name: 'Scoped notes' });
    fireEvent.pointerEnter(fileLink.parentElement!);
    const file = await screen.findByRole('dialog', { name: 'Preview: Scoped notes' });
    await within(file).findByRole('heading', { name: 'Other workspace file' });
    fireEvent.click(within(file).getByRole('button', { name: 'Open in workspace' }));
    const viewer = await screen.findByRole('region', { name: 'File docs/notes.md' });
    await within(viewer).findByRole('heading', { name: 'Other workspace file' });
    expect(requests.some(request => request.path.includes('/api/v1/tracks/w1/files'))).toBe(false);
  });

  it('previews and opens evidence linked from a workspace Markdown file', async () => {
    const { requests } = setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: '[Notes](./notes.md)', payload: null }] }));
      if (request.path === '/api/v1/tracks/w1/files?path=notes.md') return ok({ relativePath: 'notes.md', isDirectory: false, entries: [], truncated: false,
        content: '[File evidence](neige://source/src_2c9e0a1b#q1)' });
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Notes' }, { timeout: 5_000 }));
    const file = await screen.findByRole('region', { name: 'File notes.md' });
    const link = await within(file).findByRole('button', { name: 'File evidence' });
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: File evidence' });
    await within(preview).findByText('智堡摘要，非机构原文');
    fireEvent.click(within(preview).getByRole('button', { name: 'Open in workspace' }));
    const source = await within(file).findByRole('region', { name: SOURCE_ROW.title });
    expect(source.querySelector('mark')?.textContent).toBe('9月加息概率接近九成');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Preview: File evidence' })).toBeNull());
    expect(requests.every(request => request.method === 'GET')).toBe(true);
  });

  it.each(['empty', 'missing block', 'table block'])('renders the %s state of a report reference', async mode => {
    const { requests } = setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: `[Report](${trackReportLinkUrl('w2')}${mode === 'empty' ? '' : '#b_table'})`, payload: null }] }));
      if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot({ ...TRACK, id: 'w2' }, [], mode === 'empty' ? fixtureReport() : { ...REPORT, blocks: [{ id: mode === 'table block' ? 'b_table' : 'other', kind: 'table', rev: 1, markdown: '', payload: { columns: [{ key: 'result', label: 'Result' }], rows: [{ result: 'Actual table contents' }] } }] }));
    });
    const link = await screen.findByRole('button', { name: 'Report' }, { timeout: 5_000 });
    expect(requests.some(request => request.path === '/api/v1/tracks/w2')).toBe(false);
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Report' });
    await within(preview).findByText(mode === 'empty' ? '这份报告暂无内容。'
      : mode === 'missing block' ? '报告中没有这条引用指向的内容。' : 'Actual table contents');
  });

  it.each(['inline table', 'native table'])('previews evidence in a %s through the full route', async surface => {
    const table = { columns: [{ key: 'source', label: 'Source' }], rows: [{ source: '[Table evidence](neige://source/src_2c9e0a1b#q1)' }] };
    const snapshot = { id: 's1', observedAt: 1, producedAt: 2 };
    const cell = { kind: 'table', id: 'table', title: '', table };
    const native = surface === 'native table';
    const block = { markdown: '', id: 'b_table', kind: native ? 'view' : 'table', rev: 1, payload: native
      ? { version: 1, title: '', description: '', snapshot,
        rows: [{ id: 'row', title: '', layout: 'one', cells: [cell] }] }
      : table };
    const { requests } = setup(request => request.path === '/api/v1/tracks/w1' ? ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [block] })) : undefined);
    const link = await screen.findByRole('button', { name: 'Table evidence' }, { timeout: 5_000 });
    expect(requests.filter(request => request.path.includes('/sources/'))).toHaveLength(0);
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Table evidence' });
    await within(preview).findByText('智堡摘要，非机构原文');
    expect(preview.querySelector('[data-nc-report-source-reading]')).not.toBeNull();
    fireEvent.click(within(preview).getByRole('button', { name: '原文' }));
    expect(preview.querySelector('mark')?.textContent).toBe('9月加息概率接近九成');
    fireEvent.click(within(preview).getByRole('button', { name: 'Open in workspace' }));
    const report = screen.getByRole('region', { name: 'Track report' });
    const source = await within(report).findByRole('region', { name: SOURCE_ROW.title });
    expect(source.querySelector('mark')?.textContent).toBe('9月加息概率接近九成');
  });

  it.each(['旧引用', '未追加的锚点'])('explains unresolved evidence on hover for %s', async label => {
    const { requests } = setup();
    const link = await screen.findByRole('button', { name: label }, { timeout: 5_000 });
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: `Preview: ${label}` });
    await within(preview).findByRole('heading', { name: label === '旧引用' ? SOURCE_PANEL_COPY.missingTitle : SOURCE_PANEL_COPY.anchorMissingTitle });
    if (label !== '旧引用') {
      fireEvent.click(within(preview).getByRole('button', { name: '原文' }));
      expect(preview.querySelector('[data-nc-report-source-body]')?.textContent).toBe(SOURCE_ROW.body);
    }
    expect(requests.filter(request => request.path.includes('/sources/'))).toHaveLength(1);
  });

  it('explains a malformed citation on hover without making a source request', async () => {
    const { requests } = setup(request => request.path === '/api/v1/tracks/w1' ? ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: '[Invalid](neige://source/src_bad#q0)', payload: null }] })) : undefined);
    const link = await screen.findByRole('button', { name: 'Invalid' }, { timeout: 5_000 });
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Invalid' });
    expect(within(preview).getByText(SOURCE_PANEL_COPY.missingMalformed)).toBeTruthy();
    expect(requests.filter(request => request.path.includes('/sources/'))).toHaveLength(0);
  });

  it('shows loading, failure and retry inside the preview while keeping source text inert', async () => {
    let finish: (response: ApiTransportResponse) => void = () => { throw new Error('No source read'); };
    let reads = 0;
    const unsafe = '<img src="https://example.com/tracker"> [link](https://example.com)';
    setup(request => {
      if (request.path !== '/api/tracks/w1/sources/src_2c9e0a1b') return undefined;
      reads++;
      return reads === 1 ? new Promise(resolve => { finish = resolve; }) : ok({ ...SOURCE_ROW, body: unsafe, quotes: [] });
    });
    const link = await screen.findByRole('button', { name: 'Mikko 日志' }, { timeout: 5_000 });
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Mikko 日志' });
    await within(preview).findByText(SOURCE_PANEL_COPY.loading);
    finish({ status: 500, statusText: 'Error', body: { error: 'Source read failed' } });
    await within(preview).findByRole('alert');
    fireEvent.click(within(preview).getByRole('button', { name: 'Retry' }));
    await within(preview).findByText('link', { exact: true });
    expect(preview.querySelector('[data-nc-report-source-reading]')?.querySelectorAll('a, img, iframe, button')).toHaveLength(0);
    fireEvent.click(within(preview).getByRole('button', { name: '原文' }));
    await within(preview).findByText(unsafe);
    expect(reads).toBe(2);
    expect(preview.querySelectorAll('img, iframe, a, script')).toHaveLength(0);
  });

  it('previews a different track report and reads its nested evidence in that track', async () => {
    const otherReport = { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: '[Other evidence](neige://source/src_2c9e0a1b#q1)', payload: null }] };
    const { requests } = setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [], { ...REPORT, blocks: [{ id: 'b_1', kind: 'prose', rev: 1, markdown: `[Other report](${trackReportLinkUrl('w2')})`, payload: null }] }));
      if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot({ ...TRACK, id: 'w2' }, [], otherReport));
      if (request.path === '/api/tracks/w2/sources/src_2c9e0a1b') return ok({ ...SOURCE_ROW, body: 'Other track evidence.' });
    });
    const link = await screen.findByRole('button', { name: 'Other report' }, { timeout: 5_000 });
    expect(requests.some(request => request.path === '/api/v1/tracks/w2')).toBe(false);
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Other report' });
    const evidence = await within(preview).findByRole('button', { name: 'Other evidence' });
    fireEvent.pointerEnter(evidence.parentElement!);
    const nested = await screen.findByRole('dialog', { name: 'Preview: Other evidence' });
    await within(nested).findByText('Other track evidence.');
    fireEvent.click(within(nested).getByRole('button', { name: 'Open in workspace' }));
    const source = await screen.findByRole('region', { name: SOURCE_ROW.title });
    await within(source).findByText('Other track evidence.');
    expect(screen.queryByRole('dialog', { name: 'Preview: Other evidence' })).toBeNull();
    expect(screen.queryByRole('dialog', { name: 'Preview: Other report' })).toBeNull();
    expect(requests.some(request => request.path === '/api/tracks/w1/sources/src_2c9e0a1b')).toBe(false);
    expect(requests.every(request => request.method === 'GET')).toBe(true);
  });

  it('reads captured evidence only after hover, with provenance and quote, without scrolling the report', async () => {
    const { requests } = setup();
    const link = await screen.findByRole('button', { name: 'Mikko 日志' }, { timeout: 5_000 });
    expect(requests.filter(request => request.path.includes('/sources/'))).toHaveLength(0);
    fireEvent.pointerEnter(link.parentElement!);
    const preview = await screen.findByRole('dialog', { name: 'Preview: Mikko 日志' });
    await within(preview).findByText('智堡摘要，非机构原文');
    expect(preview.querySelector('[data-nc-report-source-reading]')).not.toBeNull();
    fireEvent.click(within(preview).getByRole('button', { name: '原文' }));
    expect(preview.querySelector('mark')?.textContent).toBe('9月加息概率接近九成');
    expect(preview.querySelector('[data-nc-report-source-body]')?.textContent).toBe(SOURCE_ROW.body);
    expect(scrollIntoView).not.toHaveBeenCalled();
    expect(requests.filter(request => request.path.includes('/sources/'))).toHaveLength(1);
    fireEvent.click(within(preview).getByRole('button', { name: 'Open in workspace' }));
    const source = await screen.findByRole('region', { name: SOURCE_ROW.title });
    expect(source.querySelector('mark')?.textContent).toBe('9月加息概率接近九成');
    expect(requests.filter(request => request.method !== 'GET')).toHaveLength(0);
  });

  it('opens a cited source on a phone and returns to the report without writing data', async () => {
    await page.viewport(390, 844);
    try {
      const { requests } = setup();
      const citation = page.getByRole('button', { name: 'Mikko 日志' });
      await citation.click();
      const source = page.getByRole('region', { name: SOURCE_ROW.title });
      await expect.element(source.getByText('9月加息概率接近九成', { exact: true })).toBeVisible();
      await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toBeVisible();
      expect(requests.filter((request) => request.path === '/api/tracks/w1/sources/src_2c9e0a1b')).toHaveLength(1);
      await source.getByRole('button', { name: SOURCE_PANEL_COPY.closeLabel }).click();
      await expect.element(source).not.toBeInTheDocument();
      await expect.element(citation).toHaveFocus();
      expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
    } finally {
      await page.viewport(1400, 900);
    }
  });

  it('opens the source in the report, highlights the quote, and returns focus on Escape', async () => {
    const { requests } = setup();
    const citation = page.getByRole('button', { name: 'Mikko 日志' });
    await citation.click();
    const report = screen.getByRole('region', { name: 'Track report' });
    const source = await within(report).findByRole('region', { name: SOURCE_ROW.title });
    expect(requests.filter((request) => request.path === '/api/tracks/w1/sources/src_2c9e0a1b')).toHaveLength(1);
    expect(within(source).getByText('智堡摘要，非机构原文')).toBeTruthy();
    const mark = source.querySelector('mark');
    expect(mark?.textContent).toBe('9月加息概率接近九成');
    expect(scrollIntoView.mock.instances).toContain(mark);
    expect(source).toHaveFocus();
    await citation.click();
    await expect.element(page.getByRole('region', { name: SOURCE_ROW.title })).toHaveFocus();
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(source.isConnected).toBe(false));
    await expect.element(citation).toHaveFocus();
  });

  it('says a dangling citation is missing, with its destination, instead of failing', async () => {
    const { requests } = setup();
    fireEvent.click(await screen.findByRole('button', { name: '旧引用' }, { timeout: 5_000 }));
    const source = await screen.findByRole('region', { name: SOURCE_PANEL_COPY.panelTitle });
    await within(source).findByRole('heading', { name: SOURCE_PANEL_COPY.missingTitle });
    expect(within(source).getByText('neige://source/src_0badf00d')).toBeTruthy();
    expect(within(source).queryByRole('alert')).toBeNull();
    // One read, no retry: the 404 is data.
    expect(requests.filter((request) => request.path === '/api/tracks/w1/sources/src_0badf00d')).toHaveLength(1);
  });

  it('shows the source with the missing-anchor state and its destination when the anchor is not on the row', async () => {
    setup();
    fireEvent.click(await screen.findByRole('button', { name: '未追加的锚点' }, { timeout: 5_000 }));
    const source = await screen.findByRole('region', { name: SOURCE_ROW.title });
    expect(within(source).getByText('智堡摘要，非机构原文')).toBeTruthy();
    expect(source.querySelector('mark')).toBeNull();
    expect(source.querySelector('pre[data-nc-report-source-body]')?.textContent).toBe(SOURCE_ROW.body);
    const notice = source.querySelector('[data-nc-report-source-missing="anchor"]');
    expect(within(source).getByRole('heading', { level: 3 }).textContent).toBe(SOURCE_PANEL_COPY.anchorMissingTitle);
    expect(notice?.querySelector('code')?.textContent).toBe('neige://source/src_2c9e0a1b#q7');
  });

  it('keeps the conversation list and composer usable while reading a source', async () => {
    setup();
    const citation = page.getByRole('button', { name: 'Mikko 日志' });
    const conversationRow = page.getByRole('button', { name: /Conversation Planner chat/ });
    await citation.click();
    await expect.element(page.getByText('9月加息概率接近九成', { exact: true })).toBeVisible();
    await expect.element(conversationRow).toBeVisible();
    await conversationRow.click();
    const conversation = page.getByRole('complementary', { name: 'Planner chat' });
    const composer = conversation.getByRole('combobox', { name: 'Message', exact: true });
    await composer.fill('A note about the source');
    await expect.element(page.getByText('9月加息概率接近九成', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: SOURCE_PANEL_COPY.closeLabel }).click();
    await expect.element(composer).toHaveTextContent('A note about the source');
  });

  it('does not let an Escape inside the source card interrupt a running planner turn', async () => {
    const { requests } = setup((request) => {
      if (request.path.includes('&view=run')) return ok(sessionPage(true));
      return undefined;
    });
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Planner chat/ }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => expect(requests.some((request) => request.path.includes('&view=run'))).toBe(true));
    fireEvent.click(await screen.findByRole('button', { name: 'Mikko 日志' }, { timeout: 5_000 }));
    const source = await screen.findByRole('region', { name: SOURCE_ROW.title });
    fireEvent.keyDown(source, { key: 'Escape' });
    await waitFor(() => expect(source.isConnected).toBe(false));
    expect(requests.filter((request) => request.path.endsWith('/interrupt'))).toHaveLength(0);
    const conversation = screen.getByRole('complementary', { name: 'Planner chat' });
    expect(conversation.hasAttribute('data-nc-escape-layer')).toBe(true);
    /* The premise, checked after rather than before so it cannot have used up the running turn. */
    fireEvent.keyDown(conversation, { key: 'Escape' });
    await waitFor(() => expect(requests.filter((request) => request.path.endsWith('/interrupt'))).toHaveLength(1));
    expect(conversation.hasAttribute('data-nc-escape-layer')).toBe(true);
  });
});
