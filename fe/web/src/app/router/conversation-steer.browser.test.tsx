import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import { mountSteerFixture, steerAnswer } from './conversation-steer-fixture.tsx';
import '../../styles/entry.css';
import { queryKeys } from '../providers/queries.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });
it.each([['codex', true], ['claude', false], ['opencode', false]] as const)(
  'queued %s actions consume declared steer in the real browser', async (provider, steer) => {
    const fixture = mountSteerFixture(provider, () => ({ status: 200, statusText: 'OK', body: steerAnswer(provider, steer) }));
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText('Queued words');
    await waitFor(() => expect(fixture.client.getQueryData(['managed-providers'])).toEqual(steerAnswer(provider, steer)));
    expect(screen.queryByRole('button', { name: 'Say it now' }) !== null).toBe(steer);
    const edit = screen.getByRole('button', { name: 'Edit queued message' });
    expect(screen.getByRole('button', { name: 'Delete this message' })).toBeTruthy();
    edit.focus();
    expect(document.activeElement).toBe(edit);
  });

it.each([
  { canSteer: false, delivery: null },
  { canSteer: true, delivery: 'accepted' },
  { canSteer: true, delivery: 'unconfirmed' },
] as const)('defaults to queueing and exposes explicit steering ($delivery)', async ({ canSteer, delivery }) => {
  const session = { id: 'daily-planner', trackId: 'daily', isLead: true, provider: canSteer ? 'codex' : 'claude',
    status: 'running', createdAtMs: 1, updatedAtMs: 2 };
  const turnId = 'current-perga-turn';
  const first = 'First queued message';
  const second = 'Second queued message';
  const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
  const queuedMessages: Array<{ id: string; text: string; queuedAtMs: number; state: 'waiting' | 'unconfirmed' }> = [];
  const steered: unknown[] = [];
  let savedSteer: string | undefined;
  let showSteerInHistory = false;
  let releaseReceipt = () => {};
  const firstReceipt = new Promise<void>(resolve => { releaseReceipt = resolve; });
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if (request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return ok({
      session, activeTurn: { id: turnId, sessionId: session.id, status: 'running', startedAtMs: 2 },
      unknownInput: null, canSteer, queuedMessages: [...queuedMessages], queueHandoffIds: savedSteer === undefined ? [] : [savedSteer],
      items: [
        { id: 1, sessionId: session.id, turnId, kind: 'user',
          createdAtMs: 2, payload: { content: 'Primary request', inputId: 'primary' } },
        ...(showSteerInHistory && savedSteer !== undefined ? [{ id: 2, sessionId: session.id, turnId, kind: 'user',
          createdAtMs: 3, messageStatus: 'processing', payload: { content: first, inputId: savedSteer, steered: true } }] : []),
      ],
    });
    if (request.method === 'POST' && request.path === '/api/v1/sessions/daily-planner/messages') {
      const body = request.body as { content: string; queueIfBusy: boolean; steerIfRunning: boolean };
      expect(body).toMatchObject({ queueIfBusy: true, steerIfRunning: false });
      const id = `queued-${queuedMessages.length + 1}`;
      queuedMessages.push({ id, text: body.content, queuedAtMs: queuedMessages.length + 1, state: 'waiting' });
      if (body.content === first) await firstReceipt;
      return { status: 202, statusText: 'Accepted', body: { sessionId: session.id, inputId: id, messageId: id, queued: true } };
    }
    if (request.method === 'POST' && request.path.endsWith('/messages/queue/queued-1/steer')) {
      steered.push(request.body);
      if (delivery === 'accepted') savedSteer = queuedMessages.shift()?.id;
      else queuedMessages[0].state = 'unconfirmed';
      return ok({ inputId: 'queued-1', delivery });
    }
    return undefined;
  } });
  try {
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText('Primary request');
    const field = screen.getByRole('combobox', { name: 'Message' });
    for (const text of [first, second]) {
      await userEvent.type(field, text);
      await userEvent.click(await screen.findByRole('button', { name: /^Send$/ }));
      if (text === first) {
        await waitFor(() => expect(queuedMessages).toHaveLength(1));
        await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(session.id) });
        await screen.findByText(first, { exact: true });
        expect(screen.getAllByText(first, { exact: true })).toHaveLength(1);
        releaseReceipt();
      }
      await screen.findByText(text, { exact: true });
      await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));
    }
    const queue = await screen.findByRole('region', { name: 'Queued messages' });
    expect(queue.textContent).toContain(first);
    expect(queue.textContent).toContain(second);
    expect(steered).toEqual([]);
    if (canSteer) {
      await userEvent.click((await screen.findAllByRole('button', { name: 'Say it now' }))[0]);
      if (delivery === 'accepted') {
        await screen.findByText('Processing', { exact: true });
        expect(screen.getAllByText(first, { exact: true })).toHaveLength(1);
        expect(queuedMessages.map(message => message.text)).toEqual([second]);
        showSteerInHistory = true;
        await fixture.client.invalidateQueries({ queryKey: queryKeys.harnessItems(session.id) });
        await waitFor(() => expect(document.querySelector('[data-nc-pending-entry="queued-1"]')).toBeNull());
        expect(screen.getAllByText(first, { exact: true })).toHaveLength(1);
      } else {
        await screen.findByText('Delivery not confirmed. This message is paused.');
        expect(screen.getAllByRole('button', { name: 'Say it now' })).toHaveLength(1);
        expect(queuedMessages.map(message => message.state)).toEqual(['unconfirmed', 'waiting']);
      }
      expect(steered).toEqual([{ turnId }]);
    } else {
      expect(screen.queryByRole('button', { name: 'Say it now' })).toBeNull();
    }
  } finally {
    releaseReceipt();
    fixture.client.clear();
  }
});

