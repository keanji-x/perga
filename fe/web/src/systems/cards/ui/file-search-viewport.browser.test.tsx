import '../../../styles/entry.css';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { EditorView } from '@codemirror/view';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { ActivePanelHost, createCardHost, createCardRegistry, registerAvailableBuiltinCards, type CardHostItem } from '../public.ts';

afterEach(() => { cleanup(); document.body.replaceChildren(); });

async function paint() {
  await act(async () => { await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))); });
}

function assertUsable(view: EditorView, board: HTMLElement) {
  const clip = board.getBoundingClientRect();
  const editor = view.scrollDOM.getBoundingClientRect();
  for (const control of [screen.getByRole('searchbox', { name: 'Search in file' }),
    screen.getByRole('button', { name: 'Next match' }), screen.getByRole('button', { name: 'Previous match' }),
    screen.getByRole('button', { name: 'Close search' })]) {
    const box = control.getBoundingClientRect();
    expect(box.top).toBeGreaterThanOrEqual(clip.top);
    expect(box.left).toBeGreaterThanOrEqual(clip.left);
    expect(box.right).toBeLessThanOrEqual(clip.right);
    expect(box.bottom).toBeLessThanOrEqual(clip.bottom);
    expect(control.contains(document.elementFromPoint((box.left + box.right) / 2, (box.top + box.bottom) / 2))).toBe(true);
  }
  const selected = view.state.selection.main;
  const start = view.domAtPos(selected.from), end = view.domAtPos(selected.to);
  const range = document.createRange();
  range.setStart(start.node, start.offset); range.setEnd(end.node, end.offset);
  expect(range.toString()).toBe('needle');
  const fragments = [...range.getClientRects()].filter(box => box.width > 0 && box.height > 0);
  expect(fragments.length).toBeGreaterThan(0);
  for (const box of fragments) {
    expect(box.top).toBeGreaterThanOrEqual(Math.max(clip.top, editor.top));
    expect(box.left).toBeGreaterThanOrEqual(Math.max(clip.left, editor.left));
    expect(box.right).toBeLessThanOrEqual(Math.min(clip.right, editor.right));
    expect(box.bottom).toBeLessThanOrEqual(Math.min(clip.bottom, editor.bottom));
    expect(view.contentDOM.contains(document.elementFromPoint((box.left + box.right) / 2, (box.top + box.bottom) / 2))).toBe(true);
  }
}

it.each([1400, 420])('keeps file search usable across selection, hide and viewport resize (%i)', async (width) => {
  await page.viewport(width, 900);
  const registry = createCardRegistry(); registerAvailableBuiltinCards(registry);
  const text = Array.from({ length: 8000 }, (_, index) => `row ${index}: needle`).join('\n');
  const host = createCardHost(registry, { files: {
    listDirectory: path => Promise.resolve({ path, parent: '/', entries: [{ name: 'large.txt', is_dir: false }] }),
    readFile: path => Promise.resolve({ path, size: text.length, text, truncated: false }),
    gitStatus: () => Promise.resolve({ repo_root: '/repo', files: [] }),
    gitDiff: () => Promise.reject(new Error('No diff expected')), rawUrl: path => path,
  } });
  const card = registry.resolve({ id: 'file', kind: 'file-viewer', payload: { path: '/repo' } });
  if (card === null) throw new Error('Real file card missing');
  const items: readonly CardHostItem[] = [{ card, title: 'File', originalIndex: 0, activity: null, notice: null }];
  const scene = (visible: boolean, active = 'file', cards = items) => <div style={{ display: 'flex', width: '100%', height: 'calc(100vh - 132px)' }}>
    <ActivePanelHost host={host} items={cards} activeCardId={active} visible={visible} />
  </div>;
  const rendered = render(scene(true));
  await page.getByRole('button', { name: /large\.txt/ }).click();
  const content = await screen.findByRole('textbox', { name: 'File code' });
  const view = EditorView.findFromDOM(content);
  if (view === null) throw new Error('Real editor missing');
  const cell = document.querySelector<HTMLElement>('[data-nc-card-id="file"]')!;
  await paint();
  expect(document.querySelector('[data-nc-file-card]')!.getBoundingClientRect().height).toBe(cell.getBoundingClientRect().height);
  await page.viewport(width, 500);
  await paint();
  const board = document.querySelector<HTMLElement>('[data-nc-active-panel-host]')!;
  expect(board.clientHeight).toBe(368);
  content.focus();
  await userEvent.keyboard('{Control>}{Home}{/Control}/');
  const search = screen.getByRole('searchbox', { name: 'Search in file' });
  await page.getByRole('searchbox', { name: 'Search in file' }).fill('needle');
  await waitFor(() => { expect(screen.getByText('1/8000')).toBeDefined(); });
  await paint(); assertUsable(view, board);
  for (const [keys, count] of [['{Enter}', '2/8000'], ['{Shift>}{Enter}{/Shift}', '1/8000'],
    ['{Shift>}{Enter}{/Shift}', '8000/8000'], ['{Enter}', '1/8000']] as const) {
    await userEvent.keyboard(keys);
    await waitFor(() => { expect(screen.getByText(count)).toBeDefined(); });
    await paint(); assertUsable(view, board);
    expect(document.activeElement).toBe(search);
  }
  expect(view.state.doc.toString()).toBe(text);
  const sibling = registry.resolve({ id: 'terminal', kind: 'terminal', payload: {} });
  if (sibling === null) throw new Error('Real terminal card missing');
  const withSibling: readonly CardHostItem[] = [...items, { card: sibling, title: 'Terminal', originalIndex: 1, activity: null, notice: null }];
  rendered.rerender(scene(true, 'terminal', withSibling));
  await paint();
  const terminal = document.querySelector<HTMLElement>('[data-nc-card-id="terminal"]')!;
  const terminalSurface = terminal.querySelector('[data-nc-terminal-card]')!;
  expect(terminalSurface.getBoundingClientRect().height).toBe(terminal.getBoundingClientRect().height);
  const clip = board.getBoundingClientRect();
  expect(terminalSurface.getBoundingClientRect().top).toBeGreaterThanOrEqual(clip.top - 1);
  expect(terminalSurface.getBoundingClientRect().top).toBeLessThan(clip.bottom);
  rendered.rerender(scene(true, 'file', withSibling));
  await paint(); assertUsable(view, board);
  rendered.rerender(scene(false, 'file', withSibling));
  await page.viewport(width, 600); await paint();
  rendered.rerender(scene(true, 'file', withSibling));
  await paint(); assertUsable(view, board);
  expect(cell.getBoundingClientRect().height).toBe(board.clientHeight);
  await page.viewport(width, 900); await paint();
  expect(cell.getBoundingClientRect().height).toBe(board.clientHeight);
});
