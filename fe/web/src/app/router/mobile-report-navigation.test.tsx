import { fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
// @vitest-environment jsdom
/* The real `AppShell`, the real track route, a real router and a real memory history: every claim
 * here is about how two modules are wired together, which a stub on either side could not prove. */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router';
import { Profiler, type ProfilerOnRenderCallback } from 'react';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';

const AREA = { id: 'c1', name: 'Product', color: '#5B8DEF', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
/* A second empty Area makes the centered selector’s destination observable. */
const OTHER_AREA = { id: 'c2', name: 'Second', color: '#8B7FE8', sortOrder: 2, kind: 'user', createdAtMs: 1, updatedAtMs: 1 };
const TRACK = fixtureTrack('w1', { areaId: 'c1', title: 'Responsive mobile UI', cwd: '/tmp', sortOrder: 1, closedAtMs: null, createdAtMs: 1, updatedAtMs: 2 });
const CARD = { id: 'card-term', trackId: 'w1', title: 'Build log', cwd: '/tmp', status: 'running', createdAtMs: 1 };
/* One section and one task, which is what makes the Outline and TASKS panels non-empty. */
const REPORT_CARD = {
  id: 'card-report', track_id: 'w1', kind: 'track-report', title: 'Report card', sort: 2,
  deletable: false, created_at: 1, updated_at: 2,
  payload: {
    schemaVersion: 3,
    docRev: 1,
    summary: 's',
    body: 'b',
    blocks: [
      { id: 'b-1', kind: 'prose', rev: 1, payload: { markdown: '# Findings\n' } },
      {
        id: 'b-task', kind: 'task', rev: 1,
        payload: { key: 'ship-it', kind: 'terminal', declared_by: 'spec', ready: true, command: 'true' },
      },
    ],
  },
};
const unauthorized = createUnauthorizedChannel({ enqueue: (task) => task() });
const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

function setup(path: string, onCommit: ProfilerOnRenderCallback = () => {}) {
  const transport: ApiTransportPort = {
    send(request) {
      if (request.path.startsWith('/api/v1/today/daily')) return Promise.resolve(ok(null));
      if (request.path === '/api/v1/areas') return Promise.resolve(ok([AREA, OTHER_AREA]));
      if (request.path === '/api/v1/areas/c1/tracks?includeClosed=true') return Promise.resolve(ok([TRACK]));
      if (request.path === '/api/v1/areas/c2/tracks?includeClosed=true') return Promise.resolve(ok([]));
      if (request.path === '/api/v1/tracks/w1') return Promise.resolve(ok({ ...fixtureSnapshot(TRACK, [], { docRev: REPORT_CARD.payload.docRev, summary: REPORT_CARD.payload.summary, leadingComment: '', blocks: REPORT_CARD.payload.blocks.map(block => ({ ...block, markdown: block.payload.markdown ?? '' })) }), tasks: [{ id: 'task', trackId: 'w1', blockId: 'b-task', taskKey: 'ship-it', kind: 'command', ready: true, status: 'pending', createdAtMs: 1, updatedAtMs: 1 }] }));
      if (request.path === '/api/v1/tracks/w1/terminals') return Promise.resolve(ok([CARD]));
      if (request.path === '/api/v1/tracks/w1/report') return Promise.resolve(ok({ taskDiagnostics: [] }));
      return Promise.resolve(ok([]));
    },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument,
    transport, unauthorized, client, cards: bootTestCardRuntime(),
  });
  router.update({ history: createMemoryHistory({ initialEntries: [path] }) });
  render(<Profiler id="mobile shell" onRender={onCommit}><QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => null, setItem: () => undefined }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider></Profiler>);
  return router;
}

const href = (router: ReturnType<typeof setup>) => router.state.location.href;
const mobilePanel = () => document.querySelector('[data-nc-mobile-page]');

const reportEntry = () => screen.getByRole('button', { name: 'Track actions' });

