import { useEffect, useSyncExternalStore } from 'react';
import { systemInfoOperation } from '../../../../core/domain/system-upgrade.ts';
import { runOperation } from './queries.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { useConnectionStatus, useSystemUpgradeStore } from './connection-status.tsx';

const emptyStore = Object.freeze({ read: () => null, subscribe: () => () => {} });
export function useSystemUpgrade() {
  const store = useSystemUpgradeStore();
  return useSyncExternalStore(store?.subscribe ?? emptyStore.subscribe, store?.read ?? emptyStore.read);
}

/** Authenticated app assembly owns polling. A connection transition restarts the read immediately. */
export function SystemUpgradeMonitor({ transport, unauthorized }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
}>) {
  const store = useSystemUpgradeStore();
  const connected = useConnectionStatus()?.connected;
  useEffect(() => {
    if (!store) return;
    const controller = new AbortController();
    let pending = false;
    async function refresh() {
      if (pending || !store) return;
      pending = true;
      const revision = store.revision();
      try {
        const info = await runOperation(transport, { ...systemInfoOperation(), signal: controller.signal, timeoutMs: 10_000 }, unauthorized);
        if (!controller.signal.aborted) store.acceptInfo(info, revision);
      } catch {
        // Keep the last observation while the Host restarts; polling retries the read.
      } finally { pending = false; }
    }
    void refresh();
    const interval = setInterval(() => { void refresh(); }, 5000);
    return () => { controller.abort(); clearInterval(interval); };
  }, [store, transport, unauthorized, connected]);
  return null;
}
