import { cleanup, render } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, describe, expect, it } from 'vitest';

import '../../styles/entry.css';
import sidebarStyles from './desktop/sidebar.module.css';
import styles from './shell.module.css';
import { setup } from './mobile-browser-fixture.tsx';

afterEach(cleanup);

function ShellProbe({ state }: { state: 'auto' | 'expanded' | 'collapsed' }) {
  const modifier = state === 'expanded' ? styles.shellExpanded
    : state === 'collapsed' ? styles.shellCollapsed : '';
  return <div data-testid={state} className={`${styles.shell} ${modifier}`}><aside /><main /></div>;
}

describe('responsive shell layout', () => {
  it('renders the brand mark as a crisp theme-owned square mask', () => {
    render(<span data-testid="brand-mark" className={sidebarStyles.brandMark} />);
    const mark = document.querySelector('[data-testid="brand-mark"]')!;
    const box = mark.getBoundingClientRect();
    const style = getComputedStyle(mark);

    expect(box.width).toBe(16);
    expect(box.height).toBe(16);
    expect(style.maskImage).not.toBe('none');
    expect(style.backgroundColor).toBe(style.color);
  });

  it('uses one content column below the compact boundary and preserves desktop rail choices above it', async () => {
    render(<><ShellProbe state="auto" /><ShellProbe state="expanded" /><ShellProbe state="collapsed" /></>);

    await page.viewport(900, 700);
    expect(getComputedStyle(document.querySelector('[data-testid="auto"]')!).gridTemplateColumns).toBe('900px');
    expect(getComputedStyle(document.querySelector('[data-testid="expanded"]')!).gridTemplateColumns).toBe('900px');
    expect(getComputedStyle(document.querySelector('[data-testid="collapsed"]')!).gridTemplateColumns).toBe('900px');

    await page.viewport(390, 844);
    expect(getComputedStyle(document.querySelector('[data-testid="auto"]')!).gridTemplateColumns).toBe('390px');

    await page.viewport(1400, 900);
    expect(getComputedStyle(document.querySelector('[data-testid="auto"]')!).gridTemplateColumns).toBe('240px 1160px');
    expect(getComputedStyle(document.querySelector('[data-testid="expanded"]')!).gridTemplateColumns).toBe('240px 1160px');
    expect(getComputedStyle(document.querySelector('[data-testid="collapsed"]')!).gridTemplateColumns).toBe('1400px');
  });

  it('gives Workspace the full mobile viewport and replaces it at the desktop breakpoint', async () => {
    await page.viewport(390, 844);
    const router = setup('/workspace');
    const heading = await page.getByRole('heading', { name: 'Workspace', exact: true }).findElement();
    const frame = heading.closest('main')!.getBoundingClientRect();
    expect(frame.left).toBe(0);
    expect(frame.width).toBe(390);
    expect(frame.bottom).toBe(844);
    expect(page.getByRole('form', { name: 'Quick chat' }).query()).toBeNull();
    await page.viewport(1400, 900);
    await expect.poll(() => router.state.location.pathname).toBe('/');
    expect(router.history.length).toBe(1);
    await expect.element(page.getByRole('navigation', { name: 'Workspace' })).toBeVisible();
  });
});

it('publishes --mobile-keyboard-inset on initial compact mount, updates on viewport resize, and cleans up on desktop', async () => {
  await page.viewport(390, 844);
  const viewport = window.visualViewport!;
  const descriptors = ['height', 'offsetTop'].map((key) => Object.getOwnPropertyDescriptor(viewport, key));
  try {
    Object.defineProperty(viewport, 'height', { configurable: true, value: 544 });
    Object.defineProperty(viewport, 'offsetTop', { configurable: true, value: 0 });
    setup('/track/w1');
    const dock = await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement() as HTMLElement;
    const shell = dock.closest<HTMLElement>('[style*="--mobile-keyboard-inset"]')!;
    expect(shell.style.getPropertyValue('--mobile-keyboard-inset')).toBe('300px');
    expect(dock.getBoundingClientRect().bottom).toBeLessThanOrEqual(544);

    Object.defineProperty(viewport, 'height', { configurable: true, value: 644 });
    viewport.dispatchEvent(new Event('resize'));
    await expect.poll(() => shell.style.getPropertyValue('--mobile-keyboard-inset')).toBe('200px');
    expect(dock.getBoundingClientRect().bottom).toBeLessThanOrEqual(644);

    await page.viewport(1400, 900);
    await expect.poll(() => shell.style.getPropertyValue('--mobile-keyboard-inset')).toBe('');
  } finally {
    ['height', 'offsetTop'].forEach((key, index) => {
      const descriptor = descriptors[index];
      if (descriptor === undefined) Reflect.deleteProperty(viewport, key);
      else Object.defineProperty(viewport, key, descriptor);
    });
    viewport.dispatchEvent(new Event('resize'));
  }
});
