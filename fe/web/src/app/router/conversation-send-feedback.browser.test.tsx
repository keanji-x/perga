import { historicalConversationPage, conversationCardId, historicalConversationRun } from './conversation-read-data.ts';
import '../../styles/entry.css';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { commands, page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import type { HarnessQueueEntryRef } from '../../../../core/api/generated/wire.ts';
import { trackConversationCardId } from '../../../../core/domain/conversation.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { initialReceiptFor } from './initial-input-fixture.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
const text = 'A message submitted while its request stays pending.';
const field = () => screen.getByRole('combobox', { name: 'Message' });
const userMessages = () => document.querySelectorAll<HTMLElement>(':is([data-nc-turn="you"], [data-nc-turn="unknown"])');
const outgoing = () => userMessages()[0];
const surface = () => outgoing().closest<HTMLElement>('[data-nc-entry]') ?? outgoing();
const ORDINARY_ENTRY: HarnessQueueEntryRef = Object.freeze({ id: 'ordinary-entry', rev: 0 });

function mount({ persistSent = false, running = false, message = text }: { persistSent?: boolean; running?: boolean; message?: string } = {}) {
  const replies: { promise: Promise<ApiTransportResponse | undefined>; resolve: (value: ApiTransportResponse | undefined) => void }[] = [];
  let posts = 0;
  let answers = 0;
  let storedText: string | null = null;
  let canonicalReads = 0;
  let created: { id: string; trackId: string; title: null; kind: string; provider: string; state: null; updatedAt: number; archivedAt: null, lastTurnCompletedAt: null } | null = null;
  const responseAt = (index: number) => {
    if (replies[index] === undefined) {
      let resolve!: (value: ApiTransportResponse | undefined) => void;
      const promise = new Promise<ApiTransportResponse | undefined>(done => { resolve = done; });
      replies[index] = { promise, resolve };
    }
    return replies[index];
  };
  const userRow = (cardId: string, body: string, turnId: string | null, queueEntry: { id: string; rev: number } | null) => ({
    id: 2, worker_session_id: 'runtime', card_id: cardId, track_id: 'daily', thread_id: 'thread',
    turn_id: turnId, turn_error_text: null, item_uuid: null, item_type: 'userMessage', method: 'item/completed',
    input_segments: [{ presentation: 'user', text: body, attachments: [], origin: { observation: 'user_message', event_id: null, queue_entry: queueEntry } }],
    params: JSON.stringify({ item: { type: 'userMessage', content: [{ text: body }] } }), created_at_ms: Date.now(),
  });
  const view = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (request.path === '/api/v1/bootstrap') return ok({ assistantProviders: true, conversationInitialInput: true, conversationCreateModel: true,
      webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'send-feedback' });
    if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok([{
      availability: { provider: 'codex', status: 'ready', reason: null, checked_at_ms: 1, authentication_method: null, authentication_notice: null },
      accepts_unavailable_blank_track: true, capabilities: { managed_conversation: true, steer: true, images: true, manual_compact: true, history_replacement: true, requires_ready_availability: false },
    }]);
    if (request.path === '/api/v1/sessions/second-planner/items?afterId=0&limit=1&view=run') return ok(historicalConversationRun({ card_id: 'second-planner', worker_session_id: 'runtime',
      phase: running ? 'turn_running' : 'idle', model: null, reasoning_effort: null, permission_mode: 'never', blocked_reason: null,
      manual_compact_supported: true, recovery: null, compact_outcome: null, context_usage_invalidated: false, running_turn: null }, 'idle', true));
    if (request.path.endsWith('/harness/live')) return ok({ turn_id: 'running-turn', items: [] });
    if (request.path.includes('/sessions/second-planner/items')) {
      if (storedText !== null) canonicalReads++;
      return ok(historicalConversationPage(conversationCardId(request.path), [{
      id: 1, worker_session_id: 'runtime', card_id: 'second-planner', track_id: 'daily', thread_id: 'thread',
      turn_id: null, turn_error_text: null, item_uuid: null, item_type: 'agentMessage', method: 'item/completed',
      params: JSON.stringify({ item: { text: 'Stored history remains still.' } }), created_at_ms: 1,
    }, ...(storedText === null ? [] : [userRow('second-planner', storedText, null, ORDINARY_ENTRY)])], null));
    }
    if (created !== null && request.method === 'GET' && request.path === '/api/v1/tracks/daily/sessions') return ok([created]);
    if (created !== null && request.path.endsWith('/initial-input')) {
      const post = view.requests.find(candidate => candidate.method === 'POST' && candidate.path.endsWith('/sessions')
        && candidate.headers?.['Idempotency-Key'] === request.headers?.['Idempotency-Key']);
      if (post === undefined) throw new Error('No original creation request');
      return ok({ kind: 'projected', entryRev: 0, receipt: initialReceiptFor(post, 'first-entry', 0), runtimeId: 'runtime', itemId: 2, segmentIndex: 0 });
    }
    if (created !== null && request.path.includes(`/${created.id}/items`) && !request.path.includes('&view=')) {
      canonicalReads++;
      return ok(historicalConversationPage(conversationCardId(request.path), [userRow(created.id, message, 'accepted-initial-turn', { id: 'first-entry', rev: 0 })], null));
    }
    if (request.method === 'POST' && (request.path.endsWith('/messages') || request.path.endsWith('/sessions'))) {
      return responseAt(posts++).promise.then(result => {
        if (result !== undefined) return result;
        if (request.path.endsWith('/sessions')) {
          const key = request.headers?.['Idempotency-Key'];
          if (key === undefined) throw new Error('The create request must carry its key.');
          created = { id: trackConversationCardId('daily', key), trackId: 'daily', title: null, kind: 'track-assistant', provider: 'codex', state: null, updatedAt: 2, archivedAt: null, lastTurnCompletedAt: null };
          return { status: 201, statusText: 'Created', body: { ...created,
            initialInput: { kind: 'known', receipt: initialReceiptFor(request, 'first-entry', 0) } } };
        }
        if (persistSent) {
          const body = request.body as { text: string };
          storedText = body.text;
        }
        return ok({ card_id: 'second-planner', worker_session_id: 'runtime', entry_id: ORDINARY_ENTRY.id });
      });
    }
    return undefined;
  } });
  return { ...view, canonicalReads: () => canonicalReads, answer: (result?: ApiTransportResponse) => responseAt(answers++).resolve(result) };
}
async function open(view: ReturnType<typeof mount>, draft = false, message = text, compact = false) {
  if (draft) {
    if (compact) {
      // The fixture starts in the Conversations panel; return to reading before History.
      await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
      await page.getByRole('button', { name: 'Open conversation history' }).click();
      await page.getByRole('dialog', { name: '历史对话' }).getByRole('button', { name: '发起新对话' }).click();
    } else fireEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    await waitFor(() => expect(view.client.getQueryData(['server-version'])).toMatchObject({ assistantProviders: true }));
    await waitFor(() => expect(view.client.getQueryData(['managed-providers'])).toHaveLength(1));
  } else {
    fireEvent.click(await screen.findByRole('button', { name: /Conversation Other conversation/ }));
    await screen.findByText('Stored history remains still.', {}, { timeout: 5000 });
  }
  await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
  await act(async () => { field().textContent = message; fireEvent.input(field()); await Promise.resolve(); });
}
afterEach(async () => { cleanup(); document.getElementById('root')?.remove(); await commands.emulateReducedMotion(false); });

