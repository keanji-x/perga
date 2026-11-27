// Replies read through the production router using the v1 session items resource.
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import type { SessionItemsPageDto } from '../../../../core/api/perga-v1.ts';
import { REPORT_READ_TOOLS } from '../../../../core/keys/mcp-tools.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession } from './session-contract-fixture.ts';
import { sessionItem, sessionPage, sessionTurn } from './conversation-session-data.ts';
import { queryKeys } from '../providers/query-keys.ts';

const SESSION = 'daily-planner';
function setup(initial: SessionItemsPageDto) {
  let page = initial;
  let failHistory = false;
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: true, reply: request => {
    if (!request.path.includes(`/sessions/${SESSION}/items?`)) return undefined;
    if (failHistory && !request.path.includes('limit=1')) return { status: 503, statusText: 'Unavailable', body: null };
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
  await waitFor(() => expect(fixture.requests.some(request => request.path.includes('limit=1'))).toBe(true));
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

it('uses the persisted tool wording and target and announces a truncated live failure', async () => {
  const name = REPORT_READ_TOOLS[0];
  setup({
    ...runningPage(),
    items: [sessionItem(SESSION, 1, 'question', 'user')],
    activity: {
      turnId: 'turn-1', sequence: 2, reset: false, text: '', truncated: false,
      events: [
        { sequence: 1, kind: 'session.tool_progress', createdAtMs: 1,
          payload: { callId: 'read', name, phase: 'started', args: { trackId: 'another-track' } } },
        { sequence: 2, kind: 'session.tool_progress', createdAtMs: 2,
          payload: { callId: 'read', name, phase: 'completed', isError: true, output: 'Access denied…', truncated: true } },
      ],
    },
  });

  await open();
  const label = await screen.findByText('Read report', { exact: true });
  const tool = label.closest<HTMLElement>('[role="button"]')!;
  expect(tool.textContent).toMatch(/^(Error|Failed)/);
  expect(within(tool).queryByText('another-track', { exact: true })).not.toBeNull();
  fireEvent.click(tool);
  expect(tool.parentElement?.textContent).toContain('Access denied…');
});

it('does not append completed calls preceding the first persisted tool in the live stream', async () => {
  const items = [
    sessionItem(SESSION, 1, 'question', 'user'),
    { ...sessionItem(SESSION, 61, '', 'tool_call'), payload: { callId: 'persisted', name: 'stored-tool', args: {} } },
    { ...sessionItem(SESSION, 62, '', 'tool_result'), payload: { callId: 'persisted', output: 'Done' } },
  ];
  const fixture = setup({
    ...runningPage(),
    items,
    hasMoreBefore: true,
    activity: {
      turnId: 'turn-1', sequence: 5, reset: false, text: '', truncated: false,
      // The poll window sees completion order after the persisted tool's start has aged out.
      events: [
        { sequence: 4, kind: 'session.tool_progress', createdAtMs: 4,
          payload: { callId: 'newer', name: 'new-tool', phase: 'completed', output: 'Done' } },
        { sequence: 5, kind: 'session.tool_progress', createdAtMs: 5,
          payload: { callId: 'persisted', name: 'stored-tool', phase: 'completed', output: 'Done' } },
      ],
    },
  });
  const tool = (callId: string, name: string, phase = 'completed') => ({ callId, name, phase, args: {}, isError: false });
  fixture.client.setQueryData(queryKeys.sessionLive(SESSION), {
    turnId: 'turn-1', text: '', thinking: '', updatedAtMs: 5,
    tools: [tool('older', 'paged-out-tool'), tool('pending', 'pending-tool', 'started'),
      tool('persisted', 'stored-tool'), tool('newer', 'new-tool')],
  });

  await open();
  const group = await screen.findByRole('group', { name: /tool calls$/ });
  expect(group.getAttribute('aria-label')).toBe('3 tool calls');
  fireEvent.click(group.querySelector('[aria-expanded="false"]')!);
  const tools = within(group);
  expect(tools.queryByText('stored-tool', { exact: true })).not.toBeNull();
  expect(tools.queryByText('pending-tool', { exact: true })).not.toBeNull();
  expect(tools.queryByText('new-tool', { exact: true })).not.toBeNull();
});

it('keeps a newer polled tool failure that completes before an earlier persisted tool', async () => {
  setup({
    ...runningPage(),
    items: [
      sessionItem(SESSION, 1, 'question', 'user'),
      { ...sessionItem(SESSION, 2, '', 'tool_call'), payload: { callId: 'earlier', name: 'earlier-tool', args: {} } },
      { ...sessionItem(SESSION, 4, '', 'tool_result'), payload: { callId: 'earlier', output: 'Done' } },
    ],
    activity: {
      turnId: 'turn-1', sequence: 4, reset: false, text: '', truncated: false,
      events: [
        { sequence: 3, kind: 'session.tool_progress', createdAtMs: 3,
          payload: { callId: 'newer', name: 'new-tool', phase: 'completed', isError: true, output: 'Parallel tool failed' } },
        { sequence: 4, kind: 'session.tool_progress', createdAtMs: 4,
          payload: { callId: 'earlier', name: 'earlier-tool', phase: 'completed', isError: false, output: 'Done' } },
      ],
    },
  });

  await open();
  const group = await screen.findByRole('group', { name: '2 tool calls' });
  fireEvent.click(group.querySelector('[aria-expanded="false"]')!);
  const failure = within(group).getByRole('button', { name: /^(Error|Failed).*new-tool$/ });
  fireEvent.click(failure);
  expect(group.textContent).toContain('Parallel tool failed');
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
  expect(historyTexts()[2]).toContain(status === 'completed' ? 'Done' : status === 'interrupted' ? 'Stopped' : 'Turn failed');
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
