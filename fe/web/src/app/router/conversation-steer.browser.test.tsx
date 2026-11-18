import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import { mountSteerFixture, steerAnswer } from './conversation-steer-fixture.tsx';
import '../../styles/entry.css';
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

it('submits two supplements through the composer while the first answer is held', async () => {
  const session = { id: 'daily-planner', trackId: 'daily', isLead: true, provider: 'codex',
    status: 'running', createdAtMs: 1, updatedAtMs: 2 };
  const turnId = 'current-perga-turn';
  const first = 'First supplemental message';
  const second = 'Second supplemental message';
  const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
  let release = () => {};
  const held = new Promise<void>(resolve => { release = resolve; });
  const submitted: unknown[] = [];
  let followUpFailed = false;
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if (request.path.startsWith('/api/v1/sessions/daily-planner/items?')) return ok({
      session, activeTurn: { id: turnId, sessionId: session.id, status: 'running', startedAtMs: 2 },
      unknownInput: null, items: [{ id: 1, sessionId: session.id, turnId, kind: 'user',
        createdAtMs: 2, payload: { content: 'Primary request', inputId: 'primary' } },
      ...(followUpFailed ? [{ id: 2, sessionId: session.id, turnId: 'follow-up-turn', kind: 'user', createdAtMs: 3,
        payload: { content: second, inputId: 'steer-second', deliveryError: 'The provider could not start the queued message.' } }] : [])],
    });
    if (request.method === 'POST' && request.path === '/api/v1/sessions/daily-planner/messages/steer') {
      submitted.push(request.body);
      if (submitted.length === 1) {
        await held;
        return ok({ inputId: 'steer-first', delivery: 'unconfirmed' });
      }
      followUpFailed = true;
      return ok({ inputId: 'steer-second', delivery: 'queued' });
    }
    return undefined;
  } });
  try {
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText('Primary request');
    const field = screen.getByRole('combobox', { name: 'Message' });
    await userEvent.type(field, first);
    await userEvent.click(await screen.findByRole('button', { name: 'Send supplement' }));
    await screen.findByText(first, { exact: true });
    expect(submitted).toEqual([{ turnId, content: first }]);

    await userEvent.type(field, second);
    await userEvent.click(await screen.findByRole('button', { name: 'Send supplement' }));
    await screen.findByText('The provider could not start the queued message.');
    expect(submitted).toEqual([{ turnId, content: first }, { turnId, content: second }]);
    await userEvent.type(field, 'A draft in progress');
    release();
    await screen.findByText('Delivery not confirmed');
    expect(field.textContent).toBe('A draft in progress');
    expect(screen.getByText(first, { exact: true })).toBeVisible();
    expect(screen.getByText(second, { exact: true })).toBeVisible();
  } finally {
    release();
    fixture.client.clear();
  }
});