/** Messages must stay fully legible through local admission and persisted adoption. */
function expectReadableMessage() {
  expect(outgoing().textContent).toBe(text);
  expect(getComputedStyle(surface()).opacity).toBe('1');
  expect(getComputedStyle(surface()).transform).toBe('none');
}

it.each([false, true])('shows a readable local message and Sending before the request answers (first message: %s)', async draft => {
  await page.viewport(1400, 900);
  const view = mount();
  try {
    await open(view, draft);
    const send = screen.getByRole('button', { name: 'Send' });
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expect(send.isConnected).toBe(true);
    expect(send.hasAttribute('disabled')).toBe(true);
    expectReadableMessage();
    const pendingSend = screen.getByRole('button', { name: 'Send' });
    expect(pendingSend.hasAttribute('disabled')).toBe(true);
    await expect.poll(() => pendingSend.getAnimations({ subtree: true })
      .some(animation => animation.effect?.getTiming().iterations === Infinity)).toBe(true);
    expect(screen.getByText('Sending…')).toBeDefined();
    expect(screen.queryByText('Running')).toBeNull();
    expect(userMessages()).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Close conversation' })).toBeNull());
  } finally { cleanup(); view.answer(); view.client.clear(); }
});

it.each([false, true])('keeps text readable when a fast answer replaces the echo with real history (first message: %s)', async draft => {
  const view = mount({ persistSent: true });
  try {
    await open(view, draft);
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expectReadableMessage();
    await act(async () => { view.answer(); await Promise.resolve(); });
    await waitFor(() => expect(view.canonicalReads()).toBeGreaterThan(0));
    await waitFor(() => expect(view.client.isFetching()).toBe(0));
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
    await waitFor(expectReadableMessage);
    expect(screen.queryByText('Sending…')).toBeNull();
    expect(userMessages()).toHaveLength(1);
    expect(outgoing().getAttribute('data-nc-turn')).toBe('unknown');
  } finally { cleanup(); view.answer(); view.client.clear(); }
});

