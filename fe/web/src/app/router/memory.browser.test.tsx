import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';

import entriesHtml from '../../../../../templates/memory/widgets/entries.html?raw';
import { sessionItem, sessionPage, sessionReply } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession, fixtureSnapshot, fixtureTrack, fixtureWidgetReport } from './session-contract-fixture.ts';

afterEach(async () => { cleanup(); await page.viewport(1280, 800); });

declare module 'vitest/browser' {
  interface BrowserCommands {
    readMemoryWidget(view: 'pinned' | 'topics', filter?: string): Promise<{
      entries: number; expanded: number; headings: string[]; refs: string[]; strong?: string;
      body?: string; sources?: string; links: { href: string; target: string; rel: string }[];
      injectedElements: number; scrollable: boolean;
    }>;
  }
}

const longBody = 'Long review paragraph. '.repeat(150) + 'Full body ends here.';

function renderMemory(initial = '/area/project-area/memory') {
  const report = fixtureWidgetReport([
    { title: 'Pinned', use: './widgets/entries.html', view: 'pinned' },
    { title: 'Notes / Topics', use: './widgets/entries.html', view: 'topics' },
    { title: 'Maintenance Flow', use: 'builtin:flow', view: 'flow' },
  ]);
  return renderDailyFixture({ initial, reply: request => {
    const global = request.path.includes('/global/') || request.path.includes('trk_global') || request.path.includes('ses_global');
    const scope = global ? 'global' : 'area';
    const areaId = global ? null : 'project-area';
    const trackId = `trk_${scope}`;
    const lead = fixtureSession(`ses_${scope}`, trackId, { isLead: true, title: 'Lead' });
    if (request.path === `/api/v1/memory/${global ? 'global' : 'areas/project-area'}/track`) {
      return sessionReply({ scopeKey: global ? 'global' : `area:${areaId}`, areaId, trackId, leadSessionId: lead.id });
    }
    if (request.path === `/api/v1/tracks/${trackId}`) {
      return sessionReply(fixtureSnapshot(fixtureTrack(trackId, {
        areaId: 'sys_memory', title: global ? 'Global Memory' : 'Project Memory', leadSessionId: lead.id,
        templateId: 'memory', templateTitle: 'Memory', templateHeaderView: { metrics: [
          { label: 'Pinned', value: global ? '54 B / 820 B' : '40 B / 1.2 KB' },
          { label: 'Entries', value: '103' },
          ...(!global ? [{ label: 'Global Inherited', value: '1' }] : []),
        ] },
      }), [lead], report));
    }
    if (request.path === `/api/v1/tracks/${trackId}/views`) {
      const meta = { summary: 'Separate summary', tags: ['review'], sources: ['Review handbook'],
        updatedAtMs: Date.UTC(2026, 10, 24), repeatedSummary: false };
      return sessionReply({ templateId: 'memory', views: {
        pinned: { kind: 'pinned', entries: [
          { ...meta, ref: `${scope}/releases`, title: 'Release checklist', body: 'Run **smoke tests** before tagging.' },
          { ...meta, ref: `${scope}/runbook`, title: 'Runbook', body: 'Follow all steps in the runbook.' },
        ] },
        topics: { kind: 'topics', entries: Array.from({ length: 101 }, (_, index) => ({
          ...meta, ref: `${scope}/review-${index}`, title: `Review cadence ${index}`,
          body: index === 100
            ? longBody + '\n\n<img src=x> [unsafe](javascript:alert(1)) [handbook](https://example.com/review)'
            : 'Use the **review checklist**.',
        })) },
      }, widgets: { './widgets/entries.html': entriesHtml }, flow: { rev: 0, nodes: [], archived: [] } });
    }
    if (request.path.startsWith(`/api/v1/sessions/${lead.id}/items?`)) {
      return sessionReply(sessionPage(lead, [sessionItem(lead.id, 1, 'See @`memory:global` before release.', 'user')]));
    }
    if (request.path.startsWith('/api/v1/memory/mentions?areaId=project-area&')) {
      return sessionReply([
        { scope: 'area', key: 'releases', ref: 'area/releases', title: 'Release checklist', summary: null, pinned: true },
        { scope: 'global', key: 'writing', ref: 'global/writing', title: 'Writing preferences', summary: null, pinned: false },
      ].map(entry => ({ ...entry, insert: `@\`memory:${entry.ref}\`` })));
    }
    return undefined;
  } });
}

