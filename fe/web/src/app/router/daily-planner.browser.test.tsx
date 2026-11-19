import { fixtureSession, fixtureTrack, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionPage, sessionItem, sessionTurn } from './conversation-session-data.ts';
import { withFontRoleProbe } from '../../../../tools/styles/font-role-fixture.ts';
import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(async () => { cleanup(); await page.viewport(1280, 800); });

it('gives the daily mobile page one header and opens its scoped history', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ emptyWorkspace: true });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
  const headers = [...document.querySelectorAll('[data-nc-mobile-header]')].filter(header => header.getBoundingClientRect().width > 0);
  expect(headers).toHaveLength(1);
  await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
  await page.getByRole('button', { name: '2026-10-04 Daily Planner conversation', exact: true }).click();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
});

it.each([320, 390])('keeps mobile navigation labels and pointer targets usable at %ipx', async (width) => {
  await page.viewport(width, 844);
  renderDailyFixture({ initial: '/track/project' });
  await page.getByRole('button', { name: 'Switch track, Project evidence', exact: true }).click();
  const layer = page.getByRole('dialog', { name: 'Tracks and settings', exact: true }).element();
  await expect.poll(() => layer.getAnimations({ subtree: true }).filter(animation => animation.effect?.getTiming().iterations !== Infinity).every(animation => animation.playState !== 'running')).toBe(true);
  const bounds = layer.getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(width);
  const item = page.getByRole('button', { name: 'Project evidence', exact: true }).element();
  const text = item.querySelector('span:nth-child(2)')!;
  const box = text.getBoundingClientRect();
  expect(item.contains(document.elementFromPoint((box.left + box.right) / 2, (box.top + box.bottom) / 2))).toBe(true);
  await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Open workspace', exact: true })).toBeVisible();
});

it('reads a daily plan and yesterday’s report evidence in a real browser', async () => {
  await page.viewport(1440, 900);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  await expect.element(page.getByRole('button', { name: 'Rename track' })).toHaveTextContent('2026-10-04');
  await expect.element(page.getByRole('button', { name: /Conversation Daily Planner conversation/ })).toBeVisible();
  await page.getByText('Report changes').click();
  const source = page.getByRole('heading', { name: /Project evidence.*2 report edits/ });
  await expect.element(source).toBeVisible();
  const marker = source.element().querySelector('span:first-child')!;
  expect(getComputedStyle(marker).rotate).toBe('0deg');
  await source.click();
  await expect.poll(() => getComputedStyle(marker).rotate).toBe('90deg');
  await page.getByText('Individual edits').click();
  await page.getByRole('heading', { name: /Report edit.*09:00:00/ }).click();
  await page.getByText('After', { exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'After release' })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  expect(requests.every((request) => request.method === 'GET')).toBe(true);
  await page.screenshot({ path: './__screenshots__/daily-planner-desktop.png' });
});

it.each([
  ['/', 390, /昨天的更新/],
  ['/?day=2026-10-04', 1440, /Report changes/],
] as const)('closes %s report evidence with keyboard activation and restores focus to its summary', async (initial, width, heading) => {
  await page.viewport(width, 844);
  renderDailyFixture({ initial });
  const headingNode = await page.getByRole('heading', { name: heading }).findElement();
  const summary = headingNode.closest('summary')!;
  const details = summary.closest('details')!;
  expect(details.open).toBe(false);
  summary.focus();
  await expect.element(summary).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => details.open).toBe(true);
  const back = await page.getByRole('button', { name: 'Back to Today', exact: true }).findElement();
  back.focus();
  await expect.element(back).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => details.open).toBe(false);
  expect(page.getByRole('button', { name: 'Back to Today', exact: true }).query()).toBeNull();
  await expect.element(summary).toHaveFocus();
});


it('keeps the daily report usable at phone width', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04' });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
  const headers = [...document.querySelectorAll('[data-nc-mobile-header]')].filter((header) => header.getBoundingClientRect().width > 0);
  expect(headers).toHaveLength(1);
  await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
  await expect.element(page.getByRole('dialog', { name: '历史对话', exact: true })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
});


