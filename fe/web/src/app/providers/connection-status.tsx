import type { WorkspaceFileOpenRequested } from '../../../../core/api/perga-v1.ts';
import { SystemUpgradeStore } from '../../../../core/domain/system-upgrade.ts';
import { createContext, useContext, useMemo, type ReactNode } from 'react';
import type { ConnectionState } from '../../systems/events/event-stream.ts';
import { useState } from '../../ui/state/public.ts';

export type ConnectionStatus = Readonly<{ connected: boolean; label: string; detail: string; retry?: () => void }>;
const StatusContext = createContext<ConnectionStatus | null>(null);
type FileOpenListener = (request: WorkspaceFileOpenRequested) => void;

function createFileOpenRequests() {
  const listeners = new Set<FileOpenListener>();
  return {
    publish(request: WorkspaceFileOpenRequested) {
      for (const listen of listeners) listen(request);
    },
    subscribe(listen: FileOpenListener) {
      listeners.add(listen);
      return () => { listeners.delete(listen); };
    },
  };
}

const StreamContext = createContext<Readonly<{
  publishConnection: (state: ConnectionState) => void;
  fileOpenRequests: ReturnType<typeof createFileOpenRequests>;
}> | null>(null);
export const ConnectionStatusScope = StatusContext.Provider;
export function useConnectionStatus() { return useContext(StatusContext); }
export function usePublishConnectionState() { return useContext(StreamContext)?.publishConnection ?? null; }
/** Requests are delivered to mounted consumers and have no replay buffer. */
export function useFileOpenRequests() { return useContext(StreamContext)?.fileOpenRequests ?? null; }

/** The event bridge publishes observations; this provider never starts a socket. */
export function LiveConnectionProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [state, setState] = useState<ConnectionState>('disconnected');
  const [fileOpenRequests] = useState(createFileOpenRequests);
  const stream = useMemo(() => ({ publishConnection: setState, fileOpenRequests }), [setState, fileOpenRequests]);
  const status = { connected: state === 'connected', label: state === 'connected' ? 'Connected' :
    state === 'connecting' ? 'Connecting…' : 'Disconnected', detail: '' };
  return <StreamContext.Provider value={stream}><ConnectionStatusScope value={status}>{children}</ConnectionStatusScope></StreamContext.Provider>;
}

/** Upgrade observations share the authenticated app lifetime with connection observations. */
const UpgradeContext = createContext<SystemUpgradeStore | null>(null);
export function SystemUpgradeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [store] = useState(() => new SystemUpgradeStore());
  return <UpgradeContext.Provider value={store}>{children}</UpgradeContext.Provider>;
}
export function useSystemUpgradeStore() { return useContext(UpgradeContext); }
