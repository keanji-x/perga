import { fixtureSession } from './session-contract-fixture.ts';
import { sessionPage, sessionItem, sessionTurn, sessionInput } from './conversation-session-data.ts';
import '../../styles/entry.css';
import { useLayoutEffect, useRef } from 'react';
import { ConversationSurface } from './mobile-conversation-surface.tsx';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
afterEach(async () => { cleanup(); await page.viewport(1280, 800); });

it.each([320, 390])('aligns the full-width bottom dock with fixed role slot and keeps chat usable at %ipx', async (width) => {
  await page.viewport(width, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  const field = await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
  const bar = field.closest('form')!;
  const closedRowLeft = bar.getBoundingClientRect().left;
  const closedRoleBox = page.getByRole('button', { name: /^Switch conversation,/ }).element().getBoundingClientRect();
  expect(Math.round(closedRowLeft)).toBe(16);
  expect(Math.round(bar.getBoundingClientRect().right)).toBe(width - 16);
  expect(getComputedStyle(bar).borderRadius).toBe('999px');
  expect(Math.round(closedRoleBox.width)).toBe(width === 320 ? 67 : 76);

  const leading = page.getByRole('button', { name: 'Back to Focus', exact: true }).element().getBoundingClientRect();
  const title = page.getByRole('button', { name: 'Switch track, 2026-10-04', exact: true }).element().getBoundingClientRect();
  expect(title.left).toBeGreaterThanOrEqual(leading.right);
  expect(title.right).toBeLessThanOrEqual(width - 16);

  const titlePaint = getComputedStyle(page.getByRole('button', { name: 'Switch track, 2026-10-04', exact: true }).element());
  expect(titlePaint.backgroundColor).toBe('rgba(0, 0, 0, 0)');
  expect(titlePaint.borderTopWidth).toBe('0px');
  expect(titlePaint.boxShadow).toBe('none');
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();

  await page.getByRole('button', { name: 'Switch track, 2026-10-04', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'Workspace', exact: true })).toBeVisible();
  expect(page.getByRole('menu').query()).toBeNull();
  await page.getByRole('button', { name: 'Back to Today', exact: true }).click();

  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toBeVisible();
  page.getByRole('textbox', { name: 'Chat message', exact: true }).element().focus();
  const history = await page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true }).findElement();
  const message = page.getByRole('combobox', { name: 'Message', exact: true }).element();
  expect(message.getBoundingClientRect().width).toBeGreaterThan(width - 200);
  const panel = history.querySelector<HTMLElement>('[data-nc-drawer]')!;
  const sheet = panel.closest('[data-nc-mobile-chat-panel]')!;
  expect(getComputedStyle(sheet).overflow).toBe('hidden');
  expect(getComputedStyle(sheet).borderTopLeftRadius).toBe('24px');
  const workspaceButton = page.getByRole('button', { name: 'Back to Focus', exact: true }).element();
  expect(sheet.getBoundingClientRect().top - workspaceButton.getBoundingClientRect().bottom).toBeGreaterThanOrEqual(4);
  const bounds = panel.getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(width);
  expect(bounds.height).toBeGreaterThan(844 * 0.75);
  expect(bounds.height).toBeLessThan(844 * 0.9);
  await expect.poll(() => document.activeElement === message).toBe(true);
  const expandedBar = message.closest('[class*="vendorComposer"]')?.firstElementChild;
  expect(expandedBar).toBeTruthy();
  expect(Math.round(expandedBar!.getBoundingClientRect().left)).toBe(Math.round(closedRowLeft));
  const expandedRoleBox = history.querySelector<HTMLButtonElement>('button[aria-label^="Switch conversation,"]')!.getBoundingClientRect();
  expect(Math.round(expandedRoleBox.width)).toBe(Math.round(closedRoleBox.width));
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toBeVisible();
  expect(document.activeElement?.hasAttribute('data-nc-chat-dock')).toBe(true);
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('keeps the Track Report dock and Chat Sheet controls inside landscape safe areas', async () => {
  const width = 844;
  const leftInset = 47;
  const rightInset = 23;
  await page.viewport(width, 390);
  const safeBounds = (element: Element) => {
    const bounds = element.getBoundingClientRect();
    expect(bounds.left, element.getAttribute('aria-label') ?? undefined).toBeGreaterThanOrEqual(leftInset);
    expect(bounds.right, element.getAttribute('aria-label') ?? undefined).toBeLessThanOrEqual(width - rightInset);
    return bounds;
  };
  try {
    await commands.setHorizontalSafeArea(leftInset, rightInset);
    renderDailyFixture({ initial: '/track/daily' });
    const dock = page.getByRole('form', { name: 'Quick chat' });
    const entry = dock.getByRole('textbox', { name: 'Chat message' });
    safeBounds(await dock.findElement());
    safeBounds(await entry.findElement());
    const dockSwitch = safeBounds(await dock.getByRole('button', { name: /^Switch conversation,/ }).findElement());
    const dockSend = safeBounds(await dock.getByRole('button', { name: 'Send chat message' }).findElement());

    await entry.click();
    const sheet = page.getByRole('dialog', { name: 'Daily Planner conversation' });
    safeBounds(await sheet.getByRole('button', { name: 'Expand conversation' }).findElement());
    safeBounds(await sheet.getByRole('button', { name: 'Close conversation' }).findElement());
    safeBounds(sheet.element().querySelector('[data-nc-drawer-scroll]')!);
    const sheetSwitch = safeBounds(await sheet.getByRole('button', { name: /^Switch conversation,/ }).findElement());
    const sheetSend = safeBounds(await sheet.getByRole('button', { name: 'Send', exact: true }).findElement());
    expect(sheetSwitch.left).toBeCloseTo(dockSwitch.left, 0);
    expect(sheetSend.right).toBeCloseTo(dockSend.right, 0);
    const message = await sheet.getByRole('combobox', { name: 'Message', exact: true }).findElement();

    const footer = message.closest('[data-nc-sheet-footer]')!;
    const panel = sheet.element().querySelector('[data-nc-sheet-panel]')!;
    expect(panel.getBoundingClientRect()).toMatchObject({ left: 0, right: width });
    expect(footer.getBoundingClientRect()).toMatchObject({ left: 0, right: width });
    expect(getComputedStyle(footer).backgroundColor).toBe(getComputedStyle(panel).backgroundColor);
  } finally {
    await commands.setHorizontalSafeArea(0, 0);
  }
});

