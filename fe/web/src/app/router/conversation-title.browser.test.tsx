import '../../styles/entry.css';
import { act, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { queryKeys } from '../providers/queries.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem } from './conversation-session-data.ts';

afterEach(cleanup);

it.each([390, 1400])('uses session metadata for unopened and open conversation titles (%ipx)', async width => {
  await page.viewport(width, 900);
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  fixture.sessions[0].title = 'Original title';
  fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Stale first input', 'user')]);
  await expect.element(page.getByRole('button', { name: /^Conversation Original title/ })).toBeVisible();
  // Mobile's compact dock already owns a reader; desktop unopened metadata needs no body read.
  if (width === 1400) expect(fixture.requests.some(request => request.path.includes('/items?') && !request.path.includes('limit=1'))).toBe(false);
  await page.getByRole('button', { name: /^Conversation Original title/ }).click();
  await expect.element(page.getByText('Stale first input', { exact: true })).toBeVisible();
  for (const title of [null, 'Manual name']) {
    fixture.sessions[0].title = title;
    await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.trackDetail('daily') }); });
    const name = title ?? 'Assistant';
    await expect.element(page.getByRole(width === 390 ? 'dialog' : 'complementary', { name, exact: true })).toBeVisible();
  }
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(page.getByRole('button', { name: /^Conversation Manual name/ })).toBeVisible();
});
