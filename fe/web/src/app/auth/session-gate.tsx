/** SessionGate sits outside ServerCompatGate so the whoami verdict lands before any route loader or the compatibility query can leak a preliminary 401. */
import { type QueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, type ReactNode } from 'react';
import { CurrentUserScope } from './current-user.tsx';
import { whoamiOperation, type SessionIdentity } from '../../../../core/api/auth.ts';
import { performApiRequest } from '../../../../core/api/client.ts';
import { resolveSessionProbe, type SessionProbeState } from '../../../../core/api/session.ts';
import type { AuthChanges } from './auth-changes.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { PENDING_TRACK_INITIAL_SENDS_KEY, TRACK_CREATE_DRAFTS_KEY } from '../../../../core/keys/storage.ts';
import type { SyncCursorPort } from '../../systems/events/cursor-port.ts';
import { useState } from '../../ui/state/public.ts';
import type { ProviderRuntime } from '../providers/public.tsx';

export type SessionProbeError = Extract<SessionProbeState<SessionIdentity>, { status: 'error' }>['error'];

export type SessionCleanupRuntime = Pick<ProviderRuntime, 'deleteDatabase' | 'idbDatabaseName'> &
  Readonly<{ submittedStorage?: (() => Storage) | undefined }>;
type SessionCursorStore = Pick<SyncCursorPort, 'clear'>;

/** Clears every artifact whose ownership ends with the current session. */
export function clearSessionArtifacts(
  client: QueryClient,
  cursorStore: SessionCursorStore,
  runtime: SessionCleanupRuntime,
): void {
  try { client.clear(); } catch { /* cleanup is best-effort */ }
  try { cursorStore.clear(); } catch { /* storage may be unavailable */ }
  try {
    const storage = runtime.submittedStorage?.();
    if (storage !== undefined) {
      storage.removeItem(PENDING_TRACK_INITIAL_SENDS_KEY);
      for (let index = storage.length - 1; index >= 0; index--) {
        const key = storage.key(index);
        if (key?.startsWith(`${TRACK_CREATE_DRAFTS_KEY}:`)) storage.removeItem(key);
      }
    }
  } catch { /* storage may be unavailable */ }
  try { runtime.deleteDatabase(runtime.idbDatabaseName); } catch { /* indexedDB may be unavailable */ }
}

export function SessionGate({ children, transport, unauthorized, authChanges, client, runtime, cursorStore, renderLogin, renderError }: Readonly<{
  children: ReactNode; transport: ApiTransportPort; unauthorized: UnauthorizedChannel; client: QueryClient;
  runtime: SessionCleanupRuntime; cursorStore: SessionCursorStore;
  authChanges?: AuthChanges;
  renderLogin: (retry: () => void) => ReactNode;
  renderError: (retry: () => void, error: SessionProbeError) => ReactNode;
}>) {
  const [state, setState] = useState<SessionProbeState<SessionIdentity>>({ status: 'unknown' });
  const epoch = useRef(0);
  const mounted = useRef(false);
  const activeProbe = useRef<AbortController | null>(null);
  const previousSession = useRef<SessionIdentity | null>(null);
  const probe = useCallback((hideCurrent = true) => {
    activeProbe.current?.abort();
    const controller = new AbortController();
    activeProbe.current = controller;
    const probeEpoch = ++epoch.current;
    if (hideCurrent) setState({ status: 'unknown' });
    // The probe's 401 is an expected session verdict, so it must not broadcast.
    void performApiRequest(transport, { ...whoamiOperation(), signal: controller.signal }, undefined).then((result) => {
      if (!mounted.current || epoch.current !== probeEpoch) return;
      activeProbe.current = null;
      const verdict = resolveSessionProbe(result);
      const previous = previousSession.current;
      if (verdict.status === 'unauthed' || (verdict.status === 'authed' && previous !== null
        && (previous.sessionId !== verdict.value.sessionId || previous.userId !== verdict.value.userId))) {
        clearSessionArtifacts(client, cursorStore, runtime);
      }
      if (verdict.status === 'authed') previousSession.current = verdict.value;
      if (verdict.status === 'unauthed') previousSession.current = null;
      setState(verdict);
    });
  }, [client, cursorStore, runtime, transport]);
  const retry = useCallback(() => probe(true), [probe]);
  useEffect(() => {
    mounted.current = true;
    probe(true);
    return () => {
      mounted.current = false;
      epoch.current += 1;
      activeProbe.current?.abort();
      activeProbe.current = null;
    };
  }, [probe]);
  useEffect(() => authChanges?.subscribe(() => probe(false)), [authChanges, probe]);
  useEffect(() => unauthorized.subscribe(() => {
    epoch.current += 1;
    activeProbe.current?.abort();
    activeProbe.current = null;
    previousSession.current = null;
    clearSessionArtifacts(client, cursorStore, runtime);
    setState({ status: 'unauthed' });
    authChanges?.publish();
  }), [authChanges, client, cursorStore, runtime, unauthorized]);
  if (state.status === 'unknown') return null;
  if (state.status === 'unauthed') return renderLogin(retry);
  if (state.status === 'error') return renderError(retry, state.error);
  return <CurrentUserScope key={state.value.sessionId} value={state.value}>{children}</CurrentUserScope>;
}
