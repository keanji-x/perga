import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';
import { decodeEventFrame } from '../../../../core/events/protocol.ts';
import { initialEventState, reduceEventFrame } from '../../../../core/events/reducer.ts';
import { applyEventEffects } from '../events/query-invalidation-adapter.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);
it.each([
  { width: 390, reason: 'session_lost' }, { width: 1280, reason: 'compaction_timeout' },
  { width: 1280, reason: 'stream_lost' }, { width: 1280, reason: 'stop_timeout' }, { width: 1280, reason: 'host_restarted' },
])('recovers $reason while retaining history and draft ($width px)', async ({ width, reason }) => {
  await page.viewport(width, 844);
  let unknown = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/sessions/daily-planner/control') {
      expect(request.body).toEqual({ action: 'reset_context' });
      unknown = false;
      view.sessions[0].epoch += 1;
      return sessionReply(view.sessions[0]);
    }
    if (!request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(view.sessions[0], [sessionItem('daily-planner', 1, '保留这条回复')]),
      state: unknown ? 'unknown' : 'idle', unknownDetail: unknown ? { reason, detail: '连接已断开',
        canReconnect: false, canResetContext: true, recovering: false, lastError: null } : null });
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('保留这条回复', { exact: true })).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('保留草稿');
  unknown = true;
  const invalidations = vi.spyOn(view.client, 'invalidateQueries');
  const frame = decodeEventFrame({ kind: 'session.state_changed', eventId: null, trackId: 'daily', payload: { sessionId: 'daily-planner' } });
  if (frame.status !== 'ready') throw new Error('Expected a decoded state change');
  const reduction = reduceEventFrame(initialEventState(3, 42), frame.frame);
  applyEventEffects(view.client, reduction.effects);
  await expect.poll(() => invalidations.mock.calls.map(([filters]) => filters?.queryKey)).toContainEqual(['overlays', 'track']);
  expect(invalidations.mock.calls.map(([filters]) => filters?.queryKey)).toContainEqual(['planner-run', 'daily-planner']);
  expect(reduction.effects.some(effect => effect.type === 'persist-cursor')).toBe(false);
  invalidations.mockRestore();
  await expect.element(field).toHaveAttribute('contenteditable', 'false');
  expect(document.querySelectorAll('[data-nc-conversation-action]')).toHaveLength(1);
  await page.getByRole('button', { name: 'Reset context and continue', exact: true }).click();
  await expect.element(field).toHaveAttribute('contenteditable', 'true');
  await expect.element(field).toHaveTextContent('保留草稿');
  await expect.element(page.getByText('保留这条回复', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual(['/api/v1/sessions/daily-planner/control']);
});

