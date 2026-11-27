import type { TrackSearch } from './track-search.ts';
import '../../styles/entry.css';
import { act, cleanup, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';

import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { decodeEventFrame } from '../../../../core/events/protocol.ts';
import { EventStream, type EventStreamSink } from '../../systems/events/event-stream.ts';
import { renderCommandFixture } from './composer-command-fixture.tsx';
import { fixtureSnapshot, fixtureTrack, fixtureWidgetReport } from './session-contract-fixture.ts';
import { setupWorkbench, terminalCard } from './terminal-workbench-fixture.tsx';
import { workbenchSockets } from './terminal-workbench-socket-fixture.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); vi.unstubAllGlobals(); });

const dialog = () => page.getByRole('dialog', { name: 'Open a file' });
const query = () => dialog().getByRole('combobox', { name: 'File path or name' });
const composer = () => page.getByRole('combobox', { name: 'Message' });
const back = () => page.getByRole('button', { name: 'Back to track' });
const pixelPng = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2uoAAAAASUVORK5CYII=';

function fileFixture(options: Parameters<typeof renderCommandFixture>[0] = {}) {
  const lines = Array.from({ length: 240 }, (_, i) => `content on line ${i + 1}`).join('\n');
  return renderCommandFixture({ initial: '/track/daily', reportBody: '# File opening\n\n[Read report file](docs/example.txt#L26-L28)\n\n'
    + Array.from({ length: 80 }, (_, index) => `Paragraph ${index} of the report.`).join('\n\n'), ...options, reply(request) {
    const response = options.reply?.(request);
    if (response !== undefined) return response;
    const url = new URL(request.path, 'https://fixture.invalid');
    let body: unknown;
    if (url.pathname.endsWith('/files/search')) {
      const term = url.searchParams.get('q') ?? '';
      body = { paths: ['docs/example.txt', 'src/code.ts', 'draft:notes.txt', 'draft\\notes.txt'].filter(path => path.includes(term)), truncated: false };
    } else if (url.pathname.endsWith('/files')) {
      const path = url.searchParams.get('path');
      if (path === '/tmp') {
        body = { relativePath: '', isDirectory: true, entries: [{ name: 'logs', relativePath: 'logs', isDirectory: true }] };
      } else if (path === '/tmp/logs') {
        body = { relativePath: 'logs', isDirectory: true, entries: [{ name: 'server.log', relativePath: 'logs/server.log', isDirectory: false }] };
      } else if (path === '..') {
        body = { relativePath: '/', isDirectory: true, entries: [{ name: 'home', relativePath: '/home', isDirectory: true }] };
      } else if (path === '../home') {
        body = { relativePath: '/home', isDirectory: true, entries: [{ name: 'notes.txt', relativePath: '/home/notes.txt', isDirectory: false }] };
      } else if (path === 'alias-dir/..') {
        body = { relativePath: '/home', isDirectory: true, entries: [{ name: 'outside.txt', relativePath: '/home/outside.txt', isDirectory: false }] };
      } else {
        const canonical = path === '~/debug.log' ? '/home/operator/debug.log'
          : path === 'alias-dir/../outside.txt' ? '/home/outside.txt'
            : path === '/tmp/logs/server.log' ? 'logs/server.log' : path;
        body = { relativePath: canonical, isDirectory: false, content: lines, truncated: false };
      }
    } else return undefined;
    return { status: 200, statusText: 'OK', body };
  } });
}

async function openComposer() {
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(composer()).toBeVisible();
}

async function typeCommand(text: string) {
  await composer().fill(text);
  await composer().click();
  await userEvent.keyboard('{End}{Enter}');
}

function markedLines() {
  return [...document.querySelectorAll('[data-nc-report-file-viewer] [data-nc-code-highlight-line]')].map(line => line.textContent);
}

function highlightIsCentered() {
  const line = document.querySelector('[data-nc-code-highlight-line]');
  if (line === null) return false;
  const region = line.closest('[data-nc-report-file-viewer]')!.getBoundingClientRect();
  const scroller = line.closest('[data-nc-code-scroller]')!.getBoundingClientRect();
  const rect = line.getBoundingClientRect();
  const middle = (rect.bottom + rect.top) / 2;
  return rect.top >= Math.max(scroller.top, region.top)
    && rect.bottom <= Math.min(scroller.bottom, region.bottom)
    && Math.abs(middle - (region.top + region.bottom) / 2) < 35;
}

function fileOpenEvents() {
  let sink: EventStreamSink | undefined;
  const stream = EventStream.create('ws://fixture.invalid', { start: (_config, _url, active) => { sink = active; }, stop: () => { sink = undefined; } });
  function emit(trackId: string) {
    const decoded = decodeEventFrame({ kind: 'workspace.file_open_requested', trackId, eventId: null, eventVersion: 1,
      payload: { trackId, sessionId: 'daily-planner', path: '/var/log/example.log', line: 86, endLine: 87 } });
    if (decoded.status !== 'ready' || sink === undefined) throw new Error('Event stream was not ready');
    act(() => sink?.frame(decoded.frame));
  }
  return { stream, emit };
}

function searchReply(paths: string[], truncated = false): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body: { paths, truncated } };
}

function activePath() {
  const activeId = query().element().getAttribute('aria-activedescendant');
  return activeId === null ? null : document.getElementById(activeId)?.getAttribute('aria-label');
}

function luminance(context: CanvasRenderingContext2D, ...colors: string[]) {
  context.clearRect(0, 0, 1, 1);
  for (const color of colors) {
    context.fillStyle = color;
    context.fillRect(0, 0, 1, 1);
  }
  const [r, g, b] = Array.from(context.getImageData(0, 0, 1, 1).data, byte => {
    const value = byte / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  });
  return r * 0.2126 + g * 0.7152 + b * 0.0722;
}

