import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PergaOverviewRoute } from './perga-overview.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureTrack } from './session-contract-fixture.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import '../../styles/entry.css';
import { useEffect } from 'react';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { useConversationCommands, type ConversationRegistry } from '../conversations/public.tsx';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionInput, sessionPage, sessionReply, sessionTurn } from './conversation-session-data.ts';

afterEach(cleanup);

it.each(['button', 'slash'] as const)('stops accepted startup immediately through %s while run reconciliation hangs', async gesture => {
  await page.viewport(1280, 850);
  let accepted = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.endsWith('/messages') && request.method === 'POST') {
      accepted = true; return sessionReply(sessionInput('daily-planner'));
    }
    if (request.path.endsWith('/control')) return sessionReply(view.sessions[0]);
    if (accepted && request.path.includes('limit=1')) return new Promise(() => {});
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Start slowly');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  if (gesture === 'button') await page.getByRole('button', { name: 'Stop', exact: true }).click();
  else {
    await field.fill('/stop');
    await expect.element(page.getByRole('option', { name: /^stop/ })).toBeVisible();
    await userEvent.keyboard('{Enter}'); await userEvent.keyboard('{Enter}');
  }
  await expect.poll(() => view.requests.filter(request => request.path.endsWith('/control')).map(request => request.body)).toEqual([{ action: 'stop', turnId: 'turn-1' }]);
  view.client.clear();
});

it.each(['queued', 'starting'])('stops reloaded %s lastTurn startup without an activeTurn', async status => {
  await page.viewport(1280, 850);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.endsWith('/control')) return sessionReply(view.sessions[0]);
    if (request.path.includes('/daily-planner/items?')) return sessionReply({
      ...sessionPage({ ...view.sessions[0], status }, [], null, 'running'), lastTurn: sessionTurn('daily-planner', 'startup-turn', status),
    });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect.poll(() => view.requests.filter(request => request.path.endsWith('/control')).map(request => request.body)).toEqual([{ action: 'stop', turnId: 'startup-turn' }]);
});

it.each([['permission', false], ['retry', false], ['permission', true], ['retry', true]] as const)('reset cancels a %s deferred send and restores its draft (external=%s)', async (defer, external) => {
  await page.viewport(1280, 850);
  let registry!: ConversationRegistry;
  let release!: () => void;
  const messageEpochs: number[] = [];
  const permission = new Promise<void>(resolve => { release = resolve; });
  function Probe() {
    const commands = useConversationCommands();
    useEffect(() => { registry = commands; }, [commands]);
    return null;
  }
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', routeProbe: <Probe />, reply: request => {
    if (request.path.endsWith('/messages') && request.method === 'POST') {
      messageEpochs.push(view.sessions[0].epoch); throw new Error('response lost');
    }
    if (request.path.endsWith('/control')) { view.sessions[0].epoch += 1; return sessionReply(view.sessions[0]); }
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  if (defer === 'permission') void registry.holdPermissionWrite('daily-planner', permission);
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Keep pre-reset intent');
  await userEvent.keyboard('{Enter}');
  if (defer === 'retry') await expect.poll(() => view.requests.filter(request => request.path.endsWith('/messages')).length).toBeGreaterThanOrEqual(1);
  if (external) { view.sessions[0].epoch += 1; await view.client.invalidateQueries(); }
  else {
    await page.getByRole('button', { name: '会话操作', exact: true }).click();
    await page.getByRole('menuitem', { name: '重置上下文 /reset', exact: true }).click();
    await expect.poll(() => view.requests.some(request => request.path.endsWith('/control'))).toBe(true);
  }
  const attemptsBeforeRelease = messageEpochs.length;
  release();
  await expect.element(field).toHaveTextContent('Keep pre-reset intent');
  await new Promise(resolve => setTimeout(resolve, 1800));
  expect(messageEpochs).toHaveLength(defer === 'permission' ? 0 : attemptsBeforeRelease);
  expect(messageEpochs.every(epoch => epoch === 1)).toBe(true);
  await expect.element(field).toHaveTextContent('Keep pre-reset intent');
});

