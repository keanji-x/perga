import { queryKeys } from '../providers/query-keys.ts';
import '../../styles/entry.css';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// The grouped model picker on the real new-track route, in a real engine: a Claude pick sets the
// Planner's provider and model, drops a Codex effort and offers its own levels (#1822), and the row still fits a phone (#1810).
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

afterEach(async () => { cleanup(); await page.viewport(1280, 720); });

const AREA = { id: 'c1', name: 'Work', color: '#5B8DEF', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 };

function mount(providers = ['codex', 'claude', 'opencode']) {
  const creates: ApiRequest[] = [];
  const requests: ApiRequest[] = [];
  const transport: ApiTransportPort = { async send(request) {
    requests.push(request);
    if (request.path.includes('/workspace-options')) return Promise.resolve({ status: 200, statusText: 'OK', body: { defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] } });
    if (request.method === 'POST' && request.path === '/api/v1/tracks') {
      creates.push(request);
      return new Promise<ApiTransportResponse>(() => undefined);
    }
    let body: unknown = [];
    if (request.path === '/api/v1/areas') body = [AREA];
    if (request.path.startsWith('/api/v1/bootstrap')) body = { version: 'providers', areas: [AREA], templates: [],
      providers: providers.map(provider => ({ provider: provider === 'opencode' ? 'acp:opencode' : provider, available: true, version: 'test', models: [`${provider}-model`] })) };
    if (request.path === '/api/settings') body = {};
    return { status: 200, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(), unauthorized: createUnauthorizedChannel({ enqueue: task => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: ['/area/c1/new'] }) });
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>);
  return { creates, requests, client };
}

/** Open the model popover by clicking its trigger button. */
async function openModelMenu(name: string) {
  await page.getByRole('button', { name }).click();
}

it.each([['claude', 'Claude', 1280], ['claude', 'Claude', 390], ['opencode', 'OpenCode', 1280], ['opencode', 'OpenCode', 390]] as const)('selects %s and creates from bootstrap models (%s) at %ipx', async (provider, label, width) => {
  await page.viewport(width, 844);
  const { creates } = mount();
  await openModelMenu('Model: Codex Default');
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: label }).click();
  await page.getByRole('radio', { name: `${provider}-model`, exact: true }).click();
  await expect.element(page.getByRole('button', { name: `Model: ${provider}-model` })).toBeVisible();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Plan the release');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0].body).toMatchObject({ provider: provider === 'opencode' ? 'acp:opencode' : provider, areaId: 'c1', model: `${provider}-model` });
  expect(creates[0].body).not.toHaveProperty('first_message');
  expect(creates[0].headers?.['Idempotency-Key']).toBeTruthy();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});

it('offers only providers declared by bootstrap', async () => {
  const { requests } = mount(['codex']);
  await openModelMenu('Model: Default');
  await expect.element(page.getByRole('radio', { name: 'codex-model' })).toBeVisible();
  expect(page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'Claude' }).query()).toBeNull();
  expect(requests.filter(request => request.path.includes('?provider=')).map(request => request.path))
    .toEqual(['/api/v1/bootstrap?provider=codex']);
});

it('loads newly registered providers after the managed list refreshes', async () => {
  const providers = ['codex'];
  const { client, requests } = mount(providers);
  await openModelMenu('Model: Default');
  await expect.element(page.getByRole('radio', { name: 'codex-model' })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  providers.push('grok');
  await Promise.all([
    client.invalidateQueries({ queryKey: queryKeys.managedProviders() }),
    client.invalidateQueries({ queryKey: queryKeys.agentProviders() }),
  ]);
  await openModelMenu('Model: Codex Default');
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'Grok', exact: true }).click();
  await expect.element(page.getByRole('radio', { name: 'grok-model', exact: true })).toBeVisible();
  expect(requests.some(request => request.path === '/api/v1/bootstrap?provider=grok')).toBe(true);
});

