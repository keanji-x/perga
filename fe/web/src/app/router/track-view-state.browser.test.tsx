import { fixtureTrack, fixtureSnapshot } from './session-contract-fixture.ts';
import { prepareReportDocument, type PreparedReport, type ReportPreparationPort } from '../../features/report/document/preparation.ts';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { act, cleanup, render } from '@testing-library/react';
import { commands, page } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import type { TrackAggregateSnapshotDto } from '../../../../core/api/perga-v1.ts';
import { trackReportLinkUrl, type TrackReport } from '../../../../core/domain/report.ts';
import { filePathFromLocation } from './navigation.ts';
import { queryKeys } from '../providers/query-keys.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { RecentFiles } from '../../features/report/recent-files/public.tsx';
import { renderPage } from '../../features/track/page/test-fixtures.tsx';
import '../../styles/entry.css';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

function setup(prepareReport: ReportPreparationPort = prepareReportDocument, initial = '/track/a', withProgress = false) {
  const tracks = ['a', 'b'].map(id => fixtureTrack(id, { areaId: 'area', title: `Track ${id}` }));
  const reportFor = (revision = 1) => ({ docRev: revision, summary: '', leadingComment: '', blocks: [
    ...(withProgress ? [{ id: 'progress-task', rev: 1, kind: 'task', markdown: '', payload: { key: 'progress-task', kind: 'codex', declared_by: 'user', ready: true, goal: 'Check progress' } }] : []),
    ...Array.from({ length: 50 }, (_, i) => ({ id: `paragraph-${i}`, kind: 'prose', rev: revision, payload: null,
      markdown: `Paragraph ${i}. A long report to read and return to.${revision === 1 ? '' : ` Revision ${revision}.`}${i === 0 ? `\n\n[Jump to paragraph](${trackReportLinkUrl('a')}#paragraph-40) and [Read file](README.md)` : ''}`,
    })),
  ] });
  const serverReports = new Map(tracks.map(track => [track.id, reportFor()]));
  const transport: ApiTransportPort = { async send(request) {
    await Promise.resolve();
    let body: unknown = [];
    if (request.path === '/api/v1/areas') body = [{ id: 'area', name: 'Work', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === '/api/v1/areas/area/tracks?includeClosed=true') body = tracks;
    if (request.path === '/api/settings') body = {};
    for (const track of tracks) {
      if (request.path === `/api/v1/tracks/${track.id}`) body = fixtureSnapshot(track, [], serverReports.get(track.id));
      if (request.path === `/api/v1/tracks/${track.id}/report`) body = { taskDiagnostics: [] };
    }
    return { status: 200, statusText: 'OK', body };
  } };
  const runtime = bootTestCardRuntime();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport, transport, client, cards: runtime,
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }), onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: [initial] }) });
  const container = document.createElement('div');
  container.id = 'root';
  document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>, { container });
  return { router, client, serverReports, reportFor };
}

async function settle() {
  for (let i = 0; i < 6; i += 1) await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
}

function reportPage() { return document.querySelector<HTMLElement>('[data-nc-track-page]')!; }

it('resumes independent report positions through real sidebar navigation', async () => {
  await page.viewport(1440, 900);
  const { router } = setup();
  async function reportReady(trackId: string) {
    // Cached preparation and a mounted shell do not imply current report admission.
    // Page restoration runs synchronously in the report commit's layout effects.
    await expect.poll(() => {
      const body = reportPage();
      const region = body?.querySelector('section[aria-label="Track report"]');
      return {
        path: router.state.location.pathname,
        bodyMatches: body?.querySelector('button[aria-label^="Track actions for "]')?.getAttribute('aria-label') === `Track actions for Track ${trackId}`,
        busy: region?.getAttribute('aria-busy'),
        committed: region?.querySelector('[data-nc-report]') != null,
      };
    }).toEqual({ path: `/track/${trackId}`, bodyMatches: true, busy: 'false', committed: true });
  }
  await reportReady('a');
  reportPage().scrollTop = 700;
  await settle();
  expect(reportPage().scrollTop).toBe(700);
  await page.getByRole('button', { name: /^Track Track b\b/ }).click();
  await reportReady('b');
  expect(reportPage().scrollTop).toBe(0);
  reportPage().scrollTop = 350;
  await settle();
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await reportReady('a');
  expect(reportPage().scrollTop).toBe(700);


});

