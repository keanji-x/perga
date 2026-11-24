import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

afterEach(() => {
  cleanup();
  document.getElementById('root')?.remove();
});

it.each([
  { width: 1280, explicitWidget: true },
  { width: 390, explicitWidget: false },
])('navigates from live and archived Flow nodes and back at $width px', async ({ width, explicitWidget }) => {
  await page.viewport(width, 850);
  const origin = fixtureTrack('project', { title: 'Release project', areaId: 'project-area', flowRev: 2 });
  const activeChild = fixtureTrack('trk_cache', { title: 'Cache investigation', areaId: origin.areaId, originTrackId: origin.id });
  const archivedChild = fixtureTrack('trk_storage', { title: 'Storage investigation', areaId: origin.areaId, originTrackId: origin.id });
  const report = fixtureReport();
  if (explicitWidget) report.blocks.push({ id: 'flow', rev: 1, kind: 'widget', payload: null,
    markdown: JSON.stringify({ use: 'builtin:flow', view: 'flow' }) });
  const { router } = renderDailyFixture({ initial: '/track/project', reply: (request) => {
    const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
    if (request.path === '/api/v1/tracks/project/views') return ok({ flowRev: 2, flow: {
      rev: 2,
      nodes: [{ id: 'cache', title: 'Spawn cache investigation', doKind: 'track', outcome: 'ok', status: 'done', childTrackId: activeChild.id }],
      archived: [{ id: 'storage', title: 'Spawn storage investigation', doKind: 'track', outcome: 'ok', status: 'done',
        childTrackId: archivedChild.id, archivedAtMs: 1_760_000_000_000 }],
    } });
    if (request.path === '/api/v1/tracks/project') return ok(fixtureSnapshot(origin, [], report));
    const child = [activeChild, archivedChild].find((track) => request.path === `/api/v1/tracks/${track.id}`);
    if (child !== undefined) return ok(fixtureSnapshot(child));
    if (request.path.endsWith('/views')) return ok({});
    return undefined;
  } });

  const originLink = page.getByRole('button', { name: 'Origin track', exact: true });
  const activeLink = page.getByRole('button', { name: 'Open child track', exact: true });
  await expect.element(activeLink).toBeVisible();
  await expect.element(originLink).not.toBeInTheDocument();

  await activeLink.click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/trk_cache');
  await expect.element(originLink).toBeVisible();
  expect(originLink.element().closest('header')).not.toBeNull();
  await originLink.click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/project');

  await page.getByRole('button', { name: /已归档 1 项/ }).click();
  await page.getByRole('button', { name: 'Open child track from Spawn storage investigation' }).click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/trk_storage');
  await originLink.click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/project');
  await expect.element(activeLink).toBeVisible();
});
