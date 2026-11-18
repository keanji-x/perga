import type { QueryClient } from '@tanstack/react-query';
import { ApiError } from '../../../../core/domain/failure-class.ts';

/** Query-client surface needed to replace an in-flight read before invalidating it. */
export interface QueryRefreshPort {
  cancelQueries(filters: { queryKey: readonly unknown[] }): Promise<unknown>;
  invalidateQueries(filters?: { queryKey?: readonly unknown[] }): unknown;
}

/**
 * TanStack may reuse an in-flight initial fetch after invalidation. Abort that request first, then
 * invalidate either the same query or the whole cache so the replacement read owns the final value.
 * A refresh is reconciliation: a read that fails shows on its query, and never escapes as a rejection.
 * Settles once the replacement read has.
 */
export function cancelThenInvalidate(
  client: QueryRefreshPort,
  cancelKey: readonly unknown[],
  invalidateKey: readonly unknown[] | null = cancelKey,
): Promise<void> {
  const invalidate = () => Promise.resolve(invalidateKey === null
    ? client.invalidateQueries()
    : client.invalidateQueries({ queryKey: invalidateKey })).catch(() => undefined);
  return client.cancelQueries({ queryKey: cancelKey }).then(invalidate, invalidate).then(() => undefined);
}

/** Refresh observed resources at the snapshot boundary and retire stale, unobserved data. */
export async function refreshSnapshotQueries(client: QueryClient, disposed: () => boolean): Promise<void> {
  await client.cancelQueries();
  if (disposed()) return;
  // Streamed fragments and cached history windows cannot survive a replay gap.
  client.removeQueries({ type: 'inactive' });
  await client.invalidateQueries({ refetchType: 'none' });
  if (disposed()) return;
  const active = client.getQueryCache().findAll({ type: 'active' });
  const reads = await Promise.allSettled(active.map(async query => {
    try {
      // Await the actual read, including a fetch paused until the network returns.
      await query.fetch();
      // A cancelled fetch may resolve to cached data that is still marked stale.
      if (query.isActive() && query.state.isInvalidated) throw new Error('Snapshot read was invalidated');
    } catch (error) {
      if (!query.isActive() || disposed()) return;
      if (error instanceof ApiError && error.failure.kind === 'http'
        && [403, 404, 410].includes(error.failure.status)) {
        // The authoritative read says this resource is gone or inaccessible.
        query.setState({ data: undefined, dataUpdatedAt: 0 });
        return;
      }
      throw error;
    }
  }));
  for (const read of reads) {
    if (read.status === 'rejected') throw read.reason;
  }
}