it('keeps draft words across closing and sends the same Planner before reopening its reply history', async () => {
  await page.viewport(390, 844);
  let answered = false;
  const { requests, client } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true, reply: (request) => {
    if (request.path.endsWith('/messages')) return ok(sessionInput('daily-planner'));
    if (request.path.includes('/items?') && answered) return ok(sessionPage(fixtureSession('daily-planner', 'daily'), [sessionItem('daily-planner', 1, 'Your plan is ready.')]));
    return undefined;
  } });
  const entry = page.getByRole('textbox', { name: 'Chat message', exact: true });
  (await entry.findElement() as HTMLElement).focus();
  await page.getByRole('combobox', { name: 'Message', exact: true }).click();
  await userEvent.keyboard('整理今天的计划{Shift>}{Enter}{/Shift}保留这行');
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  await expect.element(entry).toHaveValue('整理今天的计划\n保留这行');
  await page.getByRole('button', { name: 'Send chat message', exact: true }).click();
  await expect.poll(() => requests.filter((request) => request.path.endsWith('/messages')).length).toBe(1);
  const sent = requests.find((request) => request.path.endsWith('/messages'))!;
  expect(sent.path).toBe('/api/v1/sessions/daily-planner/messages');
  expect(sent.body).toMatchObject({ content: '整理今天的计划\n保留这行' });
  expect(sent.headers?.['Idempotency-Key']).toBeTruthy();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
  answered = true;
  await client.invalidateQueries();
  await expect.element(page.getByText('Your plan is ready.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  entry.element().focus();
  await expect.element(page.getByText('Your plan is ready.', { exact: true })).toBeVisible();
  expect(requests.filter((request) => request.method === 'POST' && request.path.endsWith('/sessions'))).toHaveLength(0);
});

