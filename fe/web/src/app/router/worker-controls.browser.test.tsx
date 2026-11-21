import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionPage, sessionReply, sessionTurn } from './conversation-session-data.ts';

afterEach(cleanup);

it.each(['reconnect', 'reset_context'] as const)('recovers an unknown Worker with %s through unified control', async action => {
  await page.viewport(1280, 844);
  let unknown = true;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/sessions/second-planner/control') {
      expect(request.body).toEqual({ action });
      unknown = false;
      if (action === 'reset_context') view.sessions[1].epoch++;
      return sessionReply(view.sessions[1]);
    }
    if (!request.path.startsWith('/api/v1/sessions/second-planner/items?')) return undefined;
    const session = { ...view.sessions[1] };
    return sessionReply({ ...sessionPage(session), state: unknown ? 'unknown' : 'idle',
      taskBinding: { task: { id: 'task', trackId: 'daily', blockId: 'block', taskKey: 'inspect', kind: 'agent', status: 'running', sessionId: session.id, runCount: 1, createdAtMs: 1, updatedAtMs: 1 }, phase: 'running', waitingReason: null },
      unknownDetail: unknown ? { reason: 'stream_lost', detail: '连接已断开', canReconnect: true, canResetContext: true, recovering: false, lastError: null } : null });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  const message = page.getByRole('combobox', { name: 'Message' });
  await expect.element(message).toHaveAttribute('contenteditable', 'false');
  await page.getByRole('button', { name: action === 'reconnect' ? '重连原会话' : '重置上下文并继续', exact: true }).click();
  await expect.element(message).toHaveAttribute('contenteditable', 'true');
  expect(view.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual(['/api/v1/sessions/second-planner/control']);
});

it('uses session capabilities, configuration, pending approvals and message receipts for a task session', async () => {
  await page.viewport(1280, 844);
  let approved = false;
  let sent = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    const session = { ...view.sessions[1], permissionMode: 'restricted' };
    if (request.method === 'PATCH' && request.path === '/api/v1/sessions/second-planner') {
      expect(request.body).toEqual({ permissionMode: 'full' });
      return sessionReply({ ...session, permissionMode: 'full', configVersion: 1 });
    }
    if (request.path === '/api/v1/sessions/second-planner/interactions/approval/respond') {
      expect(request.body).toEqual({ executionId: 'execution', optionId: 'approve' });
      approved = true;
      return sessionReply({ id: 'approval', kind: 'approval' });
    }
    if (request.method === 'POST' && request.path === '/api/v1/sessions/second-planner/messages') {
      expect(request.body).toEqual({ content: 'Continue inspection', mode: 'default', expectedEpoch: 1 });
      expect(request.headers?.['Idempotency-Key']).toBeTruthy();
      sent = true;
      return sessionReply({ sessionId: session.id, messageId: 'message', turnId: 'turn', delivery: 'accepted', replay: false });
    }
    if (!request.path.startsWith('/api/v1/sessions/second-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(session),
      capabilities: { models: [], defaultModel: null, defaultReasoningEffort: null, supportsCompact: false,
        supportsSessionResume: true, supportsNativeSteer: true, permissionModes: [
          { id: 'restricted', label: 'Restricted', description: 'Restricted', tone: 'neutral', isDefault: true },
          { id: 'full', label: 'Full', description: 'Full', tone: 'warning', isDefault: false },
        ] },
      taskBinding: { task: { id: 'task', trackId: 'daily', blockId: 'block', taskKey: 'inspect', kind: 'agent', status: 'running', sessionId: session.id, runCount: 1, createdAtMs: 1, updatedAtMs: 1 }, phase: 'paused', waitingReason: null },
      pendingInteractions: approved ? [] : [{ id: 'approval', kind: 'approval', sessionId: session.id, executionId: 'execution',
        title: 'Inspect the report?', options: [{ id: 'approve', label: 'Approve inspection', isApproval: true }], canRespond: true, createdAtMs: 1 }],
    });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await page.getByRole('button', { name: 'Approve inspection', exact: true }).click();
  await expect.element(page.getByText('Inspect the report?', { exact: true })).not.toBeInTheDocument();
  await page.getByRole('button', { name: 'Approvals: Restricted', exact: true }).click();
  await page.getByText('Full', { exact: true }).first().click();
  await expect.poll(() => view.requests.filter(request => request.method === 'PATCH')).toHaveLength(1);
  const message = page.getByRole('combobox', { name: 'Message' });
  await message.fill('Continue inspection');
  await message.click();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => sent).toBe(true);
});

