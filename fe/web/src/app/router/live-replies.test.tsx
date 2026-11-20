// Replies read through the production router using the v1 session items resource.
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { SessionItemsPageDto } from '../../../../core/api/perga-v1.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession } from './session-contract-fixture.ts';
import { sessionItem, sessionPage, sessionTurn } from './conversation-session-data.ts';
import { queryKeys } from '../providers/queries.ts';

const SESSION = 'daily-planner';
function setup(initial: SessionItemsPageDto) {
  let page = initial;
  let failHistory = false;
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (!request.path.includes(`/sessions/${SESSION}/items?`)) return undefined;
    if (failHistory && !request.path.includes('view=')) return { status: 503, statusText: 'Unavailable', body: null };
    return { status: 200, statusText: 'OK', body: page };
  } });
  return { ...fixture, page: (value: SessionItemsPageDto) => { page = value; }, fail: () => { failHistory = true; },
    refresh: async () => { await act(async () => {
      await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(SESSION) });
      await fixture.client.invalidateQueries({ queryKey: queryKeys.harnessItems(SESSION) });
    }); } };
}
function runningPage() {
  return sessionPage(fixtureSession(SESSION, 'daily', { status: 'running' }),
    [sessionItem(SESSION, 1, 'question', 'user'), sessionItem(SESSION, 2, 'Stored answer')], sessionTurn(SESSION));
}
async function open(name = 'Daily Planner conversation') {
  const close = screen.queryByRole('button', { name: 'Close conversation' });
  if (close !== null) fireEvent.click(close);
  fireEvent.click(await screen.findByRole('button', { name: `Conversation ${name}` }));
  await screen.findByRole('complementary', { name });
}
const historyTexts = () => [...document.querySelectorAll('[data-nc-thread] [data-nc-turn]')].map(el => el.textContent);
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('renders persisted replies once while the v1 live read returns the same session page', async () => {
  const fixture = setup(runningPage());
  await open();
  await screen.findByText('Stored answer');
  await screen.findByRole('button', { name: 'Stop' });
  await waitFor(() => expect(fixture.requests.some(request => request.path.includes('view=live'))).toBe(true));
  expect(historyTexts()).toEqual(['question', 'Stored answer']);
});
it('refreshes stored reply text without appending a duplicate', async () => {
  const fixture = setup(runningPage());
  await open();
  await screen.findByText('Stored answer');
  fixture.page(sessionPage(fixtureSession(SESSION, 'daily', { status: 'running' }),
    [sessionItem(SESSION, 1, 'question', 'user'), sessionItem(SESSION, 2, 'Updated answer')], sessionTurn(SESSION)));
  await fixture.refresh();
  await screen.findByText('Updated answer');
  expect(screen.queryByText('Stored answer')).toBeNull();
  expect(historyTexts()).toEqual(['question', 'Updated answer']);
});
it.each(['completed', 'interrupted', 'failed'])('keeps recorded text when the active turn settles as %s', async status => {
  const fixture = setup(runningPage());
  await open();
  await screen.findByRole('button', { name: 'Stop' });
  fixture.page(sessionPage(fixtureSession(SESSION, 'daily'), runningPage().items,
    { ...sessionTurn(SESSION), status, completedAtMs: Date.now() }));
  await fixture.refresh();
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Stop' })).toBeNull());
  expect(historyTexts().slice(0, 2)).toEqual(['question', 'Stored answer']);
  expect(historyTexts()[2]).toContain(status === 'completed' ? '完成' : status === 'interrupted' ? '本轮已中断' : '本轮执行失败');
});
it('never shows one session’s persisted replies in another', async () => {
  setup(runningPage());
  await open();
  await screen.findByText('Stored answer');
  await open('Other conversation');
  expect(screen.queryByText('Stored answer')).toBeNull();
  await open();
  await screen.findByText('Stored answer');
  expect(historyTexts()).toEqual(['question', 'Stored answer']);
});
it('keeps the last successful history visible after a history read fails', async () => {
  const fixture = setup(runningPage());
  await open();
  await screen.findByText('Stored answer');
  fixture.fail();
  await fixture.refresh();
  await waitFor(() => expect(fixture.client.getQueryState([...queryKeys.harnessItems(SESSION), 'conversation_v1'])?.status).toBe('error'));
  expect(historyTexts()).toEqual(['question', 'Stored answer']);
});
