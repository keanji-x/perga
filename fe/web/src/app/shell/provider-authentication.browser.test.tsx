import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../styles/entry.css';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ProviderAuthenticationNotice } from './provider-authentication.tsx';
afterEach(() => { cleanup(); document.body.replaceChildren(); });
it.each([390, 1280])('does not invent an authentication notice from v1 provider availability at %ipx', async width => {
  await page.viewport(width, 844);
  const requests: ApiRequest[] = [];
  const transport: ApiTransportPort = { send: request => {
    requests.push(request);
    return Promise.resolve({ status: 200, statusText: 'OK', body: { version: 'test', providers: [
      { provider: 'codex', available: false, version: null, reason: 'Provider is unavailable.', models: [] },
    ], templates: [], areas: [] } });
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={client}><ProviderAuthenticationNotice transport={transport}
    unauthorized={createUnauthorizedChannel({ enqueue: task => task() })} onOpenPlanners={vi.fn()} /></QueryClientProvider>);
  await expect.poll(() => client.isFetching()).toBe(0);
  expect(requests.map(request => request.path)).toEqual(['/api/v1/bootstrap?scope=providers']);
  expect(page.getByRole('alert').query()).toBeNull();
  expect(page.getByRole('button', { name: 'Settings' }).query()).toBeNull();
});
