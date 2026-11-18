import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import '../../styles/entry.css';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('holds sends until the post-resume read confirms the provider state', async () => {
  const sessionId = 'daily-planner';
  const turnId = 'observed-turn';
  const text = 'A waiting message';
  let resumeCalls = 0;
  let finishRead = () => {};
  const heldRead = new Promise<void>(resolve => { finishRead = resolve; });
  const activeSession = { id: sessionId, trackId: 'daily', provider: 'codex', isLead: true,
    status: 'running', createdAtMs: 1, updatedAtMs: 2 };
  const fixture = renderDailyFixture({
    initial: '/track/daily?panel=conversations',
    reply: async request => {
      if (request.path === `/api/v1/sessions/${sessionId}/resume`) {
        expect(request.body).toEqual({ turnId });
        resumeCalls++;
        if (resumeCalls === 1)
          return { status: 409, statusText: 'Conflict', body: { code: 'precondition_failed', error: 'The previous provider execution could not be confirmed stopped.' } };
        return { status: 200, statusText: 'OK', body: { ...activeSession, status: 'idle' } };
      }
      if (!request.path.startsWith(`/api/v1/sessions/${sessionId}/items?`)) return undefined;
      if (resumeCalls === 1 && request.path.includes('view=run')) await heldRead;
      const running = resumeCalls < 2;
      return { status: 200, statusText: 'OK', body: {
        session: { ...activeSession, status: running ? 'running' : 'idle' },
        activeTurn: running ? { id: turnId, sessionId, status: 'running', startedAtMs: 2 } : null,
        unknownInput: null,
        resume: running ? { turnId, state: resumeCalls === 0 ? 'available' : 'paused' } : null,
        canSteer: true,
        queuedMessages: running ? [{ id: 'next-message', text, queuedAtMs: 3, state: 'waiting' }] : [],
        items: [{ id: 1, sessionId, turnId, kind: 'user',
          createdAtMs: 2, payload: { content: 'Original request', inputId: 'original' } }],
      } };
    },
  });
  try {
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText(text, { exact: true });
    const field = screen.getByRole('combobox', { name: 'Message' });
    await userEvent.click(await screen.findByRole('button', { name: 'Resume provider session' }));
    await screen.findByText('Restoring the provider session…');
    expect(resumeCalls).toBe(1);
    expect(screen.queryByRole('button', { name: 'Resume provider session' })).toBeNull();
    expect(field.getAttribute('contenteditable')).toBe('false');

    finishRead();
    await screen.findByText('Provider recovery is paused.');
    expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Say it now' })).toBeNull();
    expect(field.getAttribute('contenteditable')).toBe('false');

    await userEvent.click(await screen.findByRole('button', { name: 'Resume provider session' }));
    await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));
    expect(resumeCalls).toBe(2);
    expect(screen.queryByText('Provider recovery is paused.')).toBeNull();
  } finally {
    finishRead();
    fixture.client.clear();
  }
});
