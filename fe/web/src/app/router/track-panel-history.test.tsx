// @vitest-environment jsdom
/*
 * The mobile panel's history strategy over a real memory history. History
 * assertions read `router.history.length`: `window.history.length` is pinned at 1
 * under jsdom and would pass no matter what this code did.
 */
import {
  Outlet, RouterProvider, createMemoryHistory, createRootRoute, createRoute, createRouter,
} from '@tanstack/react-router';
import { AppHistoryProvider } from './app-history.tsx';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  useGoSameTrack, useRouteCardId, useRouteFileBackPath, useRouteFilePath, useRouteHash, useRoutePanel,
  useRouteParam, useTrackFileNavigation, useTrackPanelNavigation, validateTrackSearch,
  type TrackSearch,
} from './navigation.ts';

function Probe() {
  const trackId = useRouteParam('/track/') ?? '';
  const { openPanel, closePanel } = useTrackPanelNavigation();
  const { openFile, closeFile } = useTrackFileNavigation();
  const goSameTrack = useGoSameTrack();
  const panel = useRoutePanel();
  const card = useRouteCardId();
  const file = useRouteFilePath();
  const backPath = useRouteFileBackPath();
  const hash = useRouteHash();
  return (
    <div>
      <span>{`panel:${panel ?? 'none'}`}</span>
      <span>{`card:${card ?? 'none'}`}</span>
      <span>{`file:${file ?? 'none'}`}</span>
      <span>{`back:${backPath ?? 'Report'}`}</span>
      <span>{`hash:${hash ?? 'none'}`}</span>
      <button type="button" onClick={() => { openPanel(trackId, 'cards'); }}>open cards</button>
      <button type="button" onClick={() => { openPanel(trackId, 'tasks'); }}>open tasks</button>
      <button type="button" onClick={() => { closePanel(trackId); }}>close panel</button>
      <button type="button" onClick={() => { openFile(trackId, '/tmp/a.md', { line: 42, endLine: 60, focus: false }); }}>open file a</button>
      <button type="button" onClick={() => { openFile(trackId, 'docs/b.md'); }}>open file b</button>
      <button type="button" onClick={() => { openFile(trackId, 'docs/b.md', { line: 12 }); }}>jump in b</button>
      <button type="button" onClick={() => { openFile(trackId, 'docs/b.md', { fromPath: 'resolved/a.md' }); }}>open b from resolved a</button>
      <button type="button" onClick={() => { openFile(trackId, 'docs/c.md'); }}>open file c</button>
      <button type="button" onClick={() => { openFile(trackId, 'docs/../README.md'); }}>open readme alias</button>
      <button type="button" onClick={() => { openFile(trackId, 'README.md', { fromPath: 'README.md' }); }}>open canonical readme</button>
      <button type="button" onClick={() => { closeFile(trackId); }}>close file</button>
      <button type="button" onClick={() => { goSameTrack(trackId, { card: undefined }, { replace: true }); }}>drop card</button>
      <button type="button" onClick={() => { goSameTrack('w9', { card: undefined }); }}>drop card on w9</button>
    </div>
  );
}

function setup(initialEntries: readonly string[], compact = false) {
  const media = window.matchMedia('');
  vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({ ...media, matches: compact && query.includes('width'), media: query }));
  const rootRoute = createRootRoute({ component: () => <AppHistoryProvider><Outlet /></AppHistoryProvider> });
  const todayRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/',
    component: () => <span>today</span>,
  });
  const trackRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/track/$trackId',
    validateSearch: (search: Record<string, unknown>): TrackSearch => validateTrackSearch(search),
    component: Probe,
  });
  const router = createRouter({
    routeTree: rootRoute.addChildren([todayRoute, trackRoute]),
    history: createMemoryHistory({ initialEntries: [...initialEntries] }),
  });
  render(<RouterProvider router={router} />);
  return router;
}

