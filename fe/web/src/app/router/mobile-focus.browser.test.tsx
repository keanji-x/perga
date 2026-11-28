import '../../styles/entry.css';
import { act, cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';

import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import { decodeEventFrame } from '../../../../core/events/protocol.ts';
import { THEME_KEY } from '../../../../core/keys/storage.ts';
import type { ReadReceipt } from '../../../../core/domain/read-receipts.ts';
import { EventStream, type EventStreamSink, type UnconfiguredEventStream } from '../../systems/events/event-stream.ts';
import { createReadReceiptsKernel } from '../providers/read-receipts-fixture.tsx';
import type { ThemeStorage } from '../theme/public.tsx';
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
  failedPreviewKind?: 'ask' | 'summary';
  askUsesSummary?: boolean;
  omitAskTimestamp?: boolean;
  productAreaName?: string;
  backendAreaName?: string;
  shareScope?: unknown;
  holdOverlays?: boolean;
  themeStorage?: ThemeStorage;
  stream?: UnconfiguredEventStream;
}>;

function renderFocusFixture(options: FocusFixtureOptions = {}) {
  const now = Date.now();
  const askTimestamp = options.omitAskTimestamp ? 0 : now - 120_000;
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
    name: options.productAreaName ?? 'Product',
    kind: 'user',
    color: '#5B8DEF',
    sortOrder: 1,
    createdAtMs: now - 86_400_000,
    updatedAtMs: now - 120_000,
  };
  const backendArea = {
    id: 'a-back',
    name: options.backendAreaName ?? 'Backend',
    kind: 'user',
    color: '#2F9E44',
    sortOrder: 2,
    createdAtMs: now - 86_400_000,
    updatedAtMs: now - 1_080_000,
  };
  const askTrack = fixtureTrack('t-ask', {
    areaId: 'a-prod',
    title: 'Mobile layout polish',
    updatedAtMs: askTimestamp,
  });
  const failedTrack = fixtureTrack('t-failed', {
    areaId: 'a-prod',
    title: 'Workspace setup',
    updatedAtMs: now - 180_000,
  });
  const unreadProdTrack = fixtureTrack('t-unread-prod', {
    areaId: 'a-prod',
    title: 'Design system review ' + 'with a very long title '.repeat(6),
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
    failedTrack,
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

  const pinnedOverlay = {
    trackId: 't-pinned',
    previewText: 'The roadmap is ready for review.',
    previewKind: 'summary',
    areaId: 'a-prod',
    isWorking: false,
    openAskCount: 0,
    pendingApprovalCount: 0,
    hasBlockedSession: false,
    unreadMailCount: 0,
    workspaceFailed: false,
    updatedAtMs: now - 7_200_000,
    notifiableSessions: [],
  };
  const overlaysBody = {
    tracks: [
      {
        trackId: 't-failed',
        areaId: 'a-prod',
        workspaceFailed: true,
        openAskCount: options.failedPreviewKind === 'ask' ? 1 : 0,
        updatedAtMs: now - 180_000,
        previewText: options.failedPreviewKind === 'ask' ? 'Try mounting again?'
          : options.failedPreviewKind === 'summary' ? 'The setup report is available.' : 'Mount timed out.',
        previewKind: options.failedPreviewKind ?? 'error',
      },
      {
        trackId: 't-ask',
        previewText: options.askUsesSummary ? 'The review report is available.' : 'Approve **the mobile layout**?',
        previewKind: options.askUsesSummary ? 'summary' : 'ask',
        areaId: 'a-prod',
        isWorking: false,
        openAskCount: options.askUsesSummary ? 0 : 1,
        pendingApprovalCount: options.askUsesSummary ? 1 : 0,
        hasBlockedSession: false,
        unreadMailCount: 0,
        workspaceFailed: false,
        updatedAtMs: askTimestamp,
        notifiableSessions: options.askTrackUnreadSessionId === undefined
          ? []
          : [{ sessionId: options.askTrackUnreadSessionId, lastNotifiableTurnAtMs: now - 120_000 }],
      },
      {
        trackId: 't-unread-prod',
        previewText: 'Reviewed **spacing** and [contrast](/report). ' + 'Long preview content. '.repeat(10),
        previewKind: 'reply',
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
      pinnedOverlay,
      {
        trackId: 't-closed',
        previewText: 'Previous layout archived.',
        previewKind: 'summary',
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
        previewText: 'Checking checkpoint timing.',
        previewKind: 'reply',
        areaId: 'a-back',
        isWorking: true,
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
    themeStorage: options.themeStorage,
    stream: options.stream,
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
    updatePinnedSummary: (summary: string) => { pinnedOverlay.previewText = summary; },
  };
}

function resolvedColor(value: string): string {
  const probe = document.createElement('span');
  probe.hidden = true;
  probe.style.color = value;
  document.body.append(probe);
  const color = getComputedStyle(probe).color;
  probe.remove();
  return color;
}

function contrastRatio(ink: string, ...backgrounds: string[]): number {
  const canvas = document.createElement('canvas');
  const context = canvas.getContext('2d', { willReadFrequently: true })!;
  const paint = (color: string) => {
    context.fillStyle = color;
    context.fillRect(0, 0, 1, 1);
  };
  const luminance = () => {
    const [red, green, blue] = Array.from(context.getImageData(0, 0, 1, 1).data, (value) => {
      const channel = value / 255;
      return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
  };
  backgrounds.forEach(paint);
  const background = luminance();
  paint(ink);
  const foreground = luminance();
  return (Math.max(background, foreground) + 0.05) / (Math.min(background, foreground) + 0.05);
}

describe('MobileFocusHome', () => {
  it.each([
    [390, 844],
    [320, 640],
  ] as const)('renders full-bleed scrollable Focus feed with one header, zero overflow, and 44px touch targets at %ix%i', async (width, height) => {
    await page.viewport(width, height);
    renderFocusFixture({ extraQuietCount: 12 });

    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    const feed = page.getByRole('list', { name: 'Tracks', exact: true });
    await expect.element(feed).toBeVisible();
    const rows = feed.getByRole('button').elements().filter((button) => button.parentElement?.localName === 'li');
    expect(rows).toHaveLength(17);
    const failed = feed.getByRole('button', { name: /^Workspace setup.*Needs repair Mount timed out\./ });
    const ask = feed.getByRole('button', { name: /^Mobile layout polish.*Waiting on you: Approve the mobile layout\?/ });
    const reply = feed.getByRole('button', { name: /^Design system review.*Reply: Reviewed spacing and contrast\..* Unread$/ });
    const running = feed.getByRole('button', { name: /^SQLite WAL checkpoint guard.*Running · Reply: Checking checkpoint timing\./ });
    const summary = feed.getByRole('button', { name: /^Q4 Mobile Roadmap.*Pinned · Summary: The roadmap is ready for review\./ });
    const pinned = feed.getByRole('button', { name: /^Overflow track 1\b.*Pinned$/ });
    expect(rows.slice(0, 6)).toEqual([failed, ask, reply, running, summary, pinned].map(row => row.element()));
    expect(page.getByRole('heading').elements()).toHaveLength(1);
    expect(feed.element().querySelector('section, footer, li > button svg')).toBeNull();
    expect(getComputedStyle(feed.element()).borderRadius).toBe('0px');
    expect(getComputedStyle(failed.element(), '::before').content).toBe('none');
    expect(getComputedStyle(failed.element()).borderLeftWidth).toBe('0px');
    expect(getComputedStyle(ask.element().closest('li')!, '::before').height).toBe('1px');
    const failureLabel = failed.getByText('Needs repair', { exact: true }).element();
    expect(page.getByRole('button', { name: 'Mark all read', exact: true }).query()).toBeNull();

    const firstTitle = failed.getByText('Workspace setup', { exact: true }).element();
    const firstPreview = failureLabel.parentElement!;
    const longTitle = reply.getByText(/^Design system review/).element().parentElement!;
    const longPreview = reply.getByText('Reply:', { exact: true }).element().parentElement!;
    const askItem = page.elementLocator(ask.element().closest('li')!);
    const area = askItem.getByRole('button', { name: 'Open area Product' }).element();
    const time = askItem.getByText('2m', { exact: true }).element();
    const askPreview = ask.getByText('Waiting on you:', { exact: true }).element().parentElement!;
    const areaBox = area.getBoundingClientRect();
    const timeBox = time.getBoundingClientRect();
    const askPreviewBox = askPreview.getBoundingClientRect();
    expect(ask.element()).not.toContainElement(area);
    expect(areaBox.height).toBeGreaterThanOrEqual(44);
    expect(areaBox.top).toBeGreaterThan(askPreviewBox.bottom);
    expect(timeBox.left).toBe(askPreviewBox.left);
    expect(timeBox.right).toBeLessThan(areaBox.left);
    expect(areaBox.right).toBe(askPreviewBox.right);
    expect(area.querySelector('svg')!.getBoundingClientRect().right).toBe(areaBox.right);
    expect(getComputedStyle(time).color).toBe(resolvedColor('var(--text-3)'));
    expect(getComputedStyle(area).color).toBe(resolvedColor('var(--text-2)'));
    expect(getComputedStyle(area).backgroundColor).toBe('rgba(0, 0, 0, 0)');
    expect(firstTitle.getBoundingClientRect().left).toBe(firstPreview.getBoundingClientRect().left);
    expect(parseFloat(getComputedStyle(firstTitle).fontSize)).toBeGreaterThan(parseFloat(getComputedStyle(firstPreview).fontSize));
    const titleStyle = getComputedStyle(longTitle);
    expect(titleStyle.textOverflow).toBe('ellipsis');
    expect(titleStyle.whiteSpace).toBe('nowrap');
    expect(longTitle.getBoundingClientRect().height).toBeCloseTo(parseFloat(titleStyle.lineHeight), 1);
    expect(longTitle.scrollWidth).toBeGreaterThan(longTitle.clientWidth);
    const previewStyle = getComputedStyle(longPreview);
    expect(previewStyle.getPropertyValue('-webkit-line-clamp')).toBe('2');
    expect(parseFloat(previewStyle.lineHeight) / parseFloat(previewStyle.fontSize)).toBeCloseTo(1.6, 1);
    expect(longPreview.clientHeight).toBeCloseTo(parseFloat(previewStyle.lineHeight) * 2, 0);
    expect(getComputedStyle(reply.element()).paddingBlock).toBe('20px 18px');
    expect(getComputedStyle(reply.element()).rowGap).toBe('8px');
    expect(longPreview.scrollHeight).toBeGreaterThan(longPreview.clientHeight);
    const replyArea = page.elementLocator(reply.element().closest('li')!).getByRole('button', { name: 'Open area Product' }).element();
    expect(replyArea.getBoundingClientRect().top).toBeGreaterThan(longPreview.getBoundingClientRect().bottom);
    for (const row of rows) {
      expect(row.scrollWidth).toBeLessThanOrEqual(row.clientWidth);
      expect(row.getBoundingClientRect().left).toBe(0);
      expect(row.getBoundingClientRect().right).toBe(width);
    }

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
    const contentLeft = firstTitle.getBoundingClientRect().left;
    const filterBar = page.getByRole('navigation', { name: 'Focus filters', exact: true }).element();
    expect(feed.element().getBoundingClientRect().top).toBe(filterBar.getBoundingClientRect().bottom);
    expect(getComputedStyle(filterBar).backgroundColor).toBe(getComputedStyle(headers[0]).backgroundColor);
    expect(getComputedStyle(feed.element()).backgroundColor).toBe(getComputedStyle(filterBar).backgroundColor);
    expect(Math.abs((menuGlyph.top + menuGlyph.height / 2) - (title.top + title.height / 2))).toBeLessThanOrEqual(1);
    expect(Math.abs(menuGlyph.left - contentLeft)).toBeLessThanOrEqual(1);
    expect(feedScroll!.scrollHeight).toBeGreaterThan(feedScroll!.clientHeight);
    feedScroll!.scrollTop = feedScroll!.scrollHeight;
    await expect.element(page.getByRole('button', { name: /Overflow track 12/ })).toBeInViewport();
    feedScroll!.scrollTop = 0;

    // Header actions, filter tabs, and the unread utility action meet the 44px touch height floor.
    for (const label of ['Open workspace', 'Search tracks', 'New track'] as const) {
      const box = page.getByRole('button', { name: label, exact: true }).element().getBoundingClientRect();
      expect(box.width).toBeGreaterThanOrEqual(44);
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
    for (const tab of page.getByRole('navigation', { name: 'Focus filters', exact: true }).getByRole('button').elements()) {
      expect(tab.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    }

    const unread = page.getByRole('button', { name: /^Unread/ });
    await unread.click();
    await expect.element(unread).toHaveAttribute('aria-pressed', 'true');
    expect(getComputedStyle(unread.element(), '::after').backgroundColor).toBe(resolvedColor('var(--text)'));
    expect(getComputedStyle(page.getByRole('button', { name: /^Focus\b/ }).element(), '::after').content).toBe('none');
    await userEvent.tab();
    const attention = page.getByRole('button', { name: /^Attention/ }).element();
    expect(document.activeElement).toBe(attention);
    expect(getComputedStyle(attention).outlineColor).toBe(resolvedColor('var(--accent)'));
    expect(getComputedStyle(attention).outlineStyle).toBe('solid');
    await expect.element(page.getByText('2 unread', { exact: true })).toBeVisible();
    const markRead = page.getByRole('button', { name: 'Mark all read', exact: true }).element();
    expect(markRead.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    expect(getComputedStyle(markRead.parentElement!).borderBottomWidth).toBe('1px');
    expect(getComputedStyle(markRead.parentElement!).backgroundColor).toBe(getComputedStyle(feed.element()).backgroundColor);
    expect(feed.element().getBoundingClientRect().bottom).toBe(feedScroll!.getBoundingClientRect().bottom);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
  });

  it.each([
    ['light', 390, 844], ['light', 320, 640], ['dark', 390, 844], ['dark', 320, 640],
  ] as const)('keeps inline warnings and the repair capsule readable in %s at %ix%i', async (theme, width, height) => {
    await page.viewport(width, height);
    renderFocusFixture({
      failedPreviewKind: 'ask',
      themeStorage: { getItem: key => key === THEME_KEY ? theme : null, setItem: () => undefined },
    });
    const heading = page.getByRole('heading', { name: 'Focus', exact: true });
    const feed = page.getByRole('list', { name: 'Tracks', exact: true });
    const failed = feed.getByRole('button', { name: /^Workspace setup.*Needs repair Waiting on you: Try mounting again\?/ });
    const ask = feed.getByRole('button', { name: /^Mobile layout polish.*Waiting on you: Approve the mobile layout\?/ });
    await expect.element(failed).toBeVisible();
    await expect.poll(() => document.documentElement.dataset.theme).toBe(theme);
    await heading.hover();

    const failedPill = getComputedStyle(failed.getByText('Needs repair', { exact: true }).element());
    const askLabelElement = ask.getByText('Waiting on you:', { exact: true }).element();
    const askLabel = getComputedStyle(askLabelElement);
    const failedAsk = failed.getByText('Waiting on you:', { exact: true }).element();
    const failedAskLabel = getComputedStyle(failedAsk);
    const failedInk = getComputedStyle(failedAsk.parentElement!).color;
    const surface = getComputedStyle(feed.element()).backgroundColor;
    const radius = getComputedStyle(feed.element()).getPropertyValue('--radius-pill').trim();
    const filters = page.getByRole('navigation', { name: 'Focus filters' }).getByRole('button').elements();
    expect(filters).toHaveLength(5);
    for (const tab of filters) {
      const style = getComputedStyle(tab);
      const indicator = getComputedStyle(tab, '::after');
      expect(style.borderRadius).toBe('0px');
      expect(style.borderBottomWidth).toBe('0px');
      expect(style.backgroundColor).toBe(resolvedColor('transparent'));
      if (tab.getAttribute('aria-pressed') === 'true') {
        expect(indicator.content).toBe('""');
        expect(indicator.bottom).toBe('0px');
        expect(indicator.height).toBe(style.getPropertyValue('--rule-h').trim());
        expect(indicator.backgroundColor).toBe(resolvedColor('var(--text)'));
        expect(parseFloat(indicator.width) / tab.getBoundingClientRect().width).toBeCloseTo(0.6, 2);
      } else {
        expect(indicator.content).toBe('none');
      }
    }
    expect(failedPill.borderRadius).toBe(radius);
    expect(failedPill.backgroundColor).toBe(resolvedColor('var(--chip-error)'));
    expect(failedPill.color).toBe(resolvedColor('var(--chip-on-error)'));
    for (const style of [askLabel, failedAskLabel]) {
      expect(style.color).toBe(resolvedColor('var(--warn-text)'));
      expect(style.fontWeight).toBe(style.getPropertyValue('--weight-semibold').trim());
      expect(style.display).toBe('inline');
      expect(style.backgroundColor).toBe(resolvedColor('transparent'));
      expect(style.borderTopWidth).toBe('0px');
    }
    expect(failedInk).toBe(resolvedColor('var(--text-2)'));
    expect(getComputedStyle(failed.getByText('Workspace setup', { exact: true }).element()).color).toBe(resolvedColor('var(--text)'));
    expect(getComputedStyle(failed.element().closest('li')!).backgroundColor).toBe(resolvedColor('var(--error-soft)'));
    expect(surface).toBe(resolvedColor('var(--bg)'));
    expect(getComputedStyle(filters[0].parentElement!).backgroundColor).toBe(surface);
    expect(getComputedStyle(heading.element().closest('header')!).backgroundColor).toBe(surface);
    const divider = getComputedStyle(ask.element().closest('li')!, '::before');
    expect([divider.left, divider.right, divider.height]).toEqual(['0px', '0px', '1px']);
    expect(divider.backgroundColor).toBe(resolvedColor('var(--error-border)'));

    for (const { row, title, backgrounds, checkContrast } of [
      {
        row: ask, title: 'Mobile layout polish',
        backgrounds: ['transparent', 'var(--overlay-hover)', 'var(--overlay-active)'],
        checkContrast: (fill: string) => {
          expect(contrastRatio(askLabel.color, surface, fill)).toBeGreaterThanOrEqual(4.5);
          expect(contrastRatio(getComputedStyle(askLabelElement.parentElement!).color, surface, fill)).toBeGreaterThanOrEqual(4.5);
        },
      },
      {
        row: failed, title: 'Workspace setup',
        backgrounds: ['var(--error-soft)', 'color-mix(in srgb, var(--error-soft) 80%, var(--overlay-hover))',
          'color-mix(in srgb, var(--error-soft) 65%, var(--overlay-active))'],
        checkContrast: (fill: string) => {
          expect(contrastRatio(failedInk, surface, fill)).toBeGreaterThanOrEqual(4.5);
          expect(contrastRatio(failedPill.color, surface, fill, failedPill.backgroundColor)).toBeGreaterThanOrEqual(4.5);
          expect(contrastRatio(failedAskLabel.color, surface, fill)).toBeGreaterThanOrEqual(4.5);
        },
      },
    ]) {
      const verify = (background: string) => {
        const surfaceElement = row === failed ? row.element().closest('li')! : row.element();
        const fill = getComputedStyle(surfaceElement).backgroundColor;
        expect(fill).toBe(resolvedColor(background));
        checkContrast(fill);
      };
      verify(backgrounds[0]);
      await row.hover();
      verify(backgrounds[1]);
      try {
        await commands.mouseButton(`[data-nc-focus-feed] button:has-text("${title}")`, true);
        verify(backgrounds[2]);
      } finally {
        await heading.hover();
        await commands.mouseButton('', false);
      }
    }
  });

  it('labels unread summaries and quiet collaborator replies by their content', async () => {
    await page.viewport(390, 844);
    renderFocusFixture({
      manualUnreadTrackId: 't-pinned',
      initialReceipts: [{ kind: 'conversation', targetId: 'ses-prod', readThroughMs: Date.now(), manualUnread: false }],
    });

    await page.getByRole('button', { name: /^Unread/ }).click();
    const unreadSummary = page.getByRole('button', { name: /^Q4 Mobile Roadmap.*Summary: The roadmap is ready for review\. Unread$/ });
    await expect.element(unreadSummary).toBeVisible();
    expect(getComputedStyle(unreadSummary.getByText('Unread', { exact: true }).element()).clipPath).toBe('inset(50%)');
    await page.getByRole('button', { name: 'All', exact: true }).click();
    const readReply = page.getByRole('button', { name: /^Design system review.*Reply: Reviewed spacing/ });
    await expect.element(readReply).toBeVisible();
    expect(readReply.getByText('Unread', { exact: true }).query()).toBeNull();
  });

  it.each([
    ['ask', /Waiting on you: Try mounting again\?/],
    ['summary', /Summary: The setup report is available\./],
  ] as const)('keeps an unread track’s activity distinct from its %s preview', async (failedPreviewKind, preview) => {
    await page.viewport(390, 844);
    renderFocusFixture({
      failedPreviewKind,
      askUsesSummary: true,
      manualUnreadTrackId: 't-failed',
      askTrackUnreadSessionId: 'ses-ask',
    });

    const rows = [
      page.getByRole('button', { name: new RegExp(`^Workspace setup.*Needs repair ${preview.source} Unread$`) }),
      page.getByRole('button', { name: /^Mobile layout polish.*Waiting on you · Summary: The review report is available\. Unread$/ }),
      page.getByRole('button', { name: /^SQLite WAL checkpoint guard.*Running · Reply: Checking checkpoint timing\. Unread$/ }),
    ];
    for (const row of rows) {
      await expect.element(row).toBeVisible();
      const label = row.getByText('Unread', { exact: true }).element();
      expect(getComputedStyle(label).clipPath).toBe('inset(50%)');
      expect(getComputedStyle(label).position).toBe('absolute');
    }
  });

  it('refreshes the mounted summary preview on report.updated', async () => {
    await page.viewport(390, 844);
    let sink: EventStreamSink | undefined;
    const stream = EventStream.create('ws://fixture.invalid', {
      start: (_config, _url, active) => { sink = active; },
      stop: () => { sink = undefined; },
    });
    const { now, updatePinnedSummary } = renderFocusFixture({ stream });
    await expect.element(page.getByRole('button', { name: /^Q4 Mobile Roadmap.*Summary: The roadmap/ })).toBeVisible();

    const summary = 'The roadmap is approved.';
    updatePinnedSummary(summary);
    const decoded = decodeEventFrame({
      kind: 'report.updated', areaId: 'a-prod', trackId: 't-pinned', eventId: 1, eventVersion: 1,
      createdAtMs: now, payload: { docRev: 2, summary },
    });
    if (decoded.status !== 'ready' || sink === undefined) throw new Error('Event stream was not ready');
    act(() => sink?.frame(decoded.frame));
    await expect.element(page.getByRole('button', { name: /^Q4 Mobile Roadmap.*Summary: The roadmap is approved/ })).toBeVisible();
  });

  it('keeps the Focus header, filters, and feed inside landscape safe areas', async () => {
    const width = 844;
    const leftInset = 47;
    const rightInset = 23;
    await page.viewport(width, 390);
    try {
      await commands.setHorizontalSafeArea(leftInset, rightInset);
      renderFocusFixture();
      const menu = await page.getByRole('button', { name: 'Open workspace', exact: true }).findElement();
      const filters = page.getByRole('navigation', { name: 'Focus filters' });
      const focus = await filters.getByRole('button', { name: /^Focus\b/ }).findElement();
      const row = page.getByRole('button', { name: /Mobile layout polish/ });
      await row.findElement();
      const create = await page.getByRole('button', { name: 'New track', exact: true }).findElement();

      const glyph = menu.querySelector('svg')!.getBoundingClientRect();
      const rowTitle = row.getByText('Mobile layout polish', { exact: true }).element().getBoundingClientRect();
      const item = page.elementLocator(row.element().closest('li')!);
      const rowTime = item.getByText('2m', { exact: true }).element().getBoundingClientRect();
      const area = item.getByRole('button', { name: 'Open area Product' }).element().getBoundingClientRect();
      for (const box of [glyph, focus.getBoundingClientRect(), rowTitle, rowTime, area, create.getBoundingClientRect()]) {
        expect(box.left).toBeGreaterThanOrEqual(leftInset);
        expect(box.right).toBeLessThanOrEqual(width - rightInset);
      }
      expect(Math.abs(glyph.left - rowTitle.left)).toBeLessThanOrEqual(1);
      expect(rowTime.left).toBe(rowTitle.left);
      expect(rowTitle.left - leftInset).toBeCloseTo(width - rightInset - area.right, 0);
      const reply = await page.getByRole('button', { name: /^Design system review/ }).findElement();
      const divider = getComputedStyle(reply.closest('li')!, '::before');
      expect(parseFloat(divider.left)).toBeCloseTo(rowTitle.left, 0);
      expect(parseFloat(divider.right)).toBeCloseTo(width - area.right, 0);

      await page.getByRole('button', { name: /^Unread/ }).click();
      const markRead = await page.getByRole('button', { name: 'Mark all read', exact: true }).findElement();
      expect(markRead.getBoundingClientRect().right).toBeCloseTo(area.right, 0);
      expect(page.getByText('2 unread', { exact: true }).element().getBoundingClientRect().left).toBeCloseTo(rowTitle.left, 0);
      await page.getByRole('button', { name: 'Search tracks', exact: true }).click();
      await page.getByRole('combobox', { name: 'Filter by area', exact: true }).selectOptions('a-prod');
      const reset = await page.getByRole('button', { name: 'Reset', exact: true }).findElement();
      expect(reset.getBoundingClientRect().right).toBeCloseTo(area.right, 0);
      expect(page.getByText('Found 1 track in Product').element().getBoundingClientRect().left).toBeCloseTo(rowTitle.left, 0);
    } finally {
      await commands.setHorizontalSafeArea(0, 0);
    }
  });

  it.each([320, 390])('fits a long Area name beside a timestamp and keeps untimed Area links at inline-end at %ipx', async (width) => {
    await page.viewport(width, 844);
    const productAreaName = 'I';
    const backendAreaName = 'perga 开发 ' + 'Mobile workspace and design systems '.repeat(5).trimEnd();
    renderFocusFixture({ productAreaName, backendAreaName, omitAskTimestamp: true });
    const untimed = await page.getByRole('button', { name: 'Mobile layout polish Waiting on you: Approve the mobile layout?', exact: true }).findElement();
    const timed = await page.getByRole('button', { name: /^SQLite WAL checkpoint guard\b.*Reply:/ }).findElement();
    const untimedItem = page.elementLocator(untimed.closest('li')!);
    const timedItem = page.elementLocator(timed.closest('li')!);
    const untimedArea = untimedItem.getByRole('button', { name: `Open area ${productAreaName}`, exact: true }).element();
    const timedArea = timedItem.getByRole('button', { name: `Open area ${backendAreaName}`, exact: true }).element();
    const time = timedItem.getByText('18m', { exact: true }).element();
    const label = timedItem.getByText(backendAreaName, { exact: true }).element();
    const preview = page.elementLocator(timed).getByText('Reply:', { exact: true }).element().parentElement!;

    expect(untimedArea.parentElement?.textContent).toBe(productAreaName);
    expect(untimedArea.getBoundingClientRect()).toMatchObject({ width: 44, height: 44, right: width - 16 });
    expect(untimedArea.querySelector('svg')!.getBoundingClientRect().right).toBe(width - 16);
    expect(timedArea.getBoundingClientRect().right).toBe(width - 16);
    expect(timedArea.getBoundingClientRect().top).toBeGreaterThan(preview.getBoundingClientRect().bottom);
    expect(time.getBoundingClientRect().left).toBe(16);
    expect(time.getBoundingClientRect().right).toBeLessThan(timedArea.getBoundingClientRect().left);
    expect(label.scrollWidth).toBeGreaterThan(label.clientWidth);
    expect(getComputedStyle(label).textOverflow).toBe('ellipsis');
    expect(document.documentElement.scrollWidth).toBe(width);
  });

  it.each([320, 390])('jumps from a Focus row directly to its Area and restores the opener at %ipx', async (width) => {
    await page.viewport(width, 844);
    const { router } = renderFocusFixture();
    const row = await page.getByRole('button', { name: /^Mobile layout polish\b/ }).findElement();
    const item = row.closest('li')!;
    const area = page.elementLocator(item).getByRole('button', { name: 'Open area Product' });
    const opener = await area.findElement();
    const chevron = opener.querySelector('svg')!;
    expect(getComputedStyle(chevron).transform).toBe('none');
    item.dir = 'rtl';
    expect(getComputedStyle(chevron).transform).toBe('matrix(-1, 0, 0, 1, 0, 0)');
    expect(opener.getBoundingClientRect().left).toBe(16);
    expect(chevron.getBoundingClientRect().left).toBe(16);
    expect(page.elementLocator(item).getByText('2m', { exact: true }).element().getBoundingClientRect().right).toBe(width - 16);
    item.removeAttribute('dir');
    await area.click();
    const dialog = page.getByRole('dialog', { name: 'Tracks and settings' });
    await expect.element(dialog.getByRole('heading', { name: 'Product', exact: true })).toBeVisible();
    await expect.element(dialog.getByRole('region', { name: 'Open tracks' })).toBeVisible();
    expect(dialog.getByRole('heading', { name: 'Workspace' }).query()).toBeNull();
    expect(router.state.location.pathname).toBe('/');
    await dialog.getByRole('button', { name: 'Back to Focus', exact: true }).click();
    await expect.element(area).toHaveFocus();
    expect(area.element()).toBe(opener);
    expect(router.state.location.pathname).toBe('/');
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
    await expect.element(page.getByRole('button', { name: /Archive cleanup notes.*No updates yet/ })).toBeVisible();
    expect(page.getByRole('list', { name: 'Tracks', exact: true }).element().querySelector('mark')?.textContent).toBe('Archive');
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
    const archived = page.getByRole('button', { name: /Old mobile shell notes.*Closed · Summary: Previous layout archived/ });
    await expect.element(archived).toBeVisible();
    const highlightedTitle = archived.getByText('Old mobile', { exact: true }).element();
    expect(highlightedTitle.tagName).toBe('MARK');
    expect(getComputedStyle(highlightedTitle.parentElement!).textDecorationLine).toBe('line-through');

    await searchInput.fill('product');
    const area = page.getByRole('combobox', { name: 'Filter by area', exact: true });
    await area.selectOptions('a-prod');
    await expect.element(page.getByText('Found 5 tracks in Product')).toBeVisible();
    expect(getComputedStyle(searchInput.element().closest('header')!).backgroundColor).toBe(resolvedColor('var(--bg)'));
    expect(page.getByRole('list', { name: 'Tracks', exact: true }).element().querySelector('mark')?.textContent).toBe('Product');
    await page.getByRole('button', { name: 'Reset', exact: true }).click();
    expect((searchInput.element() as HTMLInputElement).value).toBe('');
    expect((area.element() as HTMLSelectElement).value).toBe('');

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

    await expect.element(page.getByRole('button', { name: /Design system review/ })).toBeVisible();
    expect(page.getByRole('button', { name: /Mobile layout polish/ }).elements()).toHaveLength(1);
    expect(page.getByRole('button', { name: 'Mark all read', exact: true }).query()).toBeNull();

    // Scope to Product via inline search + Area select, then click Mark all read.
    await page.getByRole('button', { name: 'Search tracks', exact: true }).click();
    await page.getByRole('combobox', { name: 'Filter by area', exact: true }).selectOptions('a-prod');
    await page.getByRole('button', { name: /^Unread/ }).click();
    await expect.element(page.getByText('3 unread', { exact: true })).toBeVisible();
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
    await expect.element(page.getByRole('list', { name: 'Tracks', exact: true })).toBeVisible();
    await expect.element(page.getByRole('button', { name: /SQLite WAL checkpoint guard/ })).toBeVisible();
  });

  it('waits for pending overlays without flashing empty state and recovers from receipts load failure via Retry', async () => {
    await page.viewport(390, 844);
    const pending = renderFocusFixture({ holdOverlays: true });
    // Release only after confirming Loading status is shown and "All caught up" never flashes.
    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    expect(page.getByText('All caught up', { exact: true }).query()).toBeNull();
    pending.releaseOverlays();
    await expect.element(page.getByRole('list', { name: 'Tracks', exact: true })).toBeVisible();

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
    const persistedTheme = new Map<string, string>([[THEME_KEY, 'dark']]);
    const fixture = renderFocusFixture({
      themeStorage: {
        getItem: (key) => persistedTheme.get(key) ?? null,
        setItem: (key, value) => { persistedTheme.set(key, value); },
      },
    });

    await expect.element(page.getByRole('heading', { name: 'Focus', exact: true })).toBeVisible();
    await expect.poll(() => document.documentElement.dataset.theme).toBe('dark');
    const feed = await page.getByRole('list', { name: 'Tracks', exact: true }).findElement();
    expect(feed.getBoundingClientRect().bottom).toBe(document.querySelector('[data-nc-focus-feed]')!.getBoundingClientRect().bottom);
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
    await page.getByRole('button', { name: /^Unread/ }).click();
    await expect.element(page.getByText('2 unread', { exact: true })).toBeVisible();
    expect(page.getByRole('button', { name: 'Mark all read', exact: true }).query()).toBeNull();
    await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
    expect(page.getByRole('button', { name: 'Settings', exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'Memory', exact: true }).query()).toBeNull();
    expect(page.getByRole('button', { name: 'New area', exact: true }).query()).toBeNull();
  });
});
