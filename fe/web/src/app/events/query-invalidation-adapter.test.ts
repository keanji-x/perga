import { QueryClient,QueryObserver } from '@tanstack/react-query';
import { describe,expect,expectTypeOf,it,vi } from 'vitest';

import { wireEventSchema } from '../../../../core/api/schemas.ts';
import type { ApiRequest,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { AreaWire } from '../../../../core/domain/area.ts';
import type { CacheWrite } from '../../../../core/events/invalidation-plan.ts';
import { invalidationPlanFor } from '../../../../core/events/invalidation-plan.ts';
import type { EventEffect } from '../../../../core/events/reducer.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';
import { applyEventEffects,type QueryCachePort } from './query-invalidation-adapter.ts';

type Call = Readonly<{ op: 'reset' | 'invalidate' | 'remove' | 'set' | 'clear'; queryKey?: readonly unknown[] }>;

function recordingClient(initialAreas?: readonly ReturnType<typeof import('../../../../core/domain/area.ts').toArea>[]) {
  const calls: Call[] = [];
  const cache = new QueryClient().getQueryCache();
  let areas = initialAreas;
  const client: QueryCachePort = {
    getQueryCache: () => cache,
    cancelQueries: () => Promise.resolve(),
    resetQueries: filters => { calls.push({ op: 'reset', queryKey: filters.queryKey }); },
    invalidateQueries: (filters?: { queryKey?: readonly unknown[] }) => {
      calls.push({ op: 'invalidate', queryKey: filters?.queryKey });
    },
    removeQueries: (filters: { queryKey: readonly unknown[] }) => {
      calls.push({ op: 'remove', queryKey: filters.queryKey });
    },
    getQueryData: <T,>(key: readonly unknown[]) => key[0] === 'areas' ? areas as T | undefined : undefined,
    setQueryData: <T,>(key: readonly unknown[], value: T) => {
      calls.push({ op: 'set', queryKey: key });
      if (key[0] === 'areas') areas = value as typeof areas;
    },
    clear: () => { calls.push({ op: 'clear' }); },
  };
  return { calls, client, areas: () => areas };
}

describe('query invalidation adapter', () => {

  it('invalidates each planned key of an invalidate effect', async () => {
    const { calls, client } = recordingClient();
    applyEventEffects(client, [{ type: 'invalidate', keys: [['areas'], ['track', 'w1']] }]);
    await settleReads();
    expect(calls).toEqual([
      { op: 'invalidate', queryKey: ['areas'] },
      { op: 'invalidate', queryKey: ['track', 'w1'] },
    ]);
  });

  it('invalidates the whole cache for a null key set after canceling track overlays', async () => {
    const { calls, client } = recordingClient();
    applyEventEffects(client, [{ type: 'invalidate', keys: null }]);
    await Promise.resolve();
    expect(calls).toEqual([{ op: 'reset', queryKey: queryKeys.transcriptPrefix() }, { op: 'invalidate', queryKey: undefined }]);
  });

  it.each([
    ['mapped track overlay', [{ type: 'invalidate', keys: [['overlays', 'track']] }] as const],
    ['replay-complete full invalidation', [{ type: 'invalidate', keys: null }] as const],
  ])('aborts an active track-overlay read before starting its replacement for %s', async (_name, effects) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const starts: string[] = [];
    const signals: AbortSignal[] = [];
    const observer = new QueryObserver(client, {
      queryKey: queryKeys.overlaysByKind('track'),
      queryFn: ({ signal }) => {
        const index = signals.length;
        signals.push(signal);
        starts.push(`start-${index + 1}`);
        if (index > 0) return Promise.resolve([]);
        return new Promise<never>((_resolve, reject) => {
          signal.addEventListener('abort', () => {
            starts.push('abort-1');
            reject(new DOMException('aborted', 'AbortError'));
          }, { once: true });
        });
      },
    });
    const unsubscribe = observer.subscribe(() => undefined);
    await vi.waitFor(() => expect(signals).toHaveLength(1));

    applyEventEffects(client, effects);

    await vi.waitFor(() => expect(signals).toHaveLength(2));
    expect(signals[0]?.aborted).toBe(true);
    expect(starts).toEqual(['start-1', 'abort-1', 'start-2']);
    unsubscribe();
  });

  it('does not cancel an active card-overlay read when refreshing track overlays', async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const cardSignals: AbortSignal[] = [];
    const cardObserver = new QueryObserver(client, {
      queryKey: queryKeys.overlaysByKind('card'),
      queryFn: ({ signal }) => {
        cardSignals.push(signal);
        return new Promise<never>(() => undefined);
      },
    });
    const unsubscribe = cardObserver.subscribe(() => undefined);
    await vi.waitFor(() => expect(cardSignals).toHaveLength(1));

    applyEventEffects(client, [{ type: 'invalidate', keys: [['overlays', 'track']] }]);
    await Promise.resolve();

    expect(cardSignals[0]?.aborted).toBe(false);
    unsubscribe();
  });

  it('clears the cache for a clear-cache effect', () => {
    const { calls, client } = recordingClient();
    applyEventEffects(client, [{ type: 'clear-cache' }]);
    expect(calls).toEqual([{ op: 'clear' }]);
  });

  it('removes planned keys for a remove effect', () => {
    const { calls, client } = recordingClient();
    applyEventEffects(client, [{ type: 'remove', keys: [['track', 'w1']] }]);
    expect(calls).toEqual([{ op: 'remove', queryKey: ['track', 'w1'] }]);
  });

  it('ignores lifecycle effects, which are not cache work', () => {
    const { calls, client } = recordingClient();
    const effects: EventEffect[] = [
      { type: 'persist-cursor', id: 7 },
      { type: 'reconnect' },
    ];
    applyEventEffects(client, effects);
    expect(calls).toEqual([]);
  });

  it('write-through replaces one existing area and preserves every other row', () => {
    expectTypeOf<Extract<CacheWrite, { mode: 'replace-existing-area' }>['value']>().toEqualTypeOf<AreaWire>();
    const old = {
      id: 'c1', name: 'old', color: '#111', sort: 1, kind: 'user',
      defaultTemplateId: null, defaultCwd: null, createdAt: 10, updatedAt: 20,
    } as const;
    const other = {
      id: 'c2', name: 'other', color: '#222', sort: 2, kind: 'user',
      defaultTemplateId: null, defaultCwd: null, createdAt: 11, updatedAt: 21,
    } as const;
    const { calls, client, areas } = recordingClient([old, other]);
    applyEventEffects(client, [{ type: 'write-through', writes: [{
      key: ['areas'], mode: 'replace-existing-area',
      value: {
        id: 'c1', name: 'new', color: '#abc', sort: 3, kind: 'user',
        default_template_id: 'small-change', default_cwd: '/srv/work', created_at: 10, updated_at: 30,
      },
    }] }]);
    expect(areas()).toEqual([
      {
        id: 'c1', name: 'new', color: '#abc', sort: 3, kind: 'user',
        defaultTemplateId: 'small-change', defaultCwd: '/srv/work', createdAt: 10, updatedAt: 30,
      },
      other,
    ]);
    expect(calls).toEqual([{ op: 'set', queryKey: ['areas'] }]);
  });

  it('write-through ignores an older area event that arrives after a newer cache version', () => {
    const current = {
      id: 'c1', name: 'newer', color: '#111', sort: 1, kind: 'user',
      defaultTemplateId: 'small-change', defaultCwd: '/srv/new', createdAt: 10, updatedAt: 30,
    } as const;
    const { client, areas } = recordingClient([current]);
    applyEventEffects(client, [{ type: 'write-through', writes: [{
      key: ['areas'], mode: 'replace-existing-area',
      value: {
        id: 'c1', name: 'older', color: '#abc', sort: 3, kind: 'user',
        default_template_id: 'investigation', default_cwd: '/srv/old', created_at: 10, updated_at: 20,
      },
    }] }]);
    expect(areas()).toEqual([current]);
  });

  it('write-through applies an equal-version area event for historical replay compatibility', () => {
    const current = {
      id: 'c1', name: 'before', color: '#111', sort: 1, kind: 'user',
      defaultTemplateId: null, defaultCwd: null, createdAt: 10, updatedAt: 20,
    } as const;
    const { client, areas } = recordingClient([current]);
    applyEventEffects(client, [{ type: 'write-through', writes: [{
      key: ['areas'], mode: 'replace-existing-area',
      value: {
        id: 'c1', name: 'after', color: '#abc', sort: 3, kind: 'user',
        default_template_id: 'small-change', default_cwd: '/srv/work', created_at: 10, updated_at: 20,
      },
    }] }]);
    expect(areas()?.[0]).toMatchObject({
      name: 'after', defaultTemplateId: 'small-change', defaultCwd: '/srv/work', updatedAt: 20,
    });
  });

  it('write-through never creates a phantom area when the row is absent', () => {
    const existing = {
      id: 'c2', name: 'other', color: '#222', sort: 2, kind: 'user',
      defaultTemplateId: null, defaultCwd: null, createdAt: 11, updatedAt: 21,
    } as const;
    const { calls, client, areas } = recordingClient([existing]);
    applyEventEffects(client, [{ type: 'write-through', writes: [{
      key: ['areas'], mode: 'replace-existing-area',
      value: {
        id: 'missing', name: 'phantom', color: '#abc', sort: 3, kind: 'user',
        default_template_id: null, default_cwd: null, created_at: 10, updated_at: 30,
      },
    }] }]);
    expect(areas()).toEqual([existing]);
    expect(calls).toEqual([]);
  });

  it('write-through leaves an uninitialized area cache absent', () => {
    const { calls, client, areas } = recordingClient();
    applyEventEffects(client, [{ type: 'write-through', writes: [{
      key: ['areas'], mode: 'replace-existing-area',
      value: {
        id: 'missing', name: 'phantom', color: '#abc', sort: 3, kind: 'user',
        default_template_id: null, default_cwd: null, created_at: 10, updated_at: 30,
      },
    }] }]);
    expect(areas()).toBeUndefined();
    expect(calls).toEqual([]);
  });

  it('preserves effect order across a snapshot-required style batch', () => {
    const { calls, client } = recordingClient();
    applyEventEffects(client, [
      { type: 'clear-cache' },
      { type: 'invalidate', keys: [['areas']] },
      { type: 'remove', keys: [['track', 'w1']] },
    ]);
    expect(calls.map((call) => call.op)).toEqual(['clear', 'invalidate', 'remove']);
  });

  it('turns a real area.updated plan into an area-list invalidation', () => {
    const area = {
      id: 'c1', name: 'Area', color: '#fff', sort: 0, kind: 'user',
      default_template_id: null, default_cwd: null, created_at: 1, updated_at: 2,
    } as const;
    const plan = invalidationPlanFor({ ev: 'area.updated', data: area });
    const { calls, client } = recordingClient();
    applyEventEffects(client, [{ type: 'invalidate', keys: plan.invalidate }]);
    expect(calls).toEqual([{ op: 'invalidate', queryKey: queryKeys.areas() }]);
  });

  it('turns a real track.deleted plan into tree diagnostics refresh plus ordinary cleanup', async () => {
    const plan = invalidationPlanFor({
      ev: 'track.deleted',
      data: { id: 'w1', area_id: 'c1' },
    });
    const { calls, client } = recordingClient();
    applyEventEffects(client, [
      { type: 'invalidate', keys: plan.invalidate },
      { type: 'remove', keys: plan.remove },
    ]);
    await Promise.resolve();
    expect(calls).toEqual([
      { op: 'invalidate', queryKey: queryKeys.tracksInArea('c1') },
      { op: 'remove', queryKey: queryKeys.trackDetail('w1') },
      { op: 'remove', queryKey: queryKeys.trackReport('w1') },
      { op: 'remove', queryKey: queryKeys.trackViews('w1') },
      { op: 'invalidate', queryKey: queryKeys.overlaysByKind('track') },
      { op: 'invalidate', queryKey: queryKeys.trackReportPrefix() },
    ]);
  });
});

