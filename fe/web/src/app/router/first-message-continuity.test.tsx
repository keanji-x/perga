import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import type { SessionCreationReceipt } from '../../../../core/api/perga-v1.ts';
import { fixtureCreation, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { sessionItem, sessionPage } from './conversation-session-data.ts';
import { queryKeys } from '../providers/queries.ts';

const FIRST_MESSAGE = 'Keep the first sentence readable through adoption.';
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

function setup(receiptChange?: (receipt: SessionCreationReceipt) => SessionCreationReceipt) {
  const session = fixtureSession('server-assigned-session', 'daily', { title: 'Assistant' });
  let landed = false;
  let answer!: () => void;
  const held = new Promise<void>(resolve => { answer = resolve; });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if ((request.path === '/api/v1/bootstrap' || request.path.startsWith('/api/v1/bootstrap?scope=managed'))) return ok({ version: '1.0.0', providers: [{ provider: 'codex', available: true, models: [] }], templates: [], areas: [] });
    if (request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(fixtureTrack('daily'), landed ? [session] : []));
    if (request.path === '/api/v1/tracks/daily/sessions' && request.method === 'POST') {
      await held;
      const receipt = fixtureCreation(session);
      landed = true;
      return { status: 201, statusText: 'Created', body: receiptChange?.(receipt) ?? receipt };
    }
    if (request.path === '/api/v1/tracks/daily/sessions') return ok(landed ? [session] : []);
    if (request.path.startsWith(`/api/v1/sessions/${session.id}/items?`)) return ok(sessionPage(session, [
      { ...sessionItem(session.id, 1, FIRST_MESSAGE, 'user', 'first-turn'), payload: { content: FIRST_MESSAGE, inputId: 'first-input' } },
    ]));
    if (request.path === fixtureCreation(session).firstInput!.statusUrl) return ok(fixtureCreation(session).firstInput);
    return undefined;
  } });
  const creates = () => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'));
  return { ...fixture, creates, answer, session };
}

async function submit() {
  fireEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
  const field = await screen.findByRole('combobox', { name: 'Message' });
  await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));
  field.textContent = FIRST_MESSAGE;
  const range = document.createRange(); range.selectNodeContents(field); range.collapse(false);
  const selection = window.getSelection()!; selection.removeAllRanges(); selection.addRange(range);
  await act(async () => { fireEvent.input(field); await Promise.resolve(); });
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
}

it('adopts the server session identity and reads the first message without another send', async () => {
  const fixture = setup();
  await submit();
  await waitFor(() => expect(fixture.creates()).toHaveLength(1));
  act(() => { fixture.answer(); });
  await screen.findByRole('complementary', { name: 'Assistant' });
  await waitFor(() => expect(document.querySelectorAll('[data-nc-turn="you"], [data-nc-turn="unknown"], [data-nc-turn="human"]')).toHaveLength(1));
  expect(screen.getByText(FIRST_MESSAGE)).toBeTruthy();
  expect(fixture.creates()[0].body).toMatchObject({ text: FIRST_MESSAGE });
  expect(fixture.requests.some(request => request.path.includes(`/${fixture.session.id}/items`))).toBe(true);
  expect(fixture.requests.some(request => request.path.endsWith('/initial-input') || request.path.endsWith('/messages'))).toBe(false);
  fireEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackDetail('daily') }); });
  fireEvent.click(await screen.findByRole('button', { name: 'Conversation Assistant' }));
  await screen.findByText(FIRST_MESSAGE);
  expect(fixture.creates()).toHaveLength(1);
});

it.each(['missing', 'wrong-session'] as const)('keeps the draft recoverable when firstInput is %s', async mode => {
  const fixture = setup(receipt => ({ ...receipt, firstInput: mode === 'missing' ? null : { ...receipt.firstInput!, sessionId: 'another-session' } }));
  await submit();
  await waitFor(() => expect(fixture.creates()).toHaveLength(1));
  act(() => { fixture.answer(); });
  await screen.findByRole('button', { name: 'Try again' });
  expect(fixture.creates()).toHaveLength(1);
  expect(fixture.requests.filter((request: ApiRequest) => request.path.endsWith('/messages'))).toHaveLength(0);
});
