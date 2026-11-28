import type { SessionItemDto } from '../../../../core/api/perga-v1.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionInput,sessionItem,sessionPage,sessionTurn } from './conversation-session-data.ts';
import { fixtureBootstrap,fixtureReport,fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
// @vitest-environment jsdom
import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { act,cleanup,fireEvent,render,screen,waitFor,within } from '@testing-library/react';
import { afterEach,beforeEach,expect,it,vi } from 'vitest';

import { wireEventSchema } from '../../../../core/api/schemas.ts';
import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { HARNESS_ITEMS_PAGE_LIMIT as TRANSCRIPT_PAGE_LIMIT } from '../../../../core/domain/conversation.ts';
import { invalidationPlanFor } from '../../../../core/events/invalidation-plan.ts';
import { applyEventEffects } from '../events/query-invalidation-adapter.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

/* A send's automatic retries wait no real time here; how long they back off is not under test. */
vi.mock('../../../../core/domain/conversation-delivery.ts', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../../../core/domain/conversation-delivery.ts')>(),
  sendRetryDelay: () => 0,
}));

const AREA = { id: 'c1', name: 'Work', color: '#000', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Test track', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
const CARD = fixtureSession('card-1', TRACK.id, { title: 'Planner chat', isLead: true });
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
const TRACK_B = { ...TRACK, id: 'w2', title: 'Second track', sortOrder: 2 };
const CARD_B = { ...CARD, id: 'card-2', trackId: 'w2', title: 'Second chat' };
const CARD_SAME_TRACK = { ...CARD, id: 'card-other', title: 'Other chat' };
function trackWithReport(track: ReturnType<typeof fixtureTrack>, sessions: ReturnType<typeof fixtureSession>[]) {
  return fixtureSnapshot(track, sessions, fixtureReport('# Project notes'));
}

function ok(body: unknown): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body };
}

function transcriptQueryKey() {
  return queryKeys.harnessItems(CARD.id);
}

function messageRows(count: number): SessionItemDto[] {
  return Array.from({ length: count }, (_, index) => sessionItem(CARD.id, index + 1, `reply ${index}`));
}
function historyPage(card: string, rows: SessionItemDto[]) {
  return sessionPage(fixtureSession(card, card === CARD_B.id ? TRACK_B.id : TRACK.id), rows.map(item => ({ ...item, sessionId: card })));
}
function runningPage() {
  return sessionPage(fixtureSession(CARD.id, TRACK.id, { status: 'running' }), [], sessionTurn(CARD.id));
}

type Reply = (request: ApiRequest) => ApiTransportResponse | undefined
  | Promise<ApiTransportResponse | undefined>;

function setup(reply?: Reply) {
  const requests: ApiRequest[] = [];
  const themeValues = new Map<string, string>();
  const themeStorage: Pick<Storage, 'getItem' | 'setItem'> = {
    getItem: (key) => themeValues.get(key) ?? null,
    setItem: (key, value) => { themeValues.set(key, value); },
  };
  const transport: ApiTransportPort = {
    async send(request) {
      requests.push(request);
      if (reply) {
        const response = await reply(request);
        if (response) return response;
      }
      if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap());
      if (request.path === '/api/v1/areas') return ok([AREA]);
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([TRACK, TRACK_B]);
      if (request.path === '/api/overlays?entity_kind=track') return ok([]);
      if (request.path === '/api/v1/tracks/w1') return ok(trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })]));
      if (request.path === '/api/v1/tracks/w2') return ok(trackWithReport(TRACK_B, [fixtureSession(CARD_B.id, CARD_B.trackId, { title: CARD_B.title, isLead: true })]));
      if ((request.path.includes('/items?') && !request.path.includes('limit=1'))) return ok(historyPage(conversationCardId(request.path), []));
      if (request.path.includes('limit=1')) return ok(historyPage(conversationCardId(request.path), []));
      if (request.path.endsWith('/messages')) return ok(sessionInput(CARD.id));
      if (request.path.endsWith('/control')) return ok({ ok: true });
      if (request.path === '/api/settings') return ok({});
      return ok([]);
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, unauthorized, client, cards: bootTestCardRuntime() });
  render(<QueryClientProvider client={client}><ThemeProvider storage={themeStorage}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return { client, requests, router };
}

async function openConversation() {
  fireEvent.click(await screen.findByRole('button', { name: /Conversation Planner chat/ }));
  await screen.findByRole('complementary', { name: 'Planner chat' });
}

/* `fireEvent.change` cannot drive Astryx's `contenteditable` composer (no value
   setter) and there is no `<form>`: text is written into the editable, an `input`
   event feeds React state, and Enter sends. */
