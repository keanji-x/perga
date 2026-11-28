import { DEPLOYMENT_OWNER_USER_ID } from '../../../../core/api/auth.ts';
import { CurrentUserScope } from '../auth/current-user.tsx';
import { sessionItemsPageDtoToConversationPage } from '../../../../core/domain/conversation-display.ts';
import { fixtureBootstrap, fixtureTrack, fixtureSession, fixtureSnapshot, fixtureCreation } from './session-contract-fixture.ts';
import type { AgentSessionDto, SessionItemDto } from '../../../../core/api/perga-v1.ts';
import { latestHistory } from '../conversations/history.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionPage, sessionItem, sessionInput } from './conversation-session-data.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { useConversationStore } from '../conversations/store.ts';
import * as conversationStores from '../conversations/store.ts';
// @vitest-environment jsdom
// Starting a conversation on a track, driven through the real router; and what the
// session registry may remember about one, driven through the real store.

import { SEND_RETRIES } from '../../../../core/domain/conversation-delivery.ts';
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useEffect, useLayoutEffect } from 'react';

import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { isTrackUnread, isUnread, type ReadState } from '../../../../core/domain/read-receipts.ts';
import { queryKeys } from '../providers/query-keys.ts';
import type { Conversation, TranscriptEntry } from '../../../../core/domain/conversation.ts';
import {
  CONVERSATION_CREATE_TEXT,
} from '../../../../core/domain/conversation.ts';
import { ConversationProvider, useConversationCommands, useConversationComposerText, useConversationRememberedTurns, useConversationSendOps, useRememberedConversations } from '../conversations/public.tsx';
import { createUiPreferences, type UiPreferenceStorage } from '../providers/ui-preferences.tsx';
import { createReadReceiptsKernel, type ReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { RAIL_COLLAPSE_QUERY } from '../../styles/breakpoints.ts';

/* A send's automatic retries wait no real time here; how long they back off is not under test. */
vi.mock('../../../../core/domain/conversation-delivery.ts', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../../../core/domain/conversation-delivery.ts')>(),
  sendRetryDelay: () => 0,
}));

/** Text in the drawer outside its desktop header, which paints the conversation's name (often its first message). */
const TRANSCRIPT_TEXT = { ignore: 'script, style, [data-nc-drawer] > header *, [aria-hidden="true"] *' };

const AREA = { id: 'c1', name: 'Work', color: '#000', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Test track', leadSessionId: 'card-planner' });
const BARE_TRACK = fixtureTrack('w2', { areaId: 'c1', title: 'Bare track', sortOrder: 2 });
const PLANNER_CARD = fixtureSession('card-planner', TRACK.id, { title: 'Planner chat', isLead: true });
const ASSISTANT_CARD = fixtureSession('conv-assistant-1', TRACK.id, { updatedAtMs: 30 });
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });

const CONVERSATIONS = '/api/v1/tracks/w1/sessions';
const BARE_CONVERSATIONS = '/api/v1/tracks/w2/sessions';
const isHistoryPath = (path: string) => path.includes('/items?') && !path.includes('limit=1');

type Row = AgentSessionDto;
function assistantRow(overrides: Partial<Row> = {}): Row {
  return { ...ASSISTANT_CARD, ...overrides };
}
/* Read receipts need a database scope; without `ServerCompatGate` it is seeded
 * from the stored database identity. Under a null scope nothing is unread. */
/** The kernel's read receipts from a baseline of 0: every completion is unread until it is read. */
function receiptKernel(): ReadReceiptsKernel {
  return createReadReceiptsKernel({ baselineMs: 0 });
}

function historyPage(card: string, rows: SessionItemDto[]) {
  return sessionPage(fixtureSession(card, TRACK.id), rows);
}

/** The card a `/api/cards/{id}/…` request is about. */
function pathCardId(path: string): string {
  const match = /^\/api\/(?:v1\/sessions|cards)\/([^/?]+)/.exec(path);
  return decodeURIComponent(match?.[1] ?? path.split('/')[3] ?? '');
}

function ok(body: unknown): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body };
}

/* The shapes are schema-checked by the transport; an off-schema body is refused before any test can observe anything. */
const inputAccepted = () => ok(sessionInput(ASSISTANT_CARD.id));
const inputAcceptedWithEntry = (entryId: string, cardId = ASSISTANT_CARD.id) => ok(sessionInput(cardId, entryId));
const runIdle = () => ok(sessionPage(ASSISTANT_CARD));

function created(body: Row, request: ApiRequest): ApiTransportResponse {
  expect(request.headers?.['Idempotency-Key']).toBeTruthy();
  return { status: 201, statusText: 'Created', body: fixtureCreation(body) };
}

function failure(status: number, code: string, error: string): ApiTransportResponse {
  return { status, statusText: 'Error', body: { code, error } };
}

type Reply = (request: ApiRequest) => ApiTransportResponse | undefined
  | Promise<ApiTransportResponse | undefined>;

function setup(reply?: Reply, storage?: UiPreferenceStorage, receipts?: ReadReceiptsKernel) {
  const defaultReceipts = createReadReceiptsKernel();
  const requests: ApiRequest[] = [];
  const themeValues = new Map<string, string>();
  const themeStorage: Pick<Storage, 'getItem' | 'setItem'> = {
    getItem: (key) => themeValues.get(key) ?? null,
    setItem: (key, value) => { themeValues.set(key, value); },
  };
  const transport: ApiTransportPort = {
    async send(request) {
      const receipt = await (receipts ?? defaultReceipts).reply(request);
      if (receipt !== undefined) return receipt;
      requests.push(request);
      if (reply) {
        const response = await reply(request);
        if (response) return response;
      }
      if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap());
      if (request.path === '/api/v1/areas') return ok([AREA]);
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([TRACK, BARE_TRACK]);
      if (request.path === '/api/overlays?entity_kind=track') return ok([]);
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, assistantRow()]));
      if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot(BARE_TRACK));
      if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), []));
      /* Both card endpoints echo the card in the path, as the kernel does; a fixed id
         would be a trap for the first case that reads the field. */
      if (request.path.includes('limit=1')) return ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id)));
      /* An off-schema body is refused by the transport and the optimistic echo rolled back. */
      if (request.path.endsWith('/messages')) return ok(sessionInput(pathCardId(request.path)));
      if (request.path === '/api/settings') return ok({});
      return ok([]);
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, unauthorized, client, cards: bootTestCardRuntime(), uiPreferences: createUiPreferences(storage) });
  render(<QueryClientProvider client={client}><ThemeProvider storage={themeStorage}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return { client, requests, router };
}

function cachedHistoryKey(client: QueryClient, cardId: string): readonly unknown[] {
  const key = [...queryKeys.harnessItems(cardId), 'conversation_v1'];
  if (client.getQueryData(key) === undefined) throw new Error('history query was not cached');
  return key;
}

const creates = (requests: readonly ApiRequest[], path: string) =>
  requests.filter((request) => request.method === 'POST' && request.path === path);

/* The row the POST would mint: the panel looks for the id derived from
 * `(trackId, key)`, not for "a row that was not there before". */
const derivedRow = (trackId: string, request: ApiRequest): Row => fixtureSession(
  fixtureConversationCardId(trackId, request.headers?.['Idempotency-Key'] ?? ''), trackId, { updatedAtMs: 99 },
);

/* The open drawer, reached by the control only it has: an adopted assistant row
   is named `Assistant`, so the name cannot stand in. */
function drawerElement(): HTMLElement {
  const closer = screen.getByRole('button', { name: 'Close conversation' });
  const drawer = closer.closest('[role="complementary"]');
  if (drawer === null) throw new Error('the drawer is not open');
  return drawer as HTMLElement;
}

/* Scoped to the drawer: the list row behind it carries the same marker. */

async function openDraft() {
  fireEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
  /* The `+` is "New conversation"; the drawer it opens is "Untitled". */
  await screen.findByRole('complementary', { name: 'Untitled' });
}

function messageField(): HTMLElement {
  return screen.getByRole('combobox', { name: 'Message' });
}

/* The composer is Astryx's contenteditable div: no value setter, so `change`
   throws, and it sends on a bare Enter. */
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

async function write(text: string) {
  const field = messageField();
  await typeInto(field, text);
  await submit();
}

/** A bare command first completes the open menu; only the following Enter submits. */
async function completeAndSubmit(name: string) {
  await typeInto(messageField(), `/${name}`);
  await screen.findByRole('option', { name: new RegExp(`^${name}`) });
  await submit();
  await submit();
}

/** Send by Enter, the only door while a turn runs: Astryx's `handleSubmit` never consulted `isStopShown`. */
async function submit() {
  await act(async () => {
    fireEvent.keyDown(messageField(), { key: 'Enter' });
    await Promise.resolve();
  });
}

beforeEach(() => {
  window.history.pushState({}, '', `/track/w1`);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});

afterEach(() => {
  cleanup();
  onlineManager.setOnline(true);
  vi.unstubAllGlobals();
});

it.each(['/track/w1', '/track/w2'])('keeps compact dock text updates below both pane controllers in %s', async route => {
    window.history.pushState({}, '', `${route}`);
    vi.stubGlobal('matchMedia', vi.fn((media: string) => ({
      matches: media === RAIL_COLLAPSE_QUERY || media === '(prefers-reduced-motion: reduce)', media, onchange: null,
      addEventListener: vi.fn(), removeEventListener: vi.fn(), addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
    })));
    const controller = vi.spyOn(conversationStores, 'useConversationStore');
    try {
      setup();
      const dock = await screen.findByRole('textbox', { name: 'Chat message' });
      // Exercise the dock's controlled change port; focus deliberately opens the full editor.
      fireEvent.change(dock, { target: { value: 'First' } });
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });
      const before = controller.mock.calls.length;
      fireEvent.change(dock, { target: { value: 'First words' } });
      expect((dock as HTMLTextAreaElement).value).toBe('First words');
      expect(controller).toHaveBeenCalledTimes(before);
    } finally { controller.mockRestore(); }
  });

it('keeps ordinary existing and new composer edits below both pane controllers', async () => {
    const controller = vi.spyOn(conversationStores, 'useConversationStore');
    try {
      setup();
      fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
      await screen.findByRole('complementary', { name: 'Assistant' });
      await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
      await typeInto(messageField(), 'First');
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });
      const existingCalls = controller.mock.calls.length;
      await typeInto(messageField(), 'First words');
      expect(messageField().textContent).toBe('First words');
      expect(controller).toHaveBeenCalledTimes(existingCalls);
      fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
      await openDraft();
      await typeInto(messageField(), 'New');
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 0)); });
      const newCalls = controller.mock.calls.length;
      await typeInto(messageField(), 'New words');
      expect(messageField().textContent).toBe('New words');
      expect(controller).toHaveBeenCalledTimes(newCalls);
    } finally { controller.mockRestore(); }
  });

