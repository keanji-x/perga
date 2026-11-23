import { act,cleanup,fireEvent,screen,waitFor,within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,expect,it,vi } from 'vitest';
import { queryKeys } from '../providers/query-keys.ts';
import { openTerminalCommand,setupWorkbench,terminalCard,terminalCommandInput } from './terminal-workbench-fixture.tsx';

const { mountedHost } = vi.hoisted(() => ({ mountedHost: vi.fn() }));
vi.mock('../../systems/cards/public.js', async (load) => {
  const actual = await load<typeof import('../../systems/cards/public.js')>();
  return { ...actual, createCardHost: (...args: Parameters<typeof actual.createCardHost>) => {
    const host = actual.createCardHost(...args);
    return { ...host, mount: (...mountArgs: Parameters<typeof host.mount>) => {
      mountedHost();
      return host.mount(...mountArgs);
    } };
  } };
});

afterEach(() => { mountedHost.mockClear(); cleanup(); document.getElementById('root')?.remove(); vi.restoreAllMocks(); });

it.each(['main', 'draft'] as const)('dispatches /terminal from the %s pane to the latest committed card list and pending intent', async pane => {
  const fixture = setupWorkbench({ defer: true, failure: 'definite' });
  await userEvent.click(await screen.findByRole('button', { name: pane === 'draft' ? /^New conversation$/u : 'Conversation Main discussion' }));
  const command = async (input: HTMLElement, text: string) => {
    await waitFor(() => expect(input.getAttribute('contenteditable')).toBe('true'));
    input.textContent = text; fireEvent.input(input);
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
  };
  const input = await screen.findByRole('combobox', { name: 'Message' });
  if (pane === 'main') {
    fixture.savedReply.text = 'Saved before terminal intent';
    await act(async () => { await fixture.client.refetchQueries({ queryKey: queryKeys.plannerRun('parent') }); });
    await act(async () => { await fixture.client.refetchQueries({ queryKey: queryKeys.harnessItems('parent') }); });
    await screen.findByText('Saved before terminal intent');
  }
  // The composer was already committed with an empty card list. A new list
  // must change the event implementation without freezing that initial intent.
  fixture.cards.push(terminalCard('arrived', { source: 'manual', inWorkbench: true }));
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackTools('workbench') }); });
  // Query invalidation resolves before its observer notification commits.
  // Wait for the real inventory projection, independently of composer typing.
  await screen.findByRole('button', { name: /^arrived/ });
  await command(input, '/terminal');
  await screen.findByRole('button', { name: 'Hide terminal' });
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('arrived');
  expect(fixture.posts()).toHaveLength(0);
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  fixture.cards.length = 0;
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackTools('workbench') }); });
  await waitFor(() => expect(screen.queryByRole('button', { name: /^arrived/ })).toBeNull());
  await command(input, '/terminal'); await command(input, '/terminal');
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  await screen.findByText('Terminal refused');
  await command(input, '/terminal');
  await waitFor(() => expect(fixture.posts()).toHaveLength(2));
  expect(fixture.posts()[1]?.headers?.['Idempotency-Key']).not.toBe(fixture.posts()[0]?.headers?.['Idempotency-Key']);
  await act(async () => { fixture.releases[1]?.(); await Promise.resolve(); });
  await screen.findByText('Connecting…');
});

