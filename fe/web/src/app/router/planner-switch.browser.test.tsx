import { fixtureBootstrap, fixtureSession, fixtureTrack, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionPage, sessionItem } from './conversation-session-data.ts';
import type { ModelSelection } from '../../../../core/domain/conversation.ts';
import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import { renderPlannerSwitchFixture } from './planner-switch-fixture.tsx';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });
const posts = (fixture: ReturnType<typeof renderPlannerSwitchFixture>) => fixture.requests.filter(request => request.method === 'PATCH');
async function open() {
  await page.getByRole('button', { name: /Conversation Other conversation/ }).click();
  await expect.element(page.getByText('Ready for switching')).toBeVisible();
  return page.getByRole('combobox', { name: 'Message' });
}

it.each([1280, 390])('promotes through the real menu and Enter flow at width %s', async width => {
  await page.viewport(width, 844);
  const fixture = renderPlannerSwitchFixture(); const input = await open();
  await input.fill('/planner');
  await expect.element(page.getByRole('option', { name: /^planner / })).toBeVisible();
  await userEvent.keyboard('{Enter}'); expect(posts(fixture)).toHaveLength(0);
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => posts(fixture).map(request => request.body)).toEqual([{
    leadSessionId: 'second-planner',
  }]);
  await expect.poll(() => fixture.client.getQueryData<{ track: { leadSessionId: string } }>(['track', 'daily'])?.track.leadSessionId).toBe('second-planner');
  await expect.element(page.getByText('Ready for switching')).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation' }).click();
  await expect.element(page.getByRole('button', { name: /Conversation Other conversation/ })).toBeVisible();
  await expect.element(page.getByRole('button', { name: /Conversation Daily Planner conversation/ })).toBeVisible();
  expect(posts(fixture)).toHaveLength(1);
});

it('keeps the draft and shows a specific refusal', async () => {
  await page.viewport(1280, 800);
  const fixture = renderPlannerSwitchFixture({ switch: () => ({ status: 409, statusText: 'Conflict',
    body: { code: 'planner_open_ask', error: 'open question', card_id: 'second-planner' } }) });
  const input = await open(); await input.fill('/planner');
  await page.getByRole('option', { name: /^planner / }).click();
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('alert')).toHaveTextContent('Resolve the open questions first. (Target Assistant: Other conversation)');
  await expect.element(input).toHaveTextContent('/planner'); expect(posts(fixture)).toHaveLength(1);
});

it('rejects arguments without a write', async () => {
  await page.viewport(1280, 800);
  const fixture = renderPlannerSwitchFixture(); const input = await open(); await input.fill('/planner now');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect.element(page.getByRole('alert')).toHaveTextContent('Use /planner without arguments.');
  await expect.element(input).toHaveTextContent('/planner now'); expect(posts(fixture)).toHaveLength(0);
});

it('blocks another submission while the first request is pending', async () => {
  await page.viewport(1280, 800);
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => new Promise(resolve => {
    finish = () => { commit(); resolve(fixture.receipt()); };
  }) });
  const input = await open(); await input.fill('/planner');
  await page.getByRole('option', { name: /^planner / }).click(); await userEvent.keyboard('{Enter}');
  await expect.poll(() => posts(fixture).length).toBe(1);
  await input.fill('/planner'); await page.getByRole('option', { name: /^planner / }).click(); await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('alert')).toHaveTextContent('was not accepted');
  expect(posts(fixture)).toHaveLength(1); finish();
  await expect.poll(() => fixture.snapshot().track.leadSessionId).toBe('second-planner');
  expect(posts(fixture)).toHaveLength(1);
});

it('dispatches a direct mobile dock submission and retains a refused command draft', async () => {
  await page.viewport(390, 844);
  const fixture = renderPlannerSwitchFixture({ switch: () => ({ status: 409, statusText: 'Conflict',
    body: { code: 'planner_open_ask', error: 'open question', card_id: 'second-planner' } }) });
  const input = await open(); await input.fill('/planner');
  await page.getByRole('button', { name: 'Close conversation' }).click();
  await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toHaveValue('/planner');
  await page.getByRole('button', { name: 'Send chat message' }).click();
  await expect.element(page.getByRole('alert')).toHaveTextContent('Resolve the open questions first.');
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toHaveValue('/planner');
  expect(posts(fixture).map(request => request.path)).toEqual(['/api/v1/tracks/daily']);
});

it.each(['mounted', 'remounted', 'unchanged'] as const)('dock clears only its owned draft after pending success (%s)', async mode => {
  await page.viewport(390, 844);
  let finish!: () => void;
  const fixture = renderPlannerSwitchFixture({ switch: (_request, commit) => new Promise(resolve => {
    finish = () => { commit(); resolve(fixture.receipt()); };
  }) });
  const input = await open(); await input.fill('/planner');
  await page.getByRole('button', { name: 'Close conversation' }).click();
  await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
  const dock = page.getByRole('textbox', { name: 'Chat message', exact: true });
  const oldDock = dock.element();
  await page.getByRole('button', { name: 'Send chat message' }).click();
  await expect.poll(() => posts(fixture).length).toBe(1);
  if (mode === 'mounted') {
    // Update through the textarea without focusing it (focus opens the full composer).
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(oldDock, 'New draft');
    oldDock.dispatchEvent(new Event('input', { bubbles: true }));
    await expect.element(dock).toHaveValue('New draft');
  }
  if (mode === 'remounted') {
    await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'project' } });
    expect(oldDock.isConnected).toBe(false);
    await fixture.router.navigate({ to: '/track/$trackId', params: { trackId: 'daily' }, search: { panel: 'conversations' } });
    await (await open()).fill('New draft');
  }
  finish();
  await expect.poll(() => fixture.client.getQueryData<{ track: { leadSessionId: string } }>(['track', 'daily'])?.track.leadSessionId).toBe('second-planner');
  if (mode === 'remounted') await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('New draft');
  else await expect.element(dock).toHaveValue(mode === 'mounted' ? 'New draft' : '');
});

