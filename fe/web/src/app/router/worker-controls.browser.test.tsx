import '../../styles/entry.css';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('shows live Worker progress and sends settings, steer, and stop to the displayed attempt', async () => {
  await page.viewport(1280, 820);
  const id = 'second-planner';
  const path = `/api/v1/sessions/${id}`;
  let paused = false;
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
      activeTurn: paused ? null : { id: 'turn-1', sessionId: id, status: 'running', startedAtMs: Date.now() },
      worker: { task: { id: 'task-1', trackId: 'daily', blockId: 'block-1', taskKey: 'inspect', kind: 'agent', status: 'running', createdAtMs: 1, updatedAtMs: 2 },
        attemptId: 'attempt-1', phase: paused ? 'paused' : 'running', nativeSteer: true, sequence: 1, reset: false,
        text: paused ? '' : 'The Worker is reviewing the report.', textTruncated: false, events: [], messages: [], openAsks: [],
        nextSettings: settings, activeSettings: settings, canChangeModel: true, reasoningEfforts: ['low', 'high'] },
    });
    if (request.path === `${path}/settings`) {
      settings = { ...(request.body as typeof settings), version: 1 };
      return ok(settings);
    }
    if (request.path === `${path}/messages`) return ok({ inputId: 'input-1', turnId: 'turn-1', delivery: 'delivered' });
    if (request.path === `${path}/interrupt`) { paused = true; return ok({ ok: true }); }
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    await screen.findByText('The Worker is reviewing the report.');
    fireEvent.click(await screen.findByRole('button', { name: 'Worker settings' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Model' }), { target: { value: 'gpt-5.4' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Reasoning effort' }), { target: { value: 'high' } });
    fireEvent.change(screen.getByRole('combobox', { name: 'Approvals' }), { target: { value: 'deny' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await screen.findByText('Settings saved for the next turn.');
    expect(view.requests.find(request => request.path === `${path}/settings`)?.body).toEqual({
      attemptId: 'attempt-1', version: 0, model: 'gpt-5.4', reasoningEffort: 'high', approvalMode: 'deny',
    });

    const composer = screen.getByRole('combobox', { name: 'Message' });
    await act(async () => { composer.textContent = 'Review the dates'; fireEvent.input(composer); await Promise.resolve(); });
    fireEvent.keyDown(composer, { key: 'Enter' });
    await waitFor(() => expect(view.requests.find(request => request.path === `${path}/messages`)?.body).toEqual({
      content: 'Review the dates', attemptId: 'attempt-1', turnId: 'turn-1', allowQueue: false,
    }));
    fireEvent.click(screen.getByRole('button', { name: 'Stop' }));
    await screen.findByText('Worker paused. Send a message to continue.');
    expect(view.requests.find(request => request.path === `${path}/interrupt`)?.body).toEqual({ attemptId: 'attempt-1', turnId: 'turn-1' });
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
    expect(request?.headers?.['Idempotency-Key']).toBeTruthy();
  } finally { view.client.clear(); }
});