it('deduplicates pending commands and retains the host when hidden', async () => {
  const fixture = setupWorkbench({ defer: true });
  const mount = mountedHost;
  const entry = await terminalCommandInput();
  expect(screen.getByRole('region', { name: 'Workbench' }).textContent).toContain('No tools yet');
  expect(screen.getByRole('region', { name: 'Conversations' }).contains(entry)).toBe(false);
  await openTerminalCommand(); await openTerminalCommand();
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  await screen.findByText('Connecting…');
  expect(mount).toHaveBeenCalledTimes(1);
  const hide = screen.getByRole('button', { name: 'Hide terminal' });
  expect(hide.textContent).toBe('');
  expect(hide.querySelector('svg')?.classList.contains('lucide-chevron-right')).toBe(true);
  const dock = document.querySelector('[data-nc-card-dock]');
  const resource = document.querySelector('[data-nc-terminal-card]');
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  expect(document.querySelector('[data-nc-card-dock]')).toBe(dock);
  expect(document.querySelector('[data-nc-terminal-card]')).toBe(resource);
  expect(mount).toHaveBeenCalledTimes(1);
  await openTerminalCommand();
  expect(document.querySelector('[data-nc-card-dock]')).toBe(dock);
  expect(document.querySelector('[data-nc-terminal-card]')).toBe(resource);
  expect(mount).toHaveBeenCalledTimes(1);
  expect(fixture.posts()).toHaveLength(1);
  expect(fixture.requests.some(request => request.method === 'DELETE')).toBe(false);
});

it('retries an unknown response with the original key and first body despite a theme change', async () => {
  const fixture = setupWorkbench({ lost: true });
  await openTerminalCommand();
  await screen.findByRole('alert');
  document.documentElement.dataset.theme = 'light';
  await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
  await screen.findByText('Connecting…');
  const posts = fixture.posts();
  expect(posts).toHaveLength(2);
  expect(posts[0]?.headers?.['Idempotency-Key']).toBeTruthy();
  expect(posts[1]?.headers).toEqual(posts[0]?.headers);
  expect(posts[1]?.body).toEqual(posts[0]?.body);
  expect((posts[0]?.body as { cwd?: unknown }).cwd).toBeUndefined();
});

it('accepts /terminal in an empty draft and refuses arguments without sending', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('existing', { source: 'manual', inWorkbench: true })] });
  await userEvent.click(await screen.findByRole('button', { name: /^New conversation$/u }));
  const input = await screen.findByRole('combobox', { name: 'Message' });
  // Astryx uses a contenteditable editor, so use its native input boundary.
  input.textContent = '/terminal echo unsafe'; fireEvent.input(input);
  fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
  await screen.findByText('Use /terminal without arguments. Shell commands are entered in the terminal.');
  await waitFor(() => expect(input.textContent).toContain('/terminal echo unsafe'));
  input.textContent = '/terminal'; fireEvent.input(input); fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
  await screen.findByRole('button', { name: 'Hide terminal' });
  expect(fixture.posts()).toHaveLength(0);
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('existing');
});

it('switches existing ordinary terminals without a board double mount or implicit replacement', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true }), terminalCard('second', { source: 'manual', inWorkbench: true })] });
  const mount = mountedHost;
  await openTerminalCommand();
  await userEvent.click(within(screen.getByRole('region', { name: 'Workbench' })).getByRole('button', { name: /^second/ }));
  expect(mount).toHaveBeenCalledTimes(2);
  expect(document.querySelectorAll('[data-nc-card-dock]')).toHaveLength(2);
  expect(document.querySelector('[data-nc-card-cell][data-nc-card-id="second"]')).toBeNull();
  await userEvent.click(within(document.querySelector('[data-nc-card-dock]:not([hidden])') as HTMLElement).getByRole('button', { name: /^Delete card / }));
  expect(fixture.requests.filter(request => request.method === 'DELETE')).toHaveLength(0);
  expect(screen.getByRole('dialog', { name: 'Delete this card?' })).toBeTruthy();
  expect(fixture.posts()).toHaveLength(0);
});

it('does not reopen a hidden view or steal changed focus on late completion', async () => {
  const fixture = setupWorkbench({ defer: true });
  const entry = await terminalCommandInput();
  await openTerminalCommand();
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  entry.focus();
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  expect(document.activeElement).toBe(entry);
  expect(screen.queryByRole('button', { name: 'Hide terminal' })).toBeNull();
  expect(fixture.router.state.location.search).not.toHaveProperty('card');
});

