import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSendFeedbackFixture } from './conversation-send-feedback-fixture.tsx';

afterEach(cleanup);

it.each([false, true])('retries a refused v1 send with the original key and restores composer focus (new session: %s)', async draft => {
  await page.viewport(1400, 900);
  const view = renderSendFeedbackFixture({ refused: true });
  await page.getByRole('button', { name: draft ? 'New conversation' : 'Conversation Daily Planner conversation', exact: true }).click();
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('Keep these words through retry.');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true })).toBeVisible();
  await expect.element(page.getByText('Keep these words through retry.', { exact: true })).toBeVisible();
  view.allow();
  await page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true }).click();
  await expect.element(field).toHaveTextContent('');
  await expect.poll(() => document.activeElement).toBe(field.element());
  const writes = view.requests.filter(request => request.method === 'POST');
  expect(writes).toHaveLength(2);
  expect(writes[1].headers?.['Idempotency-Key']).toBe(writes[0].headers?.['Idempotency-Key']);
  expect(view.accepted.size).toBe(1);
});

it('replays a lost creation reply and adopts the server session without a second first input', async () => {
  await page.viewport(1400, 900);
  const view = renderSendFeedbackFixture({ lost: true, strict: true });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await page.getByRole('combobox', { name: 'Message' }).fill('Create exactly once.');
  await userEvent.keyboard('{Enter}');
  await page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true }).click();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('');
  await expect.element(page.getByRole('paragraph').filter({ hasText: /^Create exactly once\.$/ })).toBeVisible();
  const writes = view.requests.filter(request => request.method === 'POST');
  expect(writes.map(request => request.path)).toEqual(['/api/v1/tracks/daily/sessions', '/api/v1/tracks/daily/sessions']);
  expect(writes[1].headers?.['Idempotency-Key']).toBe(writes[0].headers?.['Idempotency-Key']);
  expect(view.accepted.size).toBe(1);
  expect(view.sessions.filter(session => session.id === 'server-chosen-session')).toHaveLength(1);
  expect(view.items.get('server-chosen-session')).toHaveLength(1);
});

it.each([false, true])('preserves a later focus choice when a retry finishes (new session: %s)', async draft => {
  await page.viewport(1400, 900);
  const view = renderSendFeedbackFixture({ refused: true });
  try {
    await page.getByRole('button', { name: draft ? 'New conversation' : 'Conversation Daily Planner conversation', exact: true }).click();
    await page.getByRole('combobox', { name: 'Message' }).fill('Respect my later focus choice.');
    await userEvent.keyboard('{Enter}');
    await expect.element(page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true })).toBeVisible();
    view.allow(); view.pauseSend();
    await page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true }).click();
    await expect.poll(() => view.requests.filter(request => request.method === 'POST').length).toBe(2);
    const other = document.createElement('button');
    other.textContent = 'Later focus destination';
    document.body.append(other);
    other.focus();
    view.release();
    await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('');
    expect(document.activeElement).toBe(other);
    other.remove();
  } finally { view.release(); }
});

it.each(['close', 'switch'] as const)('keeps a later view choice when session creation retry finishes after %s', async action => {
  await page.viewport(1400, 900);
  const view = renderSendFeedbackFixture({ refused: true });
  try {
    await page.getByRole('button', { name: 'New conversation', exact: true }).click();
    await page.getByRole('combobox', { name: 'Message' }).fill('Finish without reopening my drawer.');
    await userEvent.keyboard('{Enter}');
    await expect.element(page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true })).toBeVisible();
    view.allow(); view.pauseSend();
    await page.getByRole('button', { name: /^(Try again|重试|安全重试（同幂等键）)$/, exact: true }).click();
    await expect.poll(() => view.requests.filter(request => request.method === 'POST').length).toBe(2);
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    if (action === 'switch') await page.getByRole('button', { name: 'Conversation Other conversation', exact: true }).click();
    view.release();
    await expect.poll(() => view.accepted.size).toBe(1);
    await expect.poll(() => view.client.getQueryCache().getAll().some(query => query.state.fetchStatus === 'fetching')).toBe(false);
    if (action === 'close') await expect.element(page.getByRole('combobox', { name: 'Message' })).not.toBeInTheDocument();
    else await expect.element(page.getByRole('complementary', { name: 'Other conversation', exact: true })).toBeVisible();
  } finally { view.release(); }
});