it('keeps recent files as typed entries within Related', async () => {
  await page.viewport(1280, 900);
  renderPage({ recentFiles: <RecentFiles inline paths={['README.md']} onOpen={() => undefined} /> });
  await page.getByRole('button', { name: 'Related', exact: true }).click();
  const entry = page.getByRole('button', { name: 'Open README.md', exact: true });
  await expect.element(entry).toBeVisible();
  expect(entry.element().textContent).toContain('Recent');
  const icon = entry.element().querySelector('svg');
  expect(icon).not.toBeNull();
  expect(icon!.getAttribute('aria-hidden')).toBe('true');
  expect(icon!.getBoundingClientRect().width).toBeGreaterThan(0);
  expect(icon!.getBoundingClientRect().height).toBeGreaterThan(0);
});

function controlledPreparation() {
  let held = false;
  const jobs: { report: TrackReport | null; resolve: (prepared: PreparedReport) => void }[] = [];
  const execute: ReportPreparationPort = report => held
    ? new Promise<PreparedReport>(resolve => { jobs.push({ report, resolve }); }) : prepareReportDocument(report);
  return { execute, jobs, hold() { held = true; }, async release(index: number) {
    await act(async () => { jobs[index].resolve(prepareReportDocument(jobs[index].report)); await Promise.resolve(); });
  } };
}

async function replaceReport(client: QueryClient, serverReports: ReturnType<typeof setup>['serverReports'], revision: number) {
  const report = serverReports.get('a');
  if (report === undefined) throw new Error('Missing fixture Track a');
  const replacement = { ...report, docRev: revision, blocks: report.blocks.map(block => block.kind !== 'prose' ? block : ({ ...block, rev: revision,
    markdown: block.markdown.replace(/return to\.(?: Revision \d+\.)?/, `return to. Revision ${revision}.`),
  })) };
  serverReports.set('a', replacement);
  await act(() => client.setQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('a'), detail => detail === undefined ? detail : ({ ...detail, report: replacement })));
}

it('restores the saved report position only after delayed current content mounts', async () => {
  await page.viewport(1440, 900);
  const control = controlledPreparation(); const { router, client, serverReports } = setup(control.execute);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to.', { exact: true })).toBeVisible();
  await settle(); reportPage().scrollTop = 700; await settle();
  await page.getByRole('button', { name: /^Track Track b\b/ }).click(); await settle();
  control.hold(); await replaceReport(client, serverReports, 2);
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await expect.poll(() => control.jobs.length).toBe(1);
  expect(router.state.location.pathname.endsWith('/track/a')).toBe(true);
  expect(document.querySelector('[data-nc-report]')).toBeNull();
  expect(reportPage().scrollTop).toBe(0);
  await control.release(0);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to. Revision 2.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBe(700);
});

it('lets a reader gesture during pending content override deferred report restoration', async () => {
  await page.viewport(1440, 900);
  const control = controlledPreparation(); const { client, serverReports } = setup(control.execute);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to.', { exact: true })).toBeVisible();
  await settle(); reportPage().scrollTop = 700; await settle();
  await page.getByRole('button', { name: /^Track Track b\b/ }).click(); await settle();
  control.hold(); await replaceReport(client, serverReports, 2);
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await expect.poll(() => control.jobs.length).toBe(1);
  const wheel = new Promise<EventTarget | null>(resolve => { document.addEventListener('wheel', event => resolve(event.target), { capture: true, once: true }); });
  await commands.wheelScroll('[data-nc-track-page]', 160, { x: 200, y: 200 });
  const wheelTarget = await wheel;
  expect(wheelTarget instanceof Node && reportPage().contains(wheelTarget)).toBe(true);
  await control.release(0);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to. Revision 2.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBe(0);
});

