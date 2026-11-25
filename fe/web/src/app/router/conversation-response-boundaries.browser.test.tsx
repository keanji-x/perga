import '../../styles/entry.css';
import { cleanup, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem } from './conversation-session-data.ts';

afterEach(cleanup);

it.each([390, 1280])('preserves v1 turn boundaries through refresh and a fresh read (%ipx)', async width => {
  await page.viewport(width, 900);
  const mount = () => {
    const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
    view.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Analyze A', 'user', 'A'),
      sessionItem('daily-planner', 2, 'Analyze B', 'user', 'B'),
      sessionItem('daily-planner', 3, 'Conclusion A.', 'assistant_text', 'A'),
      sessionItem('daily-planner', 4, 'Conclusion B.', 'assistant_text', 'B')]);
    return view;
  };
  let view = mount();
  const check = async () => {
    await expect.element(page.getByText('Conclusion B.', { exact: true })).toBeVisible();
    const separators = screen.getAllByRole('separator', { name: 'New reply' });
    expect(separators).toHaveLength(1);
    const a = page.getByText('Conclusion A.', { exact: true }).element().getBoundingClientRect();
    const b = page.getByText('Conclusion B.', { exact: true }).element().getBoundingClientRect();
    const boundary = separators[0].getBoundingClientRect();
    expect(boundary.top).toBeGreaterThanOrEqual(a.bottom);
    expect(boundary.bottom).toBeLessThanOrEqual(b.top);
  };
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await check();
  await view.client.invalidateQueries();
  await check();
  cleanup(); view.client.clear();
  view = mount();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await check();
});
