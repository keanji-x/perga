import '../../styles/entry.css';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { queryKeys } from '../providers/query-keys.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('uses shared chat controls to show tools, change settings, retry, steer, and stop a task session', async () => {
  await page.viewport(1280, 820);
  const id = 'second-planner';
  const path = `/api/v1/sessions/${id}`;
  let paused = false;
  let finished = false;
  let refused = false;
  let approvalOpen = true;
  let queuedRequests = 0;
  let queuedVisible = false;
  let handedOff = false;
  let currentTurn = 'turn-1';
  let releaseDeny!: () => void;
  const denyApproval = new Promise<void>(resolve => { releaseDeny = resolve; });
  const approval = { id: 'worker-ask', executionId: 'turn-1', turnId: 'turn-1', sessionId: id, createdAtMs: 4,
    title: 'Approve reading the report?',
    options: [
      { id: 'approve', label: 'Approve', isApproval: true },
      { id: 'deny', label: 'Deny', isApproval: false },
    ] };
  let settings = { version: 0, model: null as string | null, reasoningEffort: null as string | null, approvalMode: 'ask' };
  const providerConfiguration = { defaultModel: 'gpt-5.4-mini', defaultReasoningEffort: 'low', models: [
    { id: 'gpt-5.4-mini', name: 'GPT 5.4 Mini', reasoningEfforts: ['low'], defaultReasoningEffort: 'low' },
    { id: 'gpt-5.4', name: 'GPT 5.4', reasoningEfforts: ['low', 'high'], defaultReasoningEffort: 'low' },
  ] };
  const view = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: async request => {
    if (request.path === '/api/v1/bootstrap') return ok({ assistantProviders: true, conversationInitialInput: true,
      webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'worker-session' });
    if (request.path === '/api/v1/bootstrap?scope=managed') return ok({ version: 'test', providers: [{
      provider: 'codex', available: true, models: [], permissionModes: [
        { id: 'never', label: 'Never', description: 'Never asks.', tone: 'neutral', isDefault: true },
      ],
    }] });
    if (request.path.startsWith(`${path}/items?`)) return ok({
      session: { id, trackId: 'daily', provider: 'codex', permissionMode: 'never', boundTaskId: 'task-1', boundAttemptId: 'attempt-1', contextUsage: null, status: paused ? 'idle' : 'running', createdAtMs: 1, updatedAtMs: 2 },
      items: [
        { id: 4, sessionId: id, turnId: 'previous', kind: 'user', createdAtMs: 1, turnStatus: 'completed',
          messageStatus: 'completed', payload: { v: 1, inputId: 'old-message', content: 'Read the weekly report.', triggerKind: 'user' } },
        { id: 6, sessionId: id, turnId: currentTurn, kind: 'tool_call', createdAtMs: 2,
          payload: { v: 1, callId: 'tool-1', name: 'read_report', args: {} } },
        ...(handedOff ? [{ id: 7, sessionId: id, turnId: currentTurn, kind: 'user', createdAtMs: 3, turnStatus: 'running',
          messageStatus: 'processing', payload: { v: 1, inputId: 'input-2', content: 'Inspect later', triggerKind: 'user', steered: true } }] : []),
      ],
      state: finished || paused ? 'idle' : 'running', readonlyReason: finished ? 'worker_finished' : null, canReopen: false, workerPaused: paused && !finished, lastTurn: null, unknownDetail: null, canSteer: !paused,
      queuedMessages: [
        ...(queuedVisible ? [{ id: 'input-2', text: 'Inspect later', queuedAtMs: 2, state: 'waiting' }] : []),
        ...(!finished ? [{ id: 'abandoned', text: 'Check the appendix', queuedAtMs: 2, state: 'waiting' }] : []),
      ],
      activeTurn: paused ? null : { id: currentTurn, sessionId: id, status: 'running', startedAtMs: Date.now() },
      worker: { task: { id: 'task-1', trackId: 'daily', blockId: 'block-1', taskKey: 'inspect', kind: 'agent', status: 'running', createdAtMs: 1, updatedAtMs: 2 },
        attemptId: 'attempt-1', phase: finished ? 'succeeded' : paused ? 'paused' : 'running', nativeSteer: true, sequence: 1, reset: false,
        text: paused ? '' : 'The Worker is reviewing the report.', textTruncated: false,
        events: [
          { sequence: 1, kind: 'session.tool_progress', payload: { callId: 'tool-1', phase: 'started', name: 'read_report', args: {} }, createdAtMs: 1 },
          { sequence: 2, kind: 'session.tool_progress', payload: { callId: 'tool-1', phase: 'completed', output: 'Found five rows.' }, createdAtMs: 2 },
        ],
        messages: [
          { id: 'old-message', author: 'lead', content: 'Read the weekly report.', delivery: 'delivered', createdAtMs: 2 },
          { id: 'abandoned', author: 'owner:installation', content: 'Check the appendix',
            delivery: finished ? 'interrupted' : 'queued', createdAtMs: 2 },
        ],
        pendingApprovals: approvalOpen ? [approval] : [],
        nextSettings: settings, activeSettings: settings, canChangeModel: true, reasoningEfforts: ['low'],
        providerConfiguration, approvalAuthorizationConfigured: true },
    });
    if (request.path === '/api/v1/tracks/daily/executions/turn-1/approvals/worker-ask/decision') {
      approvalOpen = false;
      return ok({ ...approval, status: 'answered', answeredAtMs: 5 });
    }
    if (request.path === `${path}/settings`) {
      const next = request.body as typeof settings;
      if (next.approvalMode === 'deny') await denyApproval;
      settings = { ...next, version: settings.version + 1 };
      return ok(settings);
    }
    if (request.path === `${path}/messages/queue/input-2/steer`) {
      expect(request.body).toEqual({ turnId: currentTurn });
      queuedVisible = false;
      handedOff = true;
      return ok({ inputId: 'input-2', delivery: 'accepted' });
    }
    if (request.path === `${path}/messages`) {
      if ((request.body as { content: string }).content === 'Resume the task') {
        expect(paused).toBe(true);
        expect(request.body).toEqual({ attemptId: 'attempt-1', content: 'Resume the task' });
        refused = true;
        return { status: 409, statusText: 'Conflict', body: { code: 'conflict', error: 'The task changed.' } };
      }
      queuedRequests++;
      queuedVisible = queuedRequests > 1;
      if (queuedRequests === 1) {
        paused = true;
        currentTurn = 'turn-2';
        return { status: 502, statusText: 'Bad gateway', body: { error: 'The response was lost.' } };
      }
      paused = false;
      return ok({ inputId: 'input-2', delivery: 'queued' });
    }
    if (request.path === `${path}/interrupt`) { paused = true; return ok({ ok: true }); }
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    await screen.findByText('The Worker is reviewing the report.');
    expect(screen.queryByRole('button', { name: /Worker activity|Worker settings/ })).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'Delivery' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Approvals: Ask a person' })).toBeVisible();
    expect((await screen.findByText('Approve reading the report?')).closest('[data-nc-conversation-action="ask"]')).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(view.requests.find(request => request.path === '/api/v1/tracks/daily/executions/turn-1/approvals/worker-ask/decision')?.body).toEqual({
      optionId: 'approve',
    }));
    await waitFor(() => expect(screen.queryByText('Approve reading the report?')).toBeNull());

    expect(screen.getAllByText('Read the weekly report.')).toHaveLength(1);
    const tool = await screen.findByRole('button', { name: /Called read_report/ });
    fireEvent.click(tool);
    await screen.findByText(/Found five rows/);
    expect(screen.getAllByRole('button', { name: /Called read_report/ })).toHaveLength(1);

    fireEvent.click(await screen.findByRole('button', { name: /^Model: gpt-5\.4-mini/ }));
    const picker = within(await screen.findByRole('dialog', { name: 'Select model and reasoning effort' }));
    fireEvent.click(picker.getByRole('radio', { name: 'GPT 5.4' }));
    fireEvent.keyDown(picker.getByRole('slider', { name: 'Reasoning effort' }), { key: 'End' });
    fireEvent.click(picker.getByRole('button', { name: 'Save' }));
    await screen.findByRole('button', { name: /^Model: GPT 5.4 · high/ });

    fireEvent.click(screen.getByRole('button', { name: 'Approvals: Ask a person' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Deny requests/ }));
    await waitFor(() => expect(view.requests.filter(request => request.path === `${path}/settings`)).toHaveLength(2));
    const composer = screen.getByRole('combobox', { name: 'Message' });
    await act(async () => {
      composer.textContent = 'Inspect later';
      fireEvent.input(composer);
      await Promise.resolve();
      fireEvent.keyDown(composer, { key: 'Enter' });
    });
    expect(view.requests.filter(request => request.path === `${path}/messages`)).toHaveLength(0);
    releaseDeny();
    await screen.findByRole('button', { name: 'Approvals: Deny requests' });
    const settingsWrites = view.requests.filter(request => request.path === `${path}/settings`);
    expect(settingsWrites.map(request => request.body)).toEqual([
      { attemptId: 'attempt-1', version: 0, model: 'gpt-5.4', reasoningEffort: 'high', approvalMode: 'ask' },
      { attemptId: 'attempt-1', version: 1, model: 'gpt-5.4', reasoningEffort: 'high', approvalMode: 'deny' },
    ]);
    fireEvent.click(screen.getByRole('button', { name: 'Approvals: Deny requests' }));
    const restore = await screen.findByRole('menuitem', { name: /Ask a person/ });
    expect(restore).toHaveAttribute('aria-disabled', 'true');
    expect(screen.queryByLabelText('Owner authorization key')).toBeNull();
    fireEvent.keyDown(restore, { key: 'Escape' });

    await screen.findByText('发送结果未确认。');
    fireEvent.click(await screen.findByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/ }));
    await waitFor(() => expect(queuedRequests).toBe(2));
    const queued = view.requests.filter(request => request.path === `${path}/messages`);
    expect(queued[0].body).toEqual({ attemptId: 'attempt-1', content: 'Inspect later', mode: 'queue' });
    expect(queued[1].body).toEqual(queued[0].body);
    expect(queued[0].headers?.['Idempotency-Key']).toBeTruthy();
    expect(queued[1].headers?.['Idempotency-Key']).toBe(queued[0].headers?.['Idempotency-Key']);
    await waitFor(() => expect(screen.queryByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/ })).toBeNull());
    const sharedQueue = within(await screen.findByRole('region', { name: '排队消息' }));
    const waiting = await sharedQueue.findByText('Inspect later');
    expect(waiting).toBeVisible();
    await page.elementLocator(waiting.closest('[data-nc-pending-entry]')!).getByRole('button', { name: '立即插队' }).click();
    await waitFor(() => expect(view.requests.some(request => request.path === `${path}/messages/queue/input-2/steer`)).toBe(true));
    await waitFor(() => expect(document.querySelector('[data-nc-pending-entry="input-2"]')).toBeNull());
    await waitFor(() => expect(screen.getAllByText('Inspect later')).toHaveLength(1));

    fireEvent.click(screen.getByRole('button', { name: 'Stop' }));
    await screen.findByText('已暂停，发送消息即可继续。');
    expect(view.requests.find(request => request.path === `${path}/interrupt`)?.body).toEqual({ attemptId: 'attempt-1', turnId: 'turn-2' });
    await act(async () => { composer.textContent = 'Resume the task'; fireEvent.input(composer); await Promise.resolve(); });
    fireEvent.keyDown(composer, { key: 'Enter' });
    await waitFor(() => expect(refused).toBe(true));
    await screen.findByText(/消息未发送.*The task changed/);
    fireEvent.click(await screen.findByRole('button', { name: '放回输入框' }));
    expect(composer).toHaveTextContent('Resume the task');
    expect(screen.queryByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/ })).toBeNull();

    finished = true;
    await act(async () => { await view.client.invalidateQueries({ queryKey: queryKeys.plannerRun(id) }); });
    expect(await screen.findByText('Delivery was interrupted.')).toBeVisible();
    expect(screen.getAllByText('Check the appendix')).toHaveLength(1);
    expect(screen.queryByRole('button', { name: /^Model:/ })).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'Message' })).toBeNull();
  } finally { releaseDeny(); view.client.clear(); }
});

