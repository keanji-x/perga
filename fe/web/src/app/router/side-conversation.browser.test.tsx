import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem } from './conversation-session-data.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each([1440, 390])('keeps the parent draft and refuses unsupported v1 side creation at %ipx', async width => {
  await page.viewport(width, 900);
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'The main discussion remains available.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('The main discussion remains available.')).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('/side Explore the design');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('alert').first()).toBeVisible();
  await expect.element(field).toHaveTextContent('/side Explore the design');
  expect(fixture.requests.filter(request => request.method === 'POST')).toEqual([]);
  expect(document.querySelector('[data-nc-side-conversation]')).toBeNull();
});