it.each([1000, 768, 600])('keeps the composer inside its conversation card with a long transcript at height %s', async (height) => {
  await page.viewport(1440, height);
  const history = Array.from({ length: 30 }, (_, index) => sessionItem('daily-planner', index + 1, `Reply ${index + 1}.\n\n${'A detailed progress update with enough content to need scrolling. '.repeat(12)}`));
  renderDailyFixture({ initial: '/?day=2026-10-04', reply: (request) => request.path.includes('/items?')
    ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), history) } : undefined });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  await page.getByRole('button', { name: '讨论这项工作', exact: true }).click();
  const drawer = page.getByRole('complementary', { name: 'Daily Planner conversation' });
  await expect.element(drawer).toBeVisible();
  await drawer.getByText('Reply 30.', { exact: false }).findElement();
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  const send = page.getByRole('button', { name: 'Send', exact: true });
  await expect.element(field).toBeVisible();
  const bounds = () => {
    const stack = drawer.element().closest('[data-nc-drawer-stack]')!;
    const clip = stack.closest('main')!;
    return { drawer: drawer.element().getBoundingClientRect(), clip: clip.getBoundingClientRect(),
      field: field.element().getBoundingClientRect(), send: send.element().getBoundingClientRect() };
  };
  await expect.poll(() => bounds().send.bottom <= bounds().clip.bottom).toBe(true);
  const boxes = bounds();
  expect(boxes.field.top).toBeGreaterThanOrEqual(boxes.drawer.top);
  expect(boxes.field.bottom).toBeLessThanOrEqual(boxes.drawer.bottom);
  expect(boxes.send.bottom).toBeLessThanOrEqual(Math.min(boxes.drawer.bottom, boxes.clip.bottom, height));
  const scroll = drawer.element().querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  expect(scroll.clientHeight).toBeGreaterThan(0);
  expect(scroll.scrollHeight).toBeGreaterThan(scroll.clientHeight);
  const draft = Array.from({ length: 12 }, (_, index) => `Draft line ${index + 1}`).join('\n');
  await field.fill(draft);
  await expect.element(field).toHaveTextContent('Draft line 12');
  expect(field.element().scrollHeight).toBeGreaterThan(field.element().clientHeight);
  await expect.poll(() => bounds().send.bottom <= bounds().clip.bottom).toBe(true);
  expect(bounds().send.bottom).toBeLessThanOrEqual(Math.min(bounds().drawer.bottom, height));
  await page.screenshot({ path: `../../../../test-results/today-long-conversation-${height}.png` });
});


it.each([1024, 1440])('aligns reading and chat headers independently of sidebar navigation sections at %ipx', async (width) => {
  await page.viewport(width, 900);
  renderDailyFixture({ initial: '/?day=2026-10-04' });
  await page.getByRole('button', { name: '讨论这项工作', exact: true }).click();
  const drawer = page.getByRole('complementary', { name: 'Daily Planner conversation' });
  await expect.element(drawer).toBeVisible();
  const readingHeader = document.querySelector('[data-nc-track-page] > header')!;
  const chatHeader = drawer.element().querySelector(':scope > header')!;
  // The drawer enters through a spring; compare settled content headers.
  await expect.poll(() => Math.abs(chatHeader.getBoundingClientRect().bottom - readingHeader.getBoundingClientRect().bottom)).toBeLessThanOrEqual(1);
  const today = page.getByRole('button', { name: 'Go to Today', exact: true }).element().lastElementChild!;
  const readingTitle = page.getByRole('button', { name: 'Rename track', exact: true }).element();
  const chatTitle = chatHeader.querySelector('h2')!;
  const center = (element: Element) => {
    const rect = element.getBoundingClientRect();
    return rect.top + rect.height / 2;
  };
  expect(Math.abs(center(today) - center(readingHeader))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(readingTitle) - center(readingHeader))).toBeLessThanOrEqual(1);
  await expect.poll(() => Math.abs(center(chatTitle) - center(chatHeader))).toBeLessThanOrEqual(1);
  withFontRoleProbe('--type-heading', readingHeader as HTMLElement, expected => {
    expect(getComputedStyle(readingTitle)).toMatchObject(expected);
  });
  withFontRoleProbe('--type-reading', chatHeader as HTMLElement, expected => {
    expect(getComputedStyle(chatTitle)).toMatchObject(expected);
  });
  await expect.poll(() => {
    const box = drawer.element().getBoundingClientRect();
    return Math.abs(box.top - (window.innerHeight - box.bottom));
  }).toBeLessThanOrEqual(1);
  expect(page.getByRole('button', { name: '讨论这项工作', exact: true }).query()).toBeNull();
});

