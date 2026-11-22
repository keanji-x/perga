import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { render, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { StrictMode } from 'react';
import { expect, it } from 'vitest';
import { page } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { TRACK_CREATE_TEXT } from '../../../../core/domain/track.ts';
import { createStorageKey } from '../../../../core/keys/storage.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { CurrentUserScope } from '../auth/current-user.tsx';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { fixtureBootstrap, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { sessionPage } from './conversation-session-data.ts';
import { createMemoryStorage } from './share-reuse-fixture.ts';
import { bootTestCardRuntime } from './test-card-runtime.ts';

it('opens a confirmed Track after Chromium reloads the draft page', async () => {
  await page.viewport(1280, 800);
  const identity = { userId: 'new-track-reload', displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, role: 'owner', sessionId: 'one' } as const;
  const area = { id: 'work', name: 'Work', color: '#5B8DEF', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
  const draftKey = createStorageKey('track-create-v1', identity.userId, area.id);
  const firstSendKey = createStorageKey('pending-track-initial-sends-v1');
  const track = fixtureTrack('confirmed', { areaId: area.id, title: 'Confirmed track' });
  const planner = fixtureSession('planner', track.id, { title: 'Planner', isLead: true });
  const requests: ApiRequest[] = [];
  let confirm!: () => void;
  const heldCreate = new Promise<void>((resolve) => { confirm = resolve; });
  const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
  const transport: ApiTransportPort = { async send(request) {
    requests.push(request);
    if (request.method === 'POST' && request.path === '/api/v1/tracks') {
      await heldCreate;
      return { status: 201, statusText: 'Created', body: fixtureSnapshot(track, [planner]) };
    }
    if (request.path.includes('/workspace-options')) return ok({ defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] });
    if (request.path === '/api/v1/areas') return ok([area]);
    if (request.path.startsWith('/api/v1/bootstrap')) return ok(fixtureBootstrap(['codex']));
    if (request.path === '/api/v1/tracks/confirmed') return ok(fixtureSnapshot(track, [planner]));
    if (request.path.includes('/sessions/planner/items?')) return ok(sessionPage(planner));
    if (request.method === 'POST' && request.path === '/api/v1/sessions/planner/messages') {
      return { status: 202, statusText: 'Accepted', body: { sessionId: planner.id, messageId: 'initial-message', turnId: 'turn-initial', delivery: 'accepted', replay: false } };
    }
    return ok([]);
  } };

  // Keep Vitest in the outer page while Chromium reloads the app's document.
  const frame = document.createElement('iframe');
  frame.title = 'Track creation';
  frame.width = '1280';
  frame.height = '800';
  const loaded = () => new Promise<void>((resolve) => { frame.addEventListener('load', () => resolve(), { once: true }); });
  const frameStorage = () => frame.contentWindow!.sessionStorage;
  const mount = () => {
    const doc = frame.contentDocument!;
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = createAppRouter({
      client, transport, prepareReport: prepareReportDocument, cards: bootTestCardRuntime(),
      unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }), onSignOut: () => undefined,
      submittedStorage: frameStorage,
    });
    router.update({ history: createMemoryHistory({ initialEntries: ['/area/work/new'] }) });
    const view = render(
      <StrictMode>
        <QueryClientProvider client={client}>
          <ThemeProvider storage={createMemoryStorage()}>
            <CurrentUserScope value={identity}><RouterProvider router={router} /></CurrentUserScope>
          </ThemeProvider>
        </QueryClientProvider>
      </StrictMode>,
      { container: doc.getElementById('root')! },
    );
    return { router, screen: within(doc.body), user: userEvent.setup({ document: doc }),
      dispose: () => { view.unmount(); client.clear(); } };
  };
  let app: ReturnType<typeof mount> | undefined;
  const creates = () => requests.filter(({ method, path }) => method === 'POST' && path === '/api/v1/tracks');
  const message = 'Run after reload';
  const initialLoad = loaded();
  frame.srcdoc = '<!doctype html><html lang="en"><body><div id="root"></div></body></html>';
  document.body.appendChild(frame);
  try {
    await initialLoad;
    const original = mount();
    app = original;
    await original.user.type(await original.screen.findByLabelText('What this track should do'), message);
    await original.user.click(original.screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(creates()).toHaveLength(1));
    await original.user.click(original.screen.getByRole('button', { name: 'Go to Today' }));
    await waitFor(() => expect(original.screen.queryByLabelText('What this track should do')).toBeNull());
    confirm();
    await original.user.click(original.screen.getByRole('button', { name: 'New track in Work' }));
    await original.screen.findByRole('button', { name: 'Open track' });

    const previousDocument = frame.contentDocument;
    original.dispose();
    app = undefined;
    const reloaded = loaded();
    frame.contentWindow!.location.reload();
    await reloaded;
    expect(frame.contentDocument).not.toBe(previousDocument);
    const reloadedStorage = frameStorage();
    expect(JSON.parse(reloadedStorage.getItem(draftKey) ?? 'null')).toMatchObject({ createdTrackId: 'confirmed' });
    expect(reloadedStorage.getItem(firstSendKey)).not.toBeNull();
    app = mount();
    const openTrack = await app.screen.findByRole('button', { name: 'Open track' });
    expect(app.screen.queryByText(TRACK_CREATE_TEXT.unknown)).toBeNull();
    await app.user.click(openTrack);
    await app.screen.findByRole('complementary', { name: 'Planner' });
    expect(app.router.state.location.pathname).toMatch(/\/track\/confirmed$/);
    expect(reloadedStorage.getItem(draftKey)).toBeNull();
    await waitFor(() => expect(reloadedStorage.getItem(firstSendKey)).toBeNull());
    const messages = requests.filter(({ method, path }) => method === 'POST' && path === '/api/v1/sessions/planner/messages');
    expect(messages.map(({ body }) => body)).toEqual([expect.objectContaining({ content: message })]);
    expect(creates()).toHaveLength(1);
  } finally {
    try {
      app?.dispose();
      const storage = frameStorage();
      storage.removeItem(draftKey);
      storage.removeItem(firstSendKey);
    } finally {
      frame.remove();
    }
  }
});
