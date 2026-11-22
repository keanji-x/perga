import '../../styles/entry.css';
import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionItem, sessionPage, sessionTurn, sessionInput } from './conversation-session-data.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// `@` on the real routes, in a real engine (#1881 S2): the Planner's conversation and the
// new-track sentence offer the area's tags, reports and blocks from the mentions endpoint; a
// track's assistant conversation, without an Area candidate context, offers no Report picks.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import type { TrackTemplate } from '../../../../core/domain/track.ts';
import type { MentionCandidates } from '../../../../core/api/generated/wire.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

afterEach(async () => { cleanup(); await page.viewport(1280, 720); });

const AREA = { id: 'c1', name: 'Work', color: '#5B8DEF', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Test track' });
const PLANNER_CARD = fixtureSession('card-planner', 'w1', { title: 'Planner chat', isLead: true });
const ASSISTANT_ROW = fixtureSession('conv-assistant-1', 'w1', { title: 'Side chat', updatedAtMs: 30 });
const BLOCK_INSERT = '@`/研发/Deploy notes/report.md#b_1a2b`';
const CANDIDATES: MentionCandidates = {
  tags: [{ label: '部署', track_count: 2, insert: '@`tag:部署`' }],
  tracks: [{ label: 'Deploy notes', track_id: 'w9', insert: '@`/研发/Deploy notes/report.md`' }],
  blocks: [{ label: 'Rollback', block_id: 'b_1a2b', track_title: 'Deploy notes', track_id: 'w9', insert: BLOCK_INSERT }],
};

function mount(path: string, storedText: string | null = null, pluginDescription = 'Develop issues and publish changes.', running = false, templates: readonly TrackTemplate[] = []) {
  const requests: ApiRequest[] = [];
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { send(request) {
    requests.push(request);
    if (request.path.startsWith('/api/areas/c1/mentions?q=bob')) return Promise.resolve(ok({ tags: [], tracks: [], blocks: [] }));
    if (request.path.startsWith('/api/areas/c1/mentions?')) return Promise.resolve(ok(CANDIDATES));
    if (request.path.includes('/workspace-options')) return Promise.resolve({ status: 200, statusText: 'OK', body: { defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] } });
    if (request.method === 'POST' && request.path === '/api/v1/tracks') return new Promise<ApiTransportResponse>(() => undefined);
    if (request.path === '/api/plugins') return Promise.resolve(ok([{
      id: 'dev.example', version: '1', enabled: false, state: 'disabled',
      manifest_name: 'development', manifest_description: pluginDescription,
      has_config: false, can_uninstall: false, can_disable: true,
    }]));
    if (request.path === '/api/v1/templates') return Promise.resolve(ok(templates.map(template => ({ id: template.id, title: template.title, description: template.description }))));
    if (request.path.startsWith('/api/v1/bootstrap')) return Promise.resolve(ok({ version: 'mentions', providers: [{ provider: 'codex', available: true, models: [] }], areas: [], templates: templates.map(template => ({ id: template.id, title: template.title, description: template.description })) }));
    if (request.path === '/api/v1/areas') return Promise.resolve(ok([AREA]));
    if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return Promise.resolve(ok([TRACK, fixtureTrack('w9', { areaId: 'c1', title: 'Deploy notes' })]));
    if (request.path === '/api/v1/tracks/w1') return Promise.resolve(ok(fixtureSnapshot(TRACK, [PLANNER_CARD, ASSISTANT_ROW])));
    if (request.path === '/api/v1/tracks/w1/sessions') return Promise.resolve(ok([ASSISTANT_ROW]));
    if (request.path.endsWith('/messages')) {
      storedText = (request.body as { content: string }).content;
      return Promise.resolve(ok(sessionInput(conversationCardId(request.path), 'input-1')));
    }
    if (request.path.endsWith('/control')) return Promise.resolve(ok({ interrupted: true }));
    if (request.path.includes('/items?')) {
      const id = conversationCardId(request.path);
      return Promise.resolve(ok(sessionPage(id === PLANNER_CARD.id ? PLANNER_CARD : ASSISTANT_ROW,
        running ? Array.from({ length: 5 }).flatMap((_, index) => [sessionItem(id, index * 2 + 1, `userMessage ${index}`, 'user'), sessionItem(id, index * 2 + 2, `agentMessage ${index}`)])
          : storedText === null ? [] : [sessionItem(id, 1, storedText, 'user')], running ? sessionTurn(id) : null)));
    }
    if (request.path === '/api/settings') return Promise.resolve(ok({}));
    return Promise.resolve(ok([]));
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(), unauthorized, onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: [path] }) });
  render(<QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider>);
  const mentionReads = () => requests.filter((request) => request.path.startsWith('/api/areas/c1/mentions?')).map((request) => request.path);
  return { requests, mentionReads };
}

async function openConversation(name: RegExp) {
  await page.getByRole('button', { name }).click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await expect.element(field).toBeVisible();
  if (field.element().getAttribute('contenteditable') === 'true') await field.click();
}

const menu = () => page.getByRole('listbox', { name: 'Mention' });

