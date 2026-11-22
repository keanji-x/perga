import { expect, test, type Page } from '@playwright/test';
import { areaDtoSchema } from '../core/api/perga-v1.ts';

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

test('creates, edits, and deletes an area through the shared dialog', async ({ page, request }) => {
  const errors = captureBrowserErrors(page);
  const name = `FE e2e CRUD ${Date.now()}`;
  const renamed = `${name} edited`;
  await page.goto('/');
  const rail = page.locator('nav[aria-label="Workspace"]');
  await rail.getByRole('button', { name: 'New area' }).click();
  const create = page.getByRole('dialog', { name: 'New area' });
  await create.getByRole('textbox', { name: /^Name/ }).fill(name);
  await expect(create.getByRole('button', { name: /template|isolation/i })).toHaveCount(0);
  await create.getByRole('button', { name: 'Create area' }).click();

  const row = rail.getByRole('button', { name: `Collapse area ${name}` });
  await expect(row).toBeVisible();
  await expect.poll(async () => {
    const response = await request.get('/api/v1/areas');
    return areaDtoSchema.array().parse(await response.json()).find((area) => area.name === name);
  }).toBeTruthy();
  const areasResponse = await request.get('/api/v1/areas');
  const area = areaDtoSchema.array().parse(await areasResponse.json()).find((item) => item.name === name);
  expect(area).toBeTruthy();
  createdAreaIds.push(area!.id);
  expect(areaDtoSchema.parse(area).defaultCwd).toBeNull();

  await row.hover();
  await rail.getByRole('button', { name: `Area actions for ${name}` }).click();
  await page.getByRole('menuitem', { name: 'Edit area' }).click();
  const edit = page.getByRole('dialog', { name: `Edit ${name}` });
  await expect(edit.getByRole('button', { name: /template|isolation/i })).toHaveCount(0);
  await edit.getByRole('textbox', { name: /^Name/ }).fill(renamed);
  await edit.getByRole('button', { name: 'Save changes' }).click();
  const renamedRow = rail.getByRole('button', { name: `Collapse area ${renamed}` });
  await expect(renamedRow).toBeVisible();
  await expect.poll(async () => {
    const response = await request.get('/api/v1/areas');
    return areaDtoSchema.array().parse(await response.json()).find((item) => item.id === area!.id);
  }).toMatchObject({ name: renamed, defaultCwd: null });

  await renamedRow.hover();
  await rail.getByRole('button', { name: `Area actions for ${renamed}` }).click();
  await page.getByRole('menuitem', { name: 'Delete area' }).click();
  const dialog = page.getByRole('dialog', { name: `Delete ${renamed}?` });
  await dialog.getByLabel(`Type ${renamed} to confirm.`).fill(renamed);
  await dialog.getByRole('button', { name: 'Delete area', exact: true }).click();
  await expect(renamedRow).toHaveCount(0);
  await expect.poll(async () => {
    const response = await request.get('/api/v1/areas');
    return areaDtoSchema.array().parse(await response.json()).some((item) => item.id === area!.id);
  }).toBe(false);
  createdAreaIds.length = 0;
  expect(errors).toEqual([]);
});