it('serves pending and repeated anchor navigation after current content and does not replay it on refresh', async () => {
  await page.viewport(1440, 900);
  const control = controlledPreparation(); const { router, client, serverReports } = setup(control.execute);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to.', { exact: true })).toBeVisible();
  await act(() => router.navigate({ to: '/track/a', hash: 'paragraph-40' }));
  await settle(); expect(reportPage().scrollTop).toBeGreaterThan(700);
  reportPage().scrollTop = 0; await settle();
  control.hold(); await replaceReport(client, serverReports, 2);
  await expect.poll(() => control.jobs.length).toBe(1);
  await control.release(0);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to. Revision 2.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBe(0);
  await replaceReport(client, serverReports, 3);
  await expect.poll(() => control.jobs.length).toBe(2);
  act(() => { (page.getByRole('button', { name: 'Jump to paragraph', exact: true }).element() as HTMLButtonElement).click(); });
  await settle(); expect(reportPage().scrollTop).toBe(0);
  await control.release(1);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to. Revision 3.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBeGreaterThan(700);
});

it('keeps the saved reading position when a pending header action is used', async () => {
  await page.viewport(1440, 900);
  const control = controlledPreparation(); const { client, serverReports } = setup(control.execute);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to.', { exact: true })).toBeVisible();
  await settle(); reportPage().scrollTop = 700; await settle();
  await page.getByRole('button', { name: /^Track Track b\b/ }).click(); await settle();
  control.hold(); await replaceReport(client, serverReports, 2);
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await expect.poll(() => control.jobs.length).toBe(1);
  await page.getByRole('button', { name: 'Rename track', exact: true }).click();
  await expect.element(page.getByRole('textbox', { name: 'Track title', exact: true })).toBeVisible();
  await control.release(0);
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to. Revision 2.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBe(700);
});

it('does not replay a served report hash when a same-Track panel destination changes', async () => {
  await page.viewport(1440, 900);
  const { router } = setup(prepareReportDocument, '/track/a#paragraph-40');
  await expect.element(page.getByText('Paragraph 49. A long report to read and return to.', { exact: true })).toBeVisible();
  await settle(); expect(reportPage().scrollTop).toBeGreaterThan(700);
  reportPage().scrollTop = 100; await settle();
  // Activate the actual report file link: its owner pushes a file entry retaining hash.
  act(() => { (page.getByRole('button', { name: 'Read file', exact: true }).element() as HTMLButtonElement).click(); });
  await expect.poll(() => filePathFromLocation(router.state.location)).toBe('README.md');
  await settle(); expect(reportPage().scrollTop).toBe(100);
  const file = page.getByRole('region', { name: 'File README.md', exact: true }).element();
  await act(() => file.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })));
  await expect.poll(() => filePathFromLocation(router.state.location)).toBeNull();
  await settle(); expect(reportPage().scrollTop).toBe(100);
  await act(() => router.navigate({ to: '/track/a', search: { card: 'a-card-7' }, hash: true }));
  await settle(); expect(reportPage().scrollTop).toBe(100);
  await act(() => router.navigate({ to: '/track/a', search: {}, hash: true }));
  await settle(); expect(reportPage().scrollTop).toBe(100);
});

it('remembers Progress expansion and collapse independently through sidebar navigation', async () => {
  await page.viewport(1440, 900);
  setup(prepareReportDocument, '/track/a', true);
  const progress = () => document.querySelector<HTMLElement>('[data-nc-work-inventory] section[aria-label="Progress"]')!;
  const toggle = () => page.getByRole('button', { name: 'Progress', exact: true });
  await expect.element(toggle()).toBeVisible();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false');
  await toggle().click();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('true');
  await page.getByRole('button', { name: /^Track Track b\b/ }).click();
  await expect.element(toggle()).toBeVisible();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false');
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await expect.element(toggle()).toBeVisible();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('true');
  await toggle().click();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false');
  await page.getByRole('button', { name: /^Track Track b\b/ }).click();
  await expect.element(toggle()).toBeVisible();
  await toggle().click();
  await page.getByRole('button', { name: /^Track Track a\b/ }).click();
  await expect.element(toggle()).toBeVisible();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false');
  await page.getByRole('button', { name: /^Track Track b\b/ }).click();
  await expect.element(toggle()).toBeVisible();
  expect(progress().querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('true');
});
