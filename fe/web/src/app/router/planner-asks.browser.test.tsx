import type { AskQuestion } from '../../../../core/domain/presentation-types.ts';
import { fixtureTrack, fixtureSession, fixtureSnapshot } from './session-contract-fixture.ts';
import { sessionPage, sessionItem } from './conversation-session-data.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import '../../styles/entry.css';
// #2209 U3 in a real browser: a two-question ask above the Planner composer, answered and gone.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { cleanup, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createAppRouter } from './public.tsx';
import { bootTestCardRuntime } from './test-card-runtime.ts';
import { ThemeProvider } from '../theme/public.tsx';

afterEach(async () => {
  cleanup(); document.getElementById('root')?.remove(); delete document.documentElement.dataset.theme;
  await page.viewport(1280, 720);
});

const TRACK = fixtureTrack('asks-track', { areaId: 'area', title: 'Release 2.0', leadSessionId: 'planner' });
const PLANNER = fixtureSession('planner', TRACK.id, { title: 'Planner', isLead: true });
const ASK = {
  source: 'ask', key: 'ask:7', text: 'Which branch should I release from? / Anything to tell the reviewers?', at_ms: 5, ask_id: 7,
  questions: [
    { title: 'Which branch should I release from?', options: ['main', 'release/2.0', 'A new branch from the last tag'] },
    { title: 'Anything to tell the reviewers?', options: [] },
  ],
  delivery: 'wake',
};

/** An ask of `count` questions of `count` options each, `long` making every option the server's 200 characters. */
function largeAsk(count: number, long: boolean) {
  const questions = Array.from({ length: count }, (_, question) => ({
    title: `Question ${question + 1}: ${long ? 'which of these release plans should I follow, given what the checklist found? '.repeat(3) : 'which plan?'}`,
    options: Array.from({ length: count }, (_, option) => {
      const text = `Plan ${question + 1}.${option + 1} `;
      return long ? `${text}${'keep the branch, tag it, and write the notes before the docs land. '.repeat(4)}`.slice(0, 200) : text.trim();
    }),
  }));
  /* A short notification text: the kernel's joined titles would expand the Notifications aside over the conversation
     row this test opens, which is that aside's layout, not the drawer's. */
  return { ...ASK, key: 'ask:7', text: `${count} questions`, questions };
}

function setup(theme: 'light' | 'dark', ask: Omit<typeof ASK, 'questions'> & { questions: AskQuestion[] } = ASK) {
  const requests: ApiRequest[] = [];

  const transport: ApiTransportPort = { async send(request) {
    await Promise.resolve();
    requests.push(request);
    let body: unknown = [];
    let status = 200;
    if (request.path === '/api/v1/areas') body = [{ id: 'area', name: 'Work', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }];
    if (request.path === '/api/v1/areas/area/tracks?includeClosed=true') body = [TRACK];
    if (request.path === '/api/settings') body = {};
    if (request.path === `/api/v1/tracks/${TRACK.id}`) body = {
      ...fixtureSnapshot(TRACK, [PLANNER]), openAsks: [{ id: '7', trackId: TRACK.id, status: 'open', delivery: 'wake', questions: ask.questions.map(question => ({ question: question.title, options: question.options, isMultiSelect: false })), createdAtMs: 5 }],
    };
    if (request.path.includes('/items?')) body = { ...sessionPage(PLANNER, [sessionItem(PLANNER.id, 1, 'The release checklist is green. Two things before I tag it.')]), pendingInteractions: [{ id: '7', kind: 'ask', sessionId: PLANNER.id, title: 'Release questions', canRespond: true, createdAtMs: 5, questions: ask.questions.map(question => ({ question: question.title + (question.detail === undefined ? '' : `\n${question.detail}`), options: question.options, isMultiSelect: false })) }] };
    if (request.path === `/api/v1/sessions/${PLANNER.id}/interactions/7/respond`) { body = { id: '7', kind: 'ask' }; status = 200; }
    return { status, statusText: status === 204 ? 'No Content' : status === 201 ? 'Created' : 'OK', body };
  } };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter({ prepareReport: prepareReportDocument, transport, client, cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }) });
  router.update({ history: createMemoryHistory({ initialEntries: [`/track/${TRACK.id}?panel=conversations`] }) });
  const container = document.createElement('div'); container.id = 'root'; document.body.append(container);
  render(<QueryClientProvider client={client}><ThemeProvider storage={{ getItem: () => theme, setItem: () => {} }}>
    <RouterProvider router={router} />
  </ThemeProvider></QueryClientProvider>, { container });
  return { requests };
}