it('uses the shared daily empty report for the heading-only skeleton', async () => {
  await page.viewport(1440, 900);
  renderDailyFixture({ initial: '/?day=2026-10-04', reportBody: '# 概要\n\n# 今日计划' });
  await expect.element(page.getByRole('heading', { name: '今天还没有记录' })).toBeVisible();
  expect(page.getByRole('button', { name: '开始对话', exact: true }).elements()).toHaveLength(0);
});

it('shows observed SQLite writes and deferred workspaces to the owner', async () => {
  const { requests } = renderDailyFixture({
    identity: { userId: 'local-owner', displayName: 'Owner', role: 'member', sessionId: 'supervisor' },
    reply: request => request.path === '/api/today/supervisor' ? {
      status: 200, statusText: 'OK', body: {
        observed_at_ms: 1, kernel_version: '1.2.3', build_sha: 'abc123',
        settled_worker_cleanup_candidates: 0, write_bucket_ms: 300000, write_bucket_count: 6, database_writes: [{ started_at_ms: 0, transactions: 5, failures: 1, retries: 2, mean_wait_ms: 3.5 }], scheduler_boot_sweep_completed: false, retired_workspaces: { count: 2, oldest_retired_at_ms: 1 },
      },
    } : undefined,
  });
  const supervisor = page.getByRole('button', { name: 'Daily Supervisor', exact: true });
  await expect.element(supervisor).toBeVisible();
  await supervisor.click();
  await expect.element(page.getByText('abc123', { exact: true })).not.toBeVisible();
  await expect.element(page.getByText('1.2.3', { exact: true })).not.toBeVisible();
  await expect.element(page.getByText('待清理 Worker 会话', { exact: true })).toBeVisible();
  expect(requests.some(request => request.path === '/api/today/supervisor')).toBe(true);
  await expect.element(page.getByRole('button', { name: '放大查看 Daily Supervisor' })).toBeVisible();
  await expect.element(page.getByRole('img', { name: /SQLite 写事务等待.*6 intervals/ })).toBeVisible();
  await expect.element(page.getByText('调度器启动扫描尚未完成', { exact: true })).toBeVisible();
  await page.getByText('实例明细', { exact: true }).click();
  await expect.element(page.getByText('abc123', { exact: true })).toBeVisible();
  await expect.element(page.getByText('最早退役时间')).toBeVisible();
  await expect.element(page.getByText('历史巡检', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Back to Today Assistant' })).toBeVisible();
});

it('does not show the Supervisor to a non-owner with the owner role label', async () => {
  const { requests } = renderDailyFixture({
    identity: { userId: 'other', displayName: 'Other', role: 'owner', sessionId: 'other' },
  });
  await expect.element(page.getByText('Report changes', { exact: true })).toBeVisible();
  expect(page.getByText('Daily Supervisor', { exact: true }).query()).toBeNull();
  expect(requests.some(request => request.path === '/api/today/supervisor')).toBe(false);
});

it('can inspect scheduler recovery before the daily Track exists', async () => {
  renderDailyFixture({
    identity: { userId: 'local-owner', displayName: 'Owner', role: 'owner', sessionId: 'preparing' },
    reply: request => {
      if (request.path === '/api/today/daily') return { status: 200, statusText: 'OK', body: null };
      if (request.path === '/api/today/supervisor') return { status: 200, statusText: 'OK', body: {
        observed_at_ms: 1, kernel_version: '1.2.3', build_sha: null,
        settled_worker_cleanup_candidates: 0, write_bucket_ms: 300000, write_bucket_count: 12, database_writes: [], scheduler_boot_sweep_completed: false, retired_workspaces: null,
      } };
    },
  });
  await expect.element(page.getByText('Preparing today’s Track…')).toBeVisible();
  await expect.element(page.getByRole('region', { name: 'Daily Supervisor' })).toBeVisible();
  await expect.element(page.getByText('调度器启动扫描尚未完成', { exact: true })).toBeVisible();
});

it.each([1024, 1440])('keeps the real Today conversation and draft in separate columns at %ipx', async width => {
  await page.viewport(width, 800);
  const { requests } = renderDailyFixture();
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeVisible();
  await field.fill('Help me plan today');
  const send = assistant.getByRole('button', { name: 'Send', exact: true });
  expect(send.element().getBoundingClientRect().bottom).toBeLessThanOrEqual(800);
  await page.screenshot({ path: `../../../../test-results/v10-today-assistant-${width}.png` });
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: /Report changes · 2026-10-03/ })).toBeVisible();
  await page.screenshot({ path: `../../../../test-results/v10-today-changes-${width}.png` });
  await page.getByRole('button', { name: 'Back to Today Assistant', exact: true }).click();
  await expect.element(field).toHaveTextContent('Help me plan today');
  expect(requests.some(request => request.path.includes('/items?afterId=0&limit=1&view=run'))).toBe(true);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
});

