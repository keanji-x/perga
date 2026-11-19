import '../../styles/entry.css';
import { act, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page } from 'vitest/browser';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { queryKeys } from '../providers/queries.ts';
import { historyRows, type History } from '../conversations/history.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';

const transcriptKey = (id: string) => [...queryKeys.harnessItems(id), 'conversation_v1'];
afterEach(cleanup);

it('keeps both session histories and drafts while switching and refreshes only the latest window', async () => {
  await page.viewport(1400, 900);
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  for (const id of ['daily-planner', 'second-planner']) view.items.set(id,
    Array.from({ length: 120 }, (_, index) => sessionItem(id, index + 1, `${id} reply ${index}. ${'Reading evidence. '.repeat(20)}`)));
  const open = (name: string) => page.getByRole('button', { name, exact: true }).click();
  const loaded = (id: string) => historyRows(view.client.getQueryData<History>(transcriptKey(id))).length;
  await open('Conversation Daily Planner conversation');
  await expect.poll(() => loaded('daily-planner')).toBe(60);
  await page.getByRole('combobox', { name: 'Message' }).fill('Draft A');
  await commands.wheelScroll('[data-nc-drawer-scroll]', -100000);
  await commands.wheelScroll('[data-nc-drawer-scroll]', -100000);
  await expect.poll(() => loaded('daily-planner')).toBe(120);
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await open('Conversation Other conversation');
  await expect.poll(() => loaded('second-planner')).toBe(60);
  await page.getByRole('combobox', { name: 'Message' }).fill('Draft B');
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  const before = view.requests.length;
  view.items.get('daily-planner')!.push(sessionItem('daily-planner', 121, 'Fresh reply after returning.'));
  await open('Conversation Daily Planner conversation');
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('Draft A');
  await expect.element(page.getByText('Fresh reply after returning.', { exact: true })).toBeInTheDocument();
  await expect.poll(() => loaded('daily-planner')).toBe(121);
  expect(view.requests.slice(before).filter(request => request.path.includes('beforeId='))).toHaveLength(0);
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await open('Conversation Other conversation');
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('Draft B');
});

it('cancels a closing v1 items read and rejects its late body before fetching fresh history', async () => {
  await page.viewport(1400, 900);
  let release: ((response: ApiTransportResponse) => void) | undefined;
  const held = new Promise<ApiTransportResponse>(resolve => { release = resolve; });
  let reads = 0;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path.includes('/sessions/second-planner/items?') && !request.path.includes('&view=') && ++reads === 1) return held;
    return undefined;
  } });
  view.items.set('second-planner', [sessionItem('second-planner', 1, 'Fresh history.')]);
  try {
    await page.getByRole('button', { name: 'Conversation Other conversation', exact: true }).click();
    await expect.element(page.getByText('Loading conversation…')).toBeVisible();
    await expect.poll(() => reads).toBe(1);
    const first = view.requests.find(request => request.path.includes('/sessions/second-planner/items?') && !request.path.includes('&view='))!;
    await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
    expect(first.signal?.aborted).toBe(true);
    await act(async () => {
      release?.(sessionReply(sessionPage(view.sessions[1], [sessionItem('second-planner', 1, 'Obsolete late history.')])));
      await held;
    });
    expect(view.client.getQueryData(transcriptKey('second-planner'))).toBeUndefined();
    await page.getByRole('button', { name: 'Conversation Other conversation', exact: true }).click();
    await expect.element(page.getByText('Fresh history.', { exact: true })).toBeVisible();
    expect(page.getByText('Obsolete late history.', { exact: true }).elements()).toHaveLength(0);
    expect(reads).toBe(2);
  } finally { release?.(sessionReply(sessionPage(view.sessions[1]))); }
});

it('retains expanded Track A → B → A histories without requesting earlier pages on layout', async () => {
  await page.viewport(1400, 900);
  const project = fixtureTrack('project', { areaId: 'project-area', title: 'Project evidence', leadSessionId: 'project-planner' });
  const projectSession = fixtureSession('project-planner', 'project', { isLead: true, title: 'Project conversation' });
  const projectItems = Array.from({ length: 120 }, (_, index) => sessionItem(projectSession.id, index + 1, `Project reply ${index}.`));
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.path === '/api/v1/tracks/project') return sessionReply(fixtureSnapshot(project, [projectSession]));
    if (request.path === '/api/v1/tracks/project/sessions') return sessionReply([projectSession]);
    if (request.path.includes('/sessions/project-planner/items?')) return sessionReply(sessionPage(projectSession, projectItems));
    return undefined;
  } });
  view.items.set('daily-planner', Array.from({ length: 120 }, (_, index) => sessionItem('daily-planner', index + 1, `Daily reply ${index}.`)));
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByText('Daily reply 119.', { exact: true })).toBeVisible();
  const navigate = async (trackId: string) => {
    await act(async () => { await view.router.navigate({ to: '/track/$trackId', params: { trackId }, search: { panel: 'conversations' } }); });
  };
  await navigate('project');
  await page.getByRole('button', { name: /Conversation Project conversation/ }).click();
  await expect.element(page.getByText('Project reply 119.', { exact: true })).toBeVisible();
  await navigate('daily');
  await expect.element(page.getByRole('complementary', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  await expect.element(page.getByText('Daily reply 119.', { exact: true })).toBeVisible();
  await navigate('project');
  await expect.element(page.getByRole('complementary', { name: 'Project conversation', exact: true })).toBeVisible();
  await act(async () => { await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))); });
  expect(view.requests.filter(request => request.path.includes('beforeId='))).toHaveLength(0);
  expect(historyRows(view.client.getQueryData<History>(transcriptKey('daily-planner')))).toHaveLength(60);
  expect(historyRows(view.client.getQueryData<History>(transcriptKey('project-planner')))).toHaveLength(60);
});
