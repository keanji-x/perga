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
  let track = fixtureTrack('protocol', { templateId: 'agents-delivery', templateTitle: 'Delivery', templateInputs: { count: 2 } });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request);
    const base = '/api/v1/tracks/protocol';
    if (request.path === `${base}/template/inputs` && request.method === 'PATCH') {
      track = { ...track, templateInputs: request.body };
      return Promise.resolve(ok(track));
    }
    if (request.path === `${base}/template`) return Promise.resolve(ok({ snapshot: 'source', manifest: { id: 'agents-delivery', flow: [], inputsSchema: { count: { type: 'number' } } }, inputs: track.templateInputs, state: {} }));
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
  fireEvent.click(await screen.findByRole('button', { name: /Details/ }));
  fireEvent.click(screen.getByRole('tab', { name: 'Inputs' }));
  const count = await screen.findByLabelText('count');
  fireEvent.change(count, { target: { value: '4.5' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save inputs' }));
  await waitFor(() => expect(requests.filter(request => request.method === 'PATCH')).toMatchObject([{ path: '/api/v1/tracks/protocol/template/inputs', body: { count: 4.5 } }]));
  await waitFor(() => expect(requests.filter(request => request.path === '/api/v1/tracks/protocol/views')).toHaveLength(2));
  expect(screen.getAllByRole('tab').map(tab => tab.textContent)).toEqual(['Inputs']);
  client.clear();
});
