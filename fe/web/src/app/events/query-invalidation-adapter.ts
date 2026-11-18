// Turns reducer effects into TanStack cache operations using the shared resource addresses.

import { newestArea, toArea } from '../../../../core/domain/area.ts';
import type { PergaStreamDelta } from '../../../../core/api/schemas.ts';
import type { EventEffect } from '../../../../core/events/reducer.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { retireAreaQueries, type AreaQueryLifecyclePort } from '../providers/area-query-lifecycle.ts';
import { cancelThenInvalidate } from './query-refresh.ts';

export type SessionLiveToolState = Readonly<{
  callId: string;
  name: string | null;
  args: unknown;
  phase: string;
  isError: boolean;
}>;

export type SessionLiveBuffer = Readonly<{
  parts?: readonly Readonly<{ sequence: number; delta: string }>[];
  sessionId: string;
  turnId: string;
  text: string;
  thinking: string;
  tools: readonly SessionLiveToolState[];
  updatedAtMs: number;
  completed?: boolean;
}>;

export function applySessionDeltaToBuffer(
  previous: SessionLiveBuffer | undefined,
  delta: PergaStreamDelta,
): SessionLiveBuffer {
  const base: SessionLiveBuffer = previous !== undefined && previous.turnId === delta.turnId
    ? previous
    : {
      sessionId: delta.sessionId,
      turnId: delta.turnId,
      text: '',
      thinking: '',
      tools: [],
      updatedAtMs: delta.createdAtMs,
    };
  if (delta.kind === 'session.text_delta') {
    const parts = delta.sequence === undefined ? base.parts : [...(base.parts ?? []), { sequence: delta.sequence, delta: delta.delta }].slice(-128);
    return { ...base, text: base.text + delta.delta, parts, updatedAtMs: delta.createdAtMs };
  }
  if (delta.kind === 'session.thinking_delta') {
    return { ...base, thinking: base.thinking + delta.delta, updatedAtMs: delta.createdAtMs };
  }
  const existing = base.tools.findIndex((tool) => tool.callId === delta.callId);
  const nextTool: SessionLiveToolState = {
    callId: delta.callId,
    name: delta.name ?? (existing >= 0 ? base.tools[existing].name : null),
    args: delta.args ?? (existing >= 0 ? base.tools[existing].args : null),
    phase: delta.phase,
    isError: delta.isError,
  };
  const tools = existing >= 0
    ? base.tools.map((tool, index) => (index === existing ? nextTool : tool))
    : [...base.tools, nextTool];
  return { ...base, tools, updatedAtMs: delta.createdAtMs };
}

/** The slice of `QueryClient` the adapter is allowed to use. */
export interface QueryCachePort extends AreaQueryLifecyclePort {
  invalidateQueries(filters?: { queryKey?: readonly unknown[] }): unknown;
  resetQueries(filters: { queryKey: readonly unknown[] }): unknown;
  removeQueries(filters: { queryKey: readonly unknown[] }): unknown;
  getQueryData<T>(queryKey: readonly unknown[]): T | undefined;
  clear(): void;
}

/**
 * TanStack reuses an in-flight initial fetch even after `invalidateQueries`, and the stale response
 * then clears `isInvalidated`. Event-driven reads need a replacement
 * read started after the event, even when the transport ignores abort.
 */
function invalidateQuery(client: QueryCachePort, queryKey: readonly unknown[]): void {
  const cancelFirst = queryKey[0] === 'track' || queryKey[0] === 'track-conversations'
    || queryKey[0] === 'track-report' || queryKey[0] === 'conversation-initial-input'
    || queryKey[0] === 'harness-items' || queryKey[0] === 'planner-run'
    || queryKey[0] === 'track-backlinks' || queryKey[0] === 'plugins'
    || queryKey[0] === 'track-templates' || queryKey[0] === 'plugin-data'
    || queryKey[0] === 'track-terminals' || queryKey[0] === 'task-attempts'
    || (queryKey[0] === 'overlays' && queryKey[1] === 'track');
  if (!cancelFirst) {
    void client.invalidateQueries({ queryKey });
    return;
  }
  void cancelThenInvalidate(client, queryKey);
}

/**
 * Applies the effects of one reduction; stream lifecycle effects are the bridge's. Write-through
 * updates only an existing cached area; a missing row stays absent until the invalidation refetches.
 */
export function applyEventEffects(client: QueryCachePort, effects: readonly EventEffect[]): void {
  for (const effect of effects) {
    if (effect.type === 'clear-cache') {
      client.clear();
      continue;
    }
    if (effect.type === 'invalidate') {
      // A null key set is the reducer's "everything is suspect" signal after a replay.
      if (effect.keys === null) {
        // A replay boundary cannot certify retained windows behind the latest page.
        void client.resetQueries({ queryKey: queryKeys.transcriptPrefix() });
        void cancelThenInvalidate(client, queryKeys.overlaysByKind('track'), null);
        continue;
      }
      for (const key of effect.keys) {
        invalidateQuery(client, key);
      }
      continue;
    }
    if (effect.type === 'write-through') {
      for (const write of effect.writes) {
        if (write.mode === 'area-deleted') {
          retireAreaQueries(client, write.areaId);
          continue;
        }
        if (write.mode === 'reset-query') {
          void client.resetQueries({ queryKey: write.key });
          continue;
        }
        if (write.mode === 'session-delta') {
          const previous = client.getQueryData<SessionLiveBuffer>(write.key);
          client.setQueryData(write.key, applySessionDeltaToBuffer(previous, write.delta));
          continue;
        }
        if (write.mode === 'session-turn-completed') {
          const previous = client.getQueryData<SessionLiveBuffer>(write.key);
          if (previous !== undefined && previous.turnId === write.turnId) {
            client.setQueryData(write.key, { ...previous, completed: true });
          }
          continue;
        }
        const existing = client.getQueryData<readonly ReturnType<typeof toArea>[]>(write.key);
        if (existing === undefined || !existing.some((area) => area.id === write.value.id)) continue;
        const updated = toArea(write.value);
        client.setQueryData(write.key, existing.map((area) => area.id === updated.id
          ? newestArea(area, updated)
          : area));
      }
      continue;
    }
    if (effect.type === 'remove') {
      for (const key of effect.keys) {
        void client.removeQueries({ queryKey: key });
      }
    }
  }
}
