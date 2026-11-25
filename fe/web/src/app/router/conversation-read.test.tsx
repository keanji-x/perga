import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { queryKeys } from '../providers/query-keys.ts';
import { renderConversationReadFixture } from './conversation-read-fixture.tsx';

afterEach(cleanup);

async function openConversation() {
  fireEvent.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  await screen.findByText('Retained reply.');
}

it.each([true, false])('does not claim Running after a failed run read (initial failure: %s)', async (initialFailure) => {
  const fixture = renderConversationReadFixture(initialFailure);
  await openConversation();
  const field = screen.getByRole('combobox', { name: 'Message' });
  fireEvent.input(field, { target: { textContent: 'Keep this draft.' } });
  if (!initialFailure) {
    await screen.findByText('Working…', { exact: true });
    fixture.failRun(true);
    await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun('daily-planner') }); });
  }
  await screen.findByText('Unable to sync latest conversation state', { exact: true });
  expect(screen.queryByText('Working…', { exact: true }) !== null).toBe(!initialFailure);
  const alert = await screen.findByRole('alert');
  expect(alert.textContent).toContain('Unable to sync conversation state.');
  expect(alert.textContent).not.toContain('private transport diagnostic');
  expect(within(alert).getByRole('button', { name: 'Refresh' })).toBeTruthy();
  expect(field.textContent).toBe('Keep this draft.');
  const before = fixture.requests.length;
  fixture.failRun(false);
  fireEvent.click(within(alert).getByRole('button', { name: 'Refresh' }));
  await screen.findByText('Working…', { exact: true });
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  expect(screen.queryByText('Unable to sync latest conversation state', { exact: true })).toBeNull();
  expect(screen.getByText('Retained reply.')).toBeTruthy();
  expect(field.textContent).toBe('Keep this draft.');
  const after = fixture.requests.slice(before);
  expect(after.every(request => request.method === 'GET')).toBe(true);
  expect(new Set(after.map(request => request.path)))
    .toEqual(new Set(['/api/v1/sessions/daily-planner/items?afterId=0&limit=1']));
});

it('retries history independently and retains the last readable transcript', async () => {
  const fixture = renderConversationReadFixture(false);
  await openConversation();
  fixture.failHistory(true);
  await act(async () => { await fixture.client.invalidateQueries({ type: 'active' }); });
  const alert = await screen.findByRole('alert');
  expect(alert.textContent).toContain('Unable to load conversation history.');
  expect(screen.getByText('Retained reply.')).toBeTruthy();
  const before = fixture.requests.length;
  fixture.failHistory(false);
  fireEvent.click(within(alert).getByRole('button', { name: 'Refresh' }));
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull());
  const after = fixture.requests.slice(before);
  expect(after.every(request => request.method === 'GET')).toBe(true);
  // Status/live polling may run while the history retry settles. Count the
  // independently retried history read without treating a poll as its effect.
  expect(after.filter(request => request.path === '/api/v1/sessions/daily-planner/items?afterId=0&limit=60&latest=true')).toHaveLength(1);
});

it('keeps execution status unconfirmed throughout a cold retry', async () => {
  const fixture = renderConversationReadFixture(true);
  await openConversation();
  const retry = await screen.findByRole('button', { name: 'Refresh' });
  fixture.failRun(false);
  const release = fixture.pauseRun();
  try {
    fireEvent.click(retry);
    await waitFor(() => expect(fixture.client.isFetching({ queryKey: queryKeys.plannerRun('daily-planner') })).toBe(1));
    expect(screen.getByRole('button', { name: 'Refresh' }).getAttribute('aria-busy')).toBe('true');
    await screen.findByText('Unable to sync latest conversation state', { exact: true });
    expect(screen.queryByText('Working…', { exact: true })).toBeNull();
  } finally { act(() => { release(); }); }
  await screen.findByText('Working…', { exact: true });
});
