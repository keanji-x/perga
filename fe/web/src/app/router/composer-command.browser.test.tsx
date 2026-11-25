import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderCommandFixture } from './composer-command-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('opens the real slash menu and closes the Track through its command', async () => {
  await page.viewport(1280, 800);
  const fixture = renderCommandFixture();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('combobox', { name: 'Message' }).fill('/');
  await expect.element(page.getByRole('option', { name: /^new / })).toBeVisible();
  await page.getByRole('option', { name: /^close / }).click();
  expect(fixture.requests.filter(request => request.method === 'PATCH')).toHaveLength(0);
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'PATCH').map(request => request.body)).toEqual([{ closed: true }]);
  expect(fixture.requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(0);
});


it('keeps the slash menu open when clicking within its query and completes on Enter', async () => {
  await page.viewport(1280, 800);
  const fixture = renderCommandFixture();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const input = page.getByRole('combobox', { name: 'Message' });
  await input.fill('Explain /close');
  await expect.element(page.getByRole('option', { name: /^close / })).toBeVisible();
  // Astryx 0.7 disables light-dismiss on trigger popovers: clicking within the
  // active `/close` query keeps the menu open so Enter picks the highlighted option.
  await input.click();
  await expect.element(page.getByRole('option', { name: /^close / })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  expect(fixture.requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(0);
  expect(fixture.requests.filter(request => request.method === 'PATCH' || request.method === 'PUT')).toHaveLength(0);
});

it('submits prose containing /close with real Enter after dismissing the menu without changing approvals', async () => {
  await page.viewport(1280, 800);
  const fixture = renderCommandFixture();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const input = page.getByRole('combobox', { name: 'Message' });
  await input.fill('Explain /close');
  await expect.element(page.getByRole('option', { name: /^close / })).toBeVisible();
  await input.click();
  // Dismissing the menu returns Enter to ordinary prose submission.
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('option', { name: /^close / })).not.toBeInTheDocument();
  await userEvent.keyboard('{End}');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.path.endsWith('/messages')).map(request => request.body))
    .toEqual([expect.objectContaining({ content: 'Explain /close' })]);
  expect(fixture.requests.filter(request => request.method === 'PATCH' || request.method === 'PUT')).toHaveLength(0);
});

it('menu Enter only completes and second Enter executes', async () => {
  await page.viewport(1280, 800);
  const fixture = renderCommandFixture({ closed: true });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await page.getByRole('combobox', { name: 'Message' }).fill('/open');
  await expect.element(page.getByRole('option', { name: /^open / })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  expect(fixture.requests.filter(request => request.method === 'PATCH')).toHaveLength(0);
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('/open');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'PATCH').map(request => request.body)).toEqual([{ closed: false }]);
});
