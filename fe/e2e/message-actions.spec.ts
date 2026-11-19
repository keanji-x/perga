import { expect, test } from '@playwright/test';
import { sessionItem, sessionPage } from '../web/src/app/router/conversation-session-data.ts';
import { createArea, createTrack, trackSnapshot } from './helpers/seed.js';

test('copies v1 Markdown and regenerates the original input while preserving a separate draft', async ({ page, request }) => {
  await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  const area = await createArea(request);
  try {
    const track = await createTrack(request, area.id);
    const session = (await trackSnapshot(request, track.id)).sessions.find(row => row.isLead);
    if (session === undefined) throw new Error('Missing lead session');
    const answer = 'Original answer\n\n```ts\nconst value = 1;\n```';
    await page.route(`**/api/v1/sessions/${session.id}/items?**`, async route => {
      await route.fulfill({ json: sessionPage(session, [
        sessionItem(session.id, 1, 'Original prompt', 'user'), sessionItem(session.id, 2, answer),
      ]) });
    });
    const sent: Record<string, unknown>[] = [];
    const keys: (string | undefined)[] = [];
    await page.route(`**/api/v1/sessions/${session.id}/messages`, async route => {
      sent.push(route.request().postDataJSON() as Record<string, unknown>);
      keys.push(route.request().headers()['idempotency-key']);
      await route.continue();
    });
    await page.goto(`/next/track/${track.id}`);
    await page.getByRole('button', { name: 'Conversation Lead' }).click();
    await page.getByRole('button', { name: 'Copy response', exact: true }).click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(answer);
    expect(sent).toHaveLength(0);
    const composer = page.getByRole('combobox', { name: 'Message' });
    await composer.fill('Keep this separate draft');
    await page.getByRole('button', { name: 'Regenerate response', exact: true }).click();
    await expect.poll(() => sent).toEqual([expect.objectContaining({ content: 'Original prompt' })]);
    expect(keys[0]).toBeTruthy();
    await expect(composer).toHaveText('Keep this separate draft');
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(composer).toHaveText('Keep this separate draft');
    expect(sent).toHaveLength(1);
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});
