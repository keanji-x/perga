import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionPage, sessionItem, sessionTurn } from './conversation-session-data.ts';
import '../../styles/entry.css';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider, type AnyRoute } from '@tanstack/react-router';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { trackReportLinkUrl } from '../../../../core/domain/report.ts';
import { createEvidencePreviewTransport } from '../shell/link-preview-data.ts';
import { createAppRouter, APP_BASEPATH } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it } from 'vitest';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { Profiler } from 'react';
import { useConversationCommands, type ConversationRegistry } from '../conversations/public.tsx';
import { conversationCardId } from './conversation-read-data.ts';

afterEach(() => { cleanup(); document.getElementById('root')?.remove(); });

async function setup({ streaming = false, historySuffix = '', narrow = false, fileText = '# Chat file contents' } = {}) {
  await page.viewport(narrow ? 390 : 1440, narrow ? 844 : 900);
  const requests: ApiRequest[] = [];
  const evidence = createEvidencePreviewTransport();
  const track = fixtureTrack('link-preview', { areaId: 'examples', title: 'Chat preview', cwd: '/preview' });
  const card = fixtureSession('chat-card', track.id, { title: 'Link chat', isLead: true });
  const other = fixtureSession('chat-b', track.id, { title: 'Other chat' });
  const live = { text: 'Streaming reply starts.' };
  const text = '[**Chat source**](neige://source/src_0971fbde#q1) · [Chat report](' + trackReportLinkUrl('reference-demo') + ') · [Chat file](./notes.md) · [Chat website](https://example.com) · [Today](/next/) · [Legacy Today](/next/today) · [Outside](/etc/passwd) · [Escape](../../secret.txt) · [Email](mailto:reader@example.com) · [Phone](tel:+123) · [Section](#part)';
  const transport: ApiTransportPort = { async send(request) {
    requests.push(request);
    if (request.path.includes('/sources/') || request.path === '/api/v1/tracks/reference-demo') return evidence.send(request);
    let body: unknown = [];
    if (request.path === '/api/v1/areas') body = [{ id: 'examples', name: 'Examples', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === '/api/v1/areas/examples/tracks?includeClosed=true') body = [track];
    if (request.path === '/api/v1/tracks/link-preview') body = fixtureSnapshot(track, [card, other]);
    if (request.path === '/api/v1/tracks/link-preview/sessions') body = [card, other];
    if (request.path === '/api/settings') body = {};
    if (request.path.includes('/items?')) {
      const id = conversationCardId(request.path);
      body = sessionPage(id === card.id ? card : other, [sessionItem(id, 1, text + historySuffix, 'assistant_text', 'stored'),
        ...(streaming ? [sessionItem(id, 2, live.text, 'assistant_text', 'live-turn')] : [])], streaming ? sessionTurn(id, 'live-turn') : null);
    }
    if (request.path.includes('/files')) body = { path: new URL(request.path, 'http://fixture').searchParams.get('path'), text: fileText, size: fileText.length, truncated: false };
    return { status: 200, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: task => task() }), onSignOut: () => {} });
  let registry: ConversationRegistry;
  const stale: string[] = [];
  let observing = false;
  function Probe() { registry = useConversationCommands(); return null; }
  for (const route of (router.routeTree.children ?? []) as readonly AnyRoute[]) {
    const Component = route.options.component;
    if (Component !== undefined) route.update({ component: () => <><Component /><Probe /></> });
  }
  router.update({ history: createMemoryHistory({ initialEntries: [`${APP_BASEPATH}/track/link-preview?panel=conversations`] }) });
  const root = document.createElement('div'); root.id = 'root'; document.body.append(root);
  render(<Profiler id="source-isolation" onRender={() => {
    if (observing && document.getElementById('conversation-chat-card') === null
      && page.getByRole('complementary', { name: '来源', exact: true }).query() !== null) stale.push('source survived switch');
  }}><QueryClientProvider client={client}><ThemeProvider><RouterProvider router={router} /></ThemeProvider></QueryClientProvider></Profiler>, { container: root });
  await page.getByRole('button', { name: /^Conversation Link chat/ }).click();
  return { requests, router, client, stale, switchTo: (id: string) => {
    observing = true;
    registry.requestOpen(id);
  }, append: async (text = '\n\nNew incremental [Live file](./live.md).') => {
    live.text += text;
    await client.refetchQueries({ queryKey: queryKeys.harnessItems(card.id) });
  } };
}

