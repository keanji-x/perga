import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionInput,sessionPage } from './conversation-session-data.ts';
import { fixtureBootstrap,fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
// @vitest-environment jsdom
// A track that starts with no name and no words in it: creating lands in the planner
// conversation with the caret in it, and clearing the title is a request, not a cancel.

import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { act,cleanup,render,screen,waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,beforeEach,describe,expect,it,vi } from 'vitest';

import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });

const AREA = { id: 'c1', name: 'Work', color: '#000', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
/* `title: ''` is what the kernel stores when the POST omits the key. */
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: '', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
/* A named track, so the PATCH case has something to clear. */
const NAMED_TRACK = { ...TRACK, title: 'Test track' };
/* The track a *second* create answers with, so the intent can be stated while the
   reader is standing on some other track, which is still mounted. */
const OTHER_TRACK = { ...TRACK, id: 'w2', sortOrder: 2, createdAtMs: 3, updatedAtMs: 3 };
const PLANNER_SESSION = fixtureSession('card-planner', 'w1', { title: 'Planner chat', isLead: true });
const OTHER_PLANNER_SESSION = { ...PLANNER_SESSION, id: 'card-planner-w2', trackId: 'w2' };

function ok(body: unknown): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body };
}

type Options = {
  track?: typeof TRACK;
  sessions?: readonly ReturnType<typeof fixtureSession>[];
  /** What `POST /api/tracks` answers with. Defaults to the track already listed,
   *  which is the single-track shape most of these cases want. */
  created?: typeof TRACK;
  createdSessions?: readonly ReturnType<typeof fixtureSession>[];
  /** Start with the created track's detail failing, so the route body never mounts;
   *  flipped back through the returned `gate`. */
  createdDetailFails?: boolean;
};

function setup(options: Options = {}) {
  const track = options.track ?? TRACK;
  const sessions = options.sessions ?? [PLANNER_SESSION];
  const created = options.created ?? track;
  const createdSessions = options.createdSessions ?? sessions;
  const gate = { createdDetailFails: options.createdDetailFails ?? false };
  const details = new Map<string, { track: typeof TRACK; sessions: readonly ReturnType<typeof fixtureSession>[] }>([
    [track.id, { track, sessions }],
    [created.id, { track: created, sessions: createdSessions }],
  ]);
  const requests: ApiRequest[] = [];
  const values = new Map<string, string>();
  const transport: ApiTransportPort = {
    send(request) {
      if (request.path.includes('/workspace-options')) return Promise.resolve({ status: 200, statusText: 'OK', body: { defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] } });
      requests.push(request);
      if (request.path === '/api/v1/areas') return Promise.resolve(ok([AREA]));
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') {
        return Promise.resolve(ok(created.id === track.id ? [track] : [track, created]));
      }
      if (request.path === '/api/overlays?entity_kind=track') return Promise.resolve(ok([]));
      if (request.path === '/api/v1/templates') return Promise.resolve(ok([]));
      if (request.method === 'POST' && request.path === '/api/v1/tracks') return Promise.resolve(ok(fixtureSnapshot(created, [...createdSessions])));
      const patched = request.method === 'PATCH' ? details.get(request.path.slice('/api/v1/tracks/'.length)) : undefined;
      if (patched !== undefined) {
        return Promise.resolve(ok({ ...patched.track, ...(request.body as object) }));
      }
      if (request.path.endsWith('/sessions')) return Promise.resolve(ok([]));
      const detail = details.get(request.path.slice('/api/v1/tracks/'.length));
      if (detail !== undefined) {
        if (detail.track.id === created.id && created.id !== track.id && gate.createdDetailFails) {
          return Promise.resolve({ status: 500, statusText: 'Server Error', body: {} });
        }
        return Promise.resolve(ok(fixtureSnapshot(detail.track, [...detail.sessions])));
      }
      if (request.path.startsWith('/api/v1/bootstrap')) return Promise.resolve(ok(fixtureBootstrap()));
      if (request.method === 'POST' && request.path.endsWith('/messages')) {
        return Promise.resolve(ok(sessionInput(conversationCardId(request.path))));
      }
      if (request.path.includes('/items?')) return Promise.resolve(ok(sessionPage(fixtureSession(conversationCardId(request.path), 'w1'))));
      return Promise.resolve(ok([]));
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport, unauthorized, client, cards: bootTestCardRuntime(),
  });
  render(
    <QueryClientProvider client={client}>
      <ThemeProvider storage={{
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => { values.set(key, value); },
      }}
      >
        <RouterProvider router={router} />
      </ThemeProvider>
    </QueryClientProvider>,
  );
  return {
    requests,
    router,
    gate,
    /** Change what the next read of a track's detail answers with. */
    setSessionsOf: (trackId: string, next: readonly ReturnType<typeof fixtureSession>[]) => {
      const detail = details.get(trackId);
      if (detail !== undefined) details.set(trackId, { track: detail.track, sessions: next });
    },
    client,
  };
}

/* `combobox` and not `textbox`: the track route passes `onNewConversation`, which
   arms the `/` trigger menu and turns Astryx's editable into a combobox. */
function messageField(): HTMLElement {
  return screen.getByRole('combobox', { name: 'Message' });
}

/* The composer page, with the one thing it asks for typed in; Create stays
   disabled until the field says something. */
async function composerOnScreen() {
  await userEvent.type(await screen.findByLabelText('What this track should do'), 'Read it');
}

