import { fixtureReport, fixtureSnapshot, fixtureSession, fixtureTrack } from './session-contract-fixture.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// @vitest-environment jsdom
// #2209 U3: the Planner's open asks, answered from its composer through the production router.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';

import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

const AREA = { id: 'c1', name: 'Work', color: '#000', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Release', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
const PLANNER = { id: 'planner-1', track_id: 'w1', kind: 'codex', title: 'Planner chat', sort: 1, payload: { planner_harness: true }, deletable: false, created_at: 1, updated_at: 2 };
const ASSISTANT = fixtureSession('assistant-1', 'w1', { title: 'Side chat' });
const ASK = { id: '41', trackId: 'w1', status: 'open', delivery: 'wake', createdAtMs: 5,
  questions: [{ question: 'Which branch?', options: ['main', 'release'], isMultiSelect: false }, { question: 'Notes?', options: [], isMultiSelect: false }] };
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });

function ok(body: unknown, status = 200): ApiTransportResponse {
  return { status, statusText: status === 204 ? 'No Content' : 'OK', body };
}

function setup() {
  const currentTrack = TRACK;
  const requests: ApiRequest[] = [];
  const transport: ApiTransportPort = {
    async send(request) {
      requests.push(request);
      await Promise.resolve();
      if (request.path === '/api/v1/areas') return ok([AREA]);
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([currentTrack]);
      if (request.path === '/api/v1/tracks/w1') return ok({
        ...fixtureSnapshot(currentTrack, [fixtureSession(PLANNER.id, TRACK.id, { title: PLANNER.title, isLead: true }), ASSISTANT], fixtureReport('# Release notes')),
        openAsks: [ASK],
      });
      if (request.path === '/api/v1/sessions/planner-1/interactions/41/respond') return ok({ id: '41', kind: 'ask' });
      if (request.path.includes('/items')) return ok({
        session: fixtureSession(conversationCardId(request.path), TRACK.id, { permissionMode: 'never' }),
        pendingInteractions: conversationCardId(request.path) === PLANNER.id ? [{ ...ASK, kind: 'ask', sessionId: PLANNER.id, title: 'Which branch?', canRespond: true }] : [],
        items: [], activeTurn: null, state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, attachmentsSupported: false,
      });
      if (request.path === '/api/settings') return ok({});
      return ok([]);
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, unauthorized, client, cards: bootTestCardRuntime() });
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => {} }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return { requests };
}

beforeEach(() => {
  window.history.pushState({}, '', `/track/w1`);
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function open(name: string) {
  fireEvent.click(await screen.findByRole('button', { name: new RegExp(`Conversation ${name}`) }));
  return screen.findByRole('complementary', { name });
}

it('answers the open ask from the Planner composer and hides it before the aggregate catches up', async () => {
  const { requests } = setup();
  const drawer = await open('Planner chat');
  const ask = await within(drawer).findByRole('group', { name: 'The Planner asks' });
  fireEvent.click(within(ask).getByRole('button', { name: 'release' }));
  fireEvent.change(await within(ask).findByRole('textbox', { name: 'Notes?' }), { target: { value: 'Tag it 2.0' } });
  fireEvent.click(within(ask).getByRole('button', { name: 'Answer' }));
  await waitFor(() => expect(requests.filter((request) => request.path.endsWith('/respond'))).toHaveLength(1));
  const answer = requests.find((request) => request.path.endsWith('/respond'));
  expect(answer?.method).toBe('POST');
  expect(answer?.path).toBe('/api/v1/sessions/planner-1/interactions/41/respond');
  expect(answer?.body).toEqual({ answers: [{ option: 1, value: 'release' }, { text: 'Tag it 2.0', value: 'Tag it 2.0' }] });
  await waitFor(() => expect(within(drawer).queryByRole('group', { name: 'The Planner asks' })).toBeNull());
  /* The composer itself stays. */
  expect(within(drawer).getByRole('combobox', { name: 'Message' })).toBeTruthy();
});

it('asks nothing in a conversation that is not the Planner’s', async () => {
  setup();
  const drawer = await open('Side chat');
  await within(drawer).findByRole('combobox', { name: 'Message' });
  expect(within(drawer).queryByRole('group', { name: 'The Planner asks' })).toBeNull();
});
