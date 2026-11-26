import type { SessionItemDto } from '../../../../core/api/perga-v1.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { conversationCardId } from './conversation-read-data.ts';
import { sessionItem,sessionPage } from './conversation-session-data.ts';
import { fixtureSession,fixtureSnapshot,fixtureTrack } from './session-contract-fixture.ts';
// @vitest-environment jsdom
// The new-track page: `/area/{id}/new`, reached from each Area group's `+`.
import { onlineManager,QueryClient,QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { act,cleanup,render,screen,waitFor,within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { StrictMode } from 'react';
import { afterEach,describe,expect,it,vi } from 'vitest';

import type { ApiRequest,ApiTransportPort,ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { CurrentUserScope } from '../auth/current-user.tsx';
import { createStorageKey } from '../../../../core/keys/storage.ts';
import { PLANNER_OPEN_STATE_KEY } from './navigation.ts';
import { createMemoryStorage } from './share-reuse-fixture.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

const IDENTITY = { userId: 'track-create-owner', displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, role: 'owner', sessionId: 'one' } as const;
const DRAFT_KEY = createStorageKey('track-create-v1', IDENTITY.userId, 'c1');
const FIRST_SEND_KEY = createStorageKey('pending-track-initial-sends-v1');

/** The fixed unknown state of a create whose answer was lost (#2131): never the transport's text. */
const UNCONFIRMED_TRACK = 'The track creation is unconfirmed. Try again to check the same track.';
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });

afterEach(() => { cleanup(); onlineManager.setOnline(true); delete document.documentElement.dataset.theme; });

function memoryStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
  };
}

/* astryx puts `label` on the `contenteditable` as `aria-label`, so the composer resolves by label query. */
const TASK_LABEL = 'What this track should do';

const FOLDER_PLACEHOLDER = 'Perga workspace';
const FOLDER_CHIP_NAME = `Repo: ${FOLDER_PLACEHOLDER}`;


const AREA = { id: 'c1', name: 'Work', color: '#5B8DEF', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
const OTHER = { id: 'c2', name: 'Reading', color: '#8B7FE8', sortOrder: 2, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };

const LISTING = {
  path: '/srv/app', parent: '/srv', entries: [{ name: 'crates', is_dir: true }],
};

/* The kernel returns an empty title; the planner agent names the track later through `track_rename`. */
const TRACK_ROW = fixtureTrack('w-new', { areaId: 'c1', title: '', cwd: '/srv/managed', sortOrder: 0, closedAtMs: null, createdAtMs: 1, updatedAtMs: 1 });

/** The 409 `POST /api/tracks` answers a folder clash with — no `error` key. */
const CONFLICT = {
  folder_id: 4, area_id: 'c1', conflict_path: '/srv/app', conflict_kind: 'descendant',
};

const CLAUDE_CATALOG = { version: 'test', providers: [
  { provider: 'claude', available: true, models: ['opus[1m]', 'claude-fable-5-1[1m]', 'sonnet', 'haiku'] },
  { provider: 'codex', available: true, models: ['gpt-5'] },
] };
const NO_CATALOG = { version: 'test', providers: [{ provider: 'claude', available: false, models: [] }] };
const CLAUDE_DEFAULT_TRIGGER = 'Model: Claude Default';

/** Pick `item` from the `group` section of the open model menu. */
async function pick(trigger: string, group: 'Codex' | 'Claude', item: string | RegExp) {
  await userEvent.click(await screen.findByRole('button', { name: trigger }));
  const drill = screen.queryByRole('button', { name: /^Change model:/ });
  if (drill !== null) await userEvent.click(drill);
  const providers = await screen.findByRole('group', { name: 'Providers' });
  await userEvent.click(within(providers).getByRole('radio', { name: group }));
  const models = screen.getByRole('group', { name: 'Models' });
  await userEvent.click(within(models).getByRole('radio', { name: item }));
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Select model and reasoning effort' })).toBeNull());
}

function harness(options: {
  templates?: readonly import('../../../../core/domain/track.ts').TrackTemplate[];
  heldTemplates?: Promise<void>;

  areaDefaults?: Readonly<{ default_template_id: string | null; defaultCwd: string | null }>;
  otherAreaDefaults?: Readonly<{ default_template_id: string | null; defaultCwd: string | null }>;
  trackCreate?: ApiTransportResponse;
  /** Ordered create outcomes for retry/recovery paths that cross Area scope. */
  trackCreateSequence?: readonly ApiTransportResponse[];
  loseFirstCreateAck?: boolean;
  /** Hold the `?provider=claude` catalog read open, to observe the picker before it lands. */
  heldClaudeCatalog?: Promise<void>;
  /** Claude is ready: `?provider=claude` answers the Claude CLI's list. */
  claudePlanner?: boolean;
  /** `GET /api/agent-providers` says why a configured Claude Planner cannot run now (#1817); else it is ready. */
  claudeUnavailable?: string;
  /** `GET /api/agent-providers` says Codex cannot run now (#1817); create still accepts it. */
  codexUnavailable?: string;
  /** The created Planner card's `provider`; its `?card_id=` catalog is that provider's. */
  plannerProvider?: 'codex' | 'claude';
  /** Override the detail read the track page makes when the create lands. */
  trackDetail?: ApiTransportResponse;
  /** Hold the detail read open until this resolves, to drive a slow landing. */
  heldDetail?: Promise<void>;
  /** Hold the create POST open until this resolves, to drive a late create. */
  heldCreate?: Promise<void>;
  /** Hold `GET /api/areas` open, to render the page while the area's existence is genuinely unknown. */
  heldAreas?: Promise<void>;
  /** Fail `GET /api/areas` outright: a 500 leaves `workspace.areas` at `[]` with `areasLoading` false, indistinguishable from "landed, and this area is gone". */
  areasFail?: boolean;
  /** Where the browser starts, under the basepath; deep-linking is the only entry that reaches a stale area id. */
  path?: string;
  /** Recreate app providers on the current history entry, as a reload would. */
  reload?: boolean;
  /** Rows the planner card's item read answers with; defaults to `[]`, what the kernel answers before the queue drains. */
  plannerItems?: readonly SessionItemDto[];
  submittedStorage?: Storage;
} = {}) {
  const sent: ApiRequest[] = [];
  let trackCreateIndex = 0;
  let firstCreateAckLost = false;
  const transport: ApiTransportPort = {
    send(request: ApiRequest): Promise<ApiTransportResponse> {
      sent.push(request);
      if (request.path.includes('/workspace-options')) return Promise.resolve({ status: 200, statusText: 'OK', body: { defaultId: 'direct', options: [{ id: 'direct', label: 'Direct folder', description: 'Use this folder' }] } });
      if (request.path === '/api/v1/templates') {
        return (options.heldTemplates ?? Promise.resolve()).then(() => ({ status: 200, statusText: 'OK', body: options.templates ?? [] }));
      }
      if (request.path === '/api/plugins') return Promise.resolve({ status: 200, statusText: 'OK', body: [] });
      if (request.path.includes('/mentions?')) return Promise.resolve({ status: 200, statusText: 'OK', body: { tags: [], tracks: [], blocks: [] } });
      const posted = request.body as { areaId?: string } | undefined;
      if (request.method === 'POST' && request.path === '/api/v1/tracks' && options.loseFirstCreateAck && !firstCreateAckLost) {
        firstCreateAckLost = true;
        return Promise.reject(new Error('Connection closed after server commit'));
      }
      if (request.method === 'POST' && request.path === '/api/v1/tracks' && options.trackCreate) {
        return Promise.resolve(options.trackCreate);
      }
      if (request.method === 'POST' && request.path === '/api/v1/tracks' && options.trackCreateSequence) {
        const response = options.trackCreateSequence[trackCreateIndex];
        trackCreateIndex += 1;
        if (response !== undefined) return Promise.resolve(response);
      }
      if (request.method === 'POST' && request.path === '/api/v1/tracks' && options.heldCreate) {
        return options.heldCreate.then(() => ({
          status: 201,
          statusText: 'Created',
          body: fixtureSnapshot({ ...TRACK_ROW, areaId: posted?.areaId ?? 'c1' }),
        }));
      }
      if (options.submittedStorage !== undefined && request.method === 'POST' && request.path === '/api/v1/sessions/card-planner/messages') {
        return Promise.resolve({ status: 202, statusText: 'Accepted',
          body: { sessionId: 'card-planner', messageId: 'initial-message', turnId: 'turn-initial', delivery: 'accepted', replay: false } });
      }
      /* What `routes/agent_providers.rs` answers (#1817): Codex ready; Claude ready, unavailable with a
         reason, or `not_configured` on a server without the Claude Planner. */
      if (request.path === '/api/v1/bootstrap?scope=providers' || request.path === '/api/v1/bootstrap?scope=managed') {
        return Promise.resolve({ status: 200, statusText: 'OK', body: {
          version: 'test', templates: [], areas: [], providers: [
            { provider: 'codex', available: options.codexUnavailable === undefined,
              reason: options.codexUnavailable ?? null, version: 'test', models: [] },
            ...(options.claudePlanner === true ? [{ provider: 'claude', available: options.claudeUnavailable === undefined,
              reason: options.claudeUnavailable ?? null, version: 'test', models: [] }] : []),
          ],
        } });
      }
      /* What `routes/models.rs` answers for a Claude Planner (#1822): the Claude CLI's list on a server
         whose Claude is ready, `unavailable` with no catalog on one without Claude Planners. */
      if (request.path === '/api/v1/bootstrap?provider=claude') {
        return (options.heldClaudeCatalog ?? Promise.resolve()).then(() => ({ status: 200, statusText: 'OK',
          body: options.claudePlanner === true && options.claudeUnavailable === undefined ? CLAUDE_CATALOG : NO_CATALOG }));
      }
      if (request.path === '/api/v1/bootstrap?card_id=card-planner' && options.plannerProvider === 'claude') {
        return Promise.resolve({ status: 200, statusText: 'OK', body: CLAUDE_CATALOG });
      }
      if (request.method === 'PATCH' && request.path === '/api/v1/sessions/card-planner') {
        const { model } = request.body as { model: string | null };
        return Promise.resolve({ status: 200, statusText: 'OK', body: {
          id: 'card-planner', trackId: 'w-new', provider: options.plannerProvider ?? 'codex', model, createdAtMs: 1, updatedAtMs: 2,
        } });
      }
      if (request.path === '/api/v1/bootstrap?provider=codex' || request.path === '/api/v1/bootstrap?card_id=card-planner') {
        return Promise.resolve({ status: 200, statusText: 'OK', body: {
          version: 'test', providers: [{ provider: 'codex', available: options.codexUnavailable === undefined, models: ['gpt-5'] }],
        } });
      }
      /* Served rather than left to fall through to `[]`: a decode failure would look
               identical to "the feature did not run". */
      if (request.method === 'GET' && request.path === '/api/v1/tracks/w-new') {
        if (options.trackDetail) return Promise.resolve(options.trackDetail);
        const detail = {
          status: 200,
          statusText: 'OK',
          body: fixtureSnapshot({ ...TRACK_ROW }, [fixtureSession('card-planner', TRACK_ROW.id, { title: 'Planner', isLead: true, provider: options.plannerProvider ?? 'codex' })]),
        } satisfies ApiTransportResponse;
        /* Held open on request, so a test can land the cards after the navigation. */
        return options.heldDetail
          ? options.heldDetail.then(() => detail)
          : Promise.resolve(detail);
      }
      if (request.method === 'GET' && request.path === '/api/v1/sessions/card-planner/items?afterId=0&limit=1') {
        return Promise.resolve({ status: 200, statusText: 'OK', body: sessionPage(fixtureSession('card-planner', TRACK_ROW.id, { provider: options.plannerProvider ?? 'codex' })) });
      }
      if (request.method === 'GET' && request.path.includes('/items') && !request.path.includes('limit=1')) {
        return Promise.resolve({ status: 200, statusText: 'OK', body: sessionPage(fixtureSession(conversationCardId(request.path), TRACK_ROW.id, { provider: options.plannerProvider ?? 'codex' }), [...(options.plannerItems ?? [])]) });
      }
      if (request.path === '/api/v1/areas' && options.areasFail) {
        return Promise.resolve({ status: 500, statusText: 'Server Error', body: { error: 'areas are unreadable' } });
      }
      if (request.path === '/api/v1/areas' && options.heldAreas) {
        return options.heldAreas.then(() => ({ status: 200, statusText: 'OK', body: [AREA, OTHER] }));
      }
      const body = request.path === '/api/v1/areas' ? [
        { ...AREA, ...options.areaDefaults }, { ...OTHER, ...options.otherAreaDefaults },
      ]
        : request.path.startsWith('/api/v1/fs/listdir') ? LISTING
          : request.method === 'POST' && request.path === '/api/v1/tracks'
            ? fixtureSnapshot({ ...TRACK_ROW, areaId: posted?.areaId ?? 'c1' })
            : [];
      return Promise.resolve({ status: 200, statusText: 'OK', body });
    },
  };
  if (!options.reload) window.history.pushState({}, '', `${options.path ?? '/'}`);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport, unauthorized, client, cards: bootTestCardRuntime(),
    submittedStorage: options.submittedStorage === undefined ? undefined : () => options.submittedStorage!,
  });
  /* `StrictMode`, because production runs it: its double-invoked effects are what
       catch a `useRef` latch written only in the cleanup arm. */
  const { unmount } = render(
    <StrictMode>
      <QueryClientProvider client={client}>
        <ThemeProvider storage={memoryStorage()}>
          <CurrentUserScope value={IDENTITY}><RouterProvider router={router} /></CurrentUserScope>
        </ThemeProvider>
      </QueryClientProvider>
    </StrictMode>,
  );
  return { sent, client, router, unmount };
}

