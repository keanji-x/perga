import '../../styles/entry.css';
import { cleanup, render } from '@testing-library/react';
import { page, userEvent } from 'vitest/browser';
import { afterEach, expect, it, vi } from 'vitest';

import type { Area } from '../../../../core/domain/area.ts';
import { NEUTRAL_ACTIVITY, type Track } from '../../../../core/domain/track.ts';
import { useState } from '../../ui/state/public.ts';
import { ThemeProvider } from '../theme/public.tsx';
import styles from './shell.module.css';
import { SidebarFixture as Sidebar } from './sidebar-fixture.tsx';
import { readReceiptsFixture } from '../providers/read-receipts-fixture.tsx';

const receipts = readReceiptsFixture();

afterEach(() => { cleanup(); delete document.documentElement.dataset.theme; });

const VIEWPORT_H = 480;

const AREAS: readonly Area[] = Object.freeze(Array.from({ length: 40 }, (_, index) => ({
  id: `a${index}`, name: `Area ${index}`, color: '#5B8DEF', sort: index, kind: 'user' as const,
  defaultTemplateId: null, defaultCwd: null, createdAt: 1, updatedAt: 1,
})));

const LAST_TRACK: Track = Object.freeze({
  id: 't39', areaId: 'a39', title: 'Deep track', sort: 1, cwd: '/tmp', isolation: 'none', agentCwd: '/tmp',
  pinnedAt: null, closedAt: null, createdAt: 0, updatedAt: 0,
  ...NEUTRAL_ACTIVITY,
});

type RailOptions = {
  theme?: 'light' | 'dark'; collapsed?: boolean; mainLayer?: boolean; currentPath?: string; areaCount?: number;
};

function Rail({ theme, collapsed: initiallyCollapsed, mainLayer, currentPath, areaCount, onSetupAccount }:
  Required<RailOptions> & { onSetupAccount?: () => void }) {
  const [collapsed, setCollapsed] = useState(initiallyCollapsed);
  const areas = AREAS.slice(0, areaCount);
  const tracks = areas.some((area) => area.id === LAST_TRACK.areaId) ? [LAST_TRACK] : [];
  return (
    <ThemeProvider storage={{ getItem: () => theme, setItem: () => undefined }}>
      {/* The production shell grid, so the rail has its real width in both
          modes and `.main` its real column beside it. The block size stands in
          for the app's `#root` height chain. */}
      <div
        className={`${styles.shell} ${collapsed ? styles.shellCollapsed : styles.shellExpanded}`}
        style={{ blockSize: '100dvh' }}
      >
        <Sidebar
          areas={areas}
          tracksByArea={new Map(areas.map((area) => [area.id, tracks.filter((track) => track.areaId === area.id)]))}
          tracks={tracks}
          currentPath={currentPath}
          onGo={vi.fn()}
          onRequestCreateArea={vi.fn()}
          onRequestEditArea={vi.fn()}
          onDeleteArea={vi.fn()}
          onNewTrack={vi.fn()}
          onSetPinned={vi.fn()}
          onDeleteTrack={vi.fn()}
          onOpenSettings={vi.fn()}
          onOpenPlugins={vi.fn()}
          onSignOut={onSetupAccount === undefined ? vi.fn() : undefined}
          onSetupAccount={onSetupAccount}
          collapsed={collapsed}
          onToggleCollapsed={() => setCollapsed((value) => !value)}
          userLabel={onSetupAccount === undefined ? 'Kenji Xie' : 'Local'}
        />
        {/* Stands in for `.main`'s content: its column, at the highest rank
            that content uses in place (the sticky headers at `--z-sticky`). */}
        {mainLayer
          ? <div data-testid="main-layer" style={{ position: 'relative', zIndex: 'var(--z-sticky)' }} />
          : <div />}
      </div>
    </ThemeProvider>
  );
}

async function renderRail({
  theme = 'light', collapsed = false, mainLayer = false, currentPath = '/', areaCount = AREAS.length,
}: RailOptions = {}) {
  await page.viewport(1400, VIEWPORT_H);
  render(receipts.wrap(<Rail theme={theme} collapsed={collapsed} mainLayer={mainLayer} currentPath={currentPath} areaCount={areaCount} />));
  await new Promise(requestAnimationFrame);
  return railParts();
}