it('restores each track conversation drawer after switching tracks', async () => {
    const { router } = setup();
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Planner chat' }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await act(() => router.navigate({ to: '/track/w2' }));
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
    await act(() => router.navigate({ to: '/track/w1' }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    await act(() => router.navigate({ to: '/track/w2' }));
    await act(() => router.navigate({ to: '/track/w1' }));
    expect(screen.queryByRole('complementary', { name: 'Planner chat' })).toBeNull();
  });

it('lists the track\'s assistant conversations beside the planner one', async () => {
    setup();
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await screen.findByRole('button', { name: 'Conversation Assistant' });
  });

it('uses fresh server metadata for both unopened conversations without eager history', async () => {
    const { requests } = setup((request) => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [{ ...PLANNER_CARD, title: 'Original planner input' }, assistantRow({ title: 'Original assistant input' })]));
      if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 900, 'Latest page input', 'user')]));
      return undefined;
    });
    await screen.findByRole('button', { name: 'Conversation Original planner input' });
    const assistant = await screen.findByRole('button', { name: 'Conversation Original assistant input' });
    expect(requests.filter(request => isHistoryPath(request.path))).toHaveLength(0);
    fireEvent.click(assistant);
    await screen.findByRole('complementary', { name: 'Original assistant input' });
    await screen.findByText('Latest page input');
    expect(requests.filter(request => isHistoryPath(request.path))[0]?.path).toContain('afterId=0&limit=60');
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    await screen.findByRole('button', { name: 'Conversation Original assistant input' });
  });

it('keeps the name derived from confirmed turns after the drawer closes', async () => {
    let historyAvailable = true;
    const { client } = setup((request) => request.path === '/api/v1/tracks/w1'
      ? ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title: 'Named from history' })]]))
      : isHistoryPath(request.path)
      ? historyAvailable
        ? ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 1, 'Named from history', 'user')]))
        : new Promise(() => undefined)
      : undefined);
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Named from history' }));
    expect(await screen.findByRole('complementary', { name: 'Named from history' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    const remembered = await screen.findByRole('button', { name: /Conversation Named from history/ });

    historyAvailable = false;
    client.removeQueries({ queryKey: cachedHistoryKey(client, ASSISTANT_CARD.id) });
    fireEvent.click(remembered);
    expect(await screen.findByRole('complementary', { name: 'Named from history' })).toBeTruthy();
  });

it('keeps history failures out of the send channel and offers a retry', async () => {
    let reads = 0;
    const first = sessionItem(ASSISTANT_CARD.id, 1, 'first message', 'user');
    const { requests } = setup((request) => {
      if (isHistoryPath(request.path)) {
        reads += 1;
        return reads === 1
          ? { status: 503, statusText: 'Service Unavailable', body: { error: 'history unavailable' } }
          : ok(historyPage(conversationCardId(request.path), [first]));
      }
      return undefined;
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    expect((await screen.findByRole('alert')).textContent).toContain('Unable to load conversation history.');
    /* A 503's text is the server's: only the fixed sentence shows. */
    expect(screen.getByRole('alert').textContent).not.toContain('history unavailable');
    expect(screen.queryByText(/Start a conversation/)).toBeNull();
    expect(messageField().getAttribute('contenteditable')).toBe('false');
    expect(requests.some((request) => request.path.endsWith('/messages'))).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    expect(reads).toBe(2);
  });

it('hands a send failure to the same conversation after its drawer remounts', async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => { release = resolve; });
    const { requests } = setup(async (request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      await held;
      return { status: 503, statusText: 'Service Unavailable', body: { error: 'send failed after remount' } };
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('keep this failure visible');
    await waitFor(() => expect(requests.some((request) => request.path.endsWith('/messages'))).toBe(true));

    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(screen.getByRole('button', { name: /Conversation Assistant/ }));
    expect(messageField().getAttribute('contenteditable')).toBe('false');
    await act(async () => { release(); await held; });
    /* A 503 may follow a stored message: unconfirmed, and in no words of the answer's. */
    expect(within(await screen.findByRole('alert')).getByText('Send result unconfirmed.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Conversation Planner chat' }));
    expect(screen.queryByText('Send result unconfirmed.')).toBeNull();
  });

it('keeps the message in the composer when the runtime was superseded, and re-sends it', async () => {
    let refuse = true;
    const { requests } = setup((request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      if (refuse) {
        refuse = false;
        return {
          status: 409,
          statusText: 'Conflict',
          body: {
            error: 'this runtime is no longer the card\'s; your message was not stored — send it again',
            code: 'planner_harness_runtime_superseded',
          },
        };
      }
      return ok(sessionInput(pathCardId(request.path)));
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('reconcile the ledger');

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeNull());
    fireEvent.click(await screen.findByRole('button', { name: 'Restore to input' }));
    await waitFor(() => expect(messageField().textContent).toBe('reconcile the ledger'));

    const before = requests.filter((request) => request.path.endsWith('/messages')).length;
    await act(async () => {
      fireEvent.keyDown(messageField(), { key: 'Enter' });
      await Promise.resolve();
    });
    await waitFor(() =>
      expect(requests.filter((request) => request.path.endsWith('/messages')).length)
        .toBe(before + 1));
    await waitFor(() => expect(messageField().textContent).toBe(''));
  });

it('keeps the message in the composer when the harness is dormant', async () => {
    setup((request) => request.path.endsWith('/messages')
      ? {
        status: 409,
        statusText: 'Conflict',
        body: {
          error: "This conversation's session can't be resumed; start a fresh session (history is kept)",
          code: 'planner_harness_dormant',
        },
      }
      : undefined);

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('the sentence a dormant harness must not eat');

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeNull());
    fireEvent.click(await screen.findByRole('button', { name: 'Restore to input' }));
    await waitFor(() =>
      expect(messageField().textContent).toBe('the sentence a dormant harness must not eat'));
  });

it('leaves the field empty when the send failed without saying the text was refused', async () => {
    setup((request) => request.path.endsWith('/messages')
      ? { status: 503, statusText: 'Service Unavailable', body: { code: 'unavailable', error: 'busy' } }
      : undefined);

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('the ledger again');

    /* The premise: the failure really was reported, so an empty field is not
       an unanswered request being read as a decision. */
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeNull());
    expect(messageField().textContent).toBe('');
  });

it('does not put a refused sentence into the conversation the reader walked to', async () => {
    const held = new Map<string, () => void>();
    setup(async (request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      const cardId = pathCardId(request.path);
      await new Promise<void>((resolve) => { held.set(cardId, resolve); });
      return {
        status: 409,
        statusText: 'Conflict',
        body: {
          error: 'this runtime is no longer the card\'s; your message was not stored — send it again',
          code: 'planner_harness_runtime_superseded',
        },
      };
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await screen.findByRole('complementary', { name: 'Assistant' });
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('sentence meant for assistant');
    await waitFor(() => expect(held.has(ASSISTANT_CARD.id)).toBe(true));

    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Conversation Planner chat' }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));

    await act(async () => { held.get(ASSISTANT_CARD.id)?.(); await Promise.resolve(); });
    await act(async () => { await Promise.resolve(); });

    /* The premise: the refusal really was delivered to this store, which is
       what the error line being absent must not be allowed to stand for. */
    expect(held.has(ASSISTANT_CARD.id)).toBe(true);
    expect(messageField().textContent).toBe('');
    expect(screen.queryByRole('alert')).toBeNull();
  });

it('[F4] keeps a typed refusal in the composer alone, across reopening and a newer draft', async () => {
    let refuse = true;
    const { requests } = setup((request) => request.path.endsWith('/messages') && refuse
      ? failure(409, 'planner_harness_runtime_superseded', 'Your message was not stored') : undefined);
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('Original typed refusal');
    fireEvent.click(await screen.findByRole('button', { name: 'Restore to input' }));
    await waitFor(() => expect(messageField().textContent).toBe('Original typed refusal'));
    /* Once, in the composer: no copy of it is drawn in the thread. */
    expect(within(drawerElement()).getAllByText('Original typed refusal', TRANSCRIPT_TEXT)).toHaveLength(1);
    expect(drawerElement().querySelector('[data-nc-turn="you"]')).toBeNull();
    expect(screen.queryByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Restore to input' })).toBeNull();
    await typeInto(messageField(), 'A newer unsent draft');
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().textContent).toBe('A newer unsent draft'));
    expect(within(drawerElement()).queryByText('Original typed refusal', TRANSCRIPT_TEXT)).toBeNull();
    refuse = false;
    await act(async () => { fireEvent.keyDown(messageField(), { key: 'Enter' }); await Promise.resolve(); });
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(messageField().textContent).toBe('');
    expect(requests.filter((request) => request.path.endsWith('/messages')).map((request) => request.body))
      .toEqual([expect.objectContaining({ content: 'Original typed refusal' }), expect.objectContaining({ content: 'A newer unsent draft' })]);
  });

it('[F4] edits a rejected message without replaying it before an explicit send', async () => {
    const { requests } = setup((request) => request.path.endsWith('/messages')
      ? failure(429, 'rate_limited', 'Wait a moment') : undefined);
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('Original rejected message');
    await screen.findByRole('alert');
    fireEvent.click(screen.getByRole('button', { name: 'Restore to input' }));
    expect(messageField().textContent).toBe('Original rejected message');
    expect(messageField().getAttribute('contenteditable')).toBe('true');
    expect(screen.queryByRole('alert')).toBeNull();
    expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(1);
    await write('Revised request');
    await screen.findByRole('alert');
    expect(requests.filter((request) => request.path.endsWith('/messages')).map((request) => request.body))
      .toEqual([expect.objectContaining({ content: 'Original rejected message' }), expect.objectContaining({ content: 'Revised request' })]);
  });

it.each([
    [401, 'session_expired'], [403, 'forbidden'], [429, 'rate_limited'],
  ] as const)('[F5] keeps a dropped answer unknown when the retry is answered %s', async (status, code) => {
    const text = `Lost, then ${status}`;
    let attempts = 0;
    const { requests } = setup((request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      attempts += 1;
      if (attempts === 1) throw new Error('response dropped');
      return attempts === 2 ? failure(status, code, 'Answered without handling it') : inputAccepted();
    });
    const inputs = () => requests.filter((request) => request.path.endsWith('/messages'));
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write(text);
    /* The first attempt may have been stored; an answer to the second does not say otherwise. */
    expect((await screen.findByRole('alert')).textContent).toContain('Send result unconfirmed');
    expect(screen.queryByRole('button', { name: 'Restore to input' })).toBeNull();
    expect(inputs()).toHaveLength(2);
    const key = inputs()[0]?.headers?.['Idempotency-Key'];
    expect(key).toBeTruthy();
    expect(inputs()[1]?.headers?.['Idempotency-Key']).toBe(key);

    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(inputs()).toHaveLength(3));
    expect(inputs()[2]?.headers?.['Idempotency-Key']).toBe(key);
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  });

it.each([
    [401, 'session_expired'], [403, 'forbidden'], [429, 'rate_limited'], [409, 'planner_harness_dormant'],
  ] as const)('[F5] keeps a spent unknown send unknown when its Try again is answered %s %s', async (status, code) => {
    const text = `Spent, then ${code}`;
    let answer: 'drop' | 'refuse' | 'accept' = 'drop';
    const { requests } = setup((request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      if (answer === 'drop') throw new Error('response dropped');
      return answer === 'refuse' ? failure(status, code, 'Answered without storing it') : inputAccepted();
    });
    const inputs = () => requests.filter((request) => request.path.endsWith('/messages'));
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write(text);
    expect((await screen.findByRole('alert')).textContent).toContain('Send result unconfirmed');
    expect(inputs()).toHaveLength(SEND_RETRIES + 1);
    const key = inputs()[0]?.headers?.['Idempotency-Key'];

    /* Try again resumes the op: whatever this answer says, an earlier attempt may have been stored. */
    answer = 'refuse';
    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(inputs()).toHaveLength(SEND_RETRIES + 2));
    await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Send result unconfirmed'));
    expect(screen.queryByRole('button', { name: 'Restore to input' })).toBeNull();
    expect(inputs().every((request) => request.headers?.['Idempotency-Key'] === key)).toBe(true);

    answer = 'accept';
    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(inputs().at(-1)?.headers?.['Idempotency-Key']).toBe(key);
  });

