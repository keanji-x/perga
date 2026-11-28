import '../../styles/entry.css';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { commands, page } from 'vitest/browser';

import type { Area } from '../../../../core/domain/area.ts';
import type { Track } from '../../../../core/domain/track.ts';
import { track } from '../../features/track/page/test-fixtures.tsx';
import { AreaLevelTwo } from './mobile/area-level-two.tsx';
import { WorkspaceLevelOne } from './mobile/workspace-level-one.tsx';
import type { ComponentProps } from 'react';

const now = Date.UTC(2026, 10, 27, 12);
const product: Area = {
  id: 'c1', name: 'Product', kind: 'user', color: '#5B8DEF', sort: 1,
  defaultTemplateId: null, defaultCwd: null, createdAt: 0, updatedAt: 0,
};
const platform: Area = { ...product, id: 'c2', name: 'Platform', sort: 2 };
const longTitle = 'Workspace setup with a title too long for the available width on narrow phones';
const longPreview = 'The report describes the work completed, the decisions made, and the work that remains. '.repeat(3);

function fixture({ view = 'tracks', ...overrides }: Partial<ComponentProps<typeof AreaLevelTwo>> & { view?: 'tracks' | 'areas' } = {}) {
  const updatedAt = now - 120_000;
  const open = [
    track({ id: 'failed', title: longTitle, attention: 'failed', previewKind: 'error', previewText: 'Mount **timed out**.', updatedAt }),
    track({ id: 'ask', title: 'Release approval', attention: 'input', previewKind: 'ask', previewText: 'Approve **the release**?', updatedAt }),
    track({ id: 'running', title: 'Build verification', working: true, updatedAt }),
    track({ id: 'reply', title: 'Design review', previewKind: 'reply', previewText: 'Reviewed [the mockup](/mockup).', updatedAt }),
    track({ id: 'current', title: 'Current summary', pinnedAt: updatedAt, previewKind: 'summary', previewText: longPreview, updatedAt }),
    track({ id: 'quiet', title: ' ', updatedAt }),
  ];
  const closed = track({
    id: 'closed-product', title: 'Archived roadmap', closedAt: now - 300_000, updatedAt,
    previewKind: 'summary', previewText: longPreview,
  });
  const closedWithoutPreview = track({
    id: 'closed-platform', title: 'Archived prototype', areaId: platform.id,
    closedAt: now - 420_000, updatedAt,
  });
  const tracksByArea = new Map<string, readonly Track[]>([[product.id, [...open, closed]], [platform.id, [closedWithoutPreview]]]);
  const onOpenTrack = vi.fn();
  const onMarkUnread = vi.fn();
  const props = {
    areas: [product, platform], currentTrackId: 'current', tracksByArea,
    isUnread: (candidate: Track) => candidate.id === 'reply' || candidate.id === 'closed-product',
    readError: null, readLoading: false, onRetryRead: vi.fn(),
    backLabel: 'Focus', onBack: vi.fn(), scrollRef: null, onClosedExpandedChange: vi.fn(), onOpenTrack, nowMs: now,
    ...overrides,
  };
  render(<div style={{ height: '100dvh', display: 'flex', flexDirection: 'column' }}>
    {view === 'areas'
      ? <WorkspaceLevelOne {...props} onCreateArea={vi.fn()} onSelectArea={vi.fn()}
          onOpenSettings={vi.fn()} onOpenToday={vi.fn()} onOpenMemory={vi.fn()} accountLabel="Test account" />
      : <AreaLevelTwo areaId={product.id} onEditArea={vi.fn()} onNewTrack={vi.fn()} onOpenAreaMemory={vi.fn()}
          trackActions={() => ({ areaPinned: false, onSetPinned: vi.fn(), onSetAreaPinned: vi.fn(), onMarkUnread })} {...props} />}
  </div>);
  return { onOpenTrack, onMarkUnread };
}

function color(token: string): string {
  const probe = document.createElement('span');
  probe.hidden = true;
  probe.style.color = `var(${token})`;
  document.body.append(probe);
  const value = getComputedStyle(probe).color;
  probe.remove();
  return value;
}