it('creates within the resolved daily Track rather than a global or guessed scope', async () => {
  const fixture = setupWorkbench({ daily: true });
  await openTerminalCommand();
  await screen.findByText('Connecting…');
  expect(fixture.posts()[0]?.path).toBe('/api/v1/tracks/workbench/terminals');
});

it('leaves a different Track view and its focus untouched when creation completes after navigation', async () => {
  const fixture = setupWorkbench({ defer: true });
  await userEvent.click(await screen.findByRole('button', { name: /^New conversation$/u }));
  const input = await screen.findByRole('combobox', { name: 'Message' });
  input.textContent = '/terminal'; fireEvent.input(input); fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' });
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'other' } }); });
  await screen.findAllByText('Other track');
  const destination = await terminalCommandInput();
  destination.focus();
  const location = fixture.router.state.location.href;
  const active = document.activeElement;
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  expect(fixture.router.state.location.href).toBe(location);
  expect(document.activeElement).toBe(active);
  expect(screen.queryByRole('button', { name: 'Hide terminal' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: /^New conversation$/u }));
  const current = await screen.findByRole('combobox', { name: 'Message' });
  current.textContent = '/terminal'; fireEvent.input(current); fireEvent.keyDown(current, { key: 'Enter', code: 'Enter' });
  await screen.findByText('Connecting…');
  expect(fixture.posts().map(post => post.path)).toEqual(['/api/v1/tracks/workbench/terminals', '/api/v1/tracks/other/terminals']);
});

it('routes an ordinary card URL into the same dock and keeps it out of the board', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true }), terminalCard('second', { source: 'manual', inWorkbench: true })] });
  await terminalCommandInput();
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'workbench' }, search: { card: 'second' } }); });
  await screen.findByRole('button', { name: 'Hide terminal' });
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('second');
  expect(document.querySelector('[data-nc-card-cell][data-nc-card-id="second"]')).toBeNull();
  expect(fixture.posts()).toHaveLength(0);
  expect(fixture.router.state.location.search).toHaveProperty('card', 'second');
});

it('creates an extra terminal only on explicit New, and never overrides a newer selection', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true })], defer: true });
  await openTerminalCommand();
  await userEvent.click(screen.getByRole('button', { name: 'Tools' }));
  await userEvent.click(screen.getByRole('menuitem', { name: 'terminal' }));
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await userEvent.click(within(screen.getByRole('region', { name: 'Workbench' })).getByRole('button', { name: /^first/ }));
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('first');
  expect(fixture.cards).toHaveLength(2);
  expect(fixture.posts()).toHaveLength(1);
});

it('deletes only after confirmation, removes the inventory row and selects an existing survivor', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true }), terminalCard('second', { source: 'manual', inWorkbench: true })] });
  await openTerminalCommand();
  await userEvent.click(within(screen.getByRole('region', { name: 'Workbench' })).getByRole('button', { name: /^second/ }));
  await userEvent.click(within(document.querySelector('[data-nc-card-dock]:not([hidden])') as HTMLElement).getByRole('button', { name: /^Delete card / }));
  const dialog = screen.getByRole('dialog', { name: 'Delete this card?' });
  await userEvent.click((await import('@testing-library/react')).within(dialog).getByRole('button', { name: 'Delete card' }));
  await waitFor(() => expect(within(screen.getByRole('region', { name: 'Workbench' })).queryByRole('button', { name: /^second/ })).toBeNull());
  expect(fixture.requests.filter(request => request.method === 'DELETE').map(request => request.path)).toEqual(['/api/v1/tracks/workbench/terminals/second']);
  expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('first');
  expect(fixture.posts()).toHaveLength(0);
});

it('rejoins a pending intent after leaving and returning to the Track', async () => {
  const fixture = setupWorkbench({ defer: true });
  await openTerminalCommand();
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'other' } }); });
  await screen.findAllByText('Other track');
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'workbench' } }); });
  await openTerminalCommand();
  expect(fixture.posts()).toHaveLength(1);
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  await screen.findByText('Connecting…');
  expect(fixture.posts()).toHaveLength(1);
});

