import type { BootstrapDto } from '../../../../core/api/perga-v1.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// @vitest-environment jsdom
// Settings › Planners through the real router and query layer (#1817): the pane reads
// shared availability and managed capabilities; Recheck refreshes only availability.
import { QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider,createMemoryHistory } from '@tanstack/react-router';
import { act,cleanup,render,screen,waitFor,within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach,beforeEach,describe,expect,it,vi } from 'vitest';

import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { RECHECK_TEXT,unavailableCreationNote } from '../../../../core/domain/agent-providers.ts';
import { queryKeys } from '../providers/queries.ts';
import { createAppRouter } from '../router/public.tsx';
import { bootTestCardRuntime } from '../router/test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { settingsSectionForPath } from './settings-overlay.tsx';

const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
const LOGGED_OUT = 'not logged in — run `claude /login` with CLAUDE_CONFIG_DIR=/srv/claude';

beforeEach(() => {
  vi.stubGlobal('matchMedia', vi.fn(() => ({
    matches: false,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  })));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function answer(claude: Readonly<{ status: string; reason: string | null }>): BootstrapDto {
  return { version: 'test', providers: [
    { provider: 'codex', available: true, reason: null, version: 'test', models: [], modelCatalog: [], permissionModes: [] },
    { provider: 'claude', available: claude.status === 'ready', reason: claude.reason, version: 'test', models: [], modelCatalog: [], permissionModes: [] },
  ], templates: [], areas: [] };
}

function providerResponse(_request: ApiRequest, bootstrap: BootstrapDto): ApiTransportResponse {
  return { status: 200, statusText: 'OK', body: bootstrap };
}

/** `recheck` answers `refresh=true` in place of the ready answer. */
function renderPlanners(recheck?: () => Promise<ApiTransportResponse>,
  settingsReply?: (request: ApiRequest) => Promise<ApiTransportResponse>,
  providersReply?: (request: ApiRequest) => Promise<ApiTransportResponse>) {
  const sent: ApiRequest[] = [];
  const transport: ApiTransportPort = {
    send(request): Promise<ApiTransportResponse> {
      sent.push(request);
      if (providersReply !== undefined && (request.path.startsWith('/api/v1/bootstrap?scope=') || request.path.startsWith('/api/agent-providers'))) return providersReply(request);
      if (request.path === '/api/v1/bootstrap?scope=providers') return Promise.resolve(providerResponse(request, answer({ status: 'unavailable', reason: LOGGED_OUT })));
      if (settingsReply !== undefined && request.path === '/api/settings') return settingsReply(request);
      if (recheck !== undefined && request.path === '/api/v1/bootstrap?scope=providers&refresh=true') return recheck();
      const body = request.path === '/api/v1/bootstrap?scope=managed'
        ? answer({ status: 'unavailable', reason: LOGGED_OUT })
        : request.path === '/api/v1/bootstrap?scope=providers&refresh=true'
          ? answer({ status: 'ready', reason: null })
          : request.path === '/api/settings' ? { settings: {} } : [];
      return Promise.resolve({ status: 200, statusText: 'OK', body });
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(queryKeys.modelCatalog({ kind: 'provider', provider: 'claude' }), { models: [], modelCatalog: [] });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport, unauthorized, client, cards: bootTestCardRuntime(), onSignOut: () => undefined,
  });
  router.update({ history: createMemoryHistory({ initialEntries: ['/settings/planners'] }) });
  render(
    <QueryClientProvider client={client}>
      <ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
        <RouterProvider router={router} />
      </ThemeProvider>
    </QueryClientProvider>,
  );
  return { sent, client };
}

function row(title: string): HTMLElement {
  const heading = screen.getByText(title, { selector: '*' });
  const item = heading.closest('li');
  if (item === null) throw new Error(`no row for ${title}`);
  return item;
}

it('maps the Planners path to its pane', () => {
  expect(settingsSectionForPath('/settings/planners')).toBe('planners');
});

it('lists each provider with its status and reason, and Recheck replaces the answer and refreshes the catalogs', async () => {
  const { sent, client } = renderPlanners();
  const claudeCatalog = queryKeys.modelCatalog({ kind: 'provider', provider: 'claude' });
  await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
  expect(within(row('Claude')).getByText('Unavailable')).toBeTruthy();
  expect(within(row('Codex')).getByText('Ready')).toBeTruthy();
  expect(sent.filter((request) => request.path === '/api/v1/bootstrap?scope=providers&refresh=true')).toHaveLength(0);
  expect(client.getQueryState(claudeCatalog)?.isInvalidated).toBe(false);
  const readsBeforeRecheck = sent.filter((request) => request.path === '/api/v1/bootstrap?scope=managed').length;

  await userEvent.click(screen.getByRole('button', { name: 'Recheck planners' }));
  await waitFor(() => expect(screen.queryByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`)).toBeNull());
  expect(within(row('Claude')).getByText('Ready')).toBeTruthy();
  expect(sent.filter((request) => request.path === '/api/v1/bootstrap?scope=providers&refresh=true')).toHaveLength(1);
  /* The recheck re-fetched the Claude CLI's model list on the server (#1822). */
  expect(client.getQueryState(claudeCatalog)?.isInvalidated).toBe(true);
  expect(sent.filter((request) => request.path === '/api/v1/bootstrap?scope=managed')).toHaveLength(readsBeforeRecheck + 1);
  expect(client.getQueryData(queryKeys.agentProviders())).toMatchObject([{ provider: 'codex', status: 'ready' }, { provider: 'claude', status: 'ready' }]);
});

/* #2131 S7: Recheck is a probe; a lost answer shows a fixed sentence, never transport text, and the answer stays. */
describe('a failed Recheck', () => {
  const RAW_OR_CONNECTIVITY = /Transport request failed|timed out|schema|offline|reconnect|connection/i;
  it.each([
    ['a lost answer', () => Promise.reject(new Error('socket hang up')), RECHECK_TEXT.unfinished],
    ['an unreadable answer', () => Promise.resolve({ status: 200, statusText: 'OK', body: { not: 'a list' } }), RECHECK_TEXT.unfinished],
    ['an answered failure', () => Promise.resolve({ status: 500, statusText: 'Server Error', body: { error: 'checks are wedged', code: 'internal' } }),
      'checks are wedged'],
    /* A refusal about one field names it, as every answered reason does (`failureReason`). */
    ['a refusal about one field', () => Promise.resolve({ status: 400, statusText: 'Bad Request',
      body: { error: 'must be true or false', code: 'bad_request', field: 'refresh' } }), 'refresh: must be true or false'],
  ] as const)('shows %s as its sentence and keeps the previous answer', async (_name, reply, text) => {
    renderPlanners(reply);
    await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
    await userEvent.click(screen.getByRole('button', { name: 'Recheck planners' }));
    expect((await screen.findByRole('alert')).textContent).toBe(text);
    expect(screen.getByRole('alert').textContent).not.toMatch(RAW_OR_CONNECTIVITY);
    expect(screen.getByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`)).toBeTruthy();
  });
});