it('dispatches a Worker from the report with only a goal', async () => {
  await page.viewport(1280, 820);
  const path = '/api/v1/tracks/project/tasks/dispatch';
  const view = renderDailyFixture({ initial: '/track/project', reply: request => {
    if (request.path === '/api/v1/tracks/project/report')
      return ok({ docRev: 1, renderedMarkdown: '# Project result\n\nRelease is ready.', blocks: [], tasks: [] });
    if (request.path === path) return ok({ task: { id: 'task-1', trackId: 'project', blockId: 'block-1', taskKey: 'inspect',
      kind: 'agent', status: 'pending', createdAtMs: 1, updatedAtMs: 2 }, sessionId: null, requestKey: 'dispatch-receipt' });
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: 'Dispatch Worker' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /^Goal/ }), { target: { value: 'Inspect the release' } });
    fireEvent.click(screen.getByRole('button', { name: /^Dispatch$/ }));
    await screen.findByText('Worker task queued.');
    const request = view.requests.find(candidate => candidate.path === path);
    expect(request).toMatchObject({ method: 'POST', body: { goal: 'Inspect the release' } });
    expect(request?.body).toEqual({ goal: 'Inspect the release' });
    expect(request?.headers?.['Idempotency-Key']).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Dispatch Worker' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /^Goal/ }), { target: { value: 'Inspect the release' } });
    fireEvent.click(screen.getByText('Dispatch options'));
    fireEvent.change(screen.getByRole('combobox', { name: 'Access' }), { target: { value: 'read_only' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Acceptance criteria' }), { target: { value: 'List missing checks' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Dependencies' }), { target: { value: 'build, review' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Validation command' }), { target: { value: 'dotnet test' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Provider' }), { target: { value: 'codex' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Model' }), { target: { value: 'gpt-5.4' } });
    fireEvent.click(screen.getByRole('button', { name: /^Dispatch$/ }));
    await screen.findByText('Worker task queued.');
    const advanced = view.requests.filter(candidate => candidate.path === path)[1];
    expect(advanced.body).toEqual({ goal: 'Inspect the release', access: 'read_only', acceptance: 'List missing checks',
      dependsOn: ['build', 'review'], gate: 'dotnet test', provider: 'codex', model: 'gpt-5.4' });
    expect(advanced.headers?.['Idempotency-Key']).not.toBe(request?.headers?.['Idempotency-Key']);
  } finally { view.client.clear(); }
});

it('keeps one timeline item when a waiting conversation message is steered', async () => {
  await page.viewport(1280, 820);
  const id = 'second-planner';
  const path = `/api/v1/sessions/${id}`;
  const text = 'Use the shared message identity';
  let handedOff = false;
  const view = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (request.path === '/api/v1/bootstrap') return ok({ assistantProviders: true, conversationInitialInput: true,
      webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'queue-handoff' });
    if (request.path.startsWith(`${path}/items?`)) return ok({
      session: { id, trackId: 'daily', provider: 'codex', contextUsage: null, status: 'running', createdAtMs: 1, updatedAtMs: 2 },
      activeTurn: { id: 'turn-1', sessionId: id, status: 'running', startedAtMs: Date.now() },
      state: 'running', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, canSteer: true,
      queuedMessages: handedOff ? [] : [{ id: 'message-1', text, queuedAtMs: 2, state: 'waiting' }],
      queueHandoffIds: handedOff ? ['message-1'] : [],
      items: [{
        id: 4, sessionId: id, turnId: 'turn-1', kind: 'user', createdAtMs: 1, turnStatus: 'running',
        messageStatus: 'processing', payload: { v: 1, inputId: 'initial', content: 'Start this turn', triggerKind: 'user' },
      }, ...(handedOff ? [{
        id: 5, sessionId: id, turnId: 'turn-1', kind: 'user', createdAtMs: 3, turnStatus: 'running',
        messageStatus: 'processing', payload: { v: 1, inputId: 'message-1', content: text, triggerKind: 'user', steered: true },
      }] : [])],
    });
    if (request.path === `${path}/messages/queue/message-1/steer`) {
      expect(request.body).toEqual({ turnId: 'turn-1' });
      handedOff = true;
      return ok({ inputId: 'message-1', delivery: 'accepted' });
    }
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    const queue = within(await screen.findByRole('region', { name: '排队消息' }));
    expect(await queue.findByText(text)).toBeVisible();
    await page.getByRole('button', { name: '立即插队' }).click();
    await waitFor(() => expect(view.requests.some(request => request.path === `${path}/messages/queue/message-1/steer`)).toBe(true));
    await waitFor(() => expect(document.querySelector('[data-nc-pending-entry="message-1"]')).toBeNull());
    await waitFor(() => expect(screen.getAllByText(text)).toHaveLength(1));
  } finally { view.client.clear(); }
});
