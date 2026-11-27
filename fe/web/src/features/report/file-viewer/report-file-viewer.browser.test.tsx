import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { EditorView } from '@codemirror/view';
import { language } from '@codemirror/language';
import { page as browserPage } from 'vitest/browser';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';

import type { WorkspaceFilePort } from '../../../../../core/domain/fs.ts';
import { ReportFileViewer } from './public.tsx';

afterEach(() => { cleanup(); document.getSelection()?.removeAllRanges(); vi.restoreAllMocks(); document.body.replaceChildren(); });

function files(path: string, text: string): WorkspaceFilePort {
  return {
    readFile: () => Promise.resolve({ path, size: text.length, text, truncated: false }),
    rawUrl: (value) => `/raw?path=${encodeURIComponent(value)}`,
  };
}

describe('ReportFileViewer', () => {
  it('renders source as a centered Report document, without card or directory chrome', async () => {
    await browserPage.viewport(1200, 800);
    const onFileOpened = vi.fn();
    render(
      <div
        data-testid="frame"
        style={{
          position: 'relative', inlineSize: 1000, blockSize: 600,
          ['--document-measure' as string]: '568px',
        }}
      >
        <ReportFileViewer
          path="src/main.ts"
          files={files('src/main.ts', 'export const x = 1;')}
          fileRoot="/repo"
          wide
          onClose={vi.fn()}
          onFileOpened={onFileOpened}
        />
      </div>,
    );

    await waitFor(() => { expect(onFileOpened).toHaveBeenCalledWith('src/main.ts'); });
    const frame = document.querySelector<HTMLElement>('[data-testid="frame"]')!;
    const layer = document.querySelector<HTMLElement>('[data-nc-report-file-viewer]')!;
    const source = layer.querySelector<HTMLElement>('[data-nc-report-file-source]')!;
    expect(Math.round(layer.getBoundingClientRect().width))
      .toBe(Math.round(frame.getBoundingClientRect().width));
    expect(source.textContent).toContain('export const x = 1;');
    expect(source.getBoundingClientRect().width).toBeGreaterThan(800);
    const frameRect = frame.getBoundingClientRect();
    const sourceRect = source.getBoundingClientRect();
    expect(Math.abs(
      (sourceRect.left - frameRect.left) - (frameRect.right - sourceRect.right),
    )).toBeLessThanOrEqual(1);
    expect(document.activeElement).toBe(layer);
    expect(layer.querySelector('[data-nc-fs-viewer]')).toBeNull();
    expect(layer.textContent).not.toContain('Diff');
  });

  it('renders Markdown through the same prose pipeline as the Report', async () => {
    render(<div
      data-testid="markdown-frame"
      style={{
        position: 'relative', inlineSize: 1000, blockSize: 600,
        ['--document-measure' as string]: '568px',
        ['--document-start' as string]: '80px',
      }}
    >
      <ReportFileViewer
        path="README.md"
        files={files('README.md', '# README title\n\nA **rendered** paragraph.')}
        fileRoot="/repo"
        wide
        onClose={vi.fn()}
      />
    </div>);

    await waitFor(() => {
      expect(document.querySelector('[data-nc-report]')).not.toBeNull();
    });
    expect(document.querySelector('[data-nc-report-file-source]')).toBeNull();
    expect(document.querySelector('[data-nc-report]')?.textContent)
      .toContain('README title');
    expect(document.querySelector('[data-nc-report]')?.textContent)
      .toContain('A rendered paragraph.');
    const frame = document.querySelector<HTMLElement>('[data-testid="markdown-frame"]')!;
    const heading = document.querySelector<HTMLElement>('[data-nc-report] h2')!;
    expect(Math.round(heading.getBoundingClientRect().width)).toBe(568);
    const frameRect = frame.getBoundingClientRect();
    const headingRect = heading.getBoundingClientRect();
    expect(Math.round(headingRect.left - frameRect.left)).toBe(80);
  });

  it('returns to the Report on Escape', () => {
    const onClose = vi.fn();
    render(<div style={{ position: 'relative', blockSize: 400 }}>
      <ReportFileViewer
        path="README.md"
        files={files('README.md', '# README')}
        fileRoot="/repo"
        wide={false}
        onClose={onClose}
      />
    </div>);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('keeps the Report document within a compact viewport', async () => {
    await browserPage.viewport(500, 700);
    render(<div style={{ position: 'relative', inlineSize: 460, blockSize: 600 }}>
      <ReportFileViewer
        path="src/main.ts"
        files={files('src/main.ts', 'const compact = true;')}
        fileRoot="/repo"
        wide
        onClose={vi.fn()}
      />
    </div>);

    await waitFor(() => {
      expect(document.querySelector('[data-nc-report-file-source]')).not.toBeNull();
    });
    const layer = document.querySelector<HTMLElement>('[data-nc-report-file-viewer]')!;
    expect(layer.scrollWidth).toBeLessThanOrEqual(layer.clientWidth);
  });
});

it('shares filename highlighting and original copying in the standalone file reader', async () => {
  const original = 'fn main() {}\r\n';
  const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue();
  const close = vi.fn();
  render(<div style={{ position: 'relative', inlineSize: 900, blockSize: 600 }}>
    <ReportFileViewer path="src/main.rs" files={files('src/main.rs', original)} fileRoot="/repo" wide onClose={close} />
  </div>);
  const content = await screen.findByRole('textbox', { name: 'Code' });
  const view = EditorView.findFromDOM(content);
  if (view === null) throw new Error('Standalone code view missing');
  await waitFor(() => { expect(view.state.facet(language)?.name).toBe('rust'); });
  screen.getByRole('button', { name: 'Copy code' }).click();
  expect(write).toHaveBeenCalledExactlyOnceWith(original);
  content.focus();
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  expect(close).toHaveBeenCalledOnce();
});

it('opens workspace HTML in the file viewer sandbox and can show its source', async () => {
  render(<ReportFileViewer path="report.html" files={files('report.html', '<p>Example page</p>')}
    fileRoot="/repo" wide onClose={vi.fn()} />);
  const frame = await waitFor(() => {
    const iframe = document.querySelector('iframe[title="report.html"]');
    expect(iframe).not.toBeNull();
    return iframe!;
  });
  expect(frame.getAttribute('src')).toBe('/raw?path=report.html');
  expect(frame.getAttribute('sandbox')).toBe('allow-scripts');
  expect(frame.getAttribute('referrerpolicy')).toBe('no-referrer');
  screen.getByRole('button', { name: /^Source$/ }).click();
  expect(await screen.findByRole('textbox', { name: 'Code' })).toBeTruthy();
  expect(document.querySelector('iframe[title="report.html"]')).toBeNull();
});

it('renders Markdown with a line citation as formatted prose scrolled to the target, with a Source toggle', async () => {
  await browserPage.viewport(1200, 720);
  const filler = Array.from({ length: 18 }, (_, i) => `Opening paragraph ${i + 1}.`).join('\n\n');
  const essay = [
    '# Deleting AI Slop',
    '',
    filler,
    '',
    '1. First lesson.',
    '',
    '2. Second lesson.',
    '',
    '3. **Parallel worktrees** blew up target/.',
    '',
    '   ```toml',
    '   [profile.dev]',
    '   debug = 1',
    '   ```',
    '',
    '4. **Bad code** became too expensive to delete.',
    '',
    '## Picking a backend',
    '',
    'Closing summary.',
  ].join('\n');
  const targetLine = essay.split('\n').findIndex(line => line.startsWith('3. **Parallel worktrees**')) + 1;

  render(
    <div style={{ position: 'relative', inlineSize: 960, blockSize: 420, ['--document-measure' as string]: '640px', ['--document-start' as string]: '64px' }}>
      <ReportFileViewer
        path="docs/essays/01-rust-to-csharp.md"
        line={targetLine}
        openRequest={1}
        files={files('docs/essays/01-rust-to-csharp.md', essay)}
        fileRoot="/repo"
        wide
        onClose={vi.fn()}
      />
    </div>,
  );

  // Defaults to rendered Markdown prose, highlighting the cited list item and scrolling it into view.
  await waitFor(() => {
    expect(document.querySelector('[data-nc-report]')).not.toBeNull();
  });
  expect(document.querySelector('[data-nc-report-file-source]')).toBeNull();
  const previewButton = screen.getByRole('button', { name: /^Preview$/ });
  const sourceButton = screen.getByRole('button', { name: /^Source$/ });
  expect(previewButton.getAttribute('aria-pressed')).toBe('true');
  expect(sourceButton.getAttribute('aria-pressed')).toBe('false');

  const highlighted = document.querySelector<HTMLElement>('[data-nc-markdown-highlight]')!;
  expect(highlighted).not.toBeNull();
  expect(highlighted.tagName).toBe('LI');
  expect(highlighted.textContent).toContain('Parallel worktrees');
  expect(highlighted.textContent).not.toContain('Bad code');

  const layer = document.querySelector<HTMLElement>('[data-nc-report-file-viewer]')!;
  await waitFor(() => {
    const layerRect = layer.getBoundingClientRect();
    const targetRect = highlighted.getBoundingClientRect();
    expect(targetRect.top).toBeGreaterThanOrEqual(layerRect.top);
    expect(targetRect.top).toBeLessThan(layerRect.bottom);
  });

  // Switching to Source shows wrapped CodeMirror highlighting the exact source line.
  sourceButton.click();
  expect(await screen.findByRole('textbox', { name: 'Code' })).toBeTruthy();
  expect(document.querySelector('[data-nc-report]')).toBeNull();
  await waitFor(() => {
    const marked = document.querySelector<HTMLElement>('[data-nc-code-highlight-line]');
    expect(marked?.textContent).toContain('Parallel worktrees');
  });

  // Switching back to Preview restores rendered Markdown cleanly.
  screen.getByRole('button', { name: /^Preview$/ }).click();
  await waitFor(() => {
    expect(document.querySelector('[data-nc-report]')).not.toBeNull();
  });
  expect(document.querySelector('[data-nc-report-file-source]')).toBeNull();
});

it.each([false, true])('keeps reading position across parent renders and accepts new line requests (restored=%s)', async restored => {
  const port = files('notes.md', Array.from({ length: 160 }, (_, index) => `Paragraph ${index + 1}\n`).join('\n'));
  const onClose = vi.fn();
  const view = (restoreScroll: boolean, openRequest = 1, line = 120) => (
    <div style={{ position: 'relative', width: 800, height: 400 }}>
      <ReportFileViewer path="notes.md" files={port} fileRoot="/repo" wide onClose={onClose}
        restoreScroll={restoreScroll} line={line} openRequest={openRequest} />
    </div>
  );
  const { rerender } = render(view(restored));
  await waitFor(() => expect(document.querySelector('[data-nc-markdown-highlight]')).not.toBeNull());
  const layer = document.querySelector<HTMLElement>('[data-nc-report-file-viewer]')!;
  if (restored) expect(layer.scrollTop).toBe(0);
  layer.scrollTop = 600;
  // Parent rerenders retain the entry's initial restoration decision.
  rerender(view(restored));
  expect(layer.scrollTop).toBe(600);
  // A new open request gets a fresh history entry, so highlighting can center.
  rerender(view(false, 2));
  expect(layer.scrollTop).toBeGreaterThan(600);
  const centered = layer.scrollTop;
  layer.scrollTop = 600;
  rerender(view(false, 3));
  expect(layer.scrollTop).toBe(centered);
  rerender(view(false, 3, 10));
  expect(layer.scrollTop).toBeLessThan(centered);
});
