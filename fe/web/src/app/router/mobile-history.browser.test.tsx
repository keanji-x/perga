import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

afterEach(async () => { cleanup(); await page.viewport(1280, 800); });
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

it('separates conversation switcher from workspace navigation and hands a selection to the existing Planner', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
  await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  const switcher = page.getByRole('dialog', { name: 'Switch conversation', exact: true });
  await expect.element(switcher).toBeVisible();
  expect(page.getByRole('searchbox').query()).toBeNull();
  await expect.element(page.getByRole('button', { name: 'New conversation', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: /^Lead/ }).click();
  await expect.poll(() => switcher.query()).toBeNull();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('closes switcher before opening a new conversation and opens Settings from Workspace', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
  await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.element(page.getByRole('dialog', { name: 'Untitled', exact: true })).toBeVisible();
  await expect.poll(() => document.activeElement).toBe(page.getByRole('combobox', { name: 'Message', exact: true }).element());
  expect(document.querySelector('dialog[open][aria-label="Switch conversation"]')).toBeNull();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'Settings', exact: true })).toBeVisible();
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('reads only current Track conversations and excludes sibling and other workspace histories', async () => {
  await page.viewport(390, 844);
  const track = fixtureTrack('project', { areaId: 'project-area', title: 'Project evidence' });
  const sibling = { ...track, id: 'sibling', title: 'Sibling report' };
  const foreign = { ...track, id: 'foreign', areaId: 'other-area', title: 'Other report' };
  const { requests } = renderDailyFixture({ initial: '/track/project', reply: (request) => {
    if (request.path === '/api/v1/areas') return ok([
      { id: 'project-area', name: 'Project', kind: 'user', color: '#6574cd', sortOrder: 0, createdAtMs: 1, updatedAtMs: 1 },
      { id: 'other-area', name: 'Other', kind: 'user', color: '#6574cd', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 },
    ]);
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok([track, sibling]);
    if (request.path === '/api/v1/areas/other-area/tracks?includeClosed=true') return ok([foreign]);
    if (request.path === '/api/v1/tracks/sibling') return ok(fixtureSnapshot(sibling));
    if (request.path === '/api/v1/tracks/project') return ok(fixtureSnapshot(track, [fixtureSession('first-chat', 'project', { title: 'Older chat' })]));
    if (request.path === '/api/v1/tracks/sibling/sessions') return ok([
      fixtureSession('second-chat', 'sibling', { title: 'Newest chat' }),
    ]);
    return undefined;
  } });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
  await expect.poll(() => requests.some((request) => request.path === '/api/v1/areas/project-area/tracks?includeClosed=true')).toBe(true);
  await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  await expect.element(page.getByRole('button', { name: /^Older chat/ })).toBeVisible();
  expect(page.getByRole('button', { name: /^Newest chat/ }).query()).toBeNull();
  expect(requests.some((request) => request.path === '/api/v1/tracks/sibling/sessions')).toBe(false);
  expect(requests.some((request) => request.path === '/api/v1/tracks/foreign/sessions')).toBe(false);
  expect(requests.some((request) => request.path === '/api/v1/tracks/foreign')).toBe(false);
});

it('shows the v1 aggregate read failure and retries the current Track', async () => {
  await page.viewport(390, 844);
  let fail = true;
  renderDailyFixture({ initial: '/track/project', reply: request => request.path === '/api/v1/tracks/project' && fail
    ? { status: 500, statusText: 'Error', body: { error: 'History unavailable' } } : undefined });
  await expect.element(page.getByRole('alert')).toBeVisible();
  fail = false;
  await page.getByRole('button', { name: /Retry|重试/ }).click();
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toBeVisible();
});


it('keeps the closed report composer stable while its conversation is read', async () => {
  await page.viewport(390, 844);
  const errors: string[] = [];
  const original = console.error;
  const spy = vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
    if (args.some((arg) => typeof arg === 'string' && arg.includes('Maximum update depth'))) errors.push(new Error('recursive update').stack ?? 'recursive update');
    else original(...args);
  });
  try {
    let commits = 0;
    const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true, onCommit: () => { commits += 1; } });
    await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
    await expect.poll(() => requests.some((request) => request.path.includes('/items?afterId=0&limit=1'))).toBe(true);
    page.getByRole('textbox', { name: 'Chat message', exact: true }).element().focus();
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
    await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
    const settled = commits;
    await new Promise<void>((resolve) => setTimeout(resolve, 250));
    expect(commits - settled).toBeLessThan(12);
    expect(errors).toEqual([]);
  } finally { spy.mockRestore(); }
});


