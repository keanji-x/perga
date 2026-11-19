import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { setupWorkbench, terminalCard } from './terminal-workbench-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); vi.restoreAllMocks(); });

it('keeps aggregate sessions out of the tools inventory', async () => {
  setupWorkbench({ cards: [terminalCard('shell', { source: 'manual', in_workbench: true })] });
  const workbench = await screen.findByRole('region', { name: 'Workbench' });
  expect(await within(workbench).findByRole('button', { name: /^shell/ })).toBeTruthy();
  expect(within(workbench).queryByRole('button', { name: /Main discussion/ })).toBeNull();
  expect(await screen.findByRole('button', { name: 'Conversation Main discussion' })).toBeTruthy();
});

it('opens an inventory terminal in the workbench and hides it without removing it', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('shell', { source: 'manual', in_workbench: true })] });
  await userEvent.click(await screen.findByRole('button', { name: /^shell/ }));
  await screen.findByText('No terminal session available.');
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('shell');
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  expect(screen.getByRole('button', { name: /^shell/ })).toBeTruthy();
  expect(fixture.requests.filter(request => request.method === 'DELETE')).toEqual([]);
});

it.each(['shell', 'missing'])('retains ?card=%s until the tools read succeeds', async card => {
  let release!: () => void;
  const toolsRead = new Promise<void>(resolve => { release = resolve; });
  const fixture = setupWorkbench({ initialCard: card, toolsRead,
    cards: [terminalCard('shell', { source: 'manual', in_workbench: true })] });
  await screen.findByRole('region', { name: 'Workbench' });
  await waitFor(() => expect(fixture.requests.some(request => request.path === '/api/v1/tracks/workbench/terminals' && request.method === 'GET')).toBe(true));
  expect(fixture.router.state.location.search).toHaveProperty('card', card);
  await act(async () => { release(); await toolsRead; });
  if (card === 'shell') {
    await screen.findByRole('button', { name: 'Hide terminal' });
    expect(fixture.router.state.location.search).toHaveProperty('card', card);
  } else await waitFor(() => expect(fixture.router.state.location.search).not.toHaveProperty('card'));
});

it('retains the requested card when the tools read fails', async () => {
  let refuse!: (error: Error) => void;
  const toolsRead = new Promise<void>((_resolve, reject) => { refuse = reject; });
  const fixture = setupWorkbench({ initialCard: 'shell', toolsRead });
  await screen.findByRole('region', { name: 'Workbench' });
  await act(async () => { refuse(new Error('Tools unavailable')); await toolsRead.catch(() => undefined); });
  await waitFor(() => expect(fixture.client.getQueryState(['track', 'workbench', 'tools'])?.status).toBe('error'));
  expect(fixture.router.state.location.search).toHaveProperty('card', 'shell');
});

it('shows the tools empty state while the aggregate still has a conversation', async () => {
  setupWorkbench();
  expect((await screen.findByRole('region', { name: 'Workbench' })).textContent).toContain('还没有工具');
  expect(await screen.findByRole('button', { name: 'Conversation Main discussion' })).toBeTruthy();
});

it('preserves the tools order and selects an existing terminal without creating one', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', in_workbench: true }), terminalCard('second', { source: 'manual', in_workbench: true })] });
  const workbench = await screen.findByRole('region', { name: 'Workbench' });
  await within(workbench).findByRole('button', { name: /^second/ });
  expect(within(workbench).getAllByRole('button').filter(button => /^(first|second)/u.test(button.textContent ?? '')).map(button => button.textContent))
    .toEqual(['firstterminal', 'secondterminal']);
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'workbench' }, search: { card: 'second' } }); });
  await screen.findByRole('button', { name: 'Hide terminal' });
  expect(fixture.posts()).toEqual([]);
});