const href = (router: ReturnType<typeof setup>) => router.state.location.href;
const probe = (name: string, value: string) => screen.getByText(`${name}:${value}`);

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe('opening the mobile panel', () => {
  it('pushes one entry for the panel', async () => {
    const router = setup(['/', '/track/w1']);
    await screen.findByText('open cards');
    expect(router.history.length).toBe(2);

    await userEvent.click(screen.getByText('open cards'));
    await waitFor(() => { probe('panel', 'cards'); });
    expect(href(router)).toBe('/track/w1?panel=cards');
    expect(router.history.length).toBe(3);
  });

  it('replaces when swapping panels', async () => {
    const router = setup(['/', '/track/w1']);
    await userEvent.click(await screen.findByText('open cards'));
    await waitFor(() => { probe('panel', 'cards'); });

    await userEvent.click(screen.getByText('open tasks'));
    await waitFor(() => { probe('panel', 'tasks'); });
    // One entry for the whole visit to the panel layer, not one per panel.
    expect(router.history.length).toBe(3);
  });

  it('ignores an illegal value and lets the route validator drop foreign ones', async () => {
    const router = setup(['/track/w1?panel=bogus&debug=1']);
    // The parser refuses the illegal value rather than throwing on it.
    await waitFor(() => { probe('panel', 'none'); });

    await userEvent.click(screen.getByText('open cards'));
    /* Both facts are `validateTrackSearch`'s: `panel=cards` is kept and `debug=1` is not on the whitelist. */
    await waitFor(() => { expect(href(router)).toBe('/track/w1?panel=cards'); });
  });

  it('keeps the return surface and the block anchor', async () => {
    const router = setup(['/track/w1?from=area#b3']);
    await userEvent.click(await screen.findByText('open cards'));
    await waitFor(() => { probe('panel', 'cards'); });
    expect(href(router)).toBe('/track/w1?panel=cards#b3');
  });
});

