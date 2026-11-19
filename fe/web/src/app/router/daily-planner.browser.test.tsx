import { fixtureSession, fixtureTrack, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionPage, sessionItem, sessionTurn } from './conversation-session-data.ts';
import { withFontRoleProbe } from '../../../../tools/styles/font-role-fixture.ts';
import '../../styles/entry.css';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { transcript } from '../../../../core/events/query-keys.ts';
import { NewTrackForm } from '../../features/area/new-track/public.tsx';
import { renderDailyFixture, renderWorkingDailySideFixture } from './daily-planner-fixture.tsx';

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
  renderDailyFixture({ initial: '/?day=2026-10-04', reply: (request) => request.path.includes('/items?') && !request.path.includes('&view=')
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


it.each([['/?day=2026-10-04', 1000], ['/?day=2026-10-04', 768], ['/?day=2026-10-04', 600], ['/', 600]] as const)('keeps both conversation composers reachable with a side draft on %s at height %s', async (initial, height) => {
  await page.viewport(1440, height);
  const fixture = renderDailyFixture({ initial, reply: (request) => request.path === '/api/v1/bootstrap' ? { status: 200, statusText: 'OK', body: {
    areaCreateIdempotency: true, conversationCreateModel: true, conversationSide: true, kernelVersion: '0.1.0',
    apiVersion: '23', syncEventVersion: 26, mcpProtocolVersion: '2024-11-05', pluginMcpProtocolVersion: '2025-11-25',
    webCompatVersion: 44, minWebCompatVersion: 44, supervisorControlVersion: 1, buildSha: 'fixture',
    dbInstanceId: 'fixture-instance', databaseId: 'fixture-database',
  } } : undefined });
  if (initial !== '/') await page.getByText('Prioritize the release.', { exact: false }).findElement();
  if (initial !== '/') await page.getByRole('button', { name: '讨论这项工作', exact: true }).click();
  const main = initial === '/' ? page.getByRole('region', { name: 'Today Assistant', exact: true }) : page.getByRole('complementary', { name: 'Daily Planner conversation' });
  if (initial === '/') await expect.element(main.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  else await expect.element(main.getByText('聊聊你想做什么', { exact: true })).toBeVisible();
  await main.getByRole('combobox', { name: 'Message', exact: true }).fill('/side');
  await userEvent.keyboard('{Enter}');
  const side = page.getByRole('region', { name: 'Side conversation · Codex' });
  await expect.poll(() => main.getByRole('combobox', { name: 'Message', exact: true }).element().textContent).toBe('/side ');
  await expect.element(side).not.toBeInTheDocument();
  expect(fixture.requests.filter(request => ['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method))).toHaveLength(0);
  await userEvent.keyboard('{Enter}');
  await expect.element(side).toBeVisible();
  const stack = side.element().closest('[data-nc-drawer-stack]')!;
  const clip = stack.closest('main')!;
  for (const pane of [main, side]) {
    const field = pane.getByRole('combobox', { name: 'Message', exact: true });
    const send = pane.getByRole('button', { name: 'Send', exact: true });
    await expect.element(field).toBeVisible();
    await expect.poll(() => send.element().getBoundingClientRect().bottom <= clip.getBoundingClientRect().bottom).toBe(true);
    expect(field.element().getBoundingClientRect().top).toBeGreaterThanOrEqual(pane.element().getBoundingClientRect().top);
    expect(send.element().getBoundingClientRect().bottom).toBeLessThanOrEqual(Math.min(pane.element().getBoundingClientRect().bottom, height));
  }
  await side.getByRole('combobox', { name: 'Message', exact: true }).fill('A reachable side draft.');
  await expect.element(side.getByRole('combobox', { name: 'Message', exact: true })).toHaveTextContent('A reachable side draft.');
  await side.getByRole('button', { name: 'Close side conversation', exact: true }).click();
  await expect.element(main.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
});


it.each([768, 600])('keeps both inputs inside their cards when the parent footer grows at height %s', async (height) => {
  await page.viewport(1440, height);
  let wedged = false;
  const fixture = renderDailyFixture({ initial: '/?day=2026-10-04', reply: (request) => {
    if (request.path === '/api/v1/bootstrap') return { status: 200, statusText: 'OK', body: {
      areaCreateIdempotency: true, conversationCreateModel: true, conversationSide: true, kernelVersion: '0.1.0',
      apiVersion: '23', syncEventVersion: 26, mcpProtocolVersion: '2024-11-05', pluginMcpProtocolVersion: '2025-11-25',
      webCompatVersion: 44, minWebCompatVersion: 44, supervisorControlVersion: 1, buildSha: 'fixture',
      dbInstanceId: 'fixture-instance', databaseId: 'fixture-database',
    } };
    if (request.path.includes('&view=run')) return { status: 200, statusText: 'OK', body: historicalConversationRun({
      card_id: 'daily-planner', worker_session_id: 'runtime', phase: wedged ? 'wedged' : 'idle', model: null,
      reasoning_effort: null, permission_mode: 'never', blocked_reason: wedged ? 'The stop request timed out before the model confirmed that this turn had stopped.' : null,
      pending_queue: [], manual_compact_supported: true, recovery: wedged ? { cause: 'wedged', action: 'restart_session' } : null, compact_outcome: null, context_usage_invalidated: false, running_turn: null, final_reply: null,
    }, 'idle', true) };
    return undefined;
  } });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  await page.getByRole('button', { name: '讨论这项工作', exact: true }).click();
  const main = page.getByRole('complementary', { name: 'Daily Planner conversation' });
  await expect.element(main.getByText('聊聊你想做什么', { exact: true })).toBeVisible();
  await main.getByRole('combobox', { name: 'Message', exact: true }).fill('/side');
  await userEvent.keyboard('{Enter}');
  const side = page.getByRole('region', { name: 'Side conversation · Codex' });
  await expect.poll(() => main.getByRole('combobox', { name: 'Message', exact: true }).element().textContent).toBe('/side ');
  await expect.element(side).not.toBeInTheDocument();
  expect(fixture.requests.filter(request => ['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method))).toHaveLength(0);
  await userEvent.keyboard('{Enter}');
  await expect.element(side).toBeVisible();
  await main.getByRole('combobox', { name: 'Message', exact: true }).fill(Array.from({ length: 12 }, (_, index) => `Draft ${index + 1}`).join('\n'));
  await side.getByRole('combobox', { name: 'Message', exact: true }).fill(Array.from({ length: 12 }, (_, index) => `Side draft ${index + 1}`).join('\n'));
  wedged = true;
  await fixture.client.invalidateQueries({ queryKey: ['planner-run'] });
  await expect.element(main.getByText(/^This conversation’s session is stuck\./)).toBeVisible();
  for (const pane of [main, side]) {
    const field = pane.getByRole('combobox', { name: 'Message', exact: true });
    const send = pane.getByRole('button', { name: 'Send', exact: true });
    await expect.element(field).toBeVisible();
    await expect.poll(() => send.element().getBoundingClientRect().bottom <= pane.element().getBoundingClientRect().bottom).toBe(true);
    expect(field.element().getBoundingClientRect().top).toBeGreaterThanOrEqual(pane.element().getBoundingClientRect().top);
    expect(send.element().getBoundingClientRect().bottom).toBeLessThanOrEqual(height);
  }
  await page.getByRole('button', { name: 'Close side conversation' }).click();
  await page.getByRole('button', { name: 'Close conversation' }).click();
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
  renderDailyFixture({ reply: request => request.path.includes('/items?') && !request.path.includes('&view=')
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
  renderDailyFixture({ reply: request => request.path.includes('/items?') && !request.path.includes('&view=') ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), items) } : undefined });
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


it.each([['Report changes', 'transcript'], ['Report changes', 'composer'], ['Daily Supervisor', 'transcript'], ['Daily Supervisor', 'composer']])('keeps an adopted working Today side interactive in %s from %s', async (view, input) => {
  await page.viewport(1440, 900);
  const { requests, rows } = renderWorkingDailySideFixture();
  const main = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const mainField = main.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(main.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  await expect.element(main.getByRole('link', { name: 'Side evidence', exact: true })).toBeVisible();
  await mainField.fill('/side Explore independently');
  await userEvent.keyboard('{Enter}');
  const side = page.getByRole('region', { name: 'Side conversation · Codex', exact: true });
  await expect.element(side.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  const sideId = rows[0].id;
  expect(requests.find(request => request.method === 'POST' && request.path.endsWith('/sessions'))?.body)
    .toMatchObject({ text: 'Explore independently', side: { source_card_id: 'daily-planner' } });
  const sideField = side.getByRole('combobox', { name: 'Message', exact: true });
  await sideField.fill('Side draft survives');
  await mainField.fill('Main draft survives');
  const mainElement = main.element();
  await page.getByRole('button', { name: view, exact: true }).click();
  await expect.element(mainElement).not.toBeVisible();
  await expect.element(side).toBeVisible();
  const evidence = await side.getByRole('link', { name: 'Side evidence', exact: true }).findElement();
  if (input === 'transcript') evidence.focus();
  else await sideField.click();
  await expect.element(input === 'transcript' ? evidence : sideField).toHaveFocus();
  if (input === 'transcript') await page.screenshot({ path: `../../../../test-results/today-side-${view === 'Report changes' ? 'changes' : 'supervisor'}.png` });
  await userEvent.keyboard('{Escape}');
  // One side request, no main request: the two working controllers must stay isolated.
  await expect.poll(() => requests.filter(request => request.path.endsWith('/interrupt')).map(request => request.path))
    .toEqual([`/api/v1/sessions/${sideId}/interrupt`]);
  await expect.element(side).toBeVisible();
  await expect.element(side.getByRole('button', { name: 'Stop', exact: true })).not.toBeInTheDocument();
  await page.getByRole('button', { name: 'Back to Today Assistant', exact: true }).click();
  await expect.element(mainField).toHaveTextContent('Main draft survives');
  await expect.element(sideField).toHaveTextContent('Side draft survives');
  await side.getByRole('button', { name: 'Close side conversation', exact: true }).click();
});


it.each(['terminal', 'compact'])('isolates Today side visible effects while hidden by %s and restores its reading and drafts', async hiddenBy => {
  await page.viewport(1440, 900);
  const fixture = renderWorkingDailySideFixture({ paginated: true });
  const main = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const mainField = main.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(main.getByText('Paragraph 34.', { exact: false })).toBeVisible();
  // Explicit selection preserves the parent identity while compact owns the main Sheet.
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await mainField.fill('/side Explore independently');
  await userEvent.keyboard('{Enter}');
  const side = page.getByRole('region', { name: 'Side conversation · Codex', exact: true });
  await expect.element(side.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  const sideId = fixture.rows[0].id;
  const sideField = side.getByRole('combobox', { name: 'Message', exact: true });
  await mainField.fill('Main draft survives');
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();

  const receiptWrites = () => fixture.receipts.requests.filter(request => request.method === 'POST' && request.path.includes('/conversation/'));
  const receiptStart = receiptWrites().length;
  act(() => { document.dispatchEvent(new Event('visibilitychange')); });
  await expect.poll(() => receiptWrites().slice(receiptStart).map(request => request.path))
    .toEqual([`/api/read-receipts/conversation/${sideId}/read`]);
  // Editing the real queue asks this side controller to focus its existing portalled editor.
  await sideField.fill('');
  await side.getByRole('button', { name: 'Edit queued message', exact: true }).click();
  await expect.element(sideField).toHaveFocus();
  await expect.element(sideField).toHaveTextContent('Explore independently');
  await side.getByRole('button', { name: 'Cancel edit', exact: true }).click();
  await sideField.fill('Side draft survives');
  const editor = sideField.element();
  const evidence = side.getByRole('link', { name: 'Side evidence', exact: true }).element();
  evidence.focus();
  await userEvent.keyboard('{Home}');
  const earlierReads = () => fixture.requests.filter(request => request.path.includes('/items?') && !request.path.includes('&view=') && request.path.includes('&cursor='));
  await expect.poll(() => earlierReads().map(request => conversationCardId(request.path))).toEqual([sideId]);

  // A fresh server window gives the hidden branch an unspent cursor to reject.
  fixture.advanceHistory();
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: transcript(sideId) }); });
  const sideElement = side.element();
  const scroller = sideElement.querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  fireEvent.wheel(scroller, { deltaY: 40 });
  scroller.scrollTop = 350;
  fireEvent.scroll(scroller);
  await expect.poll(() => scroller.scrollTop).toBe(350);
  const place = scroller.scrollTop;
  const beforeHidden = receiptWrites().length;
  const readsBeforeHidden = earlierReads().length;
  if (hiddenBy === 'terminal') {
    await page.getByRole('button', { name: 'Terminal', exact: true }).click();
    await expect.element(page.getByRole('button', { name: 'Hide terminal', exact: true })).toBeVisible();
    await expect.element(sideElement).not.toBeVisible();
    expect(sideElement.closest('[inert]')).not.toBeNull();
  } else {
    await page.viewport(390, 844);
    const companion = document.querySelector<HTMLElement>('[aria-label="Track workbench"]')!;
    expect(companion.hidden).toBe(true);
    expect(companion.inert).toBe(true);
  }
  act(() => {
    document.dispatchEvent(new Event('visibilitychange'));
    fireEvent.keyDown(editor, { key: 'Escape' });
    fireEvent.wheel(scroller, { deltaY: -40 });
  });
  await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  expect(receiptWrites().slice(beforeHidden).filter(request => request.path.includes(sideId))).toEqual([]);
  expect(earlierReads()).toHaveLength(readsBeforeHidden);
  expect(fixture.requests.filter(request => request.path.endsWith('/interrupt'))).toEqual([]);

  if (hiddenBy === 'terminal') await page.getByRole('button', { name: 'Discussion', exact: true }).click();
  else await page.viewport(1440, 900);
  await expect.element(side).toBeVisible();
  await expect.element(sideField).toHaveTextContent('Side draft survives');
  await expect.poll(() => side.element().querySelector<HTMLElement>('[data-nc-drawer-scroll]')!.scrollTop).toBe(place);
  const resumedReceipts = receiptWrites().length;
  act(() => { document.dispatchEvent(new Event('visibilitychange')); });
  await expect.poll(() => receiptWrites().slice(resumedReceipts).some(request => request.path.includes(sideId))).toBe(true);
  // Request older history from the first loaded link, at the top of the reader.
  side.getByRole('link', { name: 'Side evidence', exact: true }).first().element().focus();
  await userEvent.keyboard('{Home}');
  await expect.poll(() => earlierReads().length).toBe(readsBeforeHidden + 1);
  expect(conversationCardId(earlierReads().at(-1)!.path)).toBe(sideId);
  await sideField.click();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => fixture.requests.filter(request => request.path.endsWith('/interrupt')).map(request => request.path))
    .toEqual([`/api/v1/sessions/${sideId}/interrupt`]);
  await page.getByRole('button', { name: 'Back to Today Assistant', exact: true }).click();
  await expect.element(mainField).toHaveTextContent('Main draft survives');
  await side.getByRole('button', { name: 'Close side conversation', exact: true }).click();
  await page.screenshot({ path: `../../../../test-results/today-side-${hiddenBy}-restored.png` });
});

it('keeps perga Today minimal without triage or Report changes', async () => {
  await page.viewport(1440, 900);
  const { requests } = renderDailyFixture({ pergaMode: true, identity: { userId: 'local-owner', displayName: 'Owner', role: 'owner', sessionId: 'perga-owner' } });
  await expect.element(page.getByRole('region', { name: 'Today Assistant' })).toBeVisible();
  expect(page.getByRole('region', { name: 'Today triage' }).query()).toBeNull();
  expect(page.getByText(/Waiting on you/).query()).toBeNull();
  expect(page.getByText('源文件夹模式 · 未隔离', { exact: true }).query()).toBeNull();
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(page.getByText('Daily Supervisor', { exact: true }).query()).toBeNull();
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
});

it('keeps perga daily Report usable on mobile without triage or Supervisor', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ pergaMode: true, initial: '/?day=2026-10-04' });
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  await expect.element(page.getByRole('region', { name: 'Calendar', exact: true })).toBeVisible();
  expect(page.getByText('前一天的更新', { exact: true }).query()).toBeNull();
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(page.getByRole('region', { name: 'Today triage' }).query()).toBeNull();
  expect(page.getByText('源文件夹模式 · 未隔离', { exact: true }).query()).toBeNull();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
});


it.each([900, 600])('optically centers the empty perga Today composer and pins it below loaded turns at height %s', async height => {
  await page.viewport(1440, height);
  // Compare against the real New Track surface under the same production tokens.
  const referenceProps = { submitting: false, error: null, initialCwd: null,
    listDirectory: vi.fn(() => Promise.resolve({ path: '/', parent: null, entries: [] })), onSubmit: vi.fn() };
  const reference = render(<NewTrackForm {...referenceProps} />);
  const referenceField = page.getByRole('textbox', { name: 'What this track should do', exact: true }).element() as HTMLElement;
  const referenceBody = referenceField.closest('[data-density] > *')!;
  referenceField.blur();
  const referenceStyle = getComputedStyle(referenceField);
  const referenceMin = referenceStyle.minBlockSize;
  const referenceBorder = getComputedStyle(referenceBody).borderWidth;
  const referencePadding = getComputedStyle(referenceBody).padding;
  const referenceShadow = getComputedStyle(referenceBody).boxShadow;
  const referenceWidth = reference.container.querySelector('[data-nc-new-track-message]')!.getBoundingClientRect().width;
  const referenceSendRadius = getComputedStyle(page.getByRole('button', { name: 'Create track', exact: true }).element()).borderRadius;
  referenceField.focus();
  await expect.poll(() => getComputedStyle(referenceBody).boxShadow).toMatch(/\) 0px 0px 0px 2px inset$/);
  const referenceFocus = getComputedStyle(referenceBody).boxShadow;
  reference.rerender(<NewTrackForm {...referenceProps} submitting />);
  expect(referenceField.getAttribute('contenteditable')).toBe('false');
  await expect.poll(() => getComputedStyle(referenceBody).boxShadow).toBe('none');
  reference.unmount();
  let populated = false;
  const fixture = renderDailyFixture({ pergaMode: true, reply: request => request.path.includes('/items?') && !request.path.includes('&view=')
    ? { status: 200, statusText: 'OK', body: presentationPage(populated ? [humanPresentation('daily-planner', 1, 'first-message', { user_id: 'reader', display_name: 'Reader' }, 'Plan the release', 1)] : []) }
    : undefined });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeVisible();
  await expect.poll(() => assistant.element().hasAttribute('data-nc-centered-footer')).toBe(true);
  expect(assistant.getByText('聊聊你想做什么', { exact: true }).query()).toBeNull();
  expect(assistant.getByText('从一个问题或想法开始。', { exact: true }).query()).toBeNull();
  const host = assistant.element();
  const footer = host.children[2]; // EmbeddedReadingHost's masthead and composer.
  const box = host.getBoundingClientRect();
  const footerBox = footer.getBoundingClientRect();
  expect(host.querySelector('[data-nc-thread-empty]')).toBeNull();
  const hostStyle = getComputedStyle(host);
  const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
  const space6 = parseFloat(hostStyle.getPropertyValue('--space-6'));
  expect(parseFloat(hostStyle.paddingBottom)).toBeCloseTo(Math.min(Math.max(space6, height * 0.14), 9 * rem));
  const opticalOffset = (parseFloat(hostStyle.paddingBottom) - parseFloat(hostStyle.paddingTop)) / 2;
  expect(opticalOffset).toBeGreaterThan(20);
  expect(Math.abs((footerBox.top + footerBox.bottom) / 2 - ((box.top + box.bottom) / 2 - opticalOffset))).toBeLessThan(2);
  const editable = field.element() as HTMLElement;
  const body = editable.closest('[data-density] > *')!;
  editable.blur();
  expect(getComputedStyle(body).borderWidth).toBe(referenceBorder);
  expect(getComputedStyle(body).padding).toBe(referencePadding);
  expect(getComputedStyle(body).boxShadow).toBe(referenceShadow);
  expect(getComputedStyle(body).boxShadow).toBe('none');
  expect(getComputedStyle(editable).minBlockSize).toBe(referenceMin);
  expect(parseFloat(getComputedStyle(editable).minBlockSize)).toBeGreaterThanOrEqual(88);
  expect(parseFloat(getComputedStyle(editable).minBlockSize)).toBeCloseTo(4 * parseFloat(getComputedStyle(editable).lineHeight));
  expect(footerBox.width).toBeCloseTo(referenceWidth);
  expect(footerBox.width).toBeCloseTo(parseFloat(getComputedStyle(host).getPropertyValue('--measure-doc')));
  expect(getComputedStyle(host).alignItems).toBe('safe center');
  expect(getComputedStyle(host).justifyContent).toBe('safe center');
  expect(getComputedStyle(assistant.getByRole('button', { name: 'Send', exact: true }).element()).borderRadius).toBe(referenceSendRadius);
  editable.focus();
  await expect.poll(() => getComputedStyle(body).boxShadow).toBe(referenceFocus);
  expect(referenceFocus).toContain('inset');
  expect(referenceFocus).toContain('2px');
  expect(getComputedStyle(body).outlineStyle).toBe('none');
  expect(Math.abs((footerBox.left + footerBox.right) / 2 - (box.left + box.right) / 2)).toBeLessThan(2);
  expect(assistant.getByText('Report changes').query()).toBeNull();
  await expect.element(assistant.getByRole('heading', { name: '今天想推进什么？' })).toBeVisible();
  editable.blur();
  await expect.poll(() => getComputedStyle(body).boxShadow).toBe('none');
  await page.screenshot({ path: `../../../../test-results/today-centered-composer-${height}.png` });
  await field.fill('Keep this draft');
  const editor = field.element();
  populated = true;
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: transcript('daily-planner') }); });
  await expect.element(assistant.getByText('Plan the release', { exact: true })).toBeVisible();
  expect(host.hasAttribute('data-nc-centered-footer')).toBe(false);
  expect(field.element()).toBe(editor);
  await expect.element(field).toHaveTextContent('Keep this draft');
  expect(assistant.getByText('Report changes').query()).toBeNull();
  expect(assistant.getByRole('heading', { name: '今天想推进什么？' }).query()).toBeNull();
  const send = assistant.getByRole('button', { name: 'Send', exact: true }).element().getBoundingClientRect();
  expect(send.bottom).toBeLessThanOrEqual(box.bottom);
  expect(send.top).toBeGreaterThan(box.top + box.height / 2);
});

it.each([900, 600])('shows the shared masthead above a clean perga composer and keeps the right card usable at height %s', async height => {
  await page.viewport(1440, height);
  renderDailyFixture({ pergaMode: true });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const heading = assistant.getByRole('heading', { name: '今天想推进什么？' });
  await expect.element(heading).toBeVisible();
  const logo = heading.element().querySelector<SVGSVGElement>('[data-nc-motion="creation"]')!;
  await expect.element(logo).toBeVisible();
  expect(assistant.getByText('说说今天的重点，计划与进展会整理在右侧日报里。').query()).toBeNull();
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeVisible();
  const body = field.element().closest('[data-density] > *')!;
  expect(heading.element().getBoundingClientRect().bottom).toBeLessThan(body.getBoundingClientRect().top);
  expect(heading.element().getBoundingClientRect().left).toBeCloseTo(body.getBoundingClientRect().left);
  expect(getComputedStyle(body).boxShadow).toBe('none');
  expect(parseFloat(getComputedStyle(field.element()).minBlockSize)).toBeCloseTo(4 * parseFloat(getComputedStyle(field.element()).lineHeight));
  expect(assistant.getByText('Report changes').query()).toBeNull();
  expect(assistant.getByText('聊聊你想做什么').query()).toBeNull();
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar).toBeVisible();
  const card = calendar.element().closest('[data-nc-desktop-panel]')!;
  expect(calendar.element().getBoundingClientRect().left).toBeGreaterThan(assistant.element().getBoundingClientRect().right);
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(card.textContent).not.toMatch(/Active tracks/i);
  expect(card.textContent).not.toContain('Project evidence');
  await expect.element(page.getByRole('heading', { name: 'Daily report', exact: true })).toBeVisible();
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  await page.screenshot({ path: `../../../../test-results/today-v4-${height}.png` });
});

