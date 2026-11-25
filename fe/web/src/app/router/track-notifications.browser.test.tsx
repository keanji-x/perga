import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach,expect,it } from 'vitest';
import { page } from 'vitest/browser';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { sessionPage } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport,fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';

afterEach(cleanup);

it.each([1200, 390])('acknowledges a conversation update through the status entry and retains its receipt at %ipx', async width => {
  await page.viewport(width, 844);
  const receipts = createReadReceiptsKernel({ baselineMs: 0 });
  const track = fixtureTrack('project', { areaId: 'project-area', leadSessionId: 'project-planner' });
  const planner = fixtureSession('project-planner', track.id, { isLead: true, title: 'Project conversation', lastNotifiableTurnAtMs: 20 });
  const mount = () => renderDailyFixture({ initial: '/track/project', receipts, reply: request => {
    if (request.path === '/api/v1/tracks/project') return { status: 200, statusText: 'OK', body: fixtureSnapshot(track, [planner]) };
    if (request.path.includes('/project-planner/items?')) return { status: 200, statusText: 'OK', body: sessionPage(planner) };
    return undefined;
  } });
  const openEntry = async () => {
    if (width === 390) {
      await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
    } else {
      await page.getByRole('button', { name: /Conversation Project conversation/ }).click();
    }
  };
  const activity = () => (width === 390
    ? document.querySelector('[data-nc-mobile-header] [data-nc-activity="unread"], [data-nc-track-page] [data-nc-activity="unread"]')
    : document.querySelector('[data-nc-desktop-panel] [data-nc-activity="unread"]')
  )?.getAttribute('data-nc-activity');
  mount();
  await expect.poll(activity).toBe('unread');
  expect(document.querySelector('[data-nc-notification-launcher]')).toBeNull();
  expect(page.getByRole('region', { name: 'Notifications', exact: true }).query()).toBeNull();
  await openEntry();
  await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  await expect.poll(() => receipts.requests.filter(request => request.path.endsWith('/project-planner/read')).length).toBe(1);
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(activity).toBeUndefined();
  cleanup();
  mount();
  await expect.element(page.getByRole('region', { name: 'Notifications', exact: true })).not.toBeInTheDocument();
  await expect.poll(activity).toBeUndefined();
  // Completion acknowledgements retain their watermark on a new visit; #103
  // avoids redundant track-view writes when there is no manual unread.
  await openEntry();
  await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  expect(receipts.requests.filter(request => request.path.endsWith('/project-planner/read')).map(request => request.body))
    .toEqual([{ readThroughMs: 20 }, { readThroughMs: 20 }]);
  expect(receipts.requests.filter(request => request.path.endsWith('/project/read'))).toHaveLength(0);
});

it.each(['payload', 'fenced Markdown'])('keeps historical task failures out of header attention and exposes task details from %s', async declaration => {
  await page.viewport(1200, 844);
  const track = fixtureTrack('project', { areaId: 'project-area', leadSessionId: 'project-planner' });
  const planner = fixtureSession('project-planner', track.id, { isLead: true });
  const description = { kind: 'agent', goal: 'Verify the fix' };
  const task = declaration === 'payload'
    ? { markdown: '', payload: { ...description, id: 'verify-fix' } }
    : { markdown: ['```json', JSON.stringify({ ...description, task_key: 'verify-fix' }), '```'].join('\n'), payload: null };
  const report = { ...fixtureReport(), blocks: [{ id: 'b_9065', kind: 'task', rev: 1, ...task }] };
  renderDailyFixture({ initial: '/track/project', reply: request => {
    if (request.path !== '/api/v1/tracks/project' && request.path !== '/api/v1/tracks/project/report') return undefined;
    return { status: 200, statusText: 'OK', body: { ...fixtureSnapshot(track, [planner], report), tasks: [{
      trackId: track.id, kind: 'agent', status: 'failed', createdAtMs: 1, updatedAtMs: 2,
      id: 'worker-task', blockId: 'b_9065', taskKey: 'verify-fix', statusDetail: 'The host stopped during this attempt.',
    }] } };
  } });
  await expect.element(page.getByRole('button', { name: 'Progress', exact: true })).toBeVisible();
  expect(document.querySelector('[data-nc-track-page] [data-nc-activity="failed"]')).toBeNull();
  expect(page.getByRole('region', { name: 'Notifications', exact: true }).query()).toBeNull();
  await page.getByRole('button', { name: 'Progress', exact: true }).click();
  await expect.element(page.getByText('Failed', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('button', { name: /verify-fix/ })).toHaveAccessibleDescription('Failed — The host stopped during this attempt.');
});
