import { onlineManager,QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { act,cleanup,fireEvent,render,renderHook,screen,waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach,expect,it,vi } from 'vitest';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import type { ApiRequest,ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { conversationCommands } from '../../features/chat/thread/composer-command.ts';
import { ChatComposer } from '../../features/chat/thread/public.tsx';
import { trackDetailQueryOptions } from '../providers/queries.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { renderAssistantProviderFixture } from './assistant-provider-fixture.tsx';
import { usePlannerSwitchControl } from './planner-switch-control.ts';
import { renderPlannerSwitchFixture } from './planner-switch-fixture.tsx';
import { fixtureBootstrap } from './session-contract-fixture.ts';

async function open(name = 'Other conversation') {
  await userEvent.click(await screen.findByRole('button', { name: new RegExp(`Conversation ${name}`) }));
  const input = await screen.findByRole('combobox', { name: 'Message' });
  await screen.findByText('Ready for switching');
  return input;
}
async function type(input: HTMLElement, text: string) {
  input.textContent = text;
  const range = document.createRange(); range.setStart(input.firstChild!, text.length); range.collapse(true);
  const selection = window.getSelection()!; selection.removeAllRanges(); selection.addRange(range);
  await act(async () => { fireEvent.input(input); await Promise.resolve(); });
}
async function submit(input: HTMLElement, text = '/planner') {
  await type(input, text); fireEvent.click(screen.getByRole('button', { name: 'Send' }));
}
const writes = (fixture: ReturnType<typeof renderPlannerSwitchFixture>) => fixture.requests.filter(request => request.method === 'PATCH' && typeof request.body === 'object' && request.body !== null && 'leadSessionId' in request.body);
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each(['/track/daily?panel=conversations', '/'])('switches the selected Assistant using its real Track CAS pair on %s', async initial => {
  const fixture = renderPlannerSwitchFixture({ initial });
  const input = await open(); await submit(input);
  await waitFor(() => expect(writes(fixture).map(({ path, body }) => ({ path, body }))).toEqual([{
    path: '/api/v1/tracks/daily', body: { leadSessionId: 'second-planner' },
  }]));
  await waitFor(() => expect(input.textContent).toBe(''));
  expect(input.textContent).toBe('');
  expect(screen.queryAllByRole('alert')).toHaveLength(0);
  if (initial !== '/') await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  expect(screen.getAllByRole('button', { name: /Conversation Other conversation/ })).toHaveLength(1);
  expect(screen.getAllByRole('button', { name: /Conversation Daily Planner conversation/ })).toHaveLength(1);
});

it('refuses arguments before any write and keeps the command draft', async () => {
  const fixture = renderPlannerSwitchFixture(); const input = await open(); await submit(input, '/planner now');
  await screen.findByText('Use /planner without arguments.');
  await waitFor(() => expect(input.textContent).toBe('/planner now')); expect(writes(fixture)).toHaveLength(0);
});

it('shows the specific refusal and blocking role, preserves draft, and never retries', async () => {
  const fixture = renderPlannerSwitchFixture({ switch: () => ({ status: 409, statusText: 'Conflict',
    body: { error: 'busy', code: 'planner_pending_messages', card_id: 'daily-planner' } }) });
  const input = await open(); await submit(input);
  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Wait for pending messages to finish. (Original Planner: Daily Planner conversation)'));
  await waitFor(() => expect(input.textContent).toBe('/planner')); expect(writes(fixture)).toHaveLength(1);
});

it('releases an offline refusal before reconciliation, restores the draft, and never resends', async () => {
  let finish!: () => void;
  const releaseReads: (() => void)[] = [];
  const fixture = renderPlannerSwitchFixture({
    reply: request => !onlineManager.isOnline() && request.method === 'GET'
      ? new Promise(resolve => releaseReads.push(() => resolve({ status: 503, statusText: 'Offline', body: {} }))) : undefined,
    switch: () => new Promise(resolve => {
      finish = () => resolve({ status: 409, statusText: 'Conflict',
        body: { error: 'busy', code: 'planner_open_ask', card_id: 'daily-planner' } });
    }),
  });
  const input = await open(); await submit(input);
  await waitFor(() => expect(finish).toBeTypeOf('function'));
  onlineManager.setOnline(false);
  try {
    act(() => { finish(); });
    await waitFor(() => expect(screen.getByRole('alert').textContent)
      .toContain('Resolve the open questions first. (Original Planner: Daily Planner conversation)'));
    await waitFor(() => expect(input.textContent).toBe('/planner'));
    expect(writes(fixture)).toHaveLength(1);
    // A new explicit intent reaches recovery admission: the old pending guard has been released.
    await submit(input);
    await screen.findByText('Planner switching was not sent.');
    await waitFor(() => expect(input.textContent).toBe('/planner')); expect(writes(fixture)).toHaveLength(1);
  } finally {
    act(() => { releaseReads.forEach(release => release()); });
    onlineManager.setOnline(true);
  }
});

it.each(['success', 'response lost'])('settles %s offline as unconfirmed without resending', async mode => {
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({
    reply: request => {
      if (!onlineManager.isOnline() && request.method === 'GET') throw new Error('offline read');
      return undefined;
    },
    switch: (_request, commit) => new Promise((resolve, reject) => {
      finish = () => { commit(); if (mode === 'success') resolve(fixture.receipt()); else reject(new Error('response lost')); };
    }),
  });
  const input = await open(); await submit(input);
  await waitFor(() => expect(finish).toBeTypeOf('function'));
  onlineManager.setOnline(false);
  try {
    act(() => { finish(); });
    await screen.findByText('Planner switching is unconfirmed. Refresh to check.');
    await waitFor(() => expect(input.textContent).toBe('/planner'));
    expect(fixture.requests.filter(request => request.method === 'GET' &&
      (request.path === '/api/v1/tracks/daily' || request.path === '/api/v1/tracks/daily/sessions')).length).toBeGreaterThan(1);
    expect(writes(fixture)).toHaveLength(1);
    await submit(input);
    await screen.findByText('Planner switching was not sent.');
    expect(writes(fixture)).toHaveLength(1);
  } finally { onlineManager.setOnline(true); }
});

it('prevents reentry while a response is pending', async () => {
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => new Promise(resolve => {
    finish = () => { commit(); resolve(fixture.receipt()); };
  }) });
  const input = await open(); await submit(input);
  await waitFor(() => expect(finish).toBeTypeOf('function'));
  await submit(input); expect(writes(fixture)).toHaveLength(1);
  act(() => { finish(); });
  await waitFor(() => expect(input.textContent).toBe(''));
  expect(writes(fixture)).toHaveLength(1);
});