it('[F5] reconciles a resumed send whose first attempt was stored and drained, also after reopening', async () => {
    const text = 'Stored before the answer was lost';
    const earlier = sessionItem(ASSISTANT_CARD.id, 1, 'Earlier answer');
    let rows = [earlier];
    let accept = false;
    let title: string | null = null;
    const { client } = setup((request) => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title })]]));
      if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), rows));
      if (!request.path.endsWith('/messages')) return undefined;
      if (!accept) throw new Error('response dropped');
      return inputAccepted();
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await screen.findByText('Earlier answer');
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write(text);
    await screen.findByRole('alert');
    /* The kernel had stored it after all; the queue drained into the transcript. */
    title = text;
    rows = [earlier, sessionItem(ASSISTANT_CARD.id, 2, text, 'user'), sessionItem(ASSISTANT_CARD.id, 3, 'Reply to it')];
    await act(async () => { await client.invalidateQueries({ queryKey: cachedHistoryKey(client, ASSISTANT_CARD.id) }); });
    await screen.findByText('Reply to it');

    accept = true;
    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    await waitFor(() => expect(within(drawerElement()).getAllByText(text, TRANSCRIPT_TEXT)).toHaveLength(1));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));

    await waitFor(() => expect(client.getQueryData<import('../../../../core/api/perga-v1.ts').TrackAggregateSnapshotDto>(queryKeys.trackDetail('w1'))?.sessions.filter(session => !session.isLead)).toMatchObject([{ title: text }]));
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Stored before/ }));
    await screen.findByText('Reply to it');
    expect(within(drawerElement()).getAllByText(text, TRANSCRIPT_TEXT)).toHaveLength(1);
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  });

it('[#2068] keeps a composer draft when Try again resends a rejected message', async () => {
    let attempts = 0;
    const { requests } = setup((request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      attempts += 1;
      return attempts === 1 ? failure(429, 'rate_limited', 'Too many requests.') : undefined;
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('Rejected the first time');
    expect(within(await screen.findByRole('alert')).getByText('Message not sent. Too many requests.')).toBeTruthy();
    await typeInto(messageField(), 'A draft in progress');
    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(2));
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
    expect(messageField().textContent).toBe('A draft in progress');
  });

it('[F5] leaves the footer as it was when Try again is pressed offline', async () => {
    const { requests } = setup((request) => {
      if (request.path.endsWith('/messages')) throw new Error('response dropped');
      return undefined;
    });
    const inputs = () => requests.filter((request) => request.path.endsWith('/messages'));
    await sendUntilSpent('Spent online, retried offline');
    expect(inputs()).toHaveLength(SEND_RETRIES + 1);
    act(() => { onlineManager.setOnline(false); });
    fireEvent.click(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    expect(within(screen.getByRole('alert')).getByText('Send result unconfirmed.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Restore to input' })).toBeNull();
    expect(inputs()).toHaveLength(SEND_RETRIES + 1);
  });

it('send: says only that delivery is unconfirmed', async () => {
      setup((request) => request.path.endsWith('/messages') ? timedOut() : undefined);
      await sendUntilSpent('Lost on the way');
      expect(within(screen.getByRole('alert')).getByText('Send result unconfirmed.')).toBeTruthy();
      expect(chatText()).not.toMatch(CONNECTIVITY);
    });

it('model select: says the change is unconfirmed', async () => {
      setup((request) => {
        if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap(['codex'], ['gpt-5']));
        if (request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path)) return timedOut();
        return undefined;
      });
      await openAssistant();
      fireEvent.click(within(drawerElement()).getByRole('button', { name: /^Model:/ }));
      fireEvent.click(await screen.findByRole('radio', { name: /^gpt-5/ }));
      expect((await screen.findByRole('alert')).textContent).toContain('The model change is unconfirmed.');
      expect(chatText()).not.toMatch(CONNECTIVITY);
    });

it('never carries a delivered message the server has not shown yet into the next conversation', async () => {
    storedSetup();
    await openAssistantWithHistory();
    await typeInto(messageField(), 'Delivered words');
    await submit();
    await settleFor(50);
    await pickPlanner();
    await settleFor(50);
    expect(drawerElement().querySelector('[data-nc-turn="you"]')).toBeNull();
    expect(messageField().getAttribute('contenteditable')).toBe('true');
  });

it('keeps a typed draft through closing and reopening its conversation', async () => {
    storedSetup();
    await openAssistantWithHistory();
    await typeInto(messageField(), 'Half a thought');
    await reopenAssistant();
    await waitFor(() => expect(messageField().textContent).toBe('Half a thought'));
    /* The draft withholds Edit: nothing is ever merged into it. */
    expect(screen.queryByRole('button', { name: /^Edit message/ })).toBeNull();
  });

it('lets another conversation be written while one conversation’s send is out', async () => {
    let answer!: () => void;
    const answered = new Promise<void>((done) => { answer = done; });
    const { requests } = scriptedSetup(() => storedTurn(), async request => {
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title: 'Original prompt' })]]));
      if (request.path.endsWith('/messages')) { await answered; return inputAccepted(); }
      return undefined;
    });
    await openAssistantWithHistory(/Conversation Original prompt/);
    await typeInto(messageField(), 'Sent from the assistant');
    await submit();
    await waitFor(() => expect(inputBodies(requests)).toHaveLength(1));
    await pickPlanner();
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await typeInto(messageField(), 'First words');
    await typeInto(messageField(), 'Second words');
    await pickAssistant();
    await pickPlanner();
    await waitFor(() => expect(messageField().textContent).toBe('Second words'));
    await act(async () => { answer(); await answered; });
  });

it('[#2068] gives a refused send its words back in its own conversation when the refusal lands while another is shown', async () => {
    let release!: () => void;
    const held = new Promise<void>((done) => { release = done; });
    const { requests } = storedSetup(async () => {
      await held;
      return failure(409, 'planner_harness_runtime_superseded', 'Your message was not stored; send it again.');
    });
    await openAssistantWithHistory();
    await typeInto(messageField(), 'Words the server refused');
    await submit();
    await waitFor(() => expect(inputBodies(requests)).toHaveLength(1));
    await pickPlanner();
    await act(async () => { release(); await held; });
    await settleFor(20);
    expect(messageField().textContent).toBe('');
    expect(screen.queryByRole('alert')).toBeNull();
    await pickAssistant();
    expect((await screen.findByRole('alert')).textContent).toContain('Message not sent. Your message was not stored; send it again.');
    fireEvent.click(screen.getByRole('button', { name: 'Restore to input' }));
    await waitFor(() => expect(messageField().textContent).toBe('Words the server refused'));
    expect(screen.queryByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ })).toBeNull();
    /* The next press sends the words again as a new message, and nothing of the refused one is left to drop. */
    await submit();
    await waitFor(() => expect(inputBodies(requests)).toHaveLength(2));
    expect(inputBodies(requests)[1]).toEqual({ content: 'Words the server refused', mode: 'default', expectedEpoch: 1 });
  });

it('[G4] starts a conversation on a track that has no planner card', async () => {
    /* Stateful on purpose: a real server lists the row it just minted, and the create
           invalidates this very list. */
    const minted: Row[] = [];
    const { requests, router } = setup((request) => {
      if (request.method === 'POST' && request.path === BARE_CONVERSATIONS) {
        const row = { ...derivedRow('w2', request), title: 'what is in this repo?' };
        minted.push(row);
        return created(row, request);
      }
      return request.path === '/api/v1/tracks/w2' ? ok(fixtureSnapshot(BARE_TRACK, [...minted])) : undefined;
    });
    await act(async () => { await router.navigate({ to: '/track/w2' }); });
    await screen.findByRole('button', { name: 'New conversation' });
      await waitFor(() => expect(screen.queryAllByRole('button', { name: /^Conversation / })).toHaveLength(0));
    await openDraft();
    /* Nothing is minted by opening the drawer; the card is minted by the first message. */
    expect(creates(requests, BARE_CONVERSATIONS)).toHaveLength(0);
    await write('what is in this repo?');
    await waitFor(() => expect(creates(requests, BARE_CONVERSATIONS)).toHaveLength(1));
    const [post] = creates(requests, BARE_CONVERSATIONS);
    expect(post?.body).toMatchObject({ prompt: 'what is in this repo?', provider: 'codex' });
    expect(post?.headers?.['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    /* The drawer moves off the draft onto the row the derived id names. */
    await screen.findByRole('complementary', { name: 'what is in this repo?' });
    expect(requests.filter((request) => request.method === 'POST'
      && request.path.endsWith('/sessions') && request.path !== BARE_CONVERSATIONS)).toEqual([]);
    /* The message travelled with the POST, so nothing re-sends it afterwards. */
    expect(requests.filter((request) => request.path.endsWith('/messages'))).toEqual([]);
  });

it('keeps each failed draft key across track route remounts', async () => {
    const attempts = new Map<string, number>();
    const { requests, router } = setup((request) => {
      if (request.method !== 'POST'
        || (request.path !== CONVERSATIONS && request.path !== BARE_CONVERSATIONS)) return undefined;
      const attempt = (attempts.get(request.path) ?? 0) + 1;
      attempts.set(request.path, attempt);
      if (attempt === 1) return failure(500, 'internal', 'boom');
      return created(derivedRow(request.path === CONVERSATIONS ? 'w1' : 'w2', request), request);
    });

    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('words for track one');
    await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ });

    await act(async () => { await router.navigate({ to: '/track/w2' }); });
      await waitFor(() => expect(screen.queryAllByRole('button', { name: /^Conversation / })).toHaveLength(0));
    await openDraft();
    await write('words for track two');
    await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ });

    await act(async () => { await router.navigate({ to: '/track/w1' }); });
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    expect(screen.getByText('words for track one')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));

    await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(2));
    const [first, retry] = creates(requests, CONVERSATIONS);
    expect(first?.headers?.['Idempotency-Key']).toBeDefined();
    expect(retry?.headers?.['Idempotency-Key']).toBe(first?.headers?.['Idempotency-Key']);
    expect([first?.body, retry?.body]).toEqual([
      { prompt: 'words for track one', provider: 'codex', title: 'words for track one' },
      { prompt: 'words for track one', provider: 'codex', title: 'words for track one' },
    ]);

    await act(async () => { await router.navigate({ to: '/track/w2' }); });
      await waitFor(() => expect(screen.queryAllByRole('button', { name: /^Conversation / })).toHaveLength(0));
    await openDraft();
    expect(screen.getByText('words for track two')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));

    await waitFor(() => expect(creates(requests, BARE_CONVERSATIONS)).toHaveLength(2));
    const [bareFirst, bareRetry] = creates(requests, BARE_CONVERSATIONS);
    expect(bareFirst?.headers?.['Idempotency-Key']).toBeDefined();
    expect(bareRetry?.headers?.['Idempotency-Key'])
      .toBe(bareFirst?.headers?.['Idempotency-Key']);
  }, 10_000);

