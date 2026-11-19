import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { fixtureSession } from './session-contract-fixture.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);

it.each(['claude', 'codex', 'opencode'] as const)('posts v1 compact separately from messages for %s', async provider => {
  await page.viewport(1280, 800);
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request =>
    request.path === '/api/v1/sessions/daily-planner/compact' ? sessionReply(fixtureSession('daily-planner', 'daily')) : undefined });
  fixture.sessions[0].provider = provider;
  fixture.sessions[0].contextUsageRatio = 0.47;
  fixture.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Earlier reply.')]);
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Earlier reply.', { exact: true })).toBeVisible();
  await expect.element(page.getByRole('img', { name: /94k of 200k/ })).toBeVisible();
  await page.getByRole('combobox', { name: 'Message' }).fill('/compact');
  await expect.element(page.getByRole('option', { name: /^compact/ })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/compact')).length).toBe(1);
  expect(fixture.requests.filter(request => request.method === 'POST').map(request => request.path)).toEqual(['/api/v1/sessions/daily-planner/compact']);
  await expect.element(page.getByText('Earlier reply.', { exact: true })).toBeVisible();
});
