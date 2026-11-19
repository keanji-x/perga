import '../../styles/entry.css';
import { act, cleanup, screen } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { queryKeys } from '../providers/queries.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem } from './conversation-session-data.ts';

afterEach(cleanup);

it.each([390, 1280])('retains history and draft through a failed v1 status read and explicit recovery (%ipx)', async width => {
  await page.viewport(width, 844);
  let unavailable = false;
  let release: (() => void) | undefined;
  const responseGate: { held?: Promise<void> } = {};
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: async request => {
    if (!request.path.endsWith('&view=run')) return undefined;
    await responseGate.held;
    return unavailable ? { status: 503, statusText: 'Unavailable', body: { code: 'unavailable', error: 'private transport diagnostic' } } : undefined;
  } });
  fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Retained reply.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Retained reply.', { exact: true })).toBeVisible();
  const field = page.getByRole('combobox', { name: 'Message' });
  await userEvent.type(field, 'Keep this draft.');
  unavailable = true;
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun('daily-planner') }); });
  await expect.element(page.getByText('Status unconfirmed', { exact: true })).toBeVisible();
  expect(screen.queryByText('private transport diagnostic')).toBeNull();
  unavailable = false;
  responseGate.held = new Promise<void>(resolve => { release = resolve; });
  const before = fixture.requests.length;
  try {
    await page.getByRole('button', { name: 'Reload status' }).click();
    await expect.element(page.getByRole('button', { name: 'Reload status' })).toHaveAttribute('aria-busy', 'true');
    expect(fixture.requests.slice(before).filter(request => request.path.endsWith('&view=run'))).toHaveLength(1);
    await expect.element(field).toHaveTextContent('Keep this draft.');
  } finally { release?.(); }
  await expect.element(page.getByText('Status unconfirmed', { exact: true })).not.toBeInTheDocument();
  await expect.element(page.getByText('Retained reply.', { exact: true })).toBeVisible();
  await expect.element(field).toHaveTextContent('Keep this draft.');
  expect(fixture.requests.slice(before).every(request => request.method === 'GET')).toBe(true);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});