it('keeps an in-flight draft locked across a route remount', async () => {
    let releaseFirst!: (response: ApiTransportResponse) => void;
    const firstCreate = new Promise<ApiTransportResponse>((resolve) => { releaseFirst = resolve; });
    let landed: Row | null = null;
    const { requests, router } = setup((request) => {
      if (request.path === '/api/v1/tracks/w1' && request.method === 'GET' && landed !== null) {
        return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow(), landed]]));
      }
      if (request.path !== CONVERSATIONS || request.method !== 'POST') return undefined;
      return creates(requests, CONVERSATIONS).length === 1
        ? firstCreate
        : created(derivedRow('w1', request), request);
    });

    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('words still in flight');
    await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(1));

    await act(async () => { await router.navigate({ to: '/' }); });
    await act(async () => { await router.navigate({ to: '/track/w1' }); });
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();

    expect(screen.getByText('words still in flight')).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Message composer' }).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('Sending…')).toBeNull();
    expect(messageField().getAttribute('contenteditable')).toBe('false');
    await write('edited while the first request is pending');
    expect(creates(requests, CONVERSATIONS)).toHaveLength(1);

    const first = creates(requests, CONVERSATIONS)[0];
    landed = { ...derivedRow('w1', first), title: 'words still in flight' };
    await act(async () => {
      releaseFirst(created(landed, first));
      await firstCreate;
    });
    await screen.findByRole('complementary', { name: 'words still in flight' });
    expect(creates(requests, CONVERSATIONS)).toHaveLength(1);
  });

it('unlocks the fresh key after an exhausted attempt is rekeyed', async () => {
    const { requests } = setup((request) => {
      if (request.path !== CONVERSATIONS || request.method !== 'POST') return undefined;
      return creates(requests, CONVERSATIONS).length === 1
        ? failure(409, 'idempotency_key_exhausted', 'this key is used up')
        : created(derivedRow('w1', request), request);
    });

    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('retry after exhaustion');
    const retry = await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ });
    expect(retry.hasAttribute('disabled')).toBe(false);
    fireEvent.click(retry);

    await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(2));
    const [exhausted, fresh] = creates(requests, CONVERSATIONS);
    expect(fresh?.headers?.['Idempotency-Key'])
      .not.toBe(exhausted?.headers?.['Idempotency-Key']);
  });

it('offers no retry after a key-reused answer, only a new conversation', async () => {
    const { requests } = setup((request) => {
      if (request.path !== CONVERSATIONS || request.method !== 'POST') return undefined;
      return creates(requests, CONVERSATIONS).length === 1
        ? failure(409, 'idempotency_key_reused', 'this key was already used for another first message')
        : created(derivedRow('w1', request), request);
    });

    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('a key that is spent');
    await screen.findByText('this key was already used for another first message');
    expect(screen.queryByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ })).toBeNull();
    fireEvent.click(await screen.findByRole('button', { name: 'Send as a new conversation' }));

    await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(2));
    const [reused, fresh] = creates(requests, CONVERSATIONS);
    expect(fresh?.headers?.['Idempotency-Key'])
      .not.toBe(reused?.headers?.['Idempotency-Key']);
  });

it('[#2175] says a new conversation was not started when its create is refused before it is sent', async () => {
    const { client, requests } = setup((request) => {
      if (request.path !== CONVERSATIONS || request.method !== 'POST') return undefined;
      return creates(requests, CONVERSATIONS).length === 1
        ? failure(409, 'idempotency_key_reused', 'this key was already used for another first message')
        : created(derivedRow('w1', request), request);
    });
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('a key that is spent');
    const sendAsNew = await screen.findByRole('button', { name: 'Send as a new conversation' });
    await waitFor(() => expect(sendAsNew.hasAttribute('disabled')).toBe(false));
    /* Offline from the moment the create's mutation is built: its own guard refuses it, after every earlier check passed. */
    const unsubscribe = client.getMutationCache().subscribe((event) => { if (event.type === 'added') onlineManager.setOnline(false); });
    try {
      fireEvent.click(sendAsNew);
      await waitFor(() => expect(screen.getByRole('alert').textContent).toContain(CONVERSATION_CREATE_TEXT.refused));
    } finally {
      unsubscribe();
      onlineManager.setOnline(true);
    }
    expect(creates(requests, CONVERSATIONS)).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Send as a new conversation' })).toBeNull();
    fireEvent.click(await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ }));
    await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(2));
    const [reused, fresh] = creates(requests, CONVERSATIONS);
    expect(fresh?.headers?.['Idempotency-Key']).not.toBe(reused?.headers?.['Idempotency-Key']);
  });

it('[G5] lets no stale request re-open a composer whose own message is still in flight', async () => {
    const held = new Map<string, () => void>();
    const release = (cardId: string) => held.get(cardId)?.();
    const { requests } = setup(async (request) => {
      if (!request.path.endsWith('/messages')) return undefined;
      const cardId = pathCardId(request.path);
      await new Promise<void>((resolve) => { held.set(cardId, resolve); });
      return { status: 503, statusText: 'Service Unavailable', body: { code: 'unavailable', error: 'busy' } };
    });
    /* Conversation A, one message, request still out. */
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    await screen.findByRole('complementary', { name: 'Assistant' });
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('the first conversation speaks');
    await waitFor(() => expect(held.has(ASSISTANT_CARD.id)).toBe(true));

    /* Conversation B, on the same panel instance, while A's send is still out in A's outbox. */
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    fireEvent.click(screen.getByRole('button', { name: 'Conversation Planner chat' }));
    await screen.findByRole('complementary', { name: 'Planner chat' });
    await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
    await write('the second conversation speaks');
    await waitFor(() => expect(held.has(PLANNER_CARD.id)).toBe(true));
    const sends = () => requests.filter((request) =>
      request.path === `/api/v1/sessions/${PLANNER_CARD.id}/messages`);
    expect(sends()).toHaveLength(1);

    /* A's request lands now, answering for a conversation nobody is looking at. */
    await act(async () => { release(ASSISTANT_CARD.id); await Promise.resolve(); });
    await act(async () => { await Promise.resolve(); });

    /* B's message is still unanswered, so this third message does not go out. */
    await write('and a third the store must refuse');
    await act(async () => { await Promise.resolve(); });
    expect(sends()).toHaveLength(1);
    /* Nor did A's failure surface under B's composer. */
    expect(screen.queryByText(/busy|Could not send/)).toBeNull();
  });

it('does not paint the empty state while the first page is still loading', async () => {
    setup((request) => isHistoryPath(request.path)
      ? new Promise(() => undefined)
      : undefined);
    fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
    const drawer = await screen.findByRole('complementary', { name: 'Assistant' });
    expect(within(drawer).getByText('Loading conversation…', TRANSCRIPT_TEXT)).toBeTruthy();
    expect(drawer.querySelector('[data-nc-thread-empty]')).toBeNull();
  });

it('keeps the draft and reports the reason when the create is refused', async () => {
    setup((request) => request.method === 'POST' && request.path === CONVERSATIONS
      ? failure(400, 'invalid_request', 'That message was refused.')
      : undefined);
    await screen.findByRole('button', { name: 'Conversation Planner chat' });
    await openDraft();
    await write('refused words');
    expect((await screen.findByRole('alert')).textContent).toContain('That message was refused.');
    expect(screen.getByRole('complementary', { name: 'Untitled' })).toBeTruthy();
  });

it('a lost answer: says the create is unconfirmed and keeps the words and Try again', async () => {
      setup(lostCreate);
      await screen.findByRole('button', { name: 'Conversation Planner chat' });
      await openDraft();
      await write('words whose answer is lost');
      await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ });
      await says(CONVERSATION_CREATE_TEXT.unknown);
      expect(screen.getByText('words whose answer is lost')).toBeTruthy();
    });

it('offline at the press: says the conversation was not started, and sends nothing', async () => {
      const { requests } = setup();
      await screen.findByRole('button', { name: 'Conversation Planner chat' });
      await openDraft();
      onlineManager.setOnline(false);
      try {
        await write('words typed offline');
        await says(CONVERSATION_CREATE_TEXT.refused);
        expect(creates(requests, CONVERSATIONS)).toHaveLength(0);
        expect(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ })).toBeTruthy();
      } finally { onlineManager.setOnline(true); }
    });

it('offline after a lost answer: stays unconfirmed, since the first attempt may have started it', async () => {
      const { requests } = setup(lostCreate);
      await screen.findByRole('button', { name: 'Conversation Planner chat' });
      await openDraft();
      await write('words sent once');
      const retry = await screen.findByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ });
      onlineManager.setOnline(false);
      try {
        fireEvent.click(retry);
        await says(CONVERSATION_CREATE_TEXT.unknown);
        expect(creates(requests, CONVERSATIONS)).toHaveLength(1);
      } finally { onlineManager.setOnline(true); }
    });

it.each(['429', 'transport'])('[F5] keeps a %s failure and its distinct echo when an unacknowledged send meets an old equal Unknown message', async (mode) => {
  const text = 'repeat this instruction';
  const first = sessionItem(ASSISTANT_CARD.id, 1, 'Earlier different instruction', 'user');
  const oldEqual = sessionItem(ASSISTANT_CARD.id, 2, text, 'user');
  let rows = [first];
  const { client, requests } = setup((request) => {
    if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), rows));
    if (request.path.endsWith('/messages')) {
      if (mode === 'transport') throw new Error('Connection lost before request reached server');
      return failure(429, 'rate_limited', 'Request rejected before acceptance');
    }
    return undefined;
  });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  await write(text);
  expect((await screen.findByRole('alert')).textContent).toContain(mode === '429' ? 'Message not sent' : 'Send result unconfirmed');
  // The original read was stale; a refresh reveals a pre-existing equal row.
  // Its server timestamp is 2, earlier than this request's Date.now().
  rows = [first, oldEqual, sessionItem(ASSISTANT_CARD.id, 3, 'Previously completed response')];
  await act(async () => { await client.invalidateQueries({ queryKey: cachedHistoryKey(client, ASSISTANT_CARD.id) }); });
  const sent = mode === '429' ? 1 : SEND_RETRIES + 1;
  expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(sent);
  await screen.findByText('Previously completed response');
  expect(screen.getByRole('alert').textContent).toContain(mode === '429' ? 'Message not sent' : 'Send result unconfirmed');
  expect(screen.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/ })).toBeTruthy();
  /* Neither failure has an acknowledged entry ID. An old equal Unknown message cannot identify it.
     Keep both and retain Try again under the original key. */
  expect(within(drawerElement()).getAllByText(text, TRANSCRIPT_TEXT)).toHaveLength(2);
  expect(requests.filter((request) => request.path.endsWith('/messages'))).toHaveLength(sent);
});