function expectClamped(prefix: Element) {
  const preview = prefix.parentElement!;
  const style = getComputedStyle(preview);
  expect(style.getPropertyValue('-webkit-line-clamp')).toBe('2');
  expect(parseFloat(style.lineHeight) / parseFloat(style.fontSize)).toBeCloseTo(1.6, 1);
  expect(preview.clientHeight).toBeCloseTo(parseFloat(style.lineHeight) * 2, 0);
  expect(preview.scrollHeight).toBeGreaterThan(preview.clientHeight);
}

afterEach(async () => {
  cleanup();
  delete document.documentElement.dataset.theme;
  await page.viewport(1280, 800);
});

it.each([
  ['light', 390, 844], ['light', 320, 640], ['dark', 390, 844], ['dark', 320, 640],
] as const)('renders a flush Area feed with an independent repair-row menu in %s at %ix%i', async (theme, width, height) => {
  await page.viewport(width, height);
  document.documentElement.dataset.theme = theme;
  const { onOpenTrack, onMarkUnread } = fixture();

  const open = page.getByRole('region', { name: 'Open tracks', exact: true });
  const fullFeed = open.getByRole('list');
  const feed = await fullFeed.findElement();
  await expect.poll(() => feed.getBoundingClientRect().left).toBe(0);
  await page.getByRole('heading', { name: 'Product', exact: true }).hover();
  const feedBox = feed.getBoundingClientRect();
  expect([feedBox.left, feedBox.right]).toEqual([0, width]);
  expect(getComputedStyle(feed).backgroundColor).toBe(color('--bg'));
  expect(getComputedStyle(page.getByRole('heading', { name: 'Product', exact: true }).element().closest('header')!).backgroundColor).toBe(color('--bg'));
  expect(open.getByRole('heading').query()).toBeNull();
  const summaryStrip = (await page.getByText('6 open tracks · 1 unread', { exact: true }).findElement()).parentElement!;
  expect(getComputedStyle(summaryStrip).backgroundColor).toBe(color('--bg'));
  expect(getComputedStyle(summaryStrip).borderBottomWidth).toBe('1px');
  expect(summaryStrip.getBoundingClientRect().bottom).toBe(feedBox.top);
  await expect.element(page.getByRole('button', { name: 'Area memory', exact: true })).toBeVisible();

  const failed = fullFeed.getByRole('button', { name: /^Workspace setup\b/ });
  const failureTitle = failed.getByText(longTitle, { exact: true });
  const action = fullFeed.getByRole('button', { name: `Actions for track ${longTitle}`, exact: true });
  const caption = failed.getByText('Needs repair', { exact: true });
  const ask = fullFeed.getByRole('button', { name: /^Release approval\b/ });
  const running = fullFeed.getByRole('button', { name: /^Build verification\b/ });
  const reply = fullFeed.getByRole('button', { name: /^Design review\b/ });
  const current = fullFeed.getByRole('button', { name: /^Current summary\b/ });
  const quiet = fullFeed.getByRole('button', { name: /^Untitled track\b/ });

  const title = (await failureTitle.findElement()).parentElement!;
  for (const row of [failed, ask, running, reply, current, quiet]) {
    const button = row.element();
    expect(button.querySelector('svg, button, a')).toBeNull();
    expect(button.scrollWidth).toBeLessThanOrEqual(button.clientWidth);
    expect(button.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
  }
  expect(getComputedStyle(title).fontSize).toBe('16px');
  expect(getComputedStyle(title).textOverflow).toBe('ellipsis');
  expect(getComputedStyle(title).whiteSpace).toBe('nowrap');
  expect(title.getBoundingClientRect().height).toBeCloseTo(parseFloat(getComputedStyle(title).lineHeight), 1);
  expect(title.scrollWidth).toBeGreaterThan(title.clientWidth);
  const captionStyle = getComputedStyle(caption.element());
  expect(captionStyle.backgroundColor).toBe(color('--chip-error'));
  expect(captionStyle.color).toBe(color('--chip-on-error'));
  expect(captionStyle.borderRadius).toBe(captionStyle.getPropertyValue('--radius-pill').trim());
  const failurePreview = caption.element().parentElement!;
  expect(failurePreview.textContent).toContain('Mount timed out.');
  const time = page.elementLocator(failed.element().closest('li')!).getByText('2m', { exact: true });
  await expect.element(time).toBeVisible();
  expect(getComputedStyle(failurePreview).color).toBe(color('--text-2'));
  expect(title.getBoundingClientRect().left).toBe(failurePreview.getBoundingClientRect().left);

  const failureItem = failed.element().closest('li')!;
  const actionBox = action.element().getBoundingClientRect();
  expect(action.element().closest('li')).toBe(failureItem);
  expect(failed.element()).not.toContainElement(action.element());
  expect(actionBox.width).toBeGreaterThanOrEqual(44);
  expect(actionBox.height).toBeGreaterThanOrEqual(44);
  expect(actionBox.right).toBeLessThanOrEqual(width);
  const titleBox = title.getBoundingClientRect();
  const previewBox = failurePreview.getBoundingClientRect();
  const timeBox = time.element().getBoundingClientRect();
  expect(titleBox.right).toBe(previewBox.right);
  expect(titleBox.right).toBe(width - 16);
  expect(actionBox.top).toBeGreaterThan(previewBox.bottom);
  expect(timeBox.left).toBe(previewBox.left);
  expect(timeBox.right).toBeLessThan(actionBox.left);
  expect(timeBox.top + timeBox.height / 2).toBeCloseTo(actionBox.top + actionBox.height / 2, 0);
  expect(actionBox.right).toBe(titleBox.right);
  expect(getComputedStyle(failureItem).backgroundColor).toBe(color('--error-soft'));
  expect(getComputedStyle(action.element()).backgroundColor).toBe('rgba(0, 0, 0, 0)');
  const repairDivider = getComputedStyle(ask.element().closest('li')!, '::before');
  expect([repairDivider.left, repairDivider.right, repairDivider.height]).toEqual(['0px', '0px', '1px']);
  expect(repairDivider.backgroundColor).toBe(color('--error-border'));

  const warn = getComputedStyle(ask.getByText('Waiting on you:', { exact: true }).element());
  expect(warn.color).toBe(color('--warn-text'));
  expect(warn.backgroundColor).toBe('rgba(0, 0, 0, 0)');
  expect(warn.fontWeight).toBe(warn.getPropertyValue('--weight-semibold').trim());
  expect(getComputedStyle(running.getByText('Running', { exact: true }).element()).color).toBe(color('--success-text'));
  await expect.element(ask).toHaveTextContent('Waiting on you: Approve the release?');
  await expect.element(reply).toHaveTextContent('Reply: Reviewed the mockup.');
  expect(getComputedStyle(reply.getByText('Unread', { exact: true }).element()).clipPath).toBe('inset(50%)');
  expect(reply.getByText('Unread update', { exact: true }).query()).toBeNull();
  await expect.element(quiet.getByText('No updates yet', { exact: true })).toBeVisible();
  expect(current.element().getAttribute('aria-current')).toBe('page');
  expect(getComputedStyle(current.element().closest('li')!).backgroundColor).toBe(color('--selection-bg'));
  await expect.element(current.getByText('Pinned', { exact: true })).toBeVisible();
  await expect.element(current).toHaveTextContent('Pinned · Summary: The report describes');
  expectClamped(current.getByText('Summary:', { exact: true }).element());
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);

  await action.click();
  await page.getByRole('menuitem', { name: 'Mark as unread', exact: true }).click();
  expect(onMarkUnread).toHaveBeenCalledWith('failed');
  expect(onOpenTrack).not.toHaveBeenCalled();
  await failed.click();
  expect(onOpenTrack).toHaveBeenCalledExactlyOnceWith('failed');
});

