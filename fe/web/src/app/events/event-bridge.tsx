import { useSystemUpgradeStore } from '../providers/connection-status.tsx';
// The React glue that owns exactly one configured event stream: mounted inside `ServerCompatGate`,
// the only `start()` caller in the tree; `configure()` is side-effect free.

import type { QueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';

import { refreshSnapshotQueries } from './query-refresh.ts';
import type { InvalidationContext } from '../../../../core/events/invalidation-plan.ts';
import { initialEventState, reduceEventFrame, type EventState } from '../../../../core/events/reducer.ts';
import type { SyncCursorPort } from '../../systems/events/cursor-port.ts';
import type { UnconfiguredEventStream } from '../../systems/events/event-stream.ts';
import { applyEventEffects } from './query-invalidation-adapter.ts';
import { trackLookupContext } from './track-lookup.ts';
import { usePublishConnectionState } from '../providers/connection-status.tsx';

export type EventBridgeProps = Readonly<{
  client: QueryClient;
  /** An unconfigured stream, created and owned by the caller — never a module singleton. */
  stream: UnconfiguredEventStream;
  syncEventVersion: number;
  dbInstanceId: string;
  cursor: SyncCursorPort;
  /** Lets card-scoped events resolve their owning track; defaults to "unknown". */
  context?: InvalidationContext;
}>;

/** Renders an inert marker so a contract test can assert where in the tree the bridge sits. */
export function EventBridge({ client, stream, syncEventVersion, dbInstanceId, cursor, context }: EventBridgeProps) {
  const upgradeStore = useSystemUpgradeStore();
  const publishConnectionState = usePublishConnectionState();
  useEffect(() => {
    if (publishConnectionState === null) return;
    const release = stream.onConnectionState(publishConnectionState);
    return () => { release(); publishConnectionState('disconnected'); };
  }, [stream, publishConnectionState]);
  // The connection depends only on stream identity and the protocol ceiling; everything else is read
  // through a ref so fresh prop identities can never tear down and reopen the socket.
  const latest = useRef({ client, cursor, context, dbInstanceId });
  useEffect(() => {
    latest.current = { client, cursor, context, dbInstanceId };
  }, [client, cursor, context, dbInstanceId]);

  useEffect(() => {
    latest.current.cursor.adopt(latest.current.dbInstanceId);
    let state: EventState = initialEventState(syncEventVersion, latest.current.cursor.read());
    // configure() only freezes version/topics; nothing connects until start().
    const configured = stream.configure({ syncEventVersion, topics: ['*'] });
    let disposed = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    async function recoverSnapshot(tipId: number) {
      const { client, cursor } = latest.current;
      try {
        await refreshSnapshotQueries(client, () => disposed);
      } catch {
        if (!disposed) retry = setTimeout(() => { void recoverSnapshot(tipId); }, 2000);
        return;
      }
      if (disposed) return;
      state = initialEventState(syncEventVersion, tipId);
      cursor.write(tipId);
      configured.start();
    }
    const unsubscribe = stream.onFrame((frame) => {
      const current = latest.current;
      if (frame.type === 'event' && frame.event.ev === 'system.upgrade_status' && frame.meta.eventVersion <= syncEventVersion) {
        upgradeStore?.acceptStatus(frame.event.data);
      }
      const reduction = reduceEventFrame(state, frame, current.context ?? trackLookupContext(current.client));
      state = reduction.state;
      for (const effect of reduction.effects) {
        if (effect.type === 'persist-cursor') current.cursor.write(effect.id);
        else if (effect.type === 'refresh-snapshot') {
          configured.stop();
          void recoverSnapshot(effect.tipId);
        }
        else if (effect.type === 'reconnect') { configured.stop(); configured.start(); }
        else applyEventEffects(current.client, [effect]);
      }
    });
    configured.start();
    return () => {
      disposed = true;
      clearTimeout(retry);
      unsubscribe();
      configured.stop();
    };
  }, [stream, syncEventVersion, upgradeStore]);

  return <span data-nc-event-bridge="" hidden />;
}
