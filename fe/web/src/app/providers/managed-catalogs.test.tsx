import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { useManagedProviderCatalogs } from './managed-providers.ts';

afterEach(cleanup);
const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
const provider = (name: string, model: string) => ({ provider: name, available: true, models: [model], modelCatalog: [], permissionModes: [] });
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

it('keeps a late catalog from a previous execution target out of the current provider list', async () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const requests: string[] = [];
  let oldReads = 0;
  let release!: (response: ApiTransportResponse) => void;
  const held = new Promise<ApiTransportResponse>(resolve => { release = resolve; });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request.path);
    if (request.path === '/api/v1/targets/old/providers') {
      oldReads++;
      return oldReads === 1 ? Promise.resolve(ok([provider('codex', 'old-model')])) : held;
    }
    if (request.path === '/api/v1/targets/new/providers') return Promise.resolve(ok([provider('grok', 'new-model')]));
    throw new Error(`Unexpected request: ${request.path}`);
  } };
  const { result, rerender } = renderHook(({ targetId }) => useManagedProviderCatalogs(transport, unauthorized, targetId), {
    initialProps: { targetId: 'old' },
    wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  await waitFor(() => expect(oldReads).toBe(2));
  rerender({ targetId: 'new' });
  await waitFor(() => expect(result.current.catalogs[0]?.data?.models[0]?.id).toBe('new-model'));
  expect(result.current.providers.data?.map(entry => entry.availability.provider)).toEqual(['grok']);
  await act(async () => { release(ok([provider('codex', 'late-old-model')])); await held; });
  expect(result.current.catalogs.map(catalog => catalog.data?.models[0]?.id)).toEqual(['new-model']);
  expect(requests.filter(path => path.includes('/old/'))).toHaveLength(2);
  expect(requests.filter(path => path.includes('/new/'))).toHaveLength(2);
  client.clear();
});