it('injects blank-creation facts through the route without consulting managed-conversation readiness', async () => {
  const entries = answer({ status: 'unavailable', reason: LOGGED_OUT });
  renderPlanners(undefined, undefined, (request) => Promise.resolve(providerResponse(request, entries)));
  await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
  expect(within(row('Claude')).getByText('Unavailable')).toBeTruthy();
});

it('retains Checking, first-read retry and pending Recheck states through the route', async () => {
  let release!: (value: ApiTransportResponse) => void;
  let fail = true;
  const { sent } = renderPlanners(undefined, undefined, (request) => {
    if (request.path.endsWith('refresh=true')) return new Promise((resolve) => { release = resolve; });
    if (fail) return Promise.reject(new Error('read failed'));
    return Promise.resolve(providerResponse(request, answer({ status: 'unavailable', reason: LOGGED_OUT })));
  });
  await screen.findByText('Planners are unavailable.');
  expect(screen.queryByText('Checking providers…')).toBeNull();
  fail = false;
  await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
  await userEvent.click(screen.getByRole('button', { name: 'Recheck planners' }));
  expect(screen.getByRole('button', { name: 'Recheck planners' }).hasAttribute('disabled')).toBe(true);
  expect(screen.getByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`)).toBeTruthy();
  release({ status: 200, statusText: 'OK', body: answer({ status: 'ready', reason: null }) });
  await waitFor(() => expect(screen.queryByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`)).toBeNull());
  expect(sent.filter((request) => request.path.endsWith('refresh=true'))).toHaveLength(1);
});

it.each(['/api/v1/bootstrap?scope=providers', '/api/v1/bootstrap?scope=managed'])('keeps Checking until %s arrives and joins capabilities by provider', async (delayedPath) => {
  let release!: (value: ApiTransportResponse) => void;
  const live = answer({ status: 'unavailable', reason: LOGGED_OUT });
  const managed = { ...answer({ status: 'ready', reason: null }), providers: answer({ status: 'ready', reason: null }).providers.reverse() };
  renderPlanners(undefined, undefined, (request) => request.path === delayedPath
    ? new Promise((resolve) => { release = resolve; })
    : Promise.resolve(providerResponse(request, request.path === '/api/v1/bootstrap?scope=providers' ? live : managed)));
  await screen.findByText('Checking providers…');
  await waitFor(() => expect(release).toBeTypeOf('function'));
  act(() => { release(providerResponse({ path: delayedPath } as ApiRequest, delayedPath === '/api/v1/bootstrap?scope=providers' ? live : managed)); });
  await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
  expect(within(row('Claude')).getByText('Unavailable')).toBeTruthy();
  expect(screen.getAllByRole('listitem').indexOf(row('Codex'))).toBeLessThan(screen.getAllByRole('listitem').indexOf(row('Claude')));
});

it('reports missing managed capabilities explicitly and retries both reads', async () => {
  let missing = true;
  const { sent } = renderPlanners(undefined, undefined, (request) => {
    const entries = answer({ status: 'unavailable', reason: LOGGED_OUT });
    return Promise.resolve(providerResponse(request, missing && request.path === '/api/v1/bootstrap?scope=managed' ? { ...entries, providers: entries.providers.slice(0, 1) } : entries));
  });
  await screen.findByText('Planner capabilities are unavailable for claude.');
  expect(screen.queryByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`)).toBeNull();
  missing = false;
  await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
  await screen.findByText(`${LOGGED_OUT} ${unavailableCreationNote('Claude')}`);
  expect(sent.filter(({ path }) => path === '/api/v1/bootstrap?scope=providers').length).toBeGreaterThan(1);
  expect(sent.filter(({ path }) => path === '/api/v1/bootstrap?scope=managed').length).toBeGreaterThan(1);
});