it.each([1440, 390])('keeps perga calendar selection in place without a daily track at width %s', async width => {
  await page.viewport(width, 844);
  const { router } = renderDailyFixture({ pergaMode: true, initial: '/?day=2026-10-03' });
  await expect.element(page.getByText('No daily Track was created for this date.')).toBeVisible();
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar).toBeVisible();
  await page.getByRole('radio', { name: 'Month', exact: true }).click();
  await expect.element(page.getByRole('radio', { name: 'Month', exact: true })).toBeChecked();
  await calendar.getByRole('button', { name: 'Next month' }).click();
  const days = calendar.element().querySelectorAll<HTMLButtonElement>('button[aria-pressed]');
  const selected = days[4].getAttribute('aria-label')!;
  await calendar.getByRole('button', { name: selected, exact: true }).click();
  await expect.element(calendar.getByRole('button', { name: selected, exact: true })).toHaveAttribute('aria-pressed', 'true');
  expect(router.state.location.search).toEqual({ day: '2026-10-03' });
  await calendar.getByRole('button', { name: 'Today', exact: true }).click();
  expect(calendar.element().querySelector('[data-nc-today]')?.getAttribute('aria-pressed')).toBe('true');
  await page.getByRole('radio', { name: 'Week', exact: true }).click();
  await expect.element(page.getByRole('radio', { name: 'Week', exact: true })).toBeChecked();
  expect(calendar.element().querySelectorAll('button[aria-pressed]')).toHaveLength(7);
  expect(router.state.location.search).toEqual({ day: '2026-10-03' });
  await expect.element(page.getByText('No daily Track was created for this date.')).toBeVisible();
});