it('shows no draft entry on desktop and never dispatches an empty message', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await expect.element(page.getByRole('button', { name: 'Send chat message', exact: true })).toBeDisabled();
  await page.viewport(1280, 800);
  await expect.poll(() => page.getByRole('textbox', { name: 'Chat message', exact: true }).query()).toBeNull();
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

function areaTrackRow(name: RegExp = /^Project evidence\b/) {
  return page.getByRole('region', { name: 'Open tracks', exact: true }).getByRole('button', { name });
}

it('keeps workspace navigation as Area and Track lists with no filters', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture();
  await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  await expect.element(areaTrackRow()).toBeVisible();
  expect(page.getByRole('radio').query()).toBeNull();
  await page.getByRole('button', { name: 'Area actions', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'New track', exact: true })).toBeVisible();
  await expect.element(page.getByRole('menuitem', { name: 'Edit area Project', exact: true })).toBeVisible();
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('opens Workspace from the Today title when the report has no user Area', async () => {
  await page.viewport(390, 844);
  const { requests, router } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  await page.getByRole('button', { name: 'Switch track, 2026-10-04', exact: true }).click();
  await expect.element(page.getByRole('heading', { name: 'Workspace' })).toBeVisible();
  await page.getByRole('button', { name: 'Back to Today', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Switch track, 2026-10-04', exact: true })).toHaveFocus();
  expect(router.state.location.href).toBe('/?day=2026-10-04');
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it.each([320, 390])('keeps a long title readable in the document header and its Area switch visible at %ipx', async (width) => {
  await page.viewport(width, 844);
  const projectTitle = '手机端交互与视觉体验优化：工作区、历史对话、报告阅读与输入栏的完整设计';
  const { requests } = renderDailyFixture({ initial: '/track/project', projectTitle });
  const title = await page.getByRole('button', { name: 'Open area Project', exact: true }).findElement();
  expect(title.getBoundingClientRect().right).toBeLessThanOrEqual(width - 16);
  expect(title.getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
  const heading = await page.getByRole('heading', { name: projectTitle, exact: true }).findElement();
  expect(heading.getBoundingClientRect().height).toBeGreaterThan(parseFloat(getComputedStyle(heading).lineHeight));
  expect(heading.getBoundingClientRect().bottom).toBeLessThan(title.getBoundingClientRect().top);
  expect(heading.closest('header')).toBe(title.closest('header'));
  await page.getByRole('button', { name: 'Open area Project', exact: true }).click();
  await expect.element(areaTrackRow(new RegExp(`^${projectTitle}`))).toBeVisible();
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('keeps the fixed mobile editor above a reduced visible viewport without losing its draft', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  const editor = await page.getByRole('combobox', { name: 'Message', exact: true }).findElement();
  await expect.poll(() => document.activeElement === editor).toBe(true);
  await page.getByRole('combobox', { name: 'Message', exact: true }).fill('Keep this draft');
  const viewport = window.visualViewport!;
  const descriptor = Object.getOwnPropertyDescriptor(viewport, 'height');
  try {
    Object.defineProperty(viewport, 'height', { configurable: true, value: 480 });
    viewport.dispatchEvent(new Event('resize'));
    await expect.poll(() => editor.getBoundingClientRect().bottom).toBeLessThanOrEqual(480);
    const panel = document.querySelector<HTMLElement>('[data-nc-mobile-chat-panel]')!;
    const navigation = page.getByRole('button', { name: 'Back to Focus', exact: true }).element();
    await expect.poll(() => panel.getAnimations().every((animation) => animation.playState !== 'running')).toBe(true);
    await expect.poll(() => panel.getBoundingClientRect().top - navigation.getBoundingClientRect().bottom).toBeGreaterThanOrEqual(4);
    expect(document.activeElement).toBe(editor);
    await expect.element(page.getByRole('combobox', { name: 'Message', exact: true })).toHaveTextContent('Keep this draft');
  } finally {
    if (descriptor === undefined) Reflect.deleteProperty(viewport, 'height');
    else Object.defineProperty(viewport, 'height', descriptor);
    viewport.dispatchEvent(new Event('resize'));
  }
});

it('keeps the message viewport above a growing multiline composer', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true, reply: request => request.path.includes('/items?') ? ok(sessionPage(fixtureSession('daily-planner', 'daily'), [sessionItem('daily-planner', 1, 'Long reply. '.repeat(400) + '\n\nFinal line remains visible.')])) : undefined });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeEnabled();
  const footer = document.querySelector<HTMLElement>('[data-nc-chat-footer]')!;
  const finalLine = await page.getByText('Final line remains visible.', { exact: true }).findElement();
  const initialHeight = footer.getBoundingClientRect().height;
  await field.fill('First line\nSecond line\nThird line\nFourth line\nFifth line\nSixth line');
  await expect.poll(() => footer.getBoundingClientRect().height).toBeGreaterThan(initialHeight);
  const messages = document.querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  expect(messages.closest('dialog')).toBe(screen.getByRole('dialog'));
  expect(document.querySelectorAll('[data-nc-drawer-scroll]')).toHaveLength(1);
  await expect.poll(() => messages.getBoundingClientRect().bottom - footer.getBoundingClientRect().bottom).toBeLessThanOrEqual(1);
  await expect.poll(() => Number.parseFloat(getComputedStyle(messages).paddingBottom)).toBeGreaterThanOrEqual(footer.getBoundingClientRect().height);
  expect(Math.round(footer.getBoundingClientRect().bottom)).toBe(844);
  await expect.poll(() => finalLine.getBoundingClientRect().bottom).toBeLessThanOrEqual(footer.getBoundingClientRect().top);
});

it('loads real mobile CJK weight ranges and keeps shadowed controls crisp', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  const entry = await page.getByRole('textbox', { name: 'Chat message', exact: true }).findElement();
  const faces = await document.fonts.load('500 15px "Perga Mobile CJK"', '工作区手机对话');
  expect(faces.length).toBeGreaterThan(0);
  expect(faces.every(face => face.status === 'loaded' && face.weight === '400 700')).toBe(true);
  const surface = getComputedStyle(entry.closest('form')!);
  expect(surface.boxShadow).not.toBe('none');
  expect(surface.borderRadius).toBe('999px');
});


it.each(['/track/project', '/area/project-area/new'])('retains the draft after Back to its Area from %s', async (initial) => {
  await page.viewport(390, 844);
  const { router, requests } = renderDailyFixture({ initial });
  if (initial === '/track/project') {
    await page.getByRole('button', { name: 'Open area Project', exact: true }).click();
    await page.getByRole('button', { name: 'New track', exact: true }).click();
  }
  const draft = page.getByRole('combobox', { name: 'What this track should do', exact: true });
  await draft.fill('保留未提交的 Track 草稿');
  const back = page.getByRole('button', { name: 'Back to Project', exact: true });
  await back.click();
  await expect.element(areaTrackRow()).toBeVisible();
  expect(router.state.location.pathname).toBe('/area/project-area');
  await page.getByRole('button', { name: 'New track', exact: true }).click();
  await expect.element(draft).toHaveTextContent('保留未提交的 Track 草稿');
  await back.click();
  const label = initial === '/track/project' ? 'Back to Report' : 'Back to Focus';
  await page.getByRole('button', { name: label, exact: true }).click();
  await expect.poll(() => router.state.location.pathname).toBe(initial === '/track/project' ? '/track/project' : '/');
  expect(requests.filter((request) => request.method !== 'GET')).toHaveLength(0);
});

it('keeps Area editor actions inside the visible viewport when its keyboard opens', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture();
  await page.getByRole('button', { name: 'Open workspace', exact: true }).click();
  await page.getByRole('button', { name: 'New area', exact: true }).click();
  const dialog = await page.getByRole('dialog', { name: 'New area', exact: true }).findElement();
  const name = await page.getByRole('textbox', { name: /^Name/ }).findElement();
  await expect.poll(() => document.activeElement === name).toBe(true);
  await expect.poll(() => dialog.getAnimations().every(animation => animation.playState !== 'running')).toBe(true);
  const viewport = window.visualViewport!;
  const descriptors = ['height', 'offsetTop'].map(key => Object.getOwnPropertyDescriptor(viewport, key));
  try {
    for (const offsetTop of [0, 70]) {
      Object.defineProperty(viewport, 'height', { configurable: true, value: 480 });
      Object.defineProperty(viewport, 'offsetTop', { configurable: true, value: offsetTop });
      viewport.dispatchEvent(new Event('resize'));
      await expect.poll(() => dialog.getBoundingClientRect().bottom).toBeLessThanOrEqual(480 + offsetTop);
      expect(dialog.getBoundingClientRect().top).toBeGreaterThanOrEqual(offsetTop);
      expect(page.getByRole('button', { name: 'Cancel', exact: true }).element().getBoundingClientRect().bottom).toBeLessThanOrEqual(480 + offsetTop);
      expect(document.activeElement).toBe(name);
    }
  } finally {
    ['height', 'offsetTop'].forEach((key, index) => {
      const descriptor = descriptors[index];
      if (descriptor === undefined) Reflect.deleteProperty(viewport, key);
      else Object.defineProperty(viewport, key, descriptor);
    });
    viewport.dispatchEvent(new Event('resize'));
  }
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect.element(page.getByRole('region', { name: 'Areas', exact: true })).toBeVisible();
  expect(requests.filter(request => request.method !== 'GET')).toHaveLength(0);
});


it('interrupts the current working conversation from its portalled mobile editor without dismissing it', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true, reply: request => {
    if (request.path.includes('/items?')) return ok(sessionPage(fixtureSession('daily-planner', 'daily'), [], sessionTurn('daily-planner')));
    if (request.path.endsWith('/control')) return ok({ card_id: 'daily-planner', worker_session_id: 'runtime', stopped: true });
    return undefined;
  } });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  page.getByRole('button', { name: 'Stop', exact: true }).element().focus();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => requests.filter(request => request.path === '/api/v1/sessions/daily-planner/control')).toHaveLength(1);
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
});

