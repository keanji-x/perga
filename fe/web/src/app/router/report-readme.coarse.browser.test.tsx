import { verifyNestedReadmeHistory } from './report-readme-history.browser-fixture.ts';
import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page } from 'vitest/browser';
import { README_FILE_URL, README_TRACK_URL, renderReadmeFixture } from './report-readme-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('opens a Track conversation from a mobile Report file and returns to the same reading position', async () => {
  await page.viewport(390, 844);
  const { router } = renderReadmeFixture(README_TRACK_URL, undefined, { shortReport: true });
  await expect.element(page.getByRole('button', { name: 'docs/README.md', exact: true })).toBeVisible();
  await commands.tap('button[title="docs/README.md"]');
  const file = page.getByRole('region', { name: 'File docs/README.md' });
  await expect.element(file.getByRole('heading', { name: 'Documentation', exact: true })).toBeVisible();

  const back = page.getByRole('button', { name: 'Back to Report', exact: true });
  const open = page.getByRole('button', { name: 'Open conversation', exact: true });
  await expect.element(back).toBeVisible();
  await expect.element(open, { timeout: 1500 }).toBeVisible();
  const button = open.element();
  const bounds = button.getBoundingClientRect();
  expect(button.closest('header')).toBe(back.element().closest('header'));
  expect(bounds.left).toBeGreaterThan(back.element().getBoundingClientRect().right);
  expect(bounds.width).toBeGreaterThanOrEqual(44);
  expect(bounds.height).toBeGreaterThanOrEqual(44);
  await page.screenshot({ path: './__screenshots__/report-readme-mobile-chat-entry.png' });

  const documentView = file.element() as HTMLElement;
  documentView.scrollTop = 240;
  const scrollTop = documentView.scrollTop;
  expect(scrollTop).toBeGreaterThan(0);
  const historyLength = router.history.length;
  // Some browsers activate a tapped button without first transferring focus.
  documentView.focus({ preventScroll: true });
  await expect.element(file).toHaveFocus();
  button.addEventListener('mousedown', event => event.preventDefault(), { once: true });
  await commands.tap('button[aria-label="Open conversation"]');
  const chat = page.getByRole('dialog', { name: 'Existing conversation', exact: true });
  await expect.element(chat.getByText('Conversation remains available while reading documentation.')).toBeVisible();
  await expect.element(chat.getByRole('combobox', { name: 'Message' })).toBeVisible();
  await page.screenshot({ path: './__screenshots__/report-readme-mobile-chat-open.png' });
  expect(router.history.location.href).toBe(README_FILE_URL);

  await commands.tap('button[aria-label="Close conversation"]');
  await expect.element(chat).not.toBeInTheDocument();
  await expect.element(open).toHaveFocus();
  expect(file.element()).toBe(documentView);
  expect(documentView.scrollTop).toBe(scrollTop);
  expect(router.history.location.href).toBe(README_FILE_URL);
  expect(router.history.length).toBe(historyLength);

  await commands.tap('button[aria-label="Back to Report"]');
  await expect.element(file).not.toBeInTheDocument();
  expect(router.history.location.href).toBe(README_TRACK_URL);
});

it.each([false, true])('preserves nested document history and scroll (cold=%s)', async cold => {
  await verifyNestedReadmeHistory(390, cold);
});
