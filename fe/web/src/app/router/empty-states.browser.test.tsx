import '../../styles/entry.css';
import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(cleanup);
async function settle() {
  await expect.poll(() => document.getAnimations().filter(animation => animation.effect?.getTiming().iterations !== Infinity).every(animation => animation.playState !== 'running')).toBe(true);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
}

it.each([1440, 390, 320])('keeps the empty daily report and conversation reachable at %i', async (width) => {
  await page.viewport(width, 900);
  renderDailyFixture({ reportBody: '' });
  await expect.element(page.getByRole('heading', { name: width >= 960 ? '今天想推进什么？' : '今天的重点' })).toBeVisible();
  await settle();
  await page.screenshot({ path: `./__screenshots__/empty-audit/daily-${width}.png` });
  if (width >= 960) {
    const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
    await expect.element(assistant.getByRole('heading', { name: '今天想推进什么？' })).toBeVisible();
    await expect.element(assistant.getByRole('combobox', { name: 'Message' })).toBeVisible();
    expect(assistant.element().querySelector('[data-nc-thread-empty]')).toBeNull();
    return;
  }
  else {
    await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
    await page.getByRole('button', { name: '2026-10-04 Daily Planner conversation', exact: true }).click();
  }
  await expect.poll(() => document.querySelector('[data-nc-thread-empty]') !== null).toBe(true);
  await expect.element(page.getByText('聊聊你想做什么', { exact: true })).toBeVisible();
  const title = page.getByRole('heading', { name: '聊聊你想做什么', exact: true }).element();
  await settle();
  const transcript = document.querySelector('[data-nc-thread-empty]')!;
  const scroll = transcript.closest('[data-nc-drawer-scroll]')!;
  const scrollStyle = getComputedStyle(scroll);
  const host = transcript.parentElement!.parentElement!;
  const bottomInset = host === scroll ? 0 : parseFloat(getComputedStyle(host).paddingBottom);
  // The role control precedes the transcript inside this scroll host.
  const available = scroll.getBoundingClientRect().bottom - transcript.getBoundingClientRect().top
    - parseFloat(scrollStyle.paddingBottom) - bottomInset;
  expect(transcript.getBoundingClientRect().height).toBeGreaterThanOrEqual(available - 1);
  expect(getComputedStyle(title).fontSize).toBe('16px');
  const visual = title.previousElementSibling!;
  expect(visual.getBoundingClientRect().width).toBeCloseTo(16, 2);
  expect(visual.getBoundingClientRect().right).toBeLessThan(title.getBoundingClientRect().left);
  expect(Math.abs((visual.getBoundingClientRect().top + visual.getBoundingClientRect().bottom) / 2 - (title.getBoundingClientRect().top + title.getBoundingClientRect().bottom) / 2)).toBeLessThan(1);
  expect(visual.getAttribute('aria-hidden')).toBe('true');
  await settle();
  await page.screenshot({ path: `./__screenshots__/empty-audit/chat-${width}.png` });
});
it.each([1440, 390])('uses the fixed report schema without actions at %i', async (width) => {
  await page.viewport(width, 900);
  const { requests } = renderDailyFixture({ initial: '/track/project', reply: request => request.path === '/api/v1/tracks/project' ? { status: 200, statusText: 'OK', body: fixtureSnapshot(fixtureTrack('project', { areaId: 'project-area', title: '新项目' })) } : undefined });
  await expect.poll(() => document.querySelector('[data-nc-report-empty]') !== null).toBe(true);
  const title = page.getByRole('heading', { name: '进展会整理在这里' }).element();
  expect(getComputedStyle(title).fontSize).toBe('20px');
  expect(getComputedStyle(title).fontWeight).toBe('500');
  const copy = title.parentElement!;
  expect(copy.getBoundingClientRect().width).toBeLessThanOrEqual(352);
  const visual = title.previousElementSibling!;
  expect(visual.getBoundingClientRect().width).toBeCloseTo(16, 2);
  expect(visual.getBoundingClientRect().right).toBeLessThan(title.getBoundingClientRect().left);
  expect(Math.abs((visual.getBoundingClientRect().top + visual.getBoundingClientRect().bottom) / 2 - (title.getBoundingClientRect().top + title.getBoundingClientRect().bottom) / 2)).toBeLessThan(1);
  expect(copy.nextElementSibling!.getBoundingClientRect().top).toBeGreaterThan(copy.getBoundingClientRect().bottom);
  expect(document.querySelector('[data-nc-report-empty]')!.querySelector('button, a, input, textarea')).toBeNull();
  await settle();
  await page.screenshot({ path: `./__screenshots__/empty-audit/track-${width}.png` });
  expect(requests.every(request => request.method === 'GET')).toBe(true);
});
