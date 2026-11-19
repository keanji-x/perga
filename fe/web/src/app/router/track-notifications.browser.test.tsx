import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { createUiPreferences } from '../providers/ui-preferences.tsx';
import { sessionPage } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

it.each([1200, 390])('shows and remembers the read state of each notification at %ipx', async (width) => {
  await page.viewport(width, 844);
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
  const track = fixtureTrack('project', { areaId: 'project-area', leadSessionId: 'project-planner' });
  const planner = fixtureSession('project-planner', track.id, { isLead: true });
  const snapshot = {
    ...fixtureSnapshot(track, [planner]),
    openAsks: [{
      id: 'ask-1', trackId: track.id, status: 'open', delivery: 'wake', createdAtMs: 1,
      questions: [{ question: 'Review the report?', options: ['Yes', 'No'] }],
    }],
    pendingApprovals: [{
      id: 'approval-1', executionId: 'execution-1', sessionId: planner.id, createdAtMs: 2,
      questions: [{ question: 'Publish the update?', options: ['Yes', 'No'] }],
    }],
  };
  const mount = () => renderDailyFixture({ pergaMode: true, initial: '/track/project', uiPreferences: createUiPreferences(storage),
    reply: request => {
      if (request.path === '/api/v1/tracks/project') return { status: 200, statusText: 'OK', body: snapshot };
      if (request.path.startsWith('/api/v1/sessions/project-planner/items?')) return { status: 200, statusText: 'OK', body: sessionPage(planner) };
      return undefined;
    } });
  const openMobile = async () => {
    if (width === 390) await page.getByRole('button', { name: 'Open 2 notifications' }).click();
  };
  const unread = page.getByRole('button', { name: 'Unread, mark as read: Review the report?' });
  const read = page.getByRole('button', { name: 'Read, mark as unread: Review the report?' });
  const second = page.getByRole('button', { name: 'Unread, mark as read: Publish the update?' });

  mount();
  await openMobile();
  await expect.element(unread).toBeVisible();
  if (width === 390) {
    const header = document.querySelector('[data-nc-workspace-header] header');
    const notice = await page.getByRole('region', { name: 'Notifications', exact: true }).findElement();
    expect(header).not.toBeNull();
    expect(notice.getBoundingClientRect().top).toBeGreaterThanOrEqual(header!.getBoundingClientRect().bottom);
  }
  await unread.click();
  await expect.element(read).toBeVisible();
  await expect.element(second).toBeVisible();
  await page.getByRole('button', { name: 'Collapse notifications' }).click();
  const launcher = page.getByRole('button', { name: 'Open 2 notifications' });
  await expect.element(launcher).toHaveAccessibleDescription('1 unread');

  cleanup();
  mount();
  await openMobile();
  await expect.element(read).toBeVisible();
  await read.click();
  await expect.element(unread).toBeVisible();
  await page.getByRole('button', { name: 'Answer the Planner: Review the report?' }).click();
  if (width === 390) await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(launcher).toHaveAccessibleDescription('1 unread');
  await launcher.click();
  await expect.element(read).toBeVisible();
});

it.each(['payload', 'fenced Markdown'])('shows recorded failures for current tasks declared in %s', async (declaration) => {
  await page.viewport(1200, 844);
  const track = fixtureTrack('project', { areaId: 'project-area' });
  const description = { kind: 'agent', goal: 'Verify the fix' };
  const task = declaration === 'payload'
    ? { markdown: '', payload: { ...description, id: 'verify-fix' } }
    : { markdown: ['```json', JSON.stringify({ ...description, task_key: 'verify-fix', key: 'stale-replaced' }), '```'].join('\n'), payload: null };
  const report = { ...fixtureReport(), blocks: [
    { id: 'b_9065', kind: 'task', rev: 1, ...task },
    { id: 'withdrawn', kind: 'task', rev: 2, markdown: '', payload: { key: 'stale-withdrawn', declared_by: 'spec', tombstoned_by: 'user', tombstone: {} } },
  ] };
  const failed = { trackId: track.id, kind: 'agent', status: 'failed', createdAtMs: 1, updatedAtMs: 2 };
  renderDailyFixture({ pergaMode: true, initial: '/track/project', reply: request => {
    if (request.path !== '/api/v1/tracks/project') return undefined;
    return { status: 200, statusText: 'OK', body: { ...fixtureSnapshot(track, [], report), tasks: [
      { ...failed, id: 'worker-task', blockId: 'b_9065', taskKey: 'verify-fix', statusDetail: 'The host stopped during this attempt.' },
      { ...failed, id: 'removed-task', blockId: 'removed', taskKey: 'stale-removed' },
      { ...failed, id: 'withdrawn-task', blockId: 'withdrawn', taskKey: 'stale-withdrawn' },
      { ...failed, id: 'replaced-task', blockId: 'b_9065', taskKey: 'stale-replaced' },
    ] } };
  } });

  const notice = page.getByRole('region', { name: 'Notifications', exact: true });
  await expect.element(notice.getByText(/The host stopped during this attempt\./)).toBeVisible();
  expect(notice.getByText(/stale-removed/).query()).toBeNull();
  expect(notice.getByText(/stale-withdrawn/).query()).toBeNull();
  expect(notice.getByText(/stale-replaced/).query()).toBeNull();
  await notice.getByRole('button', { name: /^Open task: verify-fix:/ }).click();
  await expect.element(notice.getByRole('button', { name: /^Read, mark as unread:/ })).toBeVisible();
});
