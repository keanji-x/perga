import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory,RouterProvider } from '@tanstack/react-router';
import { cleanup,render,screen } from '@testing-library/react';
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

it('renders the quiet template protocol identity without fetching or showing an inputs form', async () => {
  const requests: ApiRequest[] = [];
  const track = fixtureTrack('protocol', { templateId: 'agents-delivery', templateTitle: 'Delivery' });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request);
    const base = '/api/v1/tracks/protocol';
    if (request.path === base) return Promise.resolve(ok(fixtureSnapshot(track, [], fixtureReport('# Report'))));
    if (request.path === `${base}/views`) return Promise.resolve(ok({ templateId: 'agents-delivery', views: {}, widgets: {}, diagnostics: [] }));
    if (request.path === `${base}/report`) return Promise.resolve(ok({ taskDiagnostics: [] }));
    if (request.path === '/api/v1/areas') return Promise.resolve(ok([{ id: track.areaId, name: 'Project', color: '#6574cd', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 }]));
    if (request.path.includes('/tracks?')) return Promise.resolve(ok([track]));
    if (request.path === '/api/settings') return Promise.resolve(ok({}));
    return Promise.resolve(ok([]));
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: ['/track/protocol'] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>, { container });
  expect(await screen.findByText('Delivery')).toBeTruthy();
  expect(screen.queryByRole('button', { name: /Details/ })).toBeNull();
  expect(requests.some(request => request.path === '/api/v1/tracks/protocol/template')).toBe(false);
  client.clear();
});