async function typeInto(field: HTMLElement, text: string) {
  field.textContent = text;
  const range = document.createRange();
  range.setStart(field.firstChild!, text.length);
  range.collapse(true);
  const selection = window.getSelection()!;
  selection.removeAllRanges();
  selection.addRange(range);
  await act(async () => {
    fireEvent.input(field);
    await Promise.resolve();
  });
}

async function sendWithEnter(field: HTMLElement) {
  await act(async () => {
    fireEvent.keyDown(field, { key: 'Enter' });
    await Promise.resolve();
  });
}

/** Wait out one `POST /planner/input` plus one macrotask, past the promise chain's `finally` where `sending` is released. */
async function settleOneSend(requests: ApiRequest[], expected = 1) {
  await waitFor(() => {
    expect(requests.filter((request) => request.path.endsWith('/messages')))
      .toHaveLength(expected);
  });
  await act(async () => { await new Promise((resolve) => { setTimeout(resolve, 0); }); });
}

it('refreshes a mounted agent catalog when its first turn settles', async () => {
  let configured = false;
  const { client } = setup((request) => {
    if (request.path === '/api/v1/tracks/w1') return ok(trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true, provider: 'opencode' })]));
    if ((request.path.startsWith('/api/v1/bootstrap?card_id=') || request.path.startsWith('/api/v1/bootstrap?provider='))) return ok(fixtureBootstrap(['opencode'], configured ? ['declared'] : []));
    return undefined;
  });
  await openConversation();
  await waitFor(() => expect(client.getQueryData(queryKeys.modelCatalog({ kind: 'card', cardId: CARD.id, provider: 'opencode' }))).toMatchObject({ models: [] }));
  configured = true;
  const event = wireEventSchema.parse({ ev: 'turn.completed', data: { track_id: TRACK.id, turn: { id: 'turn-1', sessionId: CARD.id, status: 'completed', startedAtMs: 1, completedAtMs: 2 } } });
  act(() => applyEventEffects(client, [{ type: 'invalidate', keys: invalidationPlanFor(event).invalidate }]));
  await waitFor(() => expect(within(drawerElement()).getByRole('button', { name: /^Model:/ }).hasAttribute('disabled')).toBe(false));
  expect(client.getQueryData(queryKeys.modelCatalog({ kind: 'card', cardId: CARD.id, provider: 'opencode' }))).toMatchObject({ source: 'live' });
});

it('renders local screenshot references from planner history using the agent worktree', async () => {
  const agentRoot = '/tmp/planner-worktree';
  setup((request) => {
    if (request.path === '/api/v1/tracks/w1') return ok(trackWithReport({ ...TRACK, workspaceDriver: 'vcs-workspace', workspaceSpec: { logicalRoot: '/tmp', worktreePath: agentRoot } }, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })]));
    if ((request.path.includes('/items?') && !request.path.includes('limit=1'))) return ok(historyPage(conversationCardId(request.path), [{
      ...messageRows(1)[0],
      payload: { text: `![Screenshot](${agentRoot}/screenshots/page.png)\n\n![Detail](screenshots/detail.png)` },
    }]));
    return undefined;
  });
  await openConversation();
  await waitFor(() => expect(screen.getByRole('img', { name: 'Screenshot' }).getAttribute('src'))
    .toBe('/api/v1/tracks/w1/files/raw?path=screenshots%2Fpage.png'));
  expect(screen.getByRole('img', { name: 'Detail' }).getAttribute('src'))
    .toBe('/api/v1/tracks/w1/files/raw?path=screenshots%2Fdetail.png');
});

it('binds the same relative screenshot path to the newly opened conversation track', async () => {
  const { router } = setup((request) => (request.path.includes('/items?') && !request.path.includes('limit=1')) ? ok(historyPage(conversationCardId(request.path), [{
    ...messageRows(1)[0], payload: { text: '![Screenshot](screenshots/page.png)' },
  }])) : undefined);
  await openConversation();
  expect((await screen.findByRole('img', { name: 'Screenshot' })).getAttribute('src'))
    .toBe('/api/v1/tracks/w1/files/raw?path=screenshots%2Fpage.png');
  await act(async () => { await router.navigate({ to: '/track/w2' }); });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Second chat' }));
  const secondDrawer = await screen.findByRole('complementary', { name: 'Second chat' });
  expect((await within(secondDrawer).findByRole('img', { name: 'Screenshot' })).getAttribute('src'))
    .toBe('/api/v1/tracks/w2/files/raw?path=screenshots%2Fpage.png');
});