it.each(['follow-up', 'steered'])('shows one %s message through delayed history and reopening', async kind => {
  const sessionId = 'daily-planner';
  const messageId = 'same-queued-message';
  const text = 'Keep the exact queued text visible';
  let status: 'waiting' | 'sending' | 'processing' | 'completed' = 'waiting';
  let historyHasMessage = false;
  const mount = () => renderDailyFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (!request.path.startsWith(`/api/v1/sessions/${sessionId}/items?`)) return undefined;
    const turnId = status === 'waiting' ? 'primary-turn' : 'follow-up-turn';
    return { status: 200, statusText: 'OK', body: {
      session: { id: sessionId, trackId: 'daily', isLead: true, provider: 'codex',
        status: status === 'completed' ? 'idle' : 'running', createdAtMs: 1, updatedAtMs: 3 },
      activeTurn: status === 'completed' ? null : { id: turnId, sessionId, status: 'running', startedAtMs: 2 },
      unknownInput: null, canSteer: true, queueHandoffIds: status === 'completed' ? [messageId] : [],
      queuedMessages: status === 'completed' ? [] : [{ id: messageId, text, queuedAtMs: 3, state: status }],
      items: [
        { id: 1, sessionId, turnId: 'primary-turn', kind: 'user', createdAtMs: 2,
          payload: { content: 'Primary request', inputId: 'primary' } },
        ...(historyHasMessage ? [{ id: 2, sessionId, turnId: 'follow-up-turn', kind: 'user', createdAtMs: 3,
          messageStatus: status, payload: { content: text, inputId: messageId, steered: kind === 'steered' } }] : []),
      ],
    } };
  } });
  const open = async () => {
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText(text, { exact: true });
    expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);
  };
  let fixture = mount();
  try {
    await open();
    await screen.findByText('Queued', { exact: true });
    if (kind === 'follow-up') {
      status = 'sending';
      await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(sessionId) });
      await screen.findByText('Sending…', { exact: true });
      expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);
      expect(screen.queryByText('Processing', { exact: true })).toBeNull();

      status = 'processing';
      await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(sessionId) });
      await screen.findByText('Processing', { exact: true });
      expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);
    }
    status = 'completed';
    await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(sessionId) });
    expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);

    historyHasMessage = true;
    await fixture.client.invalidateQueries({ queryKey: queryKeys.harnessItems(sessionId) });
    await screen.findByText('Completed', { exact: true });
    expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);
    expect(document.querySelector('[data-nc-pending-entry="same-queued-message"]')).toBeNull();

    cleanup();
    fixture.client.clear();
    document.getElementById('root')?.remove();
    fixture = mount();
    await open();
    await screen.findByText('Completed', { exact: true });
  } finally {
    fixture.client.clear();
  }
});
