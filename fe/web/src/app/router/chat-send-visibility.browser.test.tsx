import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionPage } from './conversation-session-data.ts';
import { fixtureSession } from './session-contract-fixture.ts';
import '../../styles/entry.css';
import { act, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import type { SessionItemDto } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { queryKeys } from '../providers/query-keys.ts';


const history = Object.freeze(Array.from({ length: 16 }, (_, index) => sessionItem('daily-planner', index + 1,
  index % 2 === 0 ? `Question ${index / 2}` : 'A reply with enough lines to make the conversation scroll. '.repeat(12),
  index % 2 === 0 ? 'user' : 'assistant_text', `turn-${Math.floor(index / 2)}`)));
function conversationPage(rows: readonly SessionItemDto[]) {
  return sessionPage(fixtureSession('daily-planner', 'daily'), [...rows]);
}
const pane = () => document.querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
const remaining = () => pane().scrollHeight - pane().scrollTop - pane().clientHeight;
async function frames() {
  for (let index = 0; index < 4; index++) await act(async () => {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  });
}
afterEach(async () => { cleanup(); document.getElementById('root')?.remove(); await commands.emulateReducedMotion(false); });

it.each([
  ['/track/daily', 'earlier history', false], ['/track/daily', 'last exchange', false], ['/track/daily', 'last exchange', true],
  ['/', 'earlier history', false], ['/', 'last exchange', false], ['/', 'last exchange', true],
] as const)('reveals a fresh local send on %s after reading %s (reduced motion: %s)', async (initial, reading, reducedMotion) => {
  await page.viewport(1400, 900);
  await commands.emulateReducedMotion(reducedMotion);
  let finish: (response: ApiTransportResponse) => void = () => {};
  const held = new Promise<ApiTransportResponse>((resolve) => { finish = resolve; });
  let rows = history;
  const { client, requests } = renderSessionFixture({ initial, identity: { userId: 'local-owner', displayName: 'Visibility User', role: 'owner', sessionId: 'visibility-session' }, reply: (request) => {
    const url = new URL(request.path, 'http://fixture');
    if (url.pathname.endsWith('/items') && !url.searchParams.has('view')) return { status: 200, statusText: 'OK', body: conversationPage(rows) };
    if (request.path.endsWith('/messages')) return held;
    return undefined;
  } });
  if (initial !== '/') await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.poll(() => document.querySelectorAll('[data-nc-turn="you"]').length).toBe(8);
  await frames();
  expect(remaining()).toBeLessThanOrEqual(1);
  if (reading === 'earlier history') {
    const before = pane().scrollTop;
    await commands.wheelScroll('[data-nc-drawer-scroll]', -300);
    await expect.poll(() => pane().scrollTop).toBe(before - 300);
  } else {
    await page.getByRole('button', { name: 'Jump to exchange 8: Question 7', exact: true }).click();
    await frames();
    expect(remaining()).toBeLessThanOrEqual(1);
  }
  await page.getByRole('combobox', { name: 'Message', exact: true }).click();
  await userEvent.keyboard('My fresh local message.');
  try {
    await page.getByRole('button', { name: 'Send', exact: true }).click();
    await expect.poll(() => requests.filter((request) => request.path.endsWith('/messages')).length).toBe(1);
    await expect.poll(() => document.querySelectorAll('[data-nc-turn="you"]').length).toBe(9);
    await frames();
    const message = page.getByText('My fresh local message.', { exact: true }).element().getBoundingClientRect();
    const viewport = pane().getBoundingClientRect();
    expect(message.top).toBeGreaterThanOrEqual(viewport.top);
    expect(message.bottom).toBeLessThanOrEqual(viewport.bottom);

    const before = pane().scrollTop;
    await commands.wheelScroll('[data-nc-drawer-scroll]', -120);
    await expect.poll(() => pane().scrollTop).toBe(before - 120);
    const parked = pane().scrollTop;
    rows = history.map((row, index) => index !== history.length - 1 ? row : {
      ...row, payload: { text: 'Background reply growth. '.repeat(100) },
    });
    await act(async () => { await client.refetchQueries({ queryKey: queryKeys.harnessItems('daily-planner') }); });
    await frames();
    expect(pane().scrollTop).toBe(parked);
  } finally {
    finish({ status: 409, statusText: 'Conflict', body: { code: 'planner_harness_runtime_superseded', error: 'Send refused for the visibility test.' } });
    await page.getByRole('button', { name: '放回输入框', exact: true }).click();
    await expect.poll(() => document.querySelectorAll('[data-nc-turn="you"]').length).toBe(8);
    const field = page.getByRole('combobox', { name: 'Message', exact: true }).element();
    await expect.poll(() => field.textContent).toBe('My fresh local message.');
    await expect.poll(() => document.activeElement).toBe(field);
  }
});

it('keeps the reading place when an existing send is retried under its same key', async () => {
  await page.viewport(1400, 900);
  let finish: (response: ApiTransportResponse) => void = () => {};
  const held = new Promise<ApiTransportResponse>((resolve) => { finish = resolve; });
  let attempts = 0;
  const rejected: ApiTransportResponse = { status: 429, statusText: 'Too Many Requests', body: { code: 'rate_limited', error: 'Wait before retrying.' } };
  const { requests } = renderSessionFixture({ initial: '/track/daily', identity: { userId: 'local-owner', displayName: 'Visibility User', role: 'owner', sessionId: 'visibility-session' }, reply: (request) => {
    const url = new URL(request.path, 'http://fixture');
    if (url.pathname.endsWith('/items') && !url.searchParams.has('view')) return { status: 200, statusText: 'OK', body: conversationPage(history) };
    if (request.path.endsWith('/messages')) return ++attempts === 1 ? rejected : held;
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.poll(() => document.querySelectorAll('[data-nc-turn="you"]').length).toBe(8);
  await page.getByRole('combobox', { name: 'Message', exact: true }).click();
  await userEvent.keyboard('Retry these same words.');
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect.element(page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true })).toBeVisible();
  await frames();
  const before = pane().scrollTop;
  await commands.wheelScroll('[data-nc-drawer-scroll]', -300);
  await expect.poll(() => pane().scrollTop).toBe(before - 300);
  const parked = pane().scrollTop;
  try {
    await page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true }).click();
    await expect.poll(() => attempts).toBe(2);
    await frames();
    const posts = requests.filter((request) => request.path.endsWith('/messages'));
    const key = posts[0].headers?.['Idempotency-Key'];
    expect(key).toBeTruthy();
    expect(posts[1].headers?.['Idempotency-Key']).toBe(key);
    expect(pane().scrollTop).toBe(parked);
  } finally {
    finish(rejected);
    await expect.element(page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true })).toBeVisible();
  }
});
