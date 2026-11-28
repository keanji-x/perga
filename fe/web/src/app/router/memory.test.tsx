import { act, cleanup, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';

import entriesHtml from '../../../../../src/Perga.Kernel/Templates/Memory/widgets/entries.html?raw';
import { invalidationPlanFor } from '../../../../core/events/invalidation-plan.ts';
import { applyEventEffects } from '../events/query-invalidation-adapter.ts';
import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { renderMemoryShortcutFixture } from './memory-shortcut-fixture.ts';
import { fixtureReport, fixtureSession, fixtureSnapshot, fixtureTrack, fixtureWidgetReport } from './session-contract-fixture.ts';

afterEach(cleanup);

const provenanceTrack = 'trk_review';

function renderMemory(initial = '/area/project-area/memory', staleBinding = false) {
  const report = fixtureWidgetReport([
    { title: 'Always Pinned', use: './widgets/entries.html', view: 'pinned' },
    { title: 'Notes & Topics', use: './widgets/entries.html', view: 'topics' },
  ]);
  const areaId = 'project-area';
  const areaTrackId = 'trk_area_memory';
  const globalTrackId = 'trk_global_memory';
  const areaLead = fixtureSession('ses_area_memory', areaTrackId, { isLead: true, title: 'Lead' });
  const globalLead = fixtureSession('ses_global_memory', globalTrackId, { isLead: true, title: 'Lead' });
  let ensures = 0;
  let updated = false;
  const harness = renderDailyFixture({ initial, reply: request => {
    if (request.path === '/api/v1/memory/global/track') {
      return sessionReply({ scopeKey: 'global', areaId: null, trackId: globalTrackId, leadSessionId: globalLead.id });
    }
    if (request.path === `/api/v1/memory/areas/${areaId}/track`) {
      const trackId = ++ensures === 1 && staleBinding ? 'trk_stale' : areaTrackId;
      return sessionReply({ scopeKey: `area:${areaId}`, areaId, trackId, leadSessionId: areaLead.id });
    }
    if (request.path === '/api/v1/tracks/trk_stale') {
      return { status: 404, statusText: 'Not Found', body: { error: 'Stale Planner Track' } };
    }
    if (request.path === `/api/v1/tracks/${provenanceTrack}`) {
      return sessionReply(fixtureSnapshot(fixtureTrack(provenanceTrack, { areaId, title: 'Original review' }), [],
        fixtureReport('# Review evidence\n\nOriginal review notes.')));
    }
    const lead = request.path.startsWith(`/api/v1/tracks/${areaTrackId}`) ? areaLead : globalLead;
    const isArea = lead === areaLead;
    const uri = `/api/v1/tracks/${lead.trackId}`;
    if (request.path === uri) {
      const metrics = [
        { label: 'Pinned', value: isArea ? updated ? '79 B / 1.2 KB' : '40 B / 1.2 KB' : '54 B / 820 B' },
        { label: 'Entries', value: '2' },
        ...(isArea ? [{ label: 'Global Inherited', value: '0' }] : []),
      ];
      return sessionReply(fixtureSnapshot(fixtureTrack(lead.trackId, {
        title: isArea ? 'Project Memory' : 'Global Memory', areaId: 'sys_memory', leadSessionId: lead.id,
        templateId: 'memory', templateTitle: 'Memory', templateHeaderView: { metrics },
      }), [lead], report));
    }
    if (request.path === uri + '/views') {
      const meta = { summary: '', tags: [], sources: [], updatedAtMs: Date.UTC(2026, 10, 24) };
      return sessionReply({
        templateId: 'memory', views: {
          pinned: { kind: 'pinned', entries: [{ ...meta, ...(isArea
            ? { ref: 'area/releases', title: updated ? 'Updated release checklist' : 'Release checklist', body: 'Run **smoke tests** before tagging.' }
            : { ref: 'global/writing', title: 'Writing preferences', body: 'Use plain language.' }) }] },
          topics: { kind: 'topics', entries: [{ ...meta, ...(isArea
            ? { ref: 'area/review', title: 'Review cadence', body: 'Review every Tuesday.' }
            : { ref: 'global/terminology', title: 'Common terminology', body: 'Use the team glossary.' }) }] },
        },
        widgets: { './widgets/entries.html': entriesHtml },
        flow: { rev: 0, nodes: [], archived: [] },
      });
    }
    if (request.path.startsWith(`/api/v1/sessions/${areaLead.id}/items?`)) {
      return sessionReply(sessionPage(areaLead, [sessionItem(areaLead.id, 1, 'Use @`memory:area/releases` and @`memory:global`.', 'user')]));
    }
    if (request.path === '/api/v1/tracks/daily') {
      const daily = fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' });
      return sessionReply(fixtureSnapshot(fixtureTrack('daily', { areaId: 'sys_daily', leadSessionId: daily.id }), [daily], fixtureReport('# Daily notes')));
    }
    if (request.path.startsWith('/api/v1/memory/mentions?')) {
      return sessionReply([{ ref: 'global/writing', scope: 'global', key: 'writing', title: 'Writing preferences',
        summary: null, pinned: true, insert: '@`memory:global/writing`' }]);
    }
    return undefined;
  } });
  return { ...harness, areaTrackId, update: () => { updated = true; } };
}

it.each([
  ['/area/project-area/memory', 'Project Memory', '40 B / 1.2 KB', '/api/v1/memory/areas/project-area/track'],
  ['/memory', 'Global Memory', '54 B / 820 B', '/api/v1/memory/global/track'],
])('renders %s through the native Track report and Planner panel', async (path, title, budget, ensurePath) => {
  const { requests } = renderMemory(path);
  await screen.findAllByRole('heading', { name: title });
  const report = within(await screen.findByRole('region', { name: 'Track report' }));
  const protocol = within(screen.getByRole('region', { name: 'Template protocol' }));
  expect(protocol.getByText(budget)).toBeTruthy();
  expect(protocol.queryByRole('button', { name: 'Rules' })).toBeNull();

  expect((await report.findByTitle('pinned')).getAttribute('srcdoc')).toBe(entriesHtml);
  expect((await report.findByTitle('topics')).getAttribute('srcdoc')).toBe(entriesHtml);
  expect(report.getByRole('heading', { name: 'Always Pinned' })).toBeTruthy();
  expect(report.getByRole('heading', { name: 'Notes & Topics' })).toBeTruthy();
  expect(report.queryByRole('heading', { name: 'Maintenance Flow' })).toBeNull();
  if (path !== '/memory') {
    expect(protocol.getByText('Global Inherited')).toBeTruthy();
  } else {
    expect(protocol.queryByText('Global Inherited')).toBeNull();
  }
  expect(report.queryByRole('textbox')).toBeNull();
  expect(report.queryByRole('button', { name: /edit|delete/i })).toBeNull();
  expect(screen.queryByRole('button', { name: /track actions for/i })).toBeNull();
  expect(screen.queryByRole('button', { name: 'Save to memory' })).toBeNull();
  expect(requests.filter(request => request.path.startsWith('/api/v1/memory/')).map(request => [request.method, request.path]))
    .toEqual([['POST', ensurePath]]);
  expect(screen.getByRole('button', { name: /Conversation Lead/ })).toBeTruthy();
});

it('sends the open Track conversation header button to the Lead without wiping a draft', async () => {
  const { requests } = renderMemoryShortcutFixture();
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Lead/ }));
  const input = await screen.findByRole('combobox', { name: 'Message' });
  await userEvent.type(input, 'Draft note');
  await userEvent.click(screen.getByRole('button', { name: 'Save to memory' }));
  await vi.waitFor(() => expect(requests.find(request => request.path === '/api/v1/sessions/project-lead/messages')).toBeTruthy());
  const message = requests.find(request => request.path === '/api/v1/sessions/project-lead/messages');
  expect(message?.method).toBe('POST');
  expect(message?.body).toHaveProperty('mode', 'default');
  expect(message?.body).toHaveProperty('content', expect.stringContaining('scope: "area" for project-specific takeaways and scope: "global" for cross-project preferences'));
  expect(input.textContent).toBe('Draft note');
  expect(requests.some(request => request.path === '/api/v1/memory/distill')).toBe(false);
});