it('keeps the keyboard-focused Quick Open field flat and its keycaps legible in both themes', async () => {
  await page.viewport(390, 800);
  fileFixture();
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
  const field = getComputedStyle(query().element());
  expect(field.outlineStyle).toBe('none');
  expect(field.boxShadow).toBe('none');
  expect(field.borderBottomWidth).toBe('0px');

  const panel = dialog().element();
  const keycaps = panel.querySelectorAll('kbd');
  expect(keycaps).toHaveLength(6);
  const context = document.createElement('canvas').getContext('2d')!;
  const previous = document.documentElement.dataset.theme;
  try {
    for (const theme of ['light', 'dark']) {
      document.documentElement.dataset.theme = theme;
      const surface = getComputedStyle(panel).backgroundColor;
      for (const keycap of keycaps) {
        const style = getComputedStyle(keycap);
        expect(style.borderTopWidth).toBe('1px');
        expect(style.borderBottomWidth).toBe('1px');
        expect(style.borderBottomColor).toBe(style.borderTopColor);
        const background = luminance(context, surface, style.backgroundColor);
        const foreground = luminance(context, surface, style.backgroundColor, style.color);
        expect((Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05)).toBeGreaterThanOrEqual(4.5);
      }
    }
  } finally {
    if (previous === undefined) delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = previous;
  }
});

