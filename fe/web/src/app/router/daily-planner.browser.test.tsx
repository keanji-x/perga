import '../../styles/entry.css';
import { act,cleanup,render } from '@testing-library/react';
import { afterEach,expect,it,vi } from 'vitest';
import { page,userEvent } from 'vitest/browser';
import { transcript } from '../../../../core/events/query-keys.ts';
import { withFontRoleProbe } from '../../../../tools/styles/font-role-fixture.ts';
import { NewTrackForm } from '../../features/area/new-track/public.tsx';
import { sessionItem,sessionPage,sessionTurn } from './conversation-session-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';
import { fixtureSession } from './session-contract-fixture.ts';

afterEach(async () => { cleanup(); await page.viewport(1280, 800); });

it('gives the daily mobile page one header and opens its scoped history', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ emptyWorkspace: true });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
  const headers = [...document.querySelectorAll('[data-nc-mobile-header]')].filter(header => header.getBoundingClientRect().width > 0);
  expect(headers).toHaveLength(1);
  await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
  await page.getByRole('button', { name: '2026-10-04 Daily Planner conversation', exact: true }).click();
  await expect.element(page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true })).toBeVisible();
});

it.each([320, 390])('keeps mobile navigation labels and pointer targets usable at %ipx', async (width) => {
  await page.viewport(width, 844);
  renderDailyFixture({ initial: '/track/project' });
  await page.getByRole('button', { name: 'Switch track, Project evidence', exact: true }).click();
  const layer = page.getByRole('dialog', { name: 'Tracks and settings', exact: true }).element();
  await expect.poll(() => layer.getAnimations({ subtree: true }).filter(animation => animation.effect?.getTiming().iterations !== Infinity).every(animation => animation.playState !== 'running')).toBe(true);
  const bounds = layer.getBoundingClientRect();
  expect(bounds.left).toBeGreaterThanOrEqual(0);
  expect(bounds.right).toBeLessThanOrEqual(width);
  const item = page.getByRole('button', { name: 'Project evidence', exact: true }).element();
  const text = item.querySelector('span:nth-child(2)')!;
  const box = text.getBoundingClientRect();
  expect(item.contains(document.elementFromPoint((box.left + box.right) / 2, (box.top + box.bottom) / 2))).toBe(true);
  await page.getByRole('button', { name: 'Back to Report', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Open workspace', exact: true })).toBeVisible();
});


it('keeps the daily report usable at phone width', async () => {
  await page.viewport(390, 844);
  renderDailyFixture({ initial: '/?day=2026-10-04' });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  expect(page.getByRole('button', { name: 'Track actions', exact: true }).query()).toBeNull();
  const headers = [...document.querySelectorAll('[data-nc-mobile-header]')].filter((header) => header.getBoundingClientRect().width > 0);
  expect(headers).toHaveLength(1);
  await page.getByRole('button', { name: 'Open conversation history', exact: true }).click();
  await expect.element(page.getByRole('dialog', { name: 'Conversations', exact: true })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
});


it.each([1000, 768, 600])('keeps the composer inside its conversation card with a long transcript at height %s', async (height) => {
  await page.viewport(1440, height);
  const history = Array.from({ length: 30 }, (_, index) => sessionItem('daily-planner', index + 1, `Reply ${index + 1}.\n\n${'A detailed progress update with enough content to need scrolling. '.repeat(12)}`));
  renderDailyFixture({ initial: '/?day=2026-10-04', reply: (request) => request.path.includes('/items?') && !request.path.includes('limit=1')
    ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), history) } : undefined });
  await page.getByText('Prioritize the release.', { exact: false }).findElement();
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const drawer = page.getByRole('complementary', { name: 'Daily Planner conversation' });
  await expect.element(drawer).toBeVisible();
  await drawer.getByText('Reply 30.', { exact: false }).findElement();
  const field = page.getByRole('combobox', { name: 'Message', exact: true });
  const send = page.getByRole('button', { name: 'Send', exact: true });
  await expect.element(field).toBeVisible();
  const bounds = () => {
    const stack = drawer.element().closest('[data-nc-drawer-stack]')!;
    const clip = stack.closest('main')!;
    return { drawer: drawer.element().getBoundingClientRect(), clip: clip.getBoundingClientRect(),
      field: field.element().getBoundingClientRect(), send: send.element().getBoundingClientRect() };
  };
  await expect.poll(() => bounds().send.bottom <= bounds().clip.bottom).toBe(true);
  const boxes = bounds();
  expect(boxes.field.top).toBeGreaterThanOrEqual(boxes.drawer.top);
  expect(boxes.field.bottom).toBeLessThanOrEqual(boxes.drawer.bottom);
  expect(boxes.send.bottom).toBeLessThanOrEqual(Math.min(boxes.drawer.bottom, boxes.clip.bottom, height));
  const scroll = drawer.element().querySelector<HTMLElement>('[data-nc-drawer-scroll]')!;
  expect(scroll.clientHeight).toBeGreaterThan(0);
  expect(scroll.scrollHeight).toBeGreaterThan(scroll.clientHeight);
  const draft = Array.from({ length: 12 }, (_, index) => `Draft line ${index + 1}`).join('\n');
  await field.fill(draft);
  await expect.element(field).toHaveTextContent('Draft line 12');
  expect(field.element().scrollHeight).toBeGreaterThan(field.element().clientHeight);
  await expect.poll(() => bounds().send.bottom <= bounds().clip.bottom).toBe(true);
  expect(bounds().send.bottom).toBeLessThanOrEqual(Math.min(bounds().drawer.bottom, height));
  await page.screenshot({ path: `../../../../test-results/today-long-conversation-${height}.png` });
});


