import { fixtureCreation, fixtureSession } from './session-contract-fixture.ts';
import type { History } from '../conversations/history.ts';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { fixtureBootstrap } from './session-contract-fixture.ts';
import { sessionPage, sessionTurn } from './conversation-session-data.ts';

import { groupTranscriptActivities } from '../../../../core/domain/conversation-groups.ts';
import { currentResponseMessage } from '../../../../core/domain/conversation-actions.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { buildConversationTranscript } from '../../../../core/domain/conversation-display.ts';

// Count the canonical production derivations, without replacing their behavior.
vi.mock(import('../../../../core/domain/conversation-display.ts'), async original => {
  const actual = await original();
  return { ...actual, buildConversationTranscript: vi.fn(actual.buildConversationTranscript) };
});
vi.mock(import('../../../../core/domain/conversation-groups.ts'), async original => {
  const actual = await original();
  return { ...actual, groupTranscriptActivities: vi.fn(actual.groupTranscriptActivities) };
});
vi.mock(import('../../../../core/domain/conversation-actions.ts'), async original => {
  const actual = await original();
  return { ...actual, currentResponseMessage: vi.fn(actual.currentResponseMessage) };
});

beforeEach(() => {
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks(); document.getElementById('root')?.remove(); });

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
const field = () => screen.getByRole('combobox', { name: 'Message' });
async function type(text: string) {
  await act(async () => {
    field().textContent = text;
    fireEvent.input(field());
    await Promise.resolve();
  });
}
function heldResponse() {
  let release!: (response: ApiTransportResponse) => void;
  const promise = new Promise<ApiTransportResponse>(resolve => { release = resolve; });
  return { promise, release: async (body: unknown) => { await act(async () => { release(ok(body)); await promise; }); } };
}
function mount({ phase = 'idle', input }: { phase?: string; input?: Promise<ApiTransportResponse> } = {}) {
  return renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (request.path === '/api/v1/bootstrap') return ok(fixtureBootstrap());
    if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok({ version: 'test', providers: [{ provider: 'codex', available: true, version: null, reason: null, models: [] }], templates: [], areas: [] });
    if (request.path === '/api/v1/sessions/second-planner/items?afterId=0&limit=1') return ok(sessionPage(fixtureSession('second-planner', 'daily'), [], phase === 'turn_running' ? sessionTurn('second-planner') : null));
    if (request.path.includes('/second-planner/items') && !request.path.includes('limit=1')) return ok(sessionPage(fixtureSession('second-planner', 'daily'), Array.from({ length: 120 }, (_, index) => ({ id: index + 1, sessionId: 'second-planner', turnId: 'turn', kind: 'assistant_text', payload: { text: `Stored reply ${index}. **Evidence** remains readable.` }, createdAtMs: index + 1 }))));
    if (input !== undefined && request.path.endsWith('/messages')) return input;
    if (input !== undefined && request.method === 'POST' && request.path.endsWith('/sessions')) return input;
    return undefined;
  } });
}
async function openExisting() {
  fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
  await screen.findByText('Stored reply 119.', { exact: false }, { timeout: 5000 });
}

it('keeps unchanged stored history derivation and grouping out of composer updates', async () => {
  const view = mount();
  try {
    await openExisting();
    // The first nonempty character legitimately withdraws Edit's empty-composer affordance.
    await type('A');
    vi.mocked(buildConversationTranscript).mockClear(); vi.mocked(groupTranscriptActivities).mockClear();
    vi.mocked(currentResponseMessage).mockClear();
    const stored = document.querySelector('[data-nc-turn="agent"]');
    await type('A draft'); await type('A draft with more words');
    expect(field().textContent).toBe('A draft with more words');
    expect(document.querySelector('[data-nc-turn="agent"]')).toBe(stored);
    expect(buildConversationTranscript).not.toHaveBeenCalled();
    expect(groupTranscriptActivities).not.toHaveBeenCalled();
    expect(currentResponseMessage).not.toHaveBeenCalled();
    // Real new server content still invalidates the projection without touching the draft.
    await act(async () => {
      view.client.setQueryData<History>(['harness-items', 'second-planner', 'conversation_v1'], previous => previous === undefined ? previous : ({
        ...previous, latest: { ...previous.latest, rows: previous.latest.rows.map(row => row.cursor.position === 120 && row.source.kind === 'entry'
          ? { ...row, source: { ...row.source, entry: { ...row.source.entry, text: 'The server updated the last reply.' } } } : row) },
      }));
      await Promise.resolve();
    });
    await screen.findByText('The server updated the last reply.');
    expect(buildConversationTranscript).toHaveBeenCalled();
    expect(groupTranscriptActivities).toHaveBeenCalled();
    expect(field().textContent).toBe('A draft with more words');
  } finally { cleanup(); view.client.clear(); }
});

it('shows the local sending state before a held request answers', async () => {
  const response = heldResponse();
  const view = mount({ input: response.promise });
  try {
    await openExisting(); await type('Send without waiting for the server');
    vi.mocked(buildConversationTranscript).mockClear();
    fireEvent.keyDown(field(), { key: 'Enter' });
    await waitFor(() => expect(view.requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(1));
    const localMessage = document.querySelector('[data-nc-thread] [data-nc-entry] [data-nc-turn="you"]');
    expect(localMessage?.textContent).toContain('Send without waiting for the server');
    expect(screen.getByRole('group', { name: 'Message composer' }).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('Sending…')).toBeNull();
    expect(buildConversationTranscript).not.toHaveBeenCalled();
    // Local dispatch is not a confirmed queued entry.
    expect(screen.queryByText('Queued · sends when this turn ends')).toBeNull();
    expect(screen.getByRole('button', { name: 'Send' }).hasAttribute('disabled')).toBe(true);
    expect(screen.queryByText('Running', { exact: true })).toBeNull();
    fireEvent.keyDown(field(), { key: 'Enter' });
    expect(view.requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(1);
  } finally { await response.release({ card_id: 'second-planner', worker_session_id: 'runtime' }); cleanup(); view.client.clear(); }
});

it('shows first-message sending feedback while conversation creation is held', async () => {
  const response = heldResponse();
  const view = mount({ input: response.promise });
  try {
    fireEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    await waitFor(() => expect(view.client.getQueryData(['server-version'])).toMatchObject({ assistantProviders: true }));
    await waitFor(() => expect(view.client.getQueryData(['managed-providers'])).toHaveLength(1));
    await type('The first local message');
    fireEvent.keyDown(field(), { key: 'Enter' });
    await waitFor(() => expect(view.requests.some(request => request.method === 'POST' && request.path.endsWith('/sessions'))).toBe(true));
    expect(document.querySelector('[data-nc-turn="you"]')?.textContent).toBe('The first local message');
    expect(screen.getByRole('group', { name: 'Message composer' }).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('Sending…')).toBeNull();
    expect(screen.getByRole('button', { name: 'Send' }).hasAttribute('disabled')).toBe(true);
  } finally { await response.release(fixtureCreation(fixtureSession('created', 'daily'))); cleanup(); view.client.clear(); }
});

it('retry callback stays committed across input renders', async () => {
  const view = mount();
  try {
    await openExisting();
    await type('Draft before retry');
    vi.mocked(currentResponseMessage).mockClear();
    vi.mocked(groupTranscriptActivities).mockClear();
    await type('Draft after retry');
    expect(currentResponseMessage).not.toHaveBeenCalled();
    expect(groupTranscriptActivities).not.toHaveBeenCalled();
  } finally { cleanup(); view.client.clear(); }
});
