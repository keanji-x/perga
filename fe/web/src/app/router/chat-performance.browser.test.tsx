import { historyRows, type History } from '../conversations/history.ts';
import { sessionItem, sessionPage, sessionTurn } from './conversation-session-data.ts';
import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
/** Production router/component probe. Durations are diagnostic, never a CI speed threshold. */
import { memo, Profiler, type ProfilerOnRenderCallback } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import { commands, page } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';

import '../../styles/entry.css';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import type { SessionItemDto } from '../../../../core/api/perga-v1.ts';
import { HARNESS_ITEMS_PAGE_LIMIT as PAGE_LIMIT } from '../../../../core/domain/conversation.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

// Vitest 4.1 defines BrowserCommands in this origin module and re-exports it publicly.
declare module 'vitest/internal/browser' {
  interface BrowserCommands { recordChatPerformance(count: number, report: string): Promise<void>; mockChatPerformanceImage(enabled: boolean): Promise<void> }
}

type Commit = Readonly<{ id: string; phase: string; duration: number; base: number }>;
const metrics = vi.hoisted<{ renders: Record<string, number>; commits: Commit[] }>(() => ({ renders: {}, commits: [] }));
const record: ProfilerOnRenderCallback = (id, phase, duration, base) => {
  metrics.commits.push({ id, phase, duration, base });
};

// Instrument the real exports; every rendering rule, hook and DOM node is production code.
vi.mock(import('../../features/chat/thread/public.tsx'), async (original) => {
  const actual = await original();
  const Thread = actual.ChatThread;
  const Composer = actual.ChatComposer;
  const onRender: ProfilerOnRenderCallback = (id, phase, duration, base) => {
    metrics.commits.push({ id, phase, duration, base });
  };
  return { ...actual,
    ChatThread: memo((props: Parameters<typeof Thread>[0]) => {
      metrics.renders['transcript'] = (metrics.renders['transcript'] ?? 0) + 1;
      return <Profiler id="transcript" onRender={onRender}><Thread {...props} /></Profiler>;
    }),
    ChatComposer: (props: Parameters<typeof Composer>[0]) => {
      metrics.renders['composer'] = (metrics.renders['composer'] ?? 0) + 1;
      return <Profiler id="composer" onRender={onRender}><Composer {...props} /></Profiler>;
    },
  };
});
vi.mock(import('../../features/chat/list/public.tsx'), async (original) => {
  const actual = await original();
  const List = actual.ChatList;
  const onRender: ProfilerOnRenderCallback = (id, phase, duration, base) => {
    metrics.commits.push({ id, phase, duration, base });
  };
  return { ...actual, ChatList: (props: Parameters<typeof List>[0]) => {
    metrics.renders['navigation'] = (metrics.renders['navigation'] ?? 0) + 1;
    return <Profiler id="navigation" onRender={onRender}><List {...props} /></Profiler>;
  } };
});

function rows(count: number): SessionItemDto[] {
  return Array.from({ length: count }, (_, index) => sessionItem('perf-card', index + 1,
    `Reply ${index}. **Evidence** and a [reference](https://example.invalid).\n\n${'A paragraph for real Markdown rendering. '.repeat(8)}`,
    'assistant_text', `stored-${index}`));
}

function mount(count: number) {
  const history = rows(count);
  const live = { text: history.at(-1)!.payload as { text: string } };
  const requests: ApiRequest[] = [];
  const track = fixtureTrack('perf-track', { areaId: 'perf-area', title: 'Performance probe', leadSessionId: 'perf-card' });
  const session = fixtureSession('perf-card', track.id, { title: 'Measured chat', isLead: true, status: 'running' });
  const activeTurn = sessionTurn(session.id);
  const transport: ApiTransportPort = { async send(request) {
    await Promise.resolve();
    requests.push(request);
    let body: unknown = [];
    if (request.path === '/api/v1/areas') body = [{ id: track.areaId, name: 'Performance', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === `/api/v1/areas/${track.areaId}/tracks?includeClosed=true`) body = [track];
    if (request.path === `/api/v1/tracks/${track.id}`) body = fixtureSnapshot(track, [session]);
    if (request.path === `/api/v1/tracks/${track.id}/sessions`) body = [session];
    if (request.path === '/api/settings') body = {};
    if (request.path.includes('/items?')) {
      const before = new URL(request.path, 'http://fixture').searchParams.get('beforeId');
      body = sessionPage(session, history.filter(row => before === null || row.id < Number(before)).slice(-500), activeTurn);
    }
    return { status: 200, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }), onSignOut: () => {} });
  router.update({ history: createMemoryHistory({ initialEntries: [`/track/${track.id}?panel=conversations`] }) });
  const root = document.createElement('div'); root.id = 'root'; document.body.append(root);
  render(<Profiler id="host" onRender={record}><QueryClientProvider client={client}><ThemeProvider>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider></Profiler>, { container: root });
  return { client, live, requests };
}