// Real production query options decode server metadata; the transport captures the server value
// at request start so releasing an obsolete response cannot accidentally read the new value.
function metadataRead(respectsAbort = false, trackId = 'track-1') {
  let title: string | null = 'Original';
  const reads: { request: ApiRequest; release: () => void }[] = [];
  const transport = { send: (request: ApiRequest) => new Promise<ApiTransportResponse>((resolve, reject) => {
    const body = { track: { id: trackId, areaId: 'area-1', createdAtMs: 1, updatedAtMs: 2 },
      report: {}, tasks: [], openAsks: [], sessions: [{ id: 'card-1', trackId, title, isLead: true,
        provider: 'codex', status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2 }] };
    reads.push({ request, release: () => resolve({ status: 200, statusText: 'OK', body }) });
    if (respectsAbort) request.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')), { once: true });
  }) };
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const options = trackDetailQueryOptions(transport, trackId, unauthorized);
  return { reads, options, setTitle: (next: string | null) => { title = next; },
    title: (client: QueryClient) => {
      const data = client.getQueryData(options.queryKey);
      return (data as { sessions: { title: string | null }[] })?.sessions[0]?.title;
    } };
}

function inputEvent(kind: 'deleted' | 'edited' | 'cleared' | 'persisted' | 'presentation', trackId = 'track-1') {
  return wireEventSchema.parse({ ev: 'session.updated', data: { track_id: trackId,
    session: { id: 'card-1', trackId, provider: 'codex', status: 'idle', contextUsage: null, title: kind, createdAtMs: 1, updatedAtMs: 2 } } });
}

