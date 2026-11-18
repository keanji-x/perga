import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import '../../styles/entry.css';
// #2348 A4 in a real browser: a Planner conversation's approval setting beside the model picker, and a
// paused turn's request in the Questions drawer, through the production router.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort, ApiTransportResponse } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { APP_BASEPATH, createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

afterEach(async () => {
  cleanup(); document.getElementById('root')?.remove(); delete document.documentElement.dataset.theme;
  await page.viewport(1280, 720);
});

const TRACK = { id: 'approvals-track', areaId: 'area', title: 'Release 2.0', cwd: '/tmp',
  workspaceDriver: 'direct', leadSessionId: 'planner', createdAtMs: 1, updatedAtMs: 2 };
const PLANNER = { id: 'planner', trackId: TRACK.id, isLead: true, provider: 'codex', title: 'Planner',
  model: null, reasoningEffort: null, createdAtMs: 1, updatedAtMs: 2 };
const HOLD_TITLE = 'Run `cargo test -p calm-server` (cwd /work/neige)?';
const HOLD = {
  source: 'ask', key: 'ask:12', text: HOLD_TITLE, at_ms: 5, ask_id: 12,
  questions: [{ title: HOLD_TITLE, options: ['Allow', 'Allow for this session', 'Deny'] }],
  delivery: 'hold',
};

type Mode = 'never' | 'ask' | 'full' | null;
type Options = Readonly<{
  theme?: 'light' | 'dark';
  mode?: Mode;
  asks?: readonly (typeof HOLD)[];
  /** The answer to `PUT /api/v1/sessions/{id}/permission-mode`; by default it stores the mode asked for. */
  putMode?: (body: { permission_mode: Mode }) => ApiTransportResponse;
  /** The answer to the hold ask's answer route. */
  answer?: () => ApiTransportResponse;
}>;

const answered = (status: number, body: unknown): ApiTransportResponse => ({
  status, statusText: status === 200 ? 'OK' : status === 204 ? 'No Content' : 'Error', body,
});

