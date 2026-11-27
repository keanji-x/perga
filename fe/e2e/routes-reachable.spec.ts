import { expect } from '@playwright/test';
import { test } from './helpers/workspace-test.ts';
import { bootstrapDtoSchema } from '../core/api/perga-v1.ts';
import { createArea, createTrack } from './helpers/seed.js';

test('v1 bootstrap and workspace routes are reachable through the real kernel', async ({ page, request }) => {
  const response = await request.get('/api/v1/bootstrap');
  expect(response.ok()).toBe(true);
  const bootstrap = bootstrapDtoSchema.parse(await response.json());
  expect(bootstrap.version).not.toBe('');
  const area = await createArea(request);
  try {
    const track = await createTrack(request, area.id);
    for (const path of [`/area/${area.id}/new`, `/track/${track.id}`, '/settings/appearance']) {
      await page.goto(path);
      await expect(page.locator('nav[aria-label="Workspace"]')).toBeVisible();
      if (path.endsWith('/new')) await expect(page.getByLabel('What this track should do')).toBeVisible();
      else if (path.includes('/track/')) await expect(page.locator('[data-nc-page-title]')).toContainText(track.title);
      else await expect(page.getByRole('radiogroup', { name: 'Theme' })).toBeVisible();
    }
  } finally { expect((await request.delete(`/api/v1/areas/${area.id}`)).status()).toBe(204); }
});
