import { sessionPage } from './conversation-session-data.ts';
import { fixtureBootstrap, fixtureSession } from './session-contract-fixture.ts';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); document.getElementById('root')?.remove(); });

it('keeps the side command text and requires support when v1 does not advertise side creation', async () => {
  const frames = new Map<number, FrameRequestCallback>();
  let sequence = 0;
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frames.set(++sequence, callback); return sequence; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => { frames.delete(id); });
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true,
    reply: request => {
      if (request.path === '/api/v1/bootstrap') return { status: 200, statusText: 'OK', body: fixtureBootstrap() };
      if (request.path.includes('/second-planner/items') && !request.path.includes('limit=1')) return { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('second-planner', 'daily'), [{ id: 1, sessionId: 'second-planner', turnId: 'turn', kind: 'assistant_text', payload: { text: 'Source ready for presentation.' }, createdAtMs: 1 }]) };
      return undefined;
    },
  });
  try {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    const field = await screen.findByRole('combobox', { name: 'Message' });
    await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));
    expect(document.querySelector('[data-nc-thread]')).toBeNull();
    await userEvent.type(field, '/');
    const menu = await screen.findByRole('listbox', { name: 'Commands' });
    const premature = within(menu).queryByRole('option', { name: /^side/ });
    if (premature !== null) await userEvent.click(premature);
    expect(field.textContent).toBe('/');
    expect(premature).toBeNull();
    for (let index = 0; index < 2; index++) {
      const callbacks = [...frames.values()];
      frames.clear();
      await act(async () => {
        for (const callback of callbacks) callback(0);
        await Promise.resolve();
      });
    }
    await screen.findByText('Source ready for presentation.');
    await userEvent.type(field, 's');
    await userEvent.click(await screen.findByRole('option', { name: /^side/ }));
    expect(field.textContent).toBe('/side ');
    expect(screen.queryByRole('region', { name: 'Side conversation · Codex' })).toBeNull();
    expect(fixture.requests.filter(request => ['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method))).toHaveLength(0);
    await userEvent.keyboard('{Enter}');
    await screen.findByText('Side conversations require a server that reports support. Update or reconnect, then try again.');
    expect(screen.queryByRole('region', { name: 'Side conversation · Codex' })).toBeNull();
    expect(fixture.requests.filter(request => request.method === 'POST'
      && request.path.endsWith('/messages'))).toHaveLength(0);
  } finally { cleanup(); fixture.client.clear(); }
});