it('keeps the first unknown request across route reentry even when the list now contains its card', async () => {
  const fixture = setupWorkbench({ lost: true });
  await openTerminalCommand();
  await screen.findByRole('alert');
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'other' } }); });
  await screen.findAllByText('Other track');
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'workbench' } }); });
  await openTerminalCommand();
  await screen.findByText('Connecting…');
  expect(fixture.posts()).toHaveLength(2);
  expect(fixture.posts()[1]?.headers).toEqual(fixture.posts()[0]?.headers);
  expect(fixture.posts()[1]?.body).toEqual(fixture.posts()[0]?.body);
});


it.each(['success', 'definite', 'unknown'] as const)('settles %s in a returned owner before reveal without another subscription', async outcome => {
  const fixture = setupWorkbench({ defer: true, failure: outcome === 'success' ? null : outcome });
  await openTerminalCommand();
  await waitFor(() => expect(fixture.posts()).toHaveLength(1));
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'other' } }); });
  await screen.findAllByText('Other track');
  await act(async () => { await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'workbench' } }); });
  const entry = await terminalCommandInput();
  entry.focus();
  await act(async () => { fixture.releases[0]?.(); await Promise.resolve(); });
  await waitFor(() => expect(screen.queryByText('Creating terminal…')).toBeNull());
  expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Tools' }).disabled).toBe(false);
  expect(document.activeElement).toBe(entry);
  expect(screen.queryByRole('button', { name: 'Hide terminal' })).toBeNull();
  expect(fixture.posts()).toHaveLength(1);
  document.documentElement.dataset.theme = 'light';
  await openTerminalCommand();
  if (outcome === 'success') {
    expect(fixture.posts()).toHaveLength(1);
  } else {
    await waitFor(() => expect(fixture.posts()).toHaveLength(2));
    expect(fixture.posts()[1]?.headers?.['Idempotency-Key'] === fixture.posts()[0]?.headers?.['Idempotency-Key']).toBe(outcome === 'unknown');
    if (outcome === 'unknown') expect(fixture.posts()[1]?.body).toEqual(fixture.posts()[0]?.body);
    await act(async () => { fixture.releases[1]?.(); await Promise.resolve(); });
  }
  await waitFor(() => expect(screen.queryByText('Creating terminal…')).toBeNull());
  expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Tools' }).disabled).toBe(false);
});

it('retains each unvisited deletion fallback lazily through hide and repeated list changes', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true }), terminalCard('second', { source: 'manual', inWorkbench: true }), terminalCard('third', { source: 'manual', inWorkbench: true })] });
  const mount = mountedHost;
  await terminalCommandInput();
  expect(mount).toHaveBeenCalledTimes(0);
  await openTerminalCommand();
  expect(mount).toHaveBeenCalledTimes(1);
  for (const [deleted, survivor, count] of [['first', 'second', 2], ['second', 'third', 3]] as const) {
    await userEvent.click(within(document.querySelector('[data-nc-card-dock]:not([hidden])') as HTMLElement).getByRole('button', { name: /^Delete card / }));
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Delete this card?' })).getByRole('button', { name: 'Delete card' }));
    await waitFor(() => expect(within(screen.getByRole('region', { name: 'Workbench' })).queryByRole('button', { name: new RegExp(`^${deleted}`) })).toBeNull());
    const findDock = () => [...document.querySelectorAll<HTMLElement>('[data-nc-card-dock]')].find(node => node.dataset.ncCardId === survivor) ?? null;
    const dock = findDock();
    expect(dock).not.toBeNull();
    const host = fixture.runtime.host.resolve(survivor);
    await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
    expect(findDock()).toBe(dock);
    expect(fixture.runtime.host.resolve(survivor)).toBe(host);
    await openTerminalCommand();
    expect(mount).toHaveBeenCalledTimes(count);
  }
  expect(fixture.posts()).toHaveLength(0);
});