it('keeps filenames legible beside long directories and gives matches 4.5:1 contrast in both themes', async () => {
  await page.viewport(390, 800);
  const filename = 'public-config.ts';
  const path = `src/${'extra-long-directory/'.repeat(10)}${filename}`;
  fileFixture({ reply(request) {
    return request.path.endsWith('/files/search?q=&limit=20000') ? searchReply([path]) : undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('public');
  const row = dialog().getByRole('option', { name: path, exact: true });
  await expect.element(row.getByText('public', { exact: true })).toBeVisible();
  const match = row.element().querySelector('mark')!;
  const name = match.parentElement!;
  const directory = name.nextElementSibling!;
  expect(name.textContent).toBe(filename);
  expect(name.scrollWidth).toBe(name.clientWidth);
  expect(directory.scrollWidth).toBeGreaterThan(directory.clientWidth);
  const context = document.createElement('canvas').getContext('2d')!;
  const previous = document.documentElement.dataset.theme;
  try {
    for (const theme of ['light', 'dark']) {
      document.documentElement.dataset.theme = theme;
      const surface = getComputedStyle(dialog().element()).backgroundColor;
      const active = getComputedStyle(row.element()).backgroundColor;
      for (const text of [match, name, directory]) {
        const foreground = luminance(context, getComputedStyle(text).color);
        for (const background of [luminance(context, surface), luminance(context, surface, active)]) {
          expect((Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05)).toBeGreaterThanOrEqual(4.5);
        }
      }
    }
  } finally {
    if (previous === undefined) delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = previous;
  }
});

it('anchors the input and filters a warm index locally across opens', async () => {
  await page.viewport(1280, 800);
  const indexPath = '/api/v1/tracks/daily/files/search?q=&limit=20000';
  const filename = 'fe/web/src/features/report/quick-open/public.tsx';
  const { requests } = fileFixture({ reply(request) {
    if (request.path === indexPath) return searchReply(['README', 'src/code.ts', filename]);
    return undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('group', { name: 'Files' })).toBeVisible();
  const y = query().element().getBoundingClientRect().top;
  expect(dialog().element().getBoundingClientRect().top).toBeCloseTo(96, 0);
  await query().fill('quick pub');
  const row = dialog().getByRole('option', { name: filename, exact: true });
  await expect.poll(() => [...row.element().querySelectorAll('mark')].map(mark => mark.textContent)).toEqual(['pub', 'quick']);
  expect(row.element().firstElementChild?.children[1]?.textContent).toBe('public.tsx');
  expect(query().element().getBoundingClientRect().top).toBe(y);
  expect(dialog().getByRole('progressbar').query()).toBeNull();

  await query().fill('does not match any file');
  await expect.element(dialog().getByText('No matching files', { exact: true })).toBeVisible();
  expect(query().element().getBoundingClientRect().top).toBe(y);
  await userEvent.keyboard('{Escape}{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option', { name: 'README' })).toBeVisible();
  await query().fill('code');
  await expect.poll(activePath).toBe('src/code.ts');
  expect(requests.filter(request => request.path.includes('/files/search')).map(request => request.path)).toEqual([indexPath]);
});

it('wraps arrow and Ctrl navigation and syncs a single highlight only on pointer movement', async () => {
  await page.viewport(1280, 800);
  fileFixture();
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  const list = dialog().getByRole('listbox', { name: 'Files' });
  const first = list.getByRole('option').first();
  const last = list.getByRole('option').last();
  await expect.element(first).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{ArrowUp}');
  await expect.element(last).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{ArrowDown}');
  await expect.element(first).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(last).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{Control>}n{/Control}');
  await expect.element(first).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{PageDown}');
  await expect.element(last).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{PageUp}');
  await expect.element(first).toHaveAttribute('aria-selected', 'true');

  await last.hover();
  await expect.element(last).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{ArrowDown}');
  await expect.element(first).toHaveAttribute('aria-selected', 'true');
  expect(last.element().matches(':hover')).toBe(true);
  expect(getComputedStyle(last.element()).backgroundColor).toBe('rgba(0, 0, 0, 0)');
  expect(list.element().querySelectorAll('[aria-selected="true"]')).toHaveLength(1);
  await first.hover();
  await last.hover();
  await expect.element(last).toHaveAttribute('aria-selected', 'true');
});

it('keeps local matches visible while a truncated index fetches additional results', async () => {
  await page.viewport(1280, 800);
  let finishSearch: (response: ApiTransportResponse) => void = () => {};
  const pending = new Promise<ApiTransportResponse>(resolve => { finishSearch = resolve; });
  const searchPath = '/api/v1/tracks/daily/files/search?q=quick%20pub&limit=200';
  const { requests } = fileFixture({ reply(request) {
    if (request.path.endsWith('/files/search?q=&limit=20000')) return searchReply(['quick-open/public.tsx', 'README'], true);
    if (request.path === searchPath) return pending;
    return undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option', { name: 'README' })).toBeVisible();
  await query().fill('quick pub');
  const local = dialog().getByRole('option', { name: 'quick-open/public.tsx', exact: true });
  await expect.element(local).toBeVisible();
  await expect.poll(() => requests.some(request => request.path === searchPath)).toBe(true);
  expect(dialog().getByRole('progressbar').query()).toBeNull();
  expect(local.element()).toBeVisible();
  finishSearch(searchReply(['quick-open/public.tsx', 'extra/quick-open/public.tsx']));
  await expect.element(dialog().getByRole('option', { name: 'extra/quick-open/public.tsx' })).toBeVisible();
  await expect.element(local).toBeVisible();
  expect(dialog().getByRole('option', { name: 'quick-open/public.tsx', exact: true }).elements()).toHaveLength(1);
});

it('commits the typed input before replacing results from a warm 20,000-path index', async () => {
  await page.viewport(1280, 800);
  const target = 'src/components/TrackSearch/public.tsx';
  const paths = ['README.md', target, ...Array.from({ length: 19_998 }, (_, i) => `packages/p${i}/Settings/public.tsx`)];
  fileFixture({ reply(request) {
    return request.path.endsWith('/files/search?q=&limit=20000') ? searchReply(paths) : undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  const previous = dialog().getByRole('option', { name: 'README.md' });
  await expect.element(previous).toHaveAttribute('aria-selected', 'true');
  const input = query().element() as HTMLInputElement;
  const commits: { value: string; previousVisible: boolean }[] = [];
  const observer = new MutationObserver(() => commits.push({ value: input.value, previousVisible: previous.query() !== null }));
  observer.observe(input, { attributeFilter: ['aria-activedescendant'] });
  try {
    await query().fill('track search');
    await expect.element(dialog().getByRole('option', { name: target })).toHaveAttribute('aria-selected', 'true');
    expect(commits[0]).toEqual({ value: 'track search', previousVisible: true });
    await expect.element(previous).not.toBeInTheDocument();
  } finally {
    observer.disconnect();
  }
});

it('aborts an obsolete supplemental search and ignores its late response', async () => {
  await page.viewport(1280, 800);
  let finishFirst: (response: ApiTransportResponse) => void = () => {};
  const pending = new Promise<ApiTransportResponse>(resolve => { finishFirst = resolve; });
  const firstPath = '/api/v1/tracks/daily/files/search?q=note&limit=200';
  const { requests } = fileFixture({ reply(request) {
    if (request.path.endsWith('/files/search?q=&limit=20000')) return searchReply(['README'], true);
    if (request.path === firstPath) return pending;
    if (request.path.endsWith('/files/search?q=notes&limit=200')) return searchReply(['docs/notes.ts']);
    return undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option', { name: 'README' })).toBeVisible();
  await query().fill('note');
  await expect.poll(() => requests.some(request => request.path === firstPath)).toBe(true);
  await query().fill('notes');
  await expect.poll(() => requests.find(request => request.path === firstPath)?.signal?.aborted).toBe(true);
  await expect.element(dialog().getByRole('option', { name: 'docs/notes.ts' })).toBeVisible();
  await act(async () => {
    finishFirst(searchReply(['obsolete/notes.ts']));
    await pending;
  });
  expect(dialog().getByRole('option', { name: 'obsolete/notes.ts' }).query()).toBeNull();
});

it('completes folders with Tab, retaining the previous listing until the new folder loads', async () => {
  await page.viewport(1280, 800);
  let finishListing: (response: ApiTransportResponse) => void = () => {};
  const pending = new Promise<ApiTransportResponse>(resolve => { finishListing = resolve; });
  const { requests } = fileFixture({ reply(request) {
    if (request.path.endsWith('/files?path=%2Ftmp%2Flogs')) return pending;
    return undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('/tmp/lg:7-9');
  const folder = dialog().getByRole('option', { name: '/tmp/logs' });
  await expect.element(folder).toBeVisible();
  expect(activePath()).toBeNull();
  await userEvent.keyboard('{Tab}');
  await expect.element(query()).toHaveValue('/tmp/logs/:7-9');
  await expect.element(folder).toBeVisible();
  await query().fill('/tmp/logs/srv:7-9');
  await expect.element(folder).toBeVisible();
  expect(dialog().getByRole('progressbar').query()).toBeNull();
  finishListing({ status: 200, statusText: 'OK', body: {
    relativePath: 'logs', isDirectory: true, entries: [{ name: 'server.log', relativePath: 'logs/server.log', isDirectory: false }],
  } });
  await expect.element(dialog().getByRole('option', { name: '/tmp/logs/server.log' })).toBeVisible();
  expect(activePath()).toBeNull();
  await userEvent.keyboard('{Tab}');
  await expect.element(query()).toHaveValue('/tmp/logs/server.log:7-9');
  expect(requests.filter(request => request.path.includes('/files?path=')).map(request => request.path)).toEqual([
    '/api/v1/tracks/daily/files?path=%2Ftmp', '/api/v1/tracks/daily/files?path=%2Ftmp%2Flogs',
  ]);
});

it('opens the typed home path from a warm listing unless a candidate is explicitly selected', async () => {
  await page.viewport(1280, 800);
  const { router, requests } = fileFixture({ reply(request) {
    if (request.path.endsWith('/files?path=~')) return { status: 200, statusText: 'OK', body: {
      relativePath: '/home/operator', isDirectory: true,
      entries: [{ name: 'notes-old.md', relativePath: '/home/operator/notes-old.md', isDirectory: false }],
    } };
    return undefined;
  } });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('~/');
  const candidate = dialog().getByRole('option', { name: '~/notes-old.md' });
  await expect.element(candidate).toHaveAttribute('aria-selected', 'true');
  await query().fill('~/notes.md:42');
  await expect.element(candidate).toBeVisible();
  expect(activePath()).toBeNull();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '~/notes.md', line: 42 });
  expect(requests.some(request => request.path.endsWith('/files?path=~%2Fnotes-old.md'))).toBe(false);

  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('~/notes.md:42');
  await expect.element(candidate).toBeVisible();
  await userEvent.keyboard('{ArrowUp}');
  await expect.element(candidate).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '~/notes-old.md', line: 42 });
});

it('opens from Ctrl+P, positions the range, and updates recency when returning through file history', async () => {
  await page.viewport(1280, 410);
  const { router } = fileFixture();
  const reportLink = page.getByRole('button', { name: 'Read report file' });
  await expect.element(reportLink).toBeVisible();
  (reportLink.element() as HTMLElement).focus();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
  const close = dialog().getByRole('button', { name: 'Close', exact: true });
  await expect.element(close).toHaveAttribute('type', 'button');
  await close.click();
  await expect.element(dialog()).not.toBeInTheDocument();
  await expect.element(reportLink).toHaveFocus();

  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('example:125-127');
  await expect.element(dialog().getByRole('option', { name: 'docs/example.txt' })).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'docs/example.txt', line: '125-127' });
  const fileHeader = page.getByText('docs/example.txt:125-127', { exact: true });
  await expect.element(fileHeader).toBeVisible();
  expect(fileHeader.element().parentElement).toBe(back().element().parentElement);
  expect(fileHeader.element().getAttribute('title')).toBe('/tmp/docs/example.txt');
  await expect.poll(markedLines).toEqual(['content on line 125', 'content on line 126', 'content on line 127']);
  await expect.poll(highlightIsCentered).toBe(true);

  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('group', { name: 'Recent' }).getByRole('option', { name: 'docs/example.txt' })).toBeVisible();
  await userEvent.keyboard('{Tab}');
  await expect.element(query()).toHaveValue('docs/example.txt');
  await expect.element(query()).toHaveFocus();
  await query().fill('code:12');
  await expect.element(dialog().getByRole('option', { name: 'src/code.ts' })).toHaveAttribute('aria-selected', 'true');
  await userEvent.keyboard('{Enter}');
  await expect.poll(markedLines).toEqual(['content on line 12']);
  await page.getByRole('button', { name: 'Back to docs/example.txt', exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'docs/example.txt', line: '125-127' });
  await back().click();
  await expect.poll(() => router.state.location.search as TrackSearch).not.toHaveProperty('file');
  await expect.element(reportLink).toHaveFocus();

  await userEvent.keyboard('{Control>}p{/Control}');
  const recent = dialog().getByRole('group', { name: 'Recent' }).getByRole('option');
  await expect.element(recent.nth(0)).toHaveAttribute('aria-label', 'docs/example.txt');
  await expect.element(recent.nth(1)).toHaveAttribute('aria-label', 'src/code.ts');
});

it('opens the Daily Track from Today with Ctrl+P and ignores silent AI opens there', async () => {
  await page.viewport(1280, 850);
  const { stream, emit } = fileOpenEvents();
  const { router, requests } = fileFixture({ initial: '/', stream });
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  emit('daily');
  expect(router.history.location.href).toBe('/');
  expect(requests.some(request => request.path.includes('/files?'))).toBe(false);

  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
  await expect.element(dialog().getByRole('group', { name: 'Files' })).toBeVisible();
  await query().fill('example:42');
  await dialog().getByRole('option', { name: 'docs/example.txt' }).click();
  await expect.poll(() => router.history.location.href).toBe('/track/daily?file=docs%2Fexample.txt&line=42');
  await expect.element(page.getByText('docs/example.txt:42', { exact: true })).toBeVisible();
});

it.each([
  { initial: '/track/daily/', areaId: null },
  { initial: '/memory/', areaId: null },
  { initial: '/area/project-area/memory/', areaId: 'project-area' },
])('opens Quick Open and handles silent AI opens in place from $initial', async ({ initial, areaId }) => {
  await page.viewport(1280, 850);
  const { stream, emit } = fileOpenEvents();
  const { router } = fileFixture({ initial, stream, reply(request) {
    if (!request.path.startsWith('/api/v1/memory/') || !request.path.endsWith('/track')) return undefined;
    return { status: 200, statusText: 'OK', body: {
      areaId, scopeKey: areaId === null ? 'global' : `area:${areaId}`, trackId: 'daily', leadSessionId: 'daily-planner',
    } };
  } });
  const link = page.getByRole('button', { name: 'Read report file' });
  await expect.element(link).toBeVisible();
  expect(router.history.location.pathname).toBe(initial);
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  emit('daily');
  await expect.element(page.getByText('/var/log/example.log:86-87', { exact: true })).toBeVisible();
  expect(router.history.location.pathname.replace(/\/$/, '')).toBe(initial.slice(0, -1));
  expect(router.state.location.state.ncFileSilent).toBe(true);
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option', { name: '/var/log/example.log' })).toBeVisible();
});

it.each(['docs/example.txt', 'src/code.ts'])('re-centers a repeated jump to the same range in %s', async path => {
  await page.viewport(1280, 410);
  fileFixture();
  await openComposer();
  const command = `/open ${path}:125-127`;
  await typeCommand(command);
  await expect.poll(highlightIsCentered).toBe(true);
  const scroller = document.querySelector<HTMLElement>('[data-nc-report-file-viewer] [data-nc-code-scroller]')!;
  scroller.scrollTop = 0;
  await expect.poll(highlightIsCentered).toBe(false);

  await typeCommand(command);
  await expect.poll(highlightIsCentered).toBe(true);
  await expect.poll(markedLines).toEqual(['content on line 125', 'content on line 126', 'content on line 127']);
});

it.each([1280, 390])('opens a Report citation with its line range and returns with Escape at %ipx', async width => {
  await page.viewport(width, 800);
  const { router } = fileFixture();
  const link = page.getByRole('button', { name: 'Read report file' });
  await link.click();
  await expect.element(page.getByText(width === 390 ? 'example.txt:26-28' : 'docs/example.txt:26-28', { exact: true })).toBeVisible();
  await expect.poll(markedLines).toEqual(['content on line 26', 'content on line 27', 'content on line 28']);
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => router.state.location.search as TrackSearch).not.toHaveProperty('file');
  await expect.element(link).toHaveFocus();
});

it('shows the requested path when reopening an alias that no longer exists', async () => {
  await page.viewport(1280, 850);
  let aliasExists = true;
  fileFixture({ reply(request) {
    if (request.path !== '/api/v1/tracks/daily/files?path=docs%2Falias.txt') return undefined;
    return aliasExists
      ? { status: 200, statusText: 'OK', body: { relativePath: '/var/log/original.txt', isDirectory: false, content: 'First line\nSecond line', truncated: false } }
      : { status: 404, statusText: 'Not Found', body: { error: 'File not found', code: 'not_found' } };
  } });
  await openComposer();
  await typeCommand('/open docs/alias.txt');
  const canonical = page.getByText('/var/log/original.txt', { exact: true });
  await expect.element(canonical).toBeVisible();
  expect(canonical.element().parentElement).toBe(back().element().parentElement);

  await typeCommand('/open docs/alias.txt:2');
  await expect.element(page.getByText('/var/log/original.txt:2', { exact: true })).toBeVisible();
  await expect.poll(markedLines).toEqual(['Second line']);
  await back().click();
  await expect.element(back()).not.toBeInTheDocument();
  aliasExists = false;

  await typeCommand('/open docs/alias.txt');
  const viewer = page.getByRole('region', { name: 'File docs/alias.txt' });
  await expect.element(viewer.getByRole('button', { name: 'Retry' })).toBeVisible();
  const requested = page.getByText('docs/alias.txt', { exact: true });
  await expect.element(requested).toBeVisible();
  expect(requested.element().parentElement).toBe(back().element().parentElement);
  await expect.element(canonical).not.toBeInTheDocument();
});

it('completes arbitrary folders and accepts a direct home-relative path', async () => {
  await page.viewport(1280, 800);
  const { router } = fileFixture();
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('/tmp/lo');
  await expect.element(dialog().getByRole('option', { name: '/tmp/logs' })).toBeVisible();
  await userEvent.keyboard('{ArrowDown}{Enter}');
  await expect.element(query()).toHaveValue('/tmp/logs/');
  await expect.element(dialog().getByRole('option', { name: '/tmp/logs/server.log' })).toBeVisible();
  await userEvent.keyboard('{ArrowDown}{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '/tmp/logs/server.log' });
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('~/debug.log:42');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '~/debug.log', line: 42 });
  await expect.element(page.getByText('/home/operator/debug.log:42', { exact: true })).toBeVisible();
  expect(router.history.location.search.endsWith('&line=42')).toBe(true);
  await expect.poll(markedLines).toEqual(['content on line 42']);
});

it('dispatches /open with no arguments, a query, or a path and retains /reopen', async () => {
  await page.viewport(1280, 800);
  const { router, requests } = fileFixture({ closed: true });
  await openComposer();
  await typeCommand('/reopen');
  if (!requests.some(request => request.method === 'PATCH')) await userEvent.keyboard('{Enter}');
  await expect.poll(() => requests.filter(request => request.method === 'PATCH').map(request => request.body)).toEqual([{ closed: false }]);

  await typeCommand('/open');
  if (dialog().query() === null) await userEvent.keyboard('{Enter}');
  await expect.element(query()).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  await expect.element(composer()).toHaveFocus();

  await typeCommand('/open example');
  await expect.element(query()).toHaveValue('example');
  await dialog().getByRole('option', { name: 'docs/example.txt' }).click();
  await expect.element(page.getByRole('region', { name: 'File docs/example.txt' })).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  await expect.element(composer()).toHaveFocus();
  await typeCommand('/open docs/example.txt:42-43');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'docs/example.txt', line: '42-43' });
  await expect.poll(markedLines).toEqual(['content on line 42', 'content on line 43']);
  await typeCommand('/open docs/example.txt:42-43');
  await expect.element(page.getByRole('region', { name: 'File docs/example.txt' })).toHaveFocus();
  await typeCommand('/open README:42');
  await expect.element(page.getByText('README:42', { exact: true })).toBeVisible();
  await expect.poll(markedLines).toEqual(['content on line 42']);
  await typeCommand('/open Makefile#L7');
  await expect.element(page.getByText('Makefile:7', { exact: true })).toBeVisible();
  await expect.poll(markedLines).toEqual(['content on line 7']);
  expect(requests.some(request => request.path.endsWith('/messages'))).toBe(false);
});

it('descends from ../ and preserves symlink-sensitive paths through completion and file reads', async () => {
  await page.viewport(1280, 850);
  const { router, requests } = fileFixture();
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('../ho');
  await dialog().getByRole('option', { name: '../home', exact: true }).click();
  await expect.element(query()).toHaveValue('../home/');
  await dialog().getByRole('option', { name: '../home/notes.txt', exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '../home/notes.txt' });

  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('alias-dir/../out:42');
  await dialog().getByRole('option', { name: 'alias-dir/../outside.txt', exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'alias-dir/../outside.txt', line: 42 });
  await expect.element(page.getByText('/home/outside.txt:42', { exact: true })).toBeVisible();
  expect(requests.map(request => request.path)).toContain('/api/v1/tracks/daily/files?path=alias-dir%2F..%2Foutside.txt');
  await expect.poll(markedLines).toEqual(['content on line 42']);
});

it.each(['draft:notes.txt', 'draft\\notes.txt'])('opens the literal search result %s and offers it in Recent Files', async path => {
  await page.viewport(1280, 850);
  const { router } = fileFixture();
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('draft');
  await dialog().getByRole('option', { name: path, exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: path });
  await expect.element(page.getByRole('region', { name: 'File ' + path })).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('group', { name: 'Recent' }).getByRole('option', { name: path, exact: true })).toBeVisible();
});

it('shares file reads between Report previews, embedded images and opened files', async () => {
  await page.viewport(1280, 850);
  const imageSrc = '/api/v1/tracks/daily/files/raw?path=alias.png';
  await commands.mockConversationMedia(imageSrc, pixelPng);
  try {
    const { requests } = fileFixture({ reportBody: '# Report\n\n![Embedded image](alias.png)\n\n[Outside link](alias-dir/../outside.txt)' });
    await expect.element(page.getByRole('img', { name: 'Embedded image' })).toHaveAttribute('src', imageSrc);
    const link = page.getByRole('button', { name: 'Outside link' });
    await link.hover();
    await expect.element(page.getByRole('dialog', { name: 'Preview: Outside link' })).toBeVisible();
    const readPath = '/api/v1/tracks/daily/files?path=alias-dir%2F..%2Foutside.txt';
    await expect.poll(() => requests.some(request => request.path === readPath)).toBe(true);
    await link.click();
    await expect.element(page.getByRole('region', { name: 'File alias-dir/../outside.txt' })).toBeVisible();
    await expect.poll(() => requests.some(request => request.path === readPath)).toBe(true);
  } finally {
    await commands.mockConversationMedia(imageSrc);
  }
});

it('opens an external widget image with the shared file viewer and MRU', async () => {
  await page.viewport(1280, 850);
  const widget = './widgets/links.html';
  const report = fixtureWidgetReport([{ title: 'Files from the widget', use: widget, view: 'links' }]);
  const widgetHtml = `<script>
    addEventListener('message', function openFile({ data }) {
      if (data.type !== 'perga:data') return;
      removeEventListener('message', openFile);
      parent.postMessage({ type: 'perga:intent', kind: 'open_file', path: '/tmp/widget.png' }, '*');
    });
  </script>`;
  const raw = '/api/v1/tracks/daily/files/raw?path=%2Ftmp%2Fwidget.png';
  const read = '/api/v1/tracks/daily/files?path=%2Ftmp%2Fwidget.png';
  await commands.mockConversationMedia(raw, pixelPng);
  try {
    const { requests } = fileFixture({ reply(request) {
      if (request.path === '/api/v1/tracks/daily') return {
        status: 200, statusText: 'OK', body: fixtureSnapshot(fixtureTrack('daily'), [], report),
      };
      if (request.path === '/api/v1/tracks/daily/views') return {
        status: 200, statusText: 'OK', body: { views: { links: {} }, widgets: { [widget]: widgetHtml } },
      };
      if (request.path === read) return {
        status: 200, statusText: 'OK', body: { relativePath: '/tmp/widget.png', isDirectory: false, isBinary: true },
      };
      return undefined;
    } });
    const image = page.getByRole('img', { name: '/tmp/widget.png', exact: true });
    await expect.element(image).toHaveAttribute('src', raw);
    await expect.poll(() => (image.query() as HTMLImageElement | null)?.naturalWidth).toBe(1);
    expect(requests.filter(request => request.path.startsWith(read)).map(request => request.path))
      .toEqual([read]);

    await userEvent.keyboard('{Control>}p{/Control}');
    await dialog().getByRole('option', { name: '/tmp/widget.png', exact: true }).click();
    await expect.element(image).toHaveAttribute('src', raw);
    await expect.poll(() => (image.query() as HTMLImageElement | null)?.naturalWidth).toBe(1);
    expect(requests.filter(request => request.path.startsWith(read)).map(request => request.path))
      .toEqual([read]);
  } finally {
    await commands.mockConversationMedia(raw);
  }
});

it('shows the canonical path of an explicitly opened image in the page header and Recent Files', async () => {
  await page.viewport(1280, 850);
  const alias = '/tmp/docs/folder/../image.png';
  const canonical = 'docs/image.png';
  const read = '/api/v1/tracks/daily/files?path=' + encodeURIComponent(alias);
  const raw = '/api/v1/tracks/daily/files/raw?path=' + encodeURIComponent(canonical);
  await commands.mockConversationMedia(raw, pixelPng);
  try {
    const { requests } = fileFixture({ reply(request) {
      if (request.path !== read) return undefined;
      return { status: 200, statusText: 'OK', body: { relativePath: canonical, isDirectory: false, isBinary: true } };
    } });
    await openComposer();
    await typeCommand('/open ' + alias);
    const header = page.getByText(canonical, { exact: true });
    await expect.element(header).toHaveAttribute('title', '/tmp/docs/image.png');
    expect(header.element().parentElement).toBe(back().element().parentElement);
    expect(requests.filter(request => request.path === read)).toHaveLength(1);
    await userEvent.keyboard('{Control>}p{/Control}');
    const recent = dialog().getByRole('group', { name: 'Recent' });
    await expect.element(recent.getByRole('option', { name: canonical, exact: true })).toBeVisible();
    expect(recent.getByRole('option', { name: alias, exact: true }).query()).toBeNull();
    await query().fill('code');
    await dialog().getByRole('option', { name: 'src/code.ts' }).click();
    await expect.element(page.getByText('src/code.ts', { exact: true })).toHaveAttribute('title', '/tmp/src/code.ts');
    expect(header.query()).toBeNull();
    await page.getByRole('button', { name: 'Back to ' + canonical, exact: true }).click();
    await expect.element(header).toBeVisible();
    await back().click();
    await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  } finally {
    await commands.mockConversationMedia(raw);
  }
});

it.each([
  { search: 'card=detail', width: 1280 },
  { search: 'panel=tasks', width: 390 },
])('returns to Report in one step after Ctrl+P from ?$search', async ({ search, width }) => {
  await page.viewport(width, 850);
  const { router } = fileFixture({ initial: '/track/daily?' + search, reply(request) {
    if (request.path !== '/api/v1/tracks/daily/terminals') return undefined;
    const terminal = terminalCard('detail', { source: 'manual', inWorkbench: false }, 'daily').terminal;
    return { status: 200, statusText: 'OK', body: [terminal] };
  } });
  await expect.poll(() => router.history.location.search).toBe('?' + search);
  // Wait until the originating view has mounted and can receive input.
  if (width === 390) await expect.element(page.getByRole('heading', { name: 'Tasks', exact: true })).toBeVisible();
  else await expect.element(page.getByText('detail', { exact: true }).first()).toBeVisible();
  await userEvent.keyboard('{Control>}p{/Control}');
  await query().fill('example');
  await dialog().getByRole('option', { name: 'docs/example.txt' }).click();
  await expect.element(page.getByRole('region', { name: 'File docs/example.txt' })).toHaveFocus();
  if (width === 390) await userEvent.keyboard('{Escape}');
  else await back().click();
  await expect.poll(() => router.history.location.href).toBe('/track/daily');
  await expect.element(page.getByRole('button', { name: 'Read report file' })).toBeVisible();
  expect(router.history.length).toBe(1);
});

it('skips composing Ctrl+P and cancels browser Print for the regular shortcut', async () => {
  await page.viewport(1280, 850);
  fileFixture();
  const link = page.getByRole('button', { name: 'Read report file' });
  await expect.element(link).toBeVisible();
  const press = (isComposing: boolean) => {
    const event = new KeyboardEvent('keydown', { key: 'p', ctrlKey: true, bubbles: true, cancelable: true, isComposing });
    act(() => { link.element().dispatchEvent(event); });
    return event.defaultPrevented;
  };
  expect(press(true)).toBe(false);
  expect(dialog().query()).toBeNull();
  expect(press(false)).toBe(true);
  await expect.element(query()).toHaveFocus();
});

it('re-enters HTML Source when /open requests another jump to the same line', async () => {
  await page.viewport(1280, 850);
  fileFixture();
  await openComposer();
  await typeCommand('/open report.html:41');
  await expect.poll(markedLines).toEqual(['content on line 41']);
  for (let jump = 0; jump < 2; jump += 1) {
    await page.getByRole('button', { name: 'Page', exact: true }).click();
    await expect.poll(() => document.querySelector('iframe[title="report.html"]')).not.toBeNull();
    await typeCommand('/open report.html:42');
    await expect.element(page.getByRole('button', { name: 'Source', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(markedLines).toEqual(['content on line 42']);
  }
});

it('opens Quick Open with Ctrl+P on the terminal toolbar and leaves Ctrl+P to xterm', async () => {
  await page.viewport(1512, 950);
  const { sockets, Socket } = workbenchSockets();
  vi.stubGlobal('WebSocket', Socket);
  setupWorkbench({ cards: [terminalCard('shell', { source: 'manual', inWorkbench: true })] });
  await page.getByRole('button', { name: /^shell/ }).click();
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => sockets[0].open());
  await expect.element(page.getByRole('textbox', { name: 'Terminal input' })).toHaveFocus();
  await userEvent.keyboard('{Control>}p{/Control}');
  expect(dialog().query()).toBeNull();
  expect(sockets[0].sent.flatMap(frame => frame[0] === 0x01 ? [...frame.subarray(1)] : [])).toContain(16);
  await userEvent.keyboard('{Meta>}p{/Meta}');
  await expect.element(query()).toHaveFocus();
  await userEvent.keyboard('{Escape}');
  const hide = page.getByRole('button', { name: 'Hide terminal' });
  (hide.element() as HTMLElement).focus();
  await expect.element(hide).toHaveFocus();
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
});

it('delivers live file_open for the displayed Track and preserves the focused composer and caret', async () => {
  await page.viewport(1440, 850);
  const { stream, emit } = fileOpenEvents();
  const { router } = fileFixture({ stream });
  await openComposer();
  await composer().fill('draft text');
  await userEvent.keyboard('{Home}{ArrowRight}{ArrowRight}');
  const input = composer().element();
  const caretOffset = document.getSelection()?.anchorOffset;
  emit('other-track');
  expect(router.state.location.search).not.toHaveProperty('file');

  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(query()).toHaveFocus();
  emit('daily');
  expect(router.state.location.search).not.toHaveProperty('file');
  await userEvent.keyboard('{Escape}');
  await expect.element(composer()).toHaveFocus();
  await userEvent.keyboard('{Home}{ArrowRight}{ArrowRight}');
  expect(document.getSelection()?.anchorOffset).toBe(caretOffset);

  emit('daily');
  await expect.element(page.getByText('/var/log/example.log:86-87', { exact: true })).toBeVisible();
  await expect.poll(markedLines).toEqual(['content on line 86', 'content on line 87']);
  expect(router.state.location.state.ncFileSilent).toBe(true);
  expect(document.activeElement).toBe(input);
  expect(document.getSelection()?.anchorOffset).toBe(caretOffset);
  expect(input.textContent).toBe('draft text');
  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option', { name: '/var/log/example.log' })).toBeVisible();
});

it('opens Quick Open in literal directory mode or globally from TrackPage header breadcrumbs with layered Escape at 1280px', async () => {
  await page.viewport(1280, 720);
  const { router } = fileFixture({
    reply(request) {
      if (request.path.endsWith('/files/search?q=&limit=20000')) {
        return searchReply(['docs/example.txt', 'docs/sibling.txt', 'other/d-o-c-s/outside.txt', '!docs/note.md', 'src/code.ts']);
      }
      if (request.path.endsWith('/files?path=.%2Fdocs')) {
        return {
          status: 200,
          statusText: 'OK',
          body: {
            relativePath: 'docs',
            isDirectory: true,
            entries: [
              { name: 'example.txt', relativePath: 'docs/example.txt', isDirectory: false },
              { name: 'sibling.txt', relativePath: 'docs/sibling.txt', isDirectory: false },
            ],
          },
        };
      }
      if (request.path.endsWith('/files?path=.%2F!docs')) {
        return {
          status: 200,
          statusText: 'OK',
          body: {
            relativePath: '!docs',
            isDirectory: true,
            entries: [
              { name: 'note.md', relativePath: '!docs/note.md', isDirectory: false },
              { name: 'sibling.md', relativePath: '!docs/sibling.md', isDirectory: false },
            ],
          },
        };
      }
      return undefined;
    },
  });
  await page.getByRole('button', { name: 'Read report file' }).click();
  await expect.element(page.getByText('docs/example.txt:26-28', { exact: true })).toBeVisible();

  const nav = page.getByRole('navigation', { name: 'File path' });
  const folderDocs = nav.getByRole('button', { name: 'Folder docs' });
  await folderDocs.click();
  await expect.element(query()).toHaveFocus();
  await expect.element(query()).toHaveValue('./docs/');
  await expect.element(dialog().getByText(/Navigate/)).toBeVisible();
  await expect.element(dialog().getByRole('option', { name: './docs/sibling.txt', exact: true })).toBeVisible();
  expect(dialog().getByRole('option', { name: 'other/d-o-c-s/outside.txt', exact: true }).query()).toBeNull();
  expect(dialog().getByRole('option', { name: 'src/code.ts', exact: true }).query()).toBeNull();

  // First Escape closes only Quick Open and restores focus to `Folder docs`; viewer stays open.
  await userEvent.keyboard('{Escape}');
  await expect.element(dialog()).not.toBeInTheDocument();
  await expect.element(folderDocs).toHaveFocus();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'docs/example.txt' });

  // Second Escape from the restored header breadcrumb closes ReportFileViewer back to Report.
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => router.state.location.search as TrackSearch).not.toHaveProperty('file');

  // Reopen and click the filename breadcrumb to open global Quick Open (`Recent` + `Files`) and switch to a `!docs/` folder file.
  await page.getByRole('button', { name: 'Read report file' }).click();
  await nav.getByRole('button', { name: 'Open file switcher for example.txt:26-28' }).click();
  await expect.element(query()).toHaveValue('');
  await expect.element(dialog().getByRole('group', { name: 'Recent' }).getByRole('option', { name: 'docs/example.txt', exact: true })).toBeVisible();
  await expect.element(dialog().getByRole('group', { name: 'Files' }).getByRole('option', { name: '!docs/note.md', exact: true })).toBeVisible();
  await dialog().getByRole('option', { name: '!docs/note.md', exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '!docs/note.md' });

  // Clicking a folder whose name starts with `!` stays in literal directory mode rather than fzf negation.
  await nav.getByRole('button', { name: 'Folder !docs' }).click();
  await expect.element(query()).toHaveValue('./!docs/');
  await expect.element(dialog().getByRole('option', { name: './!docs/sibling.md', exact: true })).toBeVisible();
  expect(dialog().getByRole('option', { name: 'src/code.ts', exact: true }).query()).toBeNull();
  await dialog().getByRole('option', { name: './!docs/sibling.md', exact: true }).click();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: './!docs/sibling.md' });
  await expect.element(page.getByText('!docs/sibling.md', { exact: true })).toBeVisible();
});

it('opens global Quick Open from the filename-only mobile header trigger, hides desktop shortcuts, and layers Escape at 390px', async () => {
  await page.viewport(390, 720);
  const { router } = fileFixture({
    reportBody: '# File opening\n\n[Read special folder file](!docs/note.md)',
    reply(request) {
      if (request.path.endsWith('/files/search?q=&limit=20000')) {
        return searchReply(['!docs/note.md', '!docs/sibling.md', 'src/code.ts']);
      }
      return undefined;
    },
  });
  await page.getByRole('button', { name: 'Read special folder file' }).click();
  const trigger = page.getByRole('button', { name: 'File location: !docs/note.md' });
  await expect.element(trigger).toBeVisible();
  expect(trigger.element().textContent).toBe('note.md');
  expect(trigger.element().getBoundingClientRect().height).toBeGreaterThanOrEqual(48);

  await trigger.click();
  await expect.element(query()).toHaveFocus();
  await expect.element(query()).toHaveValue('');
  await expect.element(dialog().getByRole('group', { name: 'Recent' }).getByRole('option', { name: '!docs/note.md', exact: true })).toBeVisible();
  await expect.element(dialog().getByRole('group', { name: 'Files' }).getByRole('option', { name: 'src/code.ts', exact: true })).toBeVisible();
  await expect.element(dialog().getByText(/Navigate/)).not.toBeVisible();

  // First Escape closes only Quick Open and restores focus to the mobile trigger; viewer stays open.
  await userEvent.keyboard('{Escape}');
  await expect.element(dialog()).not.toBeInTheDocument();
  await expect.element(trigger).toHaveFocus();
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: '!docs/note.md' });

  // Second Escape from the restored mobile trigger closes ReportFileViewer back to Report.
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => router.state.location.search as TrackSearch).not.toHaveProperty('file');
});

