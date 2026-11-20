import '../../styles/entry.css';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { act, cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession } from './session-contract-fixture.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { queryKeys } from '../providers/query-keys.ts';

afterEach(async () => { cleanup(); document.getElementById('root')?.remove(); await page.viewport(1280, 720); });

function mount(refuseDiscovery = false) {
  const creates: ApiRequest[] = [];
  const bootstrap = { version: 'test', providers: [
    { provider: refuseDiscovery ? 'acp:opencode' : 'codex', available: true, models: ['gpt-test'], permissionModes: [
      { id: 'inspect-live', label: 'Inspect live', description: 'From server', tone: 'neutral', isDefault: true },
      { id: 'act-live', label: 'Act live', description: 'From server', tone: 'warning', isDefault: false },
    ] },
    { provider: 'claude', available: true, models: ['haiku'], permissionModes: [] },
  ] };
  const transport: ApiTransportPort = { send(request) {
    if (request.method === 'POST' && request.path === '/api/v1/tracks') {
      creates.push(request);
      if (refuseDiscovery) return Promise.resolve({ status: 409, statusText: 'Conflict',
        body: { code: 'precondition_failed', error: 'Permission modes are unavailable. Retry discovery.' } });
      return new Promise(() => undefined);
    }
    const body = request.path === '/api/v1/areas'
      ? [{ id: 'c1', name: 'Work', color: '#5B8DEF', sort: 1, kind: 'user', created_at: 1, updated_at: 1 }]
      : request.path.startsWith('/api/v1/bootstrap') ? bootstrap : [];
    return Promise.resolve({ status: 200, statusText: 'OK', body });
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }), onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: ['/area/c1/new'] }) });
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>);
  return { creates };
}

it.each([1280, 390])('sends a backend mode selected on the new-track page (%ipx)', async (width) => {
  await page.viewport(width, 844);
  const { creates } = mount();
  await page.getByRole('button', { name: 'Approvals: Inspect live' }).click();
  await page.getByRole('menuitem', { name: /^Act live/ }).click();
  await expect.element(page.getByRole('button', { name: 'Approvals: Act live' })).toBeVisible();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Use selected mode');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0]?.body).toMatchObject({ permissionMode: 'act-live' });
  expect(document.documentElement.scrollWidth).toBe(width);
});

it('unlocks the picker and shows the reason after a first discovery refusal', async () => {
  const { creates } = mount(true);
  await page.getByRole('button', { name: /^Model:/ }).click();
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'OpenCode', exact: true }).click();
  await page.getByRole('group', { name: 'Models' }).getByRole('radio', { name: 'gpt-test', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByRole('button', { name: 'Approvals: Inspect live' }).click();
  await page.getByRole('menuitem', { name: /^Act live/ }).click();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Discover modes');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.element(page.getByText('Permission modes are unavailable. Retry discovery.', { exact: false })).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Approvals: Act live' })).toBeEnabled();
  await page.getByRole('button', { name: 'Approvals: Act live' }).click();
  await page.getByRole('menuitem', { name: /^Inspect live/ }).click();
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(2);
  expect(creates[1]?.body).toMatchObject({ permissionMode: 'inspect-live' });
});

it('clears the mode on a provider switch and hides an empty provider list', async () => {
  const { creates } = mount();
  await page.getByRole('button', { name: 'Approvals: Inspect live' }).click();
  await page.getByRole('menuitem', { name: /^Act live/ }).click();
  await page.getByRole('button', { name: /^Model:/ }).click();
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'Claude' }).click();
  await page.getByRole('group', { name: 'Models' }).getByRole('radio', { name: 'haiku', exact: true }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.element(page.getByRole('button', { name: /^Approvals:/ })).not.toBeInTheDocument();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Switch provider');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0]?.body).not.toHaveProperty('permissionMode');
});