it('does not repeat the run phase above the composer while the planner is working', async () => {
    setupWithTurns((request) => request.path.includes('limit=1')
      ? ok(runningPage())
      : undefined);
    await openConversationWithTurns();
    expect(screen.queryByText(/Turn complete|Still working|Stopping this turn/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Stop' })).toBeTruthy();
  });

it('sends the chosen model to the server when the picker is used', async () => {
    const { requests } = setupWithTurns((request) => {
      if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap(['codex'], ['gpt-5']));
      if (request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path)) {
        return ok({
          id: CARD.id, trackId: TRACK.id, provider: 'codex', model: 'gpt-5', createdAtMs: 1, updatedAtMs: 2,
        });
      }
      return undefined;
    });
    await openConversationWithTurns();
    const drawer = screen.getByRole('complementary', { name: 'Planner chat' });
    fireEvent.click(within(drawer).getByRole('button', { name: /^Model:/ }));
    fireEvent.click(await screen.findByRole('radio', { name: /^gpt-5/ }));

    await waitFor(() => {
      const writes = requests.filter((request) => request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path));
      expect(writes).toHaveLength(1);
      expect(writes[0]?.method).toBe('PATCH');
      /* The v1 endpoint accepts only the model selection. */
      expect(writes[0]?.body).toEqual({ model: 'gpt-5', reasoningEffort: null });
    });
  });

it('says nothing when the conversation is not blocked', async () => {
    setupWithTurns();
    await openConversationWithTurns();
    expect(screen.queryByText(/Pick a model to start it again/)).toBeNull();
  });

it('offers exactly the close, the resize edge, Send, the model picker, the role, and no other control at all', async () => {
    setupWithTurns();
    await openConversationWithTurns();
    const drawer = screen.getByRole('complementary', { name: 'Planner chat' });
    const names = within(drawer)
      .getAllByRole('button', { hidden: true })
      .map((button) => button.getAttribute('aria-label') ?? button.textContent);
    /* Bootstrap declares no models, so the trigger reads `Model: Default` and no effort control or Save/Cancel chrome appears. */
    expect([...names].sort()).toEqual([
      'Attach an image', 'Close conversation', 'Model: Default', 'Save to memory', 'Send', 'Session actions',
    ]);
    /* One shell contract and one shared host edge serve the conversation layout. */
    expect(screen.getAllByRole('separator').map((edge) => edge.getAttribute('aria-label'))).toEqual(['Resize conversation']);
    expect(screen.queryByRole('button', { name: /reset/i })).toBeNull();
  });

it('previews a drag on the main region, keeps the settled width there, and clears it on reset', async () => {
    setupWithTurns();
    await openConversationWithTurns();
    const measured = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect')
      .mockReturnValue(DOMRect.fromRect({ x: 0, y: 0, width: 480, height: 600 }));
    const capture = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'setPointerCapture');
    Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', { configurable: true, value: () => {} });
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    try {
      const main = document.querySelector('main')!;
      const edge = screen.getByRole('separator', { name: 'Resize conversation' });
      expect(main.style.getPropertyValue('--nc-drawer-width')).toBe('');
      fireEvent.pointerDown(edge, { pointerId: 1, button: 0, clientX: 500 });
      fireEvent.pointerMove(edge, { pointerId: 1, clientX: 420 });
      vi.advanceTimersToNextFrame();
      expect(main.style.getPropertyValue('--nc-drawer-width')).toBe('35rem');
      fireEvent.pointerUp(edge, { pointerId: 1, clientX: 420 });
      expect(main.style.getPropertyValue('--nc-drawer-width')).toBe('30rem');
      fireEvent.keyDown(edge, { key: 'Home' });
      expect(main.style.getPropertyValue('--nc-drawer-width')).toBe('');
    } finally {
      vi.useRealTimers();
      measured.mockRestore();
      if (capture === undefined) delete (HTMLElement.prototype as Partial<HTMLElement>).setPointerCapture;
      else Object.defineProperty(HTMLElement.prototype, 'setPointerCapture', capture);
    }
  });

it('never posts to the planner reset endpoint, however the drawer is driven', async () => {
    const { requests, router } = setupWithTurns();
    await openConversationWithTurns();
    const drawer = screen.getByRole('complementary', { name: 'Planner chat' });

    const field = within(drawer).getByRole('combobox', { name: 'Message' });
    await typeInto(field, 'a message');
    await sendWithEnter(field);

    const controls = within(drawer)
      .getAllByRole('button', { hidden: true })
      .filter((button) => button.getAttribute('aria-label') !== 'Close conversation' && button.closest('[popover]') === null);
    for (const control of controls) fireEvent.click(control);
    fireEvent.keyDown(document, { key: 'Escape' });
    fireEvent.click(within(drawer).getByRole('button', { name: 'Close conversation' }));

    await act(async () => { await router.navigate({ to: '/' }); });
    await act(async () => { await router.navigate({ to: '/track/w1' }); });
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    expect(requests.filter((request) => request.path.endsWith('/planner/reset'))).toHaveLength(0);
    /* The pressing above actually did something, so an inert sweep cannot pass. */
    expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(2);
  });

