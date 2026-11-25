import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';
import '../../styles/entry.css';
import { useCompactViewport } from '../viewport/public.ts';
import { Dialog, useDialogView } from './public.tsx';

afterEach(async () => {
  cleanup();
  await page.viewport(1280, 800);
});

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
  const overlay = dialog.closest('[role="presentation"]') as HTMLElement;
  const box = dialog.getBoundingClientRect();
  const style = getComputedStyle(dialog);
  expect(box.left).toBe(0);
  expect(box.top).toBe(0);
  expect(box.width).toBe(width);
  expect(box.height).toBe(height);
  expect(style.borderRadius).toBe('0px');
  expect(style.boxShadow).toBe('none');
  expect(dialog.getAnimations()).toHaveLength(0);
  expect(overlay.getAnimations()).toHaveLength(0);
});

function ResponsiveFullscreenDialog() {
  const compact = useCompactViewport();
  return (
    <Dialog purpose="form" origin={{ kind: 'surface-center' }} open wide="medium" fullscreen={compact} hideTitleRow={compact} title="Settings" onClose={vi.fn()}>
      <ChildViewLauncher />
    </Dialog>
  );
}

function ChildViewLauncher() {
  const dialogView = useDialogView();
  return (
    <button
      type="button"
      onClick={() => {
        dialogView?.pushView({
          title: 'Provider details',
          body: <button type="button">Save provider</button>,
        });
      }}
    >
      Open provider details
    </button>
  );
}

it('preserves child-view focus and Escape restoration when an open dialog crosses the fullscreen breakpoint', async () => {
  await page.viewport(390, 844);
  render(<ResponsiveFullscreenDialog />);

  const opener = page.getByRole('button', { name: 'Open provider details' });
  await opener.click();
  const childAction = page.getByRole('button', { name: 'Save provider' });
  await expect.element(childAction).toBeVisible();
  (childAction.element() as HTMLElement).focus();
  await expect.element(childAction).toHaveFocus();

  // Cross to desktop modal and verify the pushed child view stays open and focused.
  await page.viewport(1280, 800);
  await expect.poll(() => page.getByRole('dialog', { name: 'Provider details' }).element().getBoundingClientRect().width).toBeLessThan(1280);
  await expect.element(childAction).toHaveFocus();

  // Cross back to compact fullscreen: no scrim/panel animations and focus stays on the child action.
  await page.viewport(390, 844);
  const dialog = page.getByRole('dialog', { name: 'Provider details' }).element() as HTMLElement;
  await expect.poll(() => dialog.getBoundingClientRect().width).toBe(390);
  const overlay = dialog.closest('[role="presentation"]') as HTMLElement;
  expect(dialog.getAnimations()).toHaveLength(0);
  expect(overlay.getAnimations()).toHaveLength(0);
  await expect.element(childAction).toHaveFocus();

  // Escape pops the child view and restores focus to its opener inside the parent dialog.
  await userEvent.keyboard('{Escape}');
  await expect.element(opener).toBeVisible();
  await expect.element(opener).toHaveFocus();
});