it.each([320, 390])('switches Focus views using real workspace groups at %ipx', async (width) => {
  await page.viewport(width, 844);
  const receipts = createReadReceiptsKernel({ receipts: [{ kind: 'track', targetId: 'unread', readThroughMs: 0, manualUnread: true }] });
  const current = fixtureTrack('project', { areaId: 'project-area', title: 'Project evidence' });
  const pinned = { ...current, id: 'pinned', areaId: 'other-area', title: 'Pinned report', pinnedAtMs: 20 };
  const unread = { ...current, id: 'unread', areaId: 'other-area', title: 'Unread report' };
  const failed = { ...current, id: 'failed', areaId: 'other-area', title: 'Failed report' };
  const working = { ...current, id: 'working', title: 'Working report' };
  const overlays = { tracks: [
    { trackId: current.id, areaId: current.areaId, isWorking: false, openAskCount: 1, hasBlockedSession: false, unreadMailCount: 0, updatedAtMs: 3 },
    { trackId: failed.id, areaId: failed.areaId, isWorking: false, openAskCount: 0, hasBlockedSession: true, unreadMailCount: 0, updatedAtMs: 3 },
    { trackId: working.id, areaId: working.areaId, isWorking: true, openAskCount: 0, hasBlockedSession: false, unreadMailCount: 0, updatedAtMs: 3 },
  ] };
  const { router, requests } = renderDailyFixture({ initial: '/', receipts, reply: (request) => {
    if (request.path === '/api/v1/areas') return ok([
      { id: 'project-area', name: 'Project', kind: 'user', color: '#6574cd', sortOrder: 0, createdAtMs: 1, updatedAtMs: 1 },
      { id: 'other-area', name: 'Other', kind: 'user', color: '#6574cd', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 },
    ]);
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok([current, working]);
    if (request.path === '/api/v1/areas/other-area/tracks?includeClosed=true') return ok([pinned, unread, failed]);
    if (request.path.startsWith('/api/v1/overlays?')) return ok(overlays);
    if (request.path === '/api/v1/tracks/pinned') return ok(fixtureSnapshot(pinned));
    return undefined;
  } });
  await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
  const filters = page.getByRole('navigation', { name: 'Focus filters', exact: true });
  await filters.getByRole('button', { name: /^Pinned/ }).click();
  await expect.element(page.getByRole('button', { name: /Pinned report/ })).toBeVisible();
  await filters.getByRole('button', { name: /^Unread/ }).click();
  await expect.element(page.getByRole('button', { name: /Unread report/ })).toBeVisible();
  expect(page.getByRole('button', { name: /Pinned report/ }).query()).toBeNull();
  await filters.getByRole('button', { name: /^Attention/ }).click();
  await expect.element(page.getByRole('button', { name: /Project evidence/ })).toBeVisible();
  await expect.element(page.getByRole('button', { name: /Failed report/ })).toBeVisible();
  expect(page.getByRole('button', { name: /Working report/ }).query()).toBeNull();
  expect(requests.some((request) => request.path === '/api/v1/tracks/unread/sessions')).toBe(false);
  await filters.getByRole('button', { name: /^Pinned/ }).click();
  await page.getByRole('button', { name: /Pinned report/ }).click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/pinned');
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('keeps draft text intact when closing Chat Sheet back to Track Report', async () => {
  await page.viewport(390, 844);
  const track = fixtureTrack('project', { areaId: 'project-area', title: 'Project evidence', pinnedAtMs: 20 });
  const { requests } = renderDailyFixture({ initial: '/track/project', reply: request => request.path === '/api/v1/areas/project-area/tracks?includeClosed=true' ? ok([track]) : undefined });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  await page.getByRole('combobox', { name: 'Message', exact: true }).fill('Keep this draft');
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toHaveValue('Keep this draft');
  expect(requests.filter(request => request.method !== 'GET')).toHaveLength(0);
});


it('keeps activity read failures distinct from an empty unhandled list and can retry', async () => {
  await page.viewport(390, 844);
  let failed = true;
  renderDailyFixture({ initial: '/', reply: request => request.path.startsWith('/api/v1/overlays?')
    ? failed ? { status: 500, statusText: 'Error', body: { error: 'Activity unavailable' } } : ok({ tracks: [] }) : undefined });
  await expect.element(page.getByRole('alert')).toBeVisible();
  expect(page.getByText('All caught up', { exact: true }).query()).toBeNull();
  failed = false;
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect.poll(() => page.getByRole('alert').query()).toBeNull();
});
