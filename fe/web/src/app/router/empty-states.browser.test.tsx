import '../../styles/entry.css';
import { fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { queryKeys } from '../providers/query-keys.ts';
import { sessionInput, sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';

afterEach(cleanup);
async function settle() {
  await expect.poll(() => document.getAnimations().filter(animation => animation.effect?.getTiming().iterations !== Infinity).every(animation => animation.playState !== 'running')).toBe(true);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
}

it.each([1440, 390, 320])('keeps the empty daily report and conversation reachable at %i', async (width) => {
  await page.viewport(width, 900);
  renderDailyFixture({ initial: width >= 960 ? '/' : '/?day=2026-10-04', reportBody: '' });
  await expect.element(page.getByRole('heading', { name: width >= 960 ? 'What would you like to move forward today?' : 'Focus for this day' })).toBeVisible();
  await settle();
  await page.screenshot({ path: `./__screenshots__/empty-audit/daily-${width}.png` });
  if (width >= 960) {
    const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
    await expect.element(assistant.getByRole('heading', { name: 'What would you like to move forward today?' })).toBeVisible();
    await expect.element(assistant.getByRole('combobox', { name: 'Message' })).toBeVisible();
    expect(assistant.element().querySelector('[data-nc-thread-empty]')).toBeNull();
    return;
  }
  else {
    await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  }
  await expect.poll(() => document.querySelector('[data-nc-thread-empty]') !== null).toBe(true);
  await expect.element(page.getByText('Start a conversation', { exact: true })).toBeVisible();
  const title = page.getByRole('heading', { name: 'Start a conversation', exact: true }).element();
  await settle();
  const transcript = document.querySelector('[data-nc-thread-empty]')!;
  const scroll = transcript.closest('[data-nc-drawer-scroll]')!;
  const scrollStyle = getComputedStyle(scroll);
  const host = transcript.parentElement!.parentElement!;
  const bottomInset = host === scroll ? 0 : parseFloat(getComputedStyle(host).paddingBottom);
  // The role control precedes the transcript inside this scroll host.
  const available = scroll.getBoundingClientRect().bottom - transcript.getBoundingClientRect().top
    - parseFloat(scrollStyle.paddingBottom) - bottomInset;
  expect(transcript.getBoundingClientRect().height).toBeGreaterThanOrEqual(available - 1);
  expect(getComputedStyle(title).fontSize).toBe('16px');
  const visual = title.previousElementSibling!;
  expect(visual.getBoundingClientRect().width).toBeCloseTo(16, 2);
  expect(visual.getBoundingClientRect().right).toBeLessThan(title.getBoundingClientRect().left);
  expect(Math.abs((visual.getBoundingClientRect().top + visual.getBoundingClientRect().bottom) / 2 - (title.getBoundingClientRect().top + title.getBoundingClientRect().bottom) / 2)).toBeLessThan(1);
  expect(visual.getAttribute('aria-hidden')).toBe('true');
  await settle();
  await page.screenshot({ path: `./__screenshots__/empty-audit/chat-${width}.png` });
});

it.each(['report', 'flow'] as const)('hands the centered Lead conversation to the desktop drawer when %s content arrives', async (content) => {
  await page.viewport(1440, 900);
  const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
  const track = fixtureTrack('project', { areaId: 'project-area', title: content === 'report' ? '' : 'Untitled Track', leadSessionId: lead.id });
  const sessions = content === 'report' ? [lead] : [lead, fixtureSession('project-review', track.id, { title: 'Review' })];
  const firstMessage = 'Make a release plan';
  let sent = false;
  let published = false;
  const { client, requests } = renderDailyFixture({
    initial: '/track/project',
    reply: request => {
      if (request.path === '/api/v1/tracks/project') return sessionReply(fixtureSnapshot(track, sessions,
        published && content === 'report' ? fixtureReport('# Initial plan', 2) : fixtureReport()));
      if (request.path === '/api/v1/tracks/project/views') return sessionReply(published && content === 'flow' ? { flowRev: 1, flow: {
        rev: 1, nodes: [{ id: 'release', title: 'Plan the release', doKind: 'track', status: 'done', outcome: 'ok', childTrackId: 'release' }], archived: [],
      } } : {});
      if (request.path.startsWith('/api/v1/sessions/project-lead/items?')) return sessionReply(sessionPage(lead,
        sent ? [sessionItem(lead.id, 2, firstMessage, 'user')] : []));
      if (request.method === 'POST' && request.path === '/api/v1/sessions/project-lead/messages') {
        sent = true;
        return sessionReply(sessionInput(lead.id));
      }
      return undefined;
    },
  });
  const stage = page.getByRole('region', { name: 'Lead', exact: true });
  const composer = stage.getByRole('combobox', { name: 'Message', exact: true });
  const heading = stage.getByRole('heading', { name: 'What should this track focus on?', exact: true });
  await expect.element(heading).toBeVisible();
  const placeholder = stage.getByText('Write a message, @ to mention, or / for commands…', { exact: true });
  await expect.element(placeholder).toBeVisible();
  expect(heading.element().parentElement?.nextElementSibling).toContainElement(composer.element());
  expect(placeholder.element().closest('[data-nc-composer]')).not.toBeNull();
  await expect.element(stage.getByText('Type @ for templates or tracks · / for commands', { exact: true })).not.toBeInTheDocument();
  const rename = page.getByRole('button', { name: 'Rename track', exact: true });
  await expect.element(rename).toBeVisible();
  expect(rename.element().closest('header')).not.toBeNull();
  await expect.element(page.getByText(/Updated \d{4}-/)).not.toBeInTheDocument();
  expect(document.querySelector('[data-nc-report-empty]')).toBeNull();
  await settle();
  if (content === 'report') {
    const panel = document.querySelector('[data-nc-panel]')!;
    expect(getComputedStyle(panel).display).toBe('none');
    const workspace = panel.parentElement!.getBoundingClientRect();
    const composerBox = composer.element().getBoundingClientRect();
    expect(Math.abs((composerBox.left + composerBox.right - workspace.left - workspace.right) / 2)).toBeLessThan(2);
    await page.screenshot({ path: './__screenshots__/empty-audit/track-1440.png' });
  } else {
    await expect.element(page.getByRole('button', { name: 'Conversation Review', exact: true })).toBeVisible();
  }
  await composer.fill(firstMessage);
  await stage.getByRole('button', { name: 'Send', exact: true }).click();
  await expect.poll(() => requests.some(request => request.method === 'POST' && request.path.endsWith('/project-lead/messages'))).toBe(true);
  await client.invalidateQueries({ queryKey: queryKeys.harnessItems(lead.id) });
  await expect.element(stage.getByText(firstMessage, { exact: true })).toBeVisible();
  const followUp = 'Check the release owner';
  await composer.fill(followUp);

  published = true;
  await client.invalidateQueries({ queryKey: content === 'report' ? queryKeys.trackDetail(track.id) : queryKeys.trackViews(track.id) });
  const report = page.getByRole('region', { name: 'Track report', exact: true });
  await expect.element(report.getByRole('heading', { name: content === 'report' ? 'Initial plan' : 'Plan the release', exact: true })).toBeVisible();
  await expect.element(stage).not.toBeInTheDocument();
  const drawer = page.getByRole('complementary', { name: 'Lead', exact: true });
  await expect.element(drawer.getByText(firstMessage, { exact: true })).toBeVisible();
  await expect.element(drawer.getByRole('combobox', { name: 'Message', exact: true })).toHaveTextContent(followUp);
});

it('wraps an unbroken empty Track title within the centered composer column', async () => {
  await page.viewport(1440, 900);
  const title = '0123456789abcdef'.repeat(6);
  const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
  const track = fixtureTrack('project', { areaId: 'project-area', title, leadSessionId: lead.id });
  renderDailyFixture({ initial: '/track/project', reply: request => {
    if (request.path === '/api/v1/tracks/project') return sessionReply(fixtureSnapshot(track, [lead]));
    if (request.path.startsWith('/api/v1/sessions/project-lead/items?')) return sessionReply(sessionPage(lead));
    return undefined;
  } });

  const stage = page.getByRole('region', { name: 'Lead', exact: true });
  const heading = stage.getByRole('heading', { name: title, exact: true });
  const message = stage.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(heading).toBeVisible();
  await expect.element(message).toBeVisible();
  await settle();

  const headingElement = heading.element();
  const composer = message.element().closest('[data-nc-composer]')!;
  expect(headingElement.getBoundingClientRect().right).toBeLessThanOrEqual(composer.getBoundingClientRect().right);
  expect(headingElement.scrollWidth).toBeLessThanOrEqual(headingElement.clientWidth);
});

it('opens secondary conversations in a closable drawer on an empty Track', async () => {
  await page.viewport(1440, 900);
  const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
  const review = fixtureSession('project-review', 'project', { title: 'Review' });
  const track = fixtureTrack('project', { areaId: 'project-area', leadSessionId: lead.id });
  let sessions = [lead];
  const { client } = renderDailyFixture({ initial: '/track/project', reply: request => {
    if (request.path === '/api/v1/tracks/project') return sessionReply(fixtureSnapshot(track, sessions));
    if (request.path.startsWith('/api/v1/sessions/project-lead/items?')) return sessionReply(sessionPage(lead));
    if (request.path.startsWith('/api/v1/sessions/project-review/items?')) return sessionReply(sessionPage(review));
    return undefined;
  } });
  const stage = page.getByRole('region', { name: 'Lead', exact: true });
  const leadComposer = stage.getByRole('combobox', { name: 'Message', exact: true });
  await leadComposer.fill('/');
  await page.getByRole('option', { name: /^new / }).click();
  await userEvent.keyboard('{Enter}');

  const draft = page.getByRole('complementary', { name: 'Untitled', exact: true });
  await expect.element(draft).toBeVisible();
  await expect.element(stage).not.toBeInTheDocument();
  await draft.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(leadComposer).toBeVisible();

  sessions = [lead, review];
  await client.invalidateQueries({ queryKey: queryKeys.trackDetail(track.id) });
  await page.getByRole('button', { name: 'Conversation Review', exact: true }).click();
  const drawer = page.getByRole('complementary', { name: 'Review', exact: true });
  await expect.element(drawer).toBeVisible();
  await expect.element(stage).not.toBeInTheDocument();
  await drawer.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(leadComposer).toBeVisible();
});

it('shows Retry for a failed first report and presents the report after retrying', async () => {
  await page.viewport(390, 900);
  const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
  const track = fixtureTrack('project', { areaId: 'project-area', leadSessionId: lead.id });
  let published = false;
  let failed = false;
  const { client } = renderDailyFixture({
    initial: '/track/project',
    prepareReport: report => {
      if (report !== null && !failed) {
        failed = true;
        throw new Error('First report preparation failed');
      }
      return prepareReportDocument(report);
    },
    reply: request => {
      if (request.path === '/api/v1/tracks/project') {
        return sessionReply(fixtureSnapshot(track, [lead], published ? fixtureReport('# Initial plan', 2) : fixtureReport()));
      }
      if (request.path.startsWith('/api/v1/sessions/project-lead/items?')) return sessionReply(sessionPage(lead));
      return undefined;
    },
  });
  await expect.element(page.getByRole('heading', { name: 'This page becomes your living report' })).toBeVisible();
  await page.viewport(1440, 900);
  const stage = page.getByRole('region', { name: 'Lead', exact: true });
  await expect.element(stage.getByRole('combobox', { name: 'Message', exact: true })).toBeVisible();
  published = true;
  await client.invalidateQueries({ queryKey: queryKeys.trackDetail(track.id) });
  await expect.poll(() => failed).toBe(true);

  const report = page.getByRole('region', { name: 'Track report', exact: true });
  await expect.element(report.getByText('The report could not be loaded.')).toBeVisible();
  await expect.element(stage).not.toBeInTheDocument();
  await report.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect.element(report.getByRole('heading', { name: 'Initial plan', exact: true })).toBeVisible();
});

it('keeps the blank report and the existing Lead dock usable at 390px', async () => {
  await page.viewport(390, 900);
  const lead = fixtureSession('project-lead', 'project', { title: 'Lead', isLead: true });
  const track = fixtureTrack('project', { areaId: 'project-area', title: 'New project', leadSessionId: lead.id });
  const { requests } = renderDailyFixture({
    initial: '/track/project',
    reply: request => {
      if (request.path === '/api/v1/tracks/project') return sessionReply(fixtureSnapshot(track, [lead]));
      if (request.path.startsWith('/api/v1/sessions/project-lead/items?')) return sessionReply(sessionPage(lead));
      return undefined;
    },
  });
  await expect.poll(() => document.querySelector('[data-nc-report-empty]') !== null).toBe(true);
  await expect.element(page.getByRole('heading', { name: 'This page becomes your living report' })).toBeVisible();
  await expect.element(page.getByText(/Start typing below/)).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Start with Lead' })).not.toBeInTheDocument();
  await expect.element(page.getByText(/Updated \d{4}-/)).not.toBeInTheDocument();
  await expect.element(page.getByText('No tools yet')).not.toBeInTheDocument();
  expect(document.querySelector('[data-nc-report]')).toBeNull();
  expect(document.querySelector('[aria-label="Template protocol"]')).toBeNull();
  await settle();
  await page.screenshot({ path: './__screenshots__/empty-audit/track-390.png' });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'Start a conversation' })).toBeVisible();
  await expect.element(page.getByText('Write a message…', { exact: true })).toBeVisible();
  await expect.poll(() => requests.some(request => request.path.startsWith('/api/v1/sessions/project-lead/items?'))).toBe(true);
  expect(requests.every(request => request.method === 'GET')).toBe(true);
  expect(requests.some(request => request.path === '/api/v1/templates')).toBe(false);
});