it.each([1024, 1440])('aligns reading and chat headers independently of sidebar navigation sections at %ipx', async (width) => {
  await page.viewport(width, 900);
  renderDailyFixture({ initial: '/?day=2026-10-04' });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  const drawer = page.getByRole('complementary', { name: 'Daily Planner conversation' });
  await expect.element(drawer).toBeVisible();
  const readingHeader = document.querySelector('[data-nc-track-page] > header')!;
  const chatHeader = drawer.element().querySelector(':scope > header')!;
  // The drawer enters through a spring; compare settled content headers.
  await expect.poll(() => Math.abs(chatHeader.getBoundingClientRect().bottom - readingHeader.getBoundingClientRect().bottom)).toBeLessThanOrEqual(1);
  const today = page.getByRole('button', { name: 'Go to Today', exact: true }).element().lastElementChild!;
  const chatTitle = chatHeader.querySelector('h2')!;
  const center = (element: Element) => {
    const rect = element.getBoundingClientRect();
    return rect.top + rect.height / 2;
  };
  await expect.poll(() => Math.abs(center(chatTitle) - center(chatHeader))).toBeLessThanOrEqual(1);
  expect(Math.abs(center(today) - center(chatHeader))).toBeLessThanOrEqual(1);
  withFontRoleProbe('--type-ui', chatHeader as HTMLElement, expected => {
    expect(getComputedStyle(chatTitle)).toMatchObject({ fontFamily: expected.fontFamily, fontSize: expected.fontSize });
  });
  await expect.poll(() => {
    const box = drawer.element().getBoundingClientRect();
    return Math.abs(box.top - (window.innerHeight - box.bottom));
  }).toBeLessThanOrEqual(1);
});

it('keeps a heading-only daily report empty beside the calendar', async () => {
  await page.viewport(1440, 900);
  renderDailyFixture({ initial: '/?day=2026-10-04', reportBody: '# 概要\n\n# 今日计划' });
  await expect.element(page.getByRole('region', { name: 'Calendar', exact: true })).toBeVisible();
  expect(document.querySelector('[data-nc-report-empty]')).toBeNull();
  expect(page.getByRole('button', { name: 'Start a conversation', exact: true }).elements()).toHaveLength(0);
});


