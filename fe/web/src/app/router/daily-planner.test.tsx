import { cleanup,screen,waitFor,within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,expect,it } from 'vitest';
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
  expect(requests.some((request) => request.path.includes('limit=1'))).toBe(true);
  expect(requests.every((request) => request.method === 'GET')).toBe(true);
  await userEvent.keyboard('{Escape}');
  await userEvent.click(await screen.findByRole('button', { name: 'Project evidence' }));
  await waitFor(() => expect(router.state.location.pathname).toBe('/track/project'));
  await screen.findByText('Release is ready.', {}, { timeout: 5_000 });
});

it('keeps historical daily links readable without a second date toolbar', async () => {
  renderDailyFixture({ initial: '/?day=2026-10-03' });
  await screen.findByText('No daily Track was created for this date.');
});

it('shows the existing history retry in Today when conversation history cannot load', async () => {
  renderDailyFixture({ reply: request => (request.path.includes('/items') && !request.path.includes('limit=1'))
    ? { status: 500, statusText: 'Error', body: { error: 'unavailable' } } : undefined });
  const assistant = await screen.findByRole('region', { name: 'Today Assistant' });
  await within(assistant).findByRole('button', { name: '刷新' });
});

it('keeps a historical Perga daily Report readable without triage or Supervisor', async () => {
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  await screen.findByText('Prioritize the release.', { exact: false }, { timeout: 5_000 });
  expect(screen.queryByRole('region', { name: 'Today triage' })).toBeNull();
  expect(screen.queryByText('源文件夹模式 · 未隔离')).toBeNull();
  expect(screen.queryByText('Daily Supervisor')).toBeNull();
  expect(requests.some(request => request.path === '/api/v1/today/daily?date=2026-10-04')).toBe(true);
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
});


it('preserves the isolation badge on regular Perga repo Tracks', async () => {
  renderDailyFixture({ initial: '/track/project' });
  await screen.findByText('Release is ready.', {}, { timeout: 5_000 });
  expect(screen.getAllByText('源文件夹模式 · 未隔离').length).toBeGreaterThan(0);
});

it.each([false, true])('shows perga Conversations only with multiple conversations (extra=%s)', async extraPlanner => {
  renderDailyFixture({ extraPlanner });
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
  renderDailyFixture({ reportBody });
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
    initial: '/?day=2026-10-03',
    reply: request => {
      if (request.path === '/api/v1/today/calendar?start=2026-10-01&end=2026-10-31') {
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