it('restores the code scrollport through file Back and recenters a repeated open request', async () => {
  await page.viewport(1280, 850);
  fileFixture();
  await openComposer();
  await typeCommand('/open docs/example.txt:125-127');
  await expect.poll(markedLines).toEqual(['content on line 125', 'content on line 126', 'content on line 127']);
  const scroller = () => document.querySelector<HTMLElement>('[data-nc-report-file-viewer] [data-nc-code-scroller]')!;
  scroller().scrollTop = 40;
  await expect.poll(() => scroller().scrollTop).toBe(40);
  await typeCommand('/open src/code.ts:12');
  await expect.poll(markedLines).toEqual(['content on line 12']);
  await page.getByRole('button', { name: 'Back to docs/example.txt', exact: true }).click();
  await expect.element(page.getByRole('region', { name: 'File docs/example.txt' }).getByLabelText('Code')).toBeVisible();
  await expect.poll(() => scroller()?.scrollTop).toBe(40);
  await typeCommand('/open docs/example.txt:125-127');
  await expect.poll(highlightIsCentered).toBe(true);
  expect(scroller().scrollTop).toBeGreaterThan(40);
});

it.each(['md', 'html'])('restores the selected Source view and code scroll offset for %s through file Back', async extension => {
  await page.viewport(1280, 850);
  fileFixture();
  await openComposer();
  const path = `docs/notes.${extension}`;
  await typeCommand(`/open ${path}${extension === 'md' ? ':125' : ''}`);
  const file = () => page.getByRole('region', { name: `File ${path}`, exact: true });
  await file().getByRole('button', { name: 'Source', exact: true }).click();
  await expect.element(file().getByLabelText('Code')).toBeVisible();
  const scroller = () => file().element().querySelector<HTMLElement>('[data-nc-code-scroller]')!;
  await expect.poll(() => scroller().scrollHeight - scroller().clientHeight).toBeGreaterThan(400);
  scroller().scrollTop = 320;
  await expect.poll(() => scroller().scrollTop).toBe(320);
  await typeCommand('/open src/code.ts:12');
  await expect.poll(markedLines).toEqual(['content on line 12']);
  await page.getByRole('button', { name: `Back to ${path}`, exact: true }).click();
  await expect.element(file().getByRole('button', { name: 'Source', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.element(file().getByLabelText('Code')).toBeVisible();
  await expect.poll(() => scroller().scrollTop).toBe(320);
});