it('keeps a refused first message legible with an explicit recovery action', async () => {
  const view = mount();
  try {
    await open(view, true);
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expectReadableMessage();
    await act(async () => {
      view.answer({ status: 429, statusText: 'Too Many Requests', body: { code: 'rate_limited', error: 'Wait before retrying.' } });
      await Promise.resolve();
    });
    await waitFor(() => expect(screen.queryByText('Sending…')).toBeNull());
    expect(screen.getByRole('button', { name: 'Try again' })).toBeDefined();
    expectReadableMessage();
  } finally { cleanup(); view.answer(); view.client.clear(); }
});

it('keeps Stop available during an additional queued send', async () => {
  const view = mount({ running: true });
  try {
    await open(view);
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expectReadableMessage();
    expect(screen.getByText('Sending…')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Stop' }).getAnimations({ subtree: true })
      .some(animation => animation.effect?.getTiming().iterations === Infinity)).toBe(false);
    expect(screen.queryByRole('button', { name: 'Sending…' })).toBeNull();
    await act(async () => { view.answer(); await Promise.resolve(); });
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
    await act(async () => { field().textContent = 'Another queued message'; fireEvent.input(field()); await Promise.resolve(); });
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expect(userMessages()).toHaveLength(2);
    expect(screen.getByText('Sending…')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Stop' }).getAnimations({ subtree: true })
      .some(animation => animation.effect?.getTiming().iterations === Infinity)).toBe(false);
    expect(screen.queryByRole('button', { name: 'Sending…' })).toBeNull();
  } finally { cleanup(); view.answer(); view.client.clear(); }
});

it('provides the same immediate message and pending feedback under reduced motion', async () => {
  await commands.emulateReducedMotion(true);
  const view = mount({ persistSent: true });
  try {
    await open(view);
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    expectReadableMessage();
    const pendingSend = screen.getByRole('button', { name: 'Send' });
    expect(pendingSend.hasAttribute('disabled')).toBe(true);
    expect(pendingSend.getAnimations({ subtree: true })
      .some(animation => animation.effect?.getTiming().iterations === Infinity && animation.playState === 'running')).toBe(false);

    expect(screen.getByText('Sending…')).toBeDefined();
    await act(async () => { view.answer(); await Promise.resolve(); });
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
    expectReadableMessage();
  } finally { cleanup(); view.answer(); view.client.clear(); }
});