function mountRichCatalog() {
  const creates: ApiRequest[] = [];
  const transport: ApiTransportPort = { async send(request) {
    if (request.path.includes('/workspace-options')) return Promise.resolve({ status: 200, statusText: 'OK', body: { defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] } });
    if (request.method === 'POST' && request.path === '/api/v1/tracks') {
      creates.push(request);
      return new Promise<ApiTransportResponse>(() => undefined);
    }
    let body: unknown = [];
    if (request.path === '/api/v1/areas') body = [AREA];
    if (request.path.startsWith('/api/v1/bootstrap')) body = {
      version: 'providers',
      areas: [AREA],
      templates: [],
      providers: [
        {
          provider: 'codex',
          available: true,
          version: 'codex-cli 0.171.0',
          models: ['gpt-6.1-sol', 'gpt-6.2-sol'],
          modelCatalog: [
            {
              model: 'gpt-6.1-sol',
              displayName: 'GPT-6.1-Sol-Long-Preview-Model-Name',
              description: '',
              isDefault: true,
              defaultReasoningEffort: 'low',
              supportedReasoningEfforts: [
                { reasoningEffort: 'low', description: '' },
                { reasoningEffort: 'high', description: '' },
              ],
            },
          ],
        },
        {
          provider: 'claude',
          available: true,
          version: '2.1.280',
          models: ['opus', 'sonnet'],
          modelCatalog: [
            { model: 'opus', displayName: 'Opus', description: '', isDefault: true, defaultReasoningEffort: null, supportedReasoningEfforts: [{ reasoningEffort: 'high', description: '' }] },
          ],
        },
        {
          provider: 'acp:antigravity',
          available: true,
          version: '1.5.13',
          models: ['gemini-4-neon'],
          modelCatalog: [
            { model: 'gemini-4-neon', displayName: 'Gemini 4 Neon', description: '', isDefault: true, defaultReasoningEffort: 'high', supportedReasoningEfforts: [{ reasoningEffort: 'high', description: '' }] },
          ],
        },
        {
          provider: 'acp:opencode',
          available: true,
          version: 'opencode v2.1.4',
          models: ['deepseek/deepseek-flash-extended-preview', 'zhipuai/glm-5.3'],
          modelCatalog: [
            { model: 'deepseek/deepseek-flash-extended-preview', displayName: 'deepseek/deepseek-flash-extended-preview', description: '', isDefault: false, defaultReasoningEffort: null, supportedReasoningEfforts: [] },
            { model: 'zhipuai/glm-5.3', displayName: 'zhipuai/glm-5.3', description: '', isDefault: false, defaultReasoningEffort: null, supportedReasoningEfforts: [] },
          ],
        },
      ],
    };
    if (request.path === '/api/settings') body = {};
    return { status: 200, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(), unauthorized: createUnauthorizedChannel({ enqueue: task => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: ['/area/c1/new'] }) });
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>);
  return { creates };
}

it('selects OpenCode directly from the Providers column and updates the truncated hover tooltip', async () => {
  await page.viewport(1280, 844);
  const { creates } = mountRichCatalog();
  const initialTrigger = page.getByRole('button', { name: /^Model: Codex GPT-6\.1-Sol-Long-Preview-Model-Name · low/ });
  await expect.element(initialTrigger).toBeVisible();

  // Hover the truncated initial trigger label once truncation and Astryx's lazy tooltip chunk (200ms delay) have settled.
  const initialLabel = initialTrigger.getByText('Codex GPT-6.1-Sol-Long-Preview-Model-Name · low');
  await expect.poll(() => initialLabel.element().scrollWidth > initialLabel.element().clientWidth).toBe(true);
  await expect.poll(async () => {
    const existing = page.getByRole('tooltip').query()?.textContent ?? null;
    if (existing !== null) return existing;
    await userEvent.hover(document.body);
    await initialLabel.hover();
    await new Promise((resolve) => setTimeout(resolve, 260));
    return page.getByRole('tooltip').query()?.textContent ?? null;
  }, { interval: 100 }).toBe('Codex GPT-6.1-Sol-Long-Preview-Model-Name · low');

  // Open overview -> switch to Models view -> pick OpenCode Default (applies immediately).
  await initialTrigger.click();
  await page.getByRole('button', { name: /^Change model:/ }).click();
  const openCodeProvider = page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'OpenCode', exact: true });
  await openCodeProvider.click();
  await page.getByRole('group', { name: 'Models' }).getByRole('radio', { name: /^Default/ }).click();

  const updatedTrigger = page.getByRole('button', { name: 'Model: OpenCode Default', exact: true });
  await expect.element(updatedTrigger).toBeVisible();

  // Hovering the updated non-truncated trigger must not keep the stale tooltip.
  await userEvent.hover(document.body);
  await updatedTrigger.hover();
  await expect.element(page.getByRole('tooltip')).not.toBeInTheDocument();

  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Run with OpenCode');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0].body).toMatchObject({ provider: 'acp:opencode', areaId: 'c1' });
  expect(creates[0].body).not.toHaveProperty('model');
});

it('updates the truncated hover tooltip when switching from Codex to an explicit OpenCode model', async () => {
  await page.viewport(1280, 844);
  mountRichCatalog();
  const initialTrigger = page.getByRole('button', { name: /^Model: Codex GPT-6\.1-Sol-Long-Preview-Model-Name · low/ });
  await expect.element(initialTrigger).toBeVisible();

  await initialTrigger.hover();
  await expect.element(page.getByRole('tooltip')).toHaveTextContent('Codex GPT-6.1-Sol-Long-Preview-Model-Name · low');

  await initialTrigger.click();
  await page.getByRole('button', { name: /^Change model:/ }).click();
  await page.getByRole('group', { name: 'Providers' }).getByRole('radio', { name: 'OpenCode', exact: true }).click();
  await page.getByRole('group', { name: 'Models' }).getByRole('radio', { name: 'deepseek/deepseek-flash-extended-preview', exact: true }).click();

  const updatedTrigger = page.getByRole('button', { name: /^Model: OpenCode deepseek\/deepseek-flash-extended-preview/ });
  await expect.element(updatedTrigger).toBeVisible();

  await userEvent.hover(document.body);
  await updatedTrigger.hover();
  await expect.element(page.getByRole('tooltip')).toHaveTextContent('OpenCode deepseek/deepseek-flash-extended-preview');
});