it('restores the open conversation after visiting another Track and remembers an explicit close', async () => {
  const { router } = setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await screen.findByRole('button', { name: 'Close conversation' });
  await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w2' } }); });
  await screen.findAllByRole('heading', { name: 'Bare track', level: 1 });
  expect(screen.queryByRole('button', { name: 'Close conversation' })).toBeNull();
  await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w1' } }); });
  fireEvent.click(await screen.findByRole('button', { name: 'Close conversation' }));
  await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w2' } }); });
  await screen.findAllByRole('heading', { name: 'Bare track', level: 1 });
  await act(async () => { await router.navigate({ to: '/track/$trackId', params: { trackId: 'w1' } }); });
  await screen.findByRole('button', { name: 'Conversation Assistant' });
  expect(screen.queryByRole('button', { name: 'Close conversation' })).toBeNull();
});

it('restores conversation selection across a fresh router without persisting its contents', async () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); } };
  const first = setup(undefined, storage);
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await screen.findByRole('button', { name: 'Close conversation' });
  cleanup();
  first.client.clear();
  setup(undefined, storage);
  expect(await screen.findByRole('complementary', { name: 'Assistant' })).toBeTruthy();
  const saved = [...values.values()].map(value => JSON.parse(value) as string);
  expect(saved).toContain(ASSISTANT_CARD.id);
  expect(saved.every(value => value === ASSISTANT_CARD.id || /^\d+$/.test(value))).toBe(true);
});

it('does not fetch a stored conversation absent from the current Track rows', async () => {
  const values = new Map<string, string>();
  const storage = { getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); } };
  createUiPreferences(storage).setConversation('w1', 'foreign-or-deleted-card');
  const { requests } = setup(undefined, storage);
  await screen.findByRole('button', { name: 'Conversation Assistant' });
  expect(screen.queryByRole('button', { name: 'Close conversation' })).toBeNull();
  expect(requests.some((request) => request.path.includes('foreign-or-deleted-card'))).toBe(false);
});

it('selects a model before the first conversation message and sends it atomically', async () => {
  const { requests } = setup(request => {
    if (request.path === '/api/v1/bootstrap') return ok(fixtureBootstrap());
    if (request.path === '/api/v1/bootstrap?provider=codex') return ok(fixtureBootstrap(['codex'], ['gpt-5']));
    if (request.method === 'POST' && request.path === CONVERSATIONS) return created(derivedRow('w1', request), request);
    return undefined;
  });
  await openDraft();
  fireEvent.click(await screen.findByRole('button', { name: /^Model:/ }));
  fireEvent.click(await screen.findByRole('radio', { name: /^gpt-5/ }));

  expect(creates(requests, CONVERSATIONS)).toHaveLength(0);
  await write('Use this model from the start');
  await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(1));
  expect(creates(requests, CONVERSATIONS)[0].body).toMatchObject({ prompt: 'Use this model from the start', provider: 'codex', model: 'gpt-5' });
  expect(requests.filter(request => request.method === 'PATCH' && request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path))).toEqual([]);
});

it('uses a spinner while a closed conversation runs, a blue unread dot on completion, and no dot after reading', async () => {
  /* Unread follows `lastTurnCompletedAt`: `updatedAt` also moves when a message is
       queued. Working follows the kernel's per-card verdict, not the row's session state. */
  let rows = [assistantRow({ updatedAtMs: 30, lastNotifiableTurnAtMs: 30 })];
  const { client } = setup(request => {
    if (request.method === 'GET' && request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...rows]));
    return undefined;
  }, undefined, receiptKernel());
  const refetch = () => act(async () => {
    await client.invalidateQueries({ queryKey: queryKeys.trackDetail('w1') });
    await client.invalidateQueries({ queryKey: ['track', 'w1'] });
  });
  const indicator = () => screen.getByRole('button', { name: /^Conversation Assistant(?:,|$)/ }).closest('li')?.querySelector('[data-nc-activity]');
  await screen.findByRole('button', { name: /^Conversation Assistant(?:,|$)/ });
  expect(indicator()?.getAttribute('data-nc-activity')).toBe('unread');
  fireEvent.click(screen.getByRole('button', { name: /^Conversation Assistant(?:,|$)/ }));
  await waitFor(() => expect(indicator()).toBeNull());
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  rows = [{ ...assistantRow(), status: 'running', updatedAtMs: 30, lastNotifiableTurnAtMs: 30 }];
  await refetch();
  await waitFor(() => expect(screen.getByRole('button', { name: /^Conversation Assistant/ }).closest('li')
    ?.querySelector('[data-nc-activity]')?.getAttribute('data-nc-activity')).toBe('working'));
  // A newer `updatedAt` alone (the reader queued something) is not unread…
  rows = [{ ...assistantRow(), status: 'idle', updatedAtMs: 30, lastNotifiableTurnAtMs: 30 }];
  await refetch();
  await waitFor(() => expect(indicator()).toBeNull());
  // …a newer completion is.
  rows = [{ ...assistantRow(), status: 'idle', updatedAtMs: 50, lastNotifiableTurnAtMs: 50 }];
  await refetch();
  await waitFor(() => expect(indicator()?.getAttribute('data-nc-activity')).toBe('unread'));
  fireEvent.click(screen.getByRole('button', { name: /^Conversation Assistant(?:,|$)/ }));
  await waitFor(() => expect(indicator()).toBeNull());
});

it('keeps the first-message model fixed when a menu opened before sending is selected late', async () => {
  let settle!: (response: ApiTransportResponse) => void;
  const pending = new Promise<ApiTransportResponse>(resolve => { settle = resolve; });
  const { requests } = setup(request => {
    if (request.path === '/api/v1/bootstrap') return ok(fixtureBootstrap());
    if (request.path === '/api/v1/bootstrap?provider=codex') return ok(fixtureBootstrap(['codex'], ['gpt-5']));
    if (request.method === 'POST' && request.path === CONVERSATIONS) return pending;
    return undefined;
  });
  await openDraft();
  const modelPicker = await screen.findByRole('button', { name: /^Model:/ });
  await waitFor(() => expect(modelPicker.hasAttribute('disabled') || modelPicker.getAttribute('aria-disabled') === 'true').toBe(false));
  fireEvent.click(modelPicker);
  fireEvent.click(await screen.findByRole('radio', { name: 'gpt-5' }));
  await write('Keep my choice');
  await waitFor(() => expect(creates(requests, CONVERSATIONS)).toHaveLength(1));
  try {
    fireEvent.click(screen.getByRole('radio', { name: /^Default/, hidden: true }));
    expect(screen.getByRole('button', { name: /^Model:/ }).getAttribute('aria-label')).toMatch(/^Model: (?:codex|Codex) gpt-5$/);
  } finally {
    await act(async () => { settle(failure(500, 'internal', 'Try again')); await pending; });
  }
});

it('keeps new replies unread while reopening cached history is still loading', async () => {
  let rows = [assistantRow({ updatedAtMs: 30, lastNotifiableTurnAtMs: 30 })];
  let holdHistory = false;
  let release!: (response: ApiTransportResponse) => void;
  const pending = new Promise<ApiTransportResponse>(resolve => { release = resolve; });
  const { client, requests } = setup(request => {
    if (request.method === 'GET' && request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...rows]));
    if (isHistoryPath(request.path)) return holdHistory ? pending
      : ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 20, 'Previously read answer.')]));
    return undefined;
  }, undefined, receiptKernel());
  const row = () => screen.getByRole('button', { name: /^Conversation Assistant(?:,|$)/ });
  const unread = () => row().closest('li')?.querySelector('[data-nc-activity="unread"]');
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await waitFor(() => expect(unread()).toBeNull());
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  rows = [{ ...assistantRow(), updatedAtMs: 50, lastNotifiableTurnAtMs: 50 }];
  await act(async () => { await client.invalidateQueries({ queryKey: queryKeys.trackDetail('w1') }); });
  await waitFor(() => expect(unread()).toBeTruthy());
  const previousReads = requests.filter(request => isHistoryPath(request.path)).length;
  holdHistory = true;
  await act(async () => { await client.invalidateQueries({ queryKey: cachedHistoryKey(client, ASSISTANT_CARD.id) }); });
  fireEvent.click(row());
  try {
    await waitFor(() => expect(requests.filter(request => isHistoryPath(request.path)).length).toBeGreaterThan(previousReads));
    expect(unread()).toBeTruthy();
  } finally {
    await act(async () => { release(ok(historyPage(ASSISTANT_CARD.id, [sessionItem(ASSISTANT_CARD.id, 45, 'New completed answer.')]))); await pending; });
  }
  await waitFor(() => expect(unread()).toBeNull());
});

it('keeps a nonempty conversation read after closing when activity is newer than the last reply', async () => {
  setup(request => {
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title: 'Review receipt', updatedAtMs: 50, lastNotifiableTurnAtMs: 50 })]]));
    if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 20, 'Read this completed answer.')]));
    return undefined;
  });
  const row = () => screen.getByRole('button', { name: /^Conversation Review receipt(?:,|$)/ });
  const unread = () => row().closest('li')?.querySelector('[data-nc-activity="unread"]');
  fireEvent.click(await screen.findByRole('button', { name: /^Conversation Review receipt(?:,|$)/ }));
  await screen.findByText('Read this completed answer.');
  await waitFor(() => expect(unread()).toBeNull());
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  await waitFor(() => expect(unread()).toBeNull());
});

it('sends /compact to the selected conversation compaction endpoint without a chat input', async () => {
  const { requests } = setup((request) => request.path.endsWith('/control')
    ? ok(ASSISTANT_CARD) : undefined);
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await screen.findByRole('complementary', { name: 'Assistant' });
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  await completeAndSubmit('compact');
  await waitFor(() => expect(creates(requests, `/api/v1/sessions/${ASSISTANT_CARD.id}/control`)).toHaveLength(1));
  expect(creates(requests, `/api/v1/sessions/${ASSISTANT_CARD.id}/messages`)).toHaveLength(0);
});

