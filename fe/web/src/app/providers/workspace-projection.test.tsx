import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { type ReactNode } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type Area } from '../../../../core/domain/area.ts';
import type { TrackOverlayItemDto } from '../../../../core/api/perga-v1.ts';
import { NEUTRAL_ACTIVITY, type Track } from '../../../../core/domain/track.ts';
import { queryKeys } from './query-keys.ts';
import { useWorkspace } from './workspace-queries.ts';

afterEach(cleanup);

const area = (id: string): Area => ({ id, name: id, color: '#123456', sort: 0, kind: 'user',
  defaultTemplateId: null, defaultCwd: null, createdAt: 1, updatedAt: 1 });
const track = (id: string, areaId = 'a'): Track => ({ ...NEUTRAL_ACTIVITY,
  id, areaId, title: id, sort: 0, cwd: '/tmp', isolation: 'none', agentCwd: '/tmp', pinnedAt: null, closedAt: null,
  createdAt: 1, updatedAt: 1 });

function setup(areas: readonly Area[], rows: readonly Track[], overlays: readonly TrackOverlayItemDto[]) {
  const client = new QueryClient({ defaultOptions: { queries: { retryDelay: 0, retry: false, staleTime: Infinity, structuralSharing: false } } });
  client.setQueryData(queryKeys.areas(), areas);
  for (const owner of areas) client.setQueryData(queryKeys.tracksInArea(owner.id), rows.filter(row => row.areaId === owner.id));
  client.setQueryData(queryKeys.overlaysByKind('track'), overlays);
  const transport: ApiTransportPort = { send: () => Promise.resolve({ status: 500, statusText: 'Unavailable', body: { error: 'Read unavailable' } }) };
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const view = renderHook(() => useWorkspace(transport, unauthorized), {
    wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  return { ...view, client };
}

function activity(id: string, at: number): TrackOverlayItemDto {
  return {
    trackId: id,
    areaId: 'a',
    isWorking: false,
    openAskCount: 0,
    pendingApprovalCount: 0,
    hasBlockedSession: false,
    workspaceFailed: false,
    notifiableSessions: [{ sessionId: `s-${id}`, lastNotifiableTurnAtMs: at }],
    updatedAtMs: at,
  };
}

it('reads overlay membership linearly across the actual workspace projection', () => {
  const rows = Array.from({ length: 40 }, (_, index) => track(`track-${index}`));
  let reads = 0;
  const overlays = rows.map((row, index) => {
    const value = activity(row.id, index + 2);
    Object.defineProperty(value, 'trackId', { enumerable: true, get: () => { reads++; return row.id; } });
    return value;
  });
  const view = setup([area('a')], rows, overlays);
  try {
    expect(view.result.current.tracks.map(row => row.recentAt)).toEqual(rows.map((_, index) => index + 2));
    expect(reads).toBeLessThanOrEqual(overlays.length * 4);
  } finally { view.unmount(); view.client.clear(); }
});

it('keeps data projections stable across shell renders and failed refetches, but updates real inputs', async () => {
  const a = area('a'); const b = area('b');
  const view = setup([a, b], [track('one'), track('two'), track('other', 'b')], [activity('two', 20)]);
  try {
    const before = view.result.current;
    expect(before.tracksByArea.get('a')?.map(row => row.id)).toEqual(['two', 'one']);
    view.rerender(); view.rerender();
    expect(view.result.current.tracks).toBe(before.tracks);
    expect(view.result.current.tracksByArea).toBe(before.tracksByArea);
    act(() => { view.result.current.retryTracks('a'); });
    await waitFor(() => expect(view.result.current.trackErrorsByArea.get('a')).toBeInstanceOf(Error));
    expect(view.result.current.tracks).toBe(before.tracks);
    expect(view.result.current.tracksByArea).toBe(before.tracksByArea);
    act(() => { view.client.setQueryData(queryKeys.overlaysByKind('track'), [activity('one', 30)]); });
    await waitFor(() => expect(view.result.current.tracksByArea.get('a')?.map(row => row.id)).toEqual(['one', 'two']));
    expect(view.result.current.tracks).not.toBe(before.tracks);
    expect(view.result.current.tracks.find(row => row.id === 'two')?.recentAt).toBeNull();
    act(() => { view.client.setQueryData(queryKeys.areas(), [b, a]); });
    await waitFor(() => expect(view.result.current.tracks.map(row => row.id)).toEqual(['other', 'one', 'two']));
    act(() => { view.client.setQueryData(queryKeys.tracksInArea('b'), [track('renamed', 'b')]); });
    await waitFor(() => expect(view.result.current.tracks[0].id).toBe('renamed'));
    act(() => { view.client.setQueryData(queryKeys.areas(), [a]); });
    await waitFor(() => expect(view.result.current.tracksByArea.has('b')).toBe(false));
  } finally { view.unmount(); view.client.clear(); }
});

it('keeps a loaded Area usable while another read is pending and then fails', async () => {
  const client = new QueryClient({ defaultOptions: { queries: { retryDelay: 0, retry: false, staleTime: Infinity } } });
  client.setQueryData(queryKeys.areas(), [area('a'), area('b')]);
  client.setQueryData(queryKeys.overlaysByKind('track'), []);
  let fail!: () => void;
  const pending = new Promise<never>((_, reject) => { fail = () => reject(new Error('Area b unavailable')); });
  const transport: ApiTransportPort = { send: request => request.path.includes('/b/tracks') ? pending : Promise.resolve({
    status: 200, statusText: 'OK', body: [{ workspaceDriver: 'direct', workspaceSpec: {}, id: 'one', areaId: 'a', title: 'One', sortOrder: 0, cwd: '/tmp',
      pinnedAtMs: null, closedAtMs: null, createdAtMs: 1, updatedAtMs: 1 }],
  }) };
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const view = renderHook(() => useWorkspace(transport, unauthorized), {
    wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  try {
    await waitFor(() => expect(view.result.current.tracks.map(row => row.id)).toEqual(['one']));
    expect(view.result.current.tracksLoadingByArea.get('b')).toBe(true);
    const loaded = view.result.current.tracks;
    act(() => { fail(); });
    await waitFor(() => expect(view.result.current.trackErrorsByArea.get('b')).toBeInstanceOf(Error));
    expect(view.result.current.tracksLoadingByArea.get('b')).toBe(false);
    expect(view.result.current.tracks).toBe(loaded);
  } finally { view.unmount(); client.clear(); }
});
