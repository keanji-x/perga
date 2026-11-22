import { cleanup, render } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, describe, expect, it } from 'vitest';
import { THEME_KEY } from '../../../../core/keys/storage.ts';
import { ThemeProvider, useTheme } from './public.tsx';
import '../../styles/tokens.css';

function Controls() { const { setMode } = useTheme(); return <button type="button" onClick={() => setMode('dark')}>Dark</button>; }
function Probe() { const { resolved } = useTheme(); return <output data-testid="resolved">{resolved}</output>; }
function memoryStorage() { const values = new Map<string, string>(); return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value); } }; }
function pixelLuminance(cssColor: string): number {
  const canvas = document.createElement('canvas'); canvas.width = 1; canvas.height = 1;
  const context = canvas.getContext('2d'); if (!context) throw new Error('2D canvas context unavailable');
  context.fillStyle = cssColor; context.fillRect(0, 0, 1, 1);
  const [red, green, blue] = context.getImageData(0, 0, 1, 1).data;
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}
afterEach(() => { cleanup(); delete document.documentElement.dataset.theme; history.replaceState(null, '', '/'); });

describe('browser theme contracts', () => {
  it('E2E-CAP-THEME-011 resolves light and dark through the real document channel', async () => {
    const storage = memoryStorage(); storage.setItem(THEME_KEY, 'light'); const first = render(<ThemeProvider storage={storage}><Probe /></ThemeProvider>);
    await expect.element(page.getByTestId('resolved')).toHaveTextContent('light'); expect(document.documentElement.dataset.theme).toBe('light'); first.unmount();
    storage.setItem(THEME_KEY, 'dark'); render(<ThemeProvider storage={storage}><Probe /></ThemeProvider>);
    await expect.element(page.getByTestId('resolved')).toHaveTextContent('dark'); expect(document.documentElement.dataset.theme).toBe('dark');
  });

  it('E2E-CAP-AXE-005 a direct structured dataset write repaints token consumers', () => {
    const target = document.createElement('div'); target.style.backgroundColor = 'var(--bg)'; document.body.append(target);
    document.documentElement.dataset.theme = 'light'; const light = getComputedStyle(target).backgroundColor;
    document.documentElement.dataset.theme = 'dark'; const dark = getComputedStyle(target).backgroundColor;
    expect(light).not.toBe(dark);
    expect(pixelLuminance(dark)).toBeLessThan(pixelLuminance(light) - 80);
    expect(pixelLuminance(dark)).toBeLessThan(80); target.remove();
  });


  it('E2E-CAP-AXE-005 preserves an external dataset write across an unrelated provider re-render', () => {
    const storage = memoryStorage(); storage.setItem(THEME_KEY, 'dark');
    const view = render(<ThemeProvider storage={storage}><Probe /></ThemeProvider>);
    expect(document.documentElement.dataset.theme).toBe('dark');
    document.documentElement.dataset.theme = 'light';
    view.rerender(<ThemeProvider storage={storage}><><Probe /><span>unrelated</span></></ThemeProvider>);
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('persists theme selected through the public context controls', async () => {
    const storage = memoryStorage();
    const view = render(<ThemeProvider storage={storage}><Controls /></ThemeProvider>);
    await page.getByRole('button', { name: 'Dark' }).click();
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(storage.getItem(THEME_KEY)).toBe('dark');
    view.unmount();
    render(<ThemeProvider storage={storage}><Probe /></ThemeProvider>);
    await expect.element(page.getByTestId('resolved')).toHaveTextContent('dark');
  });
});