it.each([1400, 390])('keeps the first message bubble and mentions consistent through refusal and adoption at %ipx', async width => {
  await page.viewport(width, 900);
  await commands.emulateReducedMotion(width === 390);
  const message = 'Please read @`/研发/Release notes/report.md` before continuing.\nKeep these words together.';
  const view = mount({ persistSent: true, message });
  const appearance = () => {
    const bubble = outgoing();
    const style = getComputedStyle(bubble);
    return { padding: style.padding, borderRadius: style.borderRadius, background: style.backgroundColor,
      fontSize: style.fontSize, lineHeight: style.lineHeight, whiteSpace: style.whiteSpace, maxWidth: style.maxWidth };
  };
  const position = () => {
    const bubble = outgoing().getBoundingClientRect();
    const scroller = outgoing().closest<HTMLElement>('[data-nc-drawer-scroll]')!;
    const pane = scroller.getBoundingClientRect();
    return { top: bubble.top - pane.top + scroller.scrollTop, right: bubble.right - pane.right,
      width: bubble.width, height: bubble.height };
  };
  try {
    await open(view, true, message, width === 390);
    // Compare resting bubble layout, not two frames of the parent's entry projection.
    const drawer = screen.getByRole('button', { name: 'Close conversation' }).closest<HTMLElement>('[data-nc-drawer]')!;
    await expect.poll(() => drawer.getAnimations().length).toBe(0);
    act(() => { fireEvent.keyDown(field(), { key: 'Enter' }); });
    // Real bubble geometry and native presentation, not a CSS-module name oracle.
    const before = appearance();
    expect(parseFloat(before.padding)).toBeGreaterThan(0);
    expect(before.whiteSpace).toBe('pre-wrap');
    const container = outgoing().parentElement!;
    const parent = container.getBoundingClientRect();
    const containerStyle = getComputedStyle(container);
    const contentRight = parent.right - parseFloat(containerStyle.paddingRight) - parseFloat(containerStyle.borderRightWidth);
    const bubble = outgoing().getBoundingClientRect();
    expect(Math.abs(contentRight - bubble.right)).toBeLessThan(1);
    expect(bubble.width).toBeLessThan(parent.width);
    expect(outgoing().querySelector('[data-nc-sent-mention]')?.getAttribute('title')).toBe('@`/研发/Release notes/report.md`');
    const visible = outgoing().textContent;
    const beforePosition = position();
    await act(async () => { view.answer({ status: 429, statusText: 'Too Many Requests', body: { code: 'rate_limited', error: 'Wait before retrying.' } }); await Promise.resolve(); });
    await screen.findByRole('button', { name: 'Try again' });
    expect(appearance()).toEqual(before);
    expect(outgoing().textContent).toBe(visible);
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await act(async () => { view.answer(); await Promise.resolve(); });
    await waitFor(() => expect(view.canonicalReads()).toBeGreaterThan(0));
    await waitFor(() => expect(view.client.isFetching()).toBe(0));
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
    await waitFor(() => expect(outgoing().textContent).toBe(visible));
    expect(appearance()).toEqual(before);
    expect(userMessages()).toHaveLength(1);
    expect(outgoing().getAttribute('data-nc-turn')).toBe('unknown');
    expect(outgoing().querySelectorAll('[data-nc-sent-mention]')).toHaveLength(1);
    const afterPosition = position();
    expect(afterPosition.top).toBeCloseTo(beforePosition.top, 0);
    expect(afterPosition.right).toBeCloseTo(beforePosition.right, 0);
    expect(afterPosition.width).toBeCloseTo(beforePosition.width, 0);
    expect(afterPosition.height).toBeCloseTo(beforePosition.height, 0);
    expect(getComputedStyle(outgoing()).opacity).toBe('1');
    expect(getComputedStyle(outgoing()).transform).toBe('none');
  } finally { cleanup(); view.answer(); view.client.clear(); }
});