it('offers /new in the track composer, now that a track can hold a second conversation', async () => {
    setupWithTurns();
    await openConversationWithTurns();
    const field = messageField();
    expect(field.getAttribute('aria-haspopup')).toBe('listbox');
    expect(screen.queryByRole('textbox', { name: 'Message' })).toBeNull();
  });

it('keeps a track route conversation list scoped after visiting another track', async () => {
    const { router } = setup();
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await router.navigate({ to: '/track/w2' });
    await screen.findByRole('button', { name: 'Conversation Second chat' });
    expect(screen.queryByRole('button', { name: 'Conversation Planner chat' })).toBeNull();
  });

it('follows a card swapped out and back, showing the transcript of whichever row is open', async () => {
    const { client } = setup((request) => (request.path.includes('/items?') && !request.path.includes('limit=1'))
      ? ok(historyPage(conversationCardId(request.path), messageRows(3))) : undefined);
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Planner chat' }));
    /* Wait for all transcript messages before swapping the selected conversation. */
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => expect(within(drawerElement()).getAllByText(/^reply \d$/)).toHaveLength(3));

    client.setQueryData(queryKeys.trackDetail(TRACK.id), trackWithReport(TRACK, [fixtureSession(CARD_SAME_TRACK.id, CARD_SAME_TRACK.trackId, { title: CARD_SAME_TRACK.title, isLead: true })]));
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Other chat' }));
    const otherDrawer = await screen.findByRole('complementary', { name: 'Other chat' });
    await waitFor(() => expect(within(otherDrawer).getAllByText(/^reply \d$/)).toHaveLength(3));
    client.setQueryData(queryKeys.trackDetail(TRACK.id), trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })]));
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Planner chat' }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => expect(within(drawerElement()).getAllByText(/^reply \d$/)).toHaveLength(3));
  });

it('renders a server-sent reply from the history fixture', async () => {
    setup((request) => (request.path.includes('/items?') && !request.path.includes('limit=1')) ? ok(historyPage(conversationCardId(request.path), messageRows(1))) : undefined);
    await openConversation();
    expect(await screen.findByText('reply 0')).toBeTruthy();
  });

it('retires a completed tail thought through local send feedback and server history handoff', async () => {
    const thought = sessionItem(CARD.id, 1, '', 'thinking');
    let stored = false;
    let accept!: () => void;
    setup(request => {
      if (request.path.endsWith('/messages')) return new Promise(resolve => {
        accept = () => { stored = true; resolve(ok(sessionInput(CARD.id))); };
      });
      if (request.path.includes('/items?') && !request.path.includes('limit=1')) return ok(historyPage(conversationCardId(request.path),
        [thought, ...(stored ? [sessionItem(CARD.id, 2, 'next message', 'user')] : [])]));
      return undefined;
    });
    await openConversation();
    expect(await screen.findByText('Thought')).toBeTruthy();
    const field = messageField();
    await typeInto(field, 'next message');
    await sendWithEnter(field);
    expect(await screen.findByText('next message')).toBeTruthy();
    expect(screen.queryByText('Thought')).toBeNull();
    await act(async () => { accept(); await Promise.resolve(); });
    expect(await screen.findByText('next message')).toBeTruthy();
    expect(screen.queryByText('Thought')).toBeNull();
  });

it('does not read older bodies after initial presentation, idle, resize or initial top', async () => {
    const { requests } = setup(request => (request.path.includes('/items?') && !request.path.includes('limit=1'))
      ? historyReply(messageRows(TRANSCRIPT_PAGE_LIMIT + 1)) : undefined);
    await openConversationWithTurns();
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 60)); });
    const scroller = drawerElement().querySelector('[data-nc-drawer-scroll]')!;
    fireEvent.scroll(scroller);
    fireEvent(window, new Event('resize'));
    expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(1);
    expect(screen.queryByText('Loading earlier messages… Navigation covers loaded history.')).toBeNull();
  });

it('consumes one upward input for at most one page without response chaining', async () => {
    const rows = messageRows(TRANSCRIPT_PAGE_LIMIT * 3);
    const { requests } = setup(request => {
      if (!(request.path.includes('/items?') && !request.path.includes('limit=1'))) return undefined;
      const cursor = Number(historyPosition(request.path));
      return historyReply(cursor === 0 ? rows.slice(-500) : rows.filter(row => row.id < cursor).slice(-500));
    });
    await openConversation();
    await screen.findByText(`reply ${TRANSCRIPT_PAGE_LIMIT * 3 - 1}`);
    fireEvent.wheel(drawerElement().querySelector('[data-nc-drawer-scroll]')!, { deltaY: -40 });
    await waitFor(() => expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2));
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 60)); });
    expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2);
  });