it('shows missing captured sources without ghost requests and previews real Track files', async () => {
  const { requests } = await setup();
  await page.getByRole('button', { name: 'Chat source', exact: true }).hover();
  await expect.element(page.getByRole('heading', { name: '来源缺失', exact: true })).toBeVisible();
  expect(requests.some(request => request.path.includes('/sources/'))).toBe(false);
  await page.getByRole('button', { name: 'Chat file', exact: true }).hover();
  await expect.element(page.getByRole('heading', { name: 'Chat file contents', exact: true })).toBeVisible();
  expect(requests.some(request => request.path === '/api/v1/tracks/link-preview/files?path=notes.md')).toBe(true);
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => page.getByRole('dialog', { name: 'Preview: Chat file' }).query()).toBeNull();
  await page.getByRole('button', { name: 'Chat source', exact: true }).click();
  await expect.element(page.getByRole('complementary', { name: '来源', exact: true })).toBeVisible();
  expect(requests.some(request => request.path.includes('/sources/'))).toBe(false);
});

it.each(['history', 'incremental'])('rejects malformed navigation hrefs without losing the %s reply', async mode => {
  // These pass through the real Astryx parser and sanitizer in Reply. The first
  // throws in URL parsing; the second would normalize into an application path.
  const malformed = '\n\n[Invalid authority](/\\[) · [Reinterpreted path](/next\\today)\n\nReply survives.';
  const { append, requests, router } = await setup({ streaming: mode === 'incremental',
    historySuffix: mode === 'history' ? malformed : '' });
  if (mode === 'incremental') {
    await expect.element(page.getByText('Streaming reply starts.', { exact: true })).toBeVisible();
    await append(malformed);
  }
  await expect.element(page.getByText('Reply survives.', { exact: true })).toBeVisible();
  for (const label of ['Invalid authority', 'Reinterpreted path']) {
    expect(document.querySelector('[data-nc-thread]')?.textContent).toContain(label);
    expect(page.getByRole('link', { name: label, exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: label, exact: true }).query()).toBeNull();
  }
  expect(document.querySelector('[data-nc-thread]')?.textContent).toContain('Chat source');
  expect(page.getByText('Something went wrong!', { exact: true }).query()).toBeNull();
  expect(router.history.location.pathname).toBe(`${APP_BASEPATH}/track/link-preview`);
  expect(requests.filter(request => request.path.includes('/files'))).toHaveLength(0);
  await page.getByRole('button', { name: 'Chat file', exact: true }).hover();
  await expect.element(page.getByRole('heading', { name: 'Chat file contents', exact: true })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => page.getByRole('dialog', { name: 'Preview: Chat file' }).query()).toBeNull();
  const drawer = page.getByRole('complementary', { name: 'Link chat', exact: true });
  await drawer.getByRole('button', { name: 'Today', exact: true }).click();
  await expect.poll(() => router.state.location.pathname).toBe('/');
});

it('keeps a readable preview title when a link label is a formula', async () => {
  await setup({ historySuffix: '\n\n[$x^2$](./notes.md)' });
  await expect.poll(() => document.querySelector('button:has(math)')).not.toBeNull();
  const formulaLink = document.querySelector('button:has(math)');
  expect(formulaLink).not.toBeNull();
  await userEvent.hover(formulaLink!);
  await expect.element(page.getByRole('dialog', { name: 'Preview: x^2' })).toBeVisible();
});

