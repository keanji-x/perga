// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { fixtureSession } from '../router/session-contract-fixture.ts';
import { sessionPage, sessionReply, sessionInput } from '../router/conversation-session-data.ts';
import { ConversationProvider, useConversationCommands } from '../conversations/public.tsx';
import { useConversationStore } from '../conversations/store.ts';
import { plannerRunQueryOptions } from './conversation-queries.ts';
import { usePlannerMutations } from './planner-mutations.ts';
import type { ConversationRun } from '../../../../core/domain/conversation-display.ts';

const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
afterEach(cleanup);
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}
function wrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}><ConversationProvider>{children}</ConversationProvider></QueryClientProvider>;
}

it('publishes only the latest serialized permission ACK and cancels a stale run read', async () => {
  const writes = [deferred<ApiTransportResponse>(), deferred<ApiTransportResponse>(), deferred<ApiTransportResponse>()];
  const stale = deferred<ApiTransportResponse>();
  let puts = 0;
  let oldSignal: { readonly aborted: boolean } | undefined;
  const transport: ApiTransportPort = { send: request => {
    if (request.method === 'PATCH') return writes[puts++].promise;
    oldSignal = request.signal;
    return stale.promise;
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const options = plannerRunQueryOptions(transport, 'a', unauthorized);
  // Seed through the production schema, then hold the next GET.
  const seedTransport: ApiTransportPort = { send: () => Promise.resolve(sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'ask' })))) };
  client.setQueryData(options.queryKey, await plannerRunQueryOptions(seedTransport, 'a', unauthorized).queryFn({ signal: new AbortController().signal }));
  const reading = client.fetchQuery({ ...options, queryFn: options.queryFn }).catch(() => undefined);
  const { result } = renderHook(() => usePlannerMutations(transport, 'a', 't', unauthorized, useConversationCommands()), { wrapper: wrapper(client) });
  const first = result.current.setPermissionMode('full');
  const second = result.current.setPermissionMode('never');
  await waitFor(() => expect(puts).toBe(1));
  await act(async () => { writes[0].resolve(sessionReply(fixtureSession('a', 't', { permissionMode: 'full' }))); await Promise.resolve(); });
  expect(client.getQueryData<ConversationRun>(options.queryKey)?.session.permissionMode).toBe('ask');
  expect(oldSignal?.aborted).toBe(true);
  expect(puts).toBe(2);
  await act(async () => { writes[1].resolve(sessionReply(fixtureSession('a', 't', { permissionMode: 'never' }))); await second; await first; });
  stale.resolve(sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'ask' }))));
  await reading;
  expect(client.getQueryData<ConversationRun>(options.queryKey)?.session.permissionMode).toBe('never');
  const refused = result.current.setPermissionMode('full');
  writes[2].resolve({ status: 409, statusText: 'Conflict', body: { code: 'precondition_failed', error: 'Not available' } });
  await expect(refused).rejects.toThrow();
  expect(client.getQueryData<ConversationRun>(options.queryKey)?.session.permissionMode).toBe('never');
  client.clear();
});

it.each([false, true])('holds a production store send behind permission writes and restores attachments on failure (running=%s)', async running => {
  const write = deferred<ApiTransportResponse>();
  let posts = 0;
  const transport: ApiTransportPort = { send: async request => {
    if (request.method === 'PATCH') return write.promise;
    if (request.method === 'POST') { posts++; return sessionReply(sessionInput('a')); }
    if (request.path.includes('/items?')) return sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'ask' }), [], running ? {
      id: 'turn', sessionId: 'a', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: 1, completedAtMs: null,
    } : null));
    return sessionReply([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result } = renderHook(() => ({ store: useConversationStore(transport, unauthorized, {
    id: 't', cardId: 'a', title: 'Track', provider: 'codex', kind: 'agent', isLead: false, state: 'idle', updatedAt: 1, cardTitle: null,
  }, { rows: [], rememberOn: 't' }), registry: useConversationCommands() }), { wrapper: wrapper(client) });
  await waitFor(() => expect(result.current.store.runReady).toBe(true));
  const images = [{ id: 'image', url: '/image', contentType: 'image/png', size: 1 }];
  let sent: Promise<void> | null = null;
  act(() => {
    void result.current.store.setPermissionMode('full');
    sent = result.current.store.send('a', 'keep draft', images, true);
  });
  await act(async () => { await Promise.resolve(); });
  expect(posts).toBe(0);
  await act(async () => { write.resolve({ status: 500, statusText: 'Error', body: { error: 'write failed' } }); await sent; });
  expect(posts).toBe(0);
  expect(result.current.store.permissionMode).toBe('ask');
  expect(result.current.store.actionError).not.toBeNull();
  expect(result.current.store.failedSend?.delivery).toBe('refused');
  act(() => { result.current.store.discardFailedSend(result.current.store.failedSend!.key); });
  expect(result.current.registry.composerOf('a')).toMatchObject({ text: 'keep draft', attachments: images });
  client.clear();
});

