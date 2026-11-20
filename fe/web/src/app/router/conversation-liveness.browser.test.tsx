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
    if (request.path.endsWith('/interrupt')) return sessionReply({ stopped: true });
    if (accepted && request.path.includes('view=run')) return new Promise(() => {});
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
  await expect.poll(() => view.requests.filter(request => request.path.endsWith('/interrupt')).map(request => request.body)).toEqual([{ turnId: 'turn-1' }]);
  view.client.clear();
});

it.each(['queued', 'starting'])('stops reloaded %s lastTurn startup without an activeTurn', async status => {
  await page.viewport(1280, 850);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.endsWith('/interrupt')) return sessionReply({ stopped: true });
    if (request.path.includes('/daily-planner/items?')) return sessionReply({
      ...sessionPage({ ...view.sessions[0], status }, [], null, 'running'), lastTurn: sessionTurn('daily-planner', 'startup-turn', status),
    });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expect.poll(() => view.requests.filter(request => request.path.endsWith('/interrupt')).map(request => request.body)).toEqual([{ turnId: 'startup-turn' }]);
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
    if (request.path.endsWith('/recover')) { view.sessions[0].epoch += 1; return sessionReply(view.sessions[0]); }
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
    await expect.poll(() => view.requests.some(request => request.path.endsWith('/recover'))).toBe(true);
  }
  const attemptsBeforeRelease = messageEpochs.length;
  release();
  await expect.element(field).toHaveTextContent('Keep pre-reset intent');
  await new Promise(resolve => setTimeout(resolve, 1800));
  expect(messageEpochs).toHaveLength(defer === 'permission' ? 0 : attemptsBeforeRelease);
  expect(messageEpochs.every(epoch => epoch === 1)).toBe(true);
  await expect.element(field).toHaveTextContent('Keep pre-reset intent');
});

it('denies Worker compact/reset discovery and execution in both menus', async () => {
  await page.viewport(1280, 850);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  view.sessions[0].boundTaskId = 'task'; view.sessions[0].boundAttemptId = 'attempt';
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('button', { name: '会话操作', exact: true }).click();
  await expect.element(page.getByRole('menuitem', { name: '压缩上下文 /compact' })).toHaveAttribute('aria-disabled', 'true');
  await expect.element(page.getByRole('menuitem', { name: '重置上下文 /reset' })).toHaveAttribute('aria-disabled', 'true');
  await userEvent.keyboard('{Escape}');
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('/');
  await expect.element(page.getByRole('option', { name: /^compact/ })).not.toBeInTheDocument();
  await expect.element(page.getByRole('option', { name: /^reset/ })).not.toBeInTheDocument();
  for (const command of ['compact', 'reset']) {
    await field.fill(`/${command}`);
    await userEvent.keyboard('{Enter}');
    await expect.element(field).toHaveTextContent(`/${command}`);
  }
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
    if (request.path.endsWith('/recover')) {
      reset = true; view.sessions[0].epoch += 1; return sessionReply(view.sessions[0]);
    }
    if (reset && request.path.includes('view=run')) {
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
    if (accepted && request.path.includes('view=run')) {
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
    expect(view.requests.filter(request => request.path.endsWith('/interrupt'))).toEqual([]);
  } finally { release(); view.client.clear(); }
});