it('saves and reads back null effort on an existing session without materializing low', async () => {
  await page.viewport(1280, 800);
  let saved: ModelSelection = { model: 'test-model', reasoning_effort: null };
  const readbacks: (string | null)[] = [];
  let wrote = false;
  const session = () => fixtureSession('second-planner', 'daily', { model: saved.model, reasoningEffort: saved.reasoning_effort });
  const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
  const fixture = renderPlannerSwitchFixture({ reply: request => {
    if (request.path.startsWith('/api/v1/bootstrap')) {
      const bootstrap = fixtureBootstrap(['codex'], ['test-model']);
      bootstrap.providers[0].modelCatalog = [{ model: 'test-model', displayName: 'Test model', description: '', isDefault: true,
        defaultReasoningEffort: null, supportedReasoningEfforts: [{ reasoningEffort: 'low', description: '' }, { reasoningEffort: 'high', description: '' }] }];
      return ok(bootstrap);
    }
    if (request.method === 'PATCH' && request.path === '/api/v1/sessions/second-planner') {
      const body = request.body as { model: string | null; reasoningEffort: string | null };
      saved = { model: body.model, reasoning_effort: body.reasoningEffort };
      wrote = true;
      return ok(session());
    }
    if (request.path.startsWith('/api/v1/sessions/second-planner/items?')) {
      if (wrote && request.path.includes('limit=1')) readbacks.push(saved.reasoning_effort);
      return ok(sessionPage(session(), [sessionItem('second-planner', 1, 'Ready for switching')]));
    }
    return undefined;
  } });
  await open();
  const trigger = page.getByRole('button', { name: /^Model:/ });
  await trigger.click();
  const slider = page.getByRole('slider', { name: 'Reasoning effort' });
  await expect.element(slider).toHaveAttribute('aria-valuetext', 'Default');
  (slider.element() as HTMLElement).focus();
  await userEvent.keyboard('{End}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'PATCH').map(request => request.body))
    .toEqual([{ model: 'test-model', reasoningEffort: 'high' }]);
  await userEvent.keyboard('{Backspace}');
  await expect.poll(() => fixture.requests.filter(request => request.method === 'PATCH').map(request => request.body))
    .toEqual([
      { model: 'test-model', reasoningEffort: 'high' },
      { model: 'test-model', reasoningEffort: null },
    ]);
  await expect.poll(() => readbacks.at(-1)).toBeNull();
  await expect.poll(() => fixture.client.getQueryData<{ session: { reasoningEffort: string | null } }>(['planner-run', 'second-planner', 'conversation_v1'])?.session.reasoningEffort).toBeNull();
  await userEvent.keyboard('{Escape}');
  await trigger.click();
  await expect.element(page.getByRole('slider', { name: 'Reasoning effort' })).toHaveAttribute('aria-valuetext', 'Default');
  expect(saved.reasoning_effort).toBeNull();
});


it('defaults the mobile dock to Lead and keeps an explicitly switched conversation draft with its session', async () => {
  await page.viewport(390, 844);
  const lead = fixtureSession('daily-planner', 'daily', { isLead: true, title: 'Daily Planner conversation' });
  const sibling = fixtureSession('second-planner', 'daily', { title: 'Other conversation' });
  renderPlannerSwitchFixture({ initial: '/track/daily', reply: request => {
    const ok = (body: unknown) => ({ status: 200, statusText: 'OK', body });
    if (request.path === '/api/v1/tracks/daily') return ok(fixtureSnapshot(
      fixtureTrack('daily', { leadSessionId: lead.id }), [lead, sibling]));
    if (request.path === '/api/v1/tracks/daily/sessions') return ok([lead, sibling]);
    if (request.path.includes('/second-planner/items?')) return ok(sessionPage(sibling));
    return undefined;
  } });
  const dock = page.getByRole('textbox', { name: 'Chat message', exact: true });
  await dock.click();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  const settingsTrigger = page.getByRole('button', { name: /^Session model and mode/ });
  await expect.element(settingsTrigger).toBeVisible();
  await settingsTrigger.click();
  const card = page.getByRole('dialog', { name: 'Session model and mode', exact: true });
  await expect.element(card).toBeVisible();
  expect(card.element().tagName).not.toBe('DIALOG');
  await userEvent.keyboard('{Escape}');
  await expect.element(card).not.toBeInTheDocument();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  // Opening ConversationSwitcherSheet from inside Chat Sheet and pressing Escape closes only the switcher.
  const inSheetRoleSlot = page.getByRole('button', { name: /^Switch conversation,/ });
  await inSheetRoleSlot.click();
  await expect.element(page.getByRole('dialog', { name: 'Switch conversation', exact: true })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('dialog', { name: 'Switch conversation', exact: true })).not.toBeInTheDocument();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await page.getByRole('button', { name: /^Switch conversation,/ }).click();
  await page.getByRole('button', { name: /Other conversation/ }).click();
  await expect.element(page.getByRole('dialog', { name: 'Other conversation', exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Message', exact: true }).fill('Retain the switched conversation draft');
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.element(dock).toHaveValue('Retain the switched conversation draft');
  await expect.element(page.getByRole('button', { name: 'Send chat message', exact: true })).toBeEnabled();
  await dock.click();
  await expect.element(page.getByRole('dialog', { name: 'Other conversation', exact: true })).toBeVisible();
});