async function createATrack() {
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  await composerOnScreen();
  await userEvent.click(await screen.findByRole('button', { name: 'Create track' }));
}

/** The same create, started from the rail, which is reachable from every route. */
async function createATrackFromTheRail() {
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  await composerOnScreen();
  await userEvent.click(await screen.findByRole('button', { name: 'Create track' }));
}

async function goToTrack(router: ReturnType<typeof setup>['router'], trackId: string) {
  await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId } }); });
}

beforeEach(() => {
  window.history.pushState({}, '', `/`);
  /* The drawer, the composer and `EditableTitle` all move focus inside a frame;
       running frames synchronously is what makes focus answerable here. */
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('creating a track lands in its planner conversation', () => {
  it('opens the planner conversation with the caret in the composer', async () => {
    setup();
    await createATrack();
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => { expect(document.activeElement).toBe(messageField()); });
  });

  /* The rail's per-area `+` is rendered above the route outlet, so the track being
   * left is still mounted when the intent is stated. */
  it('opens the planner conversation of the new track when the create started on another track', async () => {
    const { router } = setup({ created: OTHER_TRACK, createdSessions: [OTHER_PLANNER_SESSION] });
    await goToTrack(router, 'w1');
    await screen.findByRole('button', { name: 'Rename track' });

    await createATrackFromTheRail();

    await waitFor(() => { expect(router.state.location.pathname.endsWith('/track/w2')).toBe(true); });
    /* The drawer lives in `TrackRouteBody`, keyed by track, so a drawer open on this
           page is this track's own. */
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => { expect(document.activeElement).toBe(messageField()); });
  });

  /* The detail read fails, so the body that would redeem the intent never mounts;
   * a later visit is a new history entry and the mark is not on it. */
  it('does not open on a later visit when the landing never reached the track', async () => {
    const { router, gate } = setup({
      created: OTHER_TRACK, createdSessions: [OTHER_PLANNER_SESSION], createdDetailFails: true,
    });
    await createATrack();
    await screen.findByRole('button', { name: 'Retry' });

    await act(async () => { await router.navigate({ to: '/' }); });
    expect(router.state.location.pathname).toBe('/');

    gate.createdDetailFails = false;
    await goToTrack(router, 'w2');
    await screen.findByRole('button', { name: 'Rename track' });
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
  });

  /* Back returns to the SAME history entry the create marked, and its mark was
   * never redeemed, so this time the conversation opens. Chosen semantics: the
   * mark belongs to the entry and expires only by being redeemed. */
  it('opens the conversation when Back returns to the entry whose landing had failed', async () => {
    const { router, gate } = setup({
      created: OTHER_TRACK, createdSessions: [OTHER_PLANNER_SESSION], createdDetailFails: true,
    });
    await createATrack();
    await screen.findByRole('button', { name: 'Retry' });

    /* A push, so the failed entry stays underneath rather than being replaced. */
    await act(async () => { await router.navigate({ to: '/' }); });
    expect(router.state.location.pathname).toBe('/');

    gate.createdDetailFails = false;
    await act(async () => {
      router.history.back();
      await new Promise((resolve) => { setTimeout(resolve, 0); });
    });

    await waitFor(() => { expect(router.state.location.pathname.endsWith('/track/w2')).toBe(true); });
    await screen.findByRole('complementary', { name: 'Planner chat' });
  });

  /* One-shot: the intent is cleared as it is redeemed, so walking back into the
   * same track later is an ordinary visit. */
  it('does not re-open an explicitly closed conversation on a later visit to the same track', async () => {
    const { router } = setup();
    await createATrack();
    await screen.findByRole('complementary', { name: 'Planner chat' });

    // Navigation remembers open drawers; an explicit close must still win on a later visit.
    await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    await act(async () => { await router.navigate({ to: '/' }); });
    expect(router.state.location.pathname).toBe('/');
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();

    await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w1' } }); });
    await screen.findByRole('button', { name: 'Rename track' });
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
  });

  /* "Nothing opened" alone is true of a page with no planner card whatever the
   * mark does: the card arrives on a later read of the same entry, and the drawer
   * must still be shut. */
  it('opens nothing, and arms nothing, when the track has no planner card', async () => {
    const { client, setSessionsOf } = setup({ sessions: [] });
    await createATrack();
    await screen.findByRole('button', { name: 'Rename track' });
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();

    setSessionsOf('w1', [PLANNER_SESSION]);
    await act(async () => { await client.invalidateQueries(); });
    /* The row for the planner card is proof the second read landed, and the only
           place `Planner chat` may appear. */
    await screen.findByText('Planner chat');
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
  });
});

describe('clearing the track title', () => {
  /* `emptyCommit="clear"` on the track header: the empty title is the one state
   * `neige_track_rename` will fill in, and the primitive swallows the keystroke by default. */
  it('PATCHes an empty title when the box is emptied and committed', async () => {
    const { requests, router } = setup({ track: NAMED_TRACK });
    await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w1' } }); });
    await userEvent.click(await screen.findByRole('button', { name: 'Rename track' }));
    await userEvent.clear(screen.getByRole('textbox', { name: 'Track title' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Track title' }), '{Enter}');

    await waitFor(() => {
      expect(requests.filter((request) => request.method === 'PATCH' && request.path === '/api/v1/tracks/w1'))
        .toEqual([expect.objectContaining({ body: { title: '' } })]);
    });
  });
});
