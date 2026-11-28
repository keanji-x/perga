import { fixtureBootstrap, fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack } from '../router/session-contract-fixture.ts';
import { sessionPage } from '../router/conversation-session-data.ts';
import { renderDailyFixture } from '../router/daily-planner-fixture.tsx';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import '../../styles/entry.css';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, render, waitFor } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, describe, expect, it } from 'vitest';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from '../router/public.tsx';
import { bootTestCardRuntime } from '../router/test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';

declare module 'vitest/internal/browser' {
  interface BrowserCommands { emulateReducedMotion(reduce: boolean): Promise<void> }
}

afterEach(cleanup);
const AREA = { id: 'c1', name: 'Product', color: '#5B8DEF', sortOrder: 1, folders: [], createdAtMs: 1, updatedAtMs: 1 };
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
function setup(path: string, areaName = AREA.name, onRequest: (request: ApiRequest) => void = () => undefined, title = 'Responsive mobile UI', options: Readonly<{ trackAreaId?: string; failTracks?: boolean; failAreas?: () => boolean; longLists?: boolean; longReport?: boolean; viewportRoot?: boolean }> = {}) {
  const track = fixtureTrack('w1', { areaId: options.trackAreaId ?? 'c1', title, leadSessionId: 'planner', workspaceDriver: 'vcs-workspace', workspaceSpec: { logicalRoot: '/tmp', worktreePath: '/tmp/.worktrees/w1' } });
  const report = fixtureReport(options.longReport ? '# Findings\n\n' + Array.from({ length: 60 }, (_, index) => `Paragraph ${index + 1}: report content remains below the fixed workspace header.`).join('\n\n') + '\n\n[Source detail](perga://source/src_2c9e0a1b)' : '## Findings\n\n[Source detail](perga://source/src_2c9e0a1b)');
  const planner = fixtureSession('planner', 'w1', { title: 'Design review', isLead: true });
  const transport: ApiTransportPort = { send(request) {
    return Promise.resolve(reply(request));
  } };
  function reply(request: ApiRequest): ApiTransportResponse {
    onRequest(request);
    if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap());
    if (request.path === '/api/v1/memory/global/track') return ok({ scopeKey: 'global', areaId: null, trackId: track.id, leadSessionId: planner.id });
    if (request.method === 'PATCH' && request.path === '/api/v1/tracks/w1') {
      const next = (request.body as { title: string }).title;
      if (next === 'Rejected name') return { status: 500, statusText: 'Server Error', body: { error: 'Rename unavailable' } };
      track.title = next;
      return ok(track);
    }
    if ((request.path === '/api/v1/areas' && options.failAreas?.()) || (options.failTracks && request.path === '/api/v1/areas/c2/tracks?includeClosed=true')) return { status: 500, statusText: 'Server Error', body: { error: 'List unavailable' } };
    if (request.path === '/api/v1/areas') return ok([...(options.longLists ? Array.from({ length: 18 }, (_, index) => ({ ...AREA, id: `extra${index}`, name: `Area ${index}`, sortOrder: index - 18 })) : []), { ...AREA, name: areaName }, { ...AREA, id: 'c2', name: 'Frontend' }]);
    if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([...(track.areaId === 'c1' ? [track] : []), { ...track, areaId: 'c1', id: 'w2', title: 'Another track' }, ...(options.longLists ? Array.from({ length: 25 }, (_, index) => ({ ...track, id: `long${index}`, title: `Long track ${index}` })) : [])]);
    if (request.path === '/api/v1/areas/c2/tracks?includeClosed=true') return ok([{ ...track, areaId: 'c2', id: 'w3', title: 'Frontend track' }]);
    if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(track, [planner], report));
    if (request.path === '/api/v1/tracks/w1/terminals') return ok([{ id: 'terminal', trackId: 'w1', title: 'Terminal', status: 'exited', cwd: '/tmp', createdAtMs: 1 }]);
    if (request.path === '/api/v1/tracks/w1/sessions') return ok([planner]);
    if (request.path === '/api/v1/tracks/w2') return ok(fixtureSnapshot({ ...track, id: 'w2', title: 'Another track' }));
    if (request.path === '/api/v1/tracks/w1/report') return ok(report);
    if (request.path.startsWith('/api/v1/sessions/planner/items?')) return ok(sessionPage(planner));
    return ok([]);
  }
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }),
    client, cards: bootTestCardRuntime() });
  router.update({ history: createMemoryHistory({ initialEntries: [path] }) });
  const view = render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  if (options.viewportRoot) view.container.id = 'root';
  return router;
}
const settlePaint = () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));

