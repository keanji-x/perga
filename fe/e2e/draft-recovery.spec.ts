import { expect, test } from '@playwright/test';
import { createArea } from './helpers/seed.js';
import { trackAggregateSnapshotDtoSchema, trackDtoSchema } from '../core/api/perga-v1.ts';

test('retains a rejected Track draft across navigation and creates it on an explicit retry', async ({ page, request }) => {
  const area = await createArea(request);
  const attempts: unknown[] = [];
  try {
    await page.route('**/api/v1/tracks', async route => {
      if (route.request().method() !== 'POST') { await route.continue(); return; }
      attempts.push(route.request().postDataJSON() as unknown);
      if (attempts.length === 1) {
        await route.fulfill({ status: 422, json: { code: 'invalid_argument', error: 'Review this request.' } });
      } else await route.continue();
    });
    await page.goto(`/area/${area.id}/new`);
    const composer = page.getByRole('combobox', { name: 'What this track should do' });
    await composer.fill('Keep this draft until accepted.');
    await page.getByRole('button', { name: 'Create track', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'Review this request.' })).toBeVisible();
    expect(trackDtoSchema.array().parse(await (await request.get(`/api/v1/areas/${area.id}/tracks`)).json())).toEqual([]);
    await page.getByRole('button', { name: 'Go to Today' }).click();
    await page.getByRole('button', { name: `New track in ${area.name}` }).click();
    await expect(composer).toHaveText('Keep this draft until accepted.');
    const created = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/v1/tracks');
    await page.getByRole('button', { name: 'Create track', exact: true }).click();
    const response = await created;
    expect(response.status()).toBe(201);
    const { track } = trackAggregateSnapshotDtoSchema.parse(await response.json());
    await expect(page).toHaveURL(new RegExp(`/track/${track.id}`));
    expect(attempts).toEqual([attempts[0], attempts[0]]);
    const tracks = trackDtoSchema.array().parse(await (await request.get(`/api/v1/areas/${area.id}/tracks`)).json());
    expect(tracks.map(row => row.id)).toEqual([track.id]);
  } finally { await request.delete(`/api/v1/areas/${area.id}`); }
});

test('keeps a deleted Area draft selectable and never creates an orphan Track', async ({ page, request }) => {
  const area = await createArea(request, `Deleted draft ${Date.now()}`);
  const creates: string[] = [];
  page.on('request', (entry) => { if (entry.method() === 'POST' && entry.url().endsWith('/api/v1/tracks')) creates.push(entry.url()); });
  try {
    await page.goto(`/area/${area.id}/new`);
    const composer = page.getByRole('combobox', { name: 'What this track should do' });
    await composer.fill('Keep this unfinished draft after its parent is deleted.');
    expect((await request.delete(`/api/v1/areas/${area.id}`)).ok()).toBe(true);
    await expect(page.getByRole('alert').filter({ hasText: 'Your draft is kept here' })).toBeVisible();
    await expect(composer).toHaveText('Keep this unfinished draft after its parent is deleted.');
    await expect(page.getByRole('button', { name: 'Create track', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Select draft' }).click();
    expect(await page.evaluate(() => window.getSelection()?.toString())).toBe('Keep this unfinished draft after its parent is deleted.');
    await composer.press('Enter');
    expect(creates).toEqual([]);
    await page.screenshot({ path: 'test-results/deleted-area-draft-desktop.png' });
  } finally {
    await request.delete(`/api/v1/areas/${area.id}`);
  }
});