it.each([
  ['light', 1280], ['dark', 1280], ['light', 390], ['dark', 390],
] as const)('answers sequential questions inside the real Planner conversation in %s at %ipx', async (theme, width) => {
  await page.viewport(width, width < 600 ? 844 : 800);
  const { requests } = setup(theme);
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(ask).toBeVisible();
  expect((await ask.findElement()).closest('[data-nc-conversation-action="ask"]')).not.toBeNull();
  const questionElement = await ask.findElement();
  await expect.element(ask.getByRole('button', { name: 'Other…' })).toBeVisible();
  expect(getComputedStyle(questionElement.querySelector('h3')!).fontSize).toBe('16px');
  expect(getComputedStyle(await ask.getByRole('button', { name: 'release/2.0' }).findElement()).fontSize).toBe('14px');

  await ask.getByRole('button', { name: 'release/2.0' }).click();
  expect(requests.filter(request => request.path.endsWith('/respond'))).toHaveLength(0);
  const answerLine = await ask.getByRole('group', { name: 'Your answer' }).findElement();
  expect(getComputedStyle(answerLine).borderBottomWidth).toBe('1px');
  const own = ask.getByRole('textbox', { name: 'Anything to tell the reviewers?' });
  await expect.element(own).toHaveFocus();
  await own.fill('Tag it after the docs land.');
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  await ask.getByRole('button', { name: 'Answer' }).click();
  await expect.poll(() => requests.filter(request => request.path.endsWith('/respond')).length).toBe(1);
  const answer = requests.find(request => request.path.endsWith('/respond'));
  expect(answer?.method).toBe('POST');
  expect(answer?.path).toBe(`/api/v1/sessions/${PLANNER.id}/interactions/7/respond`);
  expect(answer?.body).toEqual({ answers: [{ option: 1, value: 'release/2.0' }, { text: 'Tag it after the docs land.', value: 'Tag it after the docs land.' }] });
  await expect.element(ask).not.toBeInTheDocument();
  await expect.element(page.getByRole('combobox', { name: 'Message' })).toBeVisible();
});

it.each([1280, 390])('answers custom questions with Enter inside the real conversation at %ipx', async width => {
  await page.viewport(width, 844);
  const { requests } = setup('light');
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const message = page.getByRole('combobox', { name: 'Message' });
  await message.fill('Keep this message draft');
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await ask.getByRole('button', { name: 'Other…' }).click();
  const branch = ask.getByRole('textbox', { name: 'Which branch should I release from?' });
  await branch.fill('hotfix');
  await branch.click();
  await userEvent.keyboard('{Enter}');
  expect(requests.filter(request => request.path.endsWith('/respond'))).toHaveLength(0);
  const notes = ask.getByRole('textbox', { name: 'Anything to tell the reviewers?' });
  await expect.element(notes).toHaveFocus();
  await notes.fill('Wait for CI');
  await userEvent.keyboard('{Enter}');
  await expect.poll(() => requests.filter(request => request.path.endsWith('/respond')).length).toBe(1);
  expect(requests.filter(request => request.path.endsWith('/messages'))).toHaveLength(0);
  expect(requests.find(request => request.path.endsWith('/respond'))).toMatchObject({
    method: 'POST',
    path: `/api/v1/sessions/${PLANNER.id}/interactions/7/respond`,
    body: { answers: [{ text: 'hotfix' }, { text: 'Wait for CI' }] },
  });
  await expect.element(ask).not.toBeInTheDocument();
  await expect.element(message).toHaveTextContent('Keep this message draft');
});

it.each([
  [4, false, 1280, 720], [4, false, 390, 844],
  [8, true, 1280, 720], [8, true, 390, 844], [8, true, 844, 390],
] as const)('keeps the composer in view while answering %i questions (long=%s) at %i×%i', async (count, long, width, height) => {
  await page.viewport(width, height);
  const { requests } = setup('light', largeAsk(count, long));
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await expect.element(ask).toBeVisible();
  const field = (await page.getByRole('combobox', { name: 'Message' }).findElement()).getBoundingClientRect();
  expect(field.bottom).toBeLessThanOrEqual(window.innerHeight);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  expect((await ask.findElement()).closest('[data-nc-conversation-action="ask"]')).not.toBeNull();
  if (count === 4 && width === 1280) await page.screenshot({ path: '../../../../test-results/v10-track-asks.png' });
  for (let index = 0; index < count; index++) {
    await expect.element(ask.getByRole('heading', { name: new RegExp(`^Question ${index + 1}:`) })).toBeVisible();
    await ask.getByRole('button', { name: new RegExp(`^Plan ${index + 1}\\.1(?: |$)`) }).click();
  }
  await expect.poll(() => requests.filter(request => request.path.endsWith('/respond')).length).toBe(1);
  const answer = requests.find(request => request.path.endsWith('/respond'));
  expect(answer?.body).toEqual({ answers: largeAsk(count, long).questions.map(question => ({ option: 0, value: question.options[0] })) });
  await expect.element(ask).not.toBeInTheDocument();
});


