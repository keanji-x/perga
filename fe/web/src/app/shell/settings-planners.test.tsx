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
import type { SettingsSnapshotDto } from '../../../../core/api/generated/wire.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { RECHECK_TEXT,unavailableCreationNote } from '../../../../core/domain/agent-providers.ts';
import { queryKeys } from '../providers/query-keys.ts';
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
  settingsReply?: (request: ApiRequest) => ApiTransportResponse,
  providersReply?: (request: ApiRequest) => Promise<ApiTransportResponse>) {
  const sent: ApiRequest[] = [];
  const transport: ApiTransportPort = {
    send(request): Promise<ApiTransportResponse> {
      sent.push(request);
      if (providersReply !== undefined && (request.path.startsWith('/api/v1/bootstrap?scope=') || request.path.startsWith('/api/agent-providers'))) return providersReply(request);
      if (request.path === '/api/v1/bootstrap?scope=providers') return Promise.resolve(providerResponse(request, answer({ status: 'unavailable', reason: LOGGED_OUT })));
      if (request.path === '/api/v1/settings') return Promise.resolve(settingsReply?.(request)
        ?? { status: 200, statusText: 'OK', body: { rev: 1, sections: [], fields: [], values: {} } });
      if (recheck !== undefined && request.path === '/api/v1/bootstrap?scope=providers&refresh=true') return recheck();
      const body = request.path === '/api/v1/bootstrap?scope=managed'
        ? answer({ status: 'unavailable', reason: LOGGED_OUT })
        : request.path === '/api/v1/bootstrap?scope=providers&refresh=true'
          ? answer({ status: 'ready', reason: null })
          : [];
      return Promise.resolve({ status: 200, statusText: 'OK', body });
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(queryKeys.modelCatalog({ kind: 'provider', provider: 'claude' }), { models: [], modelCatalog: [] });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport, unauthorized, client, cards: bootTestCardRuntime(),
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

it('refreshes a conflicting revision and saves a subsequent choice against the refreshed snapshot', async () => {
  const key = 'sessions.auto_compaction_percent';
  const field = { key, section: 'planners', label: 'Compaction threshold', description: null, control: 'segmented',
    valueType: 'integer', defaultValue: 80, min: 50, max: 95, unit: '%',
    options: [{ value: '70', label: '70%' }, { value: '80', label: '80%' }, { value: '90', label: '90%' }] };
  const otherKey = 'other.threshold';
  let snapshot: SettingsSnapshotDto = {
    rev: 41,
    sections: [{ id: 'planners', label: 'Planners', description: null }, { id: 'other', label: 'Other', description: null }],
    fields: [field, { ...field, key: otherKey, section: 'other', label: 'Other threshold' }],
    values: { [key]: 85, [otherKey]: 80 },
  };
  const { sent, client } = renderPlanners(undefined, (request) => {
    if (request.method === 'PATCH') {
      if (snapshot.rev === 41) {
        snapshot = { ...snapshot, rev: 42, values: { ...snapshot.values, [key]: 90 } };
        return { status: 409, statusText: 'Conflict', body: { code: 'conflict', error: 'stale revision' } };
      }
      snapshot = { ...snapshot, rev: 43, values: { ...snapshot.values, [key]: 70 } };
    }
    return { status: 200, statusText: 'OK', body: snapshot };
  });
  const defaults = within(await screen.findByRole('region', { name: 'Defaults' }));
  expect(await defaults.findByRole('radiogroup', { name: 'Compaction threshold' })).toBeTruthy();
  expect(defaults.queryByRole('radiogroup', { name: 'Other threshold' })).toBeNull();
  expect((await defaults.findByRole('radio', { name: '85%' })).getAttribute('aria-checked')).toBe('true');
  await userEvent.click(defaults.getByRole('radio', { name: '70%' }));
  expect((await defaults.findByRole('alert')).textContent).toContain('Settings changed on the server.');
  expect(defaults.getByRole('radio', { name: '90%' }).getAttribute('aria-checked')).toBe('true');
  expect(sent.filter(({ path }) => path === '/api/v1/settings').map(({ method }) => method)).toEqual(['GET', 'PATCH', 'GET']);

  await userEvent.click(defaults.getByRole('radio', { name: '70%' }));
  await waitFor(() => expect(defaults.getByRole('radio', { name: '70%' }).getAttribute('aria-checked')).toBe('true'));
  expect(defaults.queryByRole('alert')).toBeNull();
  expect(sent.filter(({ method }) => method === 'PATCH').map(({ body }) => body)).toEqual([
    { expectedRev: 41, patch: { [key]: 70 } },
    { expectedRev: 42, patch: { [key]: 70 } },
  ]);
  expect(client.getQueryData(queryKeys.appSettings())).toEqual(snapshot);
});

it('shows a retryable Defaults read error alongside the provider checks', async () => {
  let fail = true;
  renderPlanners(undefined, () => fail
    ? { status: 503, statusText: 'Service Unavailable', body: { code: 'settings_unavailable', error: 'Invalid TOML' } }
    : { status: 200, statusText: 'OK', body: { rev: 1, sections: [], fields: [], values: {} } });
  const defaults = within(await screen.findByRole('region', { name: 'Defaults' }));
  expect((await defaults.findByRole('alert')).textContent).toContain('Could not load planner defaults.');
  expect(await screen.findByRole('button', { name: 'Recheck planners' })).toBeTruthy();
  fail = false;
  await userEvent.click(defaults.getByRole('button', { name: 'Retry defaults' }));
  await waitFor(() => expect(screen.queryByRole('region', { name: 'Defaults' })).toBeNull());
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
  expect(screen.getByRole('button', { name: 'Recheck planners' }).getAttribute('aria-disabled')).toBe('true');
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
