import '../../styles/entry.css';
import { useEffect } from 'react';
import { useConversationCommands, type ConversationRegistry } from '../conversations/public.tsx';
import { act, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionInput, sessionReply, sessionTurn } from './conversation-session-data.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each([false, true])('orders permission ACK before a browser send, without waiting for GET (running=%s)', async running => {
  await page.viewport(1400, 900);
  let answer!: (response: ApiTransportResponse) => void;
  const put = new Promise<ApiTransportResponse>(resolve => { answer = resolve; });
  let changing = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return sessionReply({ version: 'test', providers: [{
      provider: 'codex', available: true, models: [], permissionModes: [
        { id: 'ask', label: 'Ask', description: 'Ask first', tone: 'neutral', isDefault: true },
        { id: 'full', label: 'Full', description: 'Act directly', tone: 'warning', isDefault: false },
      ],
    }] });
    if (request.method === 'PUT') { changing = true; return put; }
    if (changing && request.path.includes('view=run')) return new Promise(() => undefined);
    if (request.method === 'POST' && request.path.endsWith('/messages')) return sessionReply(sessionInput('daily-planner'));
    return undefined;
  } });
  view.sessions[0].permissionMode = 'ask';
  if (running) view.activeTurns.set('daily-planner', sessionTurn('daily-planner'));
  try {
    await page.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true }).click();
    await page.getByRole('button', { name: 'Approvals: Ask' }).click();
    await page.getByRole('menuitem', { name: /^Full/ }).click();
    const field = page.getByRole('combobox', { name: 'Message', exact: true });
    await field.fill('Send after permission');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    expect(view.requests.filter(request => request.method === 'POST')).toHaveLength(0);
    await act(async () => { answer(sessionReply({ card_id: 'daily-planner', permission_mode: 'full' })); await put; });
    await expect.element(page.getByRole('button', { name: 'Approvals: Full' })).toBeVisible();
    await expect.poll(() => view.requests.filter(request => request.method === 'POST').length).toBe(1);
    expect(view.requests.find(request => request.method === 'POST')?.body).toMatchObject({ content: 'Send after permission', ...(running ? { queueIfBusy: true } : {}) });
  } finally { view.client.clear(); }
});

it('restores a refused browser send and keeps another conversation independent', async () => {
  await page.viewport(1400, 900);
  let answer!: (response: ApiTransportResponse) => void;
  const put = new Promise<ApiTransportResponse>(resolve => { answer = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return sessionReply({ version: 'test', providers: [{
      provider: 'codex', available: true, models: [], permissionModes: [
        { id: 'ask', label: 'Ask', description: 'Ask first', tone: 'neutral', isDefault: true },
        { id: 'full', label: 'Full', description: 'Act directly', tone: 'warning', isDefault: false },
      ],
    }] });
    if (request.method === 'PUT') return put;
    if (request.method === 'POST') return sessionReply(sessionInput('second-planner'));
    return undefined;
  } });
  view.sessions.forEach(session => { session.permissionMode = 'ask'; });
  try {
    await page.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true }).click();
    await page.getByRole('button', { name: 'Approvals: Ask' }).click();
    await page.getByRole('menuitem', { name: /^Full/ }).click();
    const field = page.getByRole('combobox', { name: 'Message', exact: true });
    await field.fill('Keep this draft');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    expect(view.requests.filter(request => request.method === 'POST')).toHaveLength(0);
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    await page.getByRole('button', { name: 'Conversation Other conversation', exact: true }).click();
    await field.fill('Independent message');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect.poll(() => view.requests.filter(request => request.method === 'POST').length).toBe(1);
    await act(async () => { answer({ status: 409, statusText: 'Conflict', body: { code: 'precondition_failed', error: 'Permission denied' } }); await put; });
    await expect.element(page.getByRole('button', { name: 'Approvals: Ask' })).toBeVisible();
    await expect.element(page.getByText('Permission denied', { exact: false })).not.toBeInTheDocument();
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    await page.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true }).click();
    await expect.element(field).toHaveTextContent('Keep this draft');
    await expect.element(page.getByText('Not sent.', { exact: false })).toBeVisible();
    expect(view.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual(['/api/v1/sessions/second-planner/messages']);
  } finally { view.client.clear(); }
});

function permissionBootstrap() {
  return sessionReply({ version: 'test', providers: [{ provider: 'codex', available: true, models: [], permissionModes: [
    { id: 'ask', label: 'Ask', description: 'Ask first', tone: 'neutral', isDefault: true },
    { id: 'full', label: 'Full', description: 'Act directly', tone: 'warning', isDefault: false },
    { id: 'never', label: 'Never', description: 'Never ask', tone: 'warning', isDefault: false },
  ] }] });
}
async function openDaily() { await page.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true }).click(); }
async function switchBack() {
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await page.getByRole('button', { name: 'Conversation Other conversation', exact: true }).click();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await openDaily();
}
async function chooseFull() {
  await page.getByRole('button', { name: 'Approvals: Ask' }).click();
  await page.getByRole('menuitem', { name: /^Full/ }).click();
}
const permissionFailure = () => ({ status: 500, statusText: 'Error', body: { error: 'Permission write failed' } });

