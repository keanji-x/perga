import { trackAggregateSnapshotDtoSchema } from '../core/api/perga-v1.ts';
import { expect, test, type Page } from '@playwright/test';

import { createArea, createTrack } from './helpers/seed.js';

const createdAreaIds: string[] = [];

function captureBrowserErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('pageerror', (error) => errors.push(error.message));
  return errors;
}

test.beforeEach(() => { createdAreaIds.length = 0; });
test.afterEach(async ({ request }) => {
  for (const id of createdAreaIds) await request.delete(`/api/v1/areas/${id}`);
  createdAreaIds.length = 0;
});

test('reopens a closed track from Track actions', async ({ page, request }) => {
  const errors = captureBrowserErrors(page);
  const area = await createArea(request);
  createdAreaIds.push(area.id);
  const track = await createTrack(request, area.id, 'Reopen me');

  const closed = await request.patch(`/api/v1/tracks/${track.id}`, {
    data: { closed: true },
  });
  expect(closed.ok()).toBe(true);

  await page.goto(`/track/${track.id}`);
  await expect(page.getByRole('status', { name: 'Track closed' })).toBeVisible();
  await page.getByRole('button', { name: /^Track actions for / }).click();
  await page.getByRole('menuitem', { name: /Reopen/ }).click();

  await expect(page.getByRole('status', { name: 'Track closed' })).toHaveCount(0);
  await expect.poll(async () => {
    const response = await request.get(`/api/v1/tracks/${track.id}`);
    const detail = trackAggregateSnapshotDtoSchema.parse(await response.json());
    return detail.track;
  }).toEqual(expect.objectContaining({ closedAtMs: null }));
  expect(errors).toEqual([]);
});

test('closes an open track from Track actions; the rail keeps it while open and hides it after', async ({ page, request }) => {
  const errors = captureBrowserErrors(page);
  const area = await createArea(request);
  createdAreaIds.push(area.id);
  const track = await createTrack(request, area.id, 'Close me');
  const rail = page.locator('nav[aria-label="Workspace"]');
  const railRow = rail.getByRole('button', { name: /^Track Close me/ });

  await page.goto(`/track/${track.id}`);
  await page.getByRole('button', { name: /^Track actions for / }).click();
  await page.getByRole('menuitem', { name: 'Close' }).click();

  await expect(page.getByRole('status', { name: 'Track closed' })).toBeVisible();
  await expect.poll(async () => {
    const response = await request.get(`/api/v1/tracks/${track.id}`);
    return (trackAggregateSnapshotDtoSchema.parse(await response.json())).track.closedAtMs;
  }).not.toBeNull();
  // Open in the view, so the rail keeps the closed row.
  await expect(railRow).toBeVisible();

  await page.getByRole('button', { name: 'Go to Today' }).click();
  await expect(railRow).toHaveCount(0);

  await rail.getByRole('button', { name: `Area actions for ${area.name}` }).click();
  await page.getByRole('menuitem', { name: 'Show closed' }).click();
  await expect(railRow).toBeVisible();
  expect(errors).toEqual([]);
});
