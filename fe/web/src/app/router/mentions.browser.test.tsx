import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionItem, sessionPage, sessionTurn, sessionInput } from './conversation-session-data.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// `@` on the real routes, in a real engine (#1881 S2): the Planner's conversation and the
// new-track sentence offer the area's tags, reports and blocks from the mentions endpoint; a
// track's assistant conversation, without an Area candidate context, offers no Report picks.
import '../../styles/entry.css';
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
    if (request.method === 'POST' && request.path === '/api/v1/tracks') return new Promise<ApiTransportResponse>(() => undefined);
    if (request.path === '/api/plugins') return Promise.resolve(ok([{
      id: 'dev.example', version: '1', enabled: false, state: 'disabled',
      manifest_name: 'development', manifest_description: pluginDescription,
      has_config: false, can_uninstall: false, can_disable: true,
    }]));
    if (request.path === '/api/v1/templates') return Promise.resolve(ok(templates.map(template => ({ id: template.id, title: template.title, description: template.description }))));
    if (request.path.startsWith('/api/v1/bootstrap')) return Promise.resolve(ok({ version: 'mentions', providers: [{ provider: 'codex', available: true, models: [] }], areas: [], templates: templates.map(template => ({ id: template.id, title: template.title, description: template.description })) }));
    if (request.path === '/api/v1/areas') return Promise.resolve(ok([AREA]));
    if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return Promise.resolve(ok([TRACK]));
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

it('groups the area\'s tags, reports and blocks under @ in the Planner conversation, and sends the pick', async () => {
  await page.viewport(1440, 900);
  const { requests, mentionReads } = mount('/track/w1');
  await openConversation(/Conversation Planner chat/);

  await userEvent.keyboard('see @dep');
  await expect.element(menu().getByRole('group', { name: 'Blocks' }).getByRole('option')).toBeVisible();
  expect(await groupOrder()).toEqual(['Tags', 'Tracks', 'Blocks']);
  expect(mentionReads().at(-1)).toBe('/api/areas/c1/mentions?q=dep&track=w1');

  await userEvent.keyboard('{ArrowDown}{ArrowDown}{Enter}');
  await expect.element(menu()).not.toBeInTheDocument();
  await expect.element(page.getByText('Deploy notes › Rollback')).toBeVisible();
  await userEvent.keyboard('{Enter}');

  await expect.poll(() => requests.filter((request) => request.path.endsWith('/messages')).length).toBe(1);
  const sent = requests.find((request) => request.path.endsWith('/messages'))!.body as { content: string };
  expect(sent.content).toBe(`see ${BLOCK_INSERT}`);
  await expect.element(page.getByText('Deploy notes › b_1a2b', { exact: true }).first()).toBeVisible();
  await expect.poll(() => document.querySelectorAll('[data-nc-turn="human"]')).toHaveLength(1);
  expect(document.querySelector('[data-nc-turn="human"]')?.textContent).toBe('see Deploy notes › b_1a2b');
  // A fresh route has no memory of the menu pick: the stored address still renders as a pill.
  cleanup();
  mount('/track/w1', sent.content);
  await openConversation(/Conversation Planner chat/);
  await expect.element(page.getByText('Deploy notes › b_1a2b', { exact: true }).first()).toBeVisible();
  expect(document.querySelector('[data-nc-sent-mention]')?.getAttribute('title')).toBe(BLOCK_INSERT);
});

/** The group headings the open menu shows, top to bottom. */
async function groupOrder(): Promise<(string | null)[]> {
  return [...(await menu().findElement()).querySelectorAll('[role="group"]')].map((group) => group.getAttribute('aria-label'));
}

it.each([
  ['#', 'Tags', '#部署', CANDIDATES.tags[0].insert],
  ['/', 'Tracks', 'Deploy notes', CANDIDATES.tracks[0].insert],
  ['>', 'Blocks', 'Deploy notes › Rollback', BLOCK_INSERT],
])('narrows @%s to %s, searches without the prefix, and sends the pick\'s insert', async (prefix, group, chip, insert) => {
  await page.viewport(1440, 900);
  const { requests, mentionReads } = mount('/track/w1');
  await openConversation(/Conversation Planner chat/);

  await userEvent.keyboard(`see @${prefix}`);
  await expect.element(menu().getByRole('group', { name: group }).getByRole('option').first()).toBeVisible();
  expect(await groupOrder()).toEqual([group]);
  expect(mentionReads()).toEqual(['/api/areas/c1/mentions?q=&track=w1']);
  await expect.element(page.getByRole('listbox', { name: 'Commands' })).not.toBeInTheDocument();

  await userEvent.keyboard('dep');
  await expect.poll(() => mentionReads().at(-1)).toBe('/api/areas/c1/mentions?q=dep&track=w1');
  await expect.element(menu().getByRole('group', { name: group }).getByRole('option').first()).toBeVisible();
  expect(await groupOrder()).toEqual([group]);
  await expect.element(page.getByRole('listbox', { name: 'Commands' })).not.toBeInTheDocument();

  await userEvent.keyboard('{Enter}');
  await expect.element(menu()).not.toBeInTheDocument();
  await expect.element(page.getByText(chip, { exact: true })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => requests.filter((request) => request.path.endsWith('/messages')).length).toBe(1);
  const sent = requests.find((request) => request.path.endsWith('/messages'))!.body as { content: string };
  expect(sent.content).toBe(`see ${insert}`);
});

