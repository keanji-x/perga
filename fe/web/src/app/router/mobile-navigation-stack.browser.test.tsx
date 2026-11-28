import '../../styles/entry.css';
import { createBrowserHistory } from '@tanstack/react-router';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

const area = '/area/project-area';
const track = '/track/project';
const button = (name: string | RegExp) => page.getByRole('button', { name, exact: typeof name === 'string' });
const back = (label: string) => button(`Back to ${label}`);
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

afterEach(async () => {
  cleanup();
  await page.viewport(1280, 800);
});

async function nativeFixture(initial: string, options: Parameters<typeof renderDailyFixture>[0] = {}) {
  // The browser test runner owns the current window. Use a separate real
  // window so native Back can traverse actual entries without leaving the runner.
  const host = window.open(new URL('./native-history-fixture.html', import.meta.url).href, '_blank');
  if (host === null) throw new Error('Could not open the native history fixture');
  await expect.poll(() => host.location.href.includes('native-history-fixture.html') && host.document.readyState === 'complete').toBe(true);
  host.history.replaceState({}, '', '/outside');
  host.history.pushState({ __TSR_index: 1, __TSR_key: 'cold-link', key: 'cold-link' }, '', initial);
  const history = createBrowserHistory({ window: host });
  const fixture = renderDailyFixture({ ...options, history });
  return {
    ...fixture,
    host,
    at: async (href: string) => {
      await expect.poll(() => host.location.pathname + host.location.search).toBe(href);
      await expect.poll(() => fixture.router.state.resolvedLocation?.href).toBe(href);
    },
    close: () => { cleanup(); history.destroy(); host.close(); },
  };
}

async function openWorkspace() { await button('Open workspace').click(); }
async function openArea() { await button('Project').click(); }
async function openProject() { await button(/^Project evidence\b/).click(); }

it.each(['header', 'native'])('traverses the complete mobile route stack using %s Back', async (mode) => {
  await page.viewport(390, 844);
  const app = await nativeFixture('/');
  try {
    await openWorkspace();
    await app.at('/workspace');
    await openArea();
    await app.at(area);
    await openProject();
    await app.at(track);
    for (const [label, href] of [['Project', area], ['Workspace', '/workspace'], ['Focus', '/']] as const) {
      await expect.element(back(label)).toBeVisible();
      if (mode === 'header') await back(label).click();
      else app.host.history.back();
      await app.at(href);
    }
    await expect.element(button('Open workspace')).toBeVisible();
    app.host.history.forward();
    await app.at('/workspace');
    expect(page.getByRole('dialog', { name: 'Tracks and settings' }).query()).toBeNull();
  } finally { app.close(); }
});

it('links from a Focus row to the Area and returns through the same entries', async () => {
  await page.viewport(320, 640);
  const { router } = renderDailyFixture();
  await button('All').click();
  await button('Open area Project').click();
  await expect.poll(() => router.state.location.pathname).toBe(area);
  await openProject();
  await back('Project').click();
  await expect.poll(() => router.state.location.pathname).toBe(area);
  await back('Focus').click();
  await expect.element(button('Open area Project')).toBeVisible();
  expect(router.state.location.pathname).toBe('/');
});

it('returns from a second Track through Area to the original Track', async () => {
  await page.viewport(390, 844);
  const first = fixtureTrack('project', { areaId: 'project-area', title: 'Project evidence' });
  const second = fixtureTrack('second', { areaId: 'project-area', title: 'Another Track' });
  const { router } = renderDailyFixture({ initial: track, reply: (request) => {
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok([first, second]);
    if (request.path === '/api/v1/tracks/second') return ok(fixtureSnapshot(second));
    return undefined;
  } });
  await button('Open area Project').click();
  await button(/^Another Track\b/).click();
  await expect.poll(() => router.state.location.pathname).toBe('/track/second');
  await back('Project').click();
  await back('Report').click();
  await expect.element(button('Open area Project')).toBeVisible();
  expect(router.state.location.pathname).toBe(track);
});