it('replays a lost session-creation reply with the same key and adopts the server session', async () => {
  await page.viewport(1400, 900);
  const session = { id: 'ses_created_on_server', trackId: 'daily', provider: 'codex', title: 'Recovered conversation',
    status: 'idle', createdAtMs: 2, updatedAtMs: 3 };
  const firstInput = { inputId: 'inp_server_message', turnId: 'trn_server', sessionId: session.id,
    deliveryState: 'delivered', turnStatus: 'completed', resolvedAtMs: null,
    statusUrl: `/api/v1/sessions/${session.id}/inputs/inp_server_message` };
  const followUp = 'A second message with a stored receipt.';
  const laterInput = { ...firstInput, inputId: 'inp_follow_up', turnId: 'trn_follow_up',
    statusUrl: `/api/v1/sessions/${session.id}/inputs/inp_follow_up` };
  let followUpAccepted = false;
  const attempts: Array<{ key: string | undefined; body: unknown }> = [];
  const view = renderDailyFixture({ initial: '/track/daily?panel=conversations',
    identity: { userId: 'local-owner', displayName: 'Owner', role: 'owner', sessionId: 'owner-session' },
    reply: request => {
    if (request.path === '/api/v1/bootstrap') return ok({ version: 'test', providers: [], areas: [], templates: [] });
    if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok([{
      availability: { provider: 'codex', status: 'ready', reason: null, checked_at_ms: 1, authentication_method: null, authentication_notice: null },
      accepts_unavailable_blank_track: true, capabilities: { managed_conversation: true, steer: true, images: true,
        manual_compact: true, history_replacement: false, requires_ready_availability: false },
    }]);
    if (request.path === '/api/v1/tracks/daily/sessions') {
      if (request.method !== 'POST') return ok(attempts.length > 0 ? [session] : []);
      attempts.push({ key: request.headers?.['Idempotency-Key'], body: request.body });
      if (attempts.length === 1) return Promise.reject(new TypeError('Response dropped after commit'));
      return ok({ session, firstInput });
    }
    if (request.path === `/api/v1/sessions/${session.id}/messages` && request.method === 'POST') {
      followUpAccepted = true;
      return { status: 202, statusText: 'Accepted', body: laterInput };
    }
    if (request.path.includes(`/sessions/${session.id}/items`)) return ok({ session, activeTurn: null, unknownInput: null, items: [
      { id: 1, sessionId: session.id, turnId: firstInput.turnId, kind: 'user', createdAtMs: 2,
        payload: { content: text, inputId: firstInput.inputId } },
      { id: 2, sessionId: session.id, turnId: firstInput.turnId, kind: 'assistant_text', createdAtMs: 3,
        payload: { text: 'The one stored answer.' } },
      ...(followUpAccepted ? [{ id: 3, sessionId: session.id, turnId: laterInput.turnId, kind: 'user', createdAtMs: 4,
        payload: { content: followUp, inputId: laterInput.inputId } }] : []),
    ] });
    return undefined;
  } });
  try {
    fireEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    await waitFor(() => expect(view.client.getQueryData(['server-version'])).toMatchObject({ sessionInputReceipts: true }));
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
    await act(async () => { field().textContent = text; fireEvent.input(field()); await Promise.resolve(); });
    fireEvent.keyDown(field(), { key: 'Enter' });
    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));
    await screen.findByText('The one stored answer.');
    expect(attempts).toHaveLength(2);
    expect(attempts[0].key).toBeTruthy();
    expect(attempts[1]).toEqual(attempts[0]);
    expect(attempts[0].body).toMatchObject({ text });
    expect(view.requests.filter(request => request.method === 'POST' && request.path.endsWith('/messages'))).toHaveLength(0);
    expect(screen.getAllByText(text, { exact: true })).toHaveLength(1);

    await act(async () => { field().textContent = followUp; fireEvent.input(field()); await Promise.resolve(); });
    fireEvent.keyDown(field(), { key: 'Enter' });
    await waitFor(() => expect(followUpAccepted).toBe(true));
    await waitFor(() => expect(screen.getAllByText(followUp, { exact: true })).toHaveLength(1));
    await waitFor(() => expect(field().getAttribute('contenteditable')).toBe('true'));
  } finally { cleanup(); view.client.clear(); }
});