it('interrupts the working Today conversation from the transcript with Escape', async () => {
  await page.viewport(1440, 800);
  const { requests } = renderDailyFixture({ reply: request => {
    if (request.path.includes('/items?')) return { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), [sessionItem('daily-planner', 1, 'Working on the [Release evidence](mailto:release@example.test).')], sessionTurn('daily-planner')) };
    if (request.path.endsWith('/control')) return { status: 200, statusText: 'OK', body: { card_id: 'daily-planner', worker_session_id: 'runtime', stopped: true } };
    return undefined;
  } });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  await expect.element(assistant.getByRole('button', { name: 'Stop', exact: true })).toBeVisible();
  // The transcript, rather than its portalled footer, must own interruption too.
  const evidence = await assistant.getByRole('link', { name: 'Release evidence', exact: true }).findElement();
  evidence.focus();
  await userEvent.keyboard('{Escape}');
  await expect.poll(() => requests.filter(request => request.path.endsWith('/control')).length).toBe(1);
  await expect.element(assistant).toBeVisible();
});


it('keeps perga Today minimal without triage or Report changes', async () => {
  await page.viewport(1440, 900);
  const { requests } = renderDailyFixture({ identity: { userId: 'local-owner', displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, role: 'owner', sessionId: 'perga-owner' } });
  await expect.element(page.getByRole('region', { name: 'Today Assistant' })).toBeVisible();
  expect(page.getByRole('region', { name: 'Today triage' }).query()).toBeNull();
  expect(page.getByText(/Waiting on you/).query()).toBeNull();
  expect(page.getByText('源文件夹模式 · 未隔离', { exact: true }).query()).toBeNull();
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(page.getByText('Daily Supervisor', { exact: true }).query()).toBeNull();
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
});

it('keeps perga daily Report usable on mobile without triage or Supervisor', async () => {
  await page.viewport(390, 844);
  const { requests } = renderDailyFixture({ initial: '/?day=2026-10-04' });
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  await expect.element(page.getByRole('region', { name: 'Calendar', exact: true })).toBeVisible();
  expect(page.getByText('前一天的更新', { exact: true }).query()).toBeNull();
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(page.getByRole('region', { name: 'Today triage' }).query()).toBeNull();
  expect(page.getByText('源文件夹模式 · 未隔离', { exact: true }).query()).toBeNull();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  expect(requests.some(request => /\/api\/today\/(supervisor|launchpad)/.test(request.path))).toBe(false);
});


