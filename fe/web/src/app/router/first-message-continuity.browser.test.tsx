import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import { FIRST_MESSAGE, renderFirstMessageFixture } from './tests/first-message-continuity-fixture.tsx';

afterEach(async () => {
  cleanup(); document.getElementById('root')?.remove();
  await commands.emulateReducedMotion(false); await page.viewport(1280, 800);
});

it.each([{ width: 1280, reduce: false }, { width: 390, reduce: false }, { width: 390, reduce: true }])(
  'keeps one readable first message through v1 creation and delayed history ($width px, reduced=$reduce)', async ({ width, reduce }) => {
    await page.viewport(width, 900); await commands.emulateReducedMotion(reduce);
    const copies = () => [...document.querySelectorAll<HTMLElement>(':is([data-nc-turn="you"], [data-nc-turn="unknown"], [data-nc-turn="human"])')]
      .filter(element => element.textContent === FIRST_MESSAGE);
    const fixture = renderFirstMessageFixture({ mobile: width === 390 });
    try {
      if (width === 390) {
        await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
        await page.getByRole('button', { name: 'New conversation', exact: true }).click();
      } else await page.getByRole('button', { name: 'New conversation', exact: true }).click();
      await page.getByRole('combobox', { name: 'Message' }).fill(FIRST_MESSAGE);
      await userEvent.keyboard('{Enter}');
      await expect.poll(() => copies().length).toBe(1);
      fixture.answerCreate();
      await expect.poll(fixture.historyCount).toBeGreaterThan(0);
      await expect.poll(() => copies().length).toBe(1);
      fixture.releaseHistory();
      await expect.poll(() => document.querySelector('[data-nc-thread]')).not.toBeNull();
      await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveAttribute('contenteditable', 'true');
      expect(copies()).toHaveLength(1);
      const box = copies()[0].getBoundingClientRect();
      expect(box.width).toBeGreaterThan(50); expect(box.height).toBeGreaterThan(10);
      expect(box.left).toBeGreaterThanOrEqual(0); expect(box.right).toBeLessThanOrEqual(width);
      expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
      expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'))).toHaveLength(1);
      expect(fixture.requests.some(request => request.path.endsWith('/initial-input'))).toBe(false);
    } finally { fixture.answerCreate(); fixture.releaseHistory(); cleanup(); fixture.client.clear(); }
  },
);

it('renders the stored first message from the v1 session items page', async () => {
  await page.viewport(1280, 900);
  const fixture = renderFirstMessageFixture();
  try {
    await page.getByRole('button', { name: 'New conversation', exact: true }).click();
    await page.getByRole('combobox', { name: 'Message' }).fill(FIRST_MESSAGE);
    await userEvent.keyboard('{Enter}');
    fixture.releaseHistory(); fixture.answerCreate();
    await expect.poll(fixture.historyCount).toBeGreaterThan(0);
    await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveAttribute('contenteditable', 'true');
    await expect.element(page.getByText(FIRST_MESSAGE, { exact: true }).last()).toBeVisible();
  } finally { fixture.answerCreate(); fixture.releaseHistory(); cleanup(); fixture.client.clear(); }
});
