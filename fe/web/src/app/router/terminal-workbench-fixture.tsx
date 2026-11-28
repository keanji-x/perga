import { createUiPreferences } from '../providers/ui-preferences.tsx';
import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory,RouterProvider } from '@tanstack/react-router';
import { fireEvent,render,screen,waitFor } from '@testing-library/react';
import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { TerminalPresentation } from '../../../../core/domain/terminal-presentation.ts';

import { terminalPanelItem, type PanelItem, type TerminalPanelItem } from '../../../../core/domain/panel-item.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionItem,sessionPage } from './conversation-session-data.ts';
import { createAppRouter } from './public.tsx';
import { fixtureBootstrap,fixtureReport,fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
import { bootTestCardRuntime } from './test-card-runtime.ts';

export function terminalCard(id: string, presentation: TerminalPresentation, trackId = 'workbench'): TerminalPanelItem {
  return { ...terminalPanelItem({ id, trackId, title: id, cwd: '/tmp', status: 'running', createdAtMs: 1, sessionId: presentation.source === 'agent' ? 'agent-session' : null }), presentation };
}

/** HTTP boundary fixture; production router, mutations, card registry and lifecycle remain real. */
export function setupWorkbench({ cards: initial = [] as PanelItem[], defer = false, lost = false, daily = false, failure = null as null | 'definite' | 'unknown', initialCard = null as string | null, secondaryReads = null as Promise<void> | null, toolsRead = null as Promise<void> | null, createdTerminalId = null as string | null } = {}) {
  const requests: ApiRequest[] = [];
  const receipts = createReadReceiptsKernel({ baselineMs: 0 });
  const cards = [...initial];
  const otherCards: PanelItem[] = [];
  const savedReply = { text: '' };
  const releases: (() => void)[] = [];
  const keys = new Map<string, PanelItem>();
  const track = fixtureTrack('workbench', { areaId: 'area', title: 'Workbench track' });
  const parent = fixtureSession('parent', track.id, { title: 'Main discussion', isLead: true });
  const rows = [parent];
  let lose = lost;
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { async send(request) {
    requests.push(request);
    const receipt = await receipts.reply(request);
    if (receipt !== undefined) return receipt;
    const { path, method } = request;
    if (path === '/api/v1/today/daily') return ok({ date: '2026-10-17', timeZone: 'Asia/Shanghai', trackId: track.id });
    if (path === '/api/v1/bootstrap') return ok(fixtureBootstrap());
    if (path.startsWith('/api/v1/bootstrap?scope=managed')) return ok({ version: 'test', providers: [{ provider: 'codex', available: true, version: null, reason: null, models: [] }], templates: [], areas: [] });
    if (path === '/api/v1/areas') return ok([{ id: 'area', name: 'Work', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }]);
    if (path === '/api/v1/areas/area/tracks?includeClosed=true') return ok([track]);
    if (path === '/api/settings') return ok({});
    if (path === '/api/v1/tracks/other') return ok(fixtureSnapshot({ ...track, id: 'other', title: 'Other track' }));
    if (path === '/api/v1/tracks/workbench') return ok(fixtureSnapshot(track, rows, fixtureReport('# Workbench notes')));
    if (secondaryReads !== null && ['/api/v1/tracks/workbench/report', '/api/v1/tracks/workbench/sessions', '/api/tracks/workbench/backlinks'].includes(path)) await secondaryReads;
    if (path === '/api/v1/tracks/workbench/report') return ok({ taskDiagnostics: [] });
    if (path === '/api/v1/tracks/workbench/sessions') return ok(rows);
    if (path === '/api/v1/tracks/workbench/terminals' && method === 'GET') { await toolsRead; return ok(cards.map(card => card.terminal)); }
    if (path === '/api/v1/tracks/other/terminals' && method === 'GET') return ok(otherCards.map(card => card.terminal));
    if (path === '/api/v1/tracks/other/terminals' && method === 'POST') {
      const card = terminalCard('other-terminal', { source: 'manual', inWorkbench: true }, 'other'); otherCards.push(card); return ok(card.terminal);
    }
    if (path === '/api/v1/tracks/workbench/terminals' && method === 'POST') {
      if (defer) await new Promise<void>(resolve => releases.push(resolve));
      const outcome = failure; failure = null;
      if (outcome === 'definite') return { status: 400, statusText: 'Bad Request', body: { code: 'bad_request', error: 'Terminal refused' } };
      const key = request.headers?.['Idempotency-Key'] ?? '';
      let card = keys.get(key);
      if (card === undefined) { card = terminalCard(`terminal-${keys.size + 1}`, { source: 'manual', inWorkbench: true }); if (createdTerminalId !== null) card = terminalCard(createdTerminalId, { source: 'manual', inWorkbench: true }); cards.push(card); keys.set(key, card); }
      if (lose || outcome === 'unknown') { lose = false; throw new Error('lost response'); }
      return ok(card.terminal);
    }
    if (method === 'DELETE' && (path.startsWith('/api/v1/tracks/') && path.includes('/terminals/'))) {
      const termId = decodeURIComponent(path.split('/').at(-1) ?? '');
      cards.splice(cards.findIndex(card => card.id === termId), 1);
      return ok(undefined);
    }
    if (method === 'DELETE' && path.startsWith('/api/cards/')) { cards.splice(cards.findIndex(card => card.id === path.split('/')[3]), 1); return ok(undefined); }
    if (path.includes('/items?')) {
      const session = { ...parent, id: conversationCardId(path), status: 'idle' };
      return ok(sessionPage(session, savedReply.text === '' ? [] : [sessionItem(session.id, 1, savedReply.text)]));
    }
    return ok([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const booted = bootTestCardRuntime();
  const runtime = { ...booted, host: { ...booted.host } };
  const uiPreferences = createUiPreferences();
  for (const item of initial) if (item.presentation !== undefined) {
    uiPreferences.setTerminalWorkbench(item.trackId, item.id, item.presentation.inWorkbench);
  }
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: runtime, uiPreferences,
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: [`${daily ? '/' : '/track/workbench'}${initialCard === null ? '' : `?card=${encodeURIComponent(initialCard)}`}`] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  const view = render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>, { container });
  return { ...view, requests, releases, router, client, runtime, cards, savedReply, posts: () => requests.filter(request => request.method === 'POST' && request.path.endsWith('/terminals')) };
}


/** Use the public composer command after removal of the redundant header shortcut. */
export async function terminalCommandInput(): Promise<HTMLElement> {
  if (screen.queryAllByRole('combobox', { name: 'Message' }).length === 0) {
    await waitFor(() => {
      const conversation = screen.queryByRole('button', { name: 'Conversation Main discussion' });
      const create = screen.queryByRole('button', { name: /^New conversation$/u });
      if (screen.queryByRole('combobox', { name: 'Message' })) return;
      if (!(conversation ?? create)) throw new Error('Conversation entry is not ready');
      fireEvent.click((conversation ?? create)!);
    });
  }
  const input = (await screen.findAllByRole('combobox', { name: 'Message' }))[0];
  await waitFor(() => { if (input.getAttribute('contenteditable') !== 'true') throw new Error('Composer is not ready'); });
  return input;
}

export async function openTerminalCommand(): Promise<void> {
  const input = await terminalCommandInput();
  input.focus();
  input.textContent = '/terminal';
  fireEvent.input(input);
  fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
}