function railParts() {
  const rail = document.querySelector<HTMLElement>('nav[aria-label="Workspace"]')!;
  const avatar = document.querySelector<HTMLElement>('[aria-label="Account menu for Kenji Xie"]')!;
  let userRow = avatar;
  while (userRow.parentElement !== rail) userRow = userRow.parentElement!;
  const folders = rail.querySelector<HTMLElement>('[aria-label^="area "]')?.parentElement
    ?? rail.querySelector<HTMLElement>('[aria-label^="Show area "]')!.parentElement!;
  return { rail, avatar, userRow, folders };
}

async function scrollRail(rail: HTMLElement, top: number) {
  rail.scrollTop = top;
  await new Promise(requestAnimationFrame);
}

/** The whole element sits above the pinned account row, not partly behind it. */
function expectAboveAccountRow(element: HTMLElement) {
  const { userRow } = railParts();
  const name = element.getAttribute('aria-label') ?? element.textContent;
  expect(element.getBoundingClientRect().bottom, `${name} reaches under the account row`)
    .toBeLessThanOrEqual(userRow.getBoundingClientRect().top + 1);
}

/** The element's centre is the topmost thing there: nothing covers it. */
function expectUncovered(element: HTMLElement) {
  const box = element.getBoundingClientRect();
  const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
  const name = element.getAttribute('aria-label') ?? element.textContent;
  expect(element.contains(hit), `${name} is covered by ${hit?.outerHTML.slice(0, 80)}`).toBe(true);
}

it('keeps the account avatar fixed with the row fill reaching the rail bottom at every folder scroll', async () => {
  const { rail, avatar, userRow, folders } = await renderRail();
  expect(folders.scrollHeight).toBeGreaterThan(folders.clientHeight);
  // Before the row was pinned, the avatar sat one rail bottom padding above
  // the rail's bottom edge. Pinning must not move it.
  const railPadding = parseFloat(getComputedStyle(rail).paddingBlockEnd);
  expect(railPadding).toBeGreaterThan(0);
  const settings = document.querySelector<HTMLElement>('[aria-label="Open settings"]')!;
  const initialAvatarOffset = rail.getBoundingClientRect().bottom - avatar.getBoundingClientRect().bottom;

  for (const top of [0, Math.round((folders.scrollHeight - folders.clientHeight) / 2), folders.scrollHeight]) {
    await scrollRail(folders, top);
    const railBottom = rail.getBoundingClientRect().bottom;
    // Within a pixel: the rail's top inset is fractional, so layout snaps.
    expect(Math.abs(railBottom - settings.getBoundingClientRect().bottom - railPadding)).toBeLessThanOrEqual(1);
    expect(Math.abs((railBottom - avatar.getBoundingClientRect().bottom) - initialAvatarOffset)).toBeLessThanOrEqual(1);
    expect(Math.abs(userRow.getBoundingClientRect().bottom - railBottom)).toBeLessThanOrEqual(1);
    expectUncovered(avatar);
  }
});

it('clips scrolling folder rows and their action buttons above the account row', async () => {
  const { rail, userRow, folders } = await renderRail();
  await scrollRail(folders, Math.round((folders.scrollHeight - folders.clientHeight) / 2));
  expect(folders.scrollTop).toBeGreaterThan(0);
  const row = userRow.getBoundingClientRect();
  const railBox = rail.getBoundingClientRect();
  expect(folders.getBoundingClientRect().bottom).toBeLessThanOrEqual(row.top);
  // Offscreen rows still exist below the scrollport, but neither their content
  // nor their raised actions may paint or receive clicks over the account.
  const below = [...folders.querySelectorAll<HTMLElement>('[aria-label^="Area actions for"]')]
    .filter(button => button.getBoundingClientRect().top >= row.top);
  expect(below.length).toBeGreaterThan(0);
  const xs = [railBox.left + 60, ...below.map(button => button.getBoundingClientRect().left + 4)];
  for (let y = row.top + 1; y < railBox.bottom - 1; y += 2) {
    for (const x of xs) {
      expect(userRow.contains(document.elementFromPoint(x, y)), `(${x}, ${y})`).toBe(true);
    }
  }
  expect(getComputedStyle(userRow).backgroundColor).toBe(getComputedStyle(rail).backgroundColor);
});