it('scrolls real Planner history above the fixed Today composer across views', async () => {
  await page.viewport(1440, 600);
  const history = Array.from({ length: 20 }, (_, index) => sessionItem('daily-planner', index + 1, `Daily reply ${index + 1}.\n\n${'Plan the release using the current evidence. '.repeat(20)}`));
  renderDailyFixture({ reply: request => request.path.includes('/items?')
    ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), history) } : undefined });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  await assistant.getByText('Daily reply 20.', { exact: false }).findElement();
  const scroll = assistant.element().querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  expect(scroll.scrollHeight).toBeGreaterThan(scroll.clientHeight);
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('A preserved draft');
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await page.getByRole('button', { name: 'Back to Today Assistant', exact: true }).click();
  await expect.element(field).toHaveTextContent('A preserved draft');
  expect(assistant.getByRole('button', { name: 'Send', exact: true }).element().getBoundingClientRect().bottom).toBeLessThanOrEqual(600);
  await page.screenshot({ path: '../../../../test-results/v10-today-history.png' });
});


it.each([false, true])('navigates loaded Today exchanges and restores the parked reading position (selected: %s)', async selected => {
  await page.viewport(1440, 700);
  const items = Array.from({ length: 8 }, (_, index) => [
    sessionItem('daily-planner', index * 2 + 1, `Today question ${index + 1}`, 'user', `turn-${index}`),
    sessionItem('daily-planner', index * 2 + 2, `Today reply ${index + 1}. ${'Read the release evidence carefully. '.repeat(30)}`, 'assistant_text', `turn-${index}`),
  ]).flat();
  renderDailyFixture({ reply: request => request.path.includes('/items?') ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), items) } : undefined });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('Keep my draft');
  const editor = field.element();
  await page.getByRole('button', { name: 'Jump to exchange 2: Today question 2', exact: true }).click();
  const scroll = assistant.element().querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  const marker = assistant.getByText('Today question 2', { exact: true }).element();
  await expect.poll(() => Math.abs(marker.getBoundingClientRect().top - scroll.getBoundingClientRect().top)).toBeLessThan(10);
  const overlay = assistant.element().querySelector<HTMLElement>('[data-nc-chat-scroll-overlay]')!;
  await expect.poll(() => overlay.querySelector('[data-nc-chat-scroll-dock]') !== null).toBe(true);
  const parked = scroll.scrollTop;
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await page.getByRole('button', { name: 'Back to Today Assistant', exact: true }).click();
  await expect.poll(() => scroll.scrollTop).toBe(parked);
  await expect.element(field).toHaveTextContent('Keep my draft');
  expect(field.element()).toBe(editor);
  await expect.element(page.getByRole('button', { name: 'Report changes', exact: true })).toHaveFocus();
  expect(document.querySelectorAll('[role="combobox"][aria-label="Message"]')).toHaveLength(1);
  await page.screenshot({ path: '../../../../test-results/v10-fixed-today-exchanges.png' });
  if (selected) await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.viewport(390, 844);
  if (selected) await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  await page.viewport(1440, 700);
  await expect.element(field).toBeVisible();
  await expect.poll(() => scroll.scrollTop).toBe(parked);
  await expect.element(field).toHaveTextContent('Keep my draft');
  if (selected) expect(field.element()).toBe(editor);
});