it('retains an unanswered ask and its question across responsive surfaces', async () => {
  await page.viewport(1280, 800);
  const { requests } = setup('light');
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  await ask.getByRole('button', { name: 'release/2.0' }).click();
  const own = ask.getByRole('textbox', { name: 'Anything to tell the reviewers?' });
  await own.fill('Keep the selected branch and this note.');
  const originalInput = await own.findElement();
  for (const width of [390, 1280]) {
    await page.viewport(width, 844);
    await expect.element(own).toHaveValue('Keep the selected branch and this note.');
    expect(await own.findElement()).toBe(originalInput);
    await expect.element(own).toHaveFocus();
    expect(requests.filter(request => request.path.endsWith('/respond'))).toHaveLength(0);
  }
  await ask.getByRole('button', { name: 'Answer' }).click();
  await expect.poll(() => requests.filter(request => request.path.endsWith('/respond')).length).toBe(1);
  expect(requests.find(request => request.path.endsWith('/respond'))?.body).toEqual({
    answers: [{ option: 1, value: 'release/2.0' }, { text: 'Keep the selected branch and this note.', value: 'Keep the selected branch and this note.' }],
  });
});

it('marks a real pending question on its Planner conversation even without a per-card input verdict', async () => {
  await page.viewport(1400, 900);
  setup('light');
  const row = page.getByRole('button', { name: 'Conversation Planner', exact: true });
  await expect.element(row).toBeVisible();
  const button = row.element();
  expect(button.closest('li')?.querySelector('[data-nc-activity]')?.getAttribute('data-nc-activity')).toBe('attention');
  const description = document.getElementById(button.getAttribute('aria-describedby') ?? '');
  expect(description?.textContent).toContain('Needs input');
});

it.each([1280, 390])('shows the complete frozen approval detail at width %i', async (width) => {
  await page.viewport(width, 844);
  const detail = `${'完整验收条件'.repeat(500)} LAST CRITERION <img src=x onerror=alert(1)>`;
  const { requests } = setup('light', { ...ASK, text: '批准此 KR 的验收标准？',
    questions: [{ title: '批准此 KR 的验收标准？', detail, options: ['批准', '不批准'] }],
  });
  await page.getByRole('button', { name: /Conversation Planner/ }).click();
  const ask = page.getByRole('group', { name: 'The Planner asks' });
  const content = await ask.getByRole('heading').findElement();
  expect(content.textContent).toBe(`批准此 KR 的验收标准？\n${detail}`);
  expect(content.querySelector('img')).toBeNull();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  await page.screenshot({ path: `../../../../test-results/objective-approval-${width}.png` });
  await ask.getByRole('button', { name: '批准', exact: true }).click();
  await expect.poll(() => requests.filter(request => request.path.endsWith('/respond')).length).toBe(1);
  expect(requests.find(request => request.path.endsWith('/respond'))?.body).toEqual({ answers: [{ option: 0, value: '批准' }] });
});

it.each([[1280, 0], [390, 0], [1280, 1], [390, 1]] as const)(
  'answers Track plugin authorization through the real conversation at %ipx with option %i', async (width, option) => {
    await page.viewport(width, 844);
    const choices = ['允许此 Track 使用', '本次不更改'];
    const { requests } = setup('light', { ...ASK, text: 'Calendar authorization',
        questions: [{ title: '允许此 Track 使用 Calendar？', detail: '可管理本 Track 的日程，定时日程可唤醒此 Track。', options: choices }],
    });
    await page.getByRole('button', { name: /Conversation Planner/ }).click();
    const ask = page.getByRole('group', { name: 'The Planner asks' });
    await expect.element(ask).toBeVisible();
    await expect.element(ask.getByRole('heading')).toHaveTextContent('定时日程可唤醒此 Track');
    expect(requests.filter(request => request.path.endsWith('/respond'))).toHaveLength(0);
    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
    await page.screenshot({ path: `../../../../test-results/track-plugin-authorization-${width}-${option}.png` });
    await ask.getByRole('button', { name: choices[option], exact: true }).click();
    await expect.poll(() => requests.find(request => request.path.endsWith('/respond'))?.body)
      .toEqual({ answers: [{ option, value: choices[option] }] });
    await expect.element(ask).not.toBeInTheDocument();
  },
);