it('scopes delayed ACKs to their original card across switching and unmounting', async () => {
  const writes = [deferred<ApiTransportResponse>(), deferred<ApiTransportResponse>()];
  let count = 0;
  const transport: ApiTransportPort = { send: request => request.method === 'PATCH' ? writes[count++].promise
    : Promise.resolve(sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'ask' })))) };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  for (const id of ['a', 'b']) {
    const options = plannerRunQueryOptions(transport, id, unauthorized);
    client.setQueryData(options.queryKey, await options.queryFn({ signal: new AbortController().signal }));
  }
  const { result, rerender, unmount } = renderHook(({ id }) => usePlannerMutations(transport, id, 't', unauthorized, useConversationCommands()), {
    initialProps: { id: 'a' }, wrapper: wrapper(client),
  });
  const first = result.current.setPermissionMode('full');
  rerender({ id: 'b' });
  const second = result.current.setPermissionMode('never');
  writes[1].resolve(sessionReply(fixtureSession('b', 't', { permissionMode: 'never' })));
  await second;
  unmount();
  writes[0].resolve(sessionReply(fixtureSession('a', 't', { permissionMode: 'full' })));
  await first;
  expect(client.getQueryData<ConversationRun>(plannerRunQueryOptions(transport, 'a', unauthorized).queryKey)?.session.permissionMode).toBe('full');
  expect(client.getQueryData<ConversationRun>(plannerRunQueryOptions(transport, 'b', unauthorized).queryKey)?.session.permissionMode).toBe('never');
  client.clear();
});

it('keeps an unknown retry and its key when a permission prerequisite fails', async () => {
  const write = deferred<ApiTransportResponse>();
  const posts: string[] = [];
  const transport: ApiTransportPort = { send: async request => {
    if (request.method === 'PATCH') return write.promise;
    if (request.method === 'POST') { posts.push(request.headers?.['Idempotency-Key'] ?? ''); return sessionReply(sessionInput('a')); }
    if (request.path.includes('/items?')) return sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'ask' })));
    return sessionReply([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result } = renderHook(() => ({ store: useConversationStore(transport, unauthorized, {
    id: 't', cardId: 'a', title: 'Track', provider: 'codex', kind: 'agent', isLead: false, state: 'idle', updatedAt: 1, cardTitle: null,
  }, { rows: [], rememberOn: 't' }), registry: useConversationCommands() }), { wrapper: wrapper(client) });
  await waitFor(() => expect(result.current.store.runReady).toBe(true));
  act(() => { result.current.registry.editOutbox('a', () => [{ key: 'original-key', phase: 'failed', delivery: 'unknown', message: '', fromComposer: true,
    echo: { id: 'echo', text: 'possibly stored', author: 'you', atMs: 1, localSend: true, queued: false } }]); });
  act(() => { void result.current.store.setPermissionMode('full'); result.current.store.retrySend('original-key'); });
  await act(async () => { write.resolve({ status: 500, statusText: 'Error', body: { error: 'permission failed' } }); await Promise.resolve(); });
  await waitFor(() => expect(result.current.store.failedSend).toMatchObject({ key: 'original-key', delivery: 'unknown' }));
  expect(result.current.registry.composerOf('a').text).toBe('');
  expect(posts).toEqual([]);
  act(() => { result.current.store.retrySend('original-key'); });
  await waitFor(() => expect(posts).toEqual(['original-key']));
  client.clear();
});

it.each([false, true])('protects a newer confirmed GET from an old ACK across A B A (new write=%s)', async newerWrite => {
  const writes = [deferred<ApiTransportResponse>(), deferred<ApiTransportResponse>()];
  let puts = 0;
  let holdReads = false;
  const transport: ApiTransportPort = { send: request => request.method === 'PATCH' ? writes[puts++].promise
    : holdReads ? new Promise(() => undefined) : Promise.resolve(sessionReply(sessionPage(fixtureSession('a', 't', { permissionMode: 'never' })))) };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result, rerender } = renderHook(({ id }) => ({ store: useConversationStore(transport, unauthorized, {
    id: 't', cardId: id, title: 'Track', provider: 'codex', kind: 'agent', isLead: false, state: 'idle', updatedAt: 1, cardTitle: null,
  }, { rows: [], rememberOn: 't' }) }), { initialProps: { id: 'a' }, wrapper: wrapper(client) });
  await waitFor(() => expect(result.current.store.runReady).toBe(true));
  act(() => { void result.current.store.setPermissionMode('full'); });
  rerender({ id: 'b' }); rerender({ id: 'a' });
  if (newerWrite) {
    act(() => { void result.current.store.setPermissionMode('never'); });
    await waitFor(() => expect(puts).toBe(1));
  }
  await client.fetchQuery(plannerRunQueryOptions(transport, 'a', unauthorized));
  holdReads = true;
  await act(async () => { writes[0].resolve(sessionReply(fixtureSession('a', 't', { permissionMode: 'full' }))); await Promise.resolve(); });
  expect(client.getQueryData<ConversationRun>(plannerRunQueryOptions(transport, 'a', unauthorized).queryKey)?.session.permissionMode).toBe('never');
  if (newerWrite) {
    await waitFor(() => expect(puts).toBe(2));
    await act(async () => { writes[1].resolve(sessionReply(fixtureSession('a', 't', { permissionMode: 'never' }))); await Promise.resolve(); });
  }
  client.clear();
});