it('cancels pending busy history input on unmount', async () => {
    let release!: (response: ApiTransportResponse) => void;
    let hold = false;
    const rows = messageRows(TRANSCRIPT_PAGE_LIMIT + 1);
    const { client, requests } = setup(request => (request.path.includes('/items?') && !request.path.includes('limit=1'))
      ? hold ? new Promise(resolve => { release = resolve; }) : historyReply(rows) : undefined);
    await openConversationWithTurns();
    hold = true;
    act(() => { void client.invalidateQueries({ queryKey: transcriptQueryKey() }); });
    await waitFor(() => expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2));
    await readEarlierWithInput();
    cleanup();
    await act(async () => { release(historyReply(rows)); await Promise.resolve(); });
    expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2);
  });

it('pauses on failure and explicitly retries only the failed page', async () => {
    let fail = true;
    const { requests } = setup(request => {
      if (!(request.path.includes('/items?') && !request.path.includes('limit=1'))) return undefined;
      if (historyPosition(request.path) === '0') return historyReply(messageRows(TRANSCRIPT_PAGE_LIMIT + 1));
      return fail ? { status: 503, statusText: 'Unavailable', body: null } : historyReply([]);
    });
    await openConversation();
    await readEarlierWithInput();
    await screen.findByRole('button', { name: 'Refresh' });
    const reads = () => requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')));
    expect(reads()).toHaveLength(2);
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 80)); });
    expect(reads()).toHaveLength(2);
    fail = false;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Refresh' })).toBeNull());
    expect(reads()).toHaveLength(3);
  });

it('does not cancel a newest reread to fill earlier history', async () => {
    let release!: (response: ApiTransportResponse) => void;
    let hold = false;
    const rows = messageRows(TRANSCRIPT_PAGE_LIMIT + 1);
    const { client, requests } = setup(request => {
      if (!(request.path.includes('/items?') && !request.path.includes('limit=1'))) return undefined;
      if (historyPosition(request.path) !== '0') return historyReply([]);
      return hold ? new Promise(resolve => { release = resolve; }) : historyReply(rows);
    });
    await openConversation();
    await waitFor(() => expect(drawerElement().querySelector('[data-nc-thread]')).not.toBeNull());
    hold = true;
    act(() => { void client.invalidateQueries({ queryKey: transcriptQueryKey() }); });
    await waitFor(() => expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2));
    fireEvent.wheel(drawerElement().querySelector('[data-nc-drawer-scroll]')!, { deltaY: -40 });
    hold = false;
    await act(async () => { release(historyReply(rows)); await Promise.resolve(); });
    await waitFor(() => expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(3));
    expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1'))).map(request =>
      historyPosition(request.path))).toEqual(['0', '0', '2']);
  });

it('cancels hidden visits and does not let A history schedule work for B or a later A visit', async () => {
    const first = messageRows(TRANSCRIPT_PAGE_LIMIT + 1).map(row => ({ ...row, id: row.id + 119 }));
    const middle = messageRows(TRANSCRIPT_PAGE_LIMIT + 1).map(row => ({ ...row, id: row.id + 59 }));
    let holdEarlier = true;
    let holdNewest = false;
    let releaseNewest!: (response: ApiTransportResponse) => void;
    let release!: (response: ApiTransportResponse) => void;
    const { client, requests } = setup(request => {
      if (request.path === '/api/v1/tracks/w1') return ok(trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })]));
      if (!(request.path.includes('/items?') && !request.path.includes('limit=1'))) return undefined;
      if (request.path.includes(CARD_SAME_TRACK.id)) return historyReply([]);
      if (historyPosition(request.path) === '0') return holdNewest ? new Promise(resolve => { releaseNewest = resolve; }) : historyReply(first);
      if (historyPosition(request.path) === '121') return holdEarlier ? new Promise(resolve => { release = resolve; }) : historyReply(middle);
      return historyReply([]);
    });
    await openConversation();
    await waitFor(() => expect(drawerElement().querySelector('[data-nc-thread]')).not.toBeNull());
    holdNewest = true;
    act(() => { void client.invalidateQueries({ queryKey: transcriptQueryKey() }); });
    await waitFor(() => expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2));
    await readEarlierWithInput();
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    holdNewest = false;
    await act(async () => { releaseNewest(historyReply(first)); await Promise.resolve(); });
    expect(requests.filter(request => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(2);
    await openConversation();
    await readEarlierWithInput();
    act(() => { client.setQueryData(queryKeys.trackDetail(TRACK.id), trackWithReport(TRACK, [fixtureSession(CARD_SAME_TRACK.id, CARD_SAME_TRACK.trackId, { title: CARD_SAME_TRACK.title, isLead: true })])); });
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other chat/ }));
    await screen.findByRole('complementary', { name: 'Other chat' });
    await act(async () => { holdEarlier = false; release(historyReply(middle)); await Promise.resolve(); });
    expect(requests.some(request => historyPosition(request.path) === '61')).toBe(false);
    act(() => { client.setQueryData(queryKeys.trackDetail(TRACK.id), trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })])); });
    await openConversation();
    await readEarlierWithInput();
    await waitFor(() => expect(requests.some(request => historyPosition(request.path) === '61')).toBe(true));
  });