it('retains visible list-refresh fallbacks without attaching hidden unvisited terminals', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('first', { source: 'manual', inWorkbench: true }), terminalCard('second', { source: 'manual', inWorkbench: true }), terminalCard('third', { source: 'manual', inWorkbench: true })] });
  const mount = mountedHost;
  await terminalCommandInput();
  await openTerminalCommand();
  fixture.cards.splice(0, 1);
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackTools('workbench') }); });
  await waitFor(() => expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('second'));
  const second = document.querySelector('[data-nc-card-dock][data-nc-card-id="second"]');
  expect(second).not.toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  expect(document.querySelector('[data-nc-card-dock][data-nc-card-id="second"]')).toBe(second);
  expect(mount).toHaveBeenCalledTimes(2);
  fixture.cards.splice(0, 1);
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackTools('workbench') }); });
  await waitFor(() => expect(document.querySelector('[data-nc-card-dock][data-nc-card-id="second"]')).toBeNull());
  expect(document.querySelector('[data-nc-card-dock][data-nc-card-id="third"]')).toBeNull();
  expect(mount).toHaveBeenCalledTimes(2);
  await openTerminalCommand();
  const third = document.querySelector('[data-nc-card-dock][data-nc-card-id="third"]');
  expect(third).not.toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
  expect(document.querySelector('[data-nc-card-dock][data-nc-card-id="third"]')).toBe(third);
  expect(mount).toHaveBeenCalledTimes(3);
  expect(fixture.posts()).toHaveLength(0);
});


it('keeps the Track conversation final frame during desktop close', async () => {
  setupWorkbench();
  fireEvent.click(await screen.findByRole('button', { name: /^New conversation$/u }));
  const drawer = await screen.findByRole('complementary', { name: 'Untitled' });
  const heading = within(drawer).getByRole('heading', { name: 'Untitled' });
  expect(drawer.textContent).toContain('Start a conversation');
  fireEvent.click(screen.getByRole('button', { name: /^Close conversation$/u }));
  expect(drawer.hasAttribute('data-nc-drawer')).toBe(true);
  expect(drawer.hidden).toBe(false);
  expect(drawer.textContent).toContain('Start a conversation');
  expect(drawer.contains(heading)).toBe(true);
  expect(heading.textContent).toBe('Untitled');
  expect(drawer.hasAttribute('inert')).toBe(true);
  expect(drawer.getAttribute('aria-hidden')).toBe('true');
  expect(screen.queryByRole('complementary', { name: 'Untitled' })).toBeNull();
  expect(screen.queryByRole('heading', { name: 'Untitled' })).toBeNull();
});


it.each(['running', 'exited'] as const)('cold-opens a %s terminal from its URL while secondary reads are held', async status => {
  let release!: () => void;
  const secondaryReads = new Promise<void>(resolve => { release = resolve; });
  const fixture = setupWorkbench({ initialCard: 'cold', secondaryReads, cards: [{ ...terminalCard('cold', { source: 'manual', inWorkbench: true }),
    presentation: { source: 'manual', inWorkbench: true }, terminal: { id: 'cold', trackId: 'workbench', title: '', cwd: '/tmp', createdAtMs: 1, status },
  }] });
  try {
    await screen.findByText(status === 'exited' ? 'Session exited.' : 'Connecting…');
    expect(fixture.router.state.location.search).toHaveProperty('card', 'cold');
    expect(fixture.requests.some(request => request.path === '/api/v1/tracks/workbench/sessions')).toBe(false);
    await userEvent.click(screen.getByRole('button', { name: 'Hide terminal' }));
    await waitFor(() => expect(fixture.router.state.location.search).not.toHaveProperty('card'));
    await act(async () => { release(); await Promise.resolve(); });
    expect(screen.queryByRole('button', { name: 'Hide terminal' })).toBeNull();
    expect(document.querySelector('[data-nc-terminal-card]')).not.toBeNull();
  } finally { release(); }
});

