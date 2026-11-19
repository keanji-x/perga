import { fixtureTrack, fixtureSnapshot, fixtureReport } from './session-contract-fixture.ts';
import { act, cleanup, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import * as markdown from '../../../../core/markdown/public.ts';
import type { TrackAggregateSnapshotDto } from '../../../../core/api/schemas.ts';
import { queryKeys } from '../providers/queries.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

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