it.each([900, 600])('optically centers the empty perga Today composer and pins it below loaded turns at height %s', async height => {
  await page.viewport(1440, height);
  // Compare against the real New Track surface under the same production tokens.
  const referenceProps = { submitting: false, error: null, initialCwd: null,
    listDirectory: vi.fn(() => Promise.resolve({ path: '/', parent: null, entries: [] })), onSubmit: vi.fn() };
  const reference = render(<NewTrackForm {...referenceProps} />);
  const referenceField = page.getByRole('textbox', { name: 'What this track should do', exact: true }).element() as HTMLElement;
  const referenceBody = referenceField.closest('[data-density] > *')!;
  referenceField.blur();
  const referenceStyle = getComputedStyle(referenceField);
  const referenceMin = referenceStyle.minBlockSize;
  const referenceBorder = getComputedStyle(referenceBody).borderWidth;
  const referencePadding = getComputedStyle(referenceBody).padding;
  const referenceShadow = getComputedStyle(referenceBody).boxShadow;
  const referenceWidth = reference.container.querySelector('[data-nc-new-track-message]')!.getBoundingClientRect().width;
  const referenceSendRadius = getComputedStyle(page.getByRole('button', { name: 'Create track', exact: true }).element()).borderRadius;
  referenceField.focus();
  await expect.poll(() => getComputedStyle(referenceBody).boxShadow).toMatch(/^oklch.*0px 0px 0px 2px$/);
  const referenceFocus = getComputedStyle(referenceBody).boxShadow;
  expect(referenceFocus).not.toBe(referenceShadow);
  reference.rerender(<NewTrackForm {...referenceProps} submitting />);
  expect(referenceField.getAttribute('contenteditable')).toBe('false');
  await expect.poll(() => getComputedStyle(referenceBody).boxShadow).toBe(referenceShadow);
  reference.unmount();
  let populated = false;
  const fixture = renderDailyFixture({ reply: request => request.path.includes('/items?') && !request.path.includes('limit=1')
    ? { status: 200, statusText: 'OK', body: sessionPage(fixtureSession('daily-planner', 'daily'), populated ? [sessionItem('daily-planner', 1, 'Plan the release', 'user')] : []) }
    : undefined });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeVisible();
  await expect.poll(() => assistant.element().hasAttribute('data-nc-centered-footer')).toBe(true);
  expect(assistant.getByText('Start a conversation', { exact: true }).query()).toBeNull();
  expect(assistant.getByText('Begin with a question, task, or idea.', { exact: true }).query()).toBeNull();
  const host = assistant.element();
  const footer = host.children[2]; // EmbeddedReadingHost's masthead and composer.
  const box = host.getBoundingClientRect();
  const footerBox = footer.getBoundingClientRect();
  expect(host.querySelector('[data-nc-thread-empty]')).toBeNull();
  const hostStyle = getComputedStyle(host);
  const rem = parseFloat(getComputedStyle(document.documentElement).fontSize);
  const space6 = parseFloat(hostStyle.getPropertyValue('--space-6'));
  expect(parseFloat(hostStyle.paddingBottom)).toBeCloseTo(Math.min(Math.max(space6, height * 0.14), 9 * rem));
  const opticalOffset = (parseFloat(hostStyle.paddingBottom) - parseFloat(hostStyle.paddingTop)) / 2;
  expect(opticalOffset).toBeGreaterThan(20);
  expect(Math.abs((footerBox.top + footerBox.bottom) / 2 - ((box.top + box.bottom) / 2 - opticalOffset))).toBeLessThan(2);
  const editable = field.element() as HTMLElement;
  const body = editable.closest('[data-density] > *')!;
  editable.blur();
  expect(getComputedStyle(body).borderWidth).toBe(referenceBorder);
  expect(getComputedStyle(body).padding).toBe(referencePadding);
  await expect.poll(() => getComputedStyle(body).boxShadow).toBe(referenceShadow);
  expect(getComputedStyle(editable).minBlockSize).toBe(referenceMin);
  expect(parseFloat(getComputedStyle(editable).minBlockSize)).toBeGreaterThanOrEqual(88);
  expect(parseFloat(getComputedStyle(editable).minBlockSize)).toBeCloseTo(4 * parseFloat(getComputedStyle(editable).lineHeight));
  expect(footerBox.width).toBeCloseTo(referenceWidth);
  expect(footerBox.width).toBeCloseTo(parseFloat(getComputedStyle(host).getPropertyValue('--measure-doc')));
  expect(getComputedStyle(host).alignItems).toBe('safe center');
  expect(getComputedStyle(host).justifyContent).toBe('safe center');
  expect(getComputedStyle(assistant.getByRole('button', { name: 'Send', exact: true }).element()).borderRadius).toBe(referenceSendRadius);
  editable.focus();
  await expect.poll(() => getComputedStyle(body).boxShadow).toBe(referenceFocus);
  expect(referenceFocus).toContain('0px 0px 0px 2px');
  expect(getComputedStyle(body).outlineStyle).toBe('none');
  expect(Math.abs((footerBox.left + footerBox.right) / 2 - (box.left + box.right) / 2)).toBeLessThan(2);
  expect(assistant.getByText('Report changes').query()).toBeNull();
  await expect.element(assistant.getByRole('heading', { name: 'What would you like to move forward today?' })).toBeVisible();
  editable.blur();
  await expect.poll(() => getComputedStyle(body).boxShadow).toBe(referenceShadow);
  await page.screenshot({ path: `../../../../test-results/today-centered-composer-${height}.png` });
  await field.fill('Keep this draft');
  const editor = field.element();
  populated = true;
  await act(async () => { await fixture.client.invalidateQueries({ queryKey: transcript('daily-planner') }); });
  await expect.element(assistant.getByText('Plan the release', { exact: true })).toBeVisible();
  expect(host.hasAttribute('data-nc-centered-footer')).toBe(false);
  expect(field.element()).toBe(editor);
  await expect.element(field).toHaveTextContent('Keep this draft');
  expect(assistant.getByText('Report changes').query()).toBeNull();
  expect(assistant.getByRole('heading', { name: 'What would you like to move forward today?' }).query()).toBeNull();
  const send = assistant.getByRole('button', { name: 'Send', exact: true }).element().getBoundingClientRect();
  expect(send.bottom).toBeLessThanOrEqual(box.bottom);
  expect(send.top).toBeGreaterThan(box.top + box.height / 2);
});