it('does not put a late refusal in another composer and restores only the original draft', async () => {
  let finish!: () => void;
  renderPlannerSwitchFixture({ switch: () => new Promise(resolve => {
    finish = () => resolve({ status: 409, statusText: 'Conflict', body: { code: 'planner_changed', error: 'changed', card_id: 'second-planner' } });
  }) });
  let input = await open(); await submit(input); await waitFor(() => expect(finish).toBeTypeOf('function'));
  await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  input = await open('Daily Planner conversation'); await type(input, 'Other draft');
  act(() => { finish(); });
  expect(input.textContent).toBe('Other draft'); expect(screen.queryByRole('alert')).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  input = await open(); await waitFor(() => expect(input.textContent).toBe('/planner'));
});

it.each(['response lost', 'decode'])('reconciles %s without resubmitting and reports unconfirmed', async mode => {
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => {
    commit(); if (mode === 'response lost') throw new Error('disconnected');
    return { status: 200, statusText: 'OK', body: {} };
  } });
  await submit(await open());
  await screen.findByText('Planner switching is unconfirmed. Refresh to check.');
  expect(writes(fixture)).toHaveLength(1);
});

it('does not offer planner in the current Planner composer', async () => {
  renderPlannerSwitchFixture(); const input = await open('Daily Planner conversation'); await type(input, '/');
  await screen.findByRole('listbox', { name: 'Commands' });
  expect(screen.queryByRole('option', { name: /^planner/ })).toBeNull();
});

it.each(['/planner', '/planner now'])('never sends unavailable typed %s from the Planner composer', async command => {
  const fixture = renderPlannerSwitchFixture(); const input = await open('Daily Planner conversation');
  await submit(input, command);
  await screen.findByText('Planner switching is not available in this conversation.');
  await waitFor(() => expect(input.textContent).toBe(command)); expect(writes(fixture)).toHaveLength(0);
});

