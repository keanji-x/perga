import { sessionPage, sessionItem, sessionInput } from './conversation-session-data.ts';
import type { AgentSessionDto, BootstrapDto } from '../../../../core/api/perga-v1.ts';
import { fixtureTrack, fixtureSession, fixtureSnapshot, fixtureCreation } from './session-contract-fixture.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { AgentProvider } from '../../../../core/domain/presentation-types.ts';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

/** Real router/query/draft/store over explicit server declarations, without CLI credentials. */
export function renderAssistantProviderFixture(options: {
  bootstrap: BootstrapDto;
  listRead?: 'pending' | 'failed';
  failFirstCreate?: boolean;
  landFirstCreate?: boolean;
  hideLanded?: boolean;
  existingProvider?: AgentProvider;
  dormant?: boolean;
  assistantProviders?: boolean;
  mobile?: boolean;
  webCompatVersion?: number;
  runAttachmentsSupported?: boolean;
}) {
  const requests: ApiRequest[] = [];
  let attempts = 0;
  let restarted = false;
  let selectedModel: string | null = null;
  const track = fixtureTrack('provider-track', { areaId: 'area', title: 'Provider discussion' });
  const rows: AgentSessionDto[] = options.existingProvider === undefined ? [] : [fixtureSession('dormant', track.id, { title: 'Saved Assistant', provider: options.existingProvider })];
  const transport: ApiTransportPort = { async send(request) {
    await Promise.resolve();
    requests.push(request);
    let body: unknown = [];
    let status = 200;
    if (request.path.startsWith('/api/v1/bootstrap')) body = options.bootstrap;
    if (request.path === '/api/v1/areas') body = [{ id: 'area', name: 'Work', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === '/api/v1/areas/area/tracks?includeClosed=true') body = [track];
    if (request.path === '/api/settings') body = {};
    if (request.path === `/api/v1/tracks/${track.id}`) body = fixtureSnapshot(track, options.hideLanded ? [] : [...rows]);
    if (request.path === `/api/v1/tracks/${track.id}/sessions`) {
      if (request.method === 'POST') {
        attempts += 1;
        if (options.failFirstCreate && !options.landFirstCreate && attempts === 1) return { status: 503, statusText: 'Unavailable', body: { code: 'service_unavailable', error: 'Unconfirmed' } };
        const input = request.body as { provider: AgentProvider; side?: { source_card_id: string } };
        const created = fixtureSession(fixtureConversationCardId(track.id, request.headers?.['Idempotency-Key'] ?? ''), track.id, { provider: input.provider, title: 'Created Assistant', updatedAtMs: 3 });
        const existing = rows.find(row => row.id === created.id);
        if (existing === undefined) rows.push(created);
        body = fixtureCreation(existing ?? created); status = 201;
        if (options.landFirstCreate && attempts === 1) return { status: 503, statusText: 'Unavailable', body: { code: 'service_unavailable', error: 'Unconfirmed' } };
      } else {
        if (options.listRead === 'pending') return new Promise(() => {});
        if (options.listRead === 'failed') throw new Error('List unavailable');
        body = options.hideLanded ? [] : [...rows];
      }
    }
    if (request.method === 'PATCH' && request.path === `/api/tracks/${track.id}/sessions/dormant/archive`) {
      const index = rows.findIndex(row => row.id === 'dormant');
      if (index >= 0) rows[index] = { ...rows[index], archivedAtMs: (request.body as { archived: boolean }).archived ? 123 : null };
      status = 204; body = undefined;
    }
    const sessionOrCardMatch = /^\/api\/(?:v1\/sessions|cards)\/([^/]+)\//.exec(new URL(request.path, 'http://fixture').pathname);
    const cardId = sessionOrCardMatch !== null ? decodeURIComponent(sessionOrCardMatch[1]) : request.path.split('/')[3];
    if (request.path.includes('/items?')) {
      const session = rows.find(row => row.id === cardId) ?? fixtureSession(cardId, track.id);
      body = sessionPage({ ...session, model: selectedModel }, cardId === 'dormant' ? [sessionItem(cardId, 1, 'Saved reply')] : []);
    }
    if (request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path)) {
      const input = request.body as { model: string | null; reasoning_effort: string | null };
      selectedModel = input.model;
      body = { id: cardId, trackId: track.id, provider: options.existingProvider ?? 'codex', model: input.model, createdAtMs: 1, updatedAtMs: 2 };
    }
    if (request.path.endsWith('/messages') && request.method === 'POST') {
      if (options.dormant && !restarted) return { status: 409, statusText: 'Conflict', body: {
        code: 'planner_harness_dormant', error: 'Start a fresh session' } };
      body = sessionInput(cardId);
    }
    if (request.path.endsWith('/planner/restart')) {
      restarted = true;
      body = { card_id: cardId, terminal_id: '', new_thread_id: 'replacement-thread' };
    }
    return { status, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }), onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: [`/track/${track.id}${options.mobile ? "" : "?panel=conversations"}`] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>, { container });
  return { requests, client, rows };
}

function fixtureConversationCardId(trackId: string, key: string): string { return `conv-${trackId}-${key}`; }
