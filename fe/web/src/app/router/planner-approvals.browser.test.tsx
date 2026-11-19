import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { fixtureTrack, fixtureSnapshot, fixtureSession } from './session-contract-fixture.ts';
import { sessionReply } from './conversation-session-data.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each([1280, 390])('answers a v1 open ask in the lead conversation at %ipx', async width => {
  await page.viewport(width, 844);
  let answered = false;
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/tracks/daily/asks/release-ask/answer') {
      answered = true;
      return { status: 204, statusText: 'No Content', body: undefined };
    }
    if (request.path === '/api/v1/tracks/daily') return sessionReply({
      ...fixtureSnapshot(fixtureTrack('daily', { leadSessionId: 'daily-planner' }), [fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' })]),
      openAsks: answered ? [] : [{ id: 'release-ask', trackId: 'daily', status: 'open', delivery: 'wake', createdAtMs: 1,
        questions: [{ title: 'Which release branch?', options: ['main', 'release'] }] }],
    });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(ask.getByRole('heading', { name: 'Which release branch?' })).toBeVisible();
  await ask.getByRole('button', { name: 'release', exact: true }).click();
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST').length).toBe(1);
  expect(fixture.requests.find(request => request.method === 'POST')).toMatchObject({ path: '/api/v1/tracks/daily/asks/release-ask/answer', body: { answers: [{ option: 1, value: 'release' }] } });
  await expect.element(ask).not.toBeInTheDocument();
  expect(page.getByRole('button', { name: /^Approvals:/ }).query()).toBeNull();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});