it('compacts each selected conversation while another compaction request is pending', async () => {
  const a = assistantRow({ title: 'Compact A' });
  const b = assistantRow({ id: 'conv-assistant-2', title: 'Compact B' });
  const answers = new Map<string, (response: ApiTransportResponse) => void>();
  const { requests } = setup((request) => {
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[a, b]]));
    if (request.path.endsWith('/control')) {
      return new Promise<ApiTransportResponse>((resolve) => { answers.set(pathCardId(request.path), resolve); });
    }
    return undefined;
  });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Compact A' }));
  await screen.findByRole('complementary', { name: 'Compact A' });
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  await completeAndSubmit('compact');
  await waitFor(() => expect(creates(requests, `/api/v1/sessions/${a.id}/control`)).toHaveLength(1));
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Compact B' }));
  await screen.findByRole('complementary', { name: 'Compact B' });
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  await completeAndSubmit('compact');
  await waitFor(() => expect(creates(requests, `/api/v1/sessions/${b.id}/control`)).toHaveLength(1));
  act(() => { answers.get(a.id)?.(ok(a)); });
  expect(document.querySelector('[data-nc-conversation-action]')).toBeNull();
  act(() => { answers.get(b.id)?.(ok(b)); });
  await waitFor(() => expect(document.querySelector('[data-nc-conversation-action]')).toBeNull());
});

it('retires pending metadata after agent-only history on open and close', async () => {
  let title: string | null = 'First pending';
  const { client } = setup(request => {
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title })]]));
    if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 1, 'Reply to a system-only turn')]));
    return undefined;
  });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation First pending' }));
  await screen.findByText('Reply to a system-only turn');
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  await screen.findByRole('button', { name: 'Conversation First pending' });
  title = null;
  await act(async () => { await client.invalidateQueries({ queryKey: queryKeys.trackDetail('w1') }); });
  expect(client.getQueryData<import('../../../../core/api/perga-v1.ts').TrackAggregateSnapshotDto>(queryKeys.trackDetail('w1'))?.sessions.filter(session => !session.isLead)).toMatchObject([{ title: null }]);
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await screen.findByRole('complementary', { name: 'Assistant' });
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  await screen.findByRole('button', { name: 'Conversation Assistant' });
});

it('keeps first confirmed server metadata through close and a cold history read', async () => {
  let confirmed = false;
  let holdHistory = false;
  const { client } = setup(request => {
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ...[assistantRow({ title: confirmed ? 'First confirmed sentence' : null })]]));
    if (isHistoryPath(request.path)) return holdHistory ? new Promise(() => undefined)
      : ok(historyPage(conversationCardId(request.path), confirmed ? [sessionItem(ASSISTANT_CARD.id, 1, 'First confirmed sentence', 'user')] : []));
    if (request.path.endsWith('/messages')) {
      confirmed = true;
      return ok(sessionInput(ASSISTANT_CARD.id));
    }
    return undefined;
  });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
  await write('First confirmed sentence');
  await waitFor(() => expect(client.getQueryData<import('../../../../core/api/perga-v1.ts').TrackAggregateSnapshotDto>(queryKeys.trackDetail('w1'))?.sessions.filter(session => !session.isLead)).toMatchObject([{ title: 'First confirmed sentence' }]));
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  const named = await screen.findByRole('button', { name: 'Conversation First confirmed sentence' });
  holdHistory = true;
  client.removeQueries({ queryKey: cachedHistoryKey(client, ASSISTANT_CARD.id) });
  fireEvent.click(named);
  await screen.findByRole('complementary', { name: 'First confirmed sentence' });
});

async function openAssistant() {
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await waitFor(() => expect(messageField().getAttribute('contenteditable')).toBe('true'));
}
async function sendUntilSpent(text: string) {
  await openAssistant();
  await write(text);
  expect((await screen.findByRole('alert')).textContent).toContain('Send result unconfirmed');
}
const CONNECTIVITY = /Transport request failed|timed out|back online|连接恢复/;
const timedOut = (): never => { throw new DOMException('Request timed out.', 'TimeoutError'); };
const chatText = () => drawerElement().textContent;
const RAW_OR_CONNECTIVITY = /Transport request failed|timed out|schema|offline|reconnect|connection|Nothing was sent|连接/i;
const says = async (text: string) => {
  await waitFor(() => expect(within(screen.getByRole('alert')).getByText(text, { exact: true })).toBeTruthy());
  expect(screen.getByRole('alert').textContent).not.toMatch(RAW_OR_CONNECTIVITY);
};
const lostCreate = (request: ApiRequest) => {
  if (request.method === 'POST' && request.path === CONVERSATIONS) throw new Error('socket hang up');
  return undefined;
};
function storedTurn() {
  return [sessionItem(ASSISTANT_CARD.id, 91, 'Original prompt', 'user'), sessionItem(ASSISTANT_CARD.id, 92, 'Original answer')];
}
function storedSetup(input: () => ApiTransportResponse | undefined | Promise<ApiTransportResponse | undefined> = () => undefined) {
  return scriptedSetup(storedTurn, request => request.path.endsWith('/messages') ? input() : undefined);
}
function scriptedSetup(rows: () => SessionItemDto[], extra: Reply) {
  return setup(async request => await extra(request) ?? (isHistoryPath(request.path)
    ? ok(historyPage(pathCardId(request.path), pathCardId(request.path) === ASSISTANT_CARD.id ? rows() : [])) : undefined));
}
async function openAssistantWithHistory(name: string | RegExp = /Conversation Assistant/) {
  fireEvent.click(await screen.findByRole('button', { name }));
  await screen.findByText('Original answer');
}