it.each(['succeeds', 'fails'] as const)('holds a production store send behind an in-flight provider switch (%s)', async outcome => {
  const write = deferred<ApiTransportResponse>();
  const events: string[] = [];
  let session = fixtureSession('a', 't', { provider: 'codex', permissionMode: 'ask' });
  const transport: ApiTransportPort = { send: async request => {
    if (request.method === 'PATCH') { events.push('patch'); return write.promise; }
    if (request.method === 'POST') { events.push('post'); return sessionReply(sessionInput('a')); }
    if (request.path.includes('/items?')) return sessionReply(sessionPage(session));
    return sessionReply([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result } = renderHook(() => ({ store: useConversationStore(transport, unauthorized, {
    id: 't', cardId: 'a', title: 'Track', provider: 'codex', kind: 'agent', isLead: false, state: 'idle', updatedAt: 1, cardTitle: null,
  }, { rows: [], rememberOn: 't' }), registry: useConversationCommands() }), { wrapper: wrapper(client) });
  await waitFor(() => expect(result.current.store.runReady).toBe(true));
  const images = outcome === 'fails' ? [{ id: 'image', url: '/image', contentType: 'image/png', size: 1 }] : [];
  let sent: Promise<void> | null = null;
  act(() => {
    result.current.store.setModel({ model: 'sonnet', reasoning_effort: null }, 'claude');
    sent = result.current.store.send('a', 'first on claude', images, true);
  });
  await act(async () => { await Promise.resolve(); });
  expect(events).toEqual(['patch']);

  if (outcome === 'succeeds') {
    session = fixtureSession('a', 't', { provider: 'claude', model: 'sonnet', permissionMode: 'default' });
    await act(async () => {
      write.resolve(sessionReply(session));
      await sent;
    });
    expect(events).toEqual(['patch', 'post']);
    await waitFor(() => expect(result.current.store.session?.provider).toBe('claude'));
  } else {
    await act(async () => {
      write.resolve({ status: 409, statusText: 'Conflict', body: { code: 'precondition_failed', error: 'Provider changes require an idle session.' } });
      await sent;
    });
    expect(events).toEqual(['patch']);
    expect(result.current.store.actionError).toBe('Provider changes require an idle session.');
    expect(result.current.store.failedSend?.delivery).toBe('refused');
    act(() => { result.current.store.discardFailedSend(result.current.store.failedSend!.key); });
    expect(result.current.registry.composerOf('a')).toMatchObject({ text: 'first on claude', attachments: images });
    // A subsequent send after the failed switch must not be blocked by a stale rejected barrier.
    let retrySent: Promise<void> | null = null;
    act(() => { retrySent = result.current.store.send('a', 'send on original', [], true); });
    await act(async () => { await retrySent; });
    expect(events).toEqual(['patch', 'post']);
  }
  client.clear();
});

it('orders a permission write after an in-flight provider switch', async () => {
  const patches = [deferred<ApiTransportResponse>(), deferred<ApiTransportResponse>()];
  const bodies: unknown[] = [];
  let session = fixtureSession('a', 't', { provider: 'codex', permissionMode: 'ask' });
  const transport: ApiTransportPort = { send: async request => {
    if (request.method === 'PATCH') {
      const index = bodies.length;
      bodies.push(request.body);
      return patches[index].promise;
    }
    if (request.path.includes('/items?')) return sessionReply(sessionPage(session));
    return sessionReply([]);
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const { result } = renderHook(() => useConversationStore(transport, unauthorized, {
    id: 't', cardId: 'a', title: 'Track', provider: 'codex', kind: 'agent', isLead: false, state: 'idle', updatedAt: 1, cardTitle: null,
  }, { rows: [], rememberOn: 't' }), { wrapper: wrapper(client) });
  await waitFor(() => expect(result.current.runReady).toBe(true));

  act(() => {
    result.current.setModel({ model: 'sonnet', reasoning_effort: null }, 'claude');
    void result.current.setPermissionMode('full');
  });
  await waitFor(() => expect(bodies).toEqual([{ provider: 'claude', model: 'sonnet', reasoningEffort: null }]));

  session = fixtureSession('a', 't', { provider: 'claude', model: 'sonnet', permissionMode: 'default' });
  await act(async () => {
    patches[0].resolve(sessionReply(session));
    await Promise.resolve();
  });
  await waitFor(() => expect(bodies).toEqual([
    { provider: 'claude', model: 'sonnet', reasoningEffort: null },
    { permissionMode: 'full' },
  ]));
  session = fixtureSession('a', 't', { provider: 'claude', model: 'sonnet', permissionMode: 'full' });
  await act(async () => {
    patches[1].resolve(sessionReply(session));
    await Promise.resolve();
  });
  await waitFor(() => expect(result.current.permissionMode).toBe('full'));
  expect(result.current.session?.provider).toBe('claude');
  client.clear();
});