it.each([900, 600])('shows the shared masthead above a clean perga composer and keeps the right card usable at height %s', async height => {
  await page.viewport(1440, height);
  renderDailyFixture({ });
  const assistant = page.getByRole('region', { name: 'Today Assistant', exact: true });
  const heading = assistant.getByRole('heading', { name: 'What would you like to move forward today?' });
  await expect.element(heading).toBeVisible();
  const logo = heading.element().querySelector<SVGSVGElement>('[data-nc-motion="creation"]')!;
  await expect.element(logo).toBeVisible();
  expect(assistant.getByText('说说今天的重点，计划与进展会整理在右侧日报里。').query()).toBeNull();
  const field = assistant.getByRole('combobox', { name: 'Message', exact: true });
  await expect.element(field).toBeVisible();
  const body = field.element().closest('[data-density] > *')!;
  expect(heading.element().getBoundingClientRect().bottom).toBeLessThan(body.getBoundingClientRect().top);
  expect(heading.element().getBoundingClientRect().left).toBeCloseTo(body.getBoundingClientRect().left);
  expect(getComputedStyle(body).boxShadow).not.toContain('0px 0px 0px 2px');
  expect(parseFloat(getComputedStyle(field.element()).minBlockSize)).toBeCloseTo(4 * parseFloat(getComputedStyle(field.element()).lineHeight));
  expect(assistant.getByText('Report changes').query()).toBeNull();
  expect(assistant.getByText('Start a conversation').query()).toBeNull();
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar).toBeVisible();
  const card = calendar.element().closest('[data-nc-desktop-panel]')!;
  expect(calendar.element().getBoundingClientRect().left).toBeGreaterThan(assistant.element().getBoundingClientRect().right);
  expect(page.getByText('Report changes', { exact: true }).query()).toBeNull();
  expect(card.textContent).not.toMatch(/Active tracks/i);
  // The Report prose legitimately cites this Track; only an extra inventory row would be wrong.
  expect([...card.querySelectorAll('[data-nc-row]')].some(row => row.textContent?.includes('Project evidence'))).toBe(false);
  await expect.element(page.getByRole('heading', { name: 'Daily report', exact: true })).toBeVisible();
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(window.innerWidth);
  await page.screenshot({ path: `../../../../test-results/today-v4-${height}.png` });
});