it('preserves the unknown browser retry key after a permission failure', async () => {
  await page.viewport(1400, 900);
  let answer!: (response: ApiTransportResponse) => void;
  const put = new Promise<ApiTransportResponse>(resolve => { answer = resolve; });
  let registry!: ConversationRegistry;
  function Probe() {
    const commands = useConversationCommands();
    useEffect(() => { registry = commands; }, [commands]);
    return null;
  }
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', routeProbe: <Probe />, reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return permissionBootstrap();
    if (request.method === 'PUT') return put;
    if (request.method === 'POST') return sessionReply(sessionInput('daily-planner'));
    return undefined;
  } });
  view.sessions[0].permissionMode = 'ask';
  view.activeTurns.set('daily-planner', sessionTurn('daily-planner'));
  try {
    await openDaily();
    const field = page.getByRole('combobox', { name: 'Message', exact: true });
    await chooseFull();
    const key = 'unknown-original-key';
    act(() => { registry.editOutbox('daily-planner', () => [{ key, phase: 'failed', delivery: 'unknown', message: '', fromComposer: true, replaces: null,
      echo: { id: 'echo', text: 'Possibly persisted', author: 'you', atMs: 1, serverHighWaterBefore: 0, entryId: null, queued: true } }]); });
    await page.getByRole('button', { name: 'Try again', exact: true }).click();
    await act(async () => { answer(permissionFailure()); await put; });
    await expect.element(page.getByRole('button', { name: 'Try again', exact: true })).toBeVisible();
    await expect.element(field).toHaveTextContent('');
    await page.getByRole('button', { name: 'Try again', exact: true }).click();
    await expect.poll(() => view.requests.filter(request => request.method === 'POST').length).toBe(1);
    expect(view.requests.filter(request => request.method === 'POST').map(request => request.headers?.['Idempotency-Key'])).toEqual([key]);
  } finally { view.client.clear(); }
});

it('keeps three generations of A B A permission writes ahead of a queued browser send', async () => {
  await page.viewport(1400, 900);
  const answers: ((response: ApiTransportResponse) => void)[] = [];
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return permissionBootstrap();
    if (request.method === 'PUT') return new Promise<ApiTransportResponse>(resolve => { answers.push(resolve); });
    if (request.method === 'POST') return sessionReply(sessionInput('daily-planner'));
    return undefined;
  } });
  view.sessions.forEach(session => { session.permissionMode = 'ask'; });
  view.activeTurns.set('daily-planner', sessionTurn('daily-planner'));
  try {
    await openDaily(); await chooseFull();
    await switchBack(); await chooseFull();
    await switchBack(); await chooseFull();
    expect(answers).toHaveLength(1);
    await act(async () => { answers[0](permissionFailure()); await Promise.resolve(); });
    await expect.poll(() => answers.length).toBeGreaterThanOrEqual(2);
    await act(async () => { answers[1](permissionFailure()); await Promise.resolve(); });
    await expect.poll(() => answers.length).toBe(3);
    await page.getByRole('combobox', { name: 'Message', exact: true }).fill('Wait for every write');
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    // A refused send would also make no POST: it must remain pending, with no draft refill.
    await expect.element(page.getByText('Sending…', { exact: true })).toBeVisible();
    await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toHaveTextContent('');
    expect(view.requests.filter(request => request.method === 'POST')).toHaveLength(0);
    await act(async () => { answers[2](sessionReply({ card_id: 'daily-planner', permission_mode: 'full' })); await Promise.resolve(); });
    await expect.element(page.getByRole('button', { name: 'Send', exact: true })).toBeEnabled();
  } finally { view.client.clear(); }
});

it('keeps a newer GET permission visible when an old A B A PUT answers', async () => {
  await page.viewport(1400, 900);
  let holdReads = false;
  let answer!: (response: ApiTransportResponse) => void;
  const put = new Promise<ApiTransportResponse>(resolve => { answer = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return permissionBootstrap();
    if (request.method === 'PUT') return put;
    if (holdReads && request.path.includes('view=run')) return new Promise(() => undefined);
    return undefined;
  } });
  view.sessions[0].permissionMode = 'ask';
  try {
    await openDaily(); await chooseFull();
    view.sessions[0].permissionMode = 'never';
    await switchBack();
    await expect.element(page.getByRole('button', { name: 'Approvals: Never' })).toBeVisible();
    holdReads = true;
    // Hold reconciliation so an incorrect ACK cannot be concealed by a following GET.
    view.sessions[0].permissionMode = 'never';
    await act(async () => { answer(sessionReply({ card_id: 'daily-planner', permission_mode: 'full' })); await put; });
    expect(page.getByRole('button', { name: 'Approvals: Never' }).elements()).toHaveLength(1);
  } finally { view.client.clear(); }
});

it('serializes A B A writes and publishes only the newest permission ACK', async () => {
  await page.viewport(1400, 900);
  const answers: ((response: ApiTransportResponse) => void)[] = [];
  let holdReads = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return permissionBootstrap();
    if (request.method === 'PUT') return new Promise<ApiTransportResponse>(resolve => { answers.push(resolve); });
    if (holdReads && request.path.includes('view=run')) return new Promise(() => undefined);
    return undefined;
  } });
  view.sessions[0].permissionMode = 'ask';
  try {
    await openDaily(); await chooseFull(); await switchBack();
    await page.getByRole('button', { name: 'Approvals: Ask' }).click();
    await page.getByRole('menuitem', { name: /^Never/ }).click();
    expect(answers).toHaveLength(1);
    holdReads = true;
    await act(async () => { answers[0](sessionReply({ card_id: 'daily-planner', permission_mode: 'full' })); await Promise.resolve(); });
    await expect.poll(() => answers.length).toBe(2);
    await expect.element(page.getByRole('button', { name: 'Approvals: Ask' })).toBeVisible();
    await act(async () => { answers[1](sessionReply({ card_id: 'daily-planner', permission_mode: 'never' })); await Promise.resolve(); });
    await expect.element(page.getByRole('button', { name: 'Approvals: Never' })).toBeVisible();
  } finally { view.client.clear(); }
});
