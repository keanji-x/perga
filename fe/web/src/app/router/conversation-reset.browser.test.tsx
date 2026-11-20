import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);

it('returns refused text to the composer and reopens it after provider cleanup', async () => {
  await page.viewport(1280, 844);
  const messagesPath = '/api/v1/sessions/daily-planner/messages';
  const error = 'Recover the provider session before sending more input.';
  const detail = '正在等待模型进程关闭确认。';
  let cleaningUp = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === messagesPath) return { status: 409, statusText: 'Conflict',
      body: { code: 'session_recovery_required', error } };
    if (!request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(view.sessions[0]),
      providerCleanup: cleaningUp ? { canReconnect: true, recovering: false } : null });
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Keep this unsent message.');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByText('消息未发送。请等待会话清理完成，或恢复会话后重试。', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.path === messagesPath)).toHaveLength(1);
  await page.getByRole('button', { name: '放回输入框' }).click();

  cleaningUp = true;
  await view.client.invalidateQueries();
  await expect.element(page.getByText(detail, { exact: true })).toBeVisible();
  await expect.element(page.getByRole('button', { name: '重连原会话' })).toBeVisible();
  await expect.element(field).toHaveAttribute('contenteditable', 'false');

  cleaningUp = false;
  await expect.element(field).toHaveAttribute('contenteditable', 'true');
  await expect.element(field).toHaveTextContent('Keep this unsent message.');
});

it('reenables recovery controls when a different tab finishes a failed recovery', async () => {
  await page.viewport(1280, 844);
  let recovering = true;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (!request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(view.sessions[0]), state: 'unknown',
      unknownDetail: { reason: 'stream_lost', detail: '连接已断开', canReconnect: true,
        canResetContext: true, recovering, lastError: null } });
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const reconnect = page.getByRole('button', { name: '重连原会话', exact: true });
  await expect.element(reconnect).toBeDisabled();
  recovering = false;
  await expect.element(reconnect).toBeEnabled();
});

it.each([
  { width: 390, reason: 'session_lost' }, { width: 1280, reason: 'compaction_timeout' },
  { width: 1280, reason: 'stream_lost' }, { width: 1280, reason: 'stop_timeout' }, { width: 1280, reason: 'host_restarted' },
])('recovers $reason while retaining history and draft ($width px)', async ({ width, reason }) => {
  await page.viewport(width, 844);
  let unknown = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/sessions/daily-planner/recover') {
      expect(request.body).toEqual({ mode: 'reset_context' });
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
  await view.client.invalidateQueries();
  await expect.element(field).toHaveAttribute('contenteditable', 'false');
  expect(document.querySelectorAll('[data-nc-conversation-action]')).toHaveLength(1);
  await page.getByRole('button', { name: '重置上下文并继续', exact: true }).click();
  await expect.element(field).toHaveAttribute('contenteditable', 'true');
  await expect.element(field).toHaveTextContent('保留草稿');
  await expect.element(page.getByText('保留这条回复', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual(['/api/v1/sessions/daily-planner/recover']);
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
  await expect.element(page.getByText('Interrupted · Unsent', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: '复制原文', exact: true }).first().click();
  await expect.poll(() => copy.mock.calls.at(-1)?.[0]).toBe(userText);
  await page.getByText('Interrupted · Unsent', { exact: true }).last().click();
  await expect.element(page.getByText(wakeText, { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '复制原文', exact: true }).click();
  await expect.poll(() => copy.mock.calls.at(-1)?.[0]).toBe(wakeText);
  const entries = [...document.querySelectorAll('[data-nc-entry]')];
  const user = entries.find(el => el.getAttribute('data-nc-entry') === 'human:input');
  const wake = entries.find(el => el.getAttribute('data-nc-entry') === 'system:wake');
  const boundary = page.getByText('已重置上下文 · Epoch 2', { exact: true }).element();
  expect(user).toBeTruthy(); expect(wake).toBeTruthy();
  expect(user!.compareDocumentPosition(wake!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(wake!.compareDocumentPosition(boundary) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  copy.mockRestore();
});

it('withdraws an unknown queue entry while the session is unknown', async () => {
  await page.viewport(1280, 844);
  let withdrawn = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.method === 'DELETE' && request.path === '/api/v1/sessions/daily-planner/messages/queue/unknown-input') {
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
  await page.getByRole('button', { name: '撤回', exact: true }).click();
  await expect.poll(() => withdrawn).toBe(true);
  await expect.element(page.getByText('Paused original', { exact: true })).not.toBeInTheDocument();
});