it('uses the shared dismissal stack for idle portalled editors and leaves IME Escape alone', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true });
  await expect.element(dialog).toBeVisible();
  const editor = await page.getByRole('combobox', { name: 'Message', exact: true }).findElement();
  editor.focus();
  for (const properties of [{ isComposing: true }, { keyCode: 229 }]) {
    editor.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true, ...properties }));
    // Do not mistake a still-visible exit animation for an IME-safe open host.
    await expect.poll(() => dialog.element().getAnimations({ subtree: true }).every(animation => animation.playState !== 'running')).toBe(true);
    expect((dialog.element() as HTMLDialogElement).open).toBe(true);
    expect(document.querySelector('[data-nc-mobile-chat-panel]')?.closest('[inert]')).toBeNull();
    await expect.element(dialog).toBeVisible();
  }
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => document.querySelector('[data-nc-mobile-chat-panel]')?.closest('dialog')?.open).toBe(false);
});

it('preserves the footer DOM and caret across native and desktop presentations then returns focus after exit', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04', emptyWorkspace: true });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  await field.fill('Retained caret draft');
  const editor = field.element() as HTMLElement;
  const footer = editor.closest('[data-nc-conversation-region]')!;
  const text = editor.firstChild!;
  const range = document.createRange();
  range.setStart(text, 5); range.collapse(true);
  document.getSelection()!.removeAllRanges(); document.getSelection()!.addRange(range);
  for (const width of [1280, 390, 1280, 390]) {
    await page.viewport(width, 844);
    await expect.poll(() => Boolean(editor.closest('dialog'))).toBe(width === 390);
    expect(field.element()).toBe(editor);
    expect(editor.closest('[data-nc-conversation-region]')).toBe(footer);
    await expect.poll(() => document.activeElement).toBe(editor);
    await expect.poll(() => document.getSelection()?.anchorNode).toBe(text);
    await expect.poll(() => document.getSelection()?.anchorOffset, { message: `Caret restored at ${width}px` }).toBe(5);
    expect(editor.textContent).toBe('Retained caret draft');
  }
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  await expect.poll(() => document.activeElement?.hasAttribute('data-nc-chat-dock')).toBe(true);
  await expect.element(page.getByRole('textbox', { name: 'Chat message', exact: true })).toHaveValue('Retained caret draft');
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  await expect.element(field).toHaveTextContent('Retained caret draft');
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => document.querySelector('dialog[open]')).toBeNull();
  await expect.poll(() => document.activeElement?.hasAttribute('data-nc-chat-dock')).toBe(true);
});

