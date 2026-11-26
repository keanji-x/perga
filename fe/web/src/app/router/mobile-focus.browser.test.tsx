import '../../styles/entry.css';
import { cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';

import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import type { ReadReceipt } from '../../../../core/domain/read-receipts.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureTrack } from './session-contract-fixture.ts';

afterEach(async () => {
  cleanup();
  document.documentElement.removeAttribute('data-theme');
  await page.viewport(1280, 800);
});

type FocusFixtureOptions = Readonly<{
  extraQuietCount?: number;
  initialReceipts?: readonly ReadReceipt[];
  manualUnreadTrackId?: string;
  askTrackUnreadSessionId?: string;
  shareScope?: unknown;
  holdOverlays?: boolean;
}>;

function renderFocusFixture(options: FocusFixtureOptions = {}) {
  const now = Date.now();
  const initialReceipts: ReadReceipt[] = [...(options.initialReceipts ?? [])];
  if (options.manualUnreadTrackId !== undefined) {
    initialReceipts.push({
      kind: 'track',
      targetId: options.manualUnreadTrackId,
      readThroughMs: 0,
      manualUnread: true,
    });
  }
  const receipts = createReadReceiptsKernel({ baselineMs: now - 3_600_000, receipts: initialReceipts });

  const productArea = {
    id: 'a-prod',
    name: 'Product',
    kind: 'user',
    color: '#5B8DEF',
    sortOrder: 1,
    createdAtMs: now - 86_400_000,
    updatedAtMs: now - 120_000,
  };
  const backendArea = {
    id: 'a-back',
    name: 'Backend',
    kind: 'user',
    color: '#2F9E44',
    sortOrder: 2,
    createdAtMs: now - 86_400_000,
    updatedAtMs: now - 1_080_000,
  };
  const askTrack = fixtureTrack('t-ask', {
    areaId: 'a-prod',
    title: 'Mobile layout polish',
    updatedAtMs: now - 120_000,
  });
  const unreadProdTrack = fixtureTrack('t-unread-prod', {
    areaId: 'a-prod',
    title: 'Design system review',
    updatedAtMs: now - 600_000,
  });
  const pinnedTrack = fixtureTrack('t-pinned', {
    areaId: 'a-prod',
    title: 'Q4 Mobile Roadmap',
    pinnedAtMs: now - 7_200_000,
    updatedAtMs: now - 7_200_000,
  });
  const closedTrack = fixtureTrack('t-closed', {
    areaId: 'a-prod',
    title: 'Old mobile shell notes',
    closedAtMs: now - 172_800_000,
    updatedAtMs: now - 172_800_000,
  });
  const unreadBackTrack = fixtureTrack('t-unread', {
    areaId: 'a-back',
    title: 'SQLite WAL checkpoint guard',
    updatedAtMs: now - 1_080_000,
  });
  const quietTrack = fixtureTrack('t-quiet', {
    areaId: 'a-back',
    title: 'Archive cleanup notes',
    updatedAtMs: now - 86_400_000,
  });

  const extraTracks = Array.from({ length: options.extraQuietCount ?? 0 }, (_, index) =>
    fixtureTrack(`t-extra-${index + 1}`, {
      areaId: index % 2 === 0 ? 'a-prod' : 'a-back',
      title: `Overflow track ${index + 1}`,
      pinnedAtMs: now - 10_000_000 - index * 1_000,
      updatedAtMs: now - 10_000_000 - index * 1_000,
    }),
  );

  const prodTracks = [
    askTrack,
    unreadProdTrack,
    pinnedTrack,
    closedTrack,
    ...extraTracks.filter((track) => track.areaId === 'a-prod'),
  ];
  const backTracks = [
    unreadBackTrack,
    quietTrack,
    ...extraTracks.filter((track) => track.areaId === 'a-back'),
  ];

  let releaseOverlays: (() => void) | null = null;
  const overlaysGate = options.holdOverlays === true
    ? new Promise<void>((resolve) => { releaseOverlays = resolve; })
    : null;

  const overlaysBody = {
    tracks: [
      {
        trackId: 't-ask',
        areaId: 'a-prod',
        isWorking: false,
        openAskCount: 1,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 120_000,
        notifiableSessions: options.askTrackUnreadSessionId === undefined
          ? []
          : [{ sessionId: options.askTrackUnreadSessionId, lastNotifiableTurnAtMs: now - 120_000 }],
      },
      {
        trackId: 't-unread-prod',
        areaId: 'a-prod',
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 600_000,
        notifiableSessions: [{ sessionId: 'ses-prod', lastNotifiableTurnAtMs: now - 600_000 }],
      },
      {
        trackId: 't-pinned',
        areaId: 'a-prod',
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 7_200_000,
        notifiableSessions: [],
      },
      {
        trackId: 't-closed',
        areaId: 'a-prod',
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 172_800_000,
        notifiableSessions: [],
      },
      {
        trackId: 't-unread',
        areaId: 'a-back',
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 1_080_000,
        notifiableSessions: [{ sessionId: 'ses-wal', lastNotifiableTurnAtMs: now - 1_080_000 }],
      },
      {
        trackId: 't-quiet',
        areaId: 'a-back',
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: now - 86_400_000,
        notifiableSessions: [],
      },
      ...extraTracks.map((track) => ({
        trackId: track.id,
        areaId: track.areaId,
        isWorking: false,
        openAskCount: 0,
        pendingApprovalCount: 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: track.updatedAtMs,
        notifiableSessions: [],
      })),
    ],
  };

  const fixture = renderDailyFixture({
    initial: '/',
    receipts,
    shareScope: options.shareScope,
    reply: async (request: ApiRequest): Promise<ApiTransportResponse | undefined> => {
      if (request.path === '/api/v1/areas') {
        return { status: 200, statusText: 'OK', body: [productArea, backendArea] };
      }
      if (request.path === '/api/v1/areas/a-prod/tracks?includeClosed=true') {
        return { status: 200, statusText: 'OK', body: prodTracks };
      }
      if (request.path === '/api/v1/areas/a-back/tracks?includeClosed=true') {
        return { status: 200, statusText: 'OK', body: backTracks };
      }
      if (request.path.startsWith('/api/v1/overlays?')) {
        if (overlaysGate !== null) await overlaysGate;
        return { status: 200, statusText: 'OK', body: overlaysBody };
      }
      return undefined;
    },
  });

  return {
    ...fixture,
    now,
    receipts,
    releaseOverlays: () => { releaseOverlays?.(); },
  };
}

describe('MobileFocusHome', () => {
  it.each([
    [390, 844],
    [320, 640],
  ] as const)('renders full-bleed scrollable Focus feed with one header, zero overflow, and 44px touch targets at %ix%i', async (width, height) => {
    await page.viewport(width, height);
    renderFocusFixture({ extraQuietCount: 12 });

    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    await expect.element(page.getByRole('region', { name: 'Needs attention', exact: true })).toBeVisible();
    await expect.element(page.getByRole('region', { name: 'Unread updates', exact: true })).toBeVisible();
    await expect.element(page.getByRole('region', { name: 'Pinned', exact: true })).toBeVisible();

    // Only one visible mobile header and no floating folder orb or bottom composer dock.
    const headers = [...document.querySelectorAll('[data-nc-mobile-header]')]
      .filter((node) => node.getBoundingClientRect().width > 0);
    expect(headers).toHaveLength(1);
    expect(document.querySelector('[data-nc-workspace-header]')).toBeNull();
    expect(document.querySelector('[data-nc-mobile-report-chat]')).toBeNull();
    expect(page.getByRole('button', { name: 'Open workspace', exact: true }).elements()).toHaveLength(1);

    // <main> fills the viewport height and the feed scroll container actually scrolls to the final row.
    const main = document.querySelector('main');
    expect(main).not.toBeNull();
    expect(main!.getBoundingClientRect().height).toBeGreaterThanOrEqual(height - 2);

    const feedScroll = document.querySelector<HTMLElement>('[data-nc-focus-feed]');
    expect(feedScroll).not.toBeNull();
    const menuGlyph = page.getByRole('button', { name: 'Open workspace', exact: true }).element().querySelector('svg')!.getBoundingClientRect();
    const title = page.getByRole('heading', { name: 'Focus', exact: true }).element().getBoundingClientRect();
    const contentLeft = feedScroll!.getBoundingClientRect().left + parseFloat(getComputedStyle(feedScroll!).paddingInlineStart);
    expect(Math.abs((menuGlyph.top + menuGlyph.height / 2) - (title.top + title.height / 2))).toBeLessThanOrEqual(1);
    expect(Math.abs(menuGlyph.left - contentLeft)).toBeLessThanOrEqual(1);
    expect(feedScroll!.scrollHeight).toBeGreaterThan(feedScroll!.clientHeight);
    feedScroll!.scrollTop = feedScroll!.scrollHeight;
    await expect.element(page.getByRole('button', { name: /Overflow track 12/ })).toBeInViewport();
    feedScroll!.scrollTop = 0;

    // Header actions, section action, and filter pills all meet the 44px touch target floor.
    for (const label of ['Open workspace', 'Search tracks', 'New track'] as const) {
      const box = page.getByRole('button', { name: label, exact: true }).element().getBoundingClientRect();
      expect(box.width).toBeGreaterThanOrEqual(44);
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
    expect(page.getByRole('button', { name: 'Mark all read', exact: true }).element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    for (const pill of page.getByRole('navigation', { name: 'Focus filters', exact: true }).getByRole('button').elements()) {
      expect(pill.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    }

    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
  });

  it('searches quiet & closed tracks from Focus, handles IME-safe Escape, preserves query via Show all matches, and navigates', async () => {
    await page.viewport(390, 844);
    const { router } = renderFocusFixture();

    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();

    // Expand inline search and verify search controls meet 44px height.
    await page.getByRole('button', { name: 'Search tracks', exact: true }).click();
    const searchInput = page.getByRole('searchbox', { name: 'Search tracks', exact: true });
    await expect.element(searchInput).toBeVisible();
    expect(page.getByRole('combobox', { name: 'Filter by area', exact: true }).element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44);

    // Quiet open track is searchable directly from default Focus tab.
    await searchInput.fill('archive');
    await expect.element(page.getByRole('region', { name: 'Other matches', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: /Archive cleanup notes/ })).toBeVisible();
    expect(page.getByRole('button', { name: 'Clear search', exact: true }).element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44);

    // Switching to Pinned while searching shows a 1-tap rescue CTA that preserves the search query.
    await page.getByRole('button', { name: /^Pinned/ }).click();
    await expect.element(page.getByText('No matching tracks in Pinned', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Show all 1 match', exact: true }).click();
    await expect.element(page.getByRole('button', { name: /Archive cleanup notes/ })).toBeVisible();
    expect((searchInput.element() as HTMLInputElement).value).toBe('archive');

    // Closed track is also searchable from default Focus tab.
    await page.getByRole('button', { name: /^Focus/ }).click();
    await searchInput.fill('old mobile');
    await expect.element(page.getByRole('region', { name: 'Closed tracks', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: /Old mobile shell notes/ })).toBeVisible();

    // IME composing Escape must NOT collapse search.
    const composingEsc = new KeyboardEvent('keydown', { key: 'Escape', keyCode: 229, bubbles: true, cancelable: true });
    searchInput.element().dispatchEvent(composingEsc);
    await expect.element(searchInput).toBeVisible();

    // Escape from outside the input (Cancel button focused) collapses search and returns focus to Search tracks trigger.
    (page.getByRole('button', { name: 'Cancel', exact: true }).element() as HTMLButtonElement).focus();
    await userEvent.keyboard('{Escape}');
    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    await expect.poll(() => document.activeElement).toBe(page.getByRole('button', { name: 'Search tracks', exact: true }).element());

    // Tapping a Focus card navigates straight into that Track.
    await page.getByRole('button', { name: /Mobile layout polish/ }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/track/t-ask');
  });

  it('persists scoped Mark all read receipts without clearing out-of-scope tracks', async () => {
    await page.viewport(390, 844);
    const { now, receipts } = renderFocusFixture({
      manualUnreadTrackId: 't-pinned',
      askTrackUnreadSessionId: 'ses-ask',
    });

    await expect.element(page.getByRole('region', { name: 'Unread updates', exact: true })).toBeVisible();

    // Scope to Product via inline search + Area select, then click Mark all read.
    await page.getByRole('button', { name: 'Search tracks', exact: true }).click();
    await page.getByRole('combobox', { name: 'Filter by area', exact: true }).selectOptions('a-prod');
    await page.getByRole('button', { name: 'Mark all read', exact: true }).click();

    await expect.poll(() => receipts.posts().length).toBe(3);
    const posted = receipts.posts().map((request) => ({
      path: request.path,
      body: request.body,
    }));
    expect(posted).toEqual(expect.arrayContaining([
      { path: '/api/v1/read-receipts/conversation/ses-ask/read', body: { readThroughMs: now - 120_000 } },
      { path: '/api/v1/read-receipts/conversation/ses-prod/read', body: { readThroughMs: now - 600_000 } },
      { path: '/api/v1/read-receipts/track/t-pinned/read', body: { readThroughMs: 0 } },
    ]));
    expect(posted.some((request) => request.path.includes('ses-wal'))).toBe(false);

    // Clearing Area scope proves Backend's unread track is still unread.
    await page.getByRole('combobox', { name: 'Filter by area', exact: true }).selectOptions('');
    await expect.element(page.getByRole('region', { name: 'Unread updates', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: /SQLite WAL checkpoint guard/ })).toBeVisible();
  });

  it('waits for pending overlays without flashing empty state and recovers from receipts load failure via Retry', async () => {
    await page.viewport(390, 844);
    const pending = renderFocusFixture({ holdOverlays: true });
    // Release only after confirming Loading status is shown and "All caught up" never flashes.
    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    expect(page.getByText('All caught up', { exact: true }).query()).toBeNull();
    pending.releaseOverlays();
    await expect.element(page.getByRole('region', { name: 'Unread updates', exact: true })).toBeVisible();

    cleanup();

    // Receipts load failure surfaces an ErrorBox with working Retry (not a false "All caught up").
    const failed = renderFocusFixture();
    failed.receipts.failReads(true);
    // Re-render fresh with failing read-receipts from mount.
    cleanup();
    const receipts = createReadReceiptsKernel({ baselineMs: Date.now() - 3_600_000 });
    receipts.failReads(true);
    renderDailyFixture({
      initial: '/',
      receipts,
      reply: (request) => {
        if (request.path === '/api/v1/areas') {
          return { status: 200, statusText: 'OK', body: [{ id: 'a-back', name: 'Backend', kind: 'user', color: '#2F9E44', sortOrder: 1, createdAtMs: 1, updatedAtMs: 1 }] };
        }
        if (request.path === '/api/v1/areas/a-back/tracks?includeClosed=true') {
          return { status: 200, statusText: 'OK', body: [fixtureTrack('t-only', { areaId: 'a-back', title: 'Pending receipt track', updatedAtMs: Date.now() - 60_000 })] };
        }
        if (request.path.startsWith('/api/v1/overlays?')) {
          return {
            status: 200,
            statusText: 'OK',
            body: {
              tracks: [{
                trackId: 't-only',
                areaId: 'a-back',
                isWorking: false,
                openAskCount: 0,
                pendingApprovalCount: 0,
                hasBlockedSession: false,
                unreadMailCount: 0,
                workspaceFailed: false,
                updatedAtMs: Date.now() - 60_000,
                notifiableSessions: [{ sessionId: 'ses-only', lastNotifiableTurnAtMs: Date.now() - 60_000 }],
              }],
            },
          };
        }
        return undefined;
      },
    });

    await expect.element(page.getByText('Read status is unavailable.', { exact: true })).toBeVisible();
    expect(page.getByText('All caught up', { exact: true }).query()).toBeNull();

    receipts.failReads(false);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect.poll(() => page.getByText('Read status is unavailable.', { exact: true }).query()).toBeNull();
    await expect.element(page.getByRole('button', { name: /Pending receipt track/ })).toBeVisible();
  });

  it('supports Workspace focus return, server-resolved Today navigation, dark theme, shareScope boundaries, and desktop resize round-trip', async () => {
    await page.viewport(390, 844);
    document.documentElement.setAttribute('data-theme', 'dark');
    const fixture = renderFocusFixture();

    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390);

    // Closing Workspace restores focus to Focus Home's Open workspace button.
    const workspaceButton = page.getByRole('button', { name: 'Open workspace', exact: true }).element() as HTMLButtonElement;
    await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
    await expect.element(page.getByRole('dialog', { name: 'Tracks and settings', exact: true })).toBeVisible();
    await userEvent.keyboard('{Escape}');
    await expect.poll(() => page.getByRole('dialog', { name: 'Tracks and settings', exact: true }).query()).toBeNull();
    await expect.poll(() => document.activeElement).toBe(workspaceButton);

    // Round-trip viewport resize: mobile -> desktop Today Assistant -> back to mobile Focus Home.
    await page.viewport(1280, 800);
    await expect.element(page.getByRole('region', { name: 'Today Assistant', exact: true })).toBeVisible();
    expect(document.querySelector('[data-nc-mobile-focus]')).toBeNull();

    await page.viewport(390, 844);
    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    expect(document.querySelector('[data-nc-workspace-header]')).toBeNull();

    // Workspace Level 1 -> Level 2 Area drill-down -> Back -> Library Today navigation.
    await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
    await expect.element(page.getByRole('heading', { name: 'Workspace', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: 'Today', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: 'Settings', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: /^Closed tracks/ })).toBeVisible();

    await page.getByRole('button', { name: 'Product', exact: true }).click();
    await expect.element(page.getByRole('heading', { name: 'Product', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: 'Back to Workspace', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: 'Show', exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Back to Workspace', exact: true }).click();
    await page.getByRole('button', { name: 'Today', exact: true }).click();
    await expect.poll(() => page.getByRole('dialog', { name: 'Tracks and settings', exact: true }).query()).toBeNull();
    await expect.poll(() => fixture.router.state.location.searchStr).toBe('?view=today');
    await expect.poll(() => document.querySelector('[data-nc-track-page]')).not.toBeNull();
    expect(fixture.requests.some((request) => request.path === '/api/v1/today/daily')).toBe(true);
    expect(fixture.requests.some((request) => request.path.startsWith('/api/v1/today/daily?date='))).toBe(false);
    expect(document.querySelector('[data-nc-mobile-focus]')).toBeNull();

    // Share-scoped sessions hide owner-only workspace creation/settings/Memory actions while permitting navigation into shared tracks.
    cleanup();
    renderFocusFixture({ shareScope: { trackId: 't-ask', token: 'share-token' } });
    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    expect(page.getByRole('button', { name: 'New track', exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'Mark all read', exact: true }).query()).toBeNull();
    await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
    expect(page.getByRole('button', { name: 'Settings', exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'Memory', exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'New area', exact: true }).query()).toBeNull();
  });
});
