import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionReply } from './conversation-session-data.ts';
import { fixtureBootstrap } from './session-contract-fixture.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each([{ width: 1440, supported: true }, { width: 1440, supported: false }, { width: 390, supported: true }])(
  'handles /Side at $width px with server support $supported', async ({ width, supported }) => {
  await page.viewport(width, 900);
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request =>
    request.path.startsWith('/api/v1/bootstrap') ? sessionReply({ ...fixtureBootstrap(), conversationSide: supported }) : undefined });
  fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'The main discussion remains available.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('The main discussion remains available.')).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('/Side ');
  await userEvent.keyboard('{Enter}');
  if (supported && width === 1440) {
    await expect.element(page.getByRole('button', { name: 'Close side conversation' })).toBeVisible();
  } else {
    await expect.element(page.getByRole('alert').first()).toBeVisible();
    await expect.element(field).toHaveTextContent('/Side');
    expect(page.getByRole('button', { name: 'Close side conversation' }).query()).toBeNull();
  }
  expect(fixture.requests.filter(request => request.method === 'POST')).toEqual([]);
});