it('renders incremental links and keeps external loading explicit', async () => {
  const { append, requests } = await setup({ streaming: true });
  await expect.element(page.getByRole('button', { name: 'Chat file', exact: true })).toBeVisible();
  await expect.element(page.getByText('Streaming reply starts.', { exact: true })).toBeVisible();
  await append();
  expect(requests.some(request => request.path.startsWith('/api/v1/sessions/chat-card/items?'))).toBe(true);
  await page.getByRole('button', { name: 'Live file', exact: true }).hover();
  await expect.element(page.getByRole('heading', { name: 'Chat file contents', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Chat website', exact: true }).hover();
  await expect.element(page.getByRole('button', { name: 'Load webpage', exact: true })).toBeVisible();
  expect(document.querySelector('iframe[src="https://example.com/"]')).toBeNull();
  await page.getByRole('button', { name: 'Load webpage', exact: true }).click();
  await expect.poll(() => document.querySelector('iframe[src="https://example.com/"]')).not.toBeNull();
});

it.each([['Today', '/'], ['Legacy Today', '/today']])('preserves application navigation without admitting rejected workspace paths: %s', async (label, path) => {
  const { router } = await setup();
  const drawer = page.getByRole('complementary', { name: 'Link chat', exact: true });
  await expect.element(drawer.getByRole('button', { name: 'Today', exact: true })).toBeVisible();
  for (const text of ['Outside', 'Escape']) {
    expect(document.querySelector('[data-nc-thread]')?.textContent).toContain(text);
    expect(page.getByRole('link', { name: text, exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: text, exact: true }).query()).toBeNull();
  }
  expect(page.getByRole('link', { name: 'Email', exact: true }).element().getAttribute('href')).toBe('mailto:reader@example.com');
  expect(page.getByRole('link', { name: 'Phone', exact: true }).element().getAttribute('href')).toBe('tel:+123');
  expect(page.getByRole('link', { name: 'Section', exact: true }).element().getAttribute('href')).toBe('#part');
  // /next/today retains the existing router's treatment, even though main moved Today to /next/.
  await drawer.getByRole('button', { name: label, exact: true }).click();
  await expect.poll(() => router.state.location.pathname).toBe(path);
  expect(router.history.location.pathname).toBe(`${APP_BASEPATH}${path}`);
});

it('clears source selection synchronously across same-Track A to B to A', async () => {
  const { switchTo, stale } = await setup();
  await page.getByRole('button', { name: 'Chat source', exact: true }).click();
  await expect.element(page.getByRole('complementary', { name: '来源', exact: true })).toBeVisible();
  switchTo('chat-b');
  await expect.poll(() => document.getElementById('conversation-chat-b')?.querySelector('[data-nc-thread]') ?? null).not.toBeNull();
  expect(page.getByRole('complementary', { name: '来源', exact: true }).query()).toBeNull();
  switchTo('chat-card');
  await expect.poll(() => document.getElementById('conversation-chat-card')?.querySelector('[data-nc-thread]') ?? null).not.toBeNull();
  expect(page.getByRole('complementary', { name: '来源', exact: true }).query()).toBeNull();
  expect(stale).toEqual([]);
});

it.each(['native initial', 'desktop to narrow'])('keeps file body above the native chat sheet: %s', async mode => {
  const { requests } = await setup({ narrow: mode === 'native initial',
    fileText: '# Chat file contents\n\n[Read nested](./nested.md)\n\n' + Array.from({ length: 50 }, (_, i) => `Reading paragraph ${i}`).join('\n\n') });
  if (mode === 'desktop to narrow') await page.viewport(390, 844);
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(true);
  const trigger = page.getByRole('button', { name: 'Chat file', exact: true });
  await expect.element(trigger).toBeVisible();
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.getAnimations({ subtree: true }).every(animation => animation.playState !== 'running')).toBe(true);
  trigger.element().focus();
  await userEvent.keyboard('{ArrowDown}');
  const content = page.getByRole('region', { name: 'Preview content: Chat file' });
  await expect.element(content.getByRole('heading', { name: 'Chat file contents' })).toBeVisible();
  const body = content.element() as HTMLElement;
  await expect.poll(() => {
    const rect = body.getBoundingClientRect();
    const hit = document.elementFromPoint(rect.left + rect.width / 2, rect.bottom - 4);
    return body.contains(hit) ? true : { bottom: rect.bottom, hit: hit?.tagName, previewOnTop: false };
  }).toBe(true);
  expect(body.textContent).toContain('Reading paragraph 0');
  await content.getByText('Reading paragraph 0', { exact: true }).wheel({ delta: { y: 120 } });
  await expect.poll(() => body.scrollTop).toBeGreaterThan(0);
  await content.wheel({ delta: { y: -240 } });
  await expect.poll(() => body.scrollTop).toBe(0);
  await content.getByRole('button', { name: 'Read nested', exact: true }).click();
  await expect.poll(() => requests.some(request => request.path.includes('/files?path=nested.md'))).toBe(true);
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(true);
});

it.each(['parent content', 'root trigger'])('dismisses only the hovered child with %s focused in native chat', async focus => {
  await setup({ narrow: true, fileText: '# Parent file\n\n[Read nested](./nested.md)' });
  const trigger = page.getByRole('button', { name: 'Chat file', exact: true });
  await expect.element(trigger).toBeVisible();
  const host = trigger.element().closest('dialog')!;
  await expect.poll(() => host.getAnimations({ subtree: true }).every(animation => animation.playState !== 'running')).toBe(true);
  trigger.element().focus();
  await userEvent.keyboard('{ArrowDown}');
  const parent = page.getByRole('region', { name: 'Preview content: Chat file' });
  await expect.element(parent).toBeVisible();
  if (focus === 'root trigger') trigger.element().focus();
  else (parent.element() as HTMLElement).focus();
  const nested = parent.getByRole('button', { name: 'Read nested', exact: true });
  const child = page.getByRole('dialog', { name: 'Preview: Read nested' });
  await trigger.hover();
  await nested.hover();
  await expect.element(child).toBeVisible();
  expect(document.activeElement).toBe(focus === 'root trigger' ? trigger.element() : parent.element());
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => child.query()).toBeNull();
  expect(host.open).toBe(true);
  expect(parent.query()).not.toBeNull();
  // Reopen without moving focus into the child, then unwind one owner per key.
  await trigger.hover();
  await nested.hover();
  await expect.element(child).toBeVisible();
  expect(document.activeElement).toBe(focus === 'root trigger' ? trigger.element() : parent.element());
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => child.query()).toBeNull();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => parent.query()).toBeNull();
  expect(host.open).toBe(true);
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => host.open).toBe(false);
  await page.getByRole('button', { name: /^Conversation Link chat/ }).click();
  await expect.element(page.getByRole('button', { name: 'Chat file', exact: true })).toBeVisible();
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(true);
});