it('opens the account menu clear of the avatar and above the main region when expanded and hides footer chrome when collapsed', async () => {
  const { rail, avatar } = await renderRail({ collapsed: false, mainLayer: true });
  const main = document.querySelector<HTMLElement>('[data-testid="main-layer"]')!;
  expect(main.getBoundingClientRect().left).toBeCloseTo(rail.getBoundingClientRect().right, 0);

  await userEvent.click(avatar);
  const items = page.getByRole('menuitem').elements() as HTMLElement[];
  expect(items.map((item) => item.textContent)).toEqual(['Settings', 'Plugins', 'Sign out']);
  const menu = items[0]?.closest<HTMLElement>('[role="menu"]');
  if (!menu) throw new Error('account menu did not open');
  // Within a pixel of snapping; an avatar pushed up under the menu overlaps by a whole rail padding.
  expect(menu.getBoundingClientRect().bottom).toBeLessThanOrEqual(avatar.getBoundingClientRect().top + 1);
  expectUncovered(avatar);
  const railRight = rail.getBoundingClientRect().right;
  for (const item of items) {
    expectUncovered(item);
    const box = item.getBoundingClientRect();
    if (box.right > railRight + 8) {
      expect(item.contains(document.elementFromPoint(box.right - 4, box.top + box.height / 2))).toBe(true);
    }
  }

  cleanup();
  document.body.replaceChildren();
  render(receipts.wrap(<Rail theme="light" collapsed mainLayer currentPath="/area/a0" areaCount={AREAS.length} />));
  await new Promise(requestAnimationFrame);
  expect(document.querySelector('[aria-label="Account menu for Kenji Xie"]')).toBeNull();
  const expand = document.querySelector<HTMLElement>('[aria-label="Expand sidebar"]')!;
  const expandBox = expand.getBoundingClientRect();
  expect(expandBox.right).toBeLessThanOrEqual(44);
  expect(getComputedStyle(expand.parentElement!).backgroundColor).toBe('rgba(0, 0, 0, 0)');
  expectUncovered(expand);
});

it('keeps expanded Area rows compact at rest instead of reserving two-action padding', async () => {
  await renderRail({ areaCount: 2 });
  const areaButton = document.querySelector<HTMLButtonElement>('[aria-label="Collapse area Area 0"]')!;
  expect(areaButton.getAttribute('aria-expanded')).toBe('true');
  const restPadding = parseFloat(getComputedStyle(areaButton).paddingInlineEnd);
  expect(restPadding).toBeLessThan(24);
});

it('leaves the Areas list where it is when focus lands on the pinned avatar', async () => {
  const { folders, avatar } = await renderRail();
  const middle = Math.round((folders.scrollHeight - folders.clientHeight) / 2);

  await scrollRail(folders, middle);
  avatar.focus();
  await new Promise(requestAnimationFrame);
  expect(folders.scrollTop).toBe(middle);

  // Opening the menu from the keyboard and closing it hands focus back.
  await userEvent.keyboard('{Enter}');
  expect(page.getByRole('menuitem').elements()).toHaveLength(3);
  await userEvent.keyboard('{Escape}');
  await new Promise(requestAnimationFrame);
  expect(document.activeElement).toBe(avatar);
  expect(folders.scrollTop).toBe(middle);
});