it.each([390, 1280])('reads and copies interrupted inputs above the epoch boundary (%ipx)', async width => {
  await page.viewport(width, 844);
  const userText = 'Unsent original\n  with <@person> and **Markdown**';
  const wakeText = '{"event":"mail","message":"Original wake"}';
  const copy = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue(undefined);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  view.items.set('daily-planner', [
    { id: 1, sessionId: 'daily-planner', turnId: null, kind: 'user', payload: { v: 1, content: userText, inputId: 'input', epoch: 1, status: 'interrupted', archived: true, reason: 'session_reset' }, createdAtMs: 1 },
    { id: 2, sessionId: 'daily-planner', turnId: null, kind: 'system', payload: { v: 1, content: wakeText, wakeId: 'wake', wakeKind: 'mail', epoch: 1, status: 'interrupted', archived: true, reason: 'session_reset' }, createdAtMs: 2 },
    { id: 3, sessionId: 'daily-planner', turnId: null, kind: 'epoch_boundary', payload: { epoch: 2, reason: 'reset_context' }, createdAtMs: 3 },
  ]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const interrupted = page.getByText('Interrupted · Unsent', { exact: true });
  await expect.element(interrupted.first()).toBeVisible();
  const wakeEntry = page.elementLocator(interrupted.last().element().closest('[data-nc-entry]')!);
  expect.soft(wakeEntry.getByRole('button', { name: 'Copy message', exact: true }).query()).toBeNull();
  const userNote = interrupted.first().element();
  const userCopy = page.elementLocator(userNote.parentElement!).getByRole('button', { name: 'Copy message', exact: true });
  expect(userCopy.query()).not.toBeNull();
  const noteBounds = userNote.getBoundingClientRect();
  const copyBounds = userCopy.element().getBoundingClientRect();
  expect(Math.abs(noteBounds.top + noteBounds.height / 2 - copyBounds.top - copyBounds.height / 2)).toBeLessThan(1);
  expect(copyBounds.left).toBeGreaterThanOrEqual(noteBounds.right);
  await userCopy.click();
  await expect.poll(() => copy.mock.calls.at(-1)?.[0]).toBe(userText);
  await interrupted.last().click();
  const wakeDetail = page.getByText(wakeText, { exact: true });
  await expect.element(wakeDetail).toBeVisible();
  const result = page.elementLocator(wakeDetail.element().closest('[data-nc-detail]')!);
  await result.getByRole('button', { name: 'Copy message', exact: true }).click();
  await expect.poll(() => copy.mock.calls.at(-1)?.[0]).toBe(wakeText);
  const entries = [...document.querySelectorAll('[data-nc-entry]')];
  const user = entries.find(el => el.getAttribute('data-nc-entry') === 'human:input');
  const wake = entries.find(el => el.getAttribute('data-nc-entry') === 'system:wake');
  const boundary = page.getByText('Context reset · Epoch 2', { exact: true }).element();
  expect(user).toBeTruthy(); expect(wake).toBeTruthy();
  expect(user!.compareDocumentPosition(wake!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(wake!.compareDocumentPosition(boundary) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  copy.mockRestore();
});

it('keeps quiet system observations free of copy actions', async () => {
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  view.items.set('daily-planner', [
    { ...sessionItem('daily-planner', 1, '', 'memory_context'), payload: { mode: 'loaded', content: 'Loaded memory' } },
  ]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const observation = page.getByText('Memory context loaded', { exact: true });
  await expect.element(observation).toBeVisible();
  const entry = page.elementLocator(observation.element().closest('[data-nc-entry]')!);
  await expect.element(entry.getByRole('group', { name: 'Original message actions' })).not.toBeInTheDocument();
});

it('withdraws an unknown queue entry while the session is unknown', async () => {
  await page.viewport(1280, 844);
  let withdrawn = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.method === 'DELETE' && request.path === '/api/v1/sessions/daily-planner/messages/unknown-input') {
      withdrawn = true; return { status: 204, statusText: 'No Content', body: undefined };
    }
    if (!request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(view.sessions[0]), state: 'unknown',
      unknownDetail: { reason: 'session_lost', detail: '会话丢失', canReconnect: false, canResetContext: true, recovering: false, lastError: null },
      queuedMessages: withdrawn ? [] : [{ id: 'unknown-input', text: 'Paused original', queuedAtMs: 1, state: 'unconfirmed' }],
    });
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Paused original', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
  await expect.poll(() => withdrawn).toBe(true);
  await expect.element(page.getByText('Paused original', { exact: true })).not.toBeInTheDocument();
});

it.each([390, 1280])('shows a localized recovery refusal after one send (%ipx)', async width => {
  await page.viewport(width, 844);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.method === 'POST' && request.path.endsWith('/messages')) return {
      status: 409, statusText: 'Conflict', body: { code: 'session_recovery_required', error: 'Recover the provider session before sending more input.' },
    };
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('combobox', { name: 'Message' }).fill('保留这条消息');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect.element(page.getByText('Message not sent. Session recovery is required before sending.', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST' && request.path.endsWith('/messages'))).toHaveLength(1);
  expect(document.body.textContent).not.toContain('Recover the provider session');
  expect(document.body.textContent).not.toContain('Not sent.');
});
