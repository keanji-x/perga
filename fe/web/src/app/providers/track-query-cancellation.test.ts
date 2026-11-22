import { QueryClient,QueryObserver } from '@tanstack/react-query';
import { afterEach,describe,expect,it,vi } from 'vitest';

import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { retireAreaQueries } from './area-query-lifecycle.ts';
import { areaListQueryOptions } from './workspace-queries.ts';
import { tracksInAreaQueryOptions } from './track-queries.ts';
import { createFetchTransport } from './transport.ts';

afterEach(() => { vi.unstubAllGlobals(); });

// The workspace exposes an explicit Retry action; terminal authorization/missing reads never loop.
describe('workspace list terminal HTTP failures', () => {
  it.each([403, 404])('settles both list reads without retrying HTTP %s', async status => {
    const fetch = vi.fn(() => Promise.resolve(new Response(JSON.stringify({ error: 'unavailable' }), { status })));
    vi.stubGlobal('fetch', fetch);
    const transport = createFetchTransport();
    const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
    const client = new QueryClient({ defaultOptions: { queries: { retry: true } } });
    try {
      for (const options of [areaListQueryOptions(transport, unauthorized), tracksInAreaQueryOptions(transport, 'a1', unauthorized)]) {
        await expect(client.fetchQuery<unknown>(options)).rejects.toThrow();
        expect(client.getQueryState(options.queryKey)).toMatchObject({ status: 'error', fetchStatus: 'idle', fetchFailureCount: 1 });
      }
      expect(fetch).toHaveBeenCalledTimes(2);
    } finally { client.clear(); }
  });
});

// Exercise Query's retryer with production options and transport, including its browser budget.
describe('Area Track read automatic recovery', () => {
  afterEach(() => { vi.useRealTimers(); });

  it.each(['http', 'transport', 'exhausted', 'exhausted-transport', '401', '403', '404'] as const)(
    'settles %s through the production query options', async kind => {
      vi.useFakeTimers();
      const fetch = vi.fn(() => {
        if (kind === 'exhausted-transport' || (kind === 'transport' && fetch.mock.calls.length === 1)) return Promise.reject(new TypeError('offline'));
        const status = kind === 'http' ? (fetch.mock.calls.length === 1 ? 500 : 200)
          : kind === 'transport' ? 200 : kind === 'exhausted' ? 500 : Number(kind);
        return Promise.resolve(new Response(JSON.stringify(status === 200 ? [] : { error: 'unavailable' }), { status }));
      });
      vi.stubGlobal('fetch', fetch);
      const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
      const notified = vi.fn();
      unauthorized.subscribe(notified);
      const client = new QueryClient();
      const options = tracksInAreaQueryOptions(createFetchTransport(), 'a1', unauthorized);
      const observer = new QueryObserver(client, options);
      const unsubscribe = observer.subscribe(() => undefined);
      try {
        await vi.advanceTimersByTimeAsync(10_000);
        const recovered = kind === 'http' || kind === 'transport';
        expect(fetch).toHaveBeenCalledTimes(recovered ? 2 : kind.startsWith('exhausted') ? 4 : 1);
        expect(observer.getCurrentResult()).toMatchObject({
          status: recovered ? 'success' : 'error', fetchStatus: 'idle',
          failureCount: recovered ? 0 : kind.startsWith('exhausted') ? 4 : 1,
          ...(recovered ? { data: [] } : {}),
        });
        expect(notified).toHaveBeenCalledTimes(kind === '401' ? 1 : 0);
      } finally { unsubscribe(); client.clear(); }
    },
  );

  it.each(['cancel', 'retire'] as const)('does not request again after %s during backoff', async action => {
    vi.useFakeTimers();
    const fetch = vi.fn(() => Promise.resolve(new Response('{"error":"temporary"}', { status: 500 })));
    vi.stubGlobal('fetch', fetch);
    const client = new QueryClient();
    const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
    const options = tracksInAreaQueryOptions(createFetchTransport(), 'a1', unauthorized);
    const observer = new QueryObserver(client, options);
    const unsubscribe = observer.subscribe(() => undefined);
    try {
      await vi.advanceTimersByTimeAsync(0);
      expect(fetch).toHaveBeenCalledTimes(1);
      expect(observer.getCurrentResult()).toMatchObject({ fetchStatus: 'fetching', failureCount: 1 });
      if (action === 'retire') retireAreaQueries(client, 'a1');
      else await client.cancelQueries({ queryKey: options.queryKey });
      unsubscribe();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(fetch).toHaveBeenCalledTimes(1);
      if (action === 'retire') expect(client.getQueryState(options.queryKey)).toBeUndefined();
      else expect(client.getQueryState(options.queryKey)).toMatchObject({ fetchStatus: 'idle' });
    } finally { unsubscribe(); client.clear(); }
  });
});