it('surfaces send failures and prevents a second send while the first is pending', async () => {
    let reject!: (reason: Error) => void;
    const pending = new Promise<ApiTransportResponse>((_resolve, rejectPromise) => { reject = rejectPromise; });
    const { requests } = setup((request) => request.path.endsWith('/messages') ? pending : undefined);
    await openConversation();
    const field = messageField();
    await typeInto(field, 'hello');
    await sendWithEnter(field);
    await sendWithEnter(field);
    expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(1);
    reject(new Error('send exploded'));
    expect(within(await screen.findByRole('alert')).getByText('Send result unconfirmed.')).toBeTruthy();
  });

it('does not mark a message sent from an idle conversation as queued', async () => {
    let stored = false;
    setup(request => {
      if (request.path.endsWith('/messages')) { stored = true; return ok(sessionInput(CARD.id)); }
      if (request.path.includes('/items?') && !request.path.includes('limit=1')) return ok(historyPage(conversationCardId(request.path),
        stored ? [sessionItem(CARD.id, 1, 'idle words', 'user')] : []));
      return undefined;
    });
    await openConversation();
    const field = messageField();
    await typeInto(field, 'idle words');
    await sendWithEnter(field);
    expect(await screen.findByText('idle words')).toBeTruthy();
    expect(document.querySelector('[data-nc-queued]')).toBeNull();
    expect(document.querySelector('[data-nc-queued-note]')).toBeNull();
  });

it('allows queuing while a v1 active turn is running and exposes Stop', async () => {
  const { requests } = setup(request => request.path.includes('limit=1') ? ok(runningPage()) : undefined);
  await openConversation();
  await screen.findByRole('button', { name: 'Stop' });
  expect(messageField().getAttribute('contenteditable')).toBe('true');
  expect(requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(0);
});

it('holds the idle composer only while the send is unanswered', async () => {
    let accept!: () => void;
    const { requests } = setup(request => request.path.endsWith('/messages') ? new Promise(resolve => {
      accept = () => resolve(ok(sessionInput(CARD.id)));
    }) : undefined);
    await openConversation();
    await typeInto(messageField(), 'idle send');
    await sendWithEnter(messageField());
    await settleOneSend(requests);
    expect(messageField().getAttribute('contenteditable')).toBe('false');
    await act(async () => { accept(); await Promise.resolve(); });
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  });

it('keeps Stop working, and offers nothing else beside it', async () => {
    const { requests } = setup((request) => request.path.includes('limit=1')
      ? ok(runningPage())
      : undefined);
    await openConversation();
    const stop = await screen.findByRole('button', { name: 'Stop' });
    expect(screen.queryByRole('button', { name: 'Queue message' })).toBeNull();
    fireEvent.click(stop);
    await waitFor(() => {
      expect(requests.filter((request) => request.path.endsWith('/control'))).toHaveLength(1);
    });
  });

it('ticks the meta row from the run response\'s running turn', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] });
    try {
      vi.setSystemTime(1_000_000);
      let elapsed = 18_000;
      const { client } = setupWithTurns((request) => {
        if (request.path.includes('limit=1')) {
          return ok(sessionPage(fixtureSession(CARD.id, TRACK.id, { status: 'running' }), [], { ...sessionTurn(CARD.id), startedAtMs: Date.now() - elapsed }));
        }
        return request.path === '/api/v1/tracks/w1'
          ? ok(trackWithReport(TRACK, [fixtureSession(CARD.id, CARD.trackId, { title: CARD.title, isLead: true })]))
          : undefined;
      });
      await openConversationWithTurns();
      const meta = () => screen.getByRole('status', { name: 'Current response status' });
      const duration = () => meta().querySelector('[data-nc-meta-duration]')?.textContent ?? null;
      await waitFor(() => expect(duration()).toBe('· 18s'));
      expect(meta().textContent).toContain('Working…');
      act(() => { vi.advanceTimersByTime(2_000); });
      expect(duration()).toBe('· 20s');
      /* The refreshed lastTurn owns the clock, including a corrected start time. */
      elapsed = 19_000;
      /* The refetched data reaches the store through TanStack's `setTimeout(0)` notify, which this clock does not fake. */
      await act(async () => {
        await client.invalidateQueries({ queryKey: queryKeys.plannerRun(CARD.id) });
        await new Promise((resolve) => { setTimeout(resolve, 0); });
      });
      act(() => { vi.advanceTimersByTime(1_000); });
      expect(duration()).toBe('· 20s');
    } finally {
      vi.useRealTimers();
    }
  });

