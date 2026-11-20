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

it('decides a live approval through its execution and structured optionId', async () => {
  await page.viewport(1280, 844);
  let answered = false;
  const decisionPath = '/api/v1/tracks/daily/executions/turn-7/approvals/apr-1/decision';
  const approval = {
    id: 'apr-1', executionId: 'turn-7', sessionId: 'daily-planner', turnId: 'turn-7', createdAtMs: 1,
    title: 'Allow publishing this report?',
    options: [
      { id: 'allow_once', label: 'Allow once', isApproval: true },
      { id: 'reject_once', label: 'Reject once', isApproval: false },
    ],
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
  const drawer = page.getByRole('group', { name: 'Approval required' });
  await expect.element(drawer.getByText('Turn paused, waiting for your approval')).toBeVisible();
  await drawer.getByRole('button', { name: 'Allow once', exact: true }).click();
  await expect.poll(() => fixture.requests.find(request => request.path === decisionPath)?.body)
    .toEqual({ optionId: 'allow_once' });
  await expect.element(drawer).not.toBeInTheDocument();
});

it('isolates subagent approvals to their owning session and routes approval notifications there', async () => {
  await page.viewport(1280, 844);
  let answered = false;
  const decisionPath = '/api/v1/tracks/daily/executions/turn-sub-1/approvals/apr-sub/decision';
  const subagentApproval = {
    id: 'apr-sub', executionId: 'turn-sub-1', sessionId: 'second-planner', turnId: 'turn-sub-1', createdAtMs: 2,
    title: 'Approve subagent shell command?',
    options: [
      { id: 'approve', label: 'Approve', isApproval: true },
      { id: 'deny', label: 'Deny', isApproval: false },
    ],
  };
  const lead = fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' });
  const subagent = fixtureSession('second-planner', 'daily', { isLead: false, title: 'Other conversation' });
  const fixture = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === decisionPath) {
      answered = true;
      return sessionReply(subagentApproval);
    }
    if (request.path === '/api/v1/tracks/daily') return sessionReply({
      ...fixtureSnapshot(fixtureTrack('daily', { leadSessionId: 'daily-planner' }), [lead, subagent]),
      pendingApprovals: answered ? [] : [subagentApproval],
    });
    return undefined;
  } });

  // Lead conversation must NOT show the subagent's approval.
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  expect(page.getByRole('group', { name: 'Approval required' }).query()).toBeNull();

  // Clicking the approval notification opens the owning subagent session directly.
  await page.getByRole('button', { name: 'Open 1 notification', exact: true }).click();
  await page.getByRole('button', { name: 'Answer the Planner: Approve subagent shell command?' }).click();
  await expect.element(page.getByRole('complementary', { name: 'Other conversation', exact: true })).toBeVisible();
  const drawer = page.getByRole('group', { name: 'Approval required' });
  await expect.element(drawer.getByRole('heading', { name: 'Approve subagent shell command?' })).toBeVisible();
  await drawer.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect.poll(() => fixture.requests.find(request => request.path === decisionPath)?.body)
    .toEqual({ optionId: 'approve' });
  await expect.element(drawer).not.toBeInTheDocument();
});

it('opens Today Assistant for a subagent approval notification from Report changes', async () => {
  await page.viewport(1280, 844);
  let answered = false;
  const decisionPath = '/api/v1/tracks/daily/executions/turn-sub-1/approvals/apr-sub/decision';
  const subagentApproval = {
    id: 'apr-sub', executionId: 'turn-sub-1', sessionId: 'second-planner', turnId: 'turn-sub-1', createdAtMs: 2,
    title: 'Approve subagent shell command?',
    options: [
      { id: 'approve', label: 'Approve', isApproval: true },
      { id: 'deny', label: 'Deny', isApproval: false },
    ],
  };
  const lead = fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' });
  const subagent = fixtureSession('second-planner', 'daily', { isLead: false, title: 'Other conversation' });
  const fixture = renderSessionFixture({ initial: '/', reply: request => {
    if (request.path === decisionPath) {
      answered = true;
      return sessionReply(subagentApproval);
    }
    if (request.path === '/api/v1/tracks/daily') return sessionReply({
      ...fixtureSnapshot(fixtureTrack('daily', { leadSessionId: 'daily-planner' }), [lead, subagent]),
      pendingApprovals: answered ? [] : [subagentApproval],
    });
    return undefined;
  } });

  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  await expect.element(assistant).toBeVisible();
  await page.getByRole('button', { name: 'Report changes', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: /Report changes ·/ })).toBeVisible();
  await expect.element(assistant).not.toBeInTheDocument();

  await page.getByRole('button', { name: 'Collapse notifications', exact: true }).click();
  await page.getByRole('button', { name: 'Open 1 notification', exact: true }).click();
  await page.getByRole('button', { name: 'Answer the Planner: Approve subagent shell command?' }).click();
  await expect.element(assistant).toBeVisible();
  const drawer = assistant.getByRole('group', { name: 'Approval required' });
  await expect.element(drawer.getByRole('heading', { name: 'Approve subagent shell command?' })).toBeVisible();
  await drawer.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect.poll(() => fixture.requests.find(request => request.path === decisionPath)?.body)
    .toEqual({ optionId: 'approve' });
  await expect.element(drawer).not.toBeInTheDocument();
});