it.each(['/planner', '/planner now'])('recognizes unavailable %s without a scope binding', async command => {
  const send = vi.fn();
  render(<ChatComposer onSend={send} commands={conversationCommands({})} />);
  const input = screen.getByRole('textbox', { name: 'Message' }); await submit(input, command);
  await screen.findByText('Planner switching is not available in this conversation.');
  await waitFor(() => expect(input.textContent).toBe(command)); expect(send).not.toHaveBeenCalled();
});

it('reports a definitely unsent switch without a POST', async () => {
  const fixture = renderPlannerSwitchFixture(); const input = await open();
  onlineManager.setOnline(false);
  try {
    await submit(input);
    await screen.findByText('Planner switching was not sent.');
    await waitFor(() => expect(input.textContent).toBe('/planner')); expect(writes(fixture)).toHaveLength(0);
  } finally { onlineManager.setOnline(true); }
});

it('uses the selected scope rather than the page Track, reads an uncached pair once, and never retries CAS', async () => {
  const fixture = renderPlannerSwitchFixture();
  await screen.findByRole('button', { name: /Conversation Other conversation/ });
  const detail = fixture.client.getQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('daily'))!;
  cleanup(); document.getElementById('root')?.remove();
  const requests: ApiRequest[] = [];
  const transport: ApiTransportPort = { send: request => {
    requests.push(request);
    return Promise.resolve(request.method === 'GET'
      ? { status: 200, statusText: 'OK', body: { ...detail, track: { ...detail.track, id: 'actual-track' }, sessions: detail.sessions.map(session => ({ ...session, trackId: 'actual-track' })) } }
      : { status: 409, statusText: 'Conflict', body: { code: 'planner_changed', error: 'ABA', card_id: 'daily-planner' } });
  } };
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={fixture.client}>{children}</QueryClientProvider>;
  const hook = renderHook(() => usePlannerSwitchControl(transport, createUnauthorizedChannel({ enqueue: task => task() }), {
    id: 'actual-track', cardId: 'second-planner', provider: 'codex', kind: 'agent', isLead: false, cardTitle: null, updatedAt: 2,
  }), { wrapper });
  await act(async () => { await expect(hook.result.current()).rejects.toThrow('The Planner changed.'); });
  expect(requests.filter(request => request.method === 'PATCH').map(({ path, body }) => ({ path, body }))).toEqual([{
    path: '/api/v1/tracks/actual-track', body: { leadSessionId: 'second-planner' },
  }]);
});

it('does not pause or write when the uncached detail preflight fails offline', async () => {
  const fixture = renderPlannerSwitchFixture(); const input = await open();
  fixture.client.removeQueries({ queryKey: queryKeys.trackDetail('daily') });
  // The pane already owns a valid selected scope, but its required CAS snapshot is unavailable.
  const preflightRequests: ApiRequest[] = [];
  const transport: ApiTransportPort = { send: request => {
    preflightRequests.push(request); return Promise.reject(new Error('read failed'));
  } };
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={fixture.client}>{children}</QueryClientProvider>;
  const hook = renderHook(() => usePlannerSwitchControl(transport, createUnauthorizedChannel({ enqueue: task => task() }), {
    id: 'missing-detail', cardId: 'second-planner', provider: 'codex', kind: 'agent', isLead: false, cardTitle: null, updatedAt: 2,
  }), { wrapper });
  onlineManager.setOnline(false);
  const paused = fixture.client.fetchQuery(trackDetailQueryOptions(transport, 'missing-detail',
    createUnauthorizedChannel({ enqueue: task => task() }))).catch(() => undefined);
  expect(fixture.client.getQueryState(queryKeys.trackDetail('missing-detail'))?.fetchStatus).toBe('paused');
  let failure: unknown;
  let outcome!: Promise<void>;
  act(() => { outcome = hook.result.current().then(() => undefined, error => { failure = error; }); });
  try {
    await waitFor(() => expect(failure).toBeInstanceOf(Error));
    expect((failure as Error).message).toContain('Planner switching is unconfirmed.');
  } finally { onlineManager.setOnline(true); await act(async () => { await outcome; await paused; }); }
  expect(preflightRequests.map(request => request.method)).toEqual(['GET']);
  expect(writes(fixture)).toHaveLength(0); expect(input.textContent).toBe('');
});