it.each([false, true])('shows perga Conversations only with multiple rows (extra=%s)', async extraPlanner => {
  await page.viewport(1440, 900);
  renderDailyFixture({ pergaMode: true, extraPlanner });
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  const conversations = page.getByRole('region', { name: 'Conversations', exact: true });
  if (extraPlanner) {
    await expect.element(conversations).toBeVisible();
    await expect.element(conversations.getByRole('button', { name: 'Conversation Other conversation', exact: true })).toBeVisible();
    await expect.element(conversations.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true })).toBeVisible();
  } else {
    expect(conversations.query()).toBeNull();
    expect(page.getByRole('button', { name: 'New conversation', exact: true }).query()).toBeNull();
  }
});

it.each(['', '   \n  ', '# 今日计划\n\nA populated daily report.'])('shows perga Daily report only when populated (%s)', async reportBody => {
  await page.viewport(1440, 900);
  renderDailyFixture({ pergaMode: true, reportBody });
  await expect.element(page.getByRole('heading', { name: '今天想推进什么？', exact: true })).toBeVisible();
  const report = page.getByRole('heading', { name: 'Daily report', exact: true });
  if (reportBody.includes('A populated')) {
    await expect.element(page.getByText('A populated daily report.')).toBeVisible();
    await expect.element(report).toBeVisible();
  } else {
    await expect.poll(() => document.querySelector('[data-nc-centered-footer]') !== null).toBe(true);
    await expect.poll(() => page.getByRole('region', { name: 'Track report' }).query()?.getAttribute('aria-busy')).not.toBe('true');
    expect(report.query()).toBeNull();
    expect(page.getByText('进展会整理在这里', { exact: true }).query()).toBeNull();
    expect(page.getByRole('region', { name: 'Track report' }).query()).toBeNull();
    expect(page.getByRole('region', { name: '空白日报' }).query()).toBeNull();
  }
});