function RetainedEditor({ native, change, duringFocus }: { native: boolean; change: (editor: HTMLDivElement) => void; duringFocus: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { if (!native && !duringFocus) change(ref.current!); }, [native, change, duringFocus]);
  return <div ref={ref} contentEditable suppressContentEditableWarning role="textbox" aria-label="Retained editor"
    onFocus={() => { if (!native && duringFocus) change(ref.current!); }}><span>abcdef</span><span>uvwxyz</span></div>;
}

it.each(['normal text', 'normal children', 'shorter anchor', 'shorter focus', 'fewer children', 'detached boundary', 'focus changes text'] as const)(
  'consumes the portal selection safely: %s', async (scenario) => {
    await page.viewport(390, 844);
    const errors: string[] = [];
    const onError = (event: ErrorEvent) => { errors.push(event.message); event.preventDefault(); };
    window.addEventListener('error', onError);
    const change = (editor: HTMLDivElement) => {
      if (scenario === 'shorter anchor' || scenario === 'focus changes text') (editor.firstChild!.firstChild! as Text).data = 'a';
      if (scenario === 'shorter focus') (editor.lastChild!.firstChild! as Text).data = 'z';
      if (scenario === 'fewer children' || scenario === 'detached boundary') editor.lastChild!.remove();
    };
    const surface = (native: boolean) => <ConversationSurface open mobileSheet={native} title="Retained conversation" onClose={() => {}}
      footer={<RetainedEditor native={native} change={change} duringFocus={scenario === 'focus changes text'} />}><p>Messages</p></ConversationSurface>;
    const view = render(surface(true));
    try {
      const editor = await page.getByRole('textbox', { name: 'Retained editor' }).findElement() as HTMLDivElement;
      const dialog = editor.closest('dialog')!;
      await expect.poll(() => dialog.open).toBe(true);
      editor.focus();
      const children = [...editor.childNodes];
      const containerBoundary = scenario === 'normal children' || scenario === 'fewer children';
      const anchor = containerBoundary ? editor : editor.firstChild!.firstChild!;
      const focus = containerBoundary ? editor : editor.lastChild!.firstChild!;
      const anchorOffset = containerBoundary ? 2 : 5;
      const focusOffset = containerBoundary ? 1 : 4;
      const selection = document.getSelection()!;
      selection.setBaseAndExtent(anchor, anchorOffset, focus, focusOffset);
      const restore = vi.spyOn(selection, 'setBaseAndExtent');
      try {
        view.rerender(surface(false));
        await expect.poll(() => dialog.open).toBe(false);
        await expect.poll(() => document.activeElement).toBe(editor);
        expect(page.getByRole('textbox', { name: 'Retained editor' }).element()).toBe(editor);
        if (!containerBoundary) expect(editor.firstChild!.firstChild).toBe(anchor);
        expect(errors).toEqual([]);
        if (scenario.startsWith('normal')) {
          expect(restore).toHaveBeenCalledExactlyOnceWith(anchor, anchorOffset, focus, focusOffset);
          expect([selection.anchorNode, selection.anchorOffset, selection.focusNode, selection.focusOffset]).toEqual([anchor, anchorOffset, focus, focusOffset]);
        } else {
          expect(restore).not.toHaveBeenCalled();
          // Make the old boundaries valid again: a consumed snapshot must still
          // stay discarded rather than resurrecting on a later notification.
          (children[0].firstChild! as Text).data = 'abcdef';
          (children[1].firstChild! as Text).data = 'uvwxyz';
          editor.replaceChildren(...children);
          dialog.setAttribute('open', ''); dialog.removeAttribute('open');
          await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
          expect(restore).not.toHaveBeenCalled();
          expect(errors).toEqual([]);
        }
      } finally { restore.mockRestore(); }
    } finally { window.removeEventListener('error', onError); view.unmount(); }
  },
);