it('accepts events before the mutation response without resubmission', async () => {
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => new Promise(resolve => {
    commit(); finish = () => resolve(fixture.receipt());
  }) });
  const input = await open(); await submit(input); await waitFor(() => expect(finish).toBeTypeOf('function'));
  await act(async () => {
    await fixture.client.invalidateQueries({ queryKey: queryKeys.trackDetail('daily') });

  });
  await waitFor(() => expect(input.textContent).toBe(''));
  act(() => { finish(); });
  await waitFor(() => expect(input.textContent).toBe(''));
  expect(screen.queryByRole('alert')).toBeNull(); expect(writes(fixture)).toHaveLength(1);
});

it('reads v1 provider model identifiers without a legacy model write', async () => {
  const fixture = renderAssistantProviderFixture({ existingProvider: 'claude', bootstrap: fixtureBootstrap(['claude'], ['selected']) });
  await userEvent.click(await screen.findByRole('button', { name: 'Conversation Saved Assistant' }));
  await screen.findByRole('combobox', { name: 'Message' });
  await screen.findByText('Saved reply');
  await userEvent.click(screen.getByRole('button', { name: /^Model:/ }));
  await screen.findByRole('radio', { name: 'selected' });
  expect(fixture.requests.filter(request => request.path.startsWith('/api/v1/bootstrap?card_id=')).map(request => request.path)).toContain('/api/v1/bootstrap?card_id=dormant');
  expect(screen.queryByRole('radio', { name: 'Codex' })).toBeNull();
  fireEvent.keyDown(document.activeElement!, { key: 'Escape' });
  expect(screen.getByRole('button', { name: 'Attach an image' }).getAttribute('aria-disabled')).toBe('true');
  expect(fixture.requests.some(request => request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path))).toBe(false);
  expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'))).toHaveLength(0);
});

it.each(['mounted', 'reopened'] as const)('full composer preserves edits after pending command success (%s)', async mode => {
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => new Promise(resolve => {
    finish = () => { commit(); resolve(fixture.receipt()); };
  }) });
  let input = await open(); await submit(input);
  await waitFor(() => expect(finish).toBeTypeOf('function'));
  if (mode === 'reopened') {
    await userEvent.click(screen.getByRole('button', { name: 'Close conversation' })); input = await open();
  }
  await type(input, 'New full draft');
  act(() => { finish(); });
  await waitFor(() => expect(fixture.client.getQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('daily'))?.track.leadSessionId).toBe('second-planner'));
  expect(input.textContent).toBe('New full draft');
});


it.each(['missing-target', 'still-lead', 'valid'] as const)('confirms a switch from a fresh aggregate (%s)', async mode => {
  const fixture = renderPlannerSwitchFixture();
  await screen.findByRole('button', { name: /Conversation Other conversation/ });
  const snapshot = fixture.client.getQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('daily'))!;
  cleanup(); document.getElementById('root')?.remove();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(queryKeys.trackDetail('daily'), snapshot);
  fixture.commit();
  const transport: ApiTransportPort = { send: request => Promise.resolve(request.method === 'PATCH' ? fixture.receipt() : ({
    status: 200, statusText: 'OK', body: mode === 'still-lead' ? snapshot : {
      ...fixture.snapshot(), sessions: fixture.sessions().filter(session => mode !== 'missing-target' || session.id !== 'second-planner'),
    },
  })) };
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  const hook = renderHook(() => usePlannerSwitchControl(transport, createUnauthorizedChannel({ enqueue: task => task() }), {
    id: 'daily', cardId: 'second-planner', provider: 'codex', kind: 'agent', isLead: false, cardTitle: null, updatedAt: 2,
  }), { wrapper });
  await act(async () => {
    if (mode === 'valid') expect(await hook.result.current()).toBe(true);
    else await expect(hook.result.current()).rejects.toThrow('Planner switching is unconfirmed.');
  });
});