async function openPanel(router: ReturnType<typeof setup>, panel: 'cards' | 'tasks' | 'outline' | 'conversations'): Promise<void> {
  await act(() => router.navigate({ to: '/track/w1', search: { panel } }));
}

/* A `matchMedia` whose answer can change, with real listeners: widening the window is a reachable gesture. */
function stubViewport(initiallyCompact: boolean) {
  const listeners = new Set<() => void>();
  let compact = initiallyCompact;
  vi.stubGlobal('matchMedia', vi.fn((media: string) => ({
    get matches() { return media.includes('width') ? compact : media.includes('prefers-reduced-motion'); },
    media,
    onchange: null,
    // Only the width query's subscribers are replayed: `ThemeProvider`'s handler reads the event, which
    // a synthetic width change does not have.
    addEventListener: (_type: string, listener: () => void) => {
      if (media.includes('width')) listeners.add(listener);
    },
    removeEventListener: (_type: string, listener: () => void) => { listeners.delete(listener); },
    addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
  })));
  return {
    widen() {
      compact = false;
      act(() => { for (const listener of [...listeners]) listener(); });
    },
  };
}

beforeEach(() => {
  // `RAIL_COLLAPSE_QUERY` is the only media query the compact shell asks about.
  vi.stubGlobal('matchMedia', vi.fn((media: string) => ({
    // Lifecycle is covered here; real animation trajectories are checked in browser tests.
    matches: media.includes('width') || media.includes('prefers-reduced-motion'), media, onchange: null,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
  })));
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callback(0); return 1; });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('the mobile report panel is the URL (#1191 §2.4)', () => {
  it('opens through ?panel= and puts focus in the panel container', async () => {
    const router = setup('/track/w1');
    await openPanel(router, 'cards');

    await waitFor(() => { expect(href(router)).toBe('/track/w1?panel=cards'); });
    expect(screen.getByRole('heading', { name: 'Tools' })).toBeTruthy();
    // The panel container takes focus, not whatever the menu left behind.
    await waitFor(() => { expect(document.activeElement).toBe(mobilePanel()); });
  });

  it('closes back to the report and returns focus to the report actions', async () => {
    const router = setup('/track/w1');
    await openPanel(router, 'cards');
    await waitFor(() => { expect(href(router)).toBe('/track/w1?panel=cards'); });

    await userEvent.click(screen.getByRole('button', { name: 'Back to Report' }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    /* The opener, not the body: closing removes the control the click landed on. */
    await waitFor(() => { expect(document.activeElement).toBe(reportEntry()); });
  });

  it('lands focus in the panel on a cold-start deep link', async () => {
    setup('/track/w1?panel=tasks');
    expect(await screen.findByRole('heading', { name: 'Tasks' })).toBeTruthy();
    // Nobody clicked anything: the first render is already inside the panel.
    await waitFor(() => { expect(document.activeElement).toBe(mobilePanel()); });
  });

  it('answers the hardware Back button, focus included', async () => {
    const router = setup('/track/w1');
    await openPanel(router, 'cards');
    await waitFor(() => { expect(href(router)).toBe('/track/w1?panel=cards'); });

    // A POP, not a click: the panel state is nowhere but the URL.
    router.history.back();
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(mobilePanel()?.getAttribute('data-nc-mobile-page')).toBe('closed');
    await waitFor(() => { expect(document.activeElement).toBe(reportEntry()); });
  });

  it('keeps the same report entry after repeated panel Back and Area navigation', async () => {
    const router = setup('/track/w1');
    await screen.findByRole('button', { name: 'Open area Product' });
    expect(router.history.location.state.__TSR_index).toBe(0);
    for (let cycle = 0; cycle < 3; cycle += 1) {
      await openPanel(router, 'cards');
      const panel = mobilePanel() as HTMLElement;
      expect(router.history.location.state.__TSR_index).toBe(1);
      await userEvent.click(await within(panel).findByRole('button', { name: 'Back to Report' }));
      await waitFor(() => { expect(router.history.location.state.__TSR_index).toBe(0); });
      await userEvent.click(await screen.findByRole('button', { name: 'Open area Product' }));
      await screen.findByRole('heading', { name: 'Product' });
      expect(href(router)).toBe('/area/c1');
      expect(router.history.location.state.__TSR_index).toBe(1);
      await userEvent.click(screen.getByRole('button', { name: 'Back to Report' }));
      await screen.findByRole('button', { name: 'Open area Product' });
      expect(href(router)).toBe('/track/w1');
      expect(router.history.location.state.__TSR_index).toBe(0);
    }
    expect(router.history.length).toBe(2);
    expect(router.history.canGoBack()).toBe(false);
  });

  it('sends an outline entry to the block anchor and clears ?panel=', async () => {
    const router = setup('/track/w1?panel=outline');
    // Scoped to the sheet: the desktop report rail draws the very same outline.
    const panel = await waitFor(() => { const found = mobilePanel(); expect(found).not.toBeNull(); return found!; });
    await userEvent.click(await within(panel as HTMLElement).findByRole('button', { name: /Findings/ }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1#b-1-h1'); });
  });

  it('sends a TASKS entry to the block anchor and clears ?panel=', async () => {
    const router = setup('/track/w1?panel=tasks');
    const panel = await waitFor(() => { const found = mobilePanel(); expect(found).not.toBeNull(); return found!; });
    await userEvent.click(await within(panel as HTMLElement).findByText('Waiting', { exact: true }));
    await userEvent.click(await within(panel as HTMLElement).findByRole('button', { name: /ship-it/ }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1#b-task'); });
  });
});

describe('mobile Track surface Back', () => {
  it.each(['panel=tasks', 'file=src%2Fa.ts', 'card=card-term'])('closes a saved %s resumed from Focus on the same Track', async (search) => {
    const router = setup(`/track/w1?${search}`);
    await screen.findByRole('button', { name: 'Back to Report' });
    await act(() => router.navigate({ to: '/' }));
    await userEvent.click(await screen.findByRole('button', { name: 'All' }));
    await userEvent.click(await screen.findByRole('button', { name: /^Responsive mobile UI\b/ }));
    await waitFor(() => { expect(href(router)).toBe(`/track/w1?${search}`); });

    await userEvent.click(await screen.findByRole('button', { name: 'Back to Report' }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.location.state.__TSR_index).toBe(2);
  });

  it('pops a card pushed from its own Report', async () => {
    const router = setup('/track/w1');
    await screen.findByRole('button', { name: 'Open area Product' });
    await act(() => router.navigate({ to: '/track/w1', search: { card: 'card-term' } }));
    await userEvent.click(await screen.findByRole('button', { name: 'Back to Report' }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.location.state.__TSR_index).toBe(0);
  });
});

describe('workspace header navigation', () => {
  it('keeps one header in every commit between the report and Focus', async () => {
    const headerCounts = new Set<number>();
    const router = setup('/track/w1', () => {
      headerCounts.add(document.querySelectorAll('[data-nc-mobile-header]').length);
    });
    await screen.findByRole('button', { name: 'Open area Product' });
    headerCounts.clear();

    await userEvent.click(screen.getByRole('button', { name: 'Back to Focus' }));
    await screen.findByRole('heading', { name: 'Focus' });
    expect(href(router)).toBe('/');
    expect(document.querySelector('[data-nc-workspace-header]')).toBeNull();
    expect(headerCounts).toEqual(new Set([1]));
    headerCounts.clear();
    await act(() => router.navigate({ to: '/track/w1' }));
    await screen.findByRole('button', { name: 'Open area Product' });
    expect(href(router)).toBe('/track/w1');
    expect(headerCounts).toEqual(new Set([1]));
  });

  it('opens the current track’s area without a dock', async () => {
    const router = setup('/track/w1');
    await userEvent.click(await screen.findByRole('button', { name: 'Open area Product' }));
    await screen.findByRole('heading', { name: 'Product' });
    expect(href(router)).toBe('/area/c1');
    expect(screen.queryByRole('dialog', { name: 'Tracks and settings' })).toBeNull();
    expect(document.querySelector('nav[aria-label="Primary"]')).toBeNull();
  });

  it('navigates between Areas through the main centered menu and opens that Area’s track list', async () => {
    const router = setup('/area/c1/new');
    await userEvent.click(await screen.findByRole('button', { name: 'Switch area, Product' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Second' }));
    await waitFor(() => { expect(href(router)).toBe('/area/c2/new'); });
    await userEvent.click(screen.getByRole('button', { name: 'Back to Second' }));
    await screen.findByRole('heading', { name: 'Second' });
    expect(href(router)).toBe('/area/c2');
    expect(screen.getByRole('button', { name: 'Back to Focus' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Areas' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: /^New track$/ }));
    await waitFor(() => { expect(href(router)).toBe('/area/c2/new'); });
    await waitFor(() => { expect(screen.queryByRole('dialog', { name: 'Tracks and settings' })).toBeNull(); });
    expect(await screen.findByRole('button', { name: 'Switch area, Second' })).toBeTruthy();
  });

  it('uses its Area predecessor when navigation opens the track', async () => {
    const router = setup('/');
    await userEvent.click(await screen.findByRole('button', { name: 'Open workspace' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Product' }));
    await userEvent.click(await screen.findByRole('button', { name: /^Responsive mobile UI\b/ }));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(await screen.findByRole('button', { name: 'Back to Product' })).toBeTruthy();
    expect(document.querySelector('nav[aria-label="Primary"]')).toBeNull();
  });

  it('keeps Today at home and starts an Area composer only after selecting an Area', async () => {
    const router = setup('/?day=2026-10-04');
    await waitFor(() => { expect(href(router)).toBe('/?day=2026-10-04'); });
    await userEvent.click(await screen.findByRole('button', { name: 'Switch area, Product' }));
    await userEvent.click(screen.getByRole('menuitem', { name: 'Second' }));
    await waitFor(() => { expect(href(router)).toBe('/area/c2/new'); });
    expect(screen.getByRole('button', { name: 'Switch area, Second' })).toBeTruthy();
  });
});

/* Above the breakpoint `?panel=` is not harmless: `TrackPage` derives `mobilePanelOpen` from the prop
 * alone and puts `inert` + `aria-hidden` on the DESKTOP panel surface — visible and unreachable. */
describe('a desktop viewport never lets ?panel= disable the track panel', () => {
  it('keeps the desktop panel in the accessibility tree for a shared ?panel= link', async () => {
    stubViewport(false);
    const router = setup('/track/w1?panel=cards');

    // A role query is the right instrument: `inert` + `aria-hidden` take the surface out of the accessibility tree.
    await screen.findByRole('region', { name: 'Workbench' });
    /* `^` anchors to the row itself: the delete sibling's accessible name also carries the card's title. */
    expect(await screen.findByRole('button', { name: /^Build log/ })).toBeTruthy();
    // And the URL stops claiming a state this viewport cannot be in.
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
  });

  it('keeps a Track reached on desktop with a saved panel URL', async () => {
    stubViewport(false);
    const router = setup('/');
    await screen.findByRole('navigation', { name: 'Workspace' });

    await openPanel(router, 'cards');

    await screen.findByRole('region', { name: 'Workbench' });
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.length).toBe(2);
  });

  it('drops ?panel= when the reader widens the window with the panel open', async () => {
    const viewport = stubViewport(true);
    const router = setup('/track/w1?panel=cards');
    expect(await screen.findByRole('heading', { name: 'Tools' })).toBeTruthy();
    expect(href(router)).toBe('/track/w1?panel=cards');

    viewport.widen();

    // `replace`, not a push: widening a window is not a place to go Back to.
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.length).toBe(1);
    /* `^` anchors to the row itself: the delete sibling's accessible name also carries the card's title. */
    expect(await screen.findByRole('button', { name: /^Build log/ })).toBeTruthy();
  });
});
