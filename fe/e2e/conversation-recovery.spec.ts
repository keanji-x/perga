import { expect, test } from '@playwright/test';
import { createArea, createTrack } from './helpers/seed.js';
test.use({ contextOptions: { reducedMotion: 'reduce' } });

test('retains a rejected conversation message and retries it once', async ({ page, request }, testInfo) => {
  const area = await createArea(request, `Recovery check ${Date.now()}`);
  try {
    const track = await createTrack(request, area.id);
    let attempts = 0;
    let accepted = false;
    await page.route('**/api/v1/sessions/*/messages', async (route) => {
      attempts += 1;
      if (attempts === 1) {
        await route.fulfill({ status: 429, json: { code: 'rate_limited', error: 'Please try again in a moment.' } });
        return;
      }
      const response = await route.fetch();
      accepted = response.ok();
      await route.fulfill({ response });
    });
    await page.goto(`/next/track/${track.id}`);
    await page.getByRole('button', { name: 'Conversation Lead' }).click();
    const composer = page.getByRole('combobox', { name: 'Message' });
    await expect(composer).toHaveAttribute('contenteditable', 'true');
    const message = 'Retain this recovery check';
    /* The same words can render in the transcript bubble and the "Queued messages" list, so each
     * assertion names its carrier; `data-nc-thread` is set on purpose, unlike hashed CSS-module classes. */
    const transcript = page.locator('[data-nc-thread]');
    await composer.fill(message);
    await composer.press('Enter');
    await expect(page.getByRole('alert')).toContainText('Please try again');
    await expect(transcript.getByText(message, { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Close conversation' }).click();
    await page.getByRole('button', { name: 'Conversation Lead' }).click();
    await expect(transcript.getByText(message, { exact: true })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('rejected-desktop.png'), fullPage: true, animations: 'disabled' });
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByRole('button', { name: 'Try again' })).toBeVisible();
    await expect.poll(async () => {
      const box = await page.locator('[data-nc-mobile-chat-panel]').boundingBox();
      return box !== null && box.x >= 0 && box.x + box.width <= 390;
    }).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('rejected-mobile.png'), fullPage: true, animations: 'disabled' });
    await page.getByRole('button', { name: 'Try again' }).click();
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect.poll(() => accepted).toBe(true);
    expect(attempts).toBe(2);
  } finally {
    await request.delete(`/api/v1/areas/${area.id}`);
  }
});