it.each([false, true])('allows selecting the advertised default on an existing ACP session with no saved mode (assistant=%s)', async (assistant) => {
  await page.viewport(1280, 720);
  const id = assistant ? 'second-planner' : 'daily-planner';
  let storedMode: string | null = null;
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', extraPlanner: assistant,
    plannerProvider: 'opencode', reply: request => {
      if (request.path === '/api/v1/bootstrap') return { status: 200, statusText: 'OK', body: {
        assistantProviders: true, conversationInitialInput: true, webCompatVersion: WEB_COMPAT_VERSION,
        minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'acp-null-mode',
      } };
      if (request.path === '/api/v1/bootstrap?scope=managed') return { status: 200, statusText: 'OK', body: {
        version: 'test', providers: [{ provider: 'acp:opencode', available: true, models: [], permissionModes: [
          { id: 'project-plan', label: 'Project plan', description: 'Workspace default', tone: 'neutral', isDefault: true },
        ] }],
      } };
      if (assistant && request.path === '/api/v1/tracks/daily/sessions') return { status: 200, statusText: 'OK', body: [
        { id, trackId: 'daily', title: 'Other conversation', kind: 'track-assistant', provider: 'opencode',
          state: 'idle', updatedAt: 2, archivedAt: null, lastTurnCompletedAt: null },
      ] };
      if (request.method === 'PUT' && request.path === `/api/v1/sessions/${id}/permission-mode`) {
        storedMode = 'project-plan';
        return { status: 200, statusText: 'OK',
          body: fixtureSession(id, 'daily', { provider: 'acp:opencode', permissionMode: storedMode, isLead: !assistant }) };
      }
      if (request.path.startsWith(`/api/v1/sessions/${id}/items?`)) return { status: 200, statusText: 'OK', body: {
        session: fixtureSession(id, 'daily', { provider: 'acp:opencode', permissionMode: storedMode, isLead: !assistant }),
        items: [], unknownInput: null, activeTurn: null, canSteer: true, queuedMessages: [],
      } };
      return undefined;
    } });
  try {
    await page.getByRole('button', { name: assistant ? /Conversation Other conversation/ : /Conversation Daily Planner conversation/ }).click();
    await page.getByRole('button', { name: 'Approvals: Provider default' }).click();
    await page.getByRole('menuitem', { name: /^Project plan/ }).click();
    await expect.element(page.getByRole('button', { name: 'Approvals: Project plan' })).toBeVisible();
    const writes = fixture.requests.filter(request => request.method === 'PUT' && request.path === `/api/v1/sessions/${id}/permission-mode`);
    expect(writes).toHaveLength(1);
    expect(writes[0]?.body).toEqual({ permissionMode: 'project-plan' });
  } finally { fixture.client.clear(); }
});

it('shows a saved agy mode before the workspace permission catalogue arrives', async () => {
  await page.viewport(1280, 720);
  const id = 'daily-planner';
  let workspaceReady = false;
  const fixture = renderDailyFixture({ initial: '/track/daily?panel=conversations', plannerProvider: 'antigravity',
    reply: request => {
      if (request.path === '/api/v1/bootstrap') return { status: 200, statusText: 'OK', body: {
        assistantProviders: true, conversationInitialInput: true, webCompatVersion: WEB_COMPAT_VERSION,
        minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'agy-workspace-mode',
      } };
      if (request.path === '/api/v1/bootstrap?scope=managed') return { status: 200, statusText: 'OK', body: {
        version: 'test', providers: [{ provider: 'acp:antigravity', available: true, models: [], permissionModes: [
          { id: 'bypassPermissions', label: 'Full access', description: 'Installation default', tone: 'danger', isDefault: true },
        ] }],
      } };
      if (request.path.startsWith(`/api/v1/sessions/${id}/items?`)) return { status: 200, statusText: 'OK', body: {
        session: fixtureSession(id, 'daily', { provider: 'acp:antigravity', permissionMode: 'workspace-review', isLead: true }),
        items: [], unknownInput: null, activeTurn: null, canSteer: true, queuedMessages: [],
        ...(workspaceReady ? { permissionModes: [
          { id: 'workspace-review', label: 'Workspace review', description: 'From the workspace', tone: 'warning', isDefault: true },
        ] } : {}),
      } };
      return undefined;
    },
  });
  try {
    await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
    await expect.element(page.getByRole('button', { name: /^Approvals:/ }), { timeout: 5000 })
      .toHaveTextContent('Approvals: workspace-review');
    workspaceReady = true;
    await act(async () => { await fixture.client.invalidateQueries({ queryKey: queryKeys.plannerRun(id) }); });
    await expect.element(page.getByRole('button', { name: 'Approvals: Workspace review' })).toBeVisible();
    expect(fixture.requests.some(request => request.method === 'PUT' && request.path.endsWith('/permission-mode'))).toBe(false);
  } finally { fixture.client.clear(); }
});
