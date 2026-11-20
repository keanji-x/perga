import { cleanup, screen, waitFor, within, fireEvent, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it } from 'vitest';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(cleanup);

it('uses the real Planner and opens cited reports from the daily homepage without creation writes', async () => {
  const { router, requests } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  // Functional content waits include admission; production latency is measured separately.
  await screen.findByText('Prioritize the release. Read', { exact: false }, { timeout: 5_000 });
  expect((await screen.findByRole('button', { name: 'Rename track' })).textContent).toBe('2026-10-04');
  expect(screen.queryByRole('navigation', { name: 'Daily Planner dates' })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Open daily Planner' })).toBeNull();
  expect(screen.queryByText('Earlier Today report')).toBeNull();
  const sidebar = screen.getByRole('navigation', { name: 'Workspace' });
  expect(within(sidebar).queryByText('2026-10-04')).toBeNull();
  expect(within(sidebar).queryByText('system')).toBeNull();
  await userEvent.click(await screen.findByRole('button', { name: '讨论这项工作' }));
  await screen.findByRole('complementary', { name: 'Daily Planner conversation' });
  expect(requests.some((request) => request.path.includes('view=run'))).toBe(true);
  expect(requests.every((request) => request.method === 'GET')).toBe(true);
  await userEvent.keyboard('{Escape}');
  await userEvent.click(await screen.findByRole('button', { name: 'Project evidence' }));
  await waitFor(() => expect(router.state.location.pathname).toBe('/track/project'));
  await screen.findByText('Release is ready.', {}, { timeout: 5_000 });
});

it('reads yesterday’s report changes and individual edits with the server snapshot cursor', async () => {
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  await userEvent.click(await screen.findByText('Report changes'));
  const source = await screen.findByRole('heading', { name: /Project evidence.*2 report edits/ });
  expect(requests.some((request) => request.path.startsWith('/api/today/report-edits'))).toBe(false);
  await userEvent.click(source);
  expect((screen.getByText('Body changes').closest('details') as HTMLDetailsElement).open).toBe(false);
  await screen.findByText('Previous summary');
  await userEvent.click(screen.getByText('Individual edits'));
  await userEvent.click(await screen.findByRole('heading', { name: /Report edit.*09:00:00/ }));
  await userEvent.click(screen.getByText('After', { exact: true }));
  await screen.findByRole('heading', { name: 'After release' });
  expect(requests.some((request) => request.path === '/api/today/report-edits?date=2026-10-03&track_id=project&through_event_id=42')).toBe(true);
  await userEvent.click(screen.getByRole('button', { name: 'Back to Today' }));
  expect((screen.getByText('Report changes').closest('details') as HTMLDetailsElement).open).toBe(false);
});

it('keeps report history failures distinct from no changes', async () => {
  renderDailyFixture({ failChanges: true });
  await userEvent.click(await screen.findByText('Report changes'));
  /* A 500's text is the server's internals: only the fixed sentence shows. */
  await screen.findByText('Report changes could not be loaded.');
  expect(screen.queryByText(/Report history unavailable/)).toBeNull();
  expect(screen.queryByText('No report changes recorded for visible Tracks on this day.')).toBeNull();
});

it('keeps historical daily links readable without a second date toolbar', async () => {
  renderDailyFixture({ initial: '/?day=2026-10-03' });
  await screen.findByText('No daily Track was created for this date.');
});


it.each(['/today/legacy', '/today/legacy?day=2026-10-04'])('redirects %s to the sole Today renderer without singleton requests', async (initial) => {
  const { router, requests } = renderDailyFixture({ initial });
  if (initial.includes('?')) await screen.findByText('Prioritize the release.', { exact: false }, { timeout: 5_000 });
  else await screen.findByRole('region', { name: 'Today Assistant' });
  expect(router.state.location.pathname).toBe('/');
  expect(requests.some((request) => request.path.includes('/today/launchpad'))).toBe(false);
  if (initial.includes('?')) expect(router.state.location.search).toEqual({ day: '2026-10-04' });
});

it('opens Today Assistant in the center and returns from report changes with its draft', async () => {
  renderDailyFixture();
  const assistant = await screen.findByRole('region', { name: 'Today Assistant' });
  const field = await within(assistant).findByRole('combobox', { name: 'Message' });
  await waitFor(() => expect(assistant.getAttribute('data-nc-centered-footer')).toBe(''));
  await userEvent.click(field);
  await userEvent.type(field, 'Keep this daily draft');
  expect(within(assistant).queryByText('聊聊你想做什么')).toBeNull();
  expect(within(assistant).queryByText('从一个问题或想法开始。')).toBeNull();
  expect(assistant.querySelector('[data-nc-thread-empty]')).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Report changes' }));
  await screen.findByRole('button', { name: 'Back to Today Assistant' });
  expect(screen.queryByRole('region', { name: 'Today Assistant' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Back to Today Assistant' }));
  expect(within(screen.getByRole('region', { name: 'Today Assistant' })).getByRole('combobox', { name: 'Message' }).textContent).toContain('Keep this daily draft');
});


it('sends from Today through the existing Planner endpoint and shows a refused send', async () => {
  const { requests } = renderDailyFixture({ reply: request => request.path.endsWith('/messages')
    ? { status: 403, statusText: 'Forbidden', body: { error: 'forbidden' } } : undefined });
  const assistant = await screen.findByRole('region', { name: 'Today Assistant' });
  const field = await within(assistant).findByRole('combobox', { name: 'Message' });
  field.textContent = 'Plan the release';
  const range = document.createRange();
  range.selectNodeContents(field);
  range.collapse(false);
  window.getSelection()!.removeAllRanges();
  window.getSelection()!.addRange(range);
  await act(async () => { fireEvent.input(field); await Promise.resolve(); });
  const send = within(assistant).getByRole('button', { name: 'Send' });
  await waitFor(() => expect(send.getAttribute('aria-disabled')).not.toBe('true'));
  await waitFor(() => expect(send.hasAttribute('disabled')).toBe(false));
  await userEvent.click(send);
  await waitFor(() => expect(requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(1));
  await within(assistant).findByText(/^消息未发送。/);
  expect(within(assistant).getByRole('button', { name: 'Report changes' })).toBeTruthy();
  const input = requests.filter(request => request.path.endsWith('/messages'));
  expect(input).toHaveLength(1);
  expect(input[0].path).toBe('/api/v1/sessions/daily-planner/messages');
  expect(input[0].method).toBe('POST');
});

it('shows the existing history retry in Today when conversation history cannot load', async () => {
  renderDailyFixture({ reply: request => (request.path.includes('/items') && !request.path.includes('view='))
    ? { status: 500, statusText: 'Error', body: { error: 'unavailable' } } : undefined });
  const assistant = await screen.findByRole('region', { name: 'Today Assistant' });
  await within(assistant).findByRole('button', { name: '刷新' });
});


it('returns to the center conversation when selected from the right inventory', async () => {
  renderDailyFixture();
  await screen.findByRole('region', { name: 'Today Assistant' });
  await userEvent.click(screen.getByRole('button', { name: 'Report changes' }));
  await screen.findByRole('button', { name: 'Back to Today Assistant' });
  await userEvent.click(screen.getByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByRole('region', { name: 'Today Assistant' });
  expect(screen.queryByRole('button', { name: 'Back to Today Assistant' })).toBeNull();
  expect(screen.getByRole('button', { name: 'New conversation' })).toBeTruthy();
});

it('renders the Perga daily Report and Assistant without triage and never requests Supervisor or launchpad', async () => {
  const { requests } = renderDailyFixture({ pergaMode: true, identity: { userId: 'local-owner', displayName: 'Owner', role: 'owner', sessionId: 'perga-owner' } });
  await screen.findByRole('region', { name: 'Today Assistant' });
  expect(screen.queryByRole('region', { name: 'Today triage' })).toBeNull();
  expect(screen.queryByText(/Waiting on you/)).toBeNull();
  expect(screen.queryByText('源文件夹模式 · 未隔离')).toBeNull();
  expect(screen.queryByText('Daily Supervisor')).toBeNull();
  expect(screen.queryByText('Report changes')).toBeNull();
  expect(requests.some(request => request.path.startsWith('/api/today/daily'))).toBe(true);
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
});

it('keeps a historical Perga daily Report readable without triage or Supervisor', async () => {
  const { requests } = renderDailyFixture({ pergaMode: true, initial: '/?day=2026-10-04' });
  await screen.findByText('Prioritize the release.', { exact: false }, { timeout: 5_000 });
  expect(screen.queryByRole('region', { name: 'Today triage' })).toBeNull();
  expect(screen.queryByText('源文件夹模式 · 未隔离')).toBeNull();
  expect(screen.queryByText('Daily Supervisor')).toBeNull();
  expect(requests.some(request => request.path === '/api/today/daily?date=2026-10-04')).toBe(true);
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
});


it('preserves the isolation badge on regular Perga repo Tracks', async () => {
  renderDailyFixture({ pergaMode: true, initial: '/track/project' });
  await screen.findByText('Release is ready.', {}, { timeout: 5_000 });
  expect(screen.getAllByText('源文件夹模式 · 未隔离').length).toBeGreaterThan(0);
});

it('keeps the perga Today masthead minimal and navigates routes on calendar selection', async () => {
  const { router, requests } = renderDailyFixture({ pergaMode: true });
  const assistant = await screen.findByRole('region', { name: 'Today Assistant' });
  const heading = await within(assistant).findByRole('heading', { name: '今天想推进什么？' });
  expect(heading.querySelector('[data-nc-motion="creation"]')).toBeTruthy();
  expect(within(assistant).queryByText('说说今天的重点，计划与进展会整理在右侧日报里。')).toBeNull();
  expect(assistant.querySelector('[data-nc-thread-empty]')).toBeNull();
  expect(screen.queryByText('Report changes')).toBeNull();
  const calendar = await screen.findByRole('region', { name: 'Calendar' });
  const panel = calendar.closest('[data-nc-desktop-panel]')!;
  expect(panel).toBeTruthy();
  expect(within(panel as HTMLElement).queryByText('Project evidence')).toBeNull();
  expect(within(panel as HTMLElement).queryByText(/Active tracks/i)).toBeNull();
  const today = within(calendar).getByRole('button', { name: '2026-10-04' });
  expect(today.getAttribute('aria-pressed')).toBe('true');
  expect(today.hasAttribute('data-nc-today')).toBe(true);
  await userEvent.click(within(calendar).getByRole('button', { name: '2026-10-03' }));
  await waitFor(() => expect((router.state.location.search as { day?: string }).day).toBe('2026-10-03'));
  await screen.findByText('No daily Track was created for this date.');
  const updatedCalendar = await screen.findByRole('region', { name: 'Calendar' });
  expect(within(updatedCalendar).getByRole('button', { name: '2026-10-03' }).getAttribute('aria-pressed')).toBe('true');
  expect(within(updatedCalendar).getByRole('button', { name: '2026-10-04' }).getAttribute('aria-pressed')).toBe('false');
  expect(requests.filter(request => request.path.startsWith('/api/today/daily'))).toHaveLength(2);
});

it.each([false, true])('shows perga Conversations only with multiple conversations (extra=%s)', async extraPlanner => {
  renderDailyFixture({ pergaMode: true, extraPlanner });
  await screen.findByRole('region', { name: 'Today Assistant' });
  await screen.findByText('Prioritize the release.', { exact: false });
  if (extraPlanner) {
    const conversations = await screen.findByRole('region', { name: 'Conversations' });
    expect(within(conversations).getByRole('button', { name: 'Conversation Other conversation' })).toBeTruthy();
    expect(within(conversations).getByRole('button', { name: 'Conversation Daily Planner conversation' })).toBeTruthy();
  } else {
    expect(screen.queryByRole('region', { name: 'Conversations' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'New conversation' })).toBeNull();
  }
});

it.each(['', '   \n  ', '# 今日计划\n\nA populated daily report.'])('shows the perga Daily report module only for populated content (%s)', async reportBody => {
  renderDailyFixture({ pergaMode: true, reportBody });
  await screen.findByRole('region', { name: 'Today Assistant' });
  if (reportBody.includes('A populated')) {
    await screen.findByText('A populated daily report.');
    expect(screen.getByRole('heading', { name: 'Daily report' })).toBeTruthy();
  } else {
    await waitFor(() => expect(document.querySelector('[data-nc-centered-footer]')).toBeTruthy());
    expect(screen.queryByRole('heading', { name: 'Daily report' })).toBeNull();
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Track report' })?.getAttribute('aria-busy')).not.toBe('true'));
    expect(screen.queryByText('进展会整理在这里')).toBeNull();
    expect(screen.queryByRole('region', { name: 'Track report' })).toBeNull();
    expect(screen.queryByRole('region', { name: '空白日报' })).toBeNull();
  }
});

it('keeps the perga calendar available without a daily track, preserves Month mode across route navigation, and surfaces calendar errors', async () => {
  const { router } = renderDailyFixture({
    pergaMode: true,
    initial: '/?day=2026-10-03',
    reply: request => {
      if (request.path === '/api/today/calendar?start=2026-10-01&end=2026-10-31') {
        return { status: 500, statusText: 'Error', body: { error: { code: 'internal', message: 'Calendar unavailable' } } };
      }
      return undefined;
    },
  });
  await screen.findByText('No daily Track was created for this date.');
  const calendar = await screen.findByRole('region', { name: 'Calendar' });
  await userEvent.click(screen.getByRole('radio', { name: 'Month' }));
  await within(calendar).findByText('Calendar data could not be loaded.');
  await userEvent.click(within(calendar).getByRole('button', { name: '2026-10-02' }));
  await waitFor(() => expect((router.state.location.search as { day?: string }).day).toBe('2026-10-02'));
  expect(screen.getByRole('radio', { name: 'Month' }).getAttribute('aria-checked')).toBe('true');
  await userEvent.click(within(calendar).getByRole('button', { name: 'Today' }));
  await waitFor(() => expect((router.state.location.search as { day?: string }).day).toBeUndefined());
  await screen.findByRole('region', { name: 'Today Assistant' });
  expect(screen.getByRole('radio', { name: 'Month' }).getAttribute('aria-checked')).toBe('true');
});
