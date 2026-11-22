import { QueryClient } from '@tanstack/react-query';
import { RouterProvider,type AnyRouter } from '@tanstack/react-router';
import { StrictMode, useMemo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel,type UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { TrackReport } from '../../../../core/domain/report.ts';
import { IDB_DB_NAME } from '../../../../core/keys/storage.ts';
import { LoginPage } from '../../features/auth/login-page/public.tsx';
import type { PreparedReport } from '../../features/report/document/preparation.ts';
import { createCardHost,createCardRegistry } from '../../systems/cards/public.js';
import { runWorkerTask } from '../../systems/worker-task/public.ts';
import { DepthFeedback } from '../../ui/motion/depth-feedback.tsx';
import { bootCards } from '../cards.ts';
import { createBrowserEventComposition } from '../composition.ts';
import { createBrowserCursorStore } from '../events/browser-cursor-store.ts';
import { EventBridge } from '../events/event-bridge.tsx';
import { SystemUpgradeProvider } from '../providers/connection-status.tsx';
import { AppProviders,type ProviderRuntime } from '../providers/public.tsx';
import { serverVersionOperation } from '../providers/server-queries.ts';
import { runOperation } from '../providers/query-operation.ts';
import { createRecentFileHistory } from '../providers/recent-files.ts';
import { SystemUpgradeMonitor } from '../providers/system-upgrade.tsx';
import { createFetchTransport } from '../providers/transport.ts';
import { createUiPreferences } from '../providers/ui-preferences.tsx';
import { createAppRouter } from '../router/public.tsx';
import { createAuthChanges, type AuthChanges } from './auth-changes.ts';
import { loginWithTransport } from './login.ts';
import { LogoutGate } from './logout-gate.tsx';
import { clearSessionArtifacts,SessionGate, type SessionCleanupRuntime, type SessionProbeError } from './session-gate.tsx';

export function ProductionApp({ transport, unauthorized, client, runtime, cursorStore, router, renderEventBridge,
  renderLogin, renderError, authChanges, }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel; client: QueryClient; runtime: ProviderRuntime & SessionCleanupRuntime;
  cursorStore: Parameters<typeof AppProviders>[0]['cursorStore']; router: AnyRouter;

  renderEventBridge?: Parameters<typeof AppProviders>[0]['renderEventBridge'];
  authChanges?: AuthChanges;
  renderLogin: (retry: () => void) => ReactNode;
  renderError: (retry: () => void, error: SessionProbeError) => ReactNode;
}>) {
  const content = <><SystemUpgradeMonitor transport={transport} unauthorized={unauthorized} /><RouterProvider router={router} /></>;
  return <StrictMode><SystemUpgradeProvider><DepthFeedback />
    <LogoutGate transport={transport} onComplete={() => {
      clearSessionArtifacts(client, cursorStore, runtime);
      authChanges?.publish();
      runtime.reload();
    }}>
      <SessionGate transport={transport} unauthorized={unauthorized} client={client} authChanges={authChanges}
        runtime={runtime} cursorStore={cursorStore} renderLogin={renderLogin} renderError={renderError}>
        <AppProviders client={client} runtime={runtime} cursorStore={cursorStore} renderEventBridge={renderEventBridge}>
          {content}
        </AppProviders>
      </SessionGate>
    </LogoutGate>
  </SystemUpgradeProvider></StrictMode>;
}

function renderSessionError(retry: () => void, error: SessionProbeError) {
  const insecure = error.kind === 'http' && error.code === 'insecure_transport_required';
  return <main>
    <p role="alert">{insecure ? error.message : 'Could not check your session.'}</p>
    {insecure && error.nextStep && <p>{error.nextStep}</p>}
    <button type="button" onClick={retry}>Try again</button>
  </main>;
}

function SessionEventBridge({ createStream, client, syncEventVersion, dbInstanceId, cursor }: Readonly<{
  createStream: () => ReturnType<typeof createBrowserEventComposition>['stream'];
  client: QueryClient; syncEventVersion: number; dbInstanceId: string;
  cursor: ReturnType<typeof createBrowserCursorStore>;
}>) {
  const stream = useMemo(() => createStream(), [createStream]);
  return <EventBridge client={client} stream={stream} syncEventVersion={syncEventVersion}
    dbInstanceId={dbInstanceId} cursor={cursor} />;
}

export function mountProductionApp(root: HTMLElement, browserInput: Readonly<{
  storage: Storage; submittedStorage?: () => Storage; reload: () => void; deleteDatabase: (name: string) => void;
}>): void {
  const prepareReport = (report: TrackReport | null, signal: AbortSignal) => runWorkerTask<TrackReport | null, PreparedReport>(
    () => new Worker(new URL('../../features/report/document/preparation.worker.ts', import.meta.url), { type: 'module' }), report, signal);
  const browser = browserInput;
  const storage = browser.storage;
  const authChanges = createAuthChanges(window, storage);
  const unauthorized = createUnauthorizedChannel({ enqueue: (task) => queueMicrotask(task) }, { report: console.error });
  const transport = createFetchTransport();
  const client = new QueryClient();
  const cursorStore = createBrowserCursorStore(storage);
  const createSessionStream = () => createBrowserEventComposition({
    storage, transport, cursorStore, unauthorizedChannel: unauthorized,
  }).stream;
  // The one place the card runtime is assembled: no module-level registry, so a second boot would be a second registry.
  const registry = createCardRegistry();
  bootCards(registry);
  const host = createCardHost(registry);
  const runtime: ProviderRuntime & SessionCleanupRuntime = {
    fetchVersion: () => runOperation(transport, serverVersionOperation(), unauthorized),
    reload: browser.reload, deleteDatabase: browser.deleteDatabase,
    idbDatabaseName: IDB_DB_NAME, storage, submittedStorage: browser.submittedStorage,
  };
  const uiPreferences = createUiPreferences(storage);
  const buildRouter = () => createAppRouter({
    submittedStorage: browser.submittedStorage,
    prepareReport,
    transport,
    unauthorized,
    client,
    cards: { registry, host },
    recentFiles: createRecentFileHistory(storage),
    uiPreferences,
  });
  const mounted = createRoot(root);
  mounted.render(<ProductionApp transport={transport} unauthorized={unauthorized} client={client}
    runtime={runtime} cursorStore={cursorStore} router={buildRouter()} authChanges={authChanges}
    renderLogin={() => <LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)}
      reload={() => { authChanges.publish(); browser.reload(); }} />}
    renderError={renderSessionError}
    renderEventBridge={(server) => <SessionEventBridge createStream={createSessionStream} client={client}
      syncEventVersion={server.syncEventVersion} dbInstanceId={server.dbInstanceId} cursor={cursorStore} />}
  />);
}
