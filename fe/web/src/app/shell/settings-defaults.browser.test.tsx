import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';

import '../../styles/entry.css';
import type { CreateTrackRequest, PatchSettingsRequestDto, SettingsSnapshotDto } from '../../../../core/api/generated/wire.ts';
import type { ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { createAppRouter } from '../router/public.tsx';
import { fixtureBootstrap } from '../router/session-contract-fixture.ts';
import { bootTestCardRuntime } from '../router/test-card-runtime.ts';
import { ThemeProvider, type ResolvedTheme } from '../theme/public.tsx';

const PROVIDER = 'planners.default_provider';
const COMPACTION = 'sessions.auto_compaction_percent';
const TIMEOUT = 'sessions.idle_timeout_minutes';

afterEach(() => { cleanup(); delete document.documentElement.dataset.theme; });

function openPlanners(theme: ResolvedTheme, { rejectTimeout = false, acknowledgeSave, codexReady = true }: {
  rejectTimeout?: boolean;
  acknowledgeSave?: (snapshot: SettingsSnapshotDto) => Promise<ApiTransportResponse>;
  codexReady?: boolean;
} = {}) {
  const patches: PatchSettingsRequestDto[] = [];
  const creates: CreateTrackRequest[] = [];
  let snapshot: SettingsSnapshotDto = {
    rev: 21,
    sections: [{ id: 'planners', label: 'Planners', description: null }],
    fields: [
      { key: PROVIDER, section: 'planners', label: 'Default provider',
        description: 'Used when creating a Track without a selected provider.', control: 'select', valueType: 'string',
        defaultValue: 'codex', min: null, max: null, unit: null,
        options: [{ value: 'codex', label: 'codex' }, { value: 'claude', label: 'claude' }] },
      { key: COMPACTION, section: 'planners', label: 'Automatic compaction',
        description: 'Compact a session after a turn when context usage reaches this threshold.',
        control: 'segmented', valueType: 'integer', defaultValue: 80, min: 50, max: 95, unit: '%',
        options: [{ value: '70', label: '70%' }, { value: '80', label: '80%' }, { value: '90', label: '90%' }] },
      { key: TIMEOUT, section: 'planners', label: 'Session idle timeout',
        description: 'Close idle session runtimes after this interval.', control: 'select', valueType: 'integer',
        defaultValue: 15, min: 5, max: 240, unit: 'min',
        options: [{ value: '5', label: '5 min' }, { value: '15', label: '15 min' }, { value: '30', label: '30 min' },
          { value: '60', label: '1 hr' }, { value: '120', label: '2 hr' }] },
    ],
    values: { [PROVIDER]: 'codex', [COMPACTION]: 85, [TIMEOUT]: 45 },
  };
  const ok = (body: unknown) => Promise.resolve({ status: 200, statusText: 'OK', body });
  const bootstrap = fixtureBootstrap(['codex', 'claude']);
  if (!codexReady) Object.assign(bootstrap.providers[0], { available: false, reason: 'Codex is unavailable.' });
  const transport: ApiTransportPort = {
    send(request) {
      if (request.path === '/api/v1/settings') {
        if (request.method === 'PATCH') {
          const change = request.body as PatchSettingsRequestDto;
          patches.push(change);
          if (rejectTimeout && TIMEOUT in change.patch) return Promise.resolve({ status: 400, statusText: 'Bad Request',
            body: { code: 'invalid_argument', field: TIMEOUT, error: 'This interval is unavailable.' } });
          snapshot = { ...snapshot, rev: snapshot.rev + 1, values: { ...snapshot.values, ...change.patch } };
          if (acknowledgeSave !== undefined) return acknowledgeSave(snapshot);
        }
        return ok(snapshot);
      }
      if (request.path === '/api/v1/areas') return ok([
        { id: 'work', name: 'Work', color: '#5B8DEF', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 },
      ]);
      if (request.path === '/api/v1/tracks' && request.method === 'POST') {
        creates.push(request.body as CreateTrackRequest);
        return new Promise(() => undefined);
      }
      if (request.path.includes('/workspace-options')) return ok({
        defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }],
      });
      return ok(request.path.startsWith('/api/v1/bootstrap') ? bootstrap : []);
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const unauth = createUnauthorizedChannel({ enqueue: task => task() });
  const router = createAppRouter({ transport, unauthorized: unauth, client,
    cards: bootTestCardRuntime(), prepareReport: prepareReportDocument });
  router.update({ history: createMemoryHistory({ initialEntries: ['/settings/planners'] }) });
  render(<QueryClientProvider client={client}>
    <ThemeProvider storage={{ getItem: () => theme, setItem: () => undefined }}>
      <RouterProvider router={router} />
    </ThemeProvider>
  </QueryClientProvider>);
  return { patches, creates, router };
}

it.each([
  { theme: 'light', width: 1280, height: 800 },
  { theme: 'dark', width: 1280, height: 800 },
  { theme: 'light', width: 390, height: 844 },
] as const)(
  'renders $theme defaults above the provider checks at $width×$height with the saved TOML values', async ({ theme, width, height }) => {
    await page.viewport(width, height);
    const { patches } = openPlanners(theme);
    const defaults = page.getByRole('region', { name: 'Defaults' });
    const providers = page.getByRole('region', { name: 'Providers' });
    const timeout = defaults.getByRole('combobox', { name: 'Session idle timeout' });
    await expect.element(timeout).toHaveTextContent('45 min');
    await expect.element(defaults.getByRole('radio', { name: '85%' })).toHaveAttribute('aria-checked', 'true');
    await expect.element(providers.getByRole('button', { name: 'Recheck planners' })).toBeVisible();

    const dialog = page.getByRole('dialog', { name: 'Settings' }).element();
    await Promise.all(dialog.getAnimations().map(animation => animation.finished));
    expect(defaults.element().getBoundingClientRect().bottom).toBeLessThan(providers.element().getBoundingClientRect().top);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
    const controlWidth = timeout.element().parentElement!.getBoundingClientRect().width;
    const rootFontSize = Number.parseFloat(getComputedStyle(document.documentElement).fontSize);
    const expectedWidth = width === 390 ? defaults.element().getBoundingClientRect().width : 10.5 * rootFontSize;
    expect(controlWidth).toBeCloseTo(expectedWidth, 0);
    expect(patches).toEqual([]);

    await page.screenshot({ path: `../../../../test-results/settings-defaults-${theme}-${width}.png` });
    await timeout.click();
    await expect.element(page.getByRole('option', { name: '45 min' })).toHaveAttribute('aria-selected', 'true');
    await userEvent.keyboard('{Escape}');
    await expect.element(timeout).toHaveFocus();
  },
);

it('saves one typed field on selection and reports a rejected edit alongside the other defaults', async () => {
  await page.viewport(1280, 800);
  const { patches } = openPlanners('light', { rejectTimeout: true });
  const defaults = page.getByRole('region', { name: 'Defaults' });
  const provider = defaults.getByRole('combobox', { name: 'Default provider' });
  await expect.element(provider).toHaveTextContent('codex');
  await provider.click();
  await page.getByRole('option', { name: 'claude', exact: true }).click();
  await expect.element(provider).toHaveTextContent('claude');
  await expect.element(defaults.getByRole('radio', { name: '85%' })).toHaveAttribute('aria-checked', 'true');

  const timeout = defaults.getByRole('combobox', { name: 'Session idle timeout' });
  await expect.element(timeout).toHaveTextContent('45 min');
  await timeout.click();
  await page.getByRole('option', { name: '30 min' }).click();
  await expect.element(defaults.getByRole('alert')).toHaveTextContent('Could not save the setting. This interval is unavailable.');
  await expect.element(timeout).toHaveTextContent('45 min');
  expect(patches).toEqual([
    { expectedRev: 21, patch: { [PROVIDER]: 'claude' } },
    { expectedRev: 22, patch: { [TIMEOUT]: 30 } },
  ]);
});

it.each([
  { scenario: 'the first ready provider when stock Codex is unavailable', codexReady: false },
  { scenario: 'the newly saved Claude default when both providers are ready', codexReady: true },
])('shows and submits $scenario for an untouched New Track draft', async ({ codexReady }) => {
  await page.viewport(1280, 800);
  const { creates, router } = openPlanners('light', { codexReady });
  const provider = page.getByRole('combobox', { name: 'Default provider' });
  await expect.element(provider).toHaveTextContent('codex');
  if (codexReady) {
    await provider.click();
    await page.getByRole('option', { name: 'claude', exact: true }).click();
    await expect.element(provider).toHaveTextContent('claude');
  }

  await router.navigate({ to: '/area/$areaId/new', params: { areaId: 'work' } });
  await expect.element(page.getByRole('button', { name: 'Model: Claude Default' })).toBeVisible();
  await page.getByRole('combobox', { name: 'What this track should do' }).fill('Plan the release');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0]).toMatchObject({ provider: 'claude' });
});