function refreshInput(client: QueryClient, kind: Parameters<typeof inputEvent>[0], trackId = 'track-1') {
  applyEventEffects(client, [{ type: 'invalidate', keys: invalidationPlanFor(inputEvent(kind, trackId)).invalidate }]);
}

// Let cancellation/invalidation continuations and a held transport response settle without timers
// that could cause a query's retry/poll policy to supply an unrelated second event.
async function settleReads() { for (let i = 0; i < 20; i++) await Promise.resolve(); }

for (const surface of ['detail'] as const) {
  for (const respectsAbort of [false, true]) {
    it.each(['deleted', 'edited', 'cleared', 'persisted', 'presentation'] as const)(
      `replaces initial ${surface} metadata for %s (transport abort=${respectsAbort})`, async kind => {
        const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const read = metadataRead(respectsAbort);
        const observer = new QueryObserver<unknown>(client, read.options);
        const unsubscribe = observer.subscribe(() => undefined);
        try {
          expect(read.reads).toHaveLength(1);
          expect(client.getQueryData(read.options.queryKey)).toBeUndefined();
          const next = kind === 'edited' || kind === 'persisted' ? 'New input' : null;
          read.setTitle(next);
          refreshInput(client, kind);
          await settleReads();
          read.reads[1]?.release();
          await settleReads();
          // Obsolete response arrives last, including when the transport ignores abort.
          read.reads[0].release();
          await settleReads();
          expect({ reads: read.reads.length, title: read.title(client), invalidated: client.getQueryState(read.options.queryKey)?.isInvalidated }).toEqual({ reads: 2, title: next, invalidated: false });
          expect(read.reads[0].request.signal?.aborted).toBe(true);
        } finally { unsubscribe(); client.clear(); }
      },
    );
  }

  it(`keeps adjacent ${surface} input events scoped and the latest replacement authoritative`, async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const read = metadataRead();
    const foreign = metadataRead(false, 'foreign');
    const release = new QueryObserver<unknown>(client, read.options).subscribe(() => undefined);
    const releaseForeign = new QueryObserver<unknown>(client, foreign.options).subscribe(() => undefined);
    try {
      read.setTitle('Edited');
      refreshInput(client, 'edited');
      await settleReads();
      read.setTitle(null);
      refreshInput(client, 'deleted');
      refreshInput(client, 'cleared'); // Same-tick adjacent events also share only a post-event read.
      await settleReads();
      read.reads.at(-1)?.release();
      await settleReads();
      for (const stale of read.reads.slice(0, -1).reverse()) stale.release();
      foreign.reads[0].release();
      await settleReads();
      expect(read.reads).toHaveLength(3);
      expect(read.title(client)).toBeNull();
      expect(foreign.reads).toHaveLength(1);
      expect(foreign.reads[0].request.signal?.aborted).toBe(false);
      expect(foreign.title(client)).toBe('Original');
      expect(client.getQueryCache().getAll().map(query => query.queryKey)).toEqual([read.options.queryKey, foreign.options.queryKey]);
    } finally { release(); releaseForeign(); client.clear(); }
  });

  it(`marks inactive ${surface} metadata stale without eagerly reading it`, async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const read = metadataRead();
    try {
      const first = client.fetchQuery<unknown>(read.options);
      read.reads[0].release();
      await first;
      read.setTitle(null);
      refreshInput(client, 'deleted');
      await settleReads();
      expect(read.reads).toHaveLength(1);
      expect(client.getQueryState(read.options.queryKey)?.isInvalidated).toBe(true);
      const next = client.fetchQuery<unknown>(read.options);
      read.reads[1].release();
      await next;
      expect(read.title(client)).toBeNull();
    } finally { client.clear(); }
  });
}
