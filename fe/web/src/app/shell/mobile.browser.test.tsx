import '../../styles/entry.css';
import { setup } from './mobile-browser-fixture.tsx';
import { withFontRoleProbe } from '../../../../tools/styles/font-role-fixture.ts';
/*
 * The mobile presentation, rendered on the real `AppShell` over a memory history;
 * this is the file that measures painted boxes.
 */
import { cleanup, fireEvent } from '@testing-library/react';
import { commands, page, userEvent } from 'vitest/browser';
import { afterEach, describe, expect, it, vi } from 'vitest';



afterEach(async () => { cleanup(); vi.restoreAllMocks(); await commands.emulateReducedMotion(false); });

const settlePaint = () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));

it('slides Workspace in and out on the left and pushes its Area in from the right', async () => {
  await page.viewport(390, 844);
  setup('/');
  const opener = await page.getByRole('button', { name: 'Open workspace' }).findElement();
  const host = document.getElementById('mobile-workspace-navigation')!;
  const animationFrames = (element: Element) => (element.getAnimations()[0].effect as KeyframeEffect).getKeyframes();
  const offset = (frame: ComputedKeyframe) => new DOMMatrix(String(frame.transform)).m41;

  fireEvent.click(opener);
  expect(offset(animationFrames(host)[0])).toBeCloseTo(-390);
  await host.getAnimations()[0].finished;

  const product = await page.getByRole('button', { name: 'Product', exact: true }).findElement();
  fireEvent.click(product);
  const area = host.querySelector('[data-nc-workspace-page="tracks"]')!;
  expect(offset(animationFrames(area)[0])).toBeCloseTo(390);
  await area.getAnimations()[0].finished;

  fireEvent.click(await page.getByRole('button', { name: 'Back to Workspace', exact: true }).findElement());
  await expectNoExitSnapBack(area, 'right');
  const close = await page.getByRole('button', { name: 'Back to workspace', exact: true }).findElement();
  fireEvent.click(close);
  expect(offset(animationFrames(host).at(-1)!)).toBeCloseTo(-390);
  await expectNoExitSnapBack(host, 'left');
});

async function expectNoExitSnapBack(element: Element, side: 'left' | 'right'): Promise<void> {
  const exit = element.getAnimations()[0];
  const cancel = exit.cancel.bind(exit);
  const visibleOnRelease: boolean[] = [];
  vi.spyOn(exit, 'cancel').mockImplementation(() => {
    cancel();
    const { left, right } = element.getBoundingClientRect();
    const offscreen = side === 'left' ? right <= 0 : left >= window.innerWidth;
    visibleOnRelease.push(getComputedStyle(element).visibility === 'visible' && !offscreen);
  });
  exit.finish();
  await expect.poll(() => getComputedStyle(element).visibility).toBe('hidden');
  expect(visibleOnRelease).toEqual([false]);
  expect(element.getAnimations()).toHaveLength(0);
}


async function openTrackNavigation(): Promise<void> {
  await expect.poll(() => (
    page.getByRole('button', { name: 'Open workspace' }).query() !== null
    || page.getByRole('button', { name: /^Switch track,/ }).query() !== null
    || page.getByRole('button', { name: 'Back to Tracks' }).query() !== null
  )).toBe(true);
  const workspace = page.getByRole('button', { name: 'Open workspace' });
  if (workspace.query() !== null) {
    await workspace.click();
    const navigation = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement();
    const current = navigation.querySelector('button[aria-current="location"]') ?? navigation.querySelector('button[aria-label="Product"]');
    if (!(current instanceof HTMLElement)) throw new Error('Expected the current Area');
    await page.elementLocator(current).click();
  } else {
    const switcher = page.getByRole('button', { name: /^Switch track,/ });
    if (switcher.query() !== null) {
      await switcher.click();
    } else {
      await page.getByRole('button', { name: 'Back to Tracks' }).click();
    }
  }
  const pane = document.querySelector('[data-nc-workspace-page="tracks"]')!;
  await Promise.all(pane.getAnimations().map((animation) => animation.finished));
  await settlePaint();
}

async function openSettings(): Promise<void> {
  const workspace = page.getByRole('button', { name: 'Open workspace', exact: true });
  if (workspace.query() !== null) {
    await workspace.click();
    await page.getByRole('button', { name: 'Settings', exact: true }).click();
    return;
  }
  // Route-scoped headers (Track Report / New Track) return to Focus or Tracks;
  // enter Settings via the desktop account menu and return to the compact viewport.
  const width = window.innerWidth; const height = window.innerHeight;
  await page.viewport(1400, 900);
  await page.getByRole('button', { name: /^Account menu for / }).click();
  await page.getByRole('menuitem', { name: 'Settings', exact: true }).click();
  await page.viewport(width, height);
}

/* Closing slides the panel out; the assertions after it read a box at rest. */
async function closePanel(): Promise<void> {
  const panel = document.querySelector<HTMLElement>('[data-nc-mobile-page]')!;
  await page.getByRole('button', { name: 'Back to Report' }).click();
  await Promise.all(panel.getAnimations().map((animation) => animation.finished));
}

