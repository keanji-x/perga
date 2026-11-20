import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { fixtureTrack, fixtureSnapshot, fixtureSession } from './session-contract-fixture.ts';
import { sessionPage, sessionReply } from './conversation-session-data.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it('uses the approval options advertised by the open ACP session', async () => {
  await page.viewport(1280, 844);
  const session = fixtureSession('daily-planner', 'daily', {
    isLead: true, provider: 'acp:opencode', title: 'ACP Planner conversation',
  });
  renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/tracks/daily')
      return sessionReply(fixtureSnapshot(fixtureTrack('daily', { leadSessionId: session.id }), [session]));
    if (request.path.startsWith(`/api/v1/sessions/${session.id}/items?`))
      return sessionReply({
        ...sessionPage(session),
        permissionModes: [
          { id: 'inspect', label: 'Inspect workspace', description: 'Read only', isDefault: true },
          { id: 'act', label: 'Make changes', description: 'Allow edits' },
        ],
      });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation ACP Planner conversation/ }).click();
  const approvals = page.getByRole('button', { name: 'Approvals: Inspect workspace' });
  await expect.element(approvals).toBeEnabled();
  await approvals.click();
  await expect.element(page.getByText('Make changes', { exact: true })).toBeVisible();
});

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

it('answers a live approval through its execution and approval identities', async () => {
  await page.viewport(1280, 844);
  let answered = false;
  const decisionPath = '/api/v1/tracks/daily/executions/turn-7/approvals/apr-1/decision';
  const approval = {
    id: 'apr-1', executionId: 'turn-7', sessionId: 'daily-planner', turnId: 'turn-7', createdAtMs: 1,
    questions: [{ question: 'Allow publishing this report?', options: ['Approve', 'Deny'] }],
  };
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === decisionPath) {
      answered = true;
      return sessionReply(approval);
    }
    if (request.path === '/api/v1/tracks/daily') return sessionReply({
      ...fixtureSnapshot(fixtureTrack('daily', { leadSessionId: 'daily-planner' }),
        [fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' })]),
      pendingApprovals: answered ? [] : [approval],
    });
    return undefined;
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const drawer = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(drawer.getByText('Turn paused, waiting for your approval')).toBeVisible();
  await drawer.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect.poll(() => fixture.requests.find(request => request.path === decisionPath)?.body)
    .toEqual({ answers: [{ option: 0, value: 'Approve' }] });
  await expect.element(drawer).not.toBeInTheDocument();
});
