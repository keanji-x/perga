import { activityLabelOf } from '../../../../../core/domain/activity.ts';
import { useTrackClosedControl } from './closed-control.ts';
import { page as browserPage, userEvent } from 'vitest/browser';
import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import '../../../styles/entry.css';

import { useState } from '../../../ui/state/public.ts';
import { TrackPage } from './public.tsx';
import { renderPage, track } from './test-fixtures.tsx';

afterEach(() => {
  cleanup(); document.body.replaceChildren();
  delete document.documentElement.dataset.theme;
});

type Rgb = readonly [number, number, number];

function paintedRgb(cssColor: string): Rgb {
  const canvas = document.createElement('canvas');
  canvas.width = 1;
  canvas.height = 1;
  const context = canvas.getContext('2d', { willReadFrequently: true });
  if (context === null) throw new Error('no 2d canvas context');
  context.fillStyle = cssColor;
  context.fillRect(0, 0, 1, 1);
  const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
  return [r, g, b];
}

function contrast(first: Rgb, second: Rgb): number {
  const channel = (byte: number) => {
    const value = byte / 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  const luminance = ([r, g, b]: Rgb) =>
    0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  const [high, low] = [luminance(first), luminance(second)].sort((a, b) => b - a);
  return (high + 0.05) / (low + 0.05);
}

it('keeps Related readable without a decorative heading icon and retains its source link', async () => {
  await browserPage.viewport(1280, 900);
  const follow = vi.fn();
  renderPage({ recentFiles: <a href="#source" onClick={event => { event.preventDefault(); follow(); }}>Source report</a> });
  const heading = browserPage.getByText('Related', { exact: true });
  await expect.element(heading).toBeVisible();
  expect(heading.element().querySelector('svg')).toBeNull();
  await browserPage.getByRole('button', { name: 'Related', exact: true }).click();
  const link = browserPage.getByRole('link', { name: 'Source report', exact: true });
  await expect.element(link).toBeVisible();
  expect(link.element().getAttribute('href')).toBe('#source');
  await link.click();
  expect(follow).toHaveBeenCalledTimes(1);
});

describe('Track actions menu text geometry', () => {
  function expectContained(inner: DOMRect, outer: DOMRect, context: string) {
    // Fractional font/anchor geometry may differ by a subpixel; clipping a glyph must fail.
    expect.soft(inner.left, `${context}: left`).toBeGreaterThanOrEqual(outer.left - 0.5);
    expect.soft(inner.right, `${context}: right`).toBeLessThanOrEqual(outer.right + 0.5);
    expect.soft(inner.top, `${context}: top`).toBeGreaterThanOrEqual(outer.top - 0.5);
    expect.soft(inner.bottom, `${context}: bottom`).toBeLessThanOrEqual(outer.bottom + 0.5);
  }

  function expectReadableLabel(menu: HTMLElement, text: string) {
    const item = browserPage.getByRole('menuitem', { name: text, exact: true }).element();
    expect(menu.contains(item)).toBe(true);
    // Locate the element containing the actual text node, not the item's accessible name
    // or its wider wrapper. Astryx can expose the full name even when its label is ellipsized.
    const walker = document.createTreeWalker(item, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node !== null && node.textContent !== text) node = walker.nextNode();
    if (node?.parentElement === null || node === null) throw new Error(`Missing label text: ${text}`);
    const label = node.parentElement;
    const labelBox = label.getBoundingClientRect();
    const range = document.createRange();
    range.selectNode(node);
    const textBox = range.getBoundingClientRect();
    const style = getComputedStyle(label);

    expect.soft(labelBox.width, `${text}: label width`).toBeGreaterThan(0);
    expect.soft(textBox.width, `${text}: text width`).toBeGreaterThan(0);
    expect.soft(textBox.height, `${text}: text height`).toBeGreaterThan(0);
    expect.soft(style.visibility, `${text}: visibility`).toBe('visible');
    expect.soft(Number(style.opacity), `${text}: opacity`).toBeGreaterThan(0);
    // text-overflow: ellipsis is allowed only when it is inactive: all text must fit.
    expect.soft(label.scrollWidth, `${text}: no overflow/ellipsis (${style.textOverflow})`)
      .toBeLessThanOrEqual(label.clientWidth);
    expectContained(textBox, labelBox, `${text}: Range inside label`);
    expectContained(textBox, item.getBoundingClientRect(), `${text}: Range inside item`);
    expectContained(textBox, menu.getBoundingClientRect(), `${text}: Range inside menu`);
  }

  for (const width of [1200, 1000, 390]) {
    for (const closed of [false, true]) {
      it(`shows complete ${closed ? 'Reopen' : 'Close'}/Delete labels at the right edge at ${width}px with a long title`, async () => {
        await browserPage.viewport(width, 844);
        renderPage({
          track: track({ title: 'A deliberately long track title '.repeat(12), closedAt: closed ? 5 : null }),
          canReopenTrack: closed,
          canCloseTrack: !closed,
        });
        // 390px exercises TrackPage's production MobileHeader / MoreMenu path.
        const actions = browserPage.getByRole('button', {
          name: width === 390 ? 'Track actions' : /^Track actions for /,
          exact: true,
        });
        await expect.element(actions).toBeVisible();
        const actionBox = actions.element().getBoundingClientRect();
        expect(window.innerWidth - actionBox.right).toBeGreaterThanOrEqual(0);
        expect(window.innerWidth - actionBox.right).toBeLessThanOrEqual(64);
        await userEvent.click(actions);
        const menuLocator = browserPage.getByRole('menu');
        await expect.element(menuLocator).toBeVisible();
        await document.fonts.ready;
        await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
        const menu = menuLocator.element() as HTMLElement;
        expect.soft(menu.getBoundingClientRect().width).toBeGreaterThan(0);
        expect.soft(menu.getBoundingClientRect().height).toBeGreaterThan(0);
        expectContained(menu.getBoundingClientRect(), new DOMRect(0, 0, window.innerWidth, window.innerHeight), 'menu inside viewport');
        expect(browserPage.getByRole('menuitem', { name: closed ? 'Close' : 'Reopen', exact: true }).query()).toBeNull();
        if (width !== 390) expect(menu.querySelectorAll('[role="menuitem"]')).toHaveLength(2);
        expectReadableLabel(menu, closed ? 'Reopen' : 'Close');
        expectReadableLabel(menu, 'Delete track');
      });
    }
  }
});

describe('the track closed status in the page header', () => {
  it('sits directly beside the title as quiet text', async () => {
    await browserPage.viewport(1200, 800);
    renderPage({ track: track({ title: 'Status preview', closedAt: 5 }) });

    const title = document.querySelector<HTMLElement>('[aria-label="Rename track"]')!;
    const status = document.querySelector<HTMLElement>('[aria-label="Track closed"]')!;
    const gap = status.getBoundingClientRect().left - title.getBoundingClientRect().right;

    expect(gap).toBeGreaterThanOrEqual(4);
    expect(gap).toBeLessThanOrEqual(12);
    expect(getComputedStyle(status).fontSize).toBe('12px');
    expect(title.scrollWidth).toBeLessThanOrEqual(title.clientWidth);
  });

  it('uses a transparent text-only treatment', async () => {
    await browserPage.viewport(1200, 800);
    renderPage({ track: track({ closedAt: 5 }), canReopenTrack: true });

    const status = document.querySelector<HTMLElement>('[aria-label="Track closed"]')!;
    const statusStyle = getComputedStyle(status);

    expect(status.getBoundingClientRect().height).toBe(24);
    expect(statusStyle.backgroundColor).toBe('rgba(0, 0, 0, 0)');
    expect(statusStyle.padding).toBe('0px');
    expect(statusStyle.borderTopWidth).toBe('0px');
  });

  it('keeps the three-dot action visible at the secondary text rank', async () => {
    await browserPage.viewport(1200, 800);
    renderPage();

    const actions = document.querySelector<HTMLElement>('[aria-label^="Track actions for "]')!;
    const text2 = getComputedStyle(document.documentElement).getPropertyValue('--text-2');
    expect(paintedRgb(getComputedStyle(actions).color)).toEqual(paintedRgb(text2));
  });

  it('returns focus to the Track actions button when Escape closes its menu', async () => {
    await browserPage.viewport(1200, 800);
    renderPage({ track: track({ closedAt: 5 }), canReopenTrack: true });
    const actions = document.querySelector<HTMLButtonElement>('[aria-label^="Track actions for "]')!;

    actions.click();
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    expect(document.querySelector<HTMLElement>('[role="menu"]')?.closest('[popover]')
      ?.matches(':popover-open')).toBe(true);

    await userEvent.keyboard('{Escape}');
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    expect(document.activeElement).toBe(
      document.querySelector('[aria-label^="Track actions for "]'),
    );
  });

  /* The server offers one of Close and Reopen; the harness flips `closedAt` and both capabilities together. */
  function ClosedStateHarness({ initiallyClosed }: { initiallyClosed: boolean }) {
    const [closed, setClosed] = useState(initiallyClosed);
    const closedControl = useTrackClosedControl({ trackId: 'w1', canReopen: closed, canClose: !closed,
      patch: value => { setClosed(value); return Promise.resolve(); } });
    return (
      <TrackPage
        mobilePanelObscured={false}
        track={track({ closedAt: closed ? 5 : null })}
        cards={[]}
        tasks={[]}
        openableCards={new Set()}
        canReopenTrack={closed}
        canCloseTrack={!closed}
        onRenameTrack={vi.fn()}
        closedControl={closedControl}
        onDeleteTrack={vi.fn()} onTrackDeleted={vi.fn()} existingCardIds={new Set<string>()}
      />
    );
  }

  /** Opens Track actions from the keyboard and activates its first item, which is the Close or Reopen action. */
  async function activateFirstTrackAction(): Promise<HTMLButtonElement> {
    const actions = document.querySelector<HTMLButtonElement>('[aria-label^="Track actions for "]')!;
    actions.focus();
    await userEvent.keyboard('{Enter}');
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    expect(document.activeElement?.getAttribute('role')).toBe('menuitem');

    await userEvent.keyboard('{Enter}');
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    return document.querySelector<HTMLButtonElement>('[aria-label^="Track actions for "]')!;
  }

  it('keeps keyboard focus on Track actions after Reopen removes the action', async () => {
    await browserPage.viewport(1200, 800);
    render(<ClosedStateHarness initiallyClosed />);
    const currentActions = await activateFirstTrackAction();
    expect(document.activeElement).toBe(currentActions);
    expect(currentActions.matches(':focus-visible')).toBe(true);
    expect(document.querySelector('[aria-label="Track closed"]')).toBeNull();
  });

  it('keeps keyboard focus on Track actions after Close removes the action', async () => {
    await browserPage.viewport(1200, 800);
    render(<ClosedStateHarness initiallyClosed={false} />);
    expect(document.querySelector('[aria-label="Track closed"]')).toBeNull();
    const currentActions = await activateFirstTrackAction();
    expect(document.activeElement).toBe(currentActions);
    expect(currentActions.matches(':focus-visible')).toBe(true);
    expect(document.querySelector('[aria-label="Track closed"]')).not.toBeNull();
  });

  it('light-dismisses Track actions on an outside click', async () => {
    await browserPage.viewport(1200, 800);
    renderPage({ track: track({ closedAt: 5 }), canReopenTrack: true });
    const actions = document.querySelector<HTMLButtonElement>('[aria-label^="Track actions for "]')!;
    const popover = () => document.querySelector<HTMLElement>('[role="menu"]')?.closest<HTMLElement>('[popover]');

    actions.click();
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    expect(popover()?.matches(':popover-open')).toBe(true);
    await userEvent.click(document.body);
    expect(popover()?.matches(':popover-open')).toBe(false);
  });

  it('keeps the subdued closed colour readable in both themes', async () => {
    await browserPage.viewport(1200, 800);
    renderPage({ track: track({ closedAt: 5 }) });
    const status = document.querySelector<HTMLElement>('[aria-label="Track closed"]')!;

    for (const theme of ['light', 'dark'] as const) {
      document.documentElement.dataset.theme = theme;
      const foreground = paintedRgb(getComputedStyle(status).color);
      const background = paintedRgb(
        getComputedStyle(document.documentElement).getPropertyValue('--bg'),
      );
      expect(contrast(foreground, background), theme).toBeGreaterThanOrEqual(4.5);
    }
  });
});

describe('the report-to-Planner entry', () => {
  it('stays visible beside a long title and focuses the supplied Planner', async () => {
    await browserPage.viewport(1200, 800);
    const onReply = vi.fn();
    const start = vi.fn();
    renderPage({ track: track({ title: 'A long-running report '.repeat(20) }), onReply, onStartConversation: start });
    const button = browserPage.getByRole('button', { name: '讨论这项工作', exact: true }).element();
    const box = button.getBoundingClientRect();
    expect(box.width).toBeGreaterThan(100);
    expect(button.querySelector('svg')).not.toBeNull();
    expect(browserPage.getByRole('button', { name: 'Chat', exact: true }).query()).toBeNull();
    expect(box.right).toBeLessThanOrEqual(window.innerWidth);
    await userEvent.click(button);
    expect(onReply).toHaveBeenCalledOnce();
    expect(start).not.toHaveBeenCalled();
  });

  it('keeps failed-task details reachable while the list remains compact', async () => {
    await browserPage.viewport(1200, 800);
    const reason = 'Validation failed because the expected report snapshot is missing. '.repeat(4);
    renderPage({ tasks: [{ blockId: 'verify', key: 'verify', state: 'ready', declaration: null,
      status: 'failed', statusDetail: reason, kind: 'codex', workerCardId: null, pendingReason: null }] });
    const group = document.querySelector<HTMLElement>('[data-nc-work-inventory] section[aria-label="Progress"]')!;
    expect(group.querySelector('button[aria-expanded]')?.getAttribute('aria-expanded')).toBe('false');
    await userEvent.click(group.querySelector('button[aria-expanded]')!);
    const reveal = group.querySelector<HTMLElement>('[data-nc-row-action="reveal-block"]')!;
    expect(reveal.getAttribute('aria-description')).toContain('Validation failed');
    const metadata = group.querySelector<HTMLElement>('[data-nc-inventory-metadata]')!;
    expect(metadata.getBoundingClientRect().width).toBeLessThanOrEqual(1);
    const row = group.querySelector<HTMLElement>('[data-nc-row]')!;
    const rowBox = row.getBoundingClientRect();
    expect(row.textContent).toContain('Failed');
    expect(rowBox.height).toBeLessThanOrEqual(32);
    expect(rowBox.right).toBeLessThanOrEqual(group.closest('aside')!.getBoundingClientRect().right);
    expect(reveal.getBoundingClientRect().right).toBeLessThanOrEqual(rowBox.right);
  });
});

describe('the Track conversation entry', () => {
  it('offers one visible Chat action on desktop and phone using the same callback', async () => {
    const start = vi.fn();
    await browserPage.viewport(1200, 800);
    renderPage({ onStartConversation: start });
    const chat = browserPage.getByRole('button', { name: '讨论这项工作', exact: true });
    await expect.element(chat).toBeVisible();
    await userEvent.hover(document.querySelector<HTMLElement>('[data-nc-page-title]')!);
    const pixel = document.createElement('canvas');
    pixel.width = 1; pixel.height = 1;
    const context = pixel.getContext('2d')!;
    for (const theme of ['light', 'dark']) {
      document.documentElement.dataset.theme = theme;
      const button = [...document.querySelectorAll('button')].find((element) =>
        element.textContent?.includes('讨论这项工作') && element.getBoundingClientRect().width > 0);
      if (button === undefined) throw new Error('the desktop Chat action is missing');
      await waitFor(() => {
        const style = getComputedStyle(button);
        // This action paints its own opaque card surface above the page ground.
        context.clearRect(0, 0, 1, 1);
        context.fillStyle = style.backgroundColor;
        context.fillRect(0, 0, 1, 1);
        expect(context.getImageData(0, 0, 1, 1).data[3]).toBe(255);
        expect(contrast(paintedRgb(style.color), paintedRgb(style.backgroundColor))).toBeGreaterThanOrEqual(4.5);
      });
    }
    await userEvent.click(chat);
    expect(start).toHaveBeenCalledTimes(1);

    await browserPage.viewport(390, 844);
    const mobileChat = browserPage.getByRole('button', { name: 'Chat', exact: true });
    await expect.element(mobileChat).toBeVisible();
    await userEvent.click(mobileChat);
    expect(start).toHaveBeenCalledTimes(2);
  });
});

it('uses the generic navigation header contract without an app host', async () => {
  await browserPage.viewport(1200, 800);
  renderPage();
  const trackPage = document.querySelector<HTMLElement>('[data-nc-track-page]')!;
  const header = trackPage.querySelector<HTMLElement>(':scope > header')!;
  const reference = document.createElement('div');
  reference.style.position = 'absolute';
  reference.style.height = 'var(--navigation-header-height)';
  trackPage.append(reference);
  try {
    for (const height of ['52px', '64px']) {
      trackPage.style.setProperty('--navigation-header-height', height);
      expect(header.getBoundingClientRect().height).toBe(reference.getBoundingClientRect().height);
      expect(header.getBoundingClientRect().height).toBeGreaterThan(0);
    }
  } finally {
    reference.remove();
    trackPage.style.removeProperty('--navigation-header-height');
  }
});

it('previews the compact share arrow, menu entry and inline owner links on desktop', async () => {
  await browserPage.viewport(1280, 900);
  const onShare = vi.fn();
  renderPage({ onShareTrack: onShare, shareLinks: <section aria-label="分享链接">
    <h2>分享链接 · 1</h2><span>报告链接 · …abcdef</span>
  </section> });
  const trigger = browserPage.getByRole('button', { name: '分享', exact: true });
  await expect.element(trigger).toBeVisible();
  expect(trigger.element().textContent).toBe('');
  expect(trigger.element().querySelector('svg')).toBeTruthy();
  await trigger.click();
  expect(onShare).toHaveBeenCalledWith(expect.objectContaining({ kind: 'anchor' }));
  await browserPage.getByRole('button', { name: /^Track actions for / }).click();
  await browserPage.getByRole('menuitem', { name: 'Share track', exact: true }).click();
  expect(onShare).toHaveBeenCalledTimes(2);
  await expect.element(browserPage.getByText('报告链接 · …abcdef', { exact: true })).toBeVisible();
});

it('keeps owner sharing controls and the share-link group off the mobile surface', async () => {
  await browserPage.viewport(390, 844);
  renderPage({ onShareTrack: vi.fn(), shareLinks: <section aria-label="分享链接">Owner links</section> });
  await expect.element(browserPage.getByRole('button', { name: 'Track actions', exact: true })).toBeVisible();
  expect(document.querySelector('button[aria-label="分享"]')).toBeNull();
  expect(document.querySelector('section[aria-label="分享链接"]')).toBeNull();
  await browserPage.getByRole('button', { name: 'Track actions', exact: true }).click();
  expect(document.querySelector('[role="menu"]')?.textContent).not.toContain('Share track');
});

it.each(['failed', 'input', 'working', 'unread'] as const)('shows %s at the closed desktop chat entry without a floating panel', async state => {
  await browserPage.viewport(1200, 800);
  const onReply = vi.fn();
  renderPage({ track: track({ attention: state === 'failed' ? 'failed' : state === 'input' ? 'input' : 'none',
    working: state === 'working' }), unread: state === 'unread', onReply });
  const trigger = browserPage.getByRole('button', { name: '讨论这项工作' });
  await expect.element(trigger).toBeVisible();
  expect(trigger.element().querySelector('[data-nc-activity]')?.getAttribute('data-nc-activity'))
    .toBe(state === 'input' ? 'attention' : state);
  await expect.element(trigger).toHaveAccessibleDescription(activityLabelOf(state === 'input' ? 'attention' : state)!);
  expect(document.querySelector('[data-nc-notification-launcher]')).toBeNull();
  expect(document.querySelector('aside[aria-label="Notifications"]')).toBeNull();
  await trigger.click();
  expect(onReply).toHaveBeenCalledOnce();
});