it('allows Worker reset and follows the runtime compact capability', async () => {
  await page.viewport(1280, 850);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (!request.path.includes('/items?')) return undefined;
    const result = sessionPage(view.sessions[0]);
    return sessionReply({ ...result, taskBinding: { task: { id: 'task', trackId: 'daily', blockId: 'block', taskKey: 'inspect', kind: 'agent', status: 'running', sessionId: view.sessions[0].id, runCount: 1, createdAtMs: 1, updatedAtMs: 1 }, phase: 'running', waitingReason: null }, capabilities: { ...result.capabilities!, supportsCompact: false } });
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('button', { name: '会话操作', exact: true }).click();
  await expect.element(page.getByRole('menuitem', { name: '压缩上下文 /compact' })).toHaveAttribute('aria-disabled', 'true');
  await expect.element(page.getByRole('menuitem', { name: '重置上下文 /reset' })).not.toHaveAttribute('aria-disabled', 'true');
  await userEvent.keyboard('{Escape}');
  await page.getByRole('combobox', { name: 'Message' }).fill('/');
  await expect.element(page.getByRole('option', { name: /^compact/ })).not.toBeInTheDocument();
  await expect.element(page.getByRole('option', { name: /^reset/ })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST')).toEqual([]);
});

it('keeps a fresh post-reset send across a stale skipped-turn read and hung post-ACK startup', async () => {
  await page.viewport(1280, 850);
  let reset = false;
  let acknowledged = false;
  let releaseRun!: () => void;
  let releaseSend!: () => void;
  const runReady = new Promise<void>(resolve => { releaseRun = resolve; });
  const sendReady = new Promise<void>(resolve => { releaseSend = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if (request.path.endsWith('/control')) {
      reset = true; view.sessions[0].epoch += 1; return sessionReply(view.sessions[0]);
    }
    if (reset && request.path.includes('limit=1')) {
      if (acknowledged) return new Promise(() => {});
      await runReady;
      return sessionReply({ ...sessionPage(view.sessions[0]), lastTurn: sessionTurn('daily-planner', 'older-turn-2', 'completed') });
    }
    if (request.method === 'POST' && request.path.endsWith('/messages')) {
      await sendReady; acknowledged = true; return sessionReply(sessionInput('daily-planner'));
    }
    return undefined;
  } });
  try {
    await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
    await page.getByRole('button', { name: '会话操作', exact: true }).click();
    await page.getByRole('menuitem', { name: '重置上下文 /reset', exact: true }).click();
    const field = page.getByRole('combobox', { name: 'Message' });
    await expect.element(field).toHaveAttribute('contenteditable', 'true');
    await field.fill('Fresh epoch intent'); await userEvent.keyboard('{Enter}');
    await expect.poll(() => view.requests.some(request => request.path.endsWith('/messages'))).toBe(true);
    releaseRun();
    await expect.poll(() => view.client.getQueryCache().getAll().some(query => query.state.fetchStatus === 'fetching')).toBe(false);
    releaseSend();
    await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    await expect.element(field).toHaveTextContent('');
  } finally { releaseRun(); releaseSend(); view.client.clear(); }
});

it.each(['idle', 'unknown'] as const)('retires accepted Stop after skipped-turn reconciliation reaches %s', async state => {
  await page.viewport(1280, 850);
  let accepted = false;
  let release!: () => void;
  const ready = new Promise<void>(resolve => { release = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if (request.path.endsWith('/messages') && request.method === 'POST') {
      accepted = true; return sessionReply(sessionInput('daily-planner'));
    }
    if (accepted && request.path.includes('limit=1')) {
      await ready;
      return sessionReply({ ...sessionPage(view.sessions[0], [], null, state),
        lastTurn: { ...sessionTurn('daily-planner', 'turn-2', 'completed'), turnIndex: 2 } });
    }
    return undefined;
  } });
  try {
    await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
    const field = page.getByRole('combobox', { name: 'Message' });
    await field.fill('Start and finish both turns'); await userEvent.keyboard('{Enter}');
    await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
    release();
    await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).not.toBeInTheDocument();
    if (state === 'idle') {
      await field.fill('Next idle message');
      await expect.element(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
    }
    expect(view.requests.filter(request => request.path.endsWith('/control'))).toEqual([]);
  } finally { release(); view.client.clear(); }
});