it('pops a cancelled draft and replaces a submitted draft before opening the Planner', async () => {
  await page.viewport(390, 844);
  const created = fixtureTrack('created', { areaId: 'project-area', title: 'Created Track', leadSessionId: 'created-lead' });
  const snapshot = fixtureSnapshot(created, [fixtureSession('created-lead', 'created', { isLead: true })]);
  const app = await nativeFixture('/', { reply: (request) => {
    if (request.method === 'POST' && request.path === '/api/v1/tracks') return { status: 201, statusText: 'Created', body: snapshot };
    if (request.path === '/api/v1/tracks/created') return ok(snapshot);
    return undefined;
  } });
  try {
    await button('New track').click();
    await app.at(`${area}/new`);
    await back('Focus').click();
    await app.at('/');
    expect(page.getByRole('dialog', { name: 'Tracks and settings' }).query()).toBeNull();
    await openWorkspace();
    await openArea();
    await button('New track').click();
    await app.at(`${area}/new`);
    const length = app.host.history.length;
    await page.getByRole('combobox', { name: 'What this track should do' }).fill('Review the release');
    await button('Create track').click();
    await app.at('/track/created');
    expect(app.host.history.length).toBe(length);
    const close = button('Close conversation');
    await expect.element(close).toBeVisible();
    await close.click();
    await back('Project').click();
    await app.at(area);
    expect(app.requests.filter((request) => request.method === 'POST' && request.path === '/api/v1/tracks')).toHaveLength(1);
  } finally { app.close(); }
});

function memoryReply(request: ApiRequest) {
  const global = request.path.includes('/global/');
  if (request.path === '/api/v1/tracks/project/terminals') {
    return ok([{ id: 'terminal', trackId: 'project', title: 'Terminal', status: 'exited', cwd: '/tmp', createdAtMs: 1 }]);
  }
  if (request.path.startsWith('/api/v1/memory/') && request.path.endsWith('/track')) {
    return ok({
      scopeKey: global ? 'global' : 'area:project-area', areaId: global ? null : 'project-area',
      trackId: 'memory', leadSessionId: 'memory-lead',
    });
  }
  if (request.path === '/api/v1/tracks/memory') return ok(fixtureSnapshot(
    fixtureTrack('memory', { title: 'Memory Report', areaId: 'sys_memory', leadSessionId: 'memory-lead' }),
    [fixtureSession('memory-lead', 'memory', { isLead: true })],
  ));
  return undefined;
}

it('returns from global and Area Memory, Today, and Settings one entry at a time', async () => {
  await page.viewport(390, 844);
  const app = await nativeFixture('/', { reply: memoryReply });
  try {
    await openWorkspace();
    await button('Memory').click();
    await app.at('/memory');
    await expect.element(page.getByRole('heading', { name: 'Memory Report', level: 1 })).toBeVisible();
    await back('Workspace').click();
    await openArea();
    await button('Area memory').click();
    await app.at(`${area}/memory`);
    await back('Project').click();
    await back('Workspace').click();
    await button('Today').click();
    await app.at('/?view=today');
    await back('Workspace').click();
    await button('Settings').click();
    await app.at('/settings');
    await button('Appearance').click();
    await app.at('/settings/appearance');
    await back('Settings').click();
    await app.at('/settings');
    await back('Workspace').click();
    await app.at('/workspace');
  } finally { app.close(); }
});

it.each([
  [track, 'Focus', '/'], [area, 'Focus', '/'], ['/workspace', 'Focus', '/'],
  ['/memory', 'Focus', '/'], ['/settings/appearance', 'Settings', '/settings'],
  [`${area}/memory`, 'Project', area], [`${area}/new`, 'Project', area],
  ['/area/missing/new', 'Focus', '/'], ['/?day=2026-10-04', 'Focus', '/'],
  [`${track}?card=terminal`, 'Report', track], [`${track}?panel=cards`, 'Report', track],
])('replaces a cold %s link via Back to %s without traversing to the earlier document', async (initial, label, parent) => {
  await page.viewport(390, 844);
  const app = await nativeFixture(initial, { reply: memoryReply });
  try {
    const length = app.host.history.length;
    await back(label).click();
    await app.at(parent);
    expect(app.host.history.length).toBe(length);
    expect(app.router.history.location.state.__TSR_index).toBe(1);
  } finally { app.close(); }
});