function resetMetrics() { metrics.renders = {}; metrics.commits.length = 0; }
function snapshot() {
  const ids = [...new Set(metrics.commits.map((commit) => commit.id))];
  return { parentRenders: { ...metrics.renders }, scopes: Object.fromEntries(ids.map((id) => {
    const values = metrics.commits.filter((commit) => commit.id === id).map((commit) => commit.duration).sort((a, b) => a - b);
    return [id, { commits: values.length, totalMs: values.reduce((sum, n) => sum + n, 0),
      medianMs: values[Math.floor(values.length / 2)] ?? 0, maxMs: values.at(-1) ?? 0 }];
  })) };
}
async function frames() {
  for (let i = 0; i < 4; i++) await act(async () => { await new Promise<void>((resolve) => requestAnimationFrame(() => resolve())); });
}
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); resetMetrics(); });

it.each([20, 120, 300, 900])('reports production rendering/update boundaries with %i loaded rows', async (count) => {
  await page.viewport(1400, 900);
  resetMetrics();
  const start = performance.now();

  const view = mount(count);
  try {
    await page.getByRole('button', { name: /^Conversation Measured chat/ }).click();
    await expect.element(page.getByText(`Reply ${count - 1}.`, { exact: false })).toBeVisible();
    const loadedRows = () => historyRows(view.client.getQueryData<History>(['harness-items', 'perf-card', 'conversation_v1'])).length;
    await expect.poll(loadedRows).toBe(Math.min(count, PAGE_LIMIT));
    while (loadedRows() < count) {
      const before = loadedRows();
      const pane = document.querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
      // Prepare geometry for this rendering probe; real input routing is covered
      // by scroll-follow browser regressions and the production browser probe.
      act(() => { pane.scrollTop = 0; fireEvent.wheel(pane, { deltaY: -40 }); });
      await expect.poll(loadedRows).toBeGreaterThan(before);
    }
    await frames();
    expect(loadedRows()).toBe(count);
    expect(document.querySelectorAll('[data-nc-turn="agent"]').length).toBe(count);
    const loaded = { durationMs: performance.now() - start, ...snapshot(), domElements: document.querySelectorAll('#root *').length,
      loadedImages: [...document.querySelectorAll<HTMLImageElement>('img[alt="Probe image"]')].filter((image) => image.complete && image.naturalWidth > 0).length };
    resetMetrics();
    let queryEvents = 0;
    const unsubscribe = view.client.getQueryCache().subscribe(() => { queryEvents++; });
    const pollsBefore = view.requests.filter((request) => request.path.includes('/items?') && !request.path.includes('limit=1')).length;
    for (let i = 0; i < 4; i++) {
      await act(async () => { await view.client.refetchQueries({ queryKey: queryKeys.harnessItems('perf-card') }); });
      await frames();
    }
    await frames();
    const steady = { ...snapshot(), queryEvents,
      polls: view.requests.filter((request) => request.path.includes('/items?') && !request.path.includes('limit=1')).length - pollsBefore };
    resetMetrics(); queryEvents = 0;
    let domMutations = 0;
    const observer = new MutationObserver((records) => { domMutations += records.length; });
    observer.observe(document.getElementById('root')!, { subtree: true, childList: true, characterData: true });
    for (let i = 0; i < 4; i++) {
      view.live.text.text += `\n\nStream sample ${i}. ${'More live words. '.repeat(5)}`;
      await act(async () => { await view.client.refetchQueries({ queryKey: queryKeys.harnessItems('perf-card') }); });
      await expect.element(page.getByText(`Stream sample ${i}.`, { exact: false })).toBeVisible();
      await frames();
    }
    const changing = { ...snapshot(), queryEvents, domMutations };
    expect(changing.parentRenders['composer'] ?? 0).toBe(0);
    // History refreshes also publish read progress to navigation; keep those counts diagnostic.
    observer.disconnect(); unsubscribe();
    await commands.recordChatPerformance(count, JSON.stringify({ count, scenario: 'v1-markdown-history', loaded, steady, changing }));
  } finally { cleanup(); view.client.clear(); await commands.mockChatPerformanceImage(false); }
}, 30000);
