import { queryKeys } from './query-keys.ts';
import { useQuery } from '@tanstack/react-query';
import { useEffect,useSyncExternalStore } from 'react';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { systemInfoOperation } from '../../../../core/domain/system-upgrade.ts';
import { useConnectionStatus,useSystemUpgradeStore } from './connection-status.tsx';
import { runOperation } from './query-operation.ts';

const emptyStore = Object.freeze({ read: () => null, subscribe: () => () => {} });
export function useSystemUpgrade() {
  const store = useSystemUpgradeStore();
  return useSyncExternalStore(store?.subscribe ?? emptyStore.subscribe, store?.read ?? emptyStore.read);
}

/** Foreground reads reconcile missed events; the store rejects reads older than a WS observation. */
export function SystemUpgradeMonitor({ transport, unauthorized }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
}>) {
  const store = useSystemUpgradeStore();
  const connected = useConnectionStatus()?.connected;
  const { refetch } = useQuery({
    queryKey: queryKeys.systemInfo(),
    enabled: store !== null,
    queryFn: async ({ signal }) => {
      const revision = store?.revision();
      const info = await runOperation(transport, { ...systemInfoOperation(), signal, timeoutMs: 10_000 }, unauthorized);
      if (!signal.aborted) store?.acceptInfo(info, revision);
      return info;
    },
    refetchInterval: 5000,
    refetchIntervalInBackground: false,
    retry: false,
  });
  useEffect(() => {
    if (connected) void refetch();
  }, [connected, refetch]);
  return null;
}