it.each([
  ['/area/project-area/memory', 'Project Memory', '40 B / 1.2 KB', 'area/releases'],
  ['/memory', 'Global Memory', '54 B / 820 B', 'global/releases'],
])('renders %s with native report widgets and no note editing controls', async (path, title, budget, reference) => {
  await page.viewport(1440, 900);
  renderMemory(path);
  await expect.element(page.getByRole('heading', { name: title })).toBeVisible();
  const protocol = page.getByRole('region', { name: 'Template protocol' });
  await expect.element(protocol.getByText(budget)).toBeVisible();
  const report = page.getByRole('region', { name: 'Track report' });
  expect(await commands.readMemoryWidget('pinned')).toMatchObject({
    entries: 2, expanded: 2, headings: ['Release checklist', 'Runbook'],
    refs: [reference, reference.replace('/releases', '/runbook')], strong: 'smoke tests',
  });
  expect(await commands.readMemoryWidget('topics')).toMatchObject({ entries: 101, expanded: 0, scrollable: true });
  await expect.poll(() => report.getByTitle('topics').query()?.getAttribute('style')).toContain('block-size: 2400px');
  const opened = await commands.readMemoryWidget('topics', 'Review cadence 100');
  expect(opened).toMatchObject({
    entries: 101, expanded: 1, headings: ['Review cadence 100'],
    sources: 'Sources · Review handbook', injectedElements: 0,
    links: [{ href: 'https://example.com/review', target: '_blank', rel: 'noopener noreferrer' }],
  });
  expect(opened.body).toContain(longBody);
  expect(opened.body).toContain('<img src=x>');
  expect(report.getByRole('alert').query()).toBeNull();
  await expect.element(report.getByRole('heading', { name: 'Maintenance Flow' })).toBeVisible();
  expect(report.getByRole('textbox').query()).toBeNull();
  expect(report.getByRole('button', { name: /edit|delete/i }).query()).toBeNull();
});

it('uses the right-side Planner panel with Area and Global Memory mentions', async () => {
  await page.viewport(1440, 900);
  const { requests } = renderMemory();
  const planner = page.getByRole('complementary').getByRole('button', { name: /Conversation Lead/ });
  await expect.element(planner).toBeVisible();
  expect(requests.filter(request => request.path === '/api/v1/memory/areas/project-area/track').map(request => request.method)).toEqual(['POST']);
  await planner.click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await expect.element(field).toBeVisible();
  await field.click();
  await userEvent.keyboard('@');
  const picker = page.getByRole('listbox', { name: 'Mention' });
  await picker.getByRole('option', { name: /^Memory/ }).click();
  await expect.poll(() => picker.getByRole('option').all().map(option => option.element().textContent?.trim()))
    .toEqual([expect.stringMatching(/^Release checklist/), expect.stringMatching(/^Writing preferences/)]);
  await picker.getByRole('option', { name: /^Release checklist/ }).click();
  await expect.element(field.getByText('Area · releases')).toBeVisible();
  await page.getByRole('link', { name: 'Global Memory', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'Global Memory' })).toBeVisible();
});

it('opens a native mobile outline anchor on the Area Memory URL', async () => {
  await page.viewport(390, 844);
  const { router } = renderMemory('/area/project-area/memory?panel=outline');
  await expect.element(page.getByRole('heading', { name: 'Outline', exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Maintenance Flow/ }).click();
  await expect.poll(() => router.state.location.href).toBe('/area/project-area/memory#flow-heading-h1');
});
