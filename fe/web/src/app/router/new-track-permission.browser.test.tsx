import '../../styles/entry.css';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';

afterEach(async () => { cleanup(); document.getElementById('root')?.remove(); await page.viewport(1280, 720); });

function mount() {
  const creates: ApiRequest[] = [];
  const bootstrap = { version: 'test', providers: [
    { provider: 'codex', available: true, models: ['gpt-test'], permissionModes: [
      { id: 'inspect-live', label: 'Inspect live', description: 'From server', tone: 'neutral', isDefault: true },
      { id: 'act-live', label: 'Act live', description: 'From server', tone: 'warning', isDefault: false },
    ] },
    { provider: 'claude', available: true, models: ['haiku'], permissionModes: [] },
  ] };
  const transport: ApiTransportPort = { send(request) {
    if (request.method === 'POST' && request.path === '/api/v1/tracks') {
      creates.push(request);
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

it('clears the mode on a provider switch and hides an empty provider list', async () => {
  const { creates } = mount();
  await page.getByRole('button', { name: 'Approvals: Inspect live' }).click();
  await page.getByRole('menuitem', { name: /^Act live/ }).click();
  await page.getByRole('button', { name: /^Model:/ }).click();
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'Claude' }).click();
  await page.getByRole('group', { name: 'Models' }).getByRole('radio', { name: 'haiku', exact: true }).click();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('button', { name: /^Approvals:/ })).not.toBeInTheDocument();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Switch provider');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0]?.body).not.toHaveProperty('permissionMode');
});

it.each([false, true])('renders ACP approvals for a null mode (assistant=%s)', async (assistant) => {
  const id = assistant ? 'second-planner' : 'daily-planner';
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
      if (request.path.startsWith(`/api/v1/sessions/${id}/items?`)) return { status: 200, statusText: 'OK', body: {
        session: { id, trackId: 'daily', provider: 'acp:opencode', permissionMode: null,
          isLead: !assistant, status: 'idle', createdAtMs: 1, updatedAtMs: 2 },
        items: [], unknownInput: null, activeTurn: null, canSteer: true, queuedMessages: [],
      } };
      return undefined;
    } });
  try {
    await page.getByRole('button', { name: assistant ? /Conversation Other conversation/ : /Conversation Daily Planner conversation/ }).click();
    await expect.element(page.getByRole('button', { name: 'Approvals: Project plan' })).toBeVisible();
    await page.getByRole('button', { name: 'Approvals: Project plan' }).click();
    await expect.element(page.getByRole('menuitem', { name: /^Project plan/ })).toBeVisible();
  } finally { fixture.client.clear(); }
});