it('keeps uncertain steering visible and withdraws through the unified message route', async () => {
  await page.viewport(1280, 844);
  let uncertain = false;
  let withdrawn = false;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    const id = 'second-planner';
    if (request.method === 'POST' && request.path === `/api/v1/sessions/${id}/messages`) {
      expect(request.body).toEqual({ mode: 'steer', messageId: 'queued', turnId: 'turn' });
      expect(request.headers?.['Idempotency-Key']).toBeUndefined();
      uncertain = true;
      return sessionReply({ sessionId: id, messageId: 'queued', turnId: 'turn', delivery: 'unknown', replay: false });
    }
    if (request.method === 'DELETE' && request.path === `/api/v1/sessions/${id}/messages/queued`) {
      withdrawn = true;
      return { status: 204, statusText: 'No Content', body: undefined };
    }
    if (!request.path.startsWith(`/api/v1/sessions/${id}/items?`)) return undefined;
    const session = { ...view.sessions[1] };
    return sessionReply({ ...sessionPage(session, [], sessionTurn(id, 'turn')), canSteer: true,
      queuedMessages: withdrawn ? [] : [{ id: 'queued', text: 'Inspect the appendix', queuedAtMs: 1, state: uncertain ? 'unconfirmed' : 'waiting' }],
    });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await page.getByRole('button', { name: '立即插队', exact: true }).click();
  await expect.element(page.getByText('Still queued — not confirmed', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: '撤回', exact: true }).click();
  await expect.element(page.getByText('Inspect the appendix', { exact: true })).not.toBeInTheDocument();
  expect(withdrawn).toBe(true);
});

it('renders polled and streamed session activity once and retires it into history', async () => {
  await page.viewport(1280, 844);
  let finished = false;
  const text = 'One streamed response';
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    const id = 'second-planner';
    if (!request.path.startsWith(`/api/v1/sessions/${id}/items?`)) return undefined;
    const session = { ...view.sessions[1], nativeThreadId: 'thread', status: finished ? 'idle' : 'running' };
    return sessionReply({ ...sessionPage(session, finished ? [{ id: 1, sessionId: id, turnId: 'turn', kind: 'assistant_text', payload: { text }, createdAtMs: 1 }] : [],
      finished ? null : sessionTurn(id, 'turn')),
      activity: finished ? null : { turnId: 'turn', sequence: 1, reset: false, text, truncated: false, events: [] },
    });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await expect.poll(() => view.requests.filter(request => request.path.includes('second-planner/items?afterId=0&limit=1')).length).toBeGreaterThanOrEqual(3);
  expect(document.querySelectorAll('[data-nc-turn="agent"]')).toHaveLength(1);
  await expect.element(page.getByText(text, { exact: true })).toBeVisible();
  finished = true;
  await view.client.invalidateQueries();
  await expect.poll(() => document.querySelectorAll('[data-nc-turn="agent"]').length).toBe(1);
  await expect.element(page.getByText(text, { exact: true })).toBeVisible();
});

it('allows configuration before a pending task starts while keeping message input frozen', async () => {
  await page.viewport(1280, 844);
  let mode = 'restricted';
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    const session = { ...view.sessions[1], permissionMode: mode };
    if (request.method === 'PATCH' && request.path === '/api/v1/sessions/second-planner') {
      expect(request.body).toEqual({ permissionMode: 'full' });
      mode = 'full';
      return sessionReply({ ...session, permissionMode: mode, configVersion: 1 });
    }
    if (!request.path.startsWith('/api/v1/sessions/second-planner/items?')) return undefined;
    return sessionReply({ ...sessionPage(session), readonlyReason: 'waiting_task',
      taskBinding: { task: { id: 'task', trackId: 'daily', blockId: 'block', taskKey: 'inspect', kind: 'agent',
        status: 'pending', sessionId: session.id, runCount: 0, createdAtMs: 1, updatedAtMs: 1 }, phase: 'pending', waitingReason: 'dependency' },
      capabilities: { models: [], defaultModel: null, defaultReasoningEffort: null, supportsCompact: false,
        supportsSessionResume: true, supportsNativeSteer: true, permissionModes: [
          { id: 'restricted', label: 'Restricted', description: 'Restricted', tone: 'neutral', isDefault: true },
          { id: 'full', label: 'Full', description: 'Full', tone: 'warning', isDefault: false },
        ] },
    });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await expect.element(page.getByText('等待任务', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).not.toBeInTheDocument();
  await page.getByRole('button', { name: 'Approvals: Restricted', exact: true }).click();
  await page.getByText('Full', { exact: true }).first().click();
  await expect.poll(() => mode).toBe('full');
  expect(view.requests.some(request => request.method === 'POST' && request.path.endsWith('/messages'))).toBe(false);
});

it('freezes the composer and configuration while the task gate verifies', async () => {
  await page.viewport(1280, 844);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (!request.path.startsWith('/api/v1/sessions/second-planner/items?')) return undefined;
    const session = { ...view.sessions[1], permissionMode: 'restricted' };
    return sessionReply({ ...sessionPage(session), readonlyReason: 'task_finished',
      capabilities: { models: [], defaultModel: null, defaultReasoningEffort: null, supportsCompact: false,
        supportsSessionResume: true, supportsNativeSteer: true, permissionModes: [
          { id: 'restricted', label: 'Restricted', description: 'Restricted', tone: 'neutral', isDefault: true },
        ] },
      taskBinding: { task: { id: 'task', trackId: 'daily', blockId: 'block', taskKey: 'inspect', kind: 'agent',
        status: 'verifying', sessionId: session.id, runCount: 1, createdAtMs: 1, updatedAtMs: 1 }, phase: 'verifying', waitingReason: null },
    });
  } });
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).not.toBeInTheDocument();
  await expect.element(page.getByText('对话已结束', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Approvals: Restricted', exact: true })).not.toBeInTheDocument();
  expect(view.requests.some(request => request.method !== 'GET')).toBe(false);
});