describe('closing the mobile panel', () => {
  it('[#1191 §0.3] pops the pushed entry instead of stacking a duplicate', async () => {
    const router = setup(['/', '/track/w1']);
    await userEvent.click(await screen.findByText('open cards'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1?panel=cards'); });

    await userEvent.click(screen.getByText('close panel'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });

    /* A `replace`-only close leaves `[/, /track/w1, /track/w1]`, so this Back would
         land on the track again. */
    router.history.back();
    await waitFor(() => { expect(router.state.location.pathname).toBe('/'); });
    expect(await screen.findByText('today')).toBeTruthy();
  });

  it('survives three open/close cycles without growing the stack', async () => {
    const router = setup(['/', '/track/w1']);
    for (let cycle = 0; cycle < 3; cycle += 1) {
      await userEvent.click(await screen.findByText('open cards'));
      await waitFor(() => { probe('panel', 'cards'); });
      await userEvent.click(screen.getByText('close panel'));
      await waitFor(() => { probe('panel', 'none'); });
    }
    router.history.back();
    await waitFor(() => { expect(router.state.location.pathname).toBe('/'); });
  });

  it('replaces on a cold-start deep link, and never steps out of the app', async () => {
    const router = setup(['/track/w1?panel=cards&from=pages']);
    await waitFor(() => { probe('panel', 'cards'); });
    expect(router.history.canGoBack()).toBe(false);
    const back = vi.spyOn(router.history, 'back');

    await userEvent.click(screen.getByText('close panel'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    // An unconditional `back()` here would leave the application entirely.
    expect(back).not.toHaveBeenCalled();
    expect(router.history.length).toBe(1);
    expect(router.state.location.pathname).toBe('/track/w1');
  });

  it('replaces when the previous entry is also a panel on the same track', async () => {
    const router = setup(['/track/w1?panel=cards'], true);
    await screen.findByText('panel:cards');
    await act(() => router.navigate({ to: '/track/w1', search: { panel: 'tasks' } }));
    await userEvent.click(screen.getByText('close panel'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.location.state.__TSR_index).toBe(1);
  });
});

describe('file history', () => {
  it.each([false, true])('pushes distinct files and returns through each file to the Report (compact=%s)', async (compact) => {
    const router = setup(['/', '/track/w1'], compact);
    await userEvent.click(await screen.findByText('open file a'));
    await waitFor(() => { probe('file', '/tmp/a.md'); });
    expect(router.history.length).toBe(3);
    expect(router.history.location.state.ncFilePushed).toBe(true);
    probe('back', 'Report');
    expect(router.history.location.state.ncFileSilent).toBe(true);
    expect(router.state.location.search).toMatchObject({ line: '42-60' });

    await userEvent.click(screen.getByText('open file b'));
    await waitFor(() => { probe('file', 'docs/b.md'); });
    expect(router.history.location.state.ncFileSilent).toBe(false);
    expect(router.state.location.search).not.toHaveProperty('line');
    probe('back', '/tmp/a.md');
    expect(router.history.length).toBe(4);
    expect(router.history.location.state.ncFilePushed).toBe(true);

    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', '/tmp/a.md'); });
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', 'none'); });
    router.history.back();
    await waitFor(() => { expect(router.state.location.pathname).toBe('/'); });
  });

  it('replaces when the previous entry is the same file at a different line on mobile', async () => {
    const router = setup(['/track/w1?file=README.md&line=1'], true);
    await screen.findByText('file:README.md');
    await act(() => router.navigate({ to: '/track/w1', search: { file: 'README.md', line: 2 } }));
    probe('back', 'Report');
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(router.history.location.state.__TSR_index).toBe(1);
  });

  it('replaces same-file line jumps and keeps the canonical predecessor label', async () => {
    const router = setup(['/track/w1']);
    await userEvent.click(await screen.findByText('open file a'));
    await userEvent.click(screen.getByText('open b from resolved a'));
    await waitFor(() => { probe('back', 'resolved/a.md'); });
    expect(router.history.length).toBe(3);
    await userEvent.click(screen.getByText('jump in b'));
    await waitFor(() => { expect(router.state.location.search).toMatchObject({ line: 12 }); });
    expect(router.history.length).toBe(3);
    probe('back', 'resolved/a.md');
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', '/tmp/a.md'); });
    probe('back', 'Report');
    router.history.forward();
    await waitFor(() => { probe('file', 'docs/b.md'); });
    probe('back', 'resolved/a.md');
    router.history.back();
    await waitFor(() => { probe('file', '/tmp/a.md'); });
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', 'none'); });
    await userEvent.click(screen.getByText('open file b'));
    await waitFor(() => { probe('file', 'docs/b.md'); });
    probe('back', 'Report');
    expect(router.history.location.state.ncFileBackPath).toBeUndefined();
  });

  it('replaces a canonical same-file request and preserves its predecessor', async () => {
    const router = setup(['/track/w1']);
    await userEvent.click(await screen.findByText('open file a'));
    await userEvent.click(screen.getByText('open readme alias'));
    await waitFor(() => { probe('file', 'docs/../README.md'); });
    expect(router.history.length).toBe(3);
    await userEvent.click(screen.getByText('open canonical readme'));
    await waitFor(() => { probe('file', 'README.md'); });
    expect(router.history.length).toBe(3);
    probe('back', '/tmp/a.md');
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', '/tmp/a.md'); });
  });

  it('pushes from a cold file and returns to that file before replacing with Report', async () => {
    const router = setup(['/track/w1?file=/tmp/a.md']);
    await userEvent.click(await screen.findByText('open file a'));
    expect(router.history.length).toBe(1);
    probe('back', 'Report');
    await userEvent.click(screen.getByText('open file b'));
    await waitFor(() => { probe('back', '/tmp/a.md'); });
    expect(router.history.length).toBe(2);
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', '/tmp/a.md'); });
    probe('back', 'Report');
    const back = vi.spyOn(router.history, 'back');
    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { probe('file', 'none'); });
    expect(router.state.location.pathname).toBe('/track/w1');
    expect(back).not.toHaveBeenCalled();
  });

  it('does not advertise an unusable predecessor at the start of history', async () => {
    const router = setup(['/track/w1?file=docs/b.md']);
    await screen.findByText('open file b');
    await router.navigate({ to: '/track/w1', search: { file: 'docs/b.md' }, replace: true,
      state: { ncFilePushed: true, ncFileBackPath: '/tmp/a.md' } });
    await waitFor(() => { probe('back', 'Report'); });
    expect(router.history.canGoBack()).toBe(false);
  });

  it('closes a cold-start file deep link with replace instead of leaving the app', async () => {
    const router = setup(['/track/w1?file=README.md&from=pages']);
    await waitFor(() => { probe('file', 'README.md'); });
    const back = vi.spyOn(router.history, 'back');

    await userEvent.click(screen.getByText('close file'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
    expect(back).not.toHaveBeenCalled();
    expect(router.history.length).toBe(1);
  });
});

describe('useGoSameTrack', () => {
  it('keeps the anchor while dropping the card', async () => {
    const router = setup(['/track/w1?card=c1&from=area#b7']);
    await waitFor(() => { probe('card', 'c1'); });

    await userEvent.click(screen.getByText('drop card'));
    await waitFor(() => { expect(href(router)).toBe('/track/w1#b7'); });
    expect(router.history.length).toBe(1);
  });

  it('clears everything when the track it was given is not the track in the URL', async () => {
    const router = setup(['/track/w1?card=c1&from=area#b7']);
    await waitFor(() => { probe('card', 'c1'); });

    // `w9` is not this URL's track, so this is an ordinary navigation and no
    // parameter of `w1` may ride along.
    await userEvent.click(screen.getByText('drop card on w9'));
    await waitFor(() => { expect(router.state.location.pathname).toBe('/track/w9'); });
    expect(href(router)).toBe('/track/w9');
  });


});

it('keeps the Report back entry across mobile panel and file switches', async () => {
  const router = setup(['/', '/track/w1'], true);
  await userEvent.click(await screen.findByText('open tasks'));
  await waitFor(() => { probe('panel', 'tasks'); });

  await userEvent.click(screen.getByText('open file b'));
  await waitFor(() => { probe('file', 'docs/b.md'); });
  expect(router.history.length).toBe(3);
  // Switching back to a panel must keep the same underlying Report entry.
  await userEvent.click(screen.getByText('open tasks'));
  await waitFor(() => { probe('panel', 'tasks'); });
  await userEvent.click(screen.getByText('open file b'));
  await waitFor(() => { probe('file', 'docs/b.md'); });
  expect(router.history.length).toBe(3);
  await userEvent.click(screen.getByText('close file'));
  await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
  router.history.back();
  await waitFor(() => { expect(href(router)).toBe('/'); });
});

it.each(['file-close', 'file-history', 'panel-close'] as const)('transfers the file predecessor through a panel replacement (%s)', async mode => {
  const router = setup(['/track/w1']);
  await userEvent.click(await screen.findByText('open file a'));
  await userEvent.click(screen.getByText('open file b'));
  await waitFor(() => { probe('back', '/tmp/a.md'); });
  await userEvent.click(screen.getByText('open tasks'));
  await waitFor(() => { probe('panel', 'tasks'); });
  await userEvent.click(screen.getByText('open cards'));
  await waitFor(() => { probe('panel', 'cards'); });
  if (mode === 'panel-close') {
    await userEvent.click(screen.getByText('close panel'));
  } else {
    await userEvent.click(screen.getByText('open file c'));
    await waitFor(() => { probe('file', 'docs/c.md'); });
    probe('back', '/tmp/a.md');
    expect(router.history.length).toBe(3);
    if (mode === 'file-close') await userEvent.click(screen.getByText('close file'));
    else router.history.back();
  }
  await waitFor(() => { probe('file', '/tmp/a.md'); });
  probe('back', 'Report');
  await userEvent.click(screen.getByText('close file'));
  await waitFor(() => { expect(href(router)).toBe('/track/w1'); });
});