describe('Track mobile presentation', () => {
  it('keeps Report as the root and pushes Cards in as a full-width page', async () => {
    await page.viewport(390, 844);
    const router = setup('/track/w1');

    /* Nothing is measured until the route has painted: a box that has not rendered
         has no geometry, and the three-dot menu is the report's own control. */
    const opener = page.getByRole('button', { name: 'Back to Focus' });
    await page.getByRole('button', { name: /^Switch track,/ }).findElement();
    const openerElement = await opener.findElement();
    const panel = document.querySelector<HTMLElement>('[data-nc-mobile-page]')!;
    const root = document.querySelector('[data-nc-track-page]')!;

    expect(root.getBoundingClientRect().width).toBeLessThanOrEqual(window.innerWidth);
    expect(getComputedStyle(panel).visibility).toBe('hidden');
    expect(openerElement.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    expect(openerElement.closest('[data-nc-workspace-header]')).not.toBeNull();
    expect(document.querySelector('nav[aria-label="Primary"]')).toBeNull();
    const header = document.querySelector<HTMLElement>('[data-nc-workspace-header]')!;
    const selector = await page.getByRole('button', { name: /^Switch track,/ }).findElement();
    const selectorBox = selector.getBoundingClientRect();
    expect(selectorBox.width).toBeGreaterThan(0);
    expect(header.getBoundingClientRect().height).toBeGreaterThanOrEqual(52);
    await settlePaint();
    await page.screenshot({ path: '../../../../test-results/mobile-report.png' });
    const reportChatDock = document.querySelector('[data-nc-mobile-report-chat]')!;
    expect(getComputedStyle(reportChatDock).display).not.toBe('none');

    await openTrackNavigation();
    await expect.element(page.getByRole('dialog', { name: 'Tracks and settings' })).toBeVisible();
    const navigation = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement();
    expect(page.getByRole('button', { name: 'Settings', exact: true }).query()).toBeNull();
    const currentTrack = await page.getByRole('button', { name: 'Responsive mobile UI', exact: true }).findElement();
    expect(currentTrack.getAttribute('aria-current')).toBe('page');
    expect(navigation.querySelector('button[aria-label="Back to Report"]')).not.toBeNull();
    const newTrack = await page.getByRole('button', { name: 'New track', exact: true }).findElement();
    expect(newTrack.closest('header')).not.toBeNull();
    for (const label of ['Back to Report', 'New track', 'Area actions']) {
      const control = await page.getByRole('button', { name: label, exact: true }).findElement();
      expect(control.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    }
    const navHeading = await page.getByRole('heading', { name: 'Product', exact: true }).findElement();
    expect(navHeading.getBoundingClientRect().bottom).toBeLessThanOrEqual(64);
    await page.screenshot({ path: '../../../../test-results/mobile-navigation-polished.png' });

    const main = document.querySelector('main')!;
    expect(main.inert).toBe(true);
    expect(main.getAttribute('aria-hidden')).toBe('true');
    expect(getComputedStyle(reportChatDock).display).toBe('none');
    openerElement.focus();
    expect(document.activeElement).not.toBe(openerElement);
    await page.getByRole('button', { name: 'Responsive mobile UI', exact: true }).click();
    expect(document.querySelector('nav[aria-label="Primary"]')).toBeNull();

    await router.navigate({ to: '/track/w1', search: { panel: 'outline' } });
    await Promise.all(panel.getAnimations().map((animation) => animation.finished));
    expect(page.getByRole('heading', { name: 'Outline' })).toBeTruthy();
    expect(page.getByRole('button', { name: 'Mobile workspace direction' })).toBeTruthy();
    await settlePaint();
    await page.screenshot({ path: '../../../../test-results/mobile-outline.png' });
    await closePanel();

    await router.navigate({ to: '/track/w1', search: { panel: 'cards' } });
    await Promise.all(panel.getAnimations().map((animation) => animation.finished));
    const panelBox = panel.getBoundingClientRect();
    expect(getComputedStyle(panel).visibility).toBe('visible');
    expect(panelBox.left).toBe(0);
    expect(panelBox.width).toBe(window.innerWidth);
    expect(document.querySelector('nav[aria-label="Primary"]')).toBeNull();
    expect(page.getByRole('heading', { name: 'Tools' })).toBeTruthy();
    const cardsHeader = await page.getByRole('heading', { name: 'Tools', exact: true }).findElement();
    withFontRoleProbe('--type-page-title', cardsHeader.closest('header')!, expected => {
      expect(getComputedStyle(cardsHeader)).toMatchObject(expected);
    });
    expect(getComputedStyle(cardsHeader.closest('header')!).backdropFilter).toBe('none');
    expect(getComputedStyle(cardsHeader.closest('header')!).borderRadius).toBe('0px');
    expect((await page.getByRole('button', { name: 'Back to Report' }).findElement()).getBoundingClientRect().height)
      .toBeGreaterThanOrEqual(44);
    await settlePaint();
    await page.screenshot({ path: '../../../../test-results/mobile-cards.png' });

    /* Opening a card is not offered on this viewport, so the row is text and not a control. */
    await page.elementLocator(document.querySelector('[data-nc-mobile-panel] [data-nc-inventory-group] > summary')!).click();
    expect(document.querySelector('[data-nc-mobile-panel]')?.textContent)
      .toContain('Implementation terminal');
    expect(document.querySelectorAll('[data-nc-mobile-panel] [data-nc-row] button').length).toBe(0);
    await closePanel();

    await router.navigate({ to: '/track/w1', search: { panel: 'tasks' } });
    await Promise.all(panel.getAnimations().map((animation) => animation.finished));
    expect(page.getByRole('heading', { name: 'Tasks' })).toBeTruthy();
    await settlePaint();
    await page.screenshot({ path: '../../../../test-results/mobile-tasks.png' });
    await closePanel();

    await router.navigate({ to: '/track/w1', search: { panel: 'conversations' } });
    await Promise.all(panel.getAnimations().map((animation) => animation.finished));
    expect(page.getByRole('heading', { name: 'Conversations' })).toBeTruthy();
    expect(document.querySelector('[data-nc-mobile-report-chat]')).toBeNull();
    const conversation = await page.getByRole('button', { name: /^Conversation Design review/ }).findElement();
    expect(conversation.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    expect(getComputedStyle(conversation.closest('ul')!).maxBlockSize).toBe('none');
    await settlePaint();
    await page.screenshot({ path: '../../../../test-results/mobile-conversations.png' });
  });

  it('keeps MobileListPage items inside landscape safe areas', async () => {
    const width = 844;
    const leftInset = 47;
    const rightInset = 23;
    await page.viewport(width, 390);
    try {
      await commands.setHorizontalSafeArea(leftInset, rightInset);
      setup('/track/w1?panel=outline');
      for (const name of [/^Mobile workspace direction/, /^Why this shape/]) {
        const item = await page.getByRole('button', { name }).findElement();
        const { left, right } = item.getBoundingClientRect();
        expect(left, name.source).toBeGreaterThanOrEqual(leftInset);
        expect(right, name.source).toBeLessThanOrEqual(width - rightInset);
      }
    } finally {
      await commands.setHorizontalSafeArea(0, 0);
    }
  });

  it('applies the landscape safe area once to the Tasks empty state', async () => {
    const leftInset = 47;
    await page.viewport(844, 390);
    try {
      await commands.setHorizontalSafeArea(leftInset, 23);
      setup('/track/w2?panel=tasks');
      const empty = await page.getByRole('heading', { name: 'No tasks yet', exact: true }).findElement();
      const { left } = empty.getBoundingClientRect();
      const gutter = Number.parseFloat(getComputedStyle(empty).getPropertyValue('--space-4'));
      expect(left).toBeGreaterThanOrEqual(leftInset);
      expect(left).toBeLessThan(2 * leftInset);
      expect(left).toBeCloseTo(leftInset + 2 * gutter, 0);
    } finally {
      await commands.setHorizontalSafeArea(0, 0);
    }
  });

  it('keeps mobile header actions separate from the desktop Track menu across viewport changes', async () => {
    await page.viewport(390, 844);
    const router = setup('/track/w1');
    const back = await page.getByRole('button', { name: 'Back to Focus' }).findElement();
    expect(back.closest('[data-nc-workspace-header]')).not.toBeNull();
    await expect.element(page.getByRole('button', { name: 'Track actions', exact: true })).toBeVisible();
    await page.viewport(1400, 900);
    await expect.poll(() => document.querySelector('[data-nc-workspace-header]')).toBeNull();
    expect(back.isConnected).toBe(false);
    await expect.element(page.getByRole('button', { name: 'Track actions for Responsive mobile UI', exact: true })).toBeVisible();
    await page.viewport(390, 844);
    await expect.poll(() => document.querySelector('[data-nc-workspace-header]')).not.toBeNull();
    expect(document.querySelectorAll('[data-nc-workspace-header] button[aria-label="Back to Focus"]')).toHaveLength(1);
    await router.navigate({ to: '/track/w1', search: { panel: 'cards' } });
    await expect.element(page.getByRole('heading', { name: 'Tools', exact: true })).toBeVisible();
  });

  it('keeps Area selection and settings navigation clear at the narrow phone width', async () => {
    await page.viewport(320, 740);
    const router = setup('/area/c2/new');
    await page.getByRole('button', { name: 'Switch area, Frontend' }).click();
    const current = await page.getByRole('menuitem', { name: 'Frontend', exact: true }).findElement();
    const other = await page.getByRole('menuitem', { name: 'Product', exact: true }).findElement();
    expect(current.getAttribute('aria-current')).toBe('page');
    expect(current.querySelector('svg')).toBeNull();
    expect(getComputedStyle(current).backgroundColor).not.toBe(getComputedStyle(other).backgroundColor);
    const separator = await page.getByRole('separator').findElement();
    expect(separator.nextElementSibling?.textContent).toBe('New area');
    await page.getByRole('button', { name: 'Switch area, Frontend' }).click();
    await openSettings();
    await page.getByRole('button', { name: 'Appearance', exact: true }).click();
    await page.getByRole('heading', { name: 'Appearance', exact: true }).findElement();
    expect(router.state.location.pathname).toBe('/settings/appearance');
    expect(page.getByRole('navigation', { name: 'Settings sections' }).query()).toBeNull();
    const networkInput = await page.getByRole('combobox', { name: 'Theme', exact: true }).findElement();
    const field = networkInput.parentElement!;
    const fieldBox = field.getBoundingClientRect();
    expect(fieldBox.width).toBeLessThanOrEqual(320);
    expect(networkInput.getBoundingClientRect().left).toBeGreaterThanOrEqual(fieldBox.left);
    expect(networkInput.getBoundingClientRect().right).toBeLessThanOrEqual(fieldBox.right);
    await page.getByRole('button', { name: 'Back to Settings' }).click();
    const about = await page.getByRole('button', { name: 'About', exact: true }).findElement();
    const aboutBox = about.getBoundingClientRect();
    expect(aboutBox.left).toBeGreaterThanOrEqual(0);
    expect(aboutBox.right).toBeLessThanOrEqual(320);
    await page.getByRole('button', { name: 'About', exact: true }).click();
    await page.getByRole('heading', { name: 'About', exact: true }).findElement();
    await page.getByRole('button', { name: 'Back to Settings' }).click();
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/area/c2/new');
  });

  it('returns from Settings to the latest search on the same workspace path', async () => {
    await page.viewport(390, 844);
    const router = setup('/track/w1?from=pages');
    await page.getByRole('button', { name: /^Switch track,/ }).findElement();
    await router.navigate({ to: '/track/w1', search: { from: 'area' } });
    await openSettings();
    await page.getByRole('heading', { name: 'Settings', exact: true }).findElement();
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => router.state.location.href).toBe('/track/w1?from=area');
  });

  it('pops the Settings visit across three cycles so hardware Back reaches the earlier workspace', async () => {
    await page.viewport(390, 844);
    const router = setup('/area/c1/new');
    await page.getByRole('button', { name: 'Switch area, Product' }).findElement();
    await router.navigate({ to: '/area/c2/new' });
    for (let cycle = 0; cycle < 3; cycle += 1) {
      await openSettings();
      await page.getByRole('heading', { name: 'Settings', exact: true }).findElement();
      await page.getByRole('button', { name: 'Appearance', exact: true }).click();
      await page.getByRole('heading', { name: 'Appearance', exact: true }).findElement();
      await page.getByRole('button', { name: 'Back to Settings' }).click();
      await expect.poll(() => router.state.location.pathname).toBe('/settings');
      await page.getByRole('button', { name: 'Back to workspace' }).click();
      await expect.poll(() => router.state.location.pathname).toBe('/area/c2/new');
      expect(router.history.location.state.__TSR_index).toBe(1);
      // The forward entries are reused on the next visit, not accumulated as duplicates.
      expect(router.history.length).toBe(4);
    }
    router.history.back();
    await expect.poll(() => router.state.location.pathname).toBe('/area/c1/new');
  });

  it('pops desktop section pushes as one visit after switching to phone width', async () => {
    await page.viewport(390, 844);
    const router = setup('/area/c2/new');
    await openSettings();
    await page.getByRole('heading', { name: 'Settings', exact: true }).findElement();
    await page.viewport(1400, 900);
    await page.getByRole('dialog', { name: 'Settings' }).findElement();
    await page.getByRole('button', { name: 'About', exact: true }).click();
    await page.getByRole('heading', { name: 'About', exact: true }).findElement();
    await page.getByRole('button', { name: 'Planners', exact: true }).click();
    await page.getByRole('heading', { name: 'Planners', exact: true }).findElement();
    expect(router.history.location.state.__TSR_index).toBe(3);
    await page.viewport(390, 844);
    await page.getByRole('button', { name: 'Back to Settings' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/settings');
    expect(router.history.location.state.__TSR_index).toBe(1);
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/area/c2/new');
    expect(router.history.location.state.__TSR_index).toBe(0);
  });

  it('opens Settings as a vertical category index with no settings controls underneath', async () => {
    await page.viewport(390, 844);
    setup('/settings');
    await page.getByRole('heading', { name: 'Settings', exact: true }).findElement();
    const network = await page.getByRole('button', { name: 'Planners', exact: true }).findElement();
    const appearance = await page.getByRole('button', { name: 'Appearance', exact: true }).findElement();
    expect(network.getBoundingClientRect().top).toBeGreaterThanOrEqual(appearance.getBoundingClientRect().bottom);
    expect(page.getByRole('textbox', { name: 'HTTP proxy', exact: true }).query()).toBeNull();
    expect(page.getByRole('heading', { name: 'Network', exact: true }).query()).toBeNull();
    await page.screenshot({ path: '../../../../test-results/mobile-settings-index.png' });
  });

  it('keeps the Settings index categories inside landscape safe areas', async () => {
    const width = 844;
    const leftInset = 47;
    const rightInset = 23;
    await page.viewport(width, 390);
    try {
      await commands.setHorizontalSafeArea(leftInset, rightInset);
      setup('/settings');
      const index = page.getByRole('navigation', { name: 'Settings categories' });
      const indexBounds = (await index.findElement()).getBoundingClientRect();
      expect(indexBounds.left).toBeGreaterThanOrEqual(leftInset);
      expect(indexBounds.right).toBeLessThanOrEqual(width - rightInset);
      for (const name of ['Appearance', 'Planners', 'About']) {
        const category = await index.getByRole('button', { name, exact: true }).findElement();
        const { left, right } = category.getBoundingClientRect();
        expect(left, name).toBeGreaterThanOrEqual(leftInset);
        expect(right, name).toBeLessThanOrEqual(width - rightInset);
      }
    } finally {
      await commands.setHorizontalSafeArea(0, 0);
    }
  });

  it.each([
    { label: 'Planners', path: '/settings/planners' },
    { label: 'Appearance', path: '/settings/appearance' },
    { label: 'About', path: '/settings/about' },
  ] as const)('opens the $label category from the index and returns through browser Back', async ({ label, path }) => {
    await page.viewport(390, 844);
    const router = setup('/settings');
    await page.getByRole('button', { name: label, exact: true }).click();
    await page.getByRole('heading', { name: label, exact: true }).findElement();
    expect(router.state.location.pathname).toBe(path);
    expect(page.getByRole('heading', { name: label, exact: true }).all()).toHaveLength(1);
    expect(page.getByRole('navigation', { name: 'Settings categories' }).query()).toBeNull();
    expect(page.getByRole('navigation', { name: 'Settings sections' }).query()).toBeNull();
    router.history.back();
    await expect.poll(() => router.state.location.pathname).toBe('/settings');
    await page.getByRole('navigation', { name: 'Settings categories' }).findElement();
    expect(router.history.location.state.__TSR_index).toBe(0);
  });

  it.each(['planners', 'appearance', 'about'] as const)('returns a cold %s detail link safely to the index', async (section) => {
    await page.viewport(390, 844);
    const router = setup(`/settings/${section}`);
    await page.getByRole('button', { name: 'Back to Settings' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/settings');
    await page.getByRole('navigation', { name: 'Settings categories' }).findElement();
    expect(router.history.length).toBe(1);
    expect(router.history.location.state.__TSR_index).toBe(0);
  });

  it.each([320, 390])('centers the current Area header and separates its actions at %ipx', async (width) => {
    await page.viewport(width, 844);
    const areaName = 'Product design and research with a deliberately long Area name';
    setup('/', areaName);
    const opener = await page.getByRole('button', { name: 'Open workspace' }).findElement();
    await page.elementLocator(opener).click();
    await page.getByRole('button', { name: areaName, exact: true }).click();
    const pane = document.querySelector('[data-nc-workspace-page="tracks"]')!;
    await Promise.all(pane.getAnimations().map((animation) => animation.finished));
    await settlePaint();
    const heading = await page.getByRole('heading', { name: areaName, exact: true }).findElement();
    const titleBox = heading.getBoundingClientRect();
    const labels = ['Back to Workspace', 'New track', 'Area actions'];
    const controls = await Promise.all(labels.map((label) => page.getByRole('button', { name: label, exact: true }).findElement()));
    const boxes = controls.map((control) => control.getBoundingClientRect());
    expect(boxes[0].right).toBeLessThanOrEqual(titleBox.left);
    expect(titleBox.right).toBeLessThanOrEqual(boxes[1].left);
    expect(boxes[1].right).toBeLessThanOrEqual(boxes[2].left);
    expect(page.getByRole('button', { name: 'Settings', exact: true }).query()).toBeNull();
    for (const box of boxes) {
      expect(box.height).toBeGreaterThanOrEqual(44);
    }
    await page.screenshot({ path: `../../../../test-results/mobile-navigation-header-${width}.png` });
    await page.getByRole('button', { name: 'Back to Workspace' }).click();
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => page.getByRole('dialog', { name: 'Tracks and settings' }).query()).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  it('omits unavailable Track actions before a Track exists', async () => {
    await page.viewport(390, 844);
    const router = setup('/area/c1/new');
    await page.getByRole('button', { name: 'Switch area, Product', exact: true }).findElement();
    expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
    expect(page.getByRole('menuitem', { name: 'Tools', exact: true }).query()).toBeNull();
    expect(page.getByRole('menuitem', { name: 'Conversations', exact: true }).query()).toBeNull();
    expect(router.state.location.pathname).toBe('/area/c1/new');
    expect(document.querySelector('[data-nc-mobile-page]')).toBeNull();
    await page.screenshot({ path: '../../../../test-results/mobile-short-track-actions.png' });
  });

  it('switches the navigation Area locally and uses that exact Area for edit and new track', async () => {
    await page.viewport(390, 844);
    const router = setup('/');
    const opener = await page.getByRole('button', { name: 'Open workspace', exact: true }).findElement();
    await page.elementLocator(opener).click();
    await page.getByRole('button', { name: 'Frontend', exact: true }).click();
    await page.getByRole('heading', { name: 'Frontend', exact: true }).findElement();
    expect(document.activeElement).not.toBe(document.body);
    expect(router.state.location.pathname).toBe('/');
    expect(page.getByRole('button', { name: 'Responsive mobile UI', exact: true }).query()).toBeNull();
    await page.getByText('No tracks in this area').findElement();
    await page.getByRole('button', { name: 'Area actions' }).click();
    await page.getByRole('menuitem', { name: 'Edit area Frontend' }).click();
    await page.getByRole('dialog', { name: 'Edit Frontend', exact: true }).findElement();
    expect((await page.getByRole('textbox', { name: /^Name/ }).findElement() as HTMLInputElement).value).toBe('Frontend');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Back to Workspace' }).click();
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => document.activeElement).toBe(opener);
    expect(router.state.location.pathname).toBe('/');
    await page.elementLocator(opener).click();
    await page.getByRole('button', { name: 'Frontend', exact: true }).click();
    await page.getByRole('button', { name: 'New track', exact: true }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/area/c2/new');
    await page.getByRole('button', { name: 'Switch area, Frontend' }).findElement();
  });

  it('keeps navigation open when Escape closes its Area editor', async () => {
    await page.viewport(320, 740);
    setup('/');
    await openTrackNavigation();
    await page.getByRole('button', { name: 'Back to Workspace' }).click();
    await page.getByRole('button', { name: 'New area', exact: true }).click();
    await page.getByRole('dialog', { name: 'New area', exact: true }).findElement();
    expect(page.getByRole('dialog').elements()).toHaveLength(1);
    expect(getComputedStyle(document.getElementById('mobile-workspace-navigation')!).visibility).toBe('hidden');
    await userEvent.keyboard('{Escape}');
    expect(page.getByRole('dialog', { name: 'New area', exact: true }).query()).toBeNull();
    const navigation = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement();
    await expect.poll(() => navigation.contains(document.activeElement)).toBe(true);
    await userEvent.keyboard('{Escape}');
    await expect.poll(() => page.getByRole('dialog', { name: 'Tracks and settings' }).query()).toBeNull();
  });

  it('creates a new Area from compact navigation without stacked dialogs and lands on its track creation page', async () => {
    await page.viewport(390, 844);
    const router = setup('/');
    await openTrackNavigation();
    await page.getByRole('button', { name: 'Back to Workspace' }).click();
    await page.getByRole('button', { name: 'New area', exact: true }).click();
    await page.getByRole('dialog', { name: 'New area', exact: true }).findElement();
    expect(page.getByRole('dialog').elements()).toHaveLength(1);
    expect(getComputedStyle(document.getElementById('mobile-workspace-navigation')!).visibility).toBe('hidden');
    await page.getByRole('textbox', { name: /^Name/ }).fill('Research');
    await page.getByRole('button', { name: 'Create area', exact: true }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/area/c3/new');
    expect(page.getByRole('dialog', { name: 'New area', exact: true }).query()).toBeNull();
    expect(page.getByRole('dialog', { name: 'Tracks and settings' }).query()).toBeNull();
    await page.getByRole('button', { name: 'Switch area, Research', exact: true }).findElement();
  });


  it('keeps workspace creation and settings independently usable across their full touch targets', async () => {
    await page.viewport(390, 844);
    const router = setup('/');
    await openTrackNavigation();
    await page.getByRole('button', { name: 'Back to Workspace', exact: true }).click();
    const settingsControl = page.getByRole('button', { name: 'Settings', exact: true });
    const settings = await settingsControl.findElement();
    const box = settings.getBoundingClientRect();
    expect(box.height).toBeGreaterThanOrEqual(44);
    await settingsControl.click({ position: { x: 4, y: box.height / 2 } });
    await expect.poll(() => router.state.location.pathname).toBe('/settings');
    await page.getByRole('navigation', { name: 'Settings categories' }).findElement();
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    await expect.poll(() => router.state.location.pathname).toBe('/');
    await openTrackNavigation();
    const create = await page.getByRole('button', { name: 'New track', exact: true }).findElement();
    expect(create.getBoundingClientRect().width).toBeGreaterThanOrEqual(44);
    expect(create.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    await page.elementLocator(create).click({ position: { x: 4, y: 22 } });
    await expect.poll(() => router.state.location.pathname).toBe('/area/c1/new');
  });

  it('keeps pressed and keyboard focus feedback on each control contour without native blue tap paint', async () => {
    await page.viewport(390, 844);
    const router = setup('/area/c1/new');
    const selector = await page.getByRole('button', { name: 'Switch area, Product' }).findElement();
    expect(getComputedStyle(document.documentElement).getPropertyValue('-webkit-tap-highlight-color')).toBe('rgba(0, 0, 0, 0)');
    await userEvent.keyboard('{Tab}');
    (selector as HTMLElement).focus();
    expect(selector.matches(':focus-visible')).toBe(true);
    expect(getComputedStyle(selector).borderRadius).toBe('12px');
    expect(getComputedStyle(selector).outlineStyle).toBe('solid');
    expect(getComputedStyle(selector).outlineWidth).toBe('2px');
    await userEvent.keyboard('[Space>]');
    await expect.poll(() => new DOMMatrix(getComputedStyle(selector).transform).a).toBeLessThan(1);
    expect(new DOMMatrix(getComputedStyle(selector).transform).f).toBeGreaterThan(0);
    expect(selector.getAnimations()).toHaveLength(1);
    expect([...selector.querySelectorAll('*')].flatMap(child => child.getAnimations())).toHaveLength(0);
    expect(document.activeElement).toBe(selector);
    expect(getComputedStyle(selector).backgroundImage).not.toBe('none');
    expect(getComputedStyle(selector).borderRadius).toBe('12px');
    await userEvent.keyboard('[/Space]');
    await expect.poll(() => selector.getAnimations().length).toBe(0);
    expect(getComputedStyle(selector).transform).toBe('none');
    const menu = await page.getByRole('menu').findElement();
    const selected = await page.getByRole('menuitem', { name: 'Product', exact: true }).findElement();
    expect(getComputedStyle(menu).borderRadius).toBe('16px');
    expect(getComputedStyle(menu).paddingTop).toBe('4px');
    expect(getComputedStyle(menu).boxShadow).not.toBe('none');
    expect(getComputedStyle(selected).borderRadius).toBe('8px');
    await page.getByRole('menuitem', { name: 'Product', exact: true }).hover();
    expect(getComputedStyle(selected).borderRadius).toBe('8px');
    await userEvent.keyboard('{Escape}');
    await userEvent.keyboard('{Escape}');
    await router.navigate({ to: '/' });
    const workspaceButton = await page.getByRole('button', { name: 'Open workspace' }).findElement();
    (workspaceButton as HTMLElement).focus();
    expect(workspaceButton.matches(':focus-visible')).toBe(true);
    await page.elementLocator(workspaceButton).click();
    await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement();
    const settings = await page.getByRole('button', { name: 'Settings', exact: true }).findElement();
    expect(settings.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    await page.screenshot({ path: '../../../../test-results/mobile-contour-feedback.png' });
  });

  it('returns from Planner immediately to Conversations without a Report flash', async () => {
    await page.viewport(390, 844);
    const router = setup('/track/w1');
    await router.navigate({ to: '/track/w1', search: { panel: 'conversations' } });
    await page.getByRole('heading', { name: 'Conversations', exact: true }).findElement();
    const panel = document.querySelector<HTMLElement>('[data-nc-mobile-page="open"]')!;
    await Promise.all(panel.getAnimations().map((animation) => animation.finished));
    const planner = await page.getByRole('button', { name: /Design review/ }).findElement();
    await page.getByRole('button', { name: /Design review/ }).click();
    const drawer = document.querySelector<HTMLElement>('[data-nc-drawer]')!;
    const sheetPanel = document.querySelector<HTMLElement>('[data-nc-mobile-chat-panel]')!;
    expect(getComputedStyle(panel).visibility).toBe('visible');
    expect(sheetPanel.getBoundingClientRect().left).toBe(0);
    expect(sheetPanel.getBoundingClientRect().width).toBe(390);
    expect(planner.closest('[inert]')).not.toBeNull();
    expect(planner.closest('[aria-hidden="true"]')).not.toBeNull();
    await Promise.all(drawer.getAnimations().map((animation) => animation.finished));
    const heading = await page.elementLocator(drawer).getByRole('heading', { name: 'Design review', exact: true }).findElement();
    expect(getComputedStyle(heading).fontSize).toBe('16px');
    await page.getByRole('button', { name: 'Close conversation' }).click();
    await expect.poll(() => drawer.closest('dialog')?.open).toBe(false);
    for (let frame = 0; frame < 4; frame += 1) {
      await settlePaint();
      expect(getComputedStyle(panel).visibility).toBe('visible');
      expect(panel.getBoundingClientRect().left).toBe(0);
      expect(planner.closest('[inert]')).toBeNull();
      expect(document.elementFromPoint(20, 150)?.closest('[data-nc-mobile-page="open"]')).toBe(panel);
    }
    await page.screenshot({ path: '../../../../test-results/planner-return-direct.png' });
    await expect.poll(() => document.activeElement).toBe(planner);
  });


});


it('keeps every mobile Track action label readable within the viewport', async () => {
  await page.viewport(390, 844);
  setup('/track/w1');
  await openTrackNavigation();
  await page.getByRole('button', { name: 'Actions for track Responsive mobile UI', exact: true }).click();
  const menu = page.getByRole('menu', { exact: true }).element();
  const bounds = menu.getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(390);
  for (const label of ['Unpin globally', 'Pin within area', 'Mark as unread']) {
    const item = page.getByRole('menuitem', { name: label, exact: true }).element();
    const nodes = document.createTreeWalker(item, NodeFilter.SHOW_TEXT);
    let node: Node | null;
    while ((node = nodes.nextNode()) !== null && node.textContent?.trim() !== label) { /* find the actual label */ }
    expect(node).not.toBeNull();
    const range = document.createRange();
    range.selectNodeContents(node!);
    const text = range.getBoundingClientRect();
    expect(text.left).toBeGreaterThanOrEqual(bounds.left);
    expect(text.right).toBeLessThanOrEqual(bounds.right);
  }
});

// Native playback controls here prove races, never natural screenshots.
function holdWorkspaceExit(host: HTMLElement) {
  const animate = host.animate.bind(host);
  const exits: Animation[] = [];
  vi.spyOn(host, 'animate').mockImplementation((frames, options) => {
    const animation = animate(frames, options);
    queueMicrotask(() => { animation.pause(); animation.currentTime = 0; });
    exits.push(animation);
    return animation;
  });
  return () => {
    expect(exits).toHaveLength(1);
    const exit = exits[0];
    expect((exit.effect as KeyframeEffect).target).toBe(host);
    expect(host.getAnimations()).toContain(exit);
    expect(exit.playState).toBe('paused');
    return exit;
  };
}

function expectPaintOnly(host: HTMLElement) {
  expect(host.isConnected).toBe(true);
  expect(getComputedStyle(host).visibility).toBe('visible');
  expect(host.inert).toBe(true);
  expect(host.getAttribute('aria-hidden')).toBe('true');
  for (const attribute of ['aria-modal', 'role', 'data-nc-escape-layer']) expect(host.hasAttribute(attribute)).toBe(false);
  expect(document.querySelector('main')?.hasAttribute('inert')).toBe(false);
  expect(document.querySelector('[data-nc-workspace-header]')?.hasAttribute('inert') ?? false).toBe(false);
}

describe('workspace exit interaction lifetime', () => {
  it.each([320, 390])('revokes Back permissions before exit paint settles at %ipx', async width => {
    await page.viewport(width, 844);
    setup('/');
    const opener = await page.getByRole('button', { name: 'Open workspace' }).findElement();
    await page.elementLocator(opener).click();
    const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    await Promise.all(host.getAnimations().map(animation => animation.finished));
    const held = holdWorkspaceExit(host);
    await page.getByRole('button', { name: 'Back to workspace' }).click();
    const exit = held();
    expectPaintOnly(host);
    expect(document.activeElement).toBe(opener);
    const box = opener.getBoundingClientRect();
    expect(document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2)?.closest('button')).toBe(opener);
    let prevented: boolean | undefined;
    const observe = (event: KeyboardEvent) => { if (event.key === 'Tab') prevented = event.defaultPrevented; };
    window.addEventListener('keydown', observe);
    try { await userEvent.keyboard('{Tab}'); } finally { window.removeEventListener('keydown', observe); }
    expect(prevented).toBe(false);
    expect(host.contains(document.activeElement)).toBe(false);
    exit.finish();
    await expect.poll(() => getComputedStyle(host).visibility).toBe('hidden');
  });

  it('gives a nested native menu Escape priority and preserves a rapid reopened presentation', async () => {
    await page.viewport(390, 844);
    setup('/');
    await openTrackNavigation();
    const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    await Promise.all(host.getAnimations().map(animation => animation.finished));
    await page.getByRole('button', { name: 'Area actions', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Edit area Product' }).findElement();
    await userEvent.keyboard('{Escape}');
    expect(host.inert).toBe(false);
    expect(host.getAttribute('aria-modal')).toBe('true');
    const held = holdWorkspaceExit(host);
    await userEvent.keyboard('{Escape}');
    const exit = held();
    expectPaintOnly(host);
    vi.restoreAllMocks();
    await page.getByRole('button', { name: 'Open workspace' }).click();
    expect(exit.playState).toBe('idle');
    exit.finish();
    await settlePaint();
    expect(page.getByRole('dialog', { name: 'Tracks and settings' }).element()).toBe(host);
    expect(host.inert).toBe(false);
    expect(host.contains(document.activeElement)).toBe(true);
  });

  it('releases selection immediately and never steals newer route or editor focus on finish', async () => {
    await page.viewport(390, 844);
    const router = setup('/track/w1');
    await openTrackNavigation();
    const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    await Promise.all(host.getAnimations().map(animation => animation.finished));
    const held = holdWorkspaceExit(host);
    await page.getByRole('button', { name: 'New track', exact: true }).click();
    const exit = held();
    expectPaintOnly(host);
    await expect.poll(() => router.state.location.pathname).toBe('/area/c1/new');
    const editor = await page.getByRole('combobox', { name: 'What this track should do', exact: true }).findElement();
    (editor as HTMLElement).focus();
    expect(document.activeElement).toBe(editor);
    exit.finish();
    await expect.poll(() => getComputedStyle(host).visibility).toBe('hidden');
    expect(document.activeElement).toBe(editor);
    expect(router.history.location.state.__TSR_index).toBe(1);
  });

  it('revokes compact scope and cancels its old animation when switching to desktop', async () => {
    await page.viewport(390, 844);
    setup('/');
    await openTrackNavigation();
    const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    await Promise.all(host.getAnimations().map(animation => animation.finished));
    const held = holdWorkspaceExit(host);
    await userEvent.keyboard('{Escape}');
    const exit = held();
    await page.viewport(1400, 900);
    await expect.element(page.getByRole('navigation', { name: 'Workspace', exact: true })).toBeVisible();
    expect(host.isConnected).toBe(false);
    expect(document.querySelector('#mobile-workspace-navigation')).toBeNull();
    expect(host.getAnimations()).toHaveLength(0);
    expect(exit.playState).toBe('idle');
    await page.viewport(390, 844);
    vi.restoreAllMocks();
    await page.getByRole('button', { name: 'Open workspace' }).click();
    exit.finish();
    await settlePaint();
    const reopened = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    expect(reopened.inert).toBe(false);
    expect(reopened.getAttribute('aria-modal')).toBe('true');
  });

  it('settles reduced motion without waiting for a native completion', async () => {
    await commands.emulateReducedMotion(true);
    await page.viewport(320, 844);
    setup('/');
    const opener = await page.getByRole('button', { name: 'Open workspace' }).findElement();
    await page.elementLocator(opener).click();
    const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
    expect(host.getAnimations()).toHaveLength(0);
    await userEvent.keyboard('{Escape}');
    expect(host.inert).toBe(true);
    expect(host.hasAttribute('data-nc-escape-layer')).toBe(false);
    expect(document.querySelector('main')?.hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(opener);
    expect(host.getAnimations()).toHaveLength(0);
    expect(getComputedStyle(host).visibility).toBe('hidden');
  });
});


it('returns to the latest report opener and releases Track selection before paint finishes', async () => {
  await page.viewport(390, 844);
  const router = setup('/track/w1');
  const selector = await page.getByRole('button', { name: /^Switch track,/ }).findElement();
  await page.elementLocator(selector).click();
  const host = await page.getByRole('dialog', { name: 'Tracks and settings' }).findElement() as HTMLElement;
  await Promise.all(host.getAnimations().map(animation => animation.finished));
  await userEvent.keyboard('{Escape}');
  expect(document.activeElement).toBe(selector);
  await page.elementLocator(selector).click();
  await Promise.all(host.getAnimations().map(animation => animation.finished));
  const held = holdWorkspaceExit(host);
  await page.getByRole('button', { name: 'Responsive mobile UI', exact: true }).click();
  const exit = held();
  expectPaintOnly(host);
  expect(router.state.location.pathname).toBe('/track/w1');
  const newer = await page.getByRole('button', { name: 'Back to Focus' }).findElement();
  (newer as HTMLElement).focus();
  exit.finish();
  await expect.poll(() => getComputedStyle(host).visibility).toBe('hidden');
  expect(document.activeElement).toBe(newer);
});
