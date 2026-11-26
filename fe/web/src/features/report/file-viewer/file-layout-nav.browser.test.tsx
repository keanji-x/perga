import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';

import { FileLayoutNav } from './public.tsx';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

describe('FileLayoutNav', () => {
  it('renders desktop breadcrumbs with exact textContent, tooltip, parent alignment, and Quick Open queries', async () => {
    await page.viewport(1200, 800);
    const onOpenQuickOpen = vi.fn();

    render(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 16px' }}>
        <button type="button" aria-label="Back to track">Back</button>
        <FileLayoutNav
          path="docs/rpc/02_rpc_full_pipeline.md"
          fullPath="/repo/docs/rpc/02_rpc_full_pipeline.md"
          lineSuffix=":125-127"
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const nav = page.getByRole('navigation', { name: 'File path' });
    await expect.element(nav).toBeVisible();
    expect(nav.element().textContent).toBe('docs/rpc/02_rpc_full_pipeline.md:125-127');
    expect(nav.element().getAttribute('title')).toBe('/repo/docs/rpc/02_rpc_full_pipeline.md');
    expect(nav.element().parentElement).toBe(page.getByRole('button', { name: 'Back to track' }).element().parentElement);
    expect(nav.getByRole('button', { name: 'Intermediate folders' }).query()).toBeNull();

    await nav.getByRole('button', { name: 'Folder docs' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('./docs/');

    await nav.getByRole('button', { name: 'Folder rpc' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('./docs/rpc/');

    await nav.getByRole('button', { name: 'Open file switcher for 02_rpc_full_pipeline.md:125-127' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('');
  });

  it('collapses only intermediate directories when deeper than three folders and routes ellipsis to the deepest collapsed folder', async () => {
    await page.viewport(1200, 800);
    const onOpenQuickOpen = vi.fn();

    render(
      <FileLayoutNav
        path="docs/architecture/node/rpc/02_pipeline.md"
        fullPath="/repo/docs/architecture/node/rpc/02_pipeline.md"
        fileRoot="/repo"
        onOpenQuickOpen={onOpenQuickOpen}
      />,
    );

    const nav = page.getByRole('navigation', { name: 'File path' });
    await expect.element(nav.getByRole('button', { name: 'Folder docs' })).toBeVisible();
    await expect.element(nav.getByRole('button', { name: 'Intermediate folders' })).toBeVisible();
    await expect.element(nav.getByRole('button', { name: 'Folder node' })).toBeVisible();
    await expect.element(nav.getByRole('button', { name: 'Folder rpc' })).toBeVisible();
    expect(nav.getByRole('button', { name: 'Folder architecture' }).query()).toBeNull();

    await nav.getByRole('button', { name: 'Intermediate folders' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('./docs/architecture/');
  });

  it('preserves out-of-workspace absolute paths, literal ./~ paths, and static fallback without callback', async () => {
    await page.viewport(1200, 800);
    const onOpenQuickOpen = vi.fn();

    const { rerender } = render(
      <FileLayoutNav
        path="/var/log/example.log"
        fullPath="/var/log/example.log"
        fileRoot="/repo"
        onOpenQuickOpen={onOpenQuickOpen}
      />,
    );

    const nav = page.getByRole('navigation', { name: 'File path' });
    expect(nav.element().textContent).toBe('/var/log/example.log');
    await nav.getByRole('button', { name: 'Folder var' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('/var/');
    await nav.getByRole('button', { name: 'Folder log' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('/var/log/');

    rerender(
      <FileLayoutNav
        path="/repo/~/picture.png"
        fullPath="/repo/~/picture.png"
        fileRoot="/repo"
        onOpenQuickOpen={onOpenQuickOpen}
      />,
    );

    expect(nav.element().textContent).toBe('./~/picture.png');
    await nav.getByRole('button', { name: 'Folder ~' }).click();
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('./~/');

    rerender(
      <FileLayoutNav
        path="docs/example.txt"
        fullPath="/repo/docs/example.txt"
        lineSuffix=":42"
        fileRoot="/repo"
      />,
    );

    expect(page.getByRole('navigation', { name: 'File path' }).query()).toBeNull();
    const staticPath = page.getByText('docs/example.txt:42', { exact: true });
    await expect.element(staticPath).toHaveAttribute('title', '/repo/docs/example.txt');
  });

  it('renders a single 48px filename-only mobile trigger at 390px that preserves extensions without overflow and opens global Quick Open', async () => {
    await page.viewport(390, 600);
    const onOpenQuickOpen = vi.fn();
    const deepCjkPath = '文档/区块架构/超级长的中文节点网络拓扑目录/02_区块链节点全链路架构梳理文档.md';

    const { rerender } = render(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', inlineSize: 390, boxSizing: 'border-box' }}>
        <button type="button" aria-label="Back to track" style={{ flex: 'none' }}>Back</button>
        <FileLayoutNav
          path={deepCjkPath}
          fullPath={`/repo/${deepCjkPath}`}
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const cjkTrigger = page.getByRole('button', { name: `File location: ${deepCjkPath}` });
    await expect.element(cjkTrigger).toBeVisible();
    expect(cjkTrigger.element().textContent).toBe('02_区块链节点全链路架构梳理文档.md');
    expect(cjkTrigger.element().getAttribute('title')).toBe(`/repo/${deepCjkPath}`);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);
    const cjkRect = cjkTrigger.element().getBoundingClientRect();
    expect(cjkRect.right).toBeLessThanOrEqual(390);
    expect(cjkRect.height).toBeGreaterThanOrEqual(48);

    await userEvent.click(cjkTrigger.element());
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('');

    const shortCjkPath = '文档/区块架构/超级长的中文节点网络拓扑目录/中文名字.md';
    rerender(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', inlineSize: 390, boxSizing: 'border-box' }}>
        <button type="button" aria-label="Back to track" style={{ flex: 'none' }}>Back</button>
        <FileLayoutNav
          path={shortCjkPath}
          fullPath={`/repo/${shortCjkPath}`}
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const shortCjkTrigger = page.getByRole('button', { name: `File location: ${shortCjkPath}` });
    await expect.element(shortCjkTrigger).toBeVisible();
    expect(shortCjkTrigger.element().textContent).toBe('中文名字.md');
    const shortCjkTail = shortCjkTrigger.getByText('.md', { exact: true });
    await expect.element(shortCjkTail).toBeVisible();
    const shortCjkTailRect = shortCjkTail.element().getBoundingClientRect();
    expect(shortCjkTailRect.width).toBeGreaterThan(0);
    expect(shortCjkTailRect.right).toBeLessThanOrEqual(390);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);

    const longLatinPath = 'docs/rpc/02_rpc_full_pipeline_and_node_topology.md';
    rerender(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', inlineSize: 390, boxSizing: 'border-box' }}>
        <button type="button" aria-label="Back to track" style={{ flex: 'none' }}>Back</button>
        <FileLayoutNav
          path={longLatinPath}
          fullPath={`/repo/${longLatinPath}`}
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const latinTrigger = page.getByRole('button', { name: `File location: ${longLatinPath}` });
    await expect.element(latinTrigger).toBeVisible();
    expect(latinTrigger.element().textContent).toBe('02_rpc_full_pipeline_and_node_topology.md');
    const latinTail = latinTrigger.getByText('ogy.md', { exact: true });
    await expect.element(latinTail).toBeVisible();
    expect(latinTail.element().getBoundingClientRect().right).toBeLessThanOrEqual(390);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);

    rerender(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', inlineSize: 390, boxSizing: 'border-box' }}>
        <button type="button" aria-label="Back to track" style={{ flex: 'none' }}>Back</button>
        <FileLayoutNav
          path="docs/example.txt"
          fullPath="/repo/docs/example.txt"
          lineSuffix=":26-28"
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const shallowTrigger = page.getByRole('button', { name: 'File location: docs/example.txt:26-28' });
    expect(shallowTrigger.element().textContent).toBe('example.txt:26-28');
    await userEvent.click(shallowTrigger.element());
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('');

    rerender(
      <header style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', inlineSize: 390, boxSizing: 'border-box' }}>
        <button type="button" aria-label="Back to track" style={{ flex: 'none' }}>Back</button>
        <FileLayoutNav
          path="README.md"
          fullPath="/repo/README.md"
          fileRoot="/repo"
          onOpenQuickOpen={onOpenQuickOpen}
        />
      </header>,
    );

    const rootTrigger = page.getByRole('button', { name: 'File location: README.md' });
    expect(rootTrigger.element().textContent).toBe('README.md');
    await userEvent.click(rootTrigger.element());
    expect(onOpenQuickOpen).toHaveBeenLastCalledWith('');
  });
});