it.each([
  [320, 640, 0, 0], [390, 844, 0, 0], [844, 390, 47, 23],
] as const)('splits Area and Workspace archive footers at %ix%i with safe areas of %i and %i', async (width, height, left, right) => {
  await page.viewport(width, height);
  await commands.setHorizontalSafeArea(left, right);
  try {
    fixture({ currentTrackId: 'closed-product' });
    const open = page.getByRole('region', { name: 'Open tracks', exact: true }).getByRole('list');
    const feed = await open.findElement();
    await expect.poll(() => feed.getBoundingClientRect().left).toBe(0);
    const failed = open.getByRole('button', { name: /^Workspace setup/ });
    const action = open.getByRole('button', { name: /^Actions for track Workspace setup/ });
    const title = await failed.getByText(longTitle, { exact: true }).findElement();
    const openTime = await page.elementLocator(failed.element().closest('li')!).getByText('2m', { exact: true }).findElement();
    const inset = title.getBoundingClientRect().left - left;
    expect(inset).toBe(16);
    expect(action.element().getBoundingClientRect().right).toBe(width - right - inset);
    expect(openTime.getBoundingClientRect().left).toBe(left + inset);
    expect(openTime.getBoundingClientRect().right).toBeLessThanOrEqual(action.element().getBoundingClientRect().left);
    const normalDivider = getComputedStyle(open.getByRole('button', { name: /^Build verification/ }).element().closest('li')!, '::before');
    expect([normalDivider.left, normalDivider.right]).toEqual([`${left + inset}px`, `${right + inset}px`]);

    const closed = page.getByRole('region', { name: 'Closed tracks', exact: true });
    const show = closed.getByRole('button', { name: 'Show', exact: true });
    expect((await show.findElement()).getAttribute('aria-expanded')).toBe('false');
    expect(closed.getByRole('list').query()).toBeNull();
    const toggleStrip = show.element().parentElement!;
    expect(toggleStrip.getBoundingClientRect().top).toBe(open.element().getBoundingClientRect().bottom);
    expect(getComputedStyle(toggleStrip).backgroundColor).toBe(color('--bg'));
    expect(getComputedStyle(toggleStrip).borderBlockWidth).toBe('1px');
    await show.click();
    const areaRow = closed.getByRole('button', { name: /^Archived roadmap\b/ });
    await expect.element(areaRow).toHaveAttribute('aria-current', 'page');
    await expect.element(areaRow).toHaveAccessibleName(/\bUnread$/);
    await expect.element(areaRow.getByText('Closed', { exact: true })).toBeVisible();
    await expect.element(areaRow.getByText('5m', { exact: true })).toBeVisible();
    const areaTitle = await areaRow.getByText('Archived roadmap', { exact: true }).findElement();
    expect(areaTitle.getBoundingClientRect().left).toBe(left + inset);
    expect(areaRow.getByText('5m', { exact: true }).element().getBoundingClientRect().left).toBe(left + inset);
    expect(getComputedStyle(areaTitle).textDecorationLine).toBe('line-through');
    expect(getComputedStyle(areaTitle).fontWeight).toBe(getComputedStyle(areaTitle).getPropertyValue('--weight-semibold').trim());
    expectClamped(areaRow.getByText('Summary:', { exact: true }).element());
    const hide = closed.getByRole('button', { name: 'Hide', exact: true });
    expect(hide.element().getAttribute('aria-expanded')).toBe('true');
    await hide.click();
    expect(closed.getByRole('list').query()).toBeNull();

    cleanup();
    const { onOpenTrack } = fixture({ view: 'areas', currentTrackId: 'closed-product' });
    const library = page.getByRole('region', { name: 'Library', exact: true });
    const closedNav = library.getByRole('button', { name: /^Closed tracks 2$/ });
    await closedNav.click();
    expect(closedNav.element().getAttribute('aria-expanded')).toBe('true');
    const workspaceRow = library.getByRole('button', { name: /^Archived roadmap\b/ });
    await expect.element(workspaceRow).toBeVisible();
    await expect.element(workspaceRow).toHaveAttribute('aria-current', 'page');
    await expect.element(workspaceRow).toHaveAccessibleName(/\bUnread$/);
    const card = workspaceRow.element().closest('ul')!;
    expect(getComputedStyle(card).backgroundColor).toBe(color('--surface-card'));
    const cardBox = card.getBoundingClientRect();
    expect([cardBox.left, width - cardBox.right]).toEqual([left + inset, right + inset]);
    const workspaceTitle = workspaceRow.getByText('Archived roadmap', { exact: true }).element();
    const workspaceBox = workspaceRow.element().getBoundingClientRect();
    const workspaceTime = workspaceRow.getByText('5m', { exact: true }).element().getBoundingClientRect();
    const workspaceArea = workspaceRow.getByText('Product', { exact: true }).element().getBoundingClientRect();
    expect(workspaceTitle.getBoundingClientRect().left - workspaceBox.left).toBe(inset);
    expect(workspaceTime.left).toBe(workspaceBox.left + inset);
    expect(workspaceArea.right).toBe(workspaceBox.right - inset);
    expect(workspaceTime.right).toBeLessThan(workspaceArea.left);
    expect(getComputedStyle(workspaceTitle).textDecorationLine).toBe('line-through');
    expectClamped(workspaceRow.getByText('Summary:', { exact: true }).element());
    const prototype = library.getByRole('button', { name: /^Archived prototype\b/ });
    await expect.element(prototype).toHaveTextContent(/Closed\s*7m\s*Platform/);
    expect(getComputedStyle(prototype.element().closest('li')!).backgroundColor).toBe('rgba(0, 0, 0, 0)');
    const prototypeTitle = getComputedStyle(prototype.getByText('Archived prototype', { exact: true }).element());
    expect(prototypeTitle.fontWeight).toBe(prototypeTitle.getPropertyValue('--weight-medium').trim());
    await workspaceRow.click();
    expect(onOpenTrack).toHaveBeenCalledExactlyOnceWith('closed-product');
  } finally {
    await commands.setHorizontalSafeArea(0, 0);
  }
});