it('keeps the / command in the Planner conversation', async () => {
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
  await userEvent.keyboard('ask @dep');
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
  await userEvent.keyboard('Continue @roll');
  await expect.element(menu().getByRole('option').first()).toBeVisible();
  await expectComboboxWithoutMultiline('What this track should do');
  await page.getByRole('button', { name: 'Create track' }).click();
  await expectComboboxWithoutMultiline('What this track should do');
});

it('offers disabled plugin documentation in an assistant conversation without area report access', async () => {
  const { requests, mentionReads } = mount('/track/w1');
  await openConversation(/Conversation Side chat/);
  await userEvent.keyboard('@de');
  await expect.element(menu().getByRole('option', { name: /development/ })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toHaveTextContent('development');
  expect(mentionReads()).toEqual([]);
  expect(requests.some(request => request.method !== 'GET' && request.path.includes('/plugins'))).toBe(false);
});

it('offers @ in the new-track sentence, lets Enter pick, and creates with the insert in the first message', async () => {
  await page.viewport(1440, 900);
  const { requests, mentionReads } = mount('/area/c1/new');
  const field = page.getByRole('combobox', { name: 'What this track should do' });
  await field.click();
  await userEvent.keyboard('Continue @roll');
  await expect.element(menu().getByRole('option', { name: /Rollback/ })).toBeVisible();
  expect(mentionReads().at(-1)).toBe('/api/areas/c1/mentions?q=roll');

  /* Attached under the composer at its full width, clear of the greeting above it. */
  const popover = (await menu().findElement()).closest('[popover]')!.getBoundingClientRect();
  const composer = document.querySelector('[data-nc-new-track-message]')!.getBoundingClientRect();
  const greeting = (await page.getByRole('heading', { level: 1 }).findElement()).getBoundingClientRect();
  expect(Math.round(popover.left)).toBe(Math.round(composer.left));
  expect(Math.round(popover.right)).toBe(Math.round(composer.right));
  expect(popover.top).toBeGreaterThanOrEqual(composer.bottom - 1);
  expect(popover.top).toBeLessThanOrEqual(composer.bottom + 12);
  expect(popover.top).toBeGreaterThan(greeting.bottom);

  await userEvent.keyboard('{ArrowUp}{Enter}');
  await expect.element(menu()).not.toBeInTheDocument();
  expect(requests.some((request) => request.method === 'POST' && request.path === '/api/v1/tracks')).toBe(false);

  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => requests.filter((request) => request.method === 'POST' && request.path === '/api/v1/tracks').length).toBe(1);
  const created = requests.find((request) => request.method === 'POST' && request.path === '/api/v1/tracks')!.body as { first_message?: string };
  expect(created.first_message).toBeUndefined();
});

it('includes plugin documentation in a new conversation first message without report reads', async () => {
  const { requests, mentionReads } = mount('/track/w1');
  await openConversation(/Conversation Side chat/);
  const writesBeforeCompletion = requests.filter(request => ['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method));
  await userEvent.keyboard('/new');
  await expect.element(page.getByRole('listbox', { name: 'Commands' }).getByRole('option')).toBeVisible();
  await userEvent.keyboard('{Enter}');
  const field = page.getByRole('combobox', { name: 'Message' });
  await expect.element(field).toHaveTextContent('/new');
  await expect.element(page.getByRole('complementary', { name: 'Untitled', exact: true })).not.toBeInTheDocument();
  expect(requests.filter(request => ['POST', 'PATCH', 'PUT', 'DELETE'].includes(request.method))).toEqual(writesBeforeCompletion);
  await userEvent.keyboard('{Enter}');
  await expect.element(page.getByRole('complementary', { name: 'Untitled', exact: true })).toBeVisible();
  await field.click();
  await userEvent.keyboard('@development');
  await expect.element(menu().getByRole('option', { name: /development/ })).toBeVisible();
  await userEvent.keyboard('{Enter}');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => requests.find(request => request.method === 'POST' && request.path === '/api/v1/tracks/w1/sessions')).toBeDefined();
  const sent = requests.find(request => request.method === 'POST' && request.path === '/api/v1/tracks/w1/sessions');
  expect(JSON.stringify(sent?.body)).toContain('Develop issues and publish changes.');
  expect(JSON.stringify(sent?.body)).toContain('documentation only');
  expect(mentionReads()).toEqual([]);
  expect(requests.some(request => request.method !== 'GET' && request.path.includes('/plugins'))).toBe(false);
});

it('shows the plugin name for a long guide and sends the complete description', async () => {
  await page.viewport(1440, 900);
  const description = 'Develop issues and publish changes. ' + 'A detailed guide for working on repository issues. '.repeat(6);
  const { requests } = mount('/track/w1', null, description);
  await openConversation(/Conversation Planner chat/);
  await userEvent.keyboard('@development');
  await expect.element(menu().getByRole('option', { name: /development/ })).toBeVisible();
  await menu().getByRole('option', { name: /development/ }).click();
  const field = page.getByRole('combobox', { name: 'Message' });
  await expect.poll(() => field.element().querySelector('[data-astryx-token]')?.textContent).toBe('development');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => requests.find(request => request.method === 'POST' && request.path.endsWith('/messages'))).toBeDefined();
  expect(JSON.stringify(requests.find(request => request.method === 'POST' && request.path.endsWith('/messages'))?.body)).toContain(description.trim());
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