it('parks the account row at the bottom when the Areas list is short', async () => {
  const { rail, avatar } = await renderRail({ areaCount: 2 });
  expect(rail.scrollHeight).toBe(rail.clientHeight);
  const railPadding = parseFloat(getComputedStyle(rail).paddingBlockEnd);
  const settings = document.querySelector<HTMLElement>('[aria-label="Open settings"]')!;
  expect(Math.abs(rail.getBoundingClientRect().bottom - settings.getBoundingClientRect().bottom - railPadding))
    .toBeLessThanOrEqual(1);
  expect(Math.abs((avatar.getBoundingClientRect().top + avatar.getBoundingClientRect().bottom) / 2
    - (settings.getBoundingClientRect().top + settings.getBoundingClientRect().bottom) / 2)).toBeLessThanOrEqual(1);
});

it('reveals the current track above the account row, not behind it', async () => {
  await renderRail({ currentPath: `/track/${LAST_TRACK.id}` });
  const current = document.querySelector<HTMLElement>('[aria-current="page"]')!;
  expect(current.textContent).toContain(LAST_TRACK.title);
  expectUncovered(current);
  expectAboveAccountRow(current);
});

it('keeps every control reached by Tab clear of the account row', async () => {
  const { avatar } = await renderRail();
  document.querySelector<HTMLElement>('[title="Today"]')!.closest('button')!.focus();
  let checked = 0;
  for (let step = 0; step < 400 && document.activeElement !== avatar; step += 1) {
    await userEvent.tab();
    await new Promise(requestAnimationFrame);
    const focused = document.activeElement as HTMLElement;
    if (focused !== avatar) { expectUncovered(focused); expectAboveAccountRow(focused); checked += 1; }
  }
  expect(document.activeElement).toBe(avatar);
  expect(checked).toBeGreaterThan(AREAS.length);
});

it('collapses down to the single expand icon without rendering the account row', async () => {
  await page.viewport(1400, VIEWPORT_H);
  render(receipts.wrap(<Rail theme="light" collapsed mainLayer={false} currentPath="/" areaCount={AREAS.length} />));
  await new Promise(requestAnimationFrame);
  expect(document.querySelector('[aria-label="Account menu for Kenji Xie"]')).toBeNull();
  await userEvent.click(page.getByRole('button', { name: 'Expand sidebar' }));
  await new Promise(requestAnimationFrame);
  const { avatar } = railParts();
  expect(avatar).toBeTruthy();
});

it.each(['light', 'dark'] as const)('paints the avatar with the card fill, not the rail fill (%s)', async (theme) => {
  const { rail, avatar } = await renderRail({ theme });
  expect(document.documentElement.dataset.theme).toBe(theme);
  const probe = document.createElement('div');
  document.body.append(probe);
  probe.style.background = getComputedStyle(document.documentElement).getPropertyValue('--surface-card');
  const cardFill = getComputedStyle(probe).backgroundColor;
  probe.remove();

  expect(getComputedStyle(avatar).backgroundColor).toBe(cardFill);
  expect(getComputedStyle(avatar).backgroundColor).not.toBe(getComputedStyle(rail).backgroundColor);
});


it('reveals the recovery menu when hiding an Area from deeply scrolled folders', async () => {
  const { rail, folders } = await renderRail();
  await page.getByRole('button', { name: 'Area actions for Area 39' }).click();
  expect(folders.scrollTop).toBeGreaterThan(0);
  await page.getByRole('menuitem', { name: 'Hide group', exact: true }).click();
  const options = document.querySelector<HTMLElement>('[aria-label="Sidebar view options"]')!;
  expect(document.activeElement).toBe(options);
  expect(options.getBoundingClientRect().top).toBeGreaterThanOrEqual(rail.getBoundingClientRect().top);
  expectUncovered(options);
});


it('offers account setup from the Local menu', async () => {
  const setup = vi.fn();
  await page.viewport(1400, VIEWPORT_H);
  render(receipts.wrap(<Rail theme="light" collapsed={false} mainLayer={false} currentPath="/" areaCount={1} onSetupAccount={setup} />));
  await page.getByRole('button', { name: 'Account menu for Local' }).click();
  expect(page.getByRole('menuitem', { name: 'Sign out' }).elements()).toHaveLength(0);
  await page.getByRole('menuitem', { name: 'Set up account & password…' }).click();
  expect(setup).toHaveBeenCalledOnce();
});