it.each([1440, 390])('navigates between dates and displays day schedules on perga calendar at width %s', async width => {
  await page.viewport(width, 844);
  const { router, requests } = renderDailyFixture({
    initial: '/',
    reply: (request) => {
      if (request.path.startsWith('/api/v1/today/calendar')) {
        const params = new URL(request.path, 'http://fixture').searchParams;
        const start = params.get('start') ?? '2026-09-28';
        const end = params.get('end') ?? '2026-10-04';
        const days: Array<{ date: string; hasDailyTrack: boolean; hasReport: boolean }> = [];
        const cursor = new Date(`${start}T12:00:00Z`);
        const limit = new Date(`${end}T12:00:00Z`);
        while (cursor <= limit && days.length < 62) {
          const day = cursor.toISOString().slice(0, 10);
          days.push({
            date: day,
            hasDailyTrack: day === '2026-10-04',
            hasReport: day === '2026-10-04',
          });
          cursor.setUTCDate(cursor.getUTCDate() + 1);
        }
        const schedules = start <= '2026-10-03' && end >= '2026-10-03' ? [{
          id: 'sch_oct3',
          date: '2026-10-03',
          trackId: 'project',
          trackTitle: 'Project evidence',
          title: 'Check release pipeline',
          scheduleKind: 'once',
          cronExpr: null,
          timezone: 'UTC',
          nextFireAtMs: Date.parse('2026-10-03T14:30:00Z'),
          state: 'active',
        }] : [];
        return { status: 200, statusText: 'OK', body: { today: '2026-10-04', timeZone: 'UTC', start, end, days, schedules } };
      }
      return undefined;
    },
  });
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar).toBeVisible();
  await expect.element(calendar.getByRole('button', { name: '2026-10-04', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.element(calendar.getByRole('button', { name: '2026-10-04', exact: true })).toHaveAttribute('data-nc-has-track', '');
  await expect.element(calendar.getByRole('button', { name: '2026-10-03', exact: true })).toHaveAttribute('data-nc-schedule-count', '1');

  // Switch to Month view, page months without altering ?day=, and keep Month mode across date navigation + Back/Forward.
  await page.getByRole('radio', { name: 'Month', exact: true }).click();
  await expect.element(page.getByRole('radio', { name: 'Month', exact: true })).toBeChecked();
  await calendar.getByRole('button', { name: 'Next month' }).click();
  await expect.poll(() => requests.some(r => r.path === '/api/v1/today/calendar?start=2026-11-01&end=2026-11-30')).toBe(true);
  expect(router.state.location.search).toEqual({ day: undefined });

  // Return to October while staying in Month mode and click 2026-10-03 (no daily track, has 1 schedule).
  await calendar.getByRole('button', { name: 'Previous month' }).click();
  await calendar.getByRole('button', { name: '2026-10-03', exact: true }).click();
  await expect.poll(() => (router.state.location.search as Readonly<{ day?: string }>).day).toBe('2026-10-03');
  await expect.element(page.getByText('No daily Track was created for this date.')).toBeVisible();
  await expect.element(page.getByRole('radio', { name: 'Month', exact: true })).toBeChecked();
  await expect.element(calendar.getByRole('button', { name: '2026-10-03', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.element(calendar.getByText('Check release pipeline', { exact: true })).toBeVisible();

  // Browser Back returns to '/', Forward returns to ?day=2026-10-03, and Today returns cleanly to '/'.
  router.history.back();
  await expect.poll(() => (router.state.location.search as Readonly<{ day?: string }>).day).toBeUndefined();
  await expect.element(calendar.getByRole('button', { name: '2026-10-04', exact: true })).toHaveAttribute('aria-pressed', 'true');
  router.history.forward();
  await expect.poll(() => (router.state.location.search as Readonly<{ day?: string }>).day).toBe('2026-10-03');
  await expect.element(page.getByText('No daily Track was created for this date.')).toBeVisible();

  // Click Today to return to '/' and load today's Track.
  await calendar.getByRole('button', { name: 'Today', exact: true }).click();
  await expect.poll(() => (router.state.location.search as Readonly<{ day?: string }>).day).toBeUndefined();
  await expect.element(calendar.getByRole('button', { name: '2026-10-04', exact: true })).toHaveAttribute('aria-pressed', 'true');
  if (width >= 1024) {
    await expect.element(page.getByRole('heading', { name: 'What would you like to move forward today?' })).toBeVisible();
  } else {
    await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  }
});

it.each([false, true])('shows perga Conversations only with multiple rows (extra=%s)', async extraPlanner => {
  await page.viewport(1440, 900);
  renderDailyFixture({ extraPlanner });
  await expect.element(page.getByText('Prioritize the release.', { exact: false })).toBeVisible();
  const conversations = page.getByRole('region', { name: 'Conversations', exact: true });
  if (extraPlanner) {
    await expect.element(conversations).toBeVisible();
    await expect.element(conversations.getByRole('button', { name: 'Conversation Other conversation', exact: true })).toBeVisible();
    await expect.element(conversations.getByRole('button', { name: 'Conversation Daily Planner conversation', exact: true })).toBeVisible();
  } else {
    expect(conversations.query()).toBeNull();
    expect(page.getByRole('button', { name: 'New conversation', exact: true }).query()).toBeNull();
  }
});

it.each(['', '   \n  ', '# 今日计划\n\nA populated daily report.'])('shows perga Daily report only when populated (%s)', async reportBody => {
  await page.viewport(1440, 900);
  renderDailyFixture({ reportBody });
  await expect.element(page.getByRole('heading', { name: 'What would you like to move forward today?', exact: true })).toBeVisible();
  const report = page.getByRole('heading', { name: 'Daily report', exact: true });
  if (reportBody.includes('A populated')) {
    await expect.element(page.getByText('A populated daily report.')).toBeVisible();
    await expect.element(report).toBeVisible();
  } else {
    await expect.poll(() => document.querySelector('[data-nc-centered-footer]') !== null).toBe(true);
    await expect.poll(() => page.getByRole('region', { name: 'Track report' }).query()?.getAttribute('aria-busy')).not.toBe('true');
    expect(report.query()).toBeNull();
    expect(page.getByText('Progress lives here', { exact: true }).query()).toBeNull();
    expect(page.getByRole('region', { name: 'Track report' }).query()).toBeNull();
    expect(page.getByRole('region', { name: 'Blank daily report' }).query()).toBeNull();
  }
});

it.each(['Today', '2026-10-04', '2026-10-03'])('escapes an invalid day query by selecting %s', async selection => {
  await page.viewport(1440, 900);
  const { router } = renderDailyFixture({ initial: '/?day=invalid' });
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar.getByRole('button', { name: '2026-10-04', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await calendar.getByRole('button', { name: selection, exact: true }).click();
  await expect.poll(() => (router.state.location.search as Readonly<{ day?: string }>).day)
    .toBe(selection === '2026-10-03' ? selection : undefined);
});

it.each([
  [390, 844, 44],
  [320, 640, 40],
] as const)('gives mobile calendar controls touch-sized targets without horizontal overflow at %ix%i', async (width, height, minDateWidth) => {
  await page.viewport(width, height);
  renderDailyFixture({ initial: '/?day=2026-10-03' });
  const calendar = page.getByRole('region', { name: 'Calendar', exact: true });
  await expect.element(calendar).toBeVisible();
  const prev = calendar.getByRole('button', { name: 'Previous week' }).element().getBoundingClientRect();
  const next = calendar.getByRole('button', { name: 'Next week' }).element().getBoundingClientRect();
  const todayBtn = calendar.getByRole('button', { name: 'Today', exact: true }).element().getBoundingClientRect();
  const dateBtn = calendar.getByRole('button', { name: '2026-10-03', exact: true }).element().getBoundingClientRect();

  expect(prev.width).toBeGreaterThanOrEqual(44);
  expect(prev.height).toBeGreaterThanOrEqual(44);
  expect(next.width).toBeGreaterThanOrEqual(44);
  expect(next.height).toBeGreaterThanOrEqual(44);
  expect(todayBtn.height).toBeGreaterThanOrEqual(44);
  expect(dateBtn.height).toBeGreaterThanOrEqual(44);
  expect(dateBtn.width).toBeGreaterThanOrEqual(minDateWidth);
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width);
});
