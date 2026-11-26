import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// @vitest-environment jsdom
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { CONVERSATION_CREATE_TEXT } from '../../../../core/domain/conversation.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { withReadReceipts } from '../providers/read-receipts-fixture.tsx';

const areas = [
  { id: 'c1', name: 'One', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 },
  { id: 'c2', name: 'Two', color: '#654321', sortOrder: 2, kind: 'user', createdAtMs: 1, updatedAtMs: 1 },
];
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
const track = fixtureTrack('w1', { areaId: 'c1', title: 'Reliable', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 1 });
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
const fail = (message: string): ApiTransportResponse => ({ status: 500, statusText: 'Server Error', body: { error: message } });

function renderRoute(path: string, reply: (request: ApiRequest) => ApiTransportResponse | Promise<ApiTransportResponse>) {
  const transport: ApiTransportPort = {
    send: (request) => Promise.resolve(request.path === '/api/v1/overlays?entity_kind=track&includeClosed=true'
      ? ok({ tracks: [] }) : reply(request)),
  };
  const client = new QueryClient({ defaultOptions: { queries: { retryDelay: 0, retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport: withReadReceipts(transport), unauthorized, client, cards: bootTestCardRuntime() });
  router.update({ history: createMemoryHistory({ initialEntries: [path] }) });
  const view = render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>);
  return { ...view, client };
}

afterEach(() => { cleanup(); onlineManager.setOnline(true); vi.restoreAllMocks(); });

describe('degraded workspace reads stay usable', () => {

  it('mounts navigation while an offline startup Areas query is paused', async () => {
    onlineManager.setOnline(false);
    renderRoute('/', () => ok([]));
    const rail = await screen.findByRole('navigation', { name: 'Workspace' });
    expect(within(rail).getByRole('button', { name: 'Go to Today' })).toBeTruthy();
    expect(within(rail).queryByRole('button', { name: '创建第一个 Area' })).toBeNull();
  });

  it.each(['areas', 'tracks'] as const)('provides recovery for the mobile %s read without claiming it is empty', async (resource) => {
    const media = window.matchMedia('');
    vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({ ...media, matches: query.includes('width'), media: query }));
    let broken = true;
    renderRoute('/', (request) => {
      if (request.path === '/api/v1/areas') return broken && resource === 'areas' ? fail('Area storage unavailable') : ok(areas);
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return broken && resource === 'tracks' ? fail('Tracks temporarily unavailable') : ok([track]);
      return ok([]);
    });
    // Track failures keep a known current Area; Area failures open navigation
    // before one exists, and recovery must attach its newly loaded track list.
    await userEvent.click(await screen.findByRole('button', { name: 'Open workspace' }));
    const sheet = screen.getByRole('dialog', { name: 'Tracks and settings' });
    if (resource === 'tracks') await userEvent.click(await within(sheet).findByRole('button', { name: 'One' }));
    const alert = await within(sheet).findByRole('alert');
    expect(alert.textContent).toContain(resource === 'areas' ? 'Areas are unavailable.' : 'Tracks are unavailable.');
    /* A 500's text is the server's internals: only the fixed sentence shows. */
    expect(alert.textContent).not.toMatch(/Area storage unavailable|Tracks temporarily unavailable/);
    expect(within(sheet).queryByText('这个 Area 还没有 Track')).toBeNull();
    broken = false;
    await userEvent.click(within(alert).getByRole('button', { name: 'Retry' }));
    if (resource === 'areas') await userEvent.click(await within(sheet).findByRole('button', { name: 'One' }));
    await within(sheet).findByRole('heading', { name: 'One' });
    await within(sheet).findByRole('button', { name: /^Reliable/ });
    await waitFor(() => expect(within(sheet).queryByRole('alert')).toBeNull());
  });

  it.each(['unavailable', 'malformed'] as const)('keeps navigation and retries an %s Areas startup read', async (failure) => {
    let broken = true;
    renderRoute('/', (request) => {
      if (request.path === '/api/v1/areas') return broken
        ? failure === 'unavailable' ? fail('Area storage temporarily unavailable') : ok({})
        : ok(areas);
      return ok([]);
    });
    const rail = await screen.findByRole('navigation', { name: 'Workspace' });
    const indicator = await within(rail).findByRole('button', { name: 'Connection status: Connection issue' });
    expect(within(rail).queryByRole('alert')).toBeNull();
    await userEvent.click(indicator);
    const details = screen.getByRole('dialog', { name: 'Connection details' });
    expect(details.textContent).toContain('Areas');
    expect(within(rail).queryByRole('button', { name: '创建第一个 Area' })).toBeNull();
    expect(within(rail).getByRole('button', { name: 'Go to Today' })).toBeTruthy();
    broken = false;
    await userEvent.click(within(details).getByRole('button', { name: 'Retry sync' }));
    await within(rail).findByRole('button', { name: 'Collapse area One' });
    await waitFor(() => expect(within(rail).queryByRole('button', { name: 'Connection status: Connection issue' })).toBeNull());
    expect(screen.queryByRole('dialog', { name: 'Connection details' })).toBeNull();
  });

  /* A refusal is the server's answer to this read: its reason follows the fixed sentence in the indicator. */
  it('says a refused Areas read with its reason behind the indicator', async () => {
    renderRoute('/', (request) => {
      if (request.path === '/api/v1/areas') return { status: 403, statusText: 'Forbidden', body: { error: 'owner login required', code: 'forbidden' } };
      return ok([]);
    });
    const rail = await screen.findByRole('navigation', { name: 'Workspace' });
    await userEvent.click(await within(rail).findByRole('button', { name: 'Connection status: Connection issue' }));
    expect(screen.getByRole('dialog', { name: 'Connection details' }).textContent).toContain('Areas are unavailable. owner login required');
  });

  it('keeps cached Areas while a refresh fails and recovers locally', async () => {
    let broken = false;
    const { client } = renderRoute('/', (request) => {
      if (request.path === '/api/v1/areas') return broken ? fail('Area refresh unavailable') : ok(areas);
      return ok([]);
    });
    const rail = await screen.findByRole('navigation', { name: 'Workspace' });
    await within(rail).findByRole('button', { name: 'Collapse area One' });
    broken = true;
    await act(() => client.invalidateQueries({ queryKey: ['areas'] }));
    const indicator = await within(rail).findByRole('button', { name: 'Connection status: Connection issue' });
    expect(within(rail).queryByRole('alert')).toBeNull();
    expect(within(rail).getByRole('button', { name: 'Collapse area One' })).toBeTruthy();
    await userEvent.click(indicator);
    const details = screen.getByRole('dialog', { name: 'Connection details' });
    expect(details.textContent).toContain('Areas could not be refreshed.');
    expect(details.textContent).not.toContain('Area refresh unavailable');
    broken = false;
    await userEvent.click(within(details).getByRole('button', { name: 'Retry sync' }));
    await waitFor(() => expect(within(rail).queryByRole('button', { name: 'Connection status: Connection issue' })).toBeNull());
    expect(screen.queryByRole('dialog', { name: 'Connection details' })).toBeNull();
  });

  it('lets an offline Area draft be cancelled without creating it on reconnect', async () => {
    const creates: ApiRequest[] = [];
    const { client } = renderRoute('/', (request) => {
      if (request.path === '/api/v1/areas') {
        if (request.method === 'POST') { creates.push(request); return ok(areas[0]); }
        return ok(areas);
      }
      return ok([]);
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New area' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Name' }), 'Offline draft');
    act(() => onlineManager.setOnline(false));
    await userEvent.click(screen.getByRole('button', { name: 'Create area' }));
    const dialog = screen.getByRole('dialog', { name: 'New area' });
    expect((await within(dialog).findByRole('alert')).textContent).toBe('The area was not created.');
    expect(within(dialog).getByRole<HTMLInputElement>('textbox', { name: 'Name' }).value).toBe('Offline draft');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog', { name: 'New area' })).toBeNull();
    await act(async () => { onlineManager.setOnline(true); await client.resumePausedMutations(); });
    expect(creates).toEqual([]);
  });

  it('keeps an offline Area draft editable and creates it once after an explicit online retry', async () => {
    const creates: ApiRequest[] = [];
    const { client } = renderRoute('/', (request) => {
      if (request.path === '/api/v1/bootstrap') return ok({ areaCreateIdempotency: true });
      if (request.path === '/api/v1/areas') {
        if (request.method === 'POST') { creates.push(request); return ok(areas[0]); }
        return ok(areas);
      }
      return ok([]);
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New area' }));
    await userEvent.type(screen.getByRole('textbox', { name: 'Name' }), 'Offline draft');
    act(() => onlineManager.setOnline(false));
    await userEvent.click(screen.getByRole('button', { name: 'Create area' }));
    const dialog = screen.getByRole('dialog', { name: 'New area' });
    await within(dialog).findByRole('alert');
    const name = within(dialog).getByRole<HTMLInputElement>('textbox', { name: 'Name' });
    await userEvent.clear(name);
    await userEvent.type(name, 'Revised draft');
    await act(async () => { onlineManager.setOnline(true); await client.resumePausedMutations(); });
    expect(creates).toEqual([]);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create area' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New area' })).toBeNull());
    expect(creates).toHaveLength(1);
    expect(creates[0]?.body).toMatchObject({ name: 'Revised draft' });
  });

  it('finishes an offline conversation submission without waiting for delivery reconciliation', async () => {
    const creates: ApiRequest[] = [];
    const { client } = renderRoute('/track/w1', (request) => {
      if (request.path === '/api/v1/bootstrap') return ok({ webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'test', assistantProviders: true, conversationInitialInput: true });
      if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok({ version: 'test', providers: [{ provider: 'codex', available: true, version: null, reason: null, models: [] }], templates: [], areas: [] });
      if (request.path === '/api/v1/areas') return ok(areas.slice(0, 1));
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([track]);
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(track, []));
      if (request.method === 'POST') creates.push(request);
      return ok([]);
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    const drawer = await screen.findByRole('complementary', { name: 'Untitled' });
    const field = within(drawer).getByRole('combobox', { name: 'Message' });
    await userEvent.type(field, 'Keep my offline conversation');
    act(() => onlineManager.setOnline(false));
    await userEvent.keyboard('{Enter}');
    expect((await within(drawer).findByRole('alert')).textContent).toContain(CONVERSATION_CREATE_TEXT.refused);
    const retry = await within(drawer).findByRole('button', { name: 'Try again' });
    await waitFor(() => expect(retry.hasAttribute('disabled')).toBe(false));
    expect(within(drawer).getByText('Keep my offline conversation')).toBeTruthy();
    expect(creates).toEqual([]);
    await userEvent.type(within(drawer).getByRole('combobox', { name: 'Message' }), 'Revised offline conversation');
    await userEvent.keyboard('{Enter}');
    expect((await within(drawer).findByRole('alert')).textContent).toContain(CONVERSATION_CREATE_TEXT.refused);
    expect((await within(drawer).findByRole('button', { name: 'Try again' })).hasAttribute('disabled')).toBe(false);
    expect(within(drawer).getByText('Revised offline conversation')).toBeTruthy();
    await userEvent.click(within(drawer).getByRole('button', { name: 'Close conversation' }));
    await act(async () => { onlineManager.setOnline(true); await client.resumePausedMutations(); });
    expect(creates).toEqual([]);
  });

  it('keeps a create undispatched when connectivity drops between the click and its mutation', async () => {
    const creates: ApiRequest[] = [];
    const { client } = renderRoute('/track/w1', (request) => {
      if (request.path === '/api/v1/bootstrap') return ok({ webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION, syncEventVersion: 29, dbInstanceId: 'test', assistantProviders: true, conversationInitialInput: true });
      if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return ok({ version: 'test', providers: [{ provider: 'codex', available: true, version: null, reason: null, models: [] }], templates: [], areas: [] });
      if (request.path === '/api/v1/areas') return ok(areas.slice(0, 1));
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return ok([track]);
      if (request.path === '/api/v1/tracks/w1') return ok(fixtureSnapshot(track, []));
      if (request.method === 'POST') creates.push(request);
      return ok([]);
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    const drawer = await screen.findByRole('complementary', { name: 'Untitled' });
    const field = within(drawer).getByRole('combobox', { name: 'Message' });
    await userEvent.type(field, 'Never dispatched');
    await act(async () => {
      fireEvent.keyDown(field, { key: 'Enter' });
      onlineManager.setOnline(false);
      await Promise.resolve();
    });
    expect((await within(drawer).findByRole('alert')).textContent).toContain(CONVERSATION_CREATE_TEXT.refused);
    expect(within(drawer).getByRole('button', { name: 'Try again' }).hasAttribute('disabled')).toBe(false);
    expect(creates).toHaveLength(0);
    await userEvent.click(within(drawer).getByRole('button', { name: 'Close conversation' }));
    await act(async () => { onlineManager.setOnline(true); await client.resumePausedMutations(); });
    await userEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    expect(screen.queryByText('Never dispatched')).toBeNull();
    expect(creates).toHaveLength(0);
  });

});
