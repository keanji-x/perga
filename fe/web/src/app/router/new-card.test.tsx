import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
// @vitest-environment jsdom

import { onlineManager,QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory,RouterProvider } from '@tanstack/react-router';
import { act,cleanup,render,screen,waitFor,within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,beforeEach,describe,expect,it,vi } from 'vitest';

import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';

import type { TerminalDto } from '../../../../core/api/perga-v1.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
const AREA = { id: 'c1', name: 'Work', color: '#000', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Test track', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });

function ok(body: unknown): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body };
}

/** How the fake server answers one create, by its 0-based place among the creates: `created` mints a card. */
type CreateAnswer = 'created' | 'lost' | ApiTransportResponse;

function setup({ createFails = false, deferCreate = false, answers = [] as readonly CreateAnswer[] } = {}) {
  const requests: ApiRequest[] = [];
  const cards: TerminalDto[] = [];
  const byKey = new Map<string, { body: string; card: TerminalDto }>();
  /* One entry per POST, in send order, so a test can release an older attempt before a newer one. */
  const releases: (() => void)[] = [];
  const transport: ApiTransportPort = {
    send(request) {
      requests.push(request);
      if (request.path === '/api/v1/areas') return Promise.resolve(ok([AREA]));
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return Promise.resolve(ok([TRACK]));
      if (request.path === '/api/v1/tracks/w1/terminals' && request.method === 'GET') return Promise.resolve(ok([...cards]));
      if (request.path === '/api/v1/tracks/w1') {
        return Promise.resolve(ok(fixtureSnapshot(TRACK, [])));
      }
      if (request.path === '/api/v1/tracks/w1/report') return Promise.resolve(ok({ taskDiagnostics: [] }));
      if (request.path.startsWith('/api/fs/listdir')) {
        return Promise.resolve(ok({
          path: '/repo', parent: '/', entries: [{ name: 'notes.md', is_dir: false }],
        }));
      }
      if (request.method === 'POST' && (request.path.startsWith('/api/tracks/w1/') || request.path.startsWith('/api/v1/tracks/w1/'))) {
        const answer = answers[requests.filter((sent) => sent.method === 'POST').length - 1] ?? 'created';
        /* Answered before anything was made. */
        if (answer !== 'created' && answer !== 'lost') return Promise.resolve(answer);
        if (createFails) {
          return Promise.resolve({
            status: 500, statusText: 'Server Error', body: { error: 'the kernel refused this card' },
          });
        }
        /* The kernel's keyed create: a retry under a stored key joins its card, a different body under it is refused. */
        const key = request.headers?.['Idempotency-Key'];
        const stored = key === undefined ? undefined : byKey.get(key);
        if (stored !== undefined && stored.body !== JSON.stringify(request.body)) {
          return Promise.resolve({ status: 409, statusText: 'Conflict', body: {
            error: 'This card request was already used for a different card.', code: 'idempotency_key_reused',
          } });
        }
        const created: TerminalDto = stored?.card ?? {
          id: `card-${cards.length + 1}`, trackId: 'w1', title: '', cwd: '/tmp', status: 'running', createdAtMs: 1,
        };
        if (stored === undefined) {
          cards.push(created);
          if (key !== undefined) byKey.set(key, { body: JSON.stringify(request.body), card: created });
        }
        /* Made, but the answer never arrived. */
        if (answer === 'lost') return Promise.reject(new Error('socket hang up'));
        if (!deferCreate) return Promise.resolve(ok(created));
        return new Promise<ApiTransportResponse>((resolve) => {
          releases.push(() => { resolve(ok(created)); });
        });
      }
      return Promise.resolve(ok([]));
    },
  };
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport: withReadReceipts(transport), unauthorized, client, cards: bootTestCardRuntime(),
  });
  router.update({ history: createMemoryHistory({ initialEntries: ['/track/w1'] }) });
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return {
    requests, router, releases, cards,
    posts: () => requests.filter((request) => request.method === 'POST'),
  };
}

async function pickKind(label: string) {
  const hide = screen.queryByRole('button', { name: 'Hide terminal' });
  if (hide !== null) await userEvent.click(hide);
  const trigger = await screen.findByRole('button', { name: 'Tools' });
  /* The popover's close from the previous cycle can land after the next click and
       read as a close, so the click is retried until the menu is actually showing. */
  await waitFor(async () => {
    if (screen.queryByRole('menuitem', { name: label }) !== null) return;
    await userEvent.click(trigger);
    expect(screen.queryByRole('menuitem', { name: label })).not.toBeNull();
  });
  await userEvent.click(screen.getByRole('menuitem', { name: label }));
}

