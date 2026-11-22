import { QueryClient } from '@tanstack/react-query';
import { RouterProvider,type AnyRouter } from '@tanstack/react-router';
import { StrictMode, useMemo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { logoutOperation, whoamiOperation } from '../../../../core/api/auth.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel,type UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { RecoveryAccess } from '../../../../core/domain/recovery/access.ts';
import type { TrackReport } from '../../../../core/domain/report.ts';
import { IDB_DB_NAME } from '../../../../core/keys/storage.ts';
import { LoginPage } from '../../features/auth/login-page/public.tsx';
import type { PreparedReport } from '../../features/report/document/preparation.ts';
import { createCardHost,createCardRegistry } from '../../systems/cards/public.js';
import { RecoverySession } from '../../systems/recovery/session.ts';
import { createRecoveryTransports } from '../../systems/recovery/transport.ts';
import { createRecoveryUnauthorizedChannel } from '../../systems/recovery/unauthorized.ts';
import { runWorkerTask } from '../../systems/worker-task/public.ts';
import { DepthFeedback } from '../../ui/motion/depth-feedback.tsx';
import { bootCards } from '../cards.ts';
import { createBrowserEventComposition } from '../composition.ts';
import { createBrowserCursorStore } from '../events/browser-cursor-store.ts';
import { EventBridge } from '../events/event-bridge.tsx';
import { RecoveryEventBridge } from '../events/recovery-event-bridge.tsx';
import { SystemUpgradeProvider } from '../providers/connection-status.tsx';
import { AppProviders,WEB_COMPAT_VERSION,type ProviderRuntime } from '../providers/public.tsx';
import { serverVersionOperation } from '../providers/server-queries.ts';
import { runOperation } from '../providers/query-operation.ts';
import { createRecentFileHistory } from '../providers/recent-files.ts';
import { SystemUpgradeMonitor } from '../providers/system-upgrade.tsx';
import { createFetchTransport } from '../providers/transport.ts';
import { createUiPreferences } from '../providers/ui-preferences.tsx';
import { createAppRouter } from '../router/public.tsx';
import { createAuthChanges, type AuthChanges } from './auth-changes.ts';
import { BundledLoginPage } from './bundled-connection.tsx';
import { loginForRecovery,loginWithTransport } from './login.ts';
import { LogoutGate } from './logout-gate.tsx';
import { RecoveryGate } from './recovery-gate.tsx';
import { clearSessionArtifacts,SessionGate, type SessionCleanupRuntime, type SessionProbeError } from './session-gate.tsx';

export function ProductionApp({ transport, unauthorized, client, runtime, cursorStore, router, renderEventBridge,
  renderLogin, renderError, recovery, authChanges, }: Readonly<{
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel; client: QueryClient; runtime: ProviderRuntime & SessionCleanupRuntime;
  cursorStore: Parameters<typeof AppProviders>[0]['cursorStore']; router: AnyRouter;

  renderEventBridge?: Parameters<typeof AppProviders>[0]['renderEventBridge'];
  recovery?: RecoverySession;
  authChanges?: AuthChanges;
  renderLogin: (retry: () => void) => ReactNode;
  renderError: (retry: () => void, error: SessionProbeError) => ReactNode;
}>) {
  const content = <><SystemUpgradeMonitor transport={transport} unauthorized={unauthorized} /><RouterProvider router={router} /></>;
  if (__NC_BUNDLED__) return <StrictMode><SystemUpgradeProvider><DepthFeedback /><RecoveryGate transport={transport} unauthorized={unauthorized} client={client}
    runtime={runtime} cursorStore={cursorStore} renderLogin={() => renderLogin(() => {})} renderEventBridge={renderEventBridge} recovery={recovery}>
    {content}
  </RecoveryGate></SystemUpgradeProvider></StrictMode>;
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
  const access = new RecoveryAccess();
  const scheduler = { enqueue: (task: () => void) => queueMicrotask(task) };
  const unauthorized = __NC_BUNDLED__ ? createRecoveryUnauthorizedChannel(access, scheduler)
    : createUnauthorizedChannel(scheduler, { report: console.error });
  const base = createFetchTransport();
  const guarded = createRecoveryTransports(base, access);
  const transport = __NC_BUNDLED__ ? guarded.business : base;
  const probe = __NC_BUNDLED__ ? guarded.probe : base;
  const client = new QueryClient();
  const cursorStore = createBrowserCursorStore(storage);
  const createSessionStream = () => createBrowserEventComposition({
    storage, transport: probe, cursorStore, unauthorizedChannel: unauthorized,
  }).stream;
  const createRecoveryEvents = () => createBrowserEventComposition({ storage, transport: probe,
    cursorStore, unauthorizedChannel: unauthorized,
    probeUnauthorized: () => { recovery?.resume(); return Promise.resolve(); } });
  // The one place the card runtime is assembled: no module-level registry, so a second boot would be a second registry.
  const registry = createCardRegistry();
  bootCards(registry);
  // Built from this app's transport and 401 channel so a card's read hits the same session handling as every other read.
  const host = createCardHost(registry, {
    ...(__NC_BUNDLED__ ? { recovery: access } : {}),
  });
  const runtime: ProviderRuntime & SessionCleanupRuntime = {
    fetchVersion: () => runOperation(probe, serverVersionOperation(), unauthorized),
    reload: browser.reload, deleteDatabase: browser.deleteDatabase,
    idbDatabaseName: IDB_DB_NAME, storage, submittedStorage: browser.submittedStorage,
  };
  const uiPreferences = createUiPreferences(storage);
  const recovery = __NC_BUNDLED__ ? new RecoverySession({ access, storage,
    origin: window.location.origin, compatibleVersion: WEB_COMPAT_VERSION,
    adoptScope: scope => uiPreferences.setRecoveryScope(scope),
    identity: (signal) => runOperation(probe, { ...whoamiOperation(), signal }, undefined),
    version: (signal) => runOperation(probe, { ...serverVersionOperation(), signal }, unauthorized),
    logout: (signal, sessionId) => runOperation(probe, { ...logoutOperation(sessionId), signal }, undefined),
    clear: () => clearSessionArtifacts(client, cursorStore, runtime), online: () => navigator.onLine, visible: () => !document.hidden,
  }) : undefined;
  const buildRouter = () => createAppRouter({
    submittedStorage: browser.submittedStorage,
    prepareReport,
    transport,
    unauthorized,
    client,
    cards: { registry, host },
    recentFiles: createRecentFileHistory(storage),
    uiPreferences,
    onSignOut: recovery === undefined ? undefined : () => { void recovery.signOut(); },
  });
  const mounted = createRoot(root);
  mounted.render(<ProductionApp transport={transport} unauthorized={unauthorized} client={client}
    runtime={runtime} cursorStore={cursorStore} router={buildRouter()} recovery={recovery} authChanges={authChanges}
    renderLogin={() => __NC_BUNDLED__
      ? <BundledLoginPage message={recovery?.access.read().detail}
        onManualEntry={() => recovery!.cancelAuthentication()}
        verifyPairing={recovery?.blocked() ? () => { void recovery.verifyNewSession(); } : undefined}
        login={(username, password, signal) => loginForRecovery(probe, recovery!, username, password, signal)}
        reload={() => { /* verified session mounts directly */ }} />
      : <LoginPage login={(username, password, signal) => loginWithTransport(transport, username, password, signal)}
        reload={() => { authChanges.publish(); browser.reload(); }} />}
    renderError={renderSessionError}
    renderEventBridge={(server) => recovery
      ? <RecoveryEventBridge key={`${recovery.scopeRevision}:${server.syncEventVersion}`} createEvents={createRecoveryEvents}
        recovery={recovery} client={client} version={server} />
      : <SessionEventBridge createStream={createSessionStream} client={client}
        syncEventVersion={server.syncEventVersion} dbInstanceId={server.dbInstanceId} cursor={cursorStore} />}
  />);
}