it('asks the Today Lead to propose only to Global memory', async () => {
  const { requests } = renderMemoryShortcutFixture({ today: true });
  await userEvent.click(await screen.findByRole('button', { name: 'Save to memory' }));
  await vi.waitFor(() => expect(requests.find(request => request.path === '/api/v1/sessions/daily-planner/messages')?.body)
    .toHaveProperty('content', expect.stringContaining('perga_memory propose with scope: "global"')));
  expect(requests.find(request => request.path === '/api/v1/sessions/daily-planner/messages')?.body)
    .not.toHaveProperty('content', expect.stringContaining('scope: "area"'));
});

it('hides Save to memory in a shared Track conversation', async () => {
  renderMemoryShortcutFixture({ shared: true });
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Lead/ }));
  await screen.findByRole('button', { name: 'Session actions' });
  expect(screen.queryByRole('button', { name: 'Save to memory' })).toBeNull();
});

it('retries a rejected closed-Track request with the same Idempotency-Key and links to Area Memory', async () => {
  const { requests, router } = renderMemoryShortcutFixture({ closed: true, failFirstDistill: true });
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Lead/ }));
  await userEvent.click(await screen.findByRole('button', { name: 'Save to memory' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));
  expect(await screen.findByText('Sent to Area Memory Planner')).toBeTruthy();
  const distills = requests.filter(request => request.path === '/api/v1/memory/distill');
  expect(distills.map(({ method, body }) => ({ method, body }))).toEqual([
    { method: 'POST', body: { trackId: 'project' } },
    { method: 'POST', body: { trackId: 'project' } },
  ]);
  expect(distills[0]?.headers?.['Idempotency-Key']).toBeTruthy();
  expect(distills[1]?.headers?.['Idempotency-Key']).toBe(distills[0]?.headers?.['Idempotency-Key']);
  await userEvent.click(screen.getByRole('button', { name: 'Open memory' }));
  await vi.waitFor(() => expect(router.state.location.pathname).toBe('/area/project-area/memory'));
});