describe('registry write-through', () => {
  const SCOPE = {
    id: 'w1', provider: 'codex' as const, title: 'Test track', cardId: ASSISTANT_CARD.id, cardTitle: null,
    updatedAt: 30, kind: 'agent' as const, isLead: false, state: 'idle' as const,
  };
  const ROWS: readonly Conversation[] = [{ provider: 'codex' as const,
    id: ASSISTANT_CARD.id, trackId: 'w1', trackTitle: 'Test track', title: null,
    kind: 'agent', isLead: false, state: 'idle', updatedAt: 30,
  }];

  /** One mounted store over one registry. `rows` is the track's server list, so the batch remember runs for real. */
  function mountStore(transport: ApiTransportPort, rows: readonly Conversation[] = ROWS) {
    let latestSend: (text: string) => void = () => undefined;
    let known: readonly Conversation[] = [];
    let readTurns: (id: string) => readonly TranscriptEntry[] = () => [];
    let ready = false;

    function StoreProbe({ scope }: { scope: typeof SCOPE | null }) {
      const store = useConversationStore(transport, unauthorized, scope, {
        rows, rememberOn: 'w1',
      });
      const send = store.send;
      useEffect(() => { ready = !store.sendBlocked; latestSend = (text) => { void send(ASSISTANT_CARD.id, text, [], true); }; });
      return null;
    }

    function RegistryProbe() {
      const registry = useConversationCommands();
      const conversations = useRememberedConversations();
      useEffect(() => { known = conversations; readTurns = registry.turnsOf; });
      return null;
    }

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    const view = (scope: typeof SCOPE | null) => (
      <QueryClientProvider client={client}>
        <CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}><ConversationProvider>
          <RegistryProbe />
          <StoreProbe scope={scope} />
        </ConversationProvider></CurrentUserScope>
      </QueryClientProvider>
    );
    const { rerender } = render(view(SCOPE));
    return {
      /* The macrotask is not padding: `mutations.send` resolves two invalidations after
             its POST, and an invalidation only refetches for committed observers. */
      send: async (text: string) => {
        await waitFor(() => expect(ready).toBe(true));
        await act(async () => { latestSend(text); await new Promise((resolve) => setTimeout(resolve, 0)); });
      },
      /** The drawer shuts; the store stays mounted, as it does in production. */
      closeDrawer: async (metadataRows: readonly Conversation[] = rows) => {
        rows = metadataRows;
        await act(async () => { rerender(view(null)); await Promise.resolve(); });
      },
      settle: async () => {
        await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
      },
      /** A history read landing, written where a landed history lives. */
      deliverHistory: async (rows: SessionItemDto[]) => {
        await act(async () => {
          client.setQueryData(
            cachedHistoryKey(client, ASSISTANT_CARD.id),
            latestHistory(undefined, sessionItemsPageDtoToConversationPage(ASSISTANT_CARD.id, historyPage(ASSISTANT_CARD.id, rows), null)),
          );
          await Promise.resolve();
        });
      },
      entry: () => known.find((candidate) => candidate.id === ASSISTANT_CARD.id),
      turns: () => readTurns(ASSISTANT_CARD.id),
    };
  }

  /* #2068 item 17: Edit takes a failed send back into the composer, which only a send that was not stored may do; the
     outbox refuses it for an unknown one itself, not only by the footer offering no Edit there. */
  it('[#2068] refuses to take a spent unknown send back for an Edit', async () => {
    const transport: ApiTransportPort = {
      send: (request) => request.path.endsWith('/messages') ? Promise.reject(new Error('response dropped'))
        : Promise.resolve(request.path.includes('limit=1') ? runIdle()
          : isHistoryPath(request.path) ? ok(historyPage(conversationCardId(request.path), [])) : ok([])),
    };
    let store!: ReturnType<typeof useConversationStore>;
    let composerText = '';
    function Probe() {
      const current = useConversationStore(transport, unauthorized, SCOPE, { rows: ROWS, rememberOn: 'w1' });
      const text = useConversationComposerText(ASSISTANT_CARD.id);
      useEffect(() => { store = current; composerText = text; });
      return null;
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    render(<QueryClientProvider client={client}><CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}><ConversationProvider><Probe /></ConversationProvider></CurrentUserScope></QueryClientProvider>);
    await waitFor(() => expect(store.historyReady).toBe(true));
    await act(async () => { void store.send(ASSISTANT_CARD.id, 'maybe stored', [], true); await Promise.resolve(); });
    await waitFor(() => expect(store.failedSend?.delivery).toBe('unknown'));
    const key = store.failedSend?.key ?? '';
    act(() => { store.discardFailedSend(key); });
    expect(store.failedSend?.key).toBe(key);
    expect(composerText).toBe('');
  });

  /* Recorded per commit (a layout effect), since `act` would flush the render a switch leaks before any assertion. */
  it('[#2041] shows the next conversation no part of the last one’s send or error, not for one commit', async () => {
    let answer!: () => void;
    const answered = new Promise<void>((done) => { answer = done; });
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.path.endsWith('/messages')) { await answered; return inputAccepted(); }
        if (request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path)) return failure(500, 'internal', 'model store unavailable');
        if (request.path.includes('limit=1')) return ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id)));
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    const PLANNER_SCOPE = { ...SCOPE, cardId: PLANNER_CARD.id };
    type Commit = Readonly<{ cardId: string; sending: boolean; blocked: boolean; pending: number; error: string | null }>;
    const commits: Commit[] = [];
    let latest: ReturnType<typeof useConversationStore> | null = null;
    function StoreProbe({ scope }: { scope: typeof SCOPE }) {
      const store = useConversationStore(transport, unauthorized, scope, { rows: ROWS, rememberOn: 'w1' });
      latest = store;
      useLayoutEffect(() => {
        commits.push({ cardId: scope.cardId, sending: store.sending, blocked: store.sendBlocked, pending: store.pending.size, error: store.actionError });
      });
      return null;
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    const view = (scope: typeof SCOPE) => (
      <QueryClientProvider client={client}><CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}><ConversationProvider><StoreProbe scope={scope} /></ConversationProvider></CurrentUserScope></QueryClientProvider>
    );
    const { rerender } = render(view(SCOPE));
    await waitFor(() => expect(latest?.sendBlocked).toBe(false));
    await act(async () => { void latest?.send(ASSISTANT_CARD.id, 'still out', [], true); await Promise.resolve(); });
    await act(async () => { latest?.setModel({ model: 'gpt-x', reasoning_effort: null }); await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(commits.at(-1)).toMatchObject({ cardId: ASSISTANT_CARD.id, sending: true, error: 'The model change is unconfirmed.' });
    const before = commits.length;
    await act(async () => { rerender(view(PLANNER_SCOPE)); await Promise.resolve(); });
    const shown = commits.slice(before);
    expect(shown.length).toBeGreaterThan(0);
    expect(shown.every((commit) => commit.cardId === PLANNER_CARD.id)).toBe(true);
    expect(shown.filter((commit) => commit.sending || commit.pending > 0 || commit.error !== null)).toEqual([]);
    await act(async () => { answer(); await answered; });
  });

  it('[#2068] drops a model refusal that settles while another conversation is shown, not showing it for one commit', async () => {
    let refuse!: () => void;
    const refused = new Promise<void>((done) => { refuse = done; });
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.method === 'PATCH' && /\/api\/v1\/sessions\/[^/]+$/.test(request.path)) { await refused; return failure(400, 'bad_request', 'Claude is not ready'); }
        if (request.path.includes('limit=1')) return ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id)));
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    const errors: (string | null)[] = [];
    let latest: ReturnType<typeof useConversationStore> | null = null;
    function StoreProbe({ scope }: { scope: typeof SCOPE }) {
      const store = useConversationStore(transport, unauthorized, scope, { rows: ROWS, rememberOn: 'w1' });
      latest = store;
      useLayoutEffect(() => { errors.push(store.actionError); });
      return null;
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    const view = (scope: typeof SCOPE) => (
      <QueryClientProvider client={client}><CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}><ConversationProvider><StoreProbe scope={scope} /></ConversationProvider></CurrentUserScope></QueryClientProvider>
    );
    const { rerender } = render(view(SCOPE));
    await act(async () => { latest?.setModel({ model: 'gpt-x', reasoning_effort: null }); await Promise.resolve(); });
    await act(async () => { rerender(view({ ...SCOPE, cardId: PLANNER_CARD.id })); await Promise.resolve(); });
    await act(async () => { refuse(); await new Promise((resolve) => setTimeout(resolve, 0)); });
    await act(async () => { rerender(view(SCOPE)); await Promise.resolve(); });
    expect(errors.filter((error) => error !== null)).toEqual([]);
  });

  it('preserves provider on rapid A->B->A switches and prevents stale in-flight GETs from reverting the switched provider', async () => {
    let releaseFirstPatch!: () => void;
    const firstPatchGate = new Promise<void>((done) => { releaseFirstPatch = done; });
    let releaseStaleGet!: () => void;
    const staleGetGate = new Promise<void>((done) => { releaseStaleGet = done; });
    let session = fixtureSession(ASSISTANT_CARD.id, TRACK.id, { provider: 'codex', model: null });
    let limitOneReads = 0;
    const patchBodies: unknown[] = [];
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.method === 'PATCH' && request.path === `/api/v1/sessions/${ASSISTANT_CARD.id}`) {
          patchBodies.push(request.body);
          if (patchBodies.length === 1) await firstPatchGate;
          const body = request.body as { provider?: string; model?: string | null; reasoningEffort?: string | null };
          session = {
            ...session,
            provider: body.provider ?? session.provider,
            model: body.model ?? null,
            reasoningEffort: body.reasoningEffort ?? null,
          };
          return ok(session);
        }
        if (request.path.includes('limit=1')) {
          limitOneReads += 1;
          const snapshot = { ...session };
          if (limitOneReads === 2) {
            await staleGetGate;
            if (request.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
          }
          return ok(sessionPage(snapshot));
        }
        return request.path.includes('/items?') ? ok(sessionPage(session)) : ok([]);
      },
    };
    let latest: ReturnType<typeof useConversationStore> | null = null;
    function StoreProbe() {
      latest = useConversationStore(transport, unauthorized, SCOPE, { rows: ROWS, rememberOn: 'w1' });
      return null;
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    render(
      <QueryClientProvider client={client}>
        <CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}>
          <ConversationProvider><StoreProbe /></ConversationProvider>
        </CurrentUserScope>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(latest?.runReady).toBe(true));

    // Trigger a stale in-flight GET (read #2) captured while session is still codex.
    void client.invalidateQueries({ queryKey: queryKeys.plannerRun(ASSISTANT_CARD.id) });
    await waitFor(() => expect(limitOneReads).toBe(2));

    // Switch codex -> claude, then rapidly back to codex while the first PATCH is still in flight.
    await act(async () => {
      latest?.setModel({ model: 'sonnet', reasoning_effort: null }, 'claude');
      latest?.setModel({ model: 'gpt-5', reasoning_effort: null }, 'codex');
      await Promise.resolve();
    });
    await act(async () => {
      releaseFirstPatch();
      await Promise.resolve();
    });
    await waitFor(() => expect(patchBodies).toEqual([
      { provider: 'claude', model: 'sonnet', reasoningEffort: null },
      { provider: 'codex', model: 'gpt-5', reasoningEffort: null },
    ]));
    await act(async () => {
      releaseStaleGet();
      await Promise.resolve();
    });
    await waitFor(() => expect(latest?.session).toMatchObject({ provider: 'codex', model: 'gpt-5' }));
  });

  /* The confirmed send acquires authoritative first-input metadata; closing must keep
   * that metadata while the registry still owns its transcript and outbox. */
  it('[G5] keeps the server first-input title after the drawer closes', async () => {
    let stored = false;
    const transport: ApiTransportPort = {
      send(request) {
        if (request.path.endsWith('/messages')) { stored = true; return Promise.resolve(inputAccepted()); }
        if (request.path.includes('limit=1')) return Promise.resolve(runIdle());
        return Promise.resolve(request.path.includes('/items?') ? ok(historyPage(conversationCardId(request.path), stored ? [sessionItem(ASSISTANT_CARD.id, 1, 'rename this conversation', 'user')] : [])) : ok([]));
      },
    };
    const store = mountStore(transport);
    await store.send('rename this conversation');
    await waitFor(() => expect(store.turns()).toHaveLength(1));
    await store.closeDrawer([{ ...ROWS[0], title: 'rename this conversation' }]);
    await store.settle();
    /* The batch remember consumes the server's actual post-confirmation metadata. */
    expect(store.entry()?.title).toBe('rename this conversation');
  });

  /* The order is the test: close the drawer while the POST is in flight, so `scope`
   * is null when it fails and the `catch` that drops the echo reaches nothing.
   * Rejecting before the close is green with or without the fix. */
  it('[G5] does not keep a name, or a time, from a message that failed to send', async () => {
    let rejectInput!: () => void;
    const settled = new Promise<void>((resolve) => { rejectInput = resolve; });
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.path.endsWith('/messages')) {
          await settled;
          return { status: 503, statusText: 'Service Unavailable', body: { code: 'unavailable', error: 'busy' } };
        }
        if (request.path.includes('limit=1')) return runIdle();
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    const store = mountStore(transport);
    const beforeSend = Date.now();
    await store.send('a message that never lands');
    /* Shut, with the POST still out: this is the window, and releasing the
       rejection before this point would test a different code path. */
    await store.closeDrawer();
    await act(async () => { rejectInput(); await Promise.resolve(); });
    await store.settle();

    /* No name: the kind label is all a `title` of null renders as. */
    expect(store.entry()?.title ?? null).toBeNull();
    /* No time from a clock only this browser read: `beforeSend` is the fence, an
           echo's `atMs` is `Date.now()`. */
    expect(store.entry()?.updatedAt).toBe(30);
    expect(store.entry()?.updatedAt).toBeLessThan(beforeSend);
    /* Nor is the message itself remembered as something that happened. */
    expect(store.turns()).toHaveLength(0);
  });

  /* The write-through may not undo what arrived while it was waiting: read and
   * write must be the same moment (`updateExisting`). Two turns rejects both a
   * captured snapshot (`1`) and an appending merge (`3`). */
  it('[G5] does not overwrite a refresh that landed while the send was still settling', async () => {
    let releaseInput!: () => void;
    const inputSettled = new Promise<void>((resolve) => { releaseInput = resolve; });
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.path.endsWith('/messages')) {
          await inputSettled;
          return inputAcceptedWithEntry('refresh-entry');
        }
        if (request.path.includes('limit=1')) return runIdle();
        if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), []));
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    const store = mountStore(transport);
    await store.send('what does this repo do?');
    /* The history refresh lands while the POST that started it is still out. */
    await store.deliverHistory([
      { ...sessionItem(ASSISTANT_CARD.id, 1, 'what does this repo do?', 'user'), payload: { content: 'what does this repo do?', inputId: 'refresh-entry' } },
      sessionItem(ASSISTANT_CARD.id, 2, 'it runs tracks'),
    ]);
    await waitFor(() => { expect(store.turns()).toHaveLength(2); });

    await store.closeDrawer();
    await act(async () => { releaseInput(); await Promise.resolve(); });
    await store.settle();
    expect(store.turns()).toHaveLength(2);
    expect(store.entry()?.turns).toBe(2);
  });

  /* The "already brought back?" check matches by text, so asked against the whole
   * entry an old identical `ping` answers for the new one; only rows that arrived
   * since the send may answer. */
  it('[G5] waits for server history to count a second identical accepted message', async () => {
    let releaseInput!: () => void;
    const inputSettled = new Promise<void>((resolve) => { releaseInput = resolve; });
    const transport: ApiTransportPort = {
      async send(request) {
        /* The server's copy of the first `ping`, and only ever that one. */
        if (isHistoryPath(request.path)) {
          return ok(historyPage(conversationCardId(request.path), [sessionItem(ASSISTANT_CARD.id, 1, 'ping', 'user')]));
        }
        if (request.path.endsWith('/messages')) {
          await inputSettled;
          return inputAccepted();
        }
        if (request.path.includes('limit=1')) return runIdle();
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    const store = mountStore(transport);
    /* The first `ping` is already in the transcript. */
    await waitFor(() => { expect(store.turns()).toHaveLength(1); });
    await store.send('ping');

    await act(async () => { releaseInput(); await Promise.resolve(); });
    await store.settle();
    expect(store.turns()).toHaveLength(1);
    await store.deliverHistory([
      sessionItem(ASSISTANT_CARD.id, 1, 'ping', 'user'),
      sessionItem(ASSISTANT_CARD.id, 2, 'ping', 'user'),
    ]);
    await waitFor(() => expect(store.turns()).toHaveLength(2));
    await store.closeDrawer();
    expect(store.entry()?.turns).toBe(2);
  });

  /* Two real store instances under one provider; the first request crosses the remount. */
  it('[G5] serializes same-card sends across a remount and keeps both identical turns', async () => {
    /* Every send is held, so both are still out when their stores unmount. */
    const holds: (() => void)[] = [];
    let historyRows: SessionItemDto[] = [];
    const transport: ApiTransportPort = {
      async send(request) {
        if (request.path.endsWith('/messages')) {
          const entryId = `serial-entry-${holds.length + 1}`;
          await new Promise<void>((resolve) => { holds.push(resolve); });
          return inputAcceptedWithEntry(entryId);
        }
        if (request.path.includes('limit=1')) return runIdle();
        if (isHistoryPath(request.path)) return ok(historyPage(conversationCardId(request.path), historyRows));
        return request.path.includes('/items?') ? ok(sessionPage(fixtureSession(pathCardId(request.path), TRACK.id))) : ok([]);
      },
    };
    let latestSend: (text: string) => void = () => undefined;
    let visibleTurns: readonly TranscriptEntry[] = [];
    let latestTurns: readonly TranscriptEntry[] = [];
    let sendReady = false;

    function StoreProbe() {
      const store = useConversationStore(transport, unauthorized, SCOPE, {
        rows: ROWS, rememberOn: 'w1',
      });
      const send = store.send;
      const turns = store.turns;
      useEffect(() => {
        sendReady = !store.sendBlocked;
        latestSend = (text) => { void send(ASSISTANT_CARD.id, text, [], true); };
        visibleTurns = turns;
      });
      return null;
    }

    function RegistryProbe() {
      const registry = useConversationCommands();
      useConversationRememberedTurns(ASSISTANT_CARD.id);
      useConversationSendOps(ASSISTANT_CARD.id);
      useEffect(() => { latestTurns = registry.turnsOf(ASSISTANT_CARD.id); });
      return null;
    }

    const client = new QueryClient({ defaultOptions: { queries: { retry: false, structuralSharing: false } } });
    const view = (instance: string | null) => (
      <QueryClientProvider client={client}>
        <CurrentUserScope value={{ userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, mode: 'owner', role: 'owner', sessionId: 'test' }}><ConversationProvider>
          <RegistryProbe />
          {instance === null ? null : <StoreProbe key={instance} />}
        </ConversationProvider></CurrentUserScope>
      </QueryClientProvider>
    );
    const { rerender } = render(view('first-mount'));

    await waitFor(() => expect(sendReady).toBe(true));
    await act(async () => { latestSend('ping'); await Promise.resolve(); });
    await waitFor(() => expect(holds).toHaveLength(1));
    /* The walk away: this store is gone, its request still out. */
    await act(async () => { rerender(view(null)); await Promise.resolve(); });
    /* The walk back: a new store, same conversation, same registry. */
    await act(async () => { rerender(view('second-mount')); await Promise.resolve(); });
    await act(async () => { latestSend('ping'); await Promise.resolve(); });
    /* The provider-wide per-card lease keeps a remount from starting another
       send while the first request is unresolved. */
    expect(holds).toHaveLength(1);

    /* The first POST lands while the second store is mounted; only after it settles
           may the new store start its same-text send. */
    historyRows = [{ ...sessionItem(ASSISTANT_CARD.id, 1, 'ping', 'user'), payload: { content: 'ping', inputId: 'serial-entry-1' } }];
    await act(async () => { holds[0]?.(); await Promise.resolve(); });
    await waitFor(() => {
      latestSend('ping');
      expect(holds).toHaveLength(2);
    });
    await waitFor(() => expect(visibleTurns).toHaveLength(2));

    /* A receipt ends local state even when its store is gone. Only published history adds the second message. */
    await act(async () => { rerender(view(null)); await Promise.resolve(); });
    await act(async () => { holds[1]?.(); await Promise.resolve(); });
    await waitFor(() => expect(latestTurns).toHaveLength(1));
    historyRows = [...historyRows, { ...sessionItem(ASSISTANT_CARD.id, 2, 'ping', 'user'), payload: { content: 'ping', inputId: 'serial-entry-2' } }];
    await act(async () => { rerender(view('third-mount')); await Promise.resolve(); });
    await waitFor(() => expect(latestTurns).toHaveLength(2));
    expect(latestTurns.map(turn => 'text' in turn ? turn.text : '')).toEqual(['ping', 'ping']);
    /* The assertion: two messages, two identities across the remount. */
    expect(new Set(latestTurns.map((turn) => turn.id)).size).toBe(2);
  });
});


