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

afterEach(async () => { cleanup(); await page.viewport(1280, 720); });

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