it('invalidates history and phase after a successful send', async () => {
    const { requests } = setup();
    await openConversation();
    await waitFor(() => {
      expect(requests.filter((request) => (request.path.includes('/items?') && !request.path.includes('limit=1'))).length).toBeGreaterThan(0);
      expect(requests.filter((request) => request.path.includes('limit=1')).length).toBeGreaterThan(0);
    });
    const beforeHistory = requests.filter((request) => (request.path.includes('/items?') && !request.path.includes('limit=1'))).length;
    const beforeRun = requests.filter((request) => request.path.includes('limit=1')).length;
    const field = messageField();
    await typeInto(field, 'hello');
    await sendWithEnter(field);
    await waitFor(() => {
      expect(requests.filter((request) => (request.path.includes('/items?') && !request.path.includes('limit=1')))).toHaveLength(beforeHistory + 1);
      expect(requests.filter((request) => request.path.includes('limit=1'))).toHaveLength(beforeRun + 1);
    });
  });

it('uses Escape to interrupt once, then closes without repeating the pending request', async () => {
    let resolveInterrupt!: (response: ApiTransportResponse) => void;
    const pendingInterrupt = new Promise<ApiTransportResponse>((resolve) => { resolveInterrupt = resolve; });
    const { requests } = setup((request) => {
      if (request.path.includes('limit=1')) {
        return ok(runningPage());
      }
      return request.path.endsWith('/control') ? pendingInterrupt : undefined;
    });
    const opener = await screen.findByRole('button', { name: /Conversation Planner chat/ });
    opener.focus();
    await openConversation();
    const drawer = screen.getByRole('complementary', { name: 'Planner chat' });
    /* Wait for the committed working view, rather than retry a gesture while
       the query is still loading (idle Escape legitimately closes the pane). */
    await within(drawer).findByRole('button', { name: 'Stop' });
    fireEvent.keyDown(drawer, { key: 'Escape' });
    await waitFor(() => expect(requests.filter((request) => request.path.endsWith('/control'))).toHaveLength(1));
    expect(screen.getByRole('complementary', { name: 'Planner chat' })).toBe(drawer);
    const title = within(drawer).getByRole('heading', { name: 'Planner chat' });
    const field = within(drawer).getByRole('combobox', { name: 'Message' });
    fireEvent.keyDown(drawer, { key: 'Escape' });
    expect(requests.filter((request) => request.path.endsWith('/control'))).toHaveLength(1);
    /* A pending stop yields Escape to the drawer owner. Its last tree remains
       for paint, with interaction and accessibility revoked in this commit. */
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
    expect(drawer.isConnected).toBe(true);
    expect(drawer.hasAttribute('inert')).toBe(true);
    expect(drawer.getAttribute('aria-hidden')).toBe('true');
    expect(drawer.contains(title)).toBe(true);
    expect(drawer.contains(field)).toBe(true);
    expect(drawer.hasAttribute('data-nc-drawer-open')).toBe(false);
    expect(document.activeElement).toBe(opener);
    await act(() => {
      resolveInterrupt(ok({ ok: true }));
      return Promise.resolve();
    });
  });
/** The open drawer, as a root for what is and is not inside it. */
function drawerElement(): HTMLElement {
  return screen.getByRole('complementary', { name: 'Planner chat' });
}

/* `combobox`, not `textbox`: the composer carries the `/` command menu, and
   `useTriggerMenu` only emits the combobox role when a trigger is configured. */
function messageField(): HTMLElement {
  return screen.getByRole('combobox', { name: 'Message' });
}

function setupWithTurns(reply?: Reply) {
  return setup(async (request) => await reply?.(request)
    ?? ((request.path.includes('/items?') && !request.path.includes('limit=1')) ? ok(historyPage(conversationCardId(request.path), messageRows(1))) : undefined));
}

async function openConversationWithTurns() {
  await openConversation();
  await screen.findByText(/reply [01]$/);
}

function historyPosition(path: string): string {
  const cursor = new URL(path, 'http://fixture').searchParams.get('beforeId');
  return cursor === null ? '0' : String(Number(cursor));
}
function historyReply(rows: ReturnType<typeof messageRows>) {
  return ok(historyPage(CARD.id, rows));
}

async function readEarlierWithInput() {
  await waitFor(() => expect(drawerElement().querySelector('[data-nc-thread]')).not.toBeNull());
  const scroller = drawerElement().querySelector('[data-nc-drawer-scroll]');
  if (scroller === null) throw new Error('No reading pane');
  await act(async () => { fireEvent.wheel(scroller, { deltaY: -40 }); await Promise.resolve(); });
}