function setup({ theme = 'light', mode = 'never', asks = [], putMode, answer }: Options = {}) {
  const requests: ApiRequest[] = [];
  let stored: Mode = mode;
  const transport: ApiTransportPort = { async send(request) {
    await Promise.resolve();
    requests.push(request);
    let body: unknown = [];
    const session = { ...PLANNER, permissionMode: stored };
    const bootstrap = { version: 'test', providers: [{ provider: 'codex', available: true,
      models: [], permissionModes: [
        { id: 'never', label: 'Never', description: 'Sandbox only; never asks.', tone: 'neutral', isDefault: true },
        { id: 'ask', label: 'Ask me', description: 'Pauses the turn to ask you.', tone: 'warning', isDefault: false },
        { id: 'full', label: 'Full access', description: "No sandbox; never asks. Runs with your account's access.", tone: 'danger', isDefault: false },
      ] }] };
    const askDtos = asks.map(ask => ({ id: String(ask.ask_id), trackId: TRACK.id, status: 'open',
      questions: ask.questions.map(question => ({ question: question.title, options: question.options, isMultiSelect: false })),
      delivery: ask.delivery, sessionId: PLANNER.id, turnId: ask.delivery === 'hold' ? 'turn-1' : null,
      answer: null, createdAtMs: ask.at_ms, answeredAtMs: null }));
    if (request.path.startsWith('/api/v1/bootstrap')) body = bootstrap;
    if (request.path === '/api/v1/areas') body = [{ id: 'area', name: 'Work', color: '#123456', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === '/api/v1/areas/area/tracks?includeClosed=true') body = [TRACK];
    if (request.path === '/api/settings') body = {};
    if (request.path === `/api/v1/tracks/${TRACK.id}`) body = { track: TRACK,
      report: { docRev: 0, summary: '', blocks: [] }, tasks: [], sessions: [session], openAsks: askDtos };
    if (request.path === `/api/v1/tracks/${TRACK.id}/sessions`) body = [session];
    if (request.path.startsWith(`/api/v1/sessions/${PLANNER.id}/items?`)) body = { session,
      unknownInput: null, activeTurn: asks.length > 0
        ? { id: 'turn-1', sessionId: PLANNER.id, status: 'running', startedAtMs: Date.now() - 42_000 } : null,
      items: [{ id: 1, sessionId: PLANNER.id, turnId: 'previous-turn', kind: 'assistant_text',
        payload: { text: 'The release checklist is green. Running the server tests next.' }, createdAtMs: 1 }] };
    if (request.method === 'PUT' && request.path === `/api/v1/sessions/${PLANNER.id}/permission-mode`) {
      const sent = request.body as { permission_mode: Mode };
      const response = putMode?.(sent) ?? answered(200, { ...session, permissionMode: sent.permission_mode });
      if (response.status === 200) stored = sent.permission_mode;
      return response;
    }
    if (request.method === 'POST' && request.path === `/api/v1/tracks/${TRACK.id}/asks/12/answer`)
      return answer?.() ?? answered(200, { ...askDtos.find(ask => ask.id === '12'), status: 'answered',
        answer: request.body, answeredAtMs: 6 });
    return { status: 200, statusText: 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }), onSignOut: () => undefined });
  router.update({ history: createMemoryHistory({ initialEntries: [`${APP_BASEPATH}/track/${TRACK.id}?panel=conversations`] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => theme, setItem: () => {} }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>, { container });
  return { requests };
}

const modeWrites = (requests: readonly ApiRequest[]) => requests.filter((request) => request.method === 'PUT'
  && request.path.endsWith('/permission-mode'));
const answers = (requests: readonly ApiRequest[]) => requests.filter((request) => request.path.endsWith('/answer'));

function expectNoHorizontalScroll() {
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
}

/** A phone keeps the composer's controls behind its options button; a desktop shows them in its footer. */
async function revealComposerControls() {
  const options = document.querySelector<HTMLElement>('summary[aria-label="对话选项"]');
  if (options === null) return;
  options.click();
  await expect.poll(() => options.closest('details')?.open).toBe(true);
}

type Rgba = readonly [number, number, number, number];

/** Unpremultiplied Porter-Duff source-over, in CSS's encoded sRGB space. */
function sourceOver(source: Rgba, backdrop: Rgba): Rgba {
  const alpha = source[3] + backdrop[3] * (1 - source[3]);
  if (alpha === 0) return [0, 0, 0, 0];
  const channel = (index: 0 | 1 | 2) =>
    (source[index] * source[3] + backdrop[index] * backdrop[3] * (1 - source[3])) / alpha;
  return [channel(0), channel(1), channel(2), alpha];
}

function paint(color: string): Rgba {
  const context = document.createElement('canvas').getContext('2d')!;
  context.fillStyle = color;
  context.fillRect(0, 0, 1, 1);
  const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data;
  return [r / 255, g / 255, b / 255, a / 255];
}

function ink(element: Element, token: string): string {
  const probe = document.createElement('span');
  probe.style.color = `var(${token})`;
  element.append(probe);
  const color = getComputedStyle(probe).color;
  probe.remove();
  return color;
}

/** Paint the glyph and its empty backdrop separately. Each CSS opacity applies
 * to the whole local group AFTER its text and background have been composited,
 * before that group is placed over the parent's background (W3C compositing).
 * Uniform gradients are Astryx's interaction overlays; reject any unknown image.
 */
function textContrast(element: Element): number {
  let glyph = paint(getComputedStyle(element).color);
  let empty: Rgba = [0, 0, 0, 0];
  for (let node: Element | null = element; node !== null; node = node.parentElement) {
    const style = getComputedStyle(node);
    let background = paint(style.backgroundColor);
    if (style.backgroundImage !== 'none') {
      const gradient = style.backgroundImage.match(/^linear-gradient\((.+), \1\)$/);
      expect(gradient, style.backgroundImage).not.toBeNull();
      background = sourceOver(paint(gradient![1]), background);
    }
    glyph = sourceOver(glyph, background);
    empty = sourceOver(empty, background);
    const opacity = Number(style.opacity);
    glyph = [glyph[0], glyph[1], glyph[2], glyph[3] * opacity];
    empty = [empty[0], empty[1], empty[2], empty[3] * opacity];
  }
  const luminance = (color: Rgba) => {
    const linear = color.slice(0, 3).map((v) => v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
    return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
  };
  const canvas: Rgba = [1, 1, 1, 1];
  const values = [luminance(sourceOver(glyph, canvas)), luminance(sourceOver(empty, canvas))];
  return (Math.max(...values) + 0.05) / (Math.min(...values) + 0.05);
}

it('measures group opacity after composing text and background', () => {
  const backdrop = document.createElement('div');
  backdrop.style.backgroundColor = 'black';
  const group = document.createElement('span');
  group.style.cssText = 'background-color: white; color: black; opacity: 0.5';
  group.textContent = 'Ink';
  backdrop.append(group);
  document.body.append(backdrop);
  try {
    /* Half-opacity black-on-white over black paints black text on sRGB 0.5 gray:
     * (linear(0.5) + 0.05) / 0.05 = 5.2808, not independently dimmed text. */
    expect(textContrast(group)).toBeCloseTo(5.2808, 4);
  } finally { backdrop.remove(); }
});

async function settled(element: Element) {
  /* Flush style before collecting CSS transitions, then measure their final ink. */
  getComputedStyle(element).getPropertyValue('background-image');
  await Promise.all(element.getAnimations({ subtree: true }).map((animation) => animation.finished));
}

it.each(['light', 'dark'] as const)('keeps borderless split-tone ink legible in every interaction (%s)', async (theme) => {
  await page.viewport(1280, 800);
  setup({ theme });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const restingInks: string[] = [];
  for (const [label, tone, token] of [
    ['Never', 'neutral', '--text-3'], ['Ask me', 'warning', '--warn-text'], ['Full access', 'danger', '--error-text'],
  ] as const) {
    const trigger = page.getByRole('button', { name: `Approvals: ${label}` });
    const button = await trigger.findElement();
    const value = button.querySelector<HTMLElement>('span[data-nc-tone]')!;
    const prefix = value.parentElement!;
    await page.getByRole('button', { name: /^Model:/ }).hover();
    button.blur();
    await settled(button);
    expect(button.getAttribute('data-variant')).toBe('ghost');
    expect(button.querySelectorAll('svg')).toHaveLength(0);
    expect(getComputedStyle(button).borderTopWidth).toBe('0px');
    expect(paint(getComputedStyle(button).backgroundColor)[3]).toBe(0);
    expect(getComputedStyle(button).backgroundImage).toBe('none');
    expect(value.dataset.ncTone).toBe(tone);
    expect(prefix.firstChild?.textContent).toBe('Approvals: ');
    expect(getComputedStyle(prefix).color).toBe(ink(button, '--text-3'));
    const foreground = getComputedStyle(value).color;
    expect(foreground).toBe(ink(button, token));
    if (tone === 'neutral') expect(foreground).toBe(getComputedStyle(prefix).color);
    else {
      expect(foreground).not.toBe(getComputedStyle(prefix).color);
      expect(getComputedStyle(value).fontWeight).toBe('500');
    }
    restingInks.push(foreground);
    await page.screenshot({ path: `./__screenshots__/approvals-ink-${theme}-${tone}.png` });
    const check = (state: string) => {
      for (const text of [prefix, value]) expect(textContrast(text), `${theme} ${label} ${state} ${text.textContent}`).toBeGreaterThanOrEqual(4.5);
    };
    check('rest');
    await trigger.hover();
    await settled(button);
    expect(button.matches(':hover')).toBe(true);
    expect(getComputedStyle(prefix).color).toBe(ink(button, '--text'));
    check('hover');
    try {
      await commands.mouseButton(`button[aria-label="Approvals: ${label}"]`, true);
      await settled(button);
      expect(button.matches(':active')).toBe(true);
      check('active');
    } finally { await commands.mouseButton('body', false); }
    await expect.element(trigger).toHaveAttribute('aria-expanded', 'true');
    await page.getByRole('button', { name: /^Model:/ }).hover();
    await settled(button);
    expect(getComputedStyle(prefix).color).toBe(ink(button, '--text'));
    check('expanded');

    const menu = page.getByRole('menu');
    const full = menu.getByRole('menuitem', { name: /^Full access/ });
    const item = await full.findElement();
    const title = await full.getByText('Full access', { exact: true }).findElement();
    const description = await full.getByText("No sandbox; never asks. Runs with your account's access.", { exact: true }).findElement();
    expect(item.getAttribute('data-variant')).toBe('destructive');
    const checkMenu = (state: string) => {
      for (const text of [title, description]) {
        expect(getComputedStyle(text).color).toBe(ink(item, '--error-text'));
        expect(textContrast(text), `${theme} Full access menu ${state} ${text.textContent}`).toBeGreaterThanOrEqual(4.5);
      }
    };
    checkMenu('rest');
    if (tone === 'danger') await page.screenshot({ path: `./__screenshots__/approvals-menu-${theme}.png` });
    await full.hover();
    await settled(item);
    checkMenu('hover');
    try {
      await commands.mouseButton('[role="menuitem"][data-variant="destructive"]', true);
      await settled(item);
      expect(item.matches(':active')).toBe(true);
      checkMenu('active');
      /* Move away before release so this contrast probe does not pick Full access. */
      await page.getByRole('button', { name: /^Model:/ }).hover();
    } finally { await commands.mouseButton('body', false); }
    const next = label === 'Never' ? 'Ask me' : label === 'Ask me' ? 'Full access' : null;
    if (next !== null) await menu.getByRole('menuitem', { name: new RegExp(`^${next}`) }).click();
    else await userEvent.keyboard('{Escape}');
  }
  expect(new Set(restingInks).size).toBe(3);
});

it.each([
  ['light', 1280], ['dark', 1280], ['light', 390], ['dark', 390],
] as const)('reads the stored mode, and a pick is written and read back (%s, %ipx)', async (theme, width) => {
  await page.viewport(width, width < 600 ? 844 : 800);
  const { requests } = setup({ theme });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toBeVisible();
  if (width < 600) await revealComposerControls();
  const trigger = page.getByRole('button', { name: 'Approvals: Never' });
  await expect.element(trigger).toBeVisible();
  /* Beside the model picker, inside the composer, and the composer's Send still inside the viewport. */
  const triggerElement = await trigger.findElement();
  expect(triggerElement.closest('[data-nc-composer]')).not.toBeNull();
  const model = (await page.getByRole('button', { name: /^Model:/ }).findElement()).getBoundingClientRect();
  const box = triggerElement.getBoundingClientRect();
  expect(Math.abs(box.top - model.top)).toBeLessThan(4);
  expect(box.left).toBeGreaterThanOrEqual(model.right);
  expect(box.right).toBeLessThanOrEqual(window.innerWidth);
  expectNoHorizontalScroll();

  await trigger.click();
  const menu = page.getByRole('menu');
  await expect.element(menu).toBeVisible();
  const bounds = (await menu.findElement()).getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(window.innerWidth);
  await expect.element(menu.getByRole('note')).toHaveTextContent('Applies from the next turn.');
  await menu.getByRole('menuitem', { name: /^Ask me/ }).click();

  await expect.poll(() => modeWrites(requests).length).toBe(1);
  expect(modeWrites(requests)[0]?.body).toEqual({ permission_mode: 'ask' });
  await expect.element(page.getByRole('button', { name: 'Approvals: Ask me' })).toBeVisible();
  expectNoHorizontalScroll();
});

it.each([
  ['light', 1280], ['dark', 1280], ['light', 390], ['dark', 390],
] as const)('offers Full access as a third mode and writes it (#2441, %s, %ipx)', async (theme, width) => {
  await page.viewport(width, width < 600 ? 844 : 800);
  const { requests } = setup({ theme });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toBeVisible();
  if (width < 600) await revealComposerControls();
  await page.getByRole('button', { name: 'Approvals: Never' }).click();
  const menu = page.getByRole('menu');
  await expect.element(menu).toBeVisible();
  const items = (await Promise.all(menu.getByRole('menuitem').all().map((item) => item.findElement())))
    .map((item) => item.textContent);
  expect(items).toEqual([
    expect.stringMatching(/^Never.*Sandbox only; never asks\./),
    expect.stringMatching(/^Ask me.*Pauses the turn to ask you\./),
    expect.stringMatching(/^Full access.*No sandbox; never asks\. Runs with your account's access\./),
  ]);
  const bounds = (await menu.findElement()).getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(window.innerWidth);
  /* The hint says what Full access risks, so it wraps rather than ending in an ellipsis. */
  const full = await menu.getByRole('menuitem', { name: /^Full access/ }).findElement();
  for (const text of full.querySelectorAll('span')) expect(text.scrollWidth).toBeLessThanOrEqual(text.clientWidth);
  await menu.getByRole('menuitem', { name: /^Full access/ }).click();

  await expect.poll(() => modeWrites(requests).length).toBe(1);
  expect(modeWrites(requests)[0]?.body).toEqual({ permission_mode: 'full' });
  await expect.element(page.getByRole('button', { name: 'Approvals: Full access' })).toBeVisible();
  expectNoHorizontalScroll();
});

it.each([
  ['a refusal', answered(403, { code: 'forbidden', error: 'Only you can change what the Planner may do.' }),
    'Only you can change what the Planner may do.'],
  ['an unknown outcome', answered(500, { code: 'internal', error: 'database is locked' }),
    'The approval setting change is unconfirmed.'],
] as const)('says so when a change fails with %s, and keeps showing the stored mode', async (_name, response, shown) => {
  await page.viewport(1280, 800);
  const { requests } = setup({ putMode: () => response });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  await page.getByRole('button', { name: 'Approvals: Never' }).click();
  await page.getByRole('menu').getByRole('menuitem', { name: /^Ask me/ }).click();
  await expect.poll(() => modeWrites(requests).length).toBe(1);
  await expect.element(page.getByText(shown)).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Approvals: Never' })).toBeVisible();
});

it('shows no approval setting for a conversation that is not a Planner', async () => {
  await page.viewport(1280, 800);
  setup({ mode: null });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  await expect.element(page.getByRole('button', { name: /^Model:/ })).toBeVisible();
  expect(page.getByRole('button', { name: /^Approvals:/ }).query()).toBeNull();
});

it.each([
  ['light', 1280], ['dark', 1280], ['light', 390], ['dark', 390],
] as const)('puts a paused turn\'s request to the reader as options only (%s, %ipx)', async (theme, width) => {
  await page.viewport(width, width < 600 ? 844 : 800);
  const { requests } = setup({ theme, mode: 'ask', asks: [HOLD] });
  /* The Notifications row says what waits, and offers no Dismiss: only an answer ends it. */
  const notice = page.getByRole('region', { name: 'Notifications' });
  if (width >= 600) {
    await expect.element(notice.getByText('Waiting for your approval')).toBeVisible();
    expect(notice.getByRole('button', { name: /^Dismiss/ }).query()).toBeNull();
  }
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(ask).toBeVisible();
  expect((await ask.findElement()).closest('[data-nc-composer]')).not.toBeNull();
  await expect.element(ask.getByText('Turn paused, waiting for your approval')).toBeVisible();
  await expect.element(ask.getByRole('heading', { name: HOLD_TITLE })).toBeVisible();
  expect(ask.getByRole('textbox').query()).toBeNull();
  expect(ask.getByRole('button', { name: 'Answer' }).query()).toBeNull();
  expect(ask.getByRole('button', { name: /^Dismiss/ }).query()).toBeNull();
  expectNoHorizontalScroll();
  const field = (await page.getByRole('combobox', { name: 'Message' }).findElement()).getBoundingClientRect();
  expect(field.bottom).toBeLessThanOrEqual(window.innerHeight);

  await ask.getByRole('button', { name: 'Allow for this session' }).click();
  await expect.poll(() => answers(requests).length).toBe(1);
  expect(answers(requests)[0]?.path).toBe(`/api/v1/tracks/${TRACK.id}/asks/12/answer`);
  expect(answers(requests)[0]?.body).toEqual({ answers: [{ option: 1, value: 'Allow for this session' }] });
  await expect.element(ask).not.toBeInTheDocument();
});

it('says a request that went away is no longer pending', async () => {
  await page.viewport(1280, 800);
  const { requests } = setup({ mode: 'ask', asks: [HOLD],
    answer: () => answered(409, { code: 'conflict', error: 'ask 12 is no longer open: its paused request is gone' }) });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await ask.getByRole('button', { name: 'Allow', exact: true }).click();
  await expect.poll(() => answers(requests).length).toBe(1);
  await expect.element(ask.getByText('This request is no longer pending.')).toBeVisible();
  expect(page.getByText(/its paused request is gone/).query()).toBeNull();
  await expect.element(ask.getByRole('heading', { name: HOLD_TITLE })).toBeVisible();
});

it('shows a paused turn\'s request ahead of an older question, since the turn is blocked on it', async () => {
  await page.viewport(1280, 800);
  const wake = { source: 'ask', key: 'ask:7', text: 'Which branch?', at_ms: 4, ask_id: 7,
    questions: [{ title: 'Which branch?', options: ['main', 'release'] }], delivery: 'wake' };
  setup({ mode: 'ask', asks: [HOLD, wake] });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(ask.getByText('Turn paused, waiting for your approval')).toBeVisible();
  await expect.element(ask.getByRole('heading', { name: HOLD_TITLE })).toBeVisible();
  await expect.element(page.getByRole('button', { name: '1 more ask' })).toBeVisible();
});