it('keeps a rapid native reopen focused inside and restores the opener only after the final exit', async () => {
  await page.viewport(390, 844);
  const opener = document.body.appendChild(document.createElement('button'));
  opener.textContent = 'Surface opener';
  opener.focus();
  const surface = (open: boolean) => <ConversationSurface open={open} mobileSheet title="Rapid conversation" onClose={() => {}}
    footer={<input aria-label="Rapid draft" />}><p>Messages</p></ConversationSurface>;
  const view = render(surface(true));
  try {
    const dialog = page.getByRole('dialog', { name: 'Rapid conversation' }).element() as HTMLDialogElement;
    const editor = page.getByRole('textbox', { name: 'Rapid draft' }).element() as HTMLInputElement;
    await expect.poll(() => dialog.open).toBe(true);
    // Retention requires entered paint: paused entry zero is already the closed endpoint.
    const entry = dialog.getAnimations({ subtree: true })[0]; entry.pause(); await entry.ready; entry.currentTime = 80;
    editor.focus();
    view.rerender(surface(false));
    expect(dialog.open).toBe(true);
    view.rerender(surface(true));
    editor.focus();
    await expect.poll(() => dialog.getAnimations({ subtree: true }).every(animation => animation.playState !== 'running')).toBe(true);
    expect(dialog.open).toBe(true);
    expect(document.activeElement).toBe(editor);
    view.rerender(surface(false));
    await expect.poll(() => dialog.open).toBe(false);
    await expect.poll(() => document.activeElement).toBe(opener);
  } finally { view.unmount(); opener.remove(); }
});