async function reportScrollport(): Promise<HTMLElement> {
  await page.getByRole('heading', { name: 'Findings', exact: true }).findElement();
  return document.querySelector<HTMLElement>('[data-nc-track-page]')!;
}

async function revealTrackSelector(title = 'Responsive mobile UI') {
  const scrollport = await reportScrollport();
  scrollport.scrollTop = scrollport.scrollHeight;
  const selector = page.getByRole('button', { name: `Switch track, ${title}`, exact: true });
  await expect.element(selector).toBeVisible();
  return selector;
}

it('reveals the Track title only past the mobile document heading and opens the Area from both surfaces', async () => {
  await page.viewport(390, 844);
  const longTitle = 'Responsive mobile UI with a deliberately long report title that wraps on a phone';
  const router = setup('/track/w1', AREA.name, undefined, longTitle, { longReport: true, viewportRoot: true });
  let scrollport = await reportScrollport();
  const shell = () => page.elementLocator(document.querySelector('[data-nc-workspace-header] header')!);
  const docTitle = await page.getByRole('heading', { name: longTitle, exact: true, level: 1 }).findElement();
  const docHeader = docTitle.closest('header')!;
  const area = page.getByRole('button', { name: 'Open area Product', exact: true });
  const areaButton = await area.findElement();
  expect(docHeader).toContainElement(areaButton);
  expect(docTitle.getBoundingClientRect().height).toBeGreaterThan(parseFloat(getComputedStyle(docTitle).lineHeight));
  expect(docTitle.getBoundingClientRect().bottom).toBeLessThan(areaButton.getBoundingClientRect().top);
  expect(docHeader.textContent).toMatch(/Product·Updated \d{4}-\d{2}-\d{2}/);
  expect(getComputedStyle(areaButton).borderWidth).toBe('0px');
  expect(getComputedStyle(areaButton).backgroundColor).toBe('rgba(0, 0, 0, 0)');
  expect(getComputedStyle(docHeader).borderBottomWidth).toBe('1px');
  const selector = () => shell().getByRole('button', { name: `Switch track, ${longTitle}`, exact: true });
  const expectBlank = () => {
    expect(selector().query()).toBeNull();
    expect(shell().getByRole('button', { name: /^Switch area,/ }).query()).toBeNull();
    expect(getComputedStyle(shell().element()).boxShadow).toBe('none');
  };
  expect(scrollport.scrollTop).toBe(0);
  expectBlank();

  await area.click();
  await expect.element(page.getByRole('region', { name: 'Open tracks' })).toBeVisible();
  await page.getByRole('button', { name: 'Back to Report' }).click();
  await expect.element(area).toHaveFocus();
  scrollport = await reportScrollport();
  expect(router.state.location.pathname).toBe('/track/w1');

  const currentHeader = area.element().closest('header')!;
  const boundary = currentHeader.getBoundingClientRect().bottom - scrollport.getBoundingClientRect().top;
  scrollport.scrollTop = boundary - 8;
  await settlePaint();
  expect(scrollport.scrollTop).toBeGreaterThan(0);
  expectBlank();
  scrollport.scrollTop = boundary + 8;
  await expect.element(selector()).toBeVisible();
  expect(currentHeader.getBoundingClientRect().bottom).toBeLessThan(scrollport.getBoundingClientRect().top);
  expect([...document.querySelectorAll('[data-nc-page-title]')]).toEqual([selector().element()]);
  const label = selector().element().querySelector('span')!;
  expect(label.scrollWidth).toBeGreaterThan(label.clientWidth);
  expect(getComputedStyle(label)).toMatchObject({ whiteSpace: 'nowrap', textOverflow: 'ellipsis' });
  expect(getComputedStyle(shell().element()).boxShadow).not.toBe('none');

  await selector().click();
  await expect.element(page.getByRole('region', { name: 'Open tracks' })).toBeVisible();
  await page.getByRole('button', { name: 'Back to Report' }).click();
  scrollport = await reportScrollport();
  scrollport.scrollTop = boundary + 8;
  (await selector().findElement() as HTMLElement).focus();
  await userEvent.keyboard('{F2}');
  await expect.element(page.getByRole('textbox', { name: 'Track title' })).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  await expect.element(selector()).toHaveFocus();
  scrollport.scrollTop = 0;
  await settlePaint();
  expectBlank();
});