it('reconciles an unconfirmed save before the next edit', async () => {
  await page.viewport(1280, 800);
  const { patches } = openPlanners('light', {
    acknowledgeSave: snapshot => Promise.resolve({
      status: 200, statusText: 'OK', body: snapshot.rev === 22 ? {} : snapshot,
    }),
  });
  const provider = page.getByRole('combobox', { name: 'Default provider' });
  await expect.element(provider).toHaveTextContent('codex');
  await provider.click();
  await page.getByRole('option', { name: 'claude', exact: true }).click();
  await expect.element(page.getByRole('alert')).toHaveTextContent('Could not confirm the save.');
  await expect.element(provider).toHaveTextContent('claude');
  const timeout = page.getByRole('combobox', { name: 'Session idle timeout' });
  await timeout.click();
  await page.getByRole('option', { name: '30 min' }).click();
  await expect.element(timeout).toHaveTextContent('30 min');
  expect(patches.map(patch => patch.expectedRev)).toEqual([21, 22]);
});

it('keeps keyboard focus on the provider selector during and after a pending save', async () => {
  await page.viewport(1280, 800);
  let releaseSave!: () => void;
  const pending = new Promise<void>(resolve => { releaseSave = resolve; });
  const { patches } = openPlanners('light', {
    acknowledgeSave: async snapshot => {
      await pending;
      return { status: 200, statusText: 'OK', body: snapshot };
    },
  });
  const provider = page.getByRole('combobox', { name: 'Default provider' });
  await provider.click();
  await userEvent.keyboard('{ArrowDown}{Enter}');
  await expect.poll(() => patches.length).toBe(1);
  await expect.element(provider).toHaveAttribute('aria-disabled', 'true');
  await expect.element(provider).toHaveFocus();
  await userEvent.keyboard('{Enter}');
  await expect.element(provider).toHaveAttribute('aria-expanded', 'false');

  releaseSave();
  await expect.element(provider).toHaveTextContent('claude');
  await expect.element(provider).toHaveFocus();
});
