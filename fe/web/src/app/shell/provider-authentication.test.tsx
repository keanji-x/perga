// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { ProviderStatusDto } from '../../../../core/api/perga-v1.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { queryKeys } from '../providers/queries.ts';
import { ProviderAuthenticationNotice } from './provider-authentication.tsx';
afterEach(cleanup);
function mount(initial: ProviderStatusDto) {
  let answer = initial;
  let failing = false;
  const requests: string[] = [];
  const transport: ApiTransportPort = { send: (request) => {
    requests.push(`${request.method} ${request.path}`);
    if (failing) return Promise.reject(new Error('network unavailable'));
    return Promise.resolve({ status: 200, statusText: 'OK', body: { version: 'test', providers: [answer], templates: [], areas: [] } });
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const open = vi.fn();
  render(<QueryClientProvider client={client}><ProviderAuthenticationNotice transport={transport}
    unauthorized={createUnauthorizedChannel({ enqueue: (task) => task() })} onOpenPlanners={open} /></QueryClientProvider>);
  return { client, open, requests, update: (next: ProviderStatusDto) => { answer = next; }, fail: () => { failing = true; } };
}
it('does not invent authentication evidence from ordinary unavailability', async () => {
  const app = mount({ provider: 'codex', available: false, reason: 'daemon starting', version: null, models: [], modelCatalog: [], permissionModes: [] });
  await waitFor(() => expect(app.client.getQueryData(queryKeys.agentProviders())).toBeDefined());
  expect(screen.queryByRole('alert')).toBeNull();
});

it('keeps ordinary v1 provider recovery free of authentication notices', async () => {
  const app = mount({ provider: 'codex', available: false, reason: 'daemon starting', version: null, models: [], modelCatalog: [], permissionModes: [] });
  await waitFor(() => expect(app.client.getQueryData(queryKeys.agentProviders())).toBeDefined());
  app.update({ provider: 'codex', available: true, reason: null, version: 'test', models: [], modelCatalog: [], permissionModes: [] });
  await app.client.invalidateQueries({ queryKey: queryKeys.agentProviders() });
  await waitFor(() => expect(app.client.getQueryData(queryKeys.agentProviders())).toMatchObject([{ provider: 'codex', status: 'ready' }]));
  expect(screen.queryByRole('alert')).toBeNull();
  expect(app.open).not.toHaveBeenCalled();
  expect(app.requests.every(request => request === 'GET /api/v1/bootstrap?scope=providers')).toBe(true);
});
