import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';
import { fixtureCreation, fixtureSession } from './session-contract-fixture.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); document.getElementById('root')?.remove(); });

it('offers the side command once its source discussion is ready and opens a side draft', async () => {
  const frames = new Map<number, FrameRequestCallback>();
  let sequence = 0;
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { frames.set(++sequence, callback); return sequence; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => { frames.delete(id); });
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true,
    reply: request => {
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
    await screen.findByRole('button', { name: 'Close side conversation' });
    expect(fixture.requests.filter(request => request.method === 'POST'
      && request.path.endsWith('/messages'))).toHaveLength(0);
  } finally { cleanup(); fixture.client.clear(); }
});

it('sends a typed /Side question while provider discovery is pending and reopens the saved side', async () => {
  const parent = 'daily-planner';
  const side = fixtureSession('side-id', 'daily', { title: 'Explore this', sourceSessionId: parent, updatedAtMs: 10 });
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/bootstrap?scope=managed') return new Promise(() => {});
    if (request.method === 'POST' && request.path === '/api/v1/tracks/daily/sessions') {
      fixture.sessions.push(side);
      fixture.items.set(side.id, [sessionItem(side.id, 2, 'Side reply')]);
      return sessionReply(fixtureCreation(side));
    }
    return undefined;
  } });
  try {
    fixture.sessions.push(fixtureSession('earlier', 'daily', { sourceSessionId: parent }),
      fixtureSession('archived', 'daily', { sourceSessionId: parent, updatedAtMs: 20, archivedAtMs: 20 }));
    fixture.items.set(parent, [sessionItem(parent, 1, 'Original discussion')]);
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText('Original discussion');
    const field = await screen.findByRole('combobox', { name: 'Message' });
    await userEvent.type(field, '/Side Explore this{Enter}');
    await waitFor(() => expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?scope=managed')).toBe(true));
    const writes = () => fixture.requests.filter(request => request.method === 'POST');
    await waitFor(() => expect(writes().map(request => [request.path, request.body])).toEqual([['/api/v1/tracks/daily/sessions', {
      title: 'Explore this', prompt: 'Explore this', sourceExcerpt: 'Assistant: Original discussion',
      provider: 'codex', sourceSessionId: parent,
    }]]));
    await screen.findByText('Side reply');
    await userEvent.click(screen.getByRole('button', { name: 'Close side conversation' }));
    await userEvent.type(field, '/side ');
    await userEvent.keyboard('{Enter}');
    await screen.findByText('Side reply');
    expect(writes()).toHaveLength(1);
  } finally { cleanup(); fixture.client.clear(); }
});

it('starts an ordinary conversation from Conversations + after closing a side draft', async () => {
  const ordinary = fixtureSession('new-id', 'daily', { title: 'Fresh topic' });
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.method === 'POST' && request.path === '/api/v1/tracks/daily/sessions') {
      fixture.sessions.push(ordinary);
      fixture.items.set(ordinary.id, [sessionItem(ordinary.id, 2, 'Fresh reply')]);
      return sessionReply(fixtureCreation(ordinary));
    }
    return undefined;
  } });
  try {
    fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Original discussion')]);
    await userEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
    await screen.findByText('Original discussion');
    await userEvent.type(screen.getByRole('combobox', { name: 'Message' }), '/side {Enter}');
    await userEvent.click(await screen.findByRole('button', { name: 'Close side conversation' }));
    await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    await userEvent.click(screen.getByRole('button', { name: 'New conversation' }));
    await userEvent.type(await screen.findByRole('combobox', { name: 'Message' }), 'Fresh topic{Enter}');
    await screen.findByText('Fresh reply');
    expect(fixture.requests.filter(request => request.method === 'POST').map(request => request.body))
      .toEqual([{ title: 'Fresh topic', prompt: 'Fresh topic', provider: 'codex' }]);
  } finally { cleanup(); fixture.client.clear(); }
});