it('opens the native Planner and retains scoped mention pills', async () => {
  const { router } = renderMemory();
  await userEvent.click(await screen.findByRole('button', { name: /Conversation Lead/ }));
  await screen.findByRole('combobox', { name: 'Message' });
  expect((await screen.findByRole('link', { name: 'Area · releases' })).getAttribute('href')).toBe('/area/project-area/memory');
  expect(screen.getByRole('link', { name: 'Global Memory' }).getAttribute('href')).toBe('/memory');
  await userEvent.click(screen.getByRole('button', { name: 'Close conversation' }));
  expect(router.state.location.pathname).toBe('/area/project-area/memory');
});

it('routes a provenance intent from a Memory widget to the source Track', async () => {
  const { router } = renderMemory();
  const frame = await screen.findByTitle<HTMLIFrameElement>('topics');
  const post = vi.spyOn(frame.contentWindow!, 'postMessage');
  await vi.waitFor(() => {
    fireEvent(window, new MessageEvent('message', { source: frame.contentWindow, data: { type: 'perga:ready' } }));
    expect(post).toHaveBeenCalledWith(expect.objectContaining({ type: 'perga:data' }), '*');
  });
  fireEvent(window, new MessageEvent('message', {
    source: frame.contentWindow,
    data: { type: 'perga:intent', kind: 'open_track', trackId: provenanceTrack },
  }));
  await vi.waitFor(() => expect(router.state.location.pathname).toBe(`/track/${provenanceTrack}`));
  expect(await screen.findByRole('heading', { name: 'Review evidence' })).toBeTruthy();
});

it('refreshes the native protocol and report views on memory.updated', async () => {
  const { client, areaTrackId, update } = renderMemory();
  const report = within(await screen.findByRole('region', { name: 'Track report' }));
  const frame = await report.findByTitle<HTMLIFrameElement>('pinned');
  const post = vi.spyOn(frame.contentWindow!, 'postMessage');
  update();
  const plan = invalidationPlanFor({ ev: 'memory.updated', data: { scope: 'area', areaId: 'project-area', trackId: areaTrackId } });
  act(() => applyEventEffects(client, [{ type: 'invalidate', keys: plan.invalidate }]));
  expect(await screen.findByText('79 B / 1.2 KB')).toBeTruthy();
  await vi.waitFor(() => expect(post).toHaveBeenCalledWith(expect.objectContaining({
    data: { kind: 'pinned', entries: [expect.objectContaining({ title: 'Updated release checklist' })] },
  }), '*'));
});

it('re-ensures a stale Planner binding on Retry', async () => {
  const { requests } = renderMemory('/area/project-area/memory', true);
  await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));
  expect(await screen.findAllByRole('heading', { name: 'Project Memory' })).not.toHaveLength(0);
  expect(requests.filter(request => request.method === 'POST' && request.path.endsWith('/memory/areas/project-area/track'))).toHaveLength(2);
});

it('suggests Global Memory on a direct Daily Track route', async () => {
  const { requests } = renderMemory('/track/daily');
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: /Conversation Daily Planner conversation/ }));
  const field = await screen.findByRole('combobox', { name: 'Message' });
  await user.type(field, 'Use @memory:writing');
  expect(await screen.findByRole('option', { name: /^Writing preferences/ })).toBeTruthy();
  expect(requests.filter(request => request.path.startsWith('/api/v1/memory/mentions?')).map(request => request.path))
    .toContain('/api/v1/memory/mentions?q=writing');
  expect(requests.some(request => request.path.startsWith('/api/v1/memory/mentions?areaId='))).toBe(false);
});
