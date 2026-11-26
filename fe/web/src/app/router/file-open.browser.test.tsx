import type { TrackSearch } from './track-search.ts';
import '../../styles/entry.css';
import { act, cleanup, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';

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

it('opens from Ctrl+P, positions the range, orders recent files, and pops back to the Report in one step', async () => {
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
  await expect.element(dialog().getByRole('option', { name: 'docs/example.txt' })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => router.state.location.search as TrackSearch).toMatchObject({ file: 'docs/example.txt', line: '125-127' });
  const fileHeader = page.getByText('docs/example.txt:125-127', { exact: true });
  await expect.element(fileHeader).toBeVisible();
  expect(fileHeader.element().parentElement).toBe(back().element().parentElement);
  expect(fileHeader.element().getAttribute('title')).toBe('/tmp/docs/example.txt');
  await expect.poll(markedLines).toEqual(['content on line 125', 'content on line 126', 'content on line 127']);
  await expect.poll(highlightIsCentered).toBe(true);

  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('listbox', { name: 'Recent files' }).getByRole('option', { name: 'docs/example.txt' })).toBeVisible();
  await userEvent.keyboard('{Tab}{Shift>}{Tab}{/Shift}');
  await expect.element(query()).toHaveFocus();
  await query().fill('code:12');
  await expect.element(dialog().getByRole('option', { name: 'src/code.ts' })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await expect.poll(markedLines).toEqual(['content on line 12']);
  await back().click();
  await expect.poll(() => router.state.location.search as TrackSearch).not.toHaveProperty('file');
  await expect.element(reportLink).toHaveFocus();

  await userEvent.keyboard('{Control>}p{/Control}');
  await expect.element(dialog().getByRole('option').first()).toHaveTextContent('src/code.ts');
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
  await expect.element(dialog().getByText('No recent files', { exact: true })).toBeVisible();
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
  await expect.element(page.getByText('docs/example.txt:26-28', { exact: true })).toBeVisible();
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
  await expect.element(dialog().getByRole('listbox', { name: 'Recent files' }).getByRole('option', { name: path, exact: true })).toBeVisible();
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
    const recent = dialog().getByRole('listbox', { name: 'Recent files' });
    await expect.element(recent.getByRole('option', { name: canonical, exact: true })).toBeVisible();
    expect(recent.getByRole('option', { name: alias, exact: true }).query()).toBeNull();
    await query().fill('code');
    await dialog().getByRole('option', { name: 'src/code.ts' }).click();
    await expect.element(page.getByText('src/code.ts', { exact: true })).toHaveAttribute('title', '/tmp/src/code.ts');
    expect(header.query()).toBeNull();
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