const inputBodies = (requests: readonly ApiRequest[]) => requests.filter(request => request.path.endsWith('/messages')).map(request => request.body);
const settleFor = (ms: number) => act(async () => { await new Promise(resolve => setTimeout(resolve, ms)); });
async function pickPlanner() {
  fireEvent.click(screen.getByRole('button', { name: /^Conversation Planner chat/ }));
  await screen.findByRole('complementary', { name: 'Planner chat' });
}
async function pickAssistant() {
  fireEvent.click(screen.getByRole('button', { name: /^Conversation (Assistant|Original prompt)/ }));
  await screen.findByRole('complementary', { name: /^(Assistant|Original prompt)$/ });
}
async function reopenAssistant() {
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  await pickAssistant();
}

it('does not loop failed acknowledgements when a conversation with no output is presented', async () => {
  const receipts = createReadReceiptsKernel({ fail: true, writeDelayMs: 10, receipts: [
    { kind: 'track', targetId: 'w1', readThroughMs: 0, manualUnread: true },
    { kind: 'conversation', targetId: ASSISTANT_CARD.id, readThroughMs: 0, manualUnread: true },
  ] });
  receipts.capWrites(8);
  const { client } = setup(request => request.path === '/api/v1/tracks/w1'
    ? ok(fixtureSnapshot(TRACK, [PLANNER_CARD, assistantRow({ lastNotifiableTurnAtMs: null })])) : undefined,
  undefined, receipts);
  fireEvent.click(await screen.findByRole('button', { name: /^Conversation Assistant(?:,|$)/ }));
  await screen.findByRole('combobox', { name: 'Message' });
  await waitFor(() => expect(receipts.posts().some(request => request.path.includes('/conversation/'))).toBe(true));
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 300)); });
  expect(receipts.posts().filter(request => request.path.includes('/conversation/'))).toHaveLength(1);
  const count = receipts.posts().length;
  await act(async () => { await client.invalidateQueries({ queryKey: queryKeys.readReceipts() }); });
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 150)); });
  expect(receipts.posts()).toHaveLength(count);
});

it('keeps a visible conversation without output manually unread after receipt reconciliation', async () => {
  const receipts = createReadReceiptsKernel({ writeDelayMs: 10, receipts: [
    { kind: 'conversation', targetId: ASSISTANT_CARD.id, readThroughMs: 0, manualUnread: true },
  ] });
  const { client } = setup(request => request.path === '/api/v1/tracks/w1'
    ? ok(fixtureSnapshot(TRACK, [PLANNER_CARD, assistantRow({ lastNotifiableTurnAtMs: null })])) : undefined,
  undefined, receipts);
  fireEvent.click(await screen.findByRole('button', { name: /^Conversation Assistant(?:,|$)/ }));
  await screen.findByRole('combobox', { name: 'Message' });
  await waitFor(() => expect(receipts.stored('conversation', ASSISTANT_CARD.id)?.manualUnread).toBe(false));
  await waitFor(() => expect(client.isFetching()).toBe(0));
  const before = receipts.posts().length;
  receipts.store({ kind: 'conversation', targetId: ASSISTANT_CARD.id, readThroughMs: 0, manualUnread: true });
  await act(async () => { await client.invalidateQueries({ queryKey: queryKeys.readReceipts() }); });
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 150)); });
  expect(receipts.posts()).toHaveLength(before);
  expect(receipts.stored('conversation', ASSISTANT_CARD.id)?.manualUnread).toBe(true);
});

function fixtureConversationCardId(trackId: string, key: string): string { return `conv-${trackId}-${key}`; }


it('marks every unread conversation read, including archived and manual unread, while preserving execution states', async () => {
  const rows = [PLANNER_CARD,
    assistantRow({ title: 'Unread reply', lastNotifiableTurnAtMs: 30 }),
    fixtureSession('archived', TRACK.id, { title: 'Archived reply', archivedAtMs: 50, lastNotifiableTurnAtMs: 40 }),
    fixtureSession('manual', TRACK.id, { title: 'Manual unread' }),
    fixtureSession('running', TRACK.id, { title: 'Running chat', status: 'running', lastNotifiableTurnAtMs: 20 }),
    fixtureSession('failed', TRACK.id, { title: 'Failed chat', status: 'unknown', lastNotifiableTurnAtMs: 25 }),
    fixtureSession('attention', TRACK.id, { title: 'Question chat', lastNotifiableTurnAtMs: 15 }),
  ];
  const receipts = createReadReceiptsKernel({ baselineMs: 0, receipts: [
    { kind: 'conversation', targetId: 'manual', readThroughMs: 0, manualUnread: true },
  ] });
  const { client } = setup(request => request.path === '/api/v1/tracks/w1'
    ? ok({ ...fixtureSnapshot(TRACK, rows), openAsks: [{ id: 'question', trackId: TRACK.id, sessionId: 'attention',
      status: 'open', delivery: 'wake', questions: [{ question: 'Continue?', options: [], isMultiSelect: false }], createdAtMs: 5 }] })
    : undefined, undefined, receipts);
  const button = await screen.findByRole('button', { name: 'Mark all read' });
  const state = () => client.getQueryData<ReadState>(queryKeys.readReceipts())!;
  const watermarks = rows.filter(row => row.lastNotifiableTurnAtMs != null).map(row => ({
    sessionId: row.id, lastNotifiableTurnAtMs: row.lastNotifiableTurnAtMs!,
  }));
  expect(isTrackUnread(state(), TRACK.id, watermarks)).toBe(true);
  const indicator = (name: string) => screen.getByRole('button', { name: new RegExp(`^Conversation ${name}`) })
    .closest('li')!.querySelector('[data-nc-activity]')?.getAttribute('data-nc-activity');
  expect(indicator('Running chat')).toBe('working');
  expect(indicator('Failed chat')).toBe('failed');
  expect(indicator('Question chat')).toBe('attention');
  // The archived group stays folded; bulk reading must include its unmounted rows.
  expect(screen.queryByRole('button', { name: 'Conversation Archived reply' })).toBeNull();
  fireEvent.click(button);
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Mark all read' })).toBeNull());
  expect(isTrackUnread(state(), TRACK.id, watermarks)).toBe(false);
  for (const row of rows) expect(isUnread(state(), 'conversation', row.id, row.lastNotifiableTurnAtMs)).toBe(false);
  await waitFor(() => {
    for (const row of rows.slice(1)) expect(receipts.stored('conversation', row.id)).toEqual({
      kind: 'conversation', targetId: row.id, readThroughMs: row.lastNotifiableTurnAtMs ?? 0, manualUnread: false,
    });
  });
  expect(indicator('Unread reply')).toBeUndefined();
  expect(indicator('Running chat')).toBe('working');
  expect(indicator('Failed chat')).toBe('failed');
  expect(indicator('Question chat')).toBe('attention');
  expect(receipts.stored('conversation', PLANNER_CARD.id)).toBeUndefined();
});
