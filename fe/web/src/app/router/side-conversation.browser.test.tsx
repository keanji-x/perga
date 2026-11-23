import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionReply } from './conversation-session-data.ts';
import { fixtureBootstrap, fixtureCreation, fixtureSession } from './session-contract-fixture.ts';

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

it('shows only the question in the saved side conversation when the source fills the excerpt limit', async () => {
  await page.viewport(1440, 900);
  const parent = 'daily-planner';
  const question = 'What changed?';
  const source = 'Discussion '.repeat(1_200);
  const side = fixtureSession('side-id', 'daily', { title: question, sourceSessionId: parent });
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.startsWith('/api/v1/bootstrap')) return sessionReply({ ...fixtureBootstrap(), conversationSide: true });
    if (request.method === 'POST' && request.path === '/api/v1/tracks/daily/sessions') {
      const body = request.body as { prompt: string; sourceExcerpt: string };
      expect(Array.from(body.sourceExcerpt)).toHaveLength(12_000);
      fixture.sessions.push(side);
      fixture.items.set(side.id, [sessionItem(side.id, 2, body.prompt, 'user'), sessionItem(side.id, 3, 'Side reply')]);
      return sessionReply(fixtureCreation(side));
    }
    return undefined;
  } });
  fixture.items.set(parent, [sessionItem(parent, 1, source)]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText(/Discussion Discussion/)).toBeVisible();
  await page.getByRole('combobox', { name: 'Message' }).fill('/Side ' + question);
  await userEvent.keyboard('{Enter}');
  const pane = page.getByRole('region', { name: 'Side conversation · Codex' });
  await expect.element(pane.getByText('Side reply')).toBeVisible();
  await expect.poll(() => pane.element().querySelector('[data-nc-turn="you"], [data-nc-turn="human"]')?.textContent).toBe(question);
  expect(pane.getByText(/Discussion Discussion/).query()).toBeNull();
});
