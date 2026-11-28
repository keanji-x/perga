import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { BootstrapDto } from '../../../../core/api/perga-v1.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { fixtureBootstrap } from './session-contract-fixture.ts';
import { renderAssistantProviderFixture } from './assistant-provider-fixture.tsx';

const claudeBootstrap = (): BootstrapDto => fixtureBootstrap(['claude'], ['selected']);
beforeEach(async () => { await page.viewport(1280, 800); });
afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

it.each([1280, 390])('selects a dynamically registered backend through the keyboard and freezes retry (%ipx)', async width => {
  await page.viewport(width, 950);
  const bootstrap = claudeBootstrap();
  const fixture = renderAssistantProviderFixture({ bootstrap, failFirstCreate: true, mobile: width < 600 });
  if (width < 600) await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  const model = page.getByRole('button', { name: width < 600 ? /^Session model and mode:/ : /^Model:/ });
  await model.click();
  const selected = page.getByRole('radio', width < 600 ? { name: /^selected(?:\s|$)/ } : { name: 'selected', exact: true });
  await expect.element(selected).toBeVisible();
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?provider=claude')).toBe(true);
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?provider=codex')).toBe(false);
  if (width < 600) {
    selected.element().focus();
    await userEvent.keyboard(' ');
    await expect.element(selected).toHaveAttribute('aria-checked', 'true');
    await page.getByRole('button', { name: 'Close session model and mode' }).click();
  } else await selected.click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Frozen first message');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/, exact: true })).toBeVisible();
  await expect.element(model).toBeDisabled();
  const posts = () => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'));
  expect(posts()).toHaveLength(1);
  expect(posts()[0].body).toMatchObject({ provider: 'claude', model: 'selected', prompt: 'Frozen first message' });
  bootstrap.providers[0] = { ...bootstrap.providers[0], available: false, reason: 'Now unavailable' };
  await fixture.client.invalidateQueries({ queryKey: ['managed-providers'] });
  await page.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/, exact: true }).click();
  await expect.poll(() => posts().length).toBe(2);
  expect(posts()[1].body).toEqual(posts()[0].body);
  expect(posts()[1].headers).toEqual(posts()[0].headers);
  await expect.element(page.getByRole(width < 600 ? 'dialog' : 'complementary', { name: 'Created Assistant' })).toBeVisible();
  expect(fixture.rows[0].provider).toBe('claude');
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});

it.each([1280, 390])('creates the declared Codex default despite auth/read and catalog unavailability (%ipx)', async width => {
  await page.viewport(width, 844);
  const bootstrap = fixtureBootstrap();
  bootstrap.providers[0] = { ...bootstrap.providers[0], available: false, reason: 'Account read failed' };
  const fixture = renderAssistantProviderFixture({ bootstrap, mobile: width < 600 });
  if (width < 600) await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await page.getByRole('button', { name: width < 600 ? /^Session model and mode:/ : /^Model:/ }).click();
  await expect.element(page.getByText('Account read failed', { exact: false })).toBeVisible();
  expect(page.getByText('A track can still be created', { exact: false }).query()).toBeNull();
  await page.getByRole('radio', { name: /^Default/ }).click();
  if (width < 600) await page.getByRole('button', { name: 'Close session model and mode' }).click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await field.fill('Follow the backend default'); await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions')).length).toBe(1);
  expect(fixture.requests.find(request => request.method === 'POST' && request.path.endsWith('/sessions'))?.body).toMatchObject({ provider: 'codex', prompt: 'Follow the backend default' });
});


it('renders, selects and sends a managed identifier independently of legacy UI labels', async () => {
  const fixture = renderAssistantProviderFixture({ bootstrap: fixtureBootstrap(['acp:opencode'], ['selected']) });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  const model = page.getByRole('button', { name: /^Model:/ });
  await model.click();
  await page.getByRole('radio', { name: 'selected', exact: true }).click();
  await expect.element(model).toHaveAccessibleName(/^Model: (?:opencode|OpenCode) selected/);
  await page.getByRole('combobox', { name: 'Message' }).fill('Owner registered backend');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions')).length).toBe(1);
  expect(fixture.requests.find(request => request.method === 'POST' && request.path.endsWith('/sessions'))?.body).toMatchObject({ provider: 'acp:opencode', model: 'selected' });
});

it.each(['pending', 'failed'] as const)('sends the first draft message with bootstrap %s', async bootstrapRead => {
  const fixture = renderAssistantProviderFixture({ bootstrap: fixtureBootstrap(), bootstrapRead });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.poll(() => fixture.requests.some(request => request.path === '/api/v1/bootstrap?scope=managed')).toBe(true);
  const status = bootstrapRead === 'pending' ? 'pending' : 'error';
  await expect.poll(() => fixture.client.getQueryState(queryKeys.managedProviders())?.status).toBe(status);
  await page.getByRole('combobox', { name: 'Message' }).fill('Send the first message');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST').map(request => [request.path, request.body]))
    .toEqual([['/api/v1/tracks/provider-track/sessions', expect.objectContaining({ prompt: 'Send the first message', provider: 'codex' })]]);
  await expect.element(page.getByRole('complementary', { name: 'Created Assistant' })).toBeVisible();
});

it('opens a draft without requesting server version', async () => {
  const fixture = renderAssistantProviderFixture({ bootstrap: fixtureBootstrap() });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.poll(() => fixture.client.getQueryState(queryKeys.managedProviders())?.status).toBe('success');
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap')).toBe(false);
});

it.each([
  { refresh: 'succeeds', failBootstrapRefresh: false },
  { refresh: 'fails', failBootstrapRefresh: true },
])('retries a draft refused for a missing provider when refresh $refresh', async ({ failBootstrapRefresh }) => {
  const options = { bootstrap: fixtureBootstrap([]), failBootstrapRefresh };
  const fixture = renderAssistantProviderFixture(options);
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.poll(() => fixture.client.getQueryData(queryKeys.managedProviders())).toEqual([]);
  await page.getByRole('combobox', { name: 'Message' }).fill('Retry the original draft');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByText('Managed provider capabilities are unavailable. Reconnect before sending.')).toBeVisible();
  expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'))).toHaveLength(0);
  if (!failBootstrapRefresh) options.bootstrap = fixtureBootstrap();
  await page.getByRole('button', { name: 'Try again', exact: true }).click();
  await expect.poll(() => fixture.requests.filter(request => request.path === '/api/v1/bootstrap?scope=managed&refresh=true' || request.method === 'POST')
    .map(request => request.path)).toEqual(['/api/v1/bootstrap?scope=managed&refresh=true', '/api/v1/tracks/provider-track/sessions']);
  await expect.element(page.getByRole('complementary', { name: 'Created Assistant' })).toBeVisible();
});