it('uses the document title for focus restoration and F2 on a short mobile report', async () => {
  await page.viewport(390, 844);
  setup('/track/w1', AREA.name, undefined, undefined, { viewportRoot: true });
  const scrollport = await reportScrollport();
  const title = await page.getByRole('heading', { name: 'Responsive mobile UI', level: 1 }).findElement();
  expect(scrollport.scrollTop).toBe(0);
  expect(scrollport.scrollHeight).toBe(scrollport.clientHeight);
  expect([...document.querySelectorAll('[data-nc-page-title]')]).toEqual([title]);
  (title as HTMLElement).focus();
  expect(document.activeElement).toBe(title);
  await userEvent.keyboard('{F2}');
  await expect.element(page.getByRole('textbox', { name: 'Track title' })).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.elementLocator(title)).toHaveFocus();
  expect(scrollport.scrollTop).toBe(0);

  const chevron = page.getByRole('button', { name: 'Open area Product' }).element().querySelector('svg')!;
  expect(getComputedStyle(chevron).transform).toBe('none');
  scrollport.dir = 'rtl';
  expect(getComputedStyle(chevron).transform).toBe('matrix(-1, 0, 0, 1, 0, 0)');
});

it('removes abandoned title containers when switching Tracks', async () => {
  await page.viewport(390, 844);
  const router = setup('/track/w1');
  const back = await page.getByRole('button', { name: 'Back to Focus' }).findElement();
  const host = back.closest('[data-nc-workspace-header]')!;
  for (const id of ['w2', 'w1', 'w2', 'w1']) {
    await router.navigate({ to: '/track/$trackId', params: { trackId: id } });
    await page.getByRole('heading', { name: id === 'w1' ? 'Responsive mobile UI' : 'Another track', exact: true }).findElement();
    await settlePaint();
    expect(document.querySelectorAll('[aria-label^="Switch track,"]')).toHaveLength(1);
    expect(host.isConnected).toBe(true);
  }
  await page.viewport(1280, 720);
});

it('uses Escape to close Planner before its Conversations page', async () => {
  await page.viewport(390, 844);
  const router = setup('/track/w1');
  await router.navigate({ to: '/track/w1', search: { panel: 'conversations' } });
  await page.getByRole('button', { name: /Design review/ }).click();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).findElement();
  await page.elementLocator(document.querySelector('[data-nc-mobile-chat-panel]')!).getByRole('heading', { name: 'Design review', exact: true }).findElement();
  await userEvent.keyboard('{Escape}');
  await settlePaint();
  expect(router.state.location.search).toHaveProperty('panel', 'conversations');
  await expect.element(page.getByRole('heading', { name: 'Conversations', exact: true })).toBeVisible();
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(false);
  await expect.poll(() => document.activeElement).toBe(await page.getByRole('button', { name: /Design review/ }).findElement());
  await userEvent.keyboard('{Escape}');
  await settlePaint();
  expect(router.state.location.search).not.toHaveProperty('panel');
  await page.viewport(1280, 720);
});

async function editTrack(): Promise<void> {
  // Renaming belongs to the desktop title. The live editor is preserved when
  // the reader subsequently switches to the compact presentation.
  await page.viewport(1280, 900);
  await page.getByRole('button', { name: 'Rename track', exact: true }).click();
  await page.getByRole('textbox', { name: 'Track title', exact: true }).findElement();
  await page.viewport(390, 844);
}