it('keeps the / command in the Planner conversation', async () => {
  await page.viewport(1440, 900);
  mount('/track/w1');
  await openConversation(/Conversation Planner chat/);
  await userEvent.keyboard('/');
  await expect.element(page.getByRole('listbox', { name: 'Commands' }).getByRole('option', { name: /^new/ })).toBeVisible();
});

it.each([
  ['@', 'ask @bob', 'No matches'],
  ['/', 'check /tmp/x', 'No command by that name'],
])('sends over an empty %s menu once and keeps the Planner drawer open', async (_, text, empty) => {
  await page.viewport(1440, 900);
  const { requests } = mount('/track/w1');
  await openConversation(/Conversation Planner chat/);
  await userEvent.keyboard(text);
  await expect.element(page.getByText(empty)).toBeVisible();
  await userEvent.keyboard('{Enter}');
  const sends = () => requests.filter((request) => request.path.endsWith('/messages'))
    .map((request) => (request.body as { content: string }).content);
  await expect.poll(sends).toEqual([text]);
  /* A frame for a close the send might have set off. */
  await new Promise((resolve) => { requestAnimationFrame(resolve); });
  expect(document.querySelector('[data-nc-escape-layer]')).not.toBeNull();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toBeInTheDocument();
  expect(sends()).toEqual([text]);
});

/** axe aria-allowed-attr: a combobox may not carry `aria-multiline` (#1891 drops the workaround). */
async function expectComboboxWithoutMultiline(name: string) {
  const field = await page.getByRole('combobox', { name }).findElement();
  await expect.poll(() => field.hasAttribute('aria-multiline')).toBe(false);
}

it('keeps aria-multiline off both trigger-bearing fields through mount, typing and a send', async () => {
  await page.viewport(1440, 900);
  const { requests } = mount('/track/w1');
  await openConversation(/Conversation Planner chat/);
  await expectComboboxWithoutMultiline('Message');
  await userEvent.keyboard('ask @/Deploy');
  await expect.element(menu().getByRole('option').first()).toBeVisible();
  await expectComboboxWithoutMultiline('Message');
  await userEvent.keyboard('{Escape}{Enter}');
  await expect.poll(() => requests.filter((request) => request.path.endsWith('/messages')).length).toBe(1);
  await expectComboboxWithoutMultiline('Message');
  cleanup();

  mount('/area/c1/new');
  const sentence = page.getByRole('combobox', { name: 'What this track should do' });
  await expectComboboxWithoutMultiline('What this track should do');
  await sentence.click();
  await userEvent.keyboard('Continue @/Deploy');
  await expect.element(menu().getByRole('option').first()).toBeVisible();
  await expectComboboxWithoutMultiline('What this track should do');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expectComboboxWithoutMultiline('What this track should do');
});

it('dismisses a native navigation preview before Escape can stop a working Planner', async () => {
  await page.viewport(1440, 900);
  const { requests } = mount('/track/w1', null, 'Development', true);
  await openConversation(/Conversation Planner chat/);
  await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  const marker = page.getByRole('button', { name: 'Jump to exchange 3: userMessage 2' });
  await marker.hover();
  await expect.poll(() => document.querySelector('[data-nc-rail-preview]')).not.toBeNull();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => document.querySelector('[data-nc-rail-preview]')).toBeNull();
  expect(requests.filter(request => request.path.endsWith('/control'))).toHaveLength(0);
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => requests.filter(request => request.path.endsWith('/control')).length).toBe(1);
});

it('loads v1 template documentation after @ and preserves its chip in the first message', async () => {
  await page.viewport(1440, 900);
  const { requests } = mount('/area/c1/new', null, '', false, [{
    id: 'small-change', title: 'Small change', description: 'Implement a focused change',
    availability: { status: 'ready' }, tasks: [],
  }]);
  const field = page.getByRole('combobox', { name: 'What this track should do' });
  await expect.element(field).toBeVisible();
  expect(requests.some(request => request.path === '/api/v1/templates')).toBe(false);
  await field.click();
  await userEvent.keyboard('Use @');
  await expect.element(menu().getByRole('option', { name: /^Templates/ })).toBeVisible();
  await menu().getByRole('option', { name: /^Templates/ }).click();
  await expect.element(field).toHaveTextContent('Use @:');
  await expect.element(menu().getByRole('option', { name: /Small change/ })).toBeVisible();
  await menu().getByRole('option', { name: /Small change/ }).click();
  expect(field.element().querySelector('[data-astryx-token]')?.textContent).toBe('Small change');
  await page.screenshot({ path: './__screenshots__/new-track-template-mention.png' });
  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => requests.filter(request => request.method === 'POST' && request.path === '/api/v1/tracks').length).toBe(1);
  expect(requests.find(request => request.method === 'POST' && request.path === '/api/v1/tracks')!.body).toMatchObject({
    areaId: 'c1',
    provider: 'codex',
  });
  expect(requests.find(request => request.method === 'POST' && request.path === '/api/v1/tracks')!.body).not.toHaveProperty('first_message');
});