it('restores the Focus, Workspace and Area scrollports and resets a fresh Area visit', async () => {
  await page.viewport(390, 844);
  const tracks = Array.from({ length: 32 }, (_, i) => fixtureTrack(`t${i}`, { areaId: 'project-area', title: `Evidence ${i}` }));
  const userArea = { name: 'Project', kind: 'user', color: '#6574cd', sortOrder: 0, createdAtMs: 1, updatedAtMs: 1 };
  const { router } = renderDailyFixture({ reply: (request) => {
    if (request.path === '/api/v1/areas') return ok([
      { ...userArea, id: 'project-area' },
      ...Array.from({ length: 22 }, (_, index) => ({ ...userArea, name: `Other Area ${index}`, id: `other-${index}` })),
    ]);
    if (request.path === '/api/v1/areas/project-area/tracks?includeClosed=true') return ok(tracks);
    const target = tracks.find((item) => request.path === `/api/v1/tracks/${item.id}`);
    return target === undefined ? undefined : ok(fixtureSnapshot(target));
  } });
  const scroll = async (host: HTMLElement, top: number) => {
    host.scrollTop = top;
    await new Promise<void>((resolve) => { requestAnimationFrame(() => { resolve(); }); });
    expect(host.scrollTop).toBe(top);
    return host;
  };
  const focus = () => document.querySelector<HTMLElement>('[data-nc-focus-feed]');
  const workspace = () => document.querySelector<HTMLElement>('[data-nc-workspace-scroll]');
  const areaBody = () => document.querySelector<HTMLElement>('[data-nc-area-scroll]');

  await button('All').click();
  await button(/^Evidence 12\b/).findElement();
  await scroll(focus()!, 700);
  // Programmatic activation leaves the offset as set instead of scrolling
  // the click target into view as Playwright's physical click does.
  (button(/^Evidence 12\b/).element() as HTMLButtonElement).click();
  await back('Focus').click();
  await expect.poll(() => focus()?.scrollTop).toBe(700);
  await openWorkspace();
  await button('Project').findElement();
  await scroll(workspace()!, 400);
  (button('Project').element() as HTMLButtonElement).click();
  await button(/^Evidence 12\b/).findElement();
  expect(areaBody()?.scrollTop).toBe(0);
  await scroll(areaBody()!, 600);
  (button(/^Evidence 12\b/).element() as HTMLButtonElement).click();
  await back('Project').click();
  await expect.poll(() => areaBody()?.scrollTop).toBe(600);
  await back('Workspace').click();
  await expect.poll(() => workspace()?.scrollTop).toBe(400);
  await openArea();
  await expect.poll(() => router.state.resolvedLocation?.href).toBe(area);
  expect(areaBody()?.scrollTop).toBe(0);
});

it.each(['/workspace', area])('replaces mobile-only %s on desktop', async (initial) => {
  await page.viewport(1280, 800);
  const app = await nativeFixture(initial);
  try {
    await app.at('/');
    expect(app.router.history.location.state.__TSR_index).toBe(1);
    await expect.element(page.getByRole('navigation', { name: 'Workspace' })).toBeVisible();
  } finally { app.close(); }
});

it('returns directly from password setup to Workspace', async () => {
  await page.viewport(390, 844);
  const app = await nativeFixture('/', { identity: {
    sessionId: 'local', userId: 'owner', username: 'local', displayName: 'Local user',
    role: 'owner', mode: 'local', expiresAtMs: Date.now() + 60_000,
  } });
  try {
    await openWorkspace();
    await button('Set up password').click();
    await app.at('/settings/account');
    await back('Workspace').click();
    await app.at('/workspace');
  } finally { app.close(); }
});

it('pops a pushed card so the next Back leaves the report', async () => {
  await page.viewport(390, 844);
  const app = await nativeFixture('/', { reply: memoryReply });
  try {
    await button('All').click();
    await openProject();
    await app.at(track);
    await app.router.navigate({ to: track, search: { card: 'terminal' } });
    await app.at(`${track}?card=terminal`);
    await back('Report').click();
    await app.at(track);
    expect(app.router.history.location.state.__TSR_index).toBe(2);
    await back('Focus').click();
    await app.at('/');
  } finally { app.close(); }
});

it('uses native history to exit a desktop Settings visit in one step', async () => {
  await page.viewport(1280, 800);
  const app = await nativeFixture(`${track}?file=README.md`);
  try {
    await button(/^Account menu for /).click();
    await page.getByRole('menuitem', { name: 'Settings', exact: true }).click();
    await app.at('/settings');
    await button('About').click();
    await app.at('/settings/about');
    await button('Planners').click();
    await app.at('/settings/planners');
    await button('Close').click();
    await app.at(`${track}?file=README.md`);
    expect(app.router.history.location.state.__TSR_index).toBe(1);
  } finally { app.close(); }
});

it('omits Workspace and Area navigation from a cold shared Track', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: track, shareScope: { trackId: 'project' } });
  await expect.element(page.getByRole('heading', { name: 'Project evidence', level: 2 })).toBeVisible();
  expect(button(/^Back to /).query()).toBeNull();
  expect(button('Open workspace').query()).toBeNull();
  expect(button('Open area Project').query()).toBeNull();
});