it('opens a blocked conversation before Lead and keeps its red indicator after viewing', async () => {
  await page.viewport(1280, 850);
  const view = renderSessionFixture({ initial: '/track/daily', reply: request => {
    if (request.path.includes('/second-planner/items?')) return sessionReply(sessionPage(
      { ...view.sessions[1], status: 'unknown' }, [], null, 'unknown'));
    return undefined;
  } });
  view.sessions[1] = { ...view.sessions[1], status: 'unknown' };
  const trigger = page.getByRole('button', { name: '讨论这项工作' });
  await expect.element(trigger).toBeVisible();
  await expect.poll(() => trigger.element().querySelector('[data-nc-activity="failed"]') !== null).toBe(true);
  await trigger.click();
  await expect.poll(() => view.requests.some(request => request.path.includes('/second-planner/items?'))).toBe(true);
  await expect.element(page.getByRole('button', { name: 'Close conversation', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(trigger).toBeVisible();
  expect(trigger.element().querySelector('[data-nc-activity="failed"]')).not.toBeNull();
  view.client.clear();
});

it.each([
  ['failed', 'Needs repair'], ['attention', 'Needs input'], ['working', 'Working'], ['unread', 'Unread updates'],
] as const)('describes %s activity on Today overview entries', async (state, description) => {
  await page.viewport(1280, 850);
  const transport: ApiTransportPort = { send: request => {
    const body = request.path === '/api/v1/areas'
      ? [{ id: 'project-area', name: 'Project', kind: 'user', color: '#6574cd', sortOrder: 0, createdAtMs: 1, updatedAtMs: 1 }]
      : request.path.includes('/areas/project-area/tracks')
        ? [fixtureTrack('overview', { areaId: 'project-area', title: 'Overview activity' })]
        : { tracks: [{ trackId: 'overview', areaId: 'project-area', isWorking: state === 'working',
          hasBlockedSession: state === 'failed', openAskCount: state === 'attention' ? 1 : 0,
          notifiableSessions: [{ sessionId: 'overview-session', lastNotifiableTurnAtMs: 200 }] }] };
    return Promise.resolve({ status: 200, statusText: 'OK', body });
  } };
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const overviewClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const fixture = renderDailyFixture({ receipts: createReadReceiptsKernel({ baselineMs: 100 }),
    routeProbe: <QueryClientProvider client={overviewClient}><PergaOverviewRoute transport={transport} unauthorized={unauthorized} /></QueryClientProvider> });
  const active = page.getByRole('region', { name: 'Active tracks', exact: true });
  await expect.element(active.getByRole('button', { name: /Overview activity/ })).toHaveAccessibleDescription(description);
  if (state === 'failed' || state === 'attention') {
    await expect.element(page.getByRole('region', { name: 'Waiting on you', exact: true })
      .getByRole('button', { name: /Overview activity/ })).toHaveAccessibleDescription(description);
  }
  fixture.client.clear();
  overviewClient.clear();
});

it('displays OAR runtime phase, active background tasks, and idle activity in the session header without blocking idle input', async () => {
  await page.viewport(1280, 850);
  let runtimeStatus: Record<string, unknown> = { kind: 'running', phase: 'tool', toolName: 'shell', toolCallId: 'call-1', writing: true };
  let runtimeTasks: Record<string, unknown>[] = [
    { taskId: 'bg-1', taskType: 'shell', background: true, ambient: false, status: 'running' },
    { taskId: 'ambient-1', taskType: 'other', background: true, ambient: true, status: 'running' },
  ];
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.includes('/daily-planner/items?')) return sessionReply({
      ...sessionPage(view.sessions[0], [], null, 'idle'),
      runtimeStatus,
      runtimeTasks,
    });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('工具：shell', { exact: true })).toBeVisible();
  await expect.element(page.getByText('运行时任务 1', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toBeEnabled();

  runtimeStatus = { kind: 'idle', idleActivity: { since: 10, lastEventAt: 20 } };
  runtimeTasks = [{ taskId: 'bg-1', taskType: 'shell', background: true, ambient: false, status: 'completed' }];
  await view.client.invalidateQueries();
  await expect.element(page.getByText('近期后台活动', { exact: true })).toBeVisible();
  await expect.element(page.getByText('工具：shell', { exact: true })).not.toBeInTheDocument();
  await expect.element(page.getByText('运行时任务 1', { exact: true })).not.toBeInTheDocument();
  view.client.clear();
});