describe('New track model selection', () => {
  it('keeps the model in the Area draft and retries the original creation configuration', async () => {
    const { sent } = harness({ loseFirstCreateAck: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.click(await screen.findByRole('button', { name: 'Model: Default' }));
    await userEvent.click(await screen.findByRole('radio', { name: 'gpt-5' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Select model and reasoning effort' })).toBeNull());
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.paste('Use these settings from the first turn');
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    expect(await screen.findByRole('button', { name: 'Model: Default' })).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await screen.findByRole('button', { name: 'Model: gpt-5' })).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await screen.findByText(UNCONFIRMED_TRACK);
    const original = createdTrackRequests(sent)[0];
    expect(original?.body).toMatchObject({ model: 'gpt-5' });
    expect(original?.body).not.toHaveProperty('first_message');
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Model: gpt-5' }).getAttribute('aria-disabled')).toBe('true');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    expect(createdTrackRequests(sent)[1]?.body).toEqual(original?.body);
    expect(createdTrackRequests(sent)[1]?.headers).toEqual(original?.headers);
  });

  it('omits overrides after returning to the installation default', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.click(await screen.findByRole('button', { name: 'Model: Default' }));
    await userEvent.click(await screen.findByRole('radio', { name: 'gpt-5' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Select model and reasoning effort' })).toBeNull());
    await userEvent.click(await screen.findByRole('button', { name: 'Model: gpt-5' }));
    await userEvent.click(screen.getByRole('radio', { name: 'Default' }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Select model and reasoning effort' })).toBeNull());
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Follow the default');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    expect(createdTrackRequests(sent)[0]?.body).not.toHaveProperty('model');
    expect(createdTrackRequests(sent)[0]?.body).not.toHaveProperty('reasoning_effort');
  });

  it('offers no Claude group on a server without Claude Planners', async () => {
    harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.click(await screen.findByRole('button', { name: 'Model: Default' }));
    await screen.findByRole('radio', { name: 'gpt-5' });
    expect(screen.queryByRole('radio', { name: 'Claude' })).toBeNull();
    expect(screen.queryByRole('radio', { name: /^sonnet/ })).toBeNull();
  });

  it('offers no Claude group before its catalog has answered', async () => {
    harness({ claudePlanner: true, heldClaudeCatalog: new Promise<void>(() => undefined) });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.click(await screen.findByRole('button', { name: 'Model: Default' }));
    await screen.findByRole('radio', { name: 'gpt-5' });
    expect(screen.queryByRole('radio', { name: 'Claude' })).toBeNull();
  });

  it('creates a fresh blank track with unavailable Claude and its server reason (#3037)', async () => {
    const reason = 'not logged in — run `claude /login` with CLAUDE_CONFIG_DIR=/srv/claude';
    const { sent } = harness({ claudePlanner: true, claudeUnavailable: reason });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.click(await screen.findByRole('button', { name: 'Model: Codex Default' }));
    const providers = await screen.findByRole('group', { name: 'Providers' });
    await userEvent.click(within(providers).getByRole('radio', { name: 'Claude' }));
    const models = screen.getByRole('group', { name: 'Models' });
    expect(within(models).getByRole('note').textContent).toBe(`Claude is unavailable: ${reason} A track can still be created; it runs once Claude is back.`);
    expect(within(models).queryByRole('radio', { name: /^Default/ })).toBeNull();
    const useAnyway = within(models).getByRole('button', { name: 'Use Claude anyway' });
    expect(useAnyway.getAttribute('aria-disabled') === 'true' || useAnyway.hasAttribute('disabled')).toBe(false);
    await userEvent.click(useAnyway);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Select model and reasoning effort' })).toBeNull());
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan while Claude is down');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    expect(createdTrackRequests(sent)[0]?.body).toMatchObject({ provider: 'claude' });
    expect(createdTrackRequests(sent)[0]?.body).not.toHaveProperty('model');
    expect(createdTrackRequests(sent)[0]?.body).not.toHaveProperty('first_message');
    expect(sent.some((request) => request.path === '/api/v1/bootstrap?scope=providers')).toBe(true);
  });

  it('creates on an unavailable Codex: its rows stay pickable, as create still accepts it (#1817)', async () => {
    const reason = 'shared codex app-server is not running';
    const { sent } = harness({ claudePlanner: true, codexUnavailable: reason });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Claude Default', 'Codex', 'gpt-5');
    expect(await screen.findByRole('button', { name: 'Model: Codex gpt-5' })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan while Codex is down');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    expect(createdTrackRequests(sent)[0]?.body).toMatchObject({ provider: 'codex', model: 'gpt-5' });
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
  });

  it('switches a Claude draft back to Codex while Codex is unavailable (#1817)', async () => {
    const { sent } = harness({ claudePlanner: true, codexUnavailable: 'shared codex app-server is not running' });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Claude Default', 'Claude', /^sonnet/);
    await pick('Model: Claude sonnet', 'Codex', /^Default/);
    expect(await screen.findByRole('button', { name: 'Model: Codex Default' })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Back on Codex');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    expect(createdTrackRequests(sent)[0]?.body).toMatchObject({ provider: 'codex' });
  });

  it('a Claude pick sets the provider and value without inventing effort metadata', async () => {
    const { sent } = harness({ claudePlanner: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Codex Default', 'Codex', 'gpt-5');
    await pick('Model: Codex gpt-5', 'Claude', /^sonnet/);
    expect(await screen.findByRole('button', { name: 'Model: Claude sonnet' })).toBeTruthy();
    /* sonnet's own levels are offered, starting from its default: Codex's `high` did not come along. */
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan with Claude');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    const body = createdTrackRequests(sent)[0]?.body;
    expect(body).toMatchObject({ provider: 'claude', model: 'sonnet' });
    expect(body).not.toHaveProperty('first_message');
    expect(body).not.toHaveProperty('reasoning_effort');
  });

  it('a Claude Default sends the provider and no model', async () => {
    const { sent } = harness({ claudePlanner: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Codex Default', 'Claude', /^Default/);
    expect(await screen.findByRole('button', { name: CLAUDE_DEFAULT_TRIGGER })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan with Claude');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    const body = createdTrackRequests(sent)[0]?.body;
    expect(body).toMatchObject({ provider: 'claude' });
    expect(body).not.toHaveProperty('model');
  });

  it('a Codex pick after a Claude one starts again on Codex', async () => {
    const { sent } = harness({ claudePlanner: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Codex Default', 'Claude', /^opus\[1m\]/);
    await pick('Model: Claude opus[1m]', 'Codex', /^Default/);
    expect(await screen.findByRole('button', { name: 'Model: Codex Default' })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Back on Codex');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    const body = createdTrackRequests(sent)[0]?.body;
    expect(body).toMatchObject({ provider: 'codex' });
    expect(body).not.toHaveProperty('model');
  });

  it('keeps the choice per Area draft and resets it once the track is created', async () => {
    harness({ claudePlanner: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Codex Default', 'Claude', /^haiku/);
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    expect(await screen.findByRole('button', { name: 'Model: Codex Default' })).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await screen.findByRole('button', { name: 'Model: Claude haiku' })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan with Claude');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await screen.findByRole('button', { name: 'Model: Codex Default' })).toBeTruthy();
  });

  it('inside a Claude track lists only its provider models and stores a pick', async () => {
    const { sent } = harness({ claudePlanner: true, plannerProvider: 'claude' });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await pick('Model: Codex Default', 'Claude', /^sonnet/);
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Plan with Claude');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    const drawer = await screen.findByRole('complementary', { name: 'Planner' });
    const trigger = await within(drawer).findByRole('button', { name: 'Model: Default' });
    await waitFor(() => expect(trigger.getAttribute('aria-disabled')).toBeNull());
    await userEvent.click(trigger);
    const dialog = await screen.findByRole('dialog');
    const models = within(dialog).getByRole('group', { name: 'Models' });
    expect(within(models).getAllByRole('radio').map((item) => item.textContent))
      .toEqual(['DefaultSelected', 'opus[1m]', 'claude-fable-5-1[1m]', 'sonnet', 'haiku']);
    expect(within(dialog).queryByRole('radio', { name: 'Codex' })).toBeNull();
    await userEvent.click(within(models).getByRole('radio', { name: /^haiku/ }));
    await waitFor(() => expect(sent.filter((request) => request.method === 'PATCH'
      && request.path === '/api/v1/sessions/card-planner').map((request) => request.body))
      .toEqual([{ model: 'haiku', reasoningEffort: null }]));
    expect(sent.some((request) => request.path === '/api/v1/bootstrap?card_id=card-planner')).toBe(true);
  });

  it('names the Codex Planner backend on every create', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Start the Planner');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
    expect(createdTrackRequests(sent)[0]?.body).toMatchObject({
      provider: 'codex',
    });
    expect(createdTrackRequests(sent)[0]?.body).not.toHaveProperty('first_message');
  });
});

describe('Track creation drafts survive navigation', () => {
  it('explains an attached repository divergence and retries the same request', async () => {
    const { sent } = harness({ trackCreateSequence: [{
      status: 409, statusText: 'Conflict',
      body: { code: 'attached_repo_diverged', error: 'The repository needs attention.' },
    }] });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Keep this message');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await screen.findByText(/Git branch has diverged/);
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const [first, retry] = createdTrackRequests(sent);
    expect(retry?.body).toEqual(first?.body);
    expect(retry?.headers).toEqual(first?.headers);
  });

  it.each(['offline', 'rate-limit', 'discovery'] as const)('keeps an earlier unconfirmed request after a later %s rejection', async (rejection) => {
    const { sent } = harness({ loseFirstCreateAck: true,
      otherAreaDefaults: { default_template_id: null, defaultCwd: '/srv/app' },
      trackCreateSequence: rejection === 'offline' ? undefined : [
        rejection === 'rate-limit'
          ? { status: 429, statusText: 'Too Many Requests', body: { error: 'rate limited' } }
          : rejection === 'discovery'
            ? { status: 409, statusText: 'Conflict', body: { code: 'precondition_failed', error: 'Retry discovery' } }
            : { status: 409, statusText: 'Conflict', body: CONFLICT },
      ],
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Keep original intent');
    document.documentElement.dataset.theme = 'light';
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await screen.findByText(UNCONFIRMED_TRACK);
    const original = createdTrackRequests(sent)[0];
    if (rejection === 'offline') act(() => onlineManager.setOnline(false));
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    /* Offline is refused before sending and leaves the earlier create unconfirmed, so its fixed state stays. */
    if (rejection === 'offline') await act(async () => { await new Promise((done) => { setTimeout(done, 20); }); });
    else await waitFor(() => expect(within(screen.getByRole('main')).getByRole('alert').textContent).not.toContain(UNCONFIRMED_TRACK));
    expect(within(screen.getByRole('main')).getByRole('alert').textContent).not.toMatch(/Transport request failed|offline|reconnect/i);
    expect(screen.getByLabelText(TASK_LABEL).getAttribute('contenteditable')).toBe('false');
    expect(screen.queryByRole('button', { name: 'Create in Work' })).toBeNull();
    act(() => onlineManager.setOnline(true));
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    document.documentElement.dataset.theme = 'dark';
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(rejection === 'offline' ? 2 : 3));
    const retry = createdTrackRequests(sent).at(-1);
    expect(retry?.headers).toEqual(original?.headers);
    expect(retry?.body).toEqual(original?.body);
  });

  it('restores unsent text and options independently for each Area', async () => {
    harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Unsent intent');

    await userEvent.click(screen.getByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    expect(composerText()).toBe('');
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Other draft');
    await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
    await findComposer();
    expect(composerText()).toBe('Unsent intent');
    await userEvent.click(screen.getByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    expect(composerText()).toBe('Other draft');
  }, 15_000);

  it('retries the exact lost-ack request and key after leaving and returning', async () => {
    const { sent } = harness({ loseFirstCreateAck: true });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), '  Create once  ');
    document.documentElement.dataset.theme = 'light';
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await screen.findByText(UNCONFIRMED_TRACK);
    const first = createdTrackRequests(sent)[0];
    expect(first?.body).not.toHaveProperty('theme');
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    expect(composerText()).toBe('  Create once  ');
    document.documentElement.dataset.theme = 'dark';
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const retry = createdTrackRequests(sent)[1];
    expect(retry?.headers).toEqual(first?.headers);
    expect(retry?.body).toEqual(first?.body);
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
  });

  it('retains an unsent draft when its parent is deleted and prevents an orphan create', async () => {
    const { sent, client } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Keep this after Area deletion');
    await act(async () => { client.setQueryData(['areas'], []); await new Promise((resolve) => setTimeout(resolve, 0)); });
    expect(screen.getByLabelText(TASK_LABEL).textContent).toBe('Keep this after Area deletion');
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Create track' }).getAttribute('aria-disabled')).toBe('true');
    await userEvent.keyboard('{Enter}');
    expect(createdTrackRequests(sent)).toEqual([]);
    expect(screen.getByRole('main').textContent).toContain('draft');
  });

  it('keeps a pending creation leased across route remount and offers its late acknowledgement', async () => {
    let release!: () => void;
    const heldCreate = new Promise<void>((resolve) => { release = resolve; });
    const { sent } = harness({ heldCreate });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Create while away');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await screen.findByRole('button', { name: 'Creating…' })).toBeTruthy();
    expect(createdTrackRequests(sent)).toHaveLength(1);
    release();
    await userEvent.click(await screen.findByRole('button', { name: 'Open track' }));
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    expect(createdTrackRequests(sent)).toHaveLength(1);
  });
});

it.each([false, true])('opens the confirmed draft after reload with initial message already delivered=%s', async (deliveredBeforeReload) => {
  let release!: () => void;
  const heldCreate = new Promise<void>((resolve) => { release = resolve; });
  const submittedStorage = createMemoryStorage();
  const original = harness({ path: '/area/c1/new', heldCreate, submittedStorage });
  const message = 'Create while away';
  await userEvent.type(await findComposer(), message);
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await waitFor(() => expect(createdTrackRequests(original.sent)).toHaveLength(1));
  await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
  await waitFor(() => expect(screen.queryByLabelText(TASK_LABEL)).toBeNull());
  release();
  await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
  await screen.findByRole('button', { name: 'Open track' });

  if (deliveredBeforeReload) {
    await act(async () => {
      await original.router.navigate({ to: '/track/w-new', state: { [PLANNER_OPEN_STATE_KEY]: true } });
    });
    await waitFor(() => expect(plannerInputTexts(original.sent)).toEqual([message]));
    await waitFor(() => expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull());
    await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
    await screen.findByRole('button', { name: 'Open track' });
  } else {
    expect(plannerInputTexts(original.sent)).toEqual([]);
    expect(submittedStorage.getItem(FIRST_SEND_KEY)).not.toBeNull();
  }

  original.unmount();
  original.client.clear();
  const reopened = harness({ path: '/area/c1/new', submittedStorage });
  const openTrack = await screen.findByRole('button', { name: 'Open track' });
  expect(screen.queryByText(UNCONFIRMED_TRACK)).toBeNull();
  await userEvent.click(openTrack);
  await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
  await screen.findByRole('complementary', { name: 'Planner' });
  expect(submittedStorage.getItem(DRAFT_KEY)).toBeNull();
  await waitFor(() => expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull());
  expect(plannerInputTexts(reopened.sent)).toEqual(deliveredBeforeReload ? [] : [message]);
  expect(createdTrackRequests([...original.sent, ...reopened.sent])).toHaveLength(1);
  await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
  await findComposer();
  expect(composerText()).toBe('');
  expect(screen.queryByRole('button', { name: 'Open track' })).toBeNull();
});

it.each([false, true])('recovers an unread first message after reload (still unreadable on first click: %s)', async (stillUnreadable) => {
  const submittedStorage = createMemoryStorage();
  const createKey = 'confirmed-create';
  const message = 'Open after storage recovers';
  const draft = JSON.stringify({ key: createKey, body: { areaId: 'c1', provider: 'codex' }, message, createdTrackId: 'w-new' });
  const firstSend = JSON.stringify({ 'w-new': { text: message, key: `${createKey}:init` } });
  submittedStorage.setItem(DRAFT_KEY, draft);
  submittedStorage.setItem(FIRST_SEND_KEY, firstSend);
  const getItem = submittedStorage.getItem.bind(submittedStorage);
  let unreadable = true;
  const read = vi.spyOn(submittedStorage, 'getItem').mockImplementation((key) => {
    if (key === FIRST_SEND_KEY && unreadable) throw new DOMException('Storage unavailable', 'SecurityError');
    return getItem(key);
  });
  const { sent } = harness({ path: '/area/c1/new', submittedStorage });
  const openTrack = await screen.findByRole('button', { name: 'Open track' });
  expect(read).toHaveBeenCalledWith(FIRST_SEND_KEY);

  if (stillUnreadable) {
    await userEvent.click(openTrack);
    expect(await screen.findByText(/its saved first message is unavailable/)).toBeTruthy();
    expect(window.location.pathname).toBe(`/area/c1/new`);
    expect(getItem(DRAFT_KEY)).toBe(draft);
    expect(getItem(FIRST_SEND_KEY)).toBe(firstSend);
    expect(plannerInputTexts(sent)).toEqual([]);
    expect(createdTrackRequests(sent)).toEqual([]);
  }

  unreadable = false;
  await userEvent.click(screen.getByRole('button', { name: 'Open track' }));
  await screen.findByRole('complementary', { name: 'Planner' });
  expect(window.location.pathname).toBe(`/track/w-new`);
  await waitFor(() => expect(plannerInputTexts(sent)).toEqual([message]));
  await waitFor(() => expect(getItem(FIRST_SEND_KEY)).toBeNull());
  const sends = sent.filter(({ method, path }) => method === 'POST' && path === '/api/v1/sessions/card-planner/messages');
  expect(sends.map(({ headers }) => headers?.['Idempotency-Key'])).toEqual([`${createKey}:init`]);
  expect(getItem(DRAFT_KEY)).toBeNull();
  expect(createdTrackRequests(sent)).toEqual([]);
});

it('replays the create after reload when the first message could not be saved', async () => {
  let confirm!: () => void;
  const heldCreate = new Promise<void>((resolve) => { confirm = resolve; });
  const submittedStorage = createMemoryStorage();
  const setItem = submittedStorage.setItem.bind(submittedStorage);
  let queueWriteFailed = false;
  vi.spyOn(submittedStorage, 'setItem').mockImplementation((key, value) => {
    if (key === FIRST_SEND_KEY && !queueWriteFailed) {
      queueWriteFailed = true;
      throw new DOMException('Storage quota reached', 'QuotaExceededError');
    }
    setItem(key, value);
  });
  const original = harness({ path: '/area/c1/new', heldCreate, submittedStorage });
  const message = 'Send after recovery';
  await userEvent.type(await findComposer(), message);
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await waitFor(() => expect(createdTrackRequests(original.sent)).toHaveLength(1));
  const [first] = createdTrackRequests(original.sent);
  const createKey = first?.headers?.['Idempotency-Key'];
  expect(createKey).toBeTruthy();
  const savedRequest = submittedStorage.getItem(DRAFT_KEY);
  expect(savedRequest).not.toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
  await waitFor(() => expect(screen.queryByLabelText(TASK_LABEL)).toBeNull());
  confirm();
  await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
  await screen.findByRole('button', { name: 'Open track' });
  expect(queueWriteFailed).toBe(true);
  expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull();
  expect(plannerInputTexts(original.sent)).toEqual([]);

  original.unmount();
  original.client.clear();
  const reopened = harness({ path: '/area/c1/new', heldCreate, submittedStorage });
  await screen.findByText(UNCONFIRMED_TRACK);
  expect(submittedStorage.getItem(DRAFT_KEY)).toBe(savedRequest);
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await screen.findByRole('complementary', { name: 'Planner' });
  expect(createdTrackRequests(reopened.sent)).toEqual([first]);
  await waitFor(() => expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull());
  expect(plannerInputTexts(reopened.sent)).toEqual([message]);
  const sends = reopened.sent.filter(({ method, path }) => method === 'POST' && path === '/api/v1/sessions/card-planner/messages');
  expect(sends.map(({ headers }) => headers?.['Idempotency-Key'])).toEqual([`${createKey}:init`]);
  expect(submittedStorage.getItem(DRAFT_KEY)).toBeNull();
});

it.each(['one-shot reload', 'persistent reload', 'persistent in tab'] as const)(
  'keeps the first message recoverable when opening with a failed queue write (%s)',
  async (scenario) => {
    let confirm!: () => void;
    let releaseDetail!: () => void;
    const heldCreate = new Promise<void>((resolve) => { confirm = resolve; });
    const heldDetail = new Promise<void>((resolve) => { releaseDetail = resolve; });
    const submittedStorage = createMemoryStorage();
    const setItem = submittedStorage.setItem.bind(submittedStorage);
    let queueWrites = 0;
    const persistentlyFailing = scenario !== 'one-shot reload';
    const write = vi.spyOn(submittedStorage, 'setItem').mockImplementation((key, value) => {
      if (key === FIRST_SEND_KEY && (++queueWrites === 1 || persistentlyFailing)) {
        throw new DOMException('Storage quota reached', 'QuotaExceededError');
      }
      setItem(key, value);
    });
    const original = harness({ path: '/area/c1/new', heldCreate, heldDetail, submittedStorage });
    const message = 'Keep the first message on open';
    await userEvent.type(await findComposer(), message);
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(original.sent)).toHaveLength(1));
    const [first] = createdTrackRequests(original.sent);
    const createKey = first?.headers?.['Idempotency-Key'];
    expect(createKey).toBeTruthy();
    const savedRequest = submittedStorage.getItem(DRAFT_KEY);
    expect(savedRequest).not.toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Go to Today' }));
    await waitFor(() => expect(screen.queryByLabelText(TASK_LABEL)).toBeNull());
    confirm();
    await userEvent.click(screen.getByRole('button', { name: 'New track in Work' }));
    const openTrack = await screen.findByRole('button', { name: 'Open track' });
    expect(queueWrites).toBe(1);
    expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull();

    await userEvent.click(openTrack);
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    await waitFor(() => expect(original.sent).toContainEqual(expect.objectContaining({ method: 'GET', path: '/api/v1/tracks/w-new' })));
    expect(plannerInputTexts(original.sent)).toEqual([]);
    expect(submittedStorage.getItem(DRAFT_KEY)).toBe(persistentlyFailing ? savedRequest : null);
    expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBe(persistentlyFailing ? null
      : JSON.stringify({ 'w-new': { text: message, key: `${createKey}:init` } }));

    let delivery = original;
    if (scenario === 'persistent in tab') {
      releaseDetail();
    } else {
      original.unmount();
      original.client.clear();
      releaseDetail();
      if (scenario === 'persistent reload') {
        write.mockRestore();
        delivery = harness({ path: '/area/c1/new', heldCreate, submittedStorage });
        await screen.findByText(UNCONFIRMED_TRACK);
        await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
        expect(createdTrackRequests(delivery.sent)).toEqual([first]);
      } else {
        delivery = harness({ reload: true, submittedStorage });
      }
    }
    await screen.findByRole('complementary', { name: 'Planner' });
    await waitFor(() => expect(plannerInputTexts(delivery.sent)).toEqual([message]));
    const sent = delivery === original ? original.sent : [...original.sent, ...delivery.sent];
    const sends = sent.filter(({ method, path }) => method === 'POST' && path === '/api/v1/sessions/card-planner/messages');
    expect(sends.map(({ headers }) => headers?.['Idempotency-Key'])).toEqual([`${createKey}:init`]);
    expect(createdTrackRequests(sent)).toHaveLength(scenario === 'persistent reload' ? 2 : 1);
    if (!persistentlyFailing) await waitFor(() => expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull());
  },
);

/* No click promises the next screen synchronously, so every role query waits on the field itself. */
async function findComposer(): Promise<HTMLElement> {
  return screen.findByLabelText(TASK_LABEL);
}

/** Waits for the track page to be mounted — it is the surface that would redeem
 *  a leftover open request, so the assertion has to happen after it exists. */
async function findTrackPage(): Promise<HTMLElement> {
  return screen.findByRole('main');
}

/** What the composer currently holds. It is a `contenteditable`, not an input,
 *  so the value is its text rather than a `value` property. */
function composerText(): string {
  return screen.getByLabelText(TASK_LABEL).textContent ?? '';
}

/** The text of every first message delivered to the planner card. */
function plannerInputTexts(sent: readonly ApiRequest[]): unknown[] {
  return sent.filter((request) => request.method === 'POST' && request.path.endsWith('/messages'))
    .map((request) => (request.body as { content?: unknown } | undefined)?.content);
}

function createdTrackBodies(sent: readonly ApiRequest[]): unknown[] {
  return createdTrackRequests(sent).map((request) => request.body);
}

function createdTrackRequests(sent: readonly ApiRequest[]): ApiRequest[] {
  return sent.filter((request) => request.method === 'POST' && request.path === '/api/v1/tracks');
}

describe('the new-track page is a route reached from Area groups', () => {
  it('carries the selected group into one shared create route', async () => {
    harness();
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    expect(await findComposer()).toBeTruthy();
    expect(window.location.pathname).toBe(`/area/c2/new`);

    expect(screen.queryByRole('dialog')).toBeNull();

    window.history.back();
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await findComposer()).toBeTruthy();
    expect(window.location.pathname).toBe(`/area/c1/new`);
  });

  it('remounts the route when switching directly between Areas and takes only the new Area defaults', async () => {
    const { sent } = harness({

      areaDefaults: { default_template_id: 'small-change', defaultCwd: '/srv/work-a' },
      otherAreaDefaults: { default_template_id: null, defaultCwd: '/srv/work-b' },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    expect(screen.queryByRole('button', { name: /^Template:/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Repo: work-a · Direct folder (/srv/work-a)' })).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Draft for A');

    await userEvent.click(screen.getByRole('button', { name: 'New track in Reading' }));
    await waitFor(() => expect(window.location.pathname).toBe(`/area/c2/new`));
    expect(composerText()).toBe('');
    expect(screen.queryByRole('button', { name: /^Template:/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Repo: work-b · Direct folder (/srv/work-b)' })).toBeTruthy();

    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Draft for B');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    const body = createdTrackBodies(sent)[0] as Record<string, unknown>;
    expect(body).toMatchObject({ areaId: 'c2', cwd: '/srv/work-b' });
    expect(body).not.toHaveProperty('template_id');
  });

  it('does not let a create started in one Area navigate away after switching to another Area', async () => {
    let releaseCreate!: () => void;
    const heldCreate = new Promise<void>((resolve) => { releaseCreate = resolve; });
    const { sent } = harness({ heldCreate });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Create in A');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));

    await userEvent.click(screen.getByRole('button', { name: 'New track in Reading' }));
    await waitFor(() => expect(window.location.pathname).toBe(`/area/c2/new`));
    releaseCreate();
    await act(async () => {
      await heldCreate;
      await new Promise((resolve) => { setTimeout(resolve, 0); });
    });
    await new Promise((resolve) => { setTimeout(resolve, 0); });
    expect(window.location.pathname).toBe(`/area/c2/new`);
  });

  it('arrives with the composer focused, so typing reaches it', async () => {
    harness();
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await act(async () => { await new Promise((resolve) => { requestAnimationFrame(() => resolve(null)); }); });

    expect(document.activeElement).toBe(screen.getByLabelText(TASK_LABEL));

    await userEvent.keyboard('Read it');
    expect(composerText()).toBe('Read it');
  });

  /* Asserted on key absence: `title: ''` reaches the same stored value, and only a
       missing key leaves `track_rename` able to name the track. */
  it('creates with no title control on screen and no title key on the wire', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    // Exposed first, so the absence checks below cannot pass vacuously.
    expect(await findComposer()).toBeTruthy();
    /* Identify the title control by name; the composer is also a textbox. */
    expect(screen.queryByRole('textbox', { name: /title/i })).toBeNull();
    expect(screen.queryByRole('combobox', { name: /title/i })).toBeNull();
    expect(screen.queryByLabelText(/title/i)).toBeNull();

    const create = await screen.findByRole('button', { name: 'Create track' });
    expect(create.getAttribute('aria-disabled')).toBe('true');
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    expect(create.getAttribute('aria-disabled')).toBeNull();
    await userEvent.click(create);
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    const body = createdTrackBodies(sent)[0] as Record<string, unknown>;
    expect(Object.hasOwn(body, 'title')).toBe(false);
    expect(body).toMatchObject({ areaId: 'c2' });
  });

  it('posts the opener\'s area_id and omits cwd / attach_folder with no folder chosen', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    // Exposed first: the absence checks below would pass vacuously against a page that never rendered.
    expect(await findComposer()).toBeTruthy();
    expect(screen.queryByLabelText('Area')).toBeNull();
    expect(screen.getByRole('button', { name: FOLDER_CHIP_NAME }).textContent).toBe(`Repo: ${FOLDER_PLACEHOLDER}`);
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(await screen.findByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    const body = createdTrackBodies(sent)[0] as Record<string, unknown>;
    expect(body).toMatchObject({ areaId: 'c2' });
    expect(body).not.toHaveProperty('theme');
    expect(body).not.toHaveProperty('title');
    expect(body).not.toHaveProperty('first_message');
    // The managed-workspace branch is keyed on *absence*, not on a value:
    // `cwd: null` and `attach_folder: false` are both a different kernel path.
    expect(body).toHaveProperty('workspaceOptionId', 'direct');
    expect(body).not.toHaveProperty('attach_folder');
    expect(sent.some((request) => request.path.startsWith('/api/v1/fs/listdir'))).toBe(false);
    // Blank means the key is not on the wire at all: `template_id: null` or `''` is a 400 from the kernel.
    expect(body).not.toHaveProperty('template_id');
    expect(body).not.toHaveProperty('templateInputs');
  });

  it('ignores the Area template default while preserving its directory snapshot', async () => {
    const message = '  Read it exactly  ';
    const { sent } = harness({

      areaDefaults: { default_template_id: 'small-change', defaultCwd: '/srv/ area ' },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await findComposer()).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^Template:|^Recipe:/i })).toBeNull();
    const folder = screen.getByRole('button', { name: 'Repo: area · Direct folder (/srv/ area )' });
    expect(folder.getAttribute('aria-label')).toBe('Repo:  area  · Direct folder (/srv/ area )');
    await userEvent.type(screen.getByLabelText(TASK_LABEL), message);
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    expect(createdTrackBodies(sent)[0]).toMatchObject({
      areaId: 'c1',
      cwd: '/srv/ area ',
    });
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('template_id');
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('templateInputs');
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('recipe_id');
  });

  it('lets one Track clear Area defaults back to a new managed folder', async () => {
    const { sent } = harness({

      areaDefaults: { default_template_id: 'small-change', defaultCwd: '/srv/area-default' },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await findComposer()).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: /^Repo: area-default/ }));
    await userEvent.click(await screen.findByRole('menuitem', { name: /Perga workspace/ }));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    const body = createdTrackBodies(sent)[0] as Record<string, unknown>;
    expect(body).not.toHaveProperty('template_id');
    expect(body).toHaveProperty('workspaceOptionId', 'direct');
    expect(body).not.toHaveProperty('attach_folder');
  });

  /* The kernel forwards `first_message` untrimmed and hashes it untrimmed, so surrounding whitespace is content. */
  it('posts the sentence exactly as typed, whitespace and all', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    const padded = '  keep indentation  ';
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), padded);
    await userEvent.click(await screen.findByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
  });

  /* With `attach_folder` omitted the kernel refuses any path no area has already claimed. */
  it('posts the picked folder as cwd with attach_folder: true', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    expect(await findComposer()).toBeTruthy();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');

    await userEvent.click(await screen.findByRole('button', { name: FOLDER_CHIP_NAME }));
    await userEvent.click(await screen.findByRole('menuitem', { name: /^Others…/ }));
    expect(await screen.findByRole('dialog', { name: 'Choose a directory' })).toBeTruthy();
    await screen.findByDisplayValue('/srv/app/');
    await userEvent.click(await screen.findByRole('button', { name: 'Select this directory' }));
    expect(await findComposer()).toBeTruthy();

    await userEvent.click(await screen.findByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    const body = createdTrackBodies(sent)[0] as Record<string, unknown>;
    expect(body).toMatchObject({
      areaId: 'c1', cwd: '/srv/app',
    });
    expect(sent.some((request) => request.path === '/api/v1/fs/listdir')).toBe(true);
    expect(body).not.toHaveProperty('template_id');
  });

  /* The 409 body has no `error` key, so `ApiError.message` is the bare status text. */
  it('creates without reading template or recipe rosters', async () => {
    const { sent } = harness();
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    expect(sent.some(request => request.path === '/api/v1/templates' || request.path === '/api/track-recipes')).toBe(false);
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it anyway');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    expect(createdTrackBodies(sent)[0]).toMatchObject({
      areaId: 'c2',
    });
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('title');
  });
});

describe('the route refuses an area id that no longer exists', () => {
  it('reports a deleted area instead of rendering a working composer', async () => {
    harness({ path: '/area/c9/new' });
    const alert = await within(await screen.findByRole('main')).findByRole('alert');
    expect(alert.textContent).toContain('This area could not be found.');
    expect(screen.queryByLabelText(TASK_LABEL)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Create track' })).toBeNull();
  });

  /* The load-bearing assertion is the one before `releaseAreas()`: a route that
       renders the form while the read is in flight would still satisfy a bare
       `findComposer()` at the end. */
  it('renders the composer for an area that does exist, and only once the list has landed', async () => {
    let releaseAreas = (): void => undefined;
    const held = new Promise<void>((resolve) => { releaseAreas = () => { resolve(); }; });
    harness({ path: '/area/c1/new', heldAreas: held });

    await screen.findByRole('button', { name: 'Go to Today' });
    expect(screen.queryByLabelText(TASK_LABEL)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Create track' })).toBeNull();

    releaseAreas();
    await held;
    expect(await findComposer()).toBeTruthy();
    expect(within(screen.getByRole('main')).queryByRole('alert')).toBeNull();
  });

  /* `workspace.areas` is `[]` while the read is in flight, so a bare `some()` would call every area deleted. */
  it('renders neither a composer nor a verdict while the area list is still loading', async () => {
    let releaseAreas = (): void => undefined;
    const held = new Promise<void>((resolve) => { releaseAreas = () => { resolve(); }; });
    harness({ path: '/area/c9/new', heldAreas: held });

    /* Without this the absence checks below would pass against an app that had not rendered at all. */
    await screen.findByRole('button', { name: 'Go to Today' });
    expect(window.location.pathname).toBe(`/area/c9/new`);

    expect(screen.queryByLabelText(TASK_LABEL)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Create track' })).toBeNull();
    expect(within(screen.getByRole('main')).queryByRole('alert')).toBeNull();

    releaseAreas();
    await held;
  });

  /* `areas` falls back to `[]` on failure with `areasLoading` false, so "has loading stopped" would read a 500 as a landed list. */
  it('reports a failed area read instead of a composer or a deletion verdict', async () => {
    harness({ path: '/area/c1/new', areasFail: true });

    /* Scoped to `main`: the rail reports the same failed read in its own ErrorBox.
           `waitFor`, because the failure needs a tick to land. */
    const main = await screen.findByRole('main');
    await waitFor(() => {
      expect(within(main).getByRole('alert').textContent).toContain('Areas are unavailable.');
    });
    // A 500's text is the server's internals, never the reader's.
    expect(within(main).getByRole('alert').textContent).not.toContain('areas are unreadable');
    // Not the deletion wording: the server never said this area is gone.
    expect(within(main).getByRole('alert').textContent).not.toContain('This area could not be found.');
    expect(within(main).getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(screen.queryByLabelText(TASK_LABEL)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Create track' })).toBeNull();
  });
});

describe('the sentence is delivered by the create, and the track opens on it', () => {
  it('sends the sentence once, on the create, and by no second write', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
    expect(createdTrackRequests(sent)[0]?.headers?.['Idempotency-Key']).toBeDefined();
    /* The `waitFor` above returns the instant the first create is seen; the landing
           is the settle window, so the count is read after the track page mounts. */
    await findTrackPage();
    expect(createdTrackBodies(sent)).toHaveLength(1);
  });

  it('opens the track\'s planner conversation on arrival', async () => {
    harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    /* Named, not by bare role: the track page's panel column is a `complementary` too. */
    expect(await screen.findByRole('complementary', { name: 'Planner' })).toBeTruthy();
  });

  /* The kernel writes the sentence to the transcript when the queue drains it, so the
       planner card's item read answers with it; nothing is minted on the client. */
  it('shows the persisted v1 input as the owner’s own message', async () => {
    const { sent } = harness({

      plannerItems: [sessionItem('card-planner', 1, 'Read it', 'user')],
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    const drawer = await screen.findByRole('complementary', { name: 'Planner' });
    await waitFor(() => expect(
      [...drawer.querySelectorAll('[data-nc-entry="human:input-1"] [data-nc-turn="you"]')].map((turn) => turn.textContent),
    ).toEqual(['Read it']));
    expect(drawer.querySelector('[data-nc-thread-empty]')).toBeNull();
    const items = sent.filter((request) => request.path.includes('/items') && !request.path.includes('limit=1'));
    expect(items.length).toBeGreaterThan(0);
    expect(plannerInputTexts(sent)).toEqual(['Read it']);
  });

  it('shows the sent first message optimistically while sending to the planner', async () => {
    const { sent } = harness({ });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
    const drawer = await screen.findByRole('complementary', { name: 'Planner' });
    await waitFor(() => expect(plannerInputTexts(sent)).toEqual(['Read it']));
    await waitFor(() => expect(drawer.querySelector('[data-nc-thread-empty]')).toBeNull());
    expect(drawer.querySelectorAll('[data-nc-turn="you"]')).toHaveLength(1);
  });

  it('navigates before the track detail lands, and opens the drawer when it does', async () => {
    let releaseDetail = (): void => undefined;
    const held = new Promise<void>((resolve) => { releaseDetail = () => { resolve(); }; });
    const { sent } = harness({ heldDetail: held });

    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => { expect(window.location.pathname).toBe(`/track/w-new`); });
    expect(createdTrackBodies(sent)).toHaveLength(1);
    await findTrackPage();
    expect(screen.queryByRole('complementary', { name: 'Planner' })).toBeNull();

    releaseDetail();
    await held;
    expect(await screen.findByRole('complementary', { name: 'Planner' })).toBeTruthy();
  }, 10_000);

  it('does not yank the reader back when the create lands after they left', async () => {
    let releaseCreate = (): void => undefined;
    const held = new Promise<void>((resolve) => { releaseCreate = () => { resolve(); }; });
    const { sent } = harness({ heldCreate: held });

    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await userEvent.click(await screen.findByRole('button', { name: 'Go to Today' }));
    await waitFor(() => { expect(window.location.pathname).toBe(`/`); });

    releaseCreate();
    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    expect(window.location.pathname).toBe(`/`);

    /* A leftover intent would surface on the next visit to the track, so that visit is the observable. */
    window.history.pushState({}, '', `/track/w-new`);
    /* The title only renders once the track detail has landed, the same read the
           drawer needs; asserting absence before that is vacuous. */
    await screen.findByRole('button', { name: 'Rename track' });
    expect(screen.queryByRole('complementary', { name: 'Planner' })).toBeNull();
  }, 10_000);

  it('still lands on the track when the track detail read fails', async () => {
    const { sent } = harness({
      trackDetail: { status: 500, statusText: 'Server Error', body: { error: 'boom' } },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
    await waitFor(() => expect(window.location.pathname).toBe(`/track/w-new`));
  });

  /* A failed harness start answers 500 *after* the track is minted, so the state of
       the server is the kernel's to say, not this test's. */
  it('reports a create that failed, keeps the sentence, and does not automatically retry', async () => {
    const { sent } = harness({
      trackCreate: { status: 500, statusText: 'Server Error', body: { error: 'boom' } },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    expect(await within(await screen.findByRole('main')).findByRole('alert')).toBeTruthy();
    expect(window.location.pathname).toBe(`/area/c2/new`);
    expect(composerText()).toBe('Read it');
    expect(createdTrackBodies(sent)).toHaveLength(1);
    expect(plannerInputTexts(sent)).toEqual([]);

    /* An explicit retry is the same draft and therefore the same key. */
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const [first, retry] = createdTrackRequests(sent);
    expect(retry?.headers?.['Idempotency-Key']).toBe(first?.headers?.['Idempotency-Key']);
  });

  it('rekeys an exhausted draft before the reader explicitly retries it', async () => {
    const { sent } = harness({
      trackCreate: {
        status: 409,
        statusText: 'Conflict',
        body: { error: 'this key is used up', code: 'idempotency_key_exhausted' },
      },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    expect((await within(await screen.findByRole('main')).findByRole('alert')).textContent).toContain('this key is used up');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const [exhausted, fresh] = createdTrackRequests(sent);
    expect(fresh?.headers?.['Idempotency-Key']).toBeDefined();
    expect(fresh?.headers?.['Idempotency-Key'])
      .not.toBe(exhausted?.headers?.['Idempotency-Key']);
    expect(createdTrackBodies(sent)).toEqual([
      expect.objectContaining({ areaId: 'c2' }),
      expect.objectContaining({ areaId: 'c2' }),
    ]);
    expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
    expect(createdTrackBodies(sent)[1]).not.toHaveProperty('first_message');
  });

  /* The server answers `idempotency_key_reused` both for a different create under the key and for a
     legacy binding it can no longer compare (#2068): the key can never be answered for this draft. */
  it.each([
    ['payload conflict', 'operation idempotency key k already used with different payload'],
    ['legacy unprovable key', 'this key predates durable request fingerprints'],
  ])('offers no retry after a %s, only an explicit new track', async (_case, errorMessage) => {
    const { sent } = harness({
      trackCreate: {
        status: 409,
        statusText: 'Conflict',
        body: { error: errorMessage, code: 'idempotency_key_reused' },
      },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    expect((await within(await screen.findByRole('main')).findByRole('alert')).textContent).toContain(errorMessage);
    expect(screen.getByRole('button', { name: 'Start as a new track' })).toBeTruthy();
    /* No retry under the reused key: Create is refused until the reader chooses a new track. */
    const create = screen.getByRole('button', { name: 'Create track' });
    expect(create.hasAttribute('disabled') || create.getAttribute('aria-disabled') === 'true').toBe(true);
    await userEvent.click(create);
    expect(createdTrackRequests(sent)).toHaveLength(1);
    const [conflict] = createdTrackRequests(sent);

    await userEvent.click(await screen.findByRole('button', { name: 'Start as a new track' }));
    expect(within(screen.getByRole('main')).queryByRole('alert')).toBeNull();
    expect(createdTrackRequests(sent)).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const explicitNew = createdTrackRequests(sent)[1];
    expect(explicitNew?.headers?.['Idempotency-Key']).toBeDefined();
    expect(explicitNew?.headers?.['Idempotency-Key'])
      .not.toBe(conflict?.headers?.['Idempotency-Key']);
  });

  it('keeps the key and the request after a concurrent create under the same key', async () => {
    const { sent } = harness({
      trackCreate: {
        status: 409,
        statusText: 'Conflict',
        body: { error: 'another request under this key was accepted', code: 'idempotency_key_concurrent' },
      },
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Reading' }));
    await findComposer();
    await userEvent.click(screen.getByLabelText(TASK_LABEL));
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Read it');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));

    expect(await within(await screen.findByRole('main')).findByRole('alert')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Start as a new track' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(2));
    const [first, retry] = createdTrackRequests(sent);
    expect(retry?.headers?.['Idempotency-Key']).toBe(first?.headers?.['Idempotency-Key']);
    expect(retry?.body).toEqual(first?.body);
  });
});

describe('a refused create that names a field', () => {
  it('names the input field a refused create is about', async () => {
    /* The kernel's exact answer to a `templateInputs` missing a required key: the reason, with the field apart. */
    harness({ trackCreateSequence: [{ status: 400, statusText: 'Bad Request', body: {
      error: 'required field is missing', code: 'bad_request', field: 'templateInputs.issue_url',
    } }] });
    await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
    await findComposer();
    await userEvent.type(screen.getByLabelText(TASK_LABEL), 'Bound input');
    await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
    expect((await within(await screen.findByRole('main')).findByRole('alert')).textContent)
      .toContain('templateInputs.issue_url: required field is missing');
  });
});

it.each([
  { templateId: 'compiled-template', templateInputs: { ticket: 'saved-input' } },
  { executionTargetId: 'remote', workspaceOptionId: 'direct' },
])('replays a restored unknown creation with its original starting point %j', async startingPoint => {
  const request = { key: 'native-unconfirmed-key', message: '  Native intent  ', body: {
    areaId: 'c1', provider: 'codex' as const,
    ...startingPoint,
  } };
  const submittedStorage = createMemoryStorage();
  submittedStorage.setItem(DRAFT_KEY, JSON.stringify(request));
  const { sent } = harness({ submittedStorage });
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  await findComposer();
  await waitFor(() => expect(screen.getByLabelText(TASK_LABEL).getAttribute('contenteditable')).toBe('false'));
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
  expect(createdTrackRequests(sent)[0]?.body).toMatchObject(request.body);
  expect(createdTrackRequests(sent)[0]?.headers?.['Idempotency-Key']).toBe(request.key);
});

it.each([
  ['ready', undefined, 'use @`template:deploy`'],
  ['ready', { type: 'object' }, 'use @`template:deploy`'],
  ['ready', null, 'use @`template:deploy`'],
  ['ready', undefined, 'use @`template:unknown`'],
  ['ready', undefined, 'use @`template:deploy` @`template:other`'],
  ['ready', undefined, 'use @`template:deploy` @`template:deploy`'],
] as const)('keeps template mentions as message content without implicit template binding (%s, %j, %s)', async (_status, input_schema, message) => {
  const { sent } = harness({ templates: [{ id: 'deploy', title: 'Deploy', description: null, tasks: [], input_schema,
    availability: { status: 'ready' } }] });
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  const composer = await findComposer();
  expect(sent.some(request => request.path === '/api/v1/templates')).toBe(false);
  await userEvent.type(composer, '@:');
  await screen.findByRole('option', { name: 'Deploy' });
  await userEvent.keyboard('{Escape}');
  await waitFor(() => expect(screen.queryByRole('listbox')).toBeNull());
  await userEvent.keyboard('{Control>}a{/Control}{Backspace}');
  await userEvent.type(composer, message);
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
  const body = createdTrackBodies(sent)[0];
  expect(body).not.toHaveProperty('first_message');
  expect(body).not.toHaveProperty('template_id');
});

it('leaves a pasted template reference to the first message when no catalog was loaded', async () => {
  const { sent } = harness();
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  const composer = await findComposer();
  await userEvent.click(composer);
  await userEvent.paste('use @`template:deploy`');
  await userEvent.click(screen.getByRole('button', { name: 'Create track' }));
  await waitFor(() => expect(createdTrackBodies(sent)).toHaveLength(1));
  expect(createdTrackBodies(sent)[0]).not.toHaveProperty('first_message');
  expect(createdTrackBodies(sent)[0]).not.toHaveProperty('template_id');
  expect(sent.some(request => request.path === '/api/v1/templates')).toBe(false);
});

it('keeps Perga creation implicit even when templates and an Area default exist', async () => {
  const { sent } = harness({
    path: '/area/c1/new',
    areaDefaults: { default_template_id: 'dev', defaultCwd: null },
    templates: [{ id: 'dev', title: 'Development', description: null, tasks: [], availability: { status: 'ready' } }],
  });
  const composer = await findComposer();
  await userEvent.type(composer, 'Build feature cleanly');
  expect(screen.queryByLabelText('Template')).toBeNull();
  expect(sent.some(request => request.path === '/api/v1/templates')).toBe(false);
});

it('keeps an unconfirmed image-only creation retryable after reload without storing an empty text initial send', async () => {
  const submittedStorage = createMemoryStorage();
  submittedStorage.setItem(DRAFT_KEY, JSON.stringify({
    key: 'image-only-unconfirmed-key',
    body: { areaId: 'c1', provider: 'codex' },
  }));
  const { sent } = harness({ submittedStorage });
  await userEvent.click(await screen.findByRole('button', { name: 'New track in Work' }));
  await findComposer();
  await waitFor(() => expect(screen.getByLabelText(TASK_LABEL).getAttribute('contenteditable')).toBe('false'));
  const submit = screen.getByRole('button', { name: 'Create track' });
  expect(submit.getAttribute('aria-disabled')).toBeNull();
  await userEvent.click(submit);
  await waitFor(() => expect(createdTrackRequests(sent)).toHaveLength(1));
  expect(createdTrackRequests(sent)[0]?.headers?.['Idempotency-Key']).toBe('image-only-unconfirmed-key');
  expect(submittedStorage.getItem(FIRST_SEND_KEY)).toBeNull();
});
