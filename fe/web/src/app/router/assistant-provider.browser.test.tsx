import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { BootstrapDto } from '../../../../core/api/perga-v1.ts';
import { fixtureBootstrap } from './session-contract-fixture.ts';
import { renderAssistantProviderFixture } from './assistant-provider-fixture.tsx';

const claudeBootstrap = (): BootstrapDto => fixtureBootstrap(['claude'], ['selected']);
afterEach(async () => { cleanup(); document.getElementById('root')?.remove(); await page.viewport(1280, 800); });

it.each([1280, 390])('selects a dynamically registered backend through the keyboard and freezes retry (%ipx)', async width => {
  await page.viewport(width, 950);
  const bootstrap = claudeBootstrap();
  const fixture = renderAssistantProviderFixture({ bootstrap, failFirstCreate: true, mobile: width < 600 });
  if (width < 600) {
    await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
    await page.getByRole('button', { name: '发起新对话', exact: true }).click();
  } else await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  if (width < 600) await page.getByLabelText('对话选项').click();
  const model = page.getByRole('button', { name: /^Model:/ });
  await model.click();
  await expect.element(page.getByRole('radio', { name: 'selected', exact: true })).toBeVisible();
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?provider=claude')).toBe(true);
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?provider=codex')).toBe(false);
  await page.getByRole('radio', { name: 'selected', exact: true }).click();
  const slider = await page.getByRole('slider', { name: 'Reasoning effort' }).findElement();
  slider.focus();
  await userEvent.keyboard('{Home}{End}');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
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
  if (width < 600) await page.getByLabelText('对话选项').click();
  await page.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/, exact: true }).click();
  await expect.poll(() => posts().length).toBe(2);
  expect(posts()[1].body).toEqual(posts()[0].body);
  expect(posts()[1].headers).toEqual(posts()[0].headers);
  await expect.element(page.getByRole(width < 600 ? 'dialog' : 'complementary', { name: 'Created Assistant' })).toBeVisible();
  expect(fixture.rows[0].provider).toBe('claude');
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});

it('creates the declared Codex default despite auth/read and catalog unavailability', async () => {
  const bootstrap = fixtureBootstrap();
  bootstrap.providers[0] = { ...bootstrap.providers[0], available: false, reason: 'Account read failed' };
  const fixture = renderAssistantProviderFixture({ bootstrap });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await page.getByRole('button', { name: /^Model:/ }).click();
  await expect.element(page.getByRole('radio', { name: /^Default/ })).toBeEnabled();
  await page.getByRole('radio', { name: /^Default/ }).click();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
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
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.element(model).toHaveAccessibleName(/^Model: (?:opencode|OpenCode) selected/);
  await page.getByRole('combobox', { name: 'Message' }).fill('Owner registered backend');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions')).length).toBe(1);
  expect(fixture.requests.find(request => request.method === 'POST' && request.path.endsWith('/sessions'))?.body).toMatchObject({ provider: 'acp:opencode', model: 'selected' });
});

it('refreshes owner metadata before retrying a draft that never dispatched', async () => {
  const options = { bootstrap: fixtureBootstrap([]) };
  const fixture = renderAssistantProviderFixture(options);
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await page.getByRole('combobox', { name: 'Message' }).fill('Retry after registration');
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/, exact: true })).toBeVisible();
  expect(fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions'))).toHaveLength(0);
  options.bootstrap = fixtureBootstrap();
  await page.getByRole('button', { name: /^(Try again|Retry|Retry safely \(same key\))$/, exact: true }).click();
  await expect.poll(() => fixture.requests.filter(request => request.method === 'POST' && request.path.endsWith('/sessions')).length).toBe(1);
  expect(fixture.requests.some(request => request.path === '/api/v1/bootstrap?scope=managed&refresh=true')).toBe(true);
});
