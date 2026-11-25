import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionTurn } from './conversation-session-data.ts';

afterEach(cleanup);

it.each([390, 1280])('reads saved v1 messages without a native thread and fences sending during a running turn (%ipx)', async width => {
  await page.viewport(width, 844);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  view.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Saved human words.', 'user'),
    sessionItem('daily-planner', 2, 'Saved reply.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Saved human words.', { exact: true })).toBeVisible();
  await expect.element(page.getByText('Saved reply.', { exact: true })).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Words for the next turn.');
  view.sessions[0].status = 'running';
  view.activeTurns.set('daily-planner', sessionTurn('daily-planner'));
  await view.client.invalidateQueries();
  await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  await expect.element(page.getByRole('button', { name: /^(Send|Queue message)$/, exact: true })).toBeVisible();
  await expect.element(field).toHaveTextContent('Words for the next turn.');
  view.activeTurns.clear();
  view.sessions[0].status = 'idle';
  await view.client.invalidateQueries();
  await expect.element(page.getByRole('button', { name: /^(Send|Queue message)$/, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Saved human words.', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST')).toHaveLength(0);
});
