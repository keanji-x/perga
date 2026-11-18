import '../../styles/entry.css';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('shows Worker progress and approvals, preserves retry targets, and stops the displayed turn', async () => {
  await page.viewport(1280, 820);
  const id = 'second-planner';
  const path = `/api/v1/sessions/${id}`;
  let paused = false;
  let approvalOpen = true;
  let queuedRequests = 0;
  let staleSteers = 0;
  let currentTurn = 'turn-1';
  const approval = { id: 'worker-ask', trackId: 'daily', status: 'open', createdAtMs: 4,
    questions: [{ question: 'Approve reading the report?', options: ['Approve', 'Deny'], isMultiSelect: false }] };
  let settings = { version: 0, model: null as string | null, reasoningEffort: null as string | null, approvalMode: 'ask' };
  const view = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (request.path === '/api/v1/bootstrap') return ok({ assistantProviders: true, conversationInitialInput: true,
      webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'worker-session' });
    if (request.path === '/api/v1/bootstrap?scope=managed') return ok([{
      availability: { provider: 'codex', status: 'ready', reason: null, checked_at_ms: 1, authentication_method: null, authentication_notice: null },
      accepts_unavailable_blank_track: true, capabilities: { managed_conversation: true, steer: true, images: false,
        manual_compact: false, history_replacement: false, requires_ready_availability: false },
    }]);
    if (request.path.startsWith(`${path}/items?`)) return ok({
      session: { id, trackId: 'daily', provider: 'codex', boundTaskId: 'task-1', boundAttemptId: 'attempt-1', status: paused ? 'idle' : 'running', createdAtMs: 1, updatedAtMs: 2 },
      items: [], unknownInput: null,
      activeTurn: paused ? null : { id: currentTurn, sessionId: id, status: 'running', startedAtMs: Date.now() },
      worker: { task: { id: 'task-1', trackId: 'daily', blockId: 'block-1', taskKey: 'inspect', kind: 'agent', status: 'running', createdAtMs: 1, updatedAtMs: 2 },
        attemptId: 'attempt-1', phase: paused ? 'paused' : 'running', nativeSteer: true, sequence: 1, reset: false,
        text: paused ? '' : 'The Worker is reviewing the report.', textTruncated: false,
        events: [
          { sequence: 1, kind: 'session.tool_progress', payload: { callId: 'tool-1', phase: 'started', name: 'read_report', args: {} }, createdAtMs: 1 },
          { sequence: 2, kind: 'session.tool_progress', payload: { callId: 'tool-1', phase: 'completed', output: 'Found five rows.' }, createdAtMs: 2 },
        ],
        messages: [{ id: 'old-message', author: 'lead', content: 'Read the weekly report.', delivery: 'delivered', createdAtMs: 2 }],
        openAsks: approvalOpen ? [approval] : [],
        nextSettings: settings, activeSettings: settings, canChangeModel: true, reasoningEfforts: ['low', 'high'] },
    });
    if (request.path === '/api/v1/tracks/daily/asks/worker-ask/answer') {
      approvalOpen = false;
      return ok({ ...approval, status: 'answered', answeredAtMs: 5 });
    }
    if (request.path === `${path}/settings`) {
      settings = { ...(request.body as typeof settings), version: 1 };
      return ok(settings);
    }
    if (request.path === `${path}/messages`) {
      const body = request.body as { mode?: string; content?: string };
      if (body.content === 'Steer before the turn changes') {
        staleSteers++;
        if (staleSteers === 1) {
          currentTurn = 'turn-2';
          return { status: 502, statusText: 'Bad gateway', body: { error: 'The response was lost.' } };
        }
        return { status: 409, statusText: 'Conflict', body: { code: 'worker_turn_changed', error: 'The Worker turn changed. Refresh the session before steering.' } };
      }
      if (body.mode !== 'queue') return ok({ inputId: 'input-1', turnId: currentTurn, delivery: 'delivered' });
      queuedRequests++;
      return queuedRequests === 1
        ? { status: 502, statusText: 'Bad gateway', body: { error: 'The response was lost.' } }
        : ok({ inputId: 'input-2', delivery: 'queued' });
    }
    if (request.path === `${path}/interrupt`) { paused = true; return ok({ ok: true }); }
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    await screen.findByText('The Worker is reviewing the report.');
    await screen.findByText('Approve reading the report?');
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(view.requests.find(request => request.path === '/api/v1/tracks/daily/asks/worker-ask/answer')?.body).toEqual({
      answers: [{ option: 0, value: 'Approve' }],
    }));
    await waitFor(() => expect(screen.queryByText('Approve reading the report?')).toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Worker activity' }));
    const activity = within(await screen.findByRole('dialog', { name: 'Worker activity' }));
    await activity.findByText('Read the weekly report.', { exact: false });
    fireEvent.click(activity.getByText('read_report · Completed'));
    expect(activity.getByText('Found five rows.')).toBeVisible();
    fireEvent.click(activity.getByRole('button', { name: 'Close' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Worker settings' }));
    expect(within(screen.getByRole('region', { name: 'Current turn settings' })).getByText('Ask a person')).toBeVisible();
    fireEvent.change(await screen.findByRole('textbox', { name: 'Model' }), { target: { value: 'gpt-5.4' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Reasoning effort' }), { target: { value: 'high' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Approvals' }), { target: { value: 'deny' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await screen.findByText('Settings saved for the next turn.');
    expect(view.requests.find(request => request.path === `${path}/settings`)?.body).toEqual({
      attemptId: 'attempt-1', version: 0, model: 'gpt-5.4', reasoningEffort: 'high', approvalMode: 'deny',
    });

    const composer = await screen.findByRole('combobox', { name: 'Message' });
    await act(async () => { composer.textContent = 'Review the dates'; fireEvent.input(composer); await Promise.resolve(); });
    fireEvent.keyDown(composer, { key: 'Enter' });
    await waitFor(() => expect(view.requests.find(request => request.path === `${path}/messages`)?.body).toEqual({
      content: 'Review the dates', attemptId: 'attempt-1', turnId: 'turn-1', mode: 'steer',
    }));
    await waitFor(() => expect(composer.getAttribute('contenteditable')).toBe('true'));
    fireEvent.change(screen.getByRole('combobox', { name: 'Delivery' }), { target: { value: 'queue' } });
    await act(async () => { composer.textContent = 'Inspect later'; fireEvent.input(composer); await Promise.resolve(); });
    fireEvent.keyDown(composer, { key: 'Enter' });
    fireEvent.click(await screen.findByRole('button', { name: 'Retry same message' }));
    await waitFor(() => expect(queuedRequests).toBe(2));
    const queued = view.requests.filter(request => request.path === `${path}/messages`).slice(1);
    expect(queued[0].body).toEqual({ attemptId: 'attempt-1', content: 'Inspect later', mode: 'queue' });
    expect(queued[1].body).toEqual(queued[0].body);
    expect(queued[0].headers?.['Idempotency-Key']).toBeTruthy();
    expect(queued[1].headers?.['Idempotency-Key']).toBe(queued[0].headers?.['Idempotency-Key']);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Retry same message' })).toBeNull());

    fireEvent.change(screen.getByRole('combobox', { name: 'Delivery' }), { target: { value: 'steer' } });
    await act(async () => { composer.textContent = 'Steer before the turn changes'; fireEvent.input(composer); await Promise.resolve(); });
    fireEvent.keyDown(composer, { key: 'Enter' });
    fireEvent.click(await screen.findByRole('button', { name: 'Retry same message' }));
    await waitFor(() => expect(composer).toHaveTextContent('Steer before the turn changes'));
    expect(screen.queryByRole('button', { name: 'Retry same message' })).toBeNull();
    const [firstSteer, retriedSteer] = view.requests.filter(request => request.path === `${path}/messages`).slice(3);
    expect(firstSteer.body).toEqual({ attemptId: 'attempt-1', turnId: 'turn-1', content: 'Steer before the turn changes', mode: 'steer' });
    expect(retriedSteer.body).toEqual(firstSteer.body);
    expect(retriedSteer.headers?.['Idempotency-Key']).toBe(firstSteer.headers?.['Idempotency-Key']);

    fireEvent.click(screen.getByRole('button', { name: 'Stop' }));
    await screen.findByText('Worker paused. Send a message to continue.');
    expect(view.requests.find(request => request.path === `${path}/interrupt`)?.body).toEqual({ attemptId: 'attempt-1', turnId: 'turn-2' });
  } finally { view.client.clear(); }
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
