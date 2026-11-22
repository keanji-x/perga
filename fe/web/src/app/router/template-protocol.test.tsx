import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory,RouterProvider } from '@tanstack/react-router';
import { cleanup,fireEvent,render,screen,waitFor } from '@testing-library/react';
import { afterEach,expect,it } from 'vitest';
import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { fixtureReport,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
import { bootTestCardRuntime } from './test-card-runtime.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('loads the current manifest and sends typed edits through the production router', async () => {
  const requests: ApiRequest[] = [];
  let track = fixtureTrack('protocol', { templateId: 'dev', templateTitle: 'Dev', templateInputs: { count: 2 } });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request);
    const base = '/api/v1/tracks/protocol';
    if (request.path === `${base}/template/inputs` && request.method === 'PATCH') {
      track = { ...track, templateInputs: request.body };
      return Promise.resolve(ok(track));
    }
    if (request.path === `${base}/template`) return Promise.resolve(ok({ snapshot: 'source', manifest: { id: 'dev', inputsSchema: { count: { type: 'number' } } }, inputs: track.templateInputs, state: {}, headerView: null }));
    if (request.path.startsWith(`${base}/schedules?`)) return Promise.resolve(ok({ schedules: [], recentRuns: [] }));
    if (request.path === `${base}/schedules` && request.method === 'POST') return Promise.resolve(ok({
      id: 'new', trackId: 'protocol', sourceKind: 'user', scheduleKind: 'cron', title: 'Daily', prompt: 'Check',
      cronExpr: '0 9 * * *', timezone: 'UTC', runAtMs: null, nextFireAtMs: 2000, lastFiredAtMs: null,
      maxOccurrences: null, firedCount: 0, state: 'active', createdAtMs: 1, updatedAtMs: 2,
    }));
    if (request.path === base) return Promise.resolve(ok(fixtureSnapshot(track, [], fixtureReport('# Report'))));
    if (request.path === `${base}/report`) return Promise.resolve(ok({ taskDiagnostics: [] }));
    if (request.path === '/api/v1/areas') return Promise.resolve(ok([{ id: track.areaId, name: 'Project', color: '#6574cd', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 }]));
    if (request.path.includes('/tracks?')) return Promise.resolve(ok([track]));
    if (request.path === '/api/settings') return Promise.resolve(ok({}));
    return Promise.resolve(ok([]));
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }), onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: ['/next/track/protocol'] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>, { container });
  fireEvent.click(await screen.findByRole('button', { name: /Details/ }));
  fireEvent.click(screen.getByRole('tab', { name: 'Inputs' }));
  const count = await screen.findByLabelText('count');
  fireEvent.change(count, { target: { value: '4.5' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save inputs' }));
  await waitFor(() => expect(requests.filter(request => request.method === 'PATCH')).toMatchObject([{ path: '/api/v1/tracks/protocol/template/inputs', body: { count: 4.5 } }]));
  fireEvent.click(screen.getByRole('tab', { name: 'Schedules' }));
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Daily' } });
  fireEvent.change(screen.getByLabelText('Prompt'), { target: { value: 'Check' } });
  fireEvent.change(screen.getByLabelText('Schedule type'), { target: { value: 'cron' } });
  fireEvent.change(screen.getByLabelText('Cron expression'), { target: { value: '0 9 * * *' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add schedule' }));
  await waitFor(() => expect(requests.filter(request => request.method === 'POST')).toMatchObject([{ path: '/api/v1/tracks/protocol/schedules', body: { title: 'Daily', prompt: 'Check', cronExpr: '0 9 * * *', timezone: 'UTC' } }]));
  await waitFor(() => expect(requests.filter(request => request.path.includes('/schedules?')).length).toBeGreaterThan(1));
  client.clear();
});