describe('Unified mobile headers', () => {
  it.each([320, 390])('centers the static Memory title with the Back glyph at %ipx', async (width) => {
    await page.viewport(width, 844);
    setup('/memory', AREA.name, () => undefined, 'Global Memory', { trackAreaId: 'sys_memory' });
    const title = await page.getByRole('heading', { name: 'Global Memory', exact: true, level: 1 }).findElement();
    const shellHeader = page.elementLocator(document.querySelector('[data-nc-workspace-header] header')!);
    const menu = await shellHeader.getByRole('button', { name: 'Back to Focus', exact: true }).findElement();
    await settlePaint();

    const glyphBox = menu.querySelector('svg')!.getBoundingClientRect();
    const titleBox = title.getBoundingClientRect();
    expect(Math.abs((glyphBox.top + glyphBox.height / 2) - (titleBox.top + titleBox.height / 2))).toBeLessThanOrEqual(1);
  });

  it('uses clean Area rows and a separate Tracks page with the same actions', async () => {
    await page.viewport(390, 844);
    const router = setup('/');
    await page.getByRole('button', { name: 'Open workspace' }).click();
    const area = await page.getByRole('button', { name: 'Product', exact: true }).findElement();
    const row = area.closest('li')!;
    const icons = row.querySelectorAll('svg');
    expect(icons).toHaveLength(2);
    expect(icons[1].getBoundingClientRect().right).toBeGreaterThan(row.getBoundingClientRect().right - 32);
    expect(row.querySelector('[style]')).toBeNull();
    await settlePaint();
    await Promise.all(document.getAnimations().filter((animation) => animation.effect?.getTiming().iterations !== Infinity).map((animation) => animation.finished.catch(() => undefined)));
    await page.screenshot({ path: '../../../../test-results/clean-390-areas.png' });
    expect(page.getByRole('button', { name: /^Responsive mobile UI\b/ }).query()).toBeNull();
    await page.elementLocator(area).click();
    await page.getByRole('heading', { name: 'Product', exact: true }).findElement();
    await settlePaint();
    await Promise.all(document.getAnimations().filter((animation) => animation.effect?.getTiming().iterations !== Infinity).map((animation) => animation.finished.catch(() => undefined)));
    await page.screenshot({ path: '../../../../test-results/clean-390-tracks.png' });
    await page.getByRole('button', { name: /^Responsive mobile UI\b/ }).findElement();
    expect(document.querySelector('[data-nc-area-scroll] [aria-label="Workspace actions"]')).toBeNull();
    await expect.element(page.getByRole('button', { name: 'New track', exact: true })).toBeVisible();
    expect(router.state.location.pathname).toBe('/area/c1');
    await page.getByRole('button', { name: 'Back to Workspace' }).click();
    await page.getByRole('heading', { name: 'Workspace', exact: true }).findElement();
    await page.getByRole('button', { name: 'Back to Focus' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/');
    await page.viewport(1280, 720);
  });

  it('keeps the report top bar blank and preserves the desktop rename editor on mobile', async () => {
    await page.viewport(390, 844);
    setup('/track/w1');
    await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
    await expect.element(page.getByRole('button', { name: 'Track actions', exact: true })).toBeVisible();
    expect(page.getByRole('button', { name: /^Switch track,/ }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'Rename track', exact: true }).query()).toBeNull();
    await editTrack();
    const input = await page.getByRole('textbox', { name: 'Track title', exact: true }).findElement();
    expect(document.activeElement).toBe(input);
    await page.screenshot({ path: '../../../../test-results/clean-390-edit-track.png' });
    await userEvent.keyboard('{Escape}');
    await page.viewport(1280, 720);
  });

  it('switches Tracks from the document Area button without starting a rename', async () => {
    await page.viewport(390, 844);
    const writes: ApiRequest[] = [];
    const router = setup('/track/w1', AREA.name, (request) => { if (request.method !== 'GET') writes.push(request); });
    await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
    const selector = page.getByRole('button', { name: 'Open area Product', exact: true });
    expect(selector.query()).not.toBeNull();
    await selector.click();
    expect(page.getByRole('textbox', { name: 'Track title', exact: true }).query()).toBeNull();
    expect((await page.getByRole('button', { name: /^Responsive mobile UI\b/ }).findElement()).getAttribute('aria-current')).toBe('page');
    await page.getByRole('button', { name: /^Another track\b/ }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/track/w2');
    expect(writes).toEqual([]);
    await editTrack();
    const input = page.getByRole('textbox', { name: 'Track title', exact: true });
    await input.fill('Cancelled draft');
    await userEvent.keyboard('{Escape}');
    await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
    expect(writes).toEqual([]);
    await page.viewport(1280, 720);
  });

  it.each(['tracks', 'areas'])('uses the Track’s exact Area and exposes %s read failures on the destination', async (failure) => {
    await page.viewport(320, 844);
    let areasUnavailable = failure === 'areas';
    setup('/track/w1', AREA.name, undefined, 'Second Area track', {
      trackAreaId: 'c2', failTracks: failure === 'tracks', failAreas: () => areasUnavailable, viewportRoot: true,
    });
    const scrollport = await reportScrollport();
    expect(scrollport.scrollTop).toBe(0);
    expect(scrollport.scrollHeight).toBe(scrollport.clientHeight);
    const opener = await page.getByRole('button', {
      name: failure === 'areas' ? 'Open workspace' : 'Open area Frontend', exact: true,
    }).findElement();
    if (failure === 'areas') expect(opener.textContent).toBe('Workspace');
    await page.elementLocator(opener).click();
    /* The 500's text ("List unavailable") is the server's: the item is the fixed sentence alone. */
    const alert = await page.getByRole('alert').findElement();
    expect(alert.textContent).toContain(failure === 'areas' ? 'Areas are unavailable.' : 'Tracks are unavailable.');
    expect(alert.textContent).not.toContain('List unavailable');
    expect(page.getByRole('button', { name: /^Another track\b/ }).query()).toBeNull();
    const retry = page.getByRole('button', { name: 'Retry', exact: true });
    await expect.element(retry).toBeVisible();
    if (failure === 'areas') {
      areasUnavailable = false;
      await retry.click();
      await expect.element(page.getByRole('button', { name: 'Frontend', exact: true })).toBeVisible();
    }
    await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
    await expect.element(page.getByRole('button', { name: 'Open area Frontend', exact: true })).toBeVisible();
    await page.viewport(1280, 720);
  });

  it('keeps the live rename input and draft without a PATCH when changing header hosts', async () => {
    await page.viewport(390, 844);
    const writes: ApiRequest[] = [];
    setup('/track/w1', AREA.name, (request) => { if (request.method !== 'GET') writes.push(request); });
    await editTrack();
    const input = await page.getByRole('textbox', { name: 'Track title', exact: true }).findElement();
    await page.getByRole('textbox', { name: 'Track title', exact: true }).fill('Rename draft before resize');
    (input as HTMLInputElement).setSelectionRange(2, 8);
    await page.viewport(1280, 900);
    const desktopInput = await page.getByRole('textbox', { name: 'Track title', exact: true }).findElement();
    expect(desktopInput).toBe(input);
    expect(document.activeElement).toBe(input);
    expect((input as HTMLInputElement).value).toBe('Rename draft before resize');
    expect([(input as HTMLInputElement).selectionStart, (input as HTMLInputElement).selectionEnd]).toEqual([2, 8]);
    expect(writes).toEqual([]);
    await page.viewport(390, 844);
    const phoneInput = await page.getByRole('textbox', { name: 'Track title', exact: true }).findElement();
    expect(phoneInput).toBe(input);
    expect(document.activeElement).toBe(input);
    expect((input as HTMLInputElement).value).toBe('Rename draft before resize');
    expect([(input as HTMLInputElement).selectionStart, (input as HTMLInputElement).selectionEnd]).toEqual([2, 8]);
    expect(writes).toEqual([]);
    await userEvent.keyboard('{Escape}');
    expect(writes).toEqual([]);
    await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
  });

  it('does not open the title menu or editor from the synthesized click after an Enter commit', async () => {
    await page.viewport(390, 844);
    const writes: ApiRequest[] = [];
    setup('/track/w1', AREA.name, (request) => { if (request.method === 'PATCH') writes.push(request); },
      'Responsive mobile UI', { longReport: true, viewportRoot: true });
    const trigger = await revealTrackSelector();
    (trigger.element() as HTMLElement).focus();
    await userEvent.keyboard('{F2}');
    const input = page.getByRole('textbox', { name: 'Track title', exact: true });
    await input.fill('Committed title');
    await userEvent.keyboard('{Enter}');
    const selector = await page.getByRole('button', { name: 'Switch track, Committed title', exact: true }).findElement();
    selector.dispatchEvent(new MouseEvent('click', { bubbles: true, detail: 0 }));
    await settlePaint();
    expect(page.getByRole('textbox', { name: 'Track title', exact: true }).query()).toBeNull();
    expect(page.getByRole('menu').query()).toBeNull();
    expect(writes).toHaveLength(1);
    await page.viewport(1280, 720);
  });

  it('still commits a real user blur and preserves empty names and rejected rename drafts', async () => {
    await page.viewport(390, 844);
    const writes: ApiRequest[] = [];
    setup('/track/w1', AREA.name, (request) => { if (request.method === 'PATCH') writes.push(request); }, '');
    await editTrack();
    const input = page.getByRole('textbox', { name: 'Track title', exact: true });
    expect((await input.findElement() as HTMLInputElement).value).toBe('');
    await userEvent.keyboard('{Escape}');
    expect(writes).toEqual([]);
    await editTrack();
    await input.fill('Changed by user');
    await page.getByRole('heading', { name: 'Findings', exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0].body).toEqual({ title: 'Changed by user' });
    await userEvent.keyboard('{Escape}');
    await editTrack();
    await input.fill('Rejected name');
    await page.getByRole('heading', { name: 'Findings', exact: true }).click();
    await expect.poll(() => writes.length).toBe(2);
    const failedInput = await input.findElement();
    expect((failedInput as HTMLInputElement).value).toBe('Rejected name');
    expect(page.getByRole('menuitem', { name: 'Edit track', exact: true }).query()).toBeNull();
    await userEvent.keyboard('{Escape}');
    expect(await input.findElement()).toBe(failedInput);
    await page.elementLocator(failedInput).click();
    expect(document.activeElement).toBe(failedInput);
    await userEvent.keyboard('{Escape}');
    await editTrack();
    expect((await input.findElement() as HTMLInputElement).value).toBe('Changed by user');
    await userEvent.keyboard('{Escape}');
  });

  it.each([320, 390])('measures the same header frame across the composed mobile pages at %ipx', async (width) => {
    await page.viewport(width, 844);
    const router = setup('/track/w1');
    const frames: Array<{ name: string; x: number; y: number; width: number; height: number; left: number; top: number }> = [];
    const record = async (name: string, header: HTMLElement) => {
      // Content is inside the Sheet paint owner; waiting on content alone
      // observes no animations while a first entry still translates the header.
      const presentation = header.closest('[data-nc-sheet-panel]')
        ?? header.closest('[data-nc-mobile-page], [data-nc-drawer]');
      await Promise.all(presentation?.getAnimations().map((animation) => animation.finished) ?? []);
      await Promise.all(header.getAnimations({ subtree: true }).filter((animation) => animation.effect?.getTiming().iterations !== Infinity)
        .map((animation) => animation.finished.catch(() => undefined)));
      await settlePaint();
      const box = header.getBoundingClientRect();
      const button = name === 'chat' ? header.querySelector('button[aria-label="Close conversation"]')! : header.querySelector('button')!;
      const hit = button.getBoundingClientRect();
      frames.push({ name, x: box.x, y: box.y, width: box.width, height: box.height, left: hit.x, top: hit.y });
      if (name === 'chat') {
        expect(box.width).toBeGreaterThanOrEqual(width - 2);
        expect(box.width).toBeLessThanOrEqual(width);
        expect(box.y).toBeGreaterThan(60);
        expect(hit.height).toBeGreaterThanOrEqual(44);
        await page.screenshot({ path: `../../../../test-results/unified-${width}-${name}.png` });
        return;
      }
      expect(box.x).toBe(0);
      expect(box.y).toBe(0);
      expect(box.width).toBe(width);
      expect(box.height).toBeGreaterThanOrEqual(52);
      expect(hit.height).toBeGreaterThanOrEqual(44);
      const title = header.querySelector<HTMLElement>('[aria-label^="Switch track,"]')
        ?? header.querySelector<HTMLElement>('[aria-label^="Switch area,"]')
        ?? header.querySelector<HTMLElement>('h1, h2');
      if (title !== null) expect(title.getBoundingClientRect().right).toBeLessThanOrEqual(width);

      await page.screenshot({ path: `../../../../test-results/unified-${width}-${name}.png` });
    };
    await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
    await record('track', document.querySelector<HTMLElement>('[data-nc-workspace-header] header')!);
    const selector = await page.getByRole('button', { name: 'Open area Product', exact: true }).findElement();
    expect(page.getByRole('button', { name: /^Switch track,/ }).query()).toBeNull();
    expect(selector.getBoundingClientRect().right).toBeLessThanOrEqual(width - 16);
    await page.elementLocator(selector).click();
    const trackPage = await page.getByRole('region', { name: 'Open tracks' }).findElement();
    expect(trackPage.getBoundingClientRect().left).toBeGreaterThanOrEqual(0);
    expect(trackPage.getBoundingClientRect().right).toBeLessThanOrEqual(width);
    await record('tracks', (await page.getByRole('heading', { name: 'Product', exact: true }).findElement()).closest('header')!);
    await page.getByRole('button', { name: 'Back to Report' }).click();
    await page.getByRole('button', { name: 'Back to Focus' }).click();
    await page.getByRole('button', { name: 'Open workspace' }).click();
    await record('areas', (await page.getByRole('heading', { name: 'Workspace', exact: true }).findElement()).closest('header')!);
    await page.getByRole('button', { name: 'Product', exact: true }).click();
    await page.getByRole('button', { name: 'New track', exact: true }).click();
    await record('new-track', (await page.getByRole('button', { name: 'Switch area, Product' }).findElement()).closest('header')!);
    await router.navigate({ to: '/track/w1' });
    await router.navigate({ to: '/track/w1', search: { panel: 'cards' } });
    await record('cards', (await page.getByRole('heading', { name: 'Tools', exact: true }).findElement()).closest('header')!);
    await page.getByRole('button', { name: 'Back to Report' }).click();
    await router.navigate({ to: '/track/w1', search: { panel: 'conversations' } });
    await record('conversations', (await page.getByRole('heading', { name: 'Conversations', exact: true }).findElement()).closest('header')!);
    await page.getByRole('button', { name: /Conversation Design review/ }).click();
    await page.getByRole('button', { name: 'Close conversation', exact: true }).findElement();
    await record('chat', (await page.elementLocator(document.querySelector('[data-nc-mobile-chat-panel]')!).getByRole('heading', { name: 'Design review', exact: true }).findElement()).closest('header')!);
    await page.getByRole('button', { name: 'Close conversation' }).click();
    await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(false);
    await page.getByRole('button', { name: 'Back to Report' }).click();
    await expect.poll(() => [...document.querySelectorAll('[data-nc-drawer]')].every((drawer) => !drawer.checkVisibility())).toBe(true);
    await router.navigate({ to: '/workspace' });
    await page.getByRole('button', { name: 'Settings', exact: true }).click();
    await record('settings', (await page.getByRole('heading', { name: 'Settings', exact: true }).findElement()).closest('header')!);
    expect(frames).toHaveLength(8);
  });

  it('uses one shared header on a cold card view instead of stacking the shell and card headers', async () => {
    await page.viewport(390, 844);
    setup('/track/w1?card=terminal');
    const back = await page.getByRole('button', { name: 'Back to Report', exact: true }).findElement();
    const header = back.closest('header')!;
    expect(header.getBoundingClientRect().y).toBe(0);
    const painted = [...document.querySelectorAll('[data-nc-mobile-header]')]
      .filter((element) => element.getBoundingClientRect().height > 0);
    expect(painted).toHaveLength(1);
  });

});


it('hands a revealed Track selector’s focus to the desktop Rename control when resizing', async () => {
  await page.viewport(390, 844);
  setup('/track/w1', AREA.name, undefined, undefined, { longReport: true, viewportRoot: true });
  const current = (await revealTrackSelector()).element();
  (current as HTMLElement).focus();
  expect(document.activeElement).toBe(current);
  await page.viewport(1280, 900);
  const desktop = await page.getByRole('button', { name: 'Rename track', exact: true }).findElement();
  await settlePaint();
  expect(document.activeElement).toBe(desktop);
});

it('hands desktop Rename focus to the document heading at the top of the mobile report', async () => {
  await page.viewport(1280, 900);
  setup('/track/w1');
  const desktop = await page.getByRole('button', { name: 'Rename track', exact: true }).findElement();
  (desktop as HTMLElement).focus();
  await page.viewport(390, 844);
  const title = await page.getByRole('heading', { name: 'Responsive mobile UI', level: 1 }).findElement();
  await settlePaint();
  expect(document.activeElement).toBe(title);
  await page.viewport(1280, 720);
});

it.each(['outside', 'blur'])('does not reclaim title focus after it has moved %s before resizing', async (destination) => {
  await page.viewport(390, 844);
  setup('/track/w1', AREA.name, undefined, undefined, { longReport: true, viewportRoot: true });
  const title = (await revealTrackSelector()).element() as HTMLElement;
  title.focus();
  const source = await page.getByRole('button', { name: 'Source detail', exact: true }).findElement() as HTMLElement;
  if (destination === 'outside') source.focus(); else title.blur();
  await page.viewport(1280, 900);
  await page.getByRole('button', { name: 'Rename track', exact: true }).findElement();
  await settlePaint();
  expect(document.activeElement).toBe(destination === 'outside' ? source : document.body);
  await page.viewport(390, 844);
  await page.getByRole('button', { name: 'Track actions', exact: true }).findElement();
  await settlePaint();
  expect(document.activeElement).toBe(destination === 'outside' ? source : document.body);
  await page.viewport(1280, 720);
});


describe('shared desktop primary header', () => {
  it('aligns New Track with an empty Track and Today and uses the shell selector on mobile', async () => {
    await page.viewport(1440, 900);
    const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
    const project = fixtureTrack('project', { areaId: 'project-area', title: '', leadSessionId: lead.id });
    const { router } = renderDailyFixture({
      initial: '/area/project-area/new',
      reply: (request) => {
        if (request.path === '/api/v1/areas') return ok([
          { ...AREA, id: 'project-area', name: 'Project' }, { ...AREA, id: 'other-area', name: 'Other', sortOrder: 2 },
        ]);
        if (request.path === '/api/v1/tracks/project') return ok(fixtureSnapshot(project, [lead]));
        return undefined;
      },
    });
    const titleRowPosition = () => {
      const box = document.querySelector('main [data-nc-header-rows] > :first-child')!.getBoundingClientRect();
      return { left: box.left, centerline: box.top + box.height / 2 };
    };
    const desktopSelector = page.getByRole('button', { name: 'Area: Project', exact: true });
    await expect.element(desktopSelector).toBeVisible();
    const newTrackPosition = titleRowPosition();

    await router.navigate({ to: '/track/$trackId', params: { trackId: project.id } });
    await expect.element(page.getByRole('region', { name: 'Lead', exact: true })).toBeVisible();
    expect(titleRowPosition()).toEqual(newTrackPosition);

    await router.navigate({ to: '/' });
    await expect.element(page.getByRole('region', { name: 'Today Assistant', exact: true })).toBeVisible();
    expect(titleRowPosition()).toEqual(newTrackPosition);

    await page.viewport(390, 844);
    await router.navigate({ to: '/area/$areaId/new', params: { areaId: 'project-area' } });
    const mobileSelector = await page.getByRole('button', { name: 'Switch area, Project', exact: true }).findElement();
    expect(mobileSelector.closest('[data-nc-workspace-header]')).not.toBeNull();
    await expect.element(desktopSelector).not.toBeInTheDocument();
  });

  it('keeps configured sidebar and page headers fixed during scrolling', async () => {
    await page.viewport(1440, 600);
    setup('/track/w1', AREA.name, () => undefined, 'Responsive mobile UI', { longLists: true, longReport: true, viewportRoot: true });
    await page.getByRole('button', { name: 'Rename track', exact: true }).findElement();
    await expect.poll(() => document.querySelector('[data-nc-report]')).not.toBeNull();
    const rail = document.querySelector<HTMLElement>('nav[aria-label="Workspace"]')!;
    const today = rail.querySelector<HTMLElement>('[aria-label="Go to Today"]')!;
    const collapse = rail.querySelector<HTMLElement>('[aria-label="Collapse sidebar"]')!;
    const header = document.querySelector<HTMLElement>('[data-nc-header-rows]')!;
    const headerRow = header.firstElementChild as HTMLElement;
    const centre = (element: HTMLElement) => { const box = element.getBoundingClientRect(); return box.top + box.height / 2; };
    expect(centre(headerRow) - centre(today)).toBeCloseTo(0, 0);
    expect(centre(headerRow) - centre(collapse)).toBeCloseTo(0, 0);
    const primaryNav = rail.querySelector<HTMLElement>('[data-nc-primary-nav]')!;
    expect(today.parentElement!.getBoundingClientRect().bottom).toBeCloseTo(primaryNav.getBoundingClientRect().top, 0);
    expect(getComputedStyle(header).borderBlockEndColor).toBe('rgba(0, 0, 0, 0)');
    expect(getComputedStyle(today.parentElement!).borderBlockEndWidth).toBe('0px');
    header.parentElement!.setAttribute('data-nc-scrolled', '');
    await settlePaint();
    expect(getComputedStyle(header).borderBlockEndColor).not.toBe('rgba(0, 0, 0, 0)');
    const scroller = document.querySelector<HTMLElement>('[data-nc-track-page]')!;
    expect(scroller.scrollHeight).toBeGreaterThan(scroller.clientHeight);
    const folders = rail.querySelector('[aria-label^="area "]')!.parentElement!;
    expect(folders.scrollHeight).toBeGreaterThan(folders.clientHeight);
    const initial = { header: header.getBoundingClientRect().top, today: today.getBoundingClientRect().top };
    scroller.scrollTop = 600;
    folders.scrollTop = 300;
    await settlePaint();
    expect(scroller.scrollTop).toBeGreaterThan(0);
    expect(folders.scrollTop).toBeGreaterThan(0);
    expect(header.getBoundingClientRect().top).toBeCloseTo(initial.header, 0);
    expect(today.getBoundingClientRect().top).toBeCloseTo(initial.today, 0);
    expect(centre(headerRow) - centre(collapse)).toBeCloseTo(0, 0);
  });

  it('keeps Today and running header aligned when text scales', async () => {
    await page.viewport(1440, 900);
    setup('/track/w1', AREA.name, () => undefined, 'Responsive mobile UI', { viewportRoot: true });
    await waitFor(() => expect(document.querySelector('[data-nc-header-rows]')).not.toBeNull());
    const headerRow = document.querySelector<HTMLElement>('[data-nc-header-rows]')!.firstElementChild as HTMLElement;
    const today = await page.getByRole('button', { name: 'Go to Today', exact: true }).findElement() as HTMLElement;
    const centre = (element: HTMLElement) => { const box = element.getBoundingClientRect(); return box.top + box.height / 2; };
    expect(centre(headerRow)).toBeCloseTo(centre(today), 0);
    // Font scaling preserves the shared top header datum alignment.
    const previousSize = document.documentElement.style.fontSize;
    try {
      document.documentElement.style.fontSize = '20px';
      expect(centre(headerRow)).toBeCloseTo(centre(today), 0);
    } finally { document.documentElement.style.fontSize = previousSize; }
    await page.screenshot({ path: './__screenshots__/header-alignment-fixed.png' });
  });
});