it('paints one Chat-owned header without a toolbar or letter badge', async () => {
  setupWorkbench({ cards: [terminalCard('shell', { source: 'manual', inWorkbench: true })] });
  await userEvent.click(await screen.findByRole('button', { name: 'Conversation Main discussion' }));
  await openTerminalCommand();
  const workbench = document.querySelector('[data-nc-terminal-workbench]')!;
  const chatHeader = screen.getByRole('complementary', { name: 'Main discussion' }).querySelector('header')!;
  const headers = workbench.querySelectorAll('header');
  expect(headers).toHaveLength(1);
  expect(headers[0].className).toBe(chatHeader.className);
  expect(within(headers[0]).getByRole('heading', { name: 'shell' })).toBeTruthy();
  expect(workbench.querySelector('select')).toBeNull();
  expect([...headers[0].children].map(child => child.tagName)).toEqual(['H2', 'SPAN', 'BUTTON', 'BUTTON']);
  expect(workbench.textContent).not.toContain('New terminal');
  expect(workbench.textContent).not.toContain('Delete card');
  expect(within(headers[0]).getByRole('button', { name: 'Delete card shell' })).toBeTruthy();
  expect(within(headers[0]).getByRole('button', { name: 'Hide terminal' })).toBeTruthy();
});


it('keeps Agent terminals temporary and creates a manual terminal for an unqualified command', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('agent-shell', { source: 'agent', inWorkbench: false })] });
  await screen.findByRole('region', { name: 'Temporary terminals' });
  expect(within(screen.getByRole('region', { name: 'Workbench' })).queryByText('agent-shell')).toBeNull();
  const toggle = within(screen.getByRole('region', { name: 'Temporary terminals' })).getByRole('button', { name: 'Temporary terminals' });
  expect(toggle.getAttribute('aria-expanded')).toBe('false');
  await openTerminalCommand();
  await screen.findByRole('button', { name: 'Hide terminal' });
  expect(fixture.posts()).toHaveLength(1);
  await waitFor(() => expect(document.querySelector('[data-nc-card-dock]:not([hidden])')?.getAttribute('data-nc-card-id')).toBe('terminal-1'));
  expect(fixture.cards.find(card => card.id === 'agent-shell')?.presentation)
    .toEqual({ source: 'agent', inWorkbench: false });
});

it('keeps terminal membership in local preferences without changing immutable creation source', async () => {
  const fixture = setupWorkbench({ cards: [terminalCard('agent-shell', { source: 'agent', inWorkbench: false })] });
  const temporary = await screen.findByRole('region', { name: 'Temporary terminals' });
  await userEvent.click(within(temporary).getByRole('button', { name: 'Temporary terminals' }));
  await userEvent.click(within(temporary).getByRole('button', { name: 'Actions for agent-shell' }));
  await userEvent.click(screen.getByRole('menuitem', { name: 'Add to Workbench' }));
  await waitFor(() => expect(within(screen.getByRole('region', { name: 'Workbench' })).getByRole('button', { name: /^agent-shell/ })).toBeTruthy());
  expect(fixture.cards[0].presentation).toEqual({ source: 'agent', inWorkbench: false });
  expect(fixture.requests.filter(request => request.method === 'PATCH')).toEqual([]);
  const workbench = screen.getByRole('region', { name: 'Workbench' });
  await userEvent.click(within(workbench).getByRole('button', { name: 'Actions for agent-shell' }));
  await userEvent.click(screen.getByRole('menuitem', { name: 'Remove from Workbench' }));
  await waitFor(() => expect(screen.getByRole('region', { name: 'Temporary terminals' })).toBeTruthy());
  expect(fixture.cards[0].presentation).toEqual({ source: 'agent', inWorkbench: false });
  expect(fixture.posts()).toHaveLength(0);
  expect(fixture.requests.some(request => request.method === 'DELETE')).toBe(false);
});
