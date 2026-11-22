// @vitest-environment jsdom
import { QueryClient,QueryObserver } from '@tanstack/react-query';
import { cleanup,render,waitFor } from '@testing-library/react';
import { afterEach,describe,expect,it,vi } from 'vitest';

import type { ApiTransportPort } from '../../../core/api/types.ts';
import { ApiError } from '../../../core/domain/failure-class.ts';
import { createFakeSocketFactory } from '../systems/events/fake-socket.ts';
import { WebSocketDriver } from '../systems/events/websocket-driver.ts';
import { createBrowserEventComposition,createEventComposition } from './composition.ts';
import { EventBridge } from './events/event-bridge.tsx';

const transport: ApiTransportPort = { send: () => Promise.reject(new Error('unused')) };
function storage() {
  const values = new Map<string, string>();
  return { values, getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); }, removeItem: (key: string) => { values.delete(key); } };
}
function cardAdded(id: number, trackId = 'track-1') {
  return { _id: id, eventVersion: 3, ev: 'task.updated', data: { track_id: trackId } };
}

afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); });

describe('event composition', () => {
  it('T-A3 publishes driver unauthorized transitions through the browser channel', () => {
    vi.useFakeTimers();
    const notify = vi.fn();
    const composition = createBrowserEventComposition({ storage: storage(), transport, unauthorizedChannel: { notify } });
    const driver = composition.driver as unknown as { onUnauthorized: () => void };
    driver.onUnauthorized();
    expect(notify).toHaveBeenCalledOnce();
  });

  it('T-A1 shares one cursor store so reconnect subscribes from the synchronously delivered event id', () => {
    vi.useFakeTimers();
    const fake = createFakeSocketFactory();
    const driverFactory = vi.fn((options: ConstructorParameters<typeof WebSocketDriver>[0]) => new WebSocketDriver(options));
    const composition = createEventComposition({ storage: storage(), transport, socketFactory: fake.factory, driverFactory, url: 'ws://example/api/events' });
    render(<EventBridge client={new QueryClient()} stream={composition.stream} syncEventVersion={3} dbInstanceId="db-a" cursor={composition.store} />);
    expect(fake.constructionCount).toBe(1);
    fake.sockets[0].open(); fake.sockets[0].message(cardAdded(23));
    fake.sockets[0].close(); vi.advanceTimersByTime(0); vi.advanceTimersByTime(500);
    fake.sockets[1].open();
    expect(JSON.parse(fake.sockets[1].sent[0])).toEqual({ sub: ['*'], since: 23 });
    expect(driverFactory).toHaveBeenCalledOnce();
    expect(composition.driver).toBe(driverFactory.mock.results[0].value);
  });

  it('T-B2 carries a real socket card event through the bridge into track invalidation', async () => {
    const fake = createFakeSocketFactory();
    const composition = createEventComposition({ storage: storage(), transport, socketFactory: fake.factory, url: 'ws://example/api/events' });
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries').mockImplementation(() => Promise.resolve());
    render(<EventBridge client={client} stream={composition.stream} syncEventVersion={3} dbInstanceId="db-a" cursor={composition.store} />);
    await waitFor(() => expect(fake.constructionCount).toBe(1));
    fake.sockets[0].open(); fake.sockets[0].message(cardAdded(1, 'track-live'));
    await waitFor(() => expect(invalidate.mock.calls.map(([filters]) => filters?.queryKey)).toContainEqual(['track', 'track-live']));
  });

  it('refreshes before resuming a v1 snapshot and receives the next live event', async () => {
    const fake = createFakeSocketFactory();
    const composition = createEventComposition({ storage: storage(), transport, socketFactory: fake.factory, url: 'ws://example/api/v1/events' });
    const client = new QueryClient();
    let refreshed!: (value: string) => void;
    const options = { staleTime: Infinity, retry: false };
    const read = vi.fn(() => new Promise<string>(resolve => { refreshed = resolve; }));
    const observer = new QueryObserver(client, { ...options, queryKey: ['track'], queryFn: read, initialData: 'before' });
    const missing = new QueryObserver(client, { ...options, queryKey: ['gone'], initialData: 'deleted',
      queryFn: () => Promise.reject(new ApiError({ kind: 'http', status: 404, code: 'not_found', message: 'gone' })) });
    const releaseRead = observer.subscribe(() => {});
    const releaseMissing = missing.subscribe(() => {});
    render(<EventBridge client={client} stream={composition.stream} syncEventVersion={3} dbInstanceId="db-a" cursor={composition.store} />);
    fake.sockets[0].open();
    fake.sockets[0].message(JSON.stringify({ kind: '_snapshot_required', tipId: 42 }));
    await waitFor(() => expect(read).toHaveBeenCalled());
    expect(composition.store.read()).toBeNull();
    expect(fake.constructionCount).toBe(1);
    refreshed('after');
    await waitFor(() => expect(fake.sockets[1]?.url).toContain('since=42'));
    fake.sockets[1].open();
    fake.sockets[1].message(cardAdded(43));
    expect(composition.store.read()).toBe(43);
    expect(client.getQueryData(['track'])).toBe('after');
    expect(client.getQueryData(['gone'])).toBeUndefined();
    releaseRead();
    releaseMissing();
  });

  it.each(['retry', 'cancel', 'unmount'])('holds the cursor across a failed refresh (%s)', async scenario => {
    vi.useFakeTimers();
    const fake = createFakeSocketFactory();
    const composition = createEventComposition({ storage: storage(), transport, socketFactory: fake.factory, url: 'ws://example/api/v1/events' });
    const client = new QueryClient();
    const read = vi.fn().mockResolvedValue('recovered');
    if (scenario === 'cancel') read.mockImplementationOnce(() => new Promise<string>(() => {}));
    else read.mockRejectedValueOnce(new Error('offline'));
    const observer = new QueryObserver(client, { queryKey: ['track'], initialData: 'before', staleTime: Infinity, retry: false, queryFn: read });
    const release = observer.subscribe(() => {});
    const view = render(<EventBridge client={client} stream={composition.stream} syncEventVersion={3} dbInstanceId="db-a" cursor={composition.store} />);
    fake.sockets[0].message(JSON.stringify({ kind: '_snapshot_required', tipId: 17 }));
    await vi.advanceTimersByTimeAsync(100);
    expect(read).toHaveBeenCalledTimes(1);
    expect(composition.store.read()).toBeNull();
    if (scenario === 'cancel') {
      await client.cancelQueries({ queryKey: ['track'] });
      await vi.advanceTimersByTimeAsync(0);
      expect(composition.store.read()).toBeNull();
      expect(fake.constructionCount).toBe(1);
    }
    if (scenario === 'unmount') view.unmount();
    await vi.advanceTimersByTimeAsync(3000);
    expect(composition.store.read()).toBe(scenario === 'unmount' ? null : 17);
    expect(fake.constructionCount).toBe(scenario === 'unmount' ? 1 : 2);
    if (scenario !== 'unmount') {
      expect(read).toHaveBeenCalledTimes(2);
      expect(client.getQueryData(['track'])).toBe('recovered');
    }
    release();
  });

  it('T-B3 snapshot-required clears once and bounces to exactly one since-zero socket', async () => {
    const fake = createFakeSocketFactory();
    const composition = createEventComposition({ storage: storage(), transport, socketFactory: fake.factory, url: 'ws://example/api/events' });
    const client = new QueryClient();
    const clear = vi.spyOn(client, 'clear');
    render(<EventBridge client={client} stream={composition.stream} syncEventVersion={3} dbInstanceId="db-a" cursor={composition.store} />);
    await waitFor(() => expect(fake.constructionCount).toBe(1));
    fake.sockets[0].open(); fake.sockets[0].message(cardAdded(8));
    fake.sockets[0].message(JSON.stringify({ ev: '_snapshot_required' }));
    expect(clear).toHaveBeenCalledOnce();
    expect(fake.constructionCount - fake.closeCount).toBe(1);
    fake.sockets[1].open();
    expect(JSON.parse(fake.sockets[1].sent[0])).toEqual({ sub: ['*'], since: 0 });
  });
});
