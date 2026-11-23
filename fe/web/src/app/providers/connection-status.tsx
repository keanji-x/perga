import { SystemUpgradeStore } from '../../../../core/domain/system-upgrade.ts';
import { MESSAGES } from '../../../../core/domain/messages.ts';
import { createContext, useContext, type ReactNode } from 'react';
import type { ConnectionState } from '../../systems/events/event-stream.ts';
import { useState } from '../../ui/state/public.ts';

export type ConnectionStatus = Readonly<{ connected: boolean; label: string; detail: string; retry?: () => void }>;
const StatusContext = createContext<ConnectionStatus | null>(null);
const StreamContext = createContext<((state: ConnectionState) => void) | null>(null);
export const ConnectionStatusScope = StatusContext.Provider;
export function useConnectionStatus() { return useContext(StatusContext); }
export function usePublishConnectionState() { return useContext(StreamContext); }

/** The event bridge publishes observations; this provider never starts a socket. */
export function LiveConnectionProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [state, setState] = useState<ConnectionState>('disconnected');
  const status = { connected: state === 'connected', label: state === 'connected' ? MESSAGES.connection.connected :
    state === 'connecting' ? MESSAGES.connection.connecting : MESSAGES.connection.disconnected, detail: '' };
  return <StreamContext.Provider value={setState}><ConnectionStatusScope value={status}>{children}</ConnectionStatusScope></StreamContext.Provider>;
}

/** Upgrade observations share the authenticated app lifetime with connection observations. */
const UpgradeContext = createContext<SystemUpgradeStore | null>(null);
export function SystemUpgradeProvider({ children }: Readonly<{ children: ReactNode }>) {
  const [store] = useState(() => new SystemUpgradeStore());
  return <UpgradeContext.Provider value={store}>{children}</UpgradeContext.Provider>;
}
export function useSystemUpgradeStore() { return useContext(UpgradeContext); }
