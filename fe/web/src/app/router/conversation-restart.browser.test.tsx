import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionInput, sessionItem, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);

it.each([390, 1280])('abandons an unknown v1 input while retaining history and the unsent draft (%ipx)', async width => {
  await page.viewport(width, 844);
  const input = { ...sessionInput('daily-planner', 'unknown-input'), deliveryState: 'unknown' as const, turnStatus: 'unknown' as const };
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path !== '/api/v1/sessions/daily-planner/inputs/unknown-input/abandon') return undefined;
    view.unknownInputs.delete('daily-planner');
    view.sessions[0].status = 'idle';
    view.sessions[0].epoch += 1;
    return sessionReply({ ...input, deliveryState: 'rejected', turnStatus: 'interrupted' });
  } });
  view.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Earlier reply that stays.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Earlier reply that stays.', { exact: true })).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Keep this draft.');
  view.unknownInputs.set('daily-planner', input);
  view.sessions[0].status = 'unknown';
  await view.client.invalidateQueries();
  const action = page.getByRole('button', { name: 'Start a fresh provider session', exact: true });
  await expect.element(action).toBeVisible();
  await action.click();
  await expect.element(action).not.toBeInTheDocument();
  await expect.element(field).toHaveTextContent('Keep this draft.');
  await expect.element(field).toHaveAttribute('contenteditable', 'true');
  await expect.element(page.getByText('Earlier reply that stays.', { exact: true })).toBeVisible();
  expect(view.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual([
    '/api/v1/sessions/daily-planner/inputs/unknown-input/abandon',
  ]);
});
