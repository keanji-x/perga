import '../../styles/entry.css';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';

import type { WorkspaceFilePort } from '../../../../core/domain/fs.ts';
import { ReportDocument } from '../../features/report/document/public.tsx';
import { createCardHost, createCardRegistry, registerAvailableBuiltinCards, ActivePanelHost } from '../../systems/cards/public.ts';
import { Dialog } from '../../ui/dialog/public.tsx';
import { Drawer } from '../../ui/drawer/public.tsx';
import { PanelCard } from '../../ui/panel-card/public.tsx';

let previousTheme: string | null;
beforeEach(async () => { previousTheme = document.documentElement.getAttribute('data-theme'); await page.viewport(1400, 900); });
afterEach(() => {
  cleanup();
  if (previousTheme === null) document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', previousTheme);
});

function reference(theme: 'light' | 'dark') {
  document.documentElement.setAttribute('data-theme', theme);
  render(<div style={{ position: 'relative', width: 1200, height: 700, containerType: 'inline-size' }}>
    <Drawer open title="Shared card reference" onClose={() => {}}>Conversation</Drawer>
  </div>);
  const style = getComputedStyle(screen.getByRole('complementary', { name: 'Shared card reference' }));
  expect(style.borderRadius).toBe('16px');
  expect(style.boxShadow).not.toBe('none');
  return style;
}

function expectFrame(element: Element, expected: CSSStyleDeclaration, flat = false) {
  const actual = getComputedStyle(element);
  expect(actual.borderRadius).toBe(expected.borderRadius);
  expect(actual.boxShadow).toBe(flat ? 'none' : expected.boxShadow);
  expect(actual.borderWidth).toBe(expected.borderWidth);
  expect(actual.backgroundColor).toBe(expected.backgroundColor);
  expect(actual.overflow).toBe('hidden');
}

for (const theme of ['light', 'dark'] as const) {
  it(`shares the conversation frame with registered Terminal and File cards (${theme})`, async () => {
    const expected = reference(theme);
    const registry = createCardRegistry();
    registerAvailableBuiltinCards(registry);
    const cards = [
      registry.resolve({ id: 'terminal-frame', kind: 'terminal', payload: {} }),
      registry.resolve({ id: 'file-frame', kind: 'file-viewer', payload: { path: '/repo' } }),
    ];
    if (cards.some(card => card === null)) throw new Error('Missing builtin card');
    const items = cards.map((card, originalIndex) => {
      if (card === null) throw new Error('Missing builtin card');
      return { card, title: card.id, originalIndex, activity: null, notice: null };
    });
    const host = createCardHost(registry);
    const scene = (id: string) => <div style={{ display: 'flex', width: 1200, height: 700 }}><ActivePanelHost host={host} items={items} activeCardId={id} visible /></div>;
    const view = render(scene('terminal-frame'));
    await waitFor(() => expect(document.querySelector('[data-nc-terminal-card]')).not.toBeNull());
    expectFrame(document.querySelector('[data-nc-terminal-card]')!, expected);
    view.rerender(scene('file-frame'));
    await waitFor(() => expect(document.querySelector('[data-nc-file-card]')).not.toBeNull());
    expectFrame(document.querySelector('[data-nc-file-card]')!, expected);
  });

  it(`shares the conversation frame with real file link previews (${theme})`, async () => {
    const expected = reference(theme);
    const files: WorkspaceFilePort = {
      readFile: path => Promise.resolve({ path, text: '# File contents', size: 15, truncated: false }),
      rawUrl: path => `/workspace/${path}`,
    };
    const report = { summary: '', body: '[Notes](./notes.md)', blocks: null };
    render(<ReportDocument report={report} empty={null} fileRoot="/repo"
      linkPreview={{ files, trackId: 'frame-test', report }} onOpenFileLink={() => {}} />);
    fireEvent.keyDown(screen.getByRole('button', { name: 'Notes' }), { key: 'ArrowDown' });
    const preview = await screen.findByRole('dialog', { name: 'Preview: Notes' });
    await screen.findByRole('heading', { name: 'File contents' });
    expectFrame(preview, expected);
  });

  it(`shares the conversation frame with modal card surfaces (${theme})`, () => {
    const expected = reference(theme);
    render(<Dialog purpose="info" origin={{ kind: 'surface-center' }} open title="Files" onClose={() => {}}><button type="button">Choose file</button></Dialog>);
    expectFrame(screen.getByRole('dialog', { name: 'Files' }), expected);
  });

  it(`uses the shared card frame with floating elevation for context panels (${theme})`, () => {
    const expected = reference(theme);
    render(<section aria-label="Context panel"><PanelCard>Context</PanelCard></section>);
    const panel = screen.getByRole('region', { name: 'Context panel' }).firstElementChild!;
    const actual = getComputedStyle(panel);
    expect(actual.borderRadius).toBe(expected.borderRadius);
    expect(actual.boxShadow).not.toBe('none');
    expect(actual.borderWidth).toBe(expected.borderWidth);
    expect(actual.backgroundColor).toBe(expected.backgroundColor);
    expect(actual.overflow).toBe('hidden');
  });
}
