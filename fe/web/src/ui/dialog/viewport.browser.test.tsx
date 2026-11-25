import { cleanup, render } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../styles/entry.css';
import { Dialog } from './public.tsx';

afterEach(cleanup);

it.each([400, 900])('contains a long wide dialog and its last action within a %ipx viewport', async height => {
  await page.viewport(900, height);
  render(<Dialog purpose="info" origin={{ kind: 'surface-center' }} open wide title="Long settings" onClose={vi.fn()}>
    {Array.from({ length: 80 }, (_, index) => <p key={index}>Setting {index + 1}</p>)}
    <button type="button">Complete long dialog</button>
  </Dialog>);
  const dialog = page.getByRole('dialog', { name: 'Long settings' }).element();
  await Promise.all(dialog.getAnimations().map(animation => animation.finished));
  expect.soft(dialog.getBoundingClientRect().top).toBeGreaterThanOrEqual(0);
  expect.soft(dialog.getBoundingClientRect().bottom).toBeLessThanOrEqual(height);
  const last = page.getByRole('button', { name: 'Complete long dialog' }).element();
  last.scrollIntoView({ block: 'nearest' });
  last.focus();
  expect.soft(last.getBoundingClientRect().bottom).toBeLessThanOrEqual(height);
  expect(last.getBoundingClientRect().top).toBeGreaterThanOrEqual(dialog.getBoundingClientRect().top);
  await expect.element(page.getByRole('button', { name: 'Complete long dialog' })).toHaveFocus();
});

it.each([
  [390, 844],
  [320, 640],
] as const)('fills the full %ix%i viewport cleanly in fullscreen mode without modal frame chrome', async (width, height) => {
  await page.viewport(width, height);
  render(<Dialog purpose="form" origin={{ kind: 'surface-center' }} open wide fullscreen hideTitleRow title="Settings" onClose={vi.fn()}>
    <p>Mobile settings body</p>
  </Dialog>);
  const dialog = page.getByRole('dialog', { name: 'Settings' }).element() as HTMLElement;
  const box = dialog.getBoundingClientRect();
  const style = getComputedStyle(dialog);
  expect(box.left).toBe(0);
  expect(box.top).toBe(0);
  expect(box.width).toBe(width);
  expect(box.height).toBe(height);
  expect(style.borderRadius).toBe('0px');
  expect(style.boxShadow).toBe('none');
  expect(dialog.getAnimations()).toHaveLength(0);
});