beforeEach(() => {
  window.history.pushState({}, '', `/track/w1`);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => window.setTimeout(() => callback(0), 0));
  vi.stubGlobal('cancelAnimationFrame', (id: number) => window.clearTimeout(id));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function toolCall(id: number, callId: string, name: string): SessionItemDto {
  return { id, sessionId: CARD.id, turnId: 'turn-1', kind: 'tool_call', payload: { callId, name, args: {} }, createdAtMs: id };
}
function toolResult(id: number, callId: string, output: string, isError = false): SessionItemDto {
  return { id, sessionId: CARD.id, turnId: 'turn-1', kind: 'tool_result', payload: { callId, output, isError }, createdAtMs: id };
}

it('keeps a completed v1 tool call before the reply interleaved with its result', async () => {
  setup(request => request.path.includes('/items?') && !request.path.includes('limit=1') ? ok(historyPage(CARD.id, [
    toolCall(1, 'call-1', 'inspect'), sessionItem(CARD.id, 2, 'interleaved reply'), toolResult(3, 'call-1', 'done'),
  ])) : undefined);
  await openConversation();
  await screen.findByText('interleaved reply');
  const text = drawerElement().textContent ?? '';
  expect(text.indexOf('inspect')).toBeGreaterThanOrEqual(0);
  expect(text.indexOf('inspect')).toBeLessThan(text.indexOf('interleaved reply'));
});
it('keeps a v1 tool group expanded when a result arrives for its running call', async () => {
  let rows = [toolCall(1, 'call-1', 'inspect'), toolCall(2, 'call-2', 'verify')];
  const { client } = setup(request => request.path.includes('/items?') && !request.path.includes('limit=1')
    ? ok(historyPage(CARD.id, rows)) : undefined);
  await openConversation();
  const group = await screen.findByRole('group', { name: '2 tool calls' });
  const header = group.querySelector<HTMLElement>(':scope > [role="button"]')!;
  fireEvent.click(header);
  expect(header.getAttribute('aria-expanded')).toBe('true');
  rows = [...rows, toolResult(3, 'call-1', 'passed'), toolResult(4, 'call-2', 'done')];
  await act(async () => { await client.invalidateQueries({ queryKey: transcriptQueryKey() }); });
  await waitFor(() => expect(within(group).queryByText('Working…')).toBeNull());
  expect(screen.getByRole('group', { name: '2 tool calls' })).toBe(group);
  expect(header.getAttribute('aria-expanded')).toBe('true');
});

it('extends a tool group across the v1 history window when earlier reading is requested', async () => {
  const rows = [toolCall(1, 'call-1', 'first'), toolCall(2, 'call-2', 'second'),
    ...Array.from({ length: TRANSCRIPT_PAGE_LIMIT - 1 }, (_, index) => toolCall(index + 3, `call-${index + 3}`, `tool-${index + 3}`))];
  setup(request => {
    if (!request.path.includes('/items?') || request.path.includes('limit=1')) return undefined;
    const beforeId = new URL(request.path, 'http://fixture').searchParams.get('beforeId');
    return ok(historyPage(CARD.id, beforeId === null ? rows : rows.filter(row => row.id < Number(beforeId))));
  });
  await openConversation();
  const group = await screen.findByRole('group', { name: '60 tool calls' });
  fireEvent.click(group.querySelector<HTMLElement>(':scope > [role="button"]')!);
  await readEarlierWithInput();
  const expanded = await screen.findByRole('group', { name: '61 tool calls' });
  expect(expanded.querySelector(':scope > [aria-expanded="true"]')).not.toBeNull();
});

it('retains an opened tool failure as the latest v1 window rolls forward', async () => {
  const initial = [toolCall(1, 'call-1', 'inspect'), toolCall(2, 'call-2', 'verify'), toolResult(3, 'call-2', 'failure detail', true),
    ...Array.from({ length: TRANSCRIPT_PAGE_LIMIT - 3 }, (_, index) => sessionItem(CARD.id, index + 4, `reply ${index + 4}`))];
  let rows = initial;
  const { client } = setup(request => request.path.includes('/items?') && !request.path.includes('limit=1')
    ? ok(historyPage(CARD.id, rows)) : undefined);
  await openConversation();
  const group = await screen.findByRole('group', { name: '2 tool calls' });
  fireEvent.click(group.querySelector<HTMLElement>(':scope > [role="button"]')!);
  fireEvent.click(within(group).getByRole('button', { name: /verify/ }));
  expect((await within(group).findAllByText(/failure detail/)).length).toBeGreaterThan(0);
  rows = [...initial, sessionItem(CARD.id, TRANSCRIPT_PAGE_LIMIT + 1, 'new reply')];
  await act(async () => { await client.invalidateQueries({ queryKey: transcriptQueryKey() }); });
  await screen.findByText('new reply');
  expect(screen.getByRole('group', { name: '2 tool calls' })).toBe(group);
  expect(within(group).getAllByText(/failure detail/).length).toBeGreaterThan(0);
});