beforeEach(() => {
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  onlineManager.setOnline(true);
});

describe('adding a card from the CARDS module', () => {

  it('says so on screen when a fieldless kind fails to create', async () => {
    setup({ createFails: true });
    await pickKind('terminal');
    const alert = await screen.findByRole('alert');
    /* A 500 may follow a card that was made: the fixed unknown state, not the server's words (#2131). */
    expect(within(alert).getByText('Creating the terminal card is unconfirmed.')).toBeTruthy();
    expect(within(alert).getByRole('button', { name: 'Try again' })).toBeTruthy();
    // No dialog was opened for this kind, so the message cannot have come from `NewCardForm`.
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('does not navigate when a create lands after the reader left the track', async () => {
    const { router, releases, posts } = setup({ deferCreate: true });
    await pickKind('terminal');
    await waitFor(() => { expect(posts()).toHaveLength(1); });
    expect(releases).toHaveLength(1);

    await act(async () => { await router.navigate({ to: '/' }); });
    await waitFor(() => { expect(router.state.location.pathname).toBe('/'); });

    await act(async () => {
      releases[0]?.();
      await new Promise((done) => { setTimeout(done, 0); });
    });

    expect(router.state.location.pathname).toBe('/');
    expect(router.state.location.searchStr).not.toContain('card=');
  });
});

/* #2131 S2: one add-card intent is one `Idempotency-Key`. The fake server joins a retry under a stored key to its card,
 * so "one card" below is the kernel's answer to two attempts under one key, not a count of POSTs. */
describe('a keyed card create', () => {
  const keyOf = (request: ApiRequest | undefined) => request?.headers?.['Idempotency-Key'];

  it('resends a terminal create whose answer was lost under the same key and body, and makes one card', async () => {
    const { posts, cards } = setup({ answers: ['lost'] });
    await pickKind('terminal');
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Creating the terminal card is unconfirmed.');
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    await waitFor(() => { expect(screen.queryByRole('alert')).toBeNull(); });
    const [first, second] = posts();
    expect(posts()).toHaveLength(2);
    expect(first?.path).toBe('/api/v1/tracks/w1/terminals');
    expect(keyOf(first)).toMatch(/^[0-9a-f-]{36}$/);
    expect(keyOf(second)).toBe(keyOf(first));
    expect(second?.body).toEqual(first?.body);
    expect(cards).toHaveLength(1);
  });

  it('retains the Workbench held key and body when terminal is picked again', async () => {
    const { posts, cards } = setup({ answers: ['lost'] });
    await pickKind('terminal');
    await screen.findByRole('button', { name: 'Try again' });
    await pickKind('terminal');
    await waitFor(() => { expect(posts()).toHaveLength(2); });
    expect(keyOf(posts()[1])).toBe(keyOf(posts()[0]));
    expect(posts()[1]?.body).toEqual(posts()[0]?.body);
    expect(cards).toHaveLength(1);
  });

  /* Offline, a press is refused before anything is sent (`NotSentError`). */
  it('drops a fresh intent that could not be sent: a refusal with no Try again', async () => {
    const { posts } = setup();
    await screen.findByRole('button', { name: 'Tools' });
    onlineManager.setOnline(false);
    await pickKind('terminal');
    expect(within(await screen.findByRole('alert')).getByText('The terminal card was not created.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
    expect(posts()).toHaveLength(0);
  });

  it('keeps an unknown intent whose Try again could not be sent, and resends its key once back online', async () => {
    const { posts, cards } = setup({ answers: ['lost'] });
    await pickKind('terminal');
    await screen.findByRole('button', { name: 'Try again' });
    onlineManager.setOnline(false);
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    const alert = await screen.findByRole('alert');
    await waitFor(() => { expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeNull(); });
    expect(alert.textContent).toContain('Creating the terminal card is unconfirmed.');
    expect(posts()).toHaveLength(1);
    onlineManager.setOnline(true);
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() => { expect(posts()).toHaveLength(2); });
    expect(keyOf(posts()[1])).toBe(keyOf(posts()[0]));
    expect(cards).toHaveLength(1);
  });
});