it.each([false, true])('focuses every explicit Today Reply without replacing the editor (from subview: %s)', async subview => {
  await page.viewport(1440, 800);
  renderDailyFixture({ reply: request => request.path === '/api/v1/tracks/daily' ? { status: 200, statusText: 'OK', body: {
    ...fixtureSnapshot(fixtureTrack('daily', { leadSessionId: 'daily-planner' }), [fixtureSession('daily-planner', 'daily', { title: 'Daily Planner conversation', isLead: true })]),
    openAsks: [{ id: '7', trackId: 'daily', status: 'open', delivery: 'wake', createdAtMs: 5, questions: [{ title: 'Choose the release branch', options: ['main'] }] }],
  } } : undefined });
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('An unfinished sentence');
  await userEvent.keyboard('{Home}{ArrowRight}{ArrowRight}');
  const caret = window.getSelection()?.anchorOffset;
  const editor = field.element();
  for (let index = 0; index < 2; index++) {
    if (subview) await page.getByRole('button', { name: 'Report changes', exact: true }).click();
    await page.getByRole('button', { name: 'Answer the Planner: Choose the release branch', exact: true }).click();
    await expect.element(field).toHaveFocus();
    expect(field.element()).toBe(editor);
    expect(window.getSelection()?.anchorOffset).toBe(caret);
    await expect.element(field).toHaveTextContent('An unfinished sentence');
  }
});


it('interrupts the working Today conversation from the transcript with Escape', async () => {
  await page.viewport(1440, 800);
  const { requests } = renderDailyFixture({ reply: request => {
    if (request.path.includes('/items?')) return { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), [sessionItem('daily-planner', 1, 'Working on the [Release evidence](mailto:release@example.test).')], sessionTurn('daily-planner')) };
    if (request.path.endsWith('/interrupt')) return { status: 200, statusText: 'OK', body: { card_id: 'daily-planner', worker_session_id: 'runtime', stopped: true } };
    return undefined;
  } });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  await expect.element(assistant.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  // The transcript, rather than its portalled footer, must own interruption too.
  const evidence = await assistant.getByRole('link', { name: 'Release evidence', exact: true }).findElement();
  evidence.focus();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => requests.filter(request => request.path.endsWith('/interrupt')).length).toBe(1);
  await expect.element(assistant).toBeVisible();
});

it('honors inventory and New conversation focus when leaving a Today subview', async () => {
  await page.viewport(1440, 800);
  renderDailyFixture({ extraPlanner: true });
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('Planner draft');
  const editor = field.element();
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(field).toHaveFocus();
  expect(field.element()).toBe(editor);
  await expect.element(field).toHaveTextContent('Planner draft');
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.element(field).toHaveFocus();
  await expect.element(field).toHaveTextContent('');
  await field.fill('New conversation draft');
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(field).toHaveFocus();
  await expect.element(field).toHaveTextContent('Planner draft');
});
