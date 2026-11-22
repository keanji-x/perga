import { act,cleanup,fireEvent,screen,waitFor } from '@testing-library/react';
import { afterEach,beforeEach,expect,it,vi } from 'vitest';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import * as markdown from '../../../../core/markdown/public.ts';
import { queryKeys } from '../providers/queries.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { useReadReceipts } from '../providers/read-receipts.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureReport,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';

const SOURCE = '# Prepared report\n\nStored **evidence** with a [reference](https://github.com/example/project/issues/1).';
function setup() {
  const fixture = renderDailyFixture({ initial: '/track/daily', reply: request => {
    if (request.path === '/api/v1/tracks/daily') return { status: 200, statusText: 'OK', body: fixtureSnapshot(fixtureTrack('daily', { title: 'Report preparation' }), [], fixtureReport(SOURCE)) };
    if (/^\/api\/v1\/tracks\/(daily|project)\/report$/.test(request.path)) return { status: 200, statusText: 'OK', body: { taskDiagnostics: [] } };
    if (/^\/api\/tracks\/(daily|project)\/backlinks$/.test(request.path)) return { status: 200, statusText: 'OK', body: { backlinks: [], truncated: false, skipped_sources: 0 } };
    return undefined;
  } });
  return fixture;
}
const clients: ReturnType<typeof setup>[] = [];
beforeEach(() => { vi.spyOn(window, 'scrollTo').mockImplementation(() => {}); });
afterEach(() => { cleanup(); for (const fixture of clients.splice(0)) fixture.client.clear(); vi.restoreAllMocks(); document.getElementById('root')?.remove(); });

it('does not reparse a mounted report when unrelated Track detail changes', async () => {
  const parse = vi.spyOn(markdown, 'parse');
  const fixture = setup(); clients.push(fixture);
  await screen.findByRole('heading', { name: 'Prepared report', level: 2 }, { timeout: 5_000 });
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0)); parse.mockClear();
  await act(() => fixture.client.setQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('daily'), current => current === undefined ? current : ({
    ...current, track: { ...current.track, title: 'Renamed without changing report' },
  })));
  await screen.findAllByText('Renamed without changing report');
  expect(parse.mock.calls.filter(([source]) => source === SOURCE)).toHaveLength(0);
  expect(screen.getByRole('heading', { name: 'Prepared report' })).toBeTruthy();
});

it('reuses immutable report preparation after a cached Track return', async () => {
  const parse = vi.spyOn(markdown, 'parse');
  const fixture = setup(); clients.push(fixture);
  await screen.findByRole('heading', { name: 'Prepared report' }, { timeout: 5_000 });
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0));
  await act(async () => { await fixture.router.navigate({ to: '/track/project' }); });
  await screen.findByRole('heading', { name: 'Project result' }, { timeout: 5_000 });
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0)); parse.mockClear();
  await act(async () => { await fixture.router.navigate({ to: '/track/daily' }); });
  await screen.findByRole('heading', { name: 'Prepared report' }, { timeout: 5_000 });
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0));
  expect(parse.mock.calls.filter(([source]) => source === SOURCE)).toHaveLength(0);
});

it('prepares a changed report once and keeps its new content and destinations', async () => {
  const parse = vi.spyOn(markdown, 'parse');
  const fixture = setup(); clients.push(fixture);
  await screen.findByRole('heading', { name: 'Prepared report' }, { timeout: 5_000 });
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0)); parse.mockClear();
  const changed = '# Changed report\n\nFresh [reference](./fresh.md). <script>bad()</script>';
  await act(() => fixture.client.setQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail('daily'), current => current === undefined ? current : ({
    ...current, report: fixtureReport(changed, 2),
  })));
  await screen.findByRole('heading', { name: 'Changed report' }, { timeout: 5_000 });
  expect(screen.queryByRole('heading', { name: 'Prepared report' })).toBeNull();
  expect(screen.getByRole('button', { name: 'reference' }).getAttribute('title')).toBe('fresh.md');
  expect(document.querySelector('script')).toBeNull();
  expect(parse.mock.calls.filter(([source]) => source === changed)).toHaveLength(1);
});

it('does not retry a failed report acknowledgement on receipt refetch', async () => {
  const receipts = createReadReceiptsKernel({ fail: true, writeDelayMs: 10, receipts: [{ kind: 'track', targetId: 'daily', readThroughMs: 0, manualUnread: true }] });
  receipts.capWrites(4);
  const fixture = renderDailyFixture({ initial: '/track/daily', receipts }); clients.push(fixture);
  await screen.findByRole('region', { name: 'Track report' });
  await waitFor(() => expect(receipts.posts().length).toBeGreaterThan(0));
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 300)); });
  expect(receipts.posts()).toHaveLength(1);
});

it('keeps a visible report manually unread after a later receipt update', async () => {
  const receipts = createReadReceiptsKernel({ writeDelayMs: 10, receipts: [{ kind: 'track', targetId: 'daily', readThroughMs: 0, manualUnread: true }] });
  receipts.capWrites(5);
  function MarkUnread() {
    const channel = useReadReceipts();
    return <button onClick={() => { void channel.markUnread('track', 'daily'); }}>Mark report unread</button>;
  }
  const fixture = renderDailyFixture({ initial: '/track/daily', receipts, routeProbe: <MarkUnread /> }); clients.push(fixture);
  await screen.findByRole('region', { name: 'Track report' });
  await waitFor(() => expect(receipts.posts()).toHaveLength(1));
  await waitFor(() => expect(fixture.client.isFetching()).toBe(0));
  const before = receipts.posts().length;
  fireEvent.click(screen.getByRole('button', { name: 'Mark report unread' }));
  await waitFor(() => expect(receipts.stored('track', 'daily')?.manualUnread).toBe(true));
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.readReceipts() }); });
  await act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  expect(receipts.posts()).toHaveLength(before + 1);
  expect(receipts.stored('track', 'daily')?.manualUnread).toBe(true);
});