it('extends the Area feed surface to the bottom when there is no archive toggle', async () => {
  await page.viewport(390, 844);
  fixture({ tracksByArea: new Map([[product.id, [track()]]]), trackActions: undefined });
  const open = page.getByRole('region', { name: 'Open tracks', exact: true }).getByRole('list');
  expect((await open.findElement()).getBoundingClientRect().bottom).toBe(844);
  expect(page.getByRole('region', { name: 'Closed tracks', exact: true }).query()).toBeNull();
});

it('truncates long Workspace archive Area labels and aligns an untimed label at 320px', async () => {
  await page.viewport(320, 640);
  const longArea = { ...product, name: 'perga 开发 ' + 'Mobile workspace and design systems '.repeat(5).trimEnd() };
  fixture({
    view: 'areas',
    areas: [longArea, platform],
    nowMs: new Date(2026, 10, 27, 12).getTime(),
    tracksByArea: new Map([
      [product.id, [track({ id: 'timed-archive', title: 'Timed archive', closedAt: new Date(2026, 10, 25, 12).getTime() })]],
      [platform.id, [track({ id: 'untimed-archive', title: 'Untimed archive', areaId: platform.id, closedAt: 0 })]],
    ]),
  });
  await page.getByRole('button', { name: /^Closed tracks 2$/ }).click();
  const timed = page.getByRole('button', { name: /^Timed archive\b/ });
  const untimed = page.getByRole('button', { name: 'Untimed archive Closed Platform', exact: true });
  const time = (await timed.getByText('Nov 25', { exact: true }).findElement()).getBoundingClientRect();
  const label = timed.getByText(longArea.name, { exact: true }).element();
  const labelBox = label.getBoundingClientRect();
  expect(time.left).toBe(timed.element().getBoundingClientRect().left + 16);
  expect(time.right).toBeLessThan(labelBox.left);
  expect(labelBox.right).toBe(timed.element().getBoundingClientRect().right - 16);
  expect(label.scrollWidth).toBeGreaterThan(label.clientWidth);
  expect(getComputedStyle(label).textOverflow).toBe('ellipsis');
  const untimedRow = await untimed.findElement();
  expect(untimed.getByText('Platform', { exact: true }).element().getBoundingClientRect().right).toBe(untimedRow.getBoundingClientRect().right - 16);
  expect(document.documentElement.scrollWidth).toBe(320);
});

it.each([320, 390])('keeps the menu below a two-line preview when there is no timestamp at %ipx', async (width) => {
  await page.viewport(width, 640);
  fixture({ tracksByArea: new Map([[product.id, [track({
    title: 'Untimed work', previewKind: 'summary', previewText: longPreview, updatedAt: 0, createdAt: 0,
  })]]]) });
  const row = await page.getByRole('button', { name: /^Untimed work Summary:/ }).findElement();
  const summary = page.elementLocator(row).getByText('Summary:', { exact: true }).element();
  const action = await page.getByRole('button', { name: 'Actions for track Untimed work' }).findElement();
  await expect.poll(() => row.getBoundingClientRect().left).toBe(0);
  expectClamped(summary);
  expect(action.getBoundingClientRect().top).toBeGreaterThan(summary.parentElement!.getBoundingClientRect().bottom);
  expect(action.getBoundingClientRect()).toMatchObject({ width: 44, height: 44, right: width - 16 });
  row.closest('li')!.dir = 'rtl';
  expect(action.getBoundingClientRect().left).toBe(16);
});
