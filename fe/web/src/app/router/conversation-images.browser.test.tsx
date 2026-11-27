import '../../styles/entry.css';
import { cleanup, fireEvent } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import type { ApiRequest, ApiTransportResponse } from '../../../../core/api/types.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionInput, sessionItem, sessionPage, sessionReply, sessionTurn } from './conversation-session-data.ts';
import { fixtureCreation, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';

declare module 'vitest/browser' {
  interface BrowserCommands { mockConversationMedia(path: string, pngBase64?: string): Promise<void> }
}

const png = 'iVBORw0KGgoAAAANSUhEUgAAAUAAAAC0CAIAAABqhmJGAAACLUlEQVR4nO3TMQ3AMBDAwIdRRKVR+CHQpVs4dIksnXQAvHiu+wGi5ngB8JuBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhrBZ7wdEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEGhjADQ5iBIczAEGZgCDMwhBkYwgwMYQaGMANDmIEhzMAQZmAIMzCEGRjCDAxhBoYwA0OYgSHMwBBmYAgzMIQZGMIMDGEb/HgmtocxoioAAAAASUVORK5CYII=';
const bytes = () => Uint8Array.from(atob(png), ch => ch.charCodeAt(0));
const screenshot = () => new File([bytes()], 'screenshot.png', { type: 'image/png' });
const session = 'daily-planner';
const mediaId = `${'a'.repeat(64)}.png`;
const mediaUrl = `/api/v1/tracks/daily/media/${mediaId}`;
const attachment = Object.freeze({ id: mediaId, contentType: 'image/png', size: atob(png).length, url: mediaUrl });

const previews = () => [...document.querySelectorAll<HTMLImageElement>('[data-nc-attachments] img')];
const transcriptImages = () => [...document.querySelectorAll<HTMLImageElement>('[data-nc-thread] [data-nc-turn-attachments] img')];
const composer = () => page.getByRole('combobox', { name: 'Message', exact: true });
const send = () => page.getByRole('button', { name: 'Send image', exact: true }).click();

function paste(field: Element, file: File, itemsOnly = false, text = '') {
  const data = new DataTransfer();
  data.items.add(file);
  if (text !== '') data.setData('text/plain', text);
  if (itemsOnly) Object.defineProperty(data, 'files', { value: [] });
  fireEvent(field, new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
}

function form(request: ApiRequest) {
  expect(request.body).toBeInstanceOf(FormData);
  expect(request.headers?.['content-type']).toBeUndefined();
  const body = request.body as FormData;
  expect([...body.keys()]).toEqual(['payload', 'attachments']);
  return body;
}

function payload(body: FormData): unknown {
  const text = body.get('payload');
  if (typeof text !== 'string') throw new Error('Expected a payload form field.');
  return JSON.parse(text);
}

function savedItem(id: string, input: string, content: string) {
  return { ...sessionItem(id, 1, content, 'user'), payload: { inputId: input, content, attachments: [attachment] } };
}

afterEach(async () => {
  cleanup();
  document.getElementById('root')?.remove();
  await commands.mockConversationMedia(mediaUrl);
});

it('previews files and screenshot items, restores a refused image, then retries and opens canonical history', async () => {
  await page.viewport(1280, 900);
  await commands.mockConversationMedia(mediaUrl, png);
  let decline!: (response: ApiTransportResponse) => void;
  const pending = new Promise<ApiTransportResponse>(resolve => { decline = resolve; });
  let attempts = 0;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true,
    reply: request => {
    if (request.path !== `/api/v1/sessions/${session}/messages` || request.method !== 'POST') return;
    attempts += 1;
    if (attempts === 1) return pending;
    if (attempts === 2) return { status: 429, statusText: 'Too many requests', body: { code: 'rate_limited', error: 'Try the same image again.' } };
    view.items.set(session, [savedItem(session, 'image-input', '')]);
    return sessionReply(sessionInput(session, 'image-input'));
  } });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(composer()).toHaveAttribute('contenteditable', 'true');
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  const file = screenshot();
  paste(composer().element(), file);
  await expect.poll(() => previews().map(image => image.naturalWidth)).toEqual([320]);
  await page.getByRole('button', { name: /Remove/ }).click();
  await expect.poll(() => previews().length).toBe(0);

  paste(composer().element(), file, true);
  const blobUrl = previews()[0].src;
  expect(view.requests.some(request => request.method === 'POST')).toBe(false);
  expect(composer().element().textContent).toBe('');
  await send();
  await expect.poll(() => transcriptImages().map(image => image.src)).toEqual([blobUrl]);
  expect(previews()).toHaveLength(0);
  await expect.poll(() => transcriptImages()[0]?.naturalWidth).toBe(320);
  decline({ status: 409, statusText: 'Conflict', body: { code: 'attachments_unsupported', error: 'Image refused.' } });
  await page.getByRole('button', { name: 'Restore to input', exact: true }).click();
  await expect.poll(() => previews().map(image => image.src)).toEqual([blobUrl]);
  await expect.poll(() => transcriptImages().length).toBe(0);

  await send();
  const retry = page.getByRole('button', { name: 'Retry', exact: true });
  await expect.element(retry).toBeVisible();
  await expect.poll(() => transcriptImages()[0]?.naturalWidth).toBe(320);
  await retry.click();

  await expect.poll(() => attempts).toBe(3);
  const posts = view.requests.filter(request => request.path.endsWith('/messages') && request.method === 'POST');
  expect(posts).toHaveLength(3);
  for (const post of posts) {
    const body = form(post);
    expect(payload(body)).toMatchObject({ content: '' });
    expect(await (body.getAll('attachments')[0] as File).bytes()).toEqual(bytes());
  }
  expect(posts[2].headers?.['Idempotency-Key']).toBe(posts[1].headers?.['Idempotency-Key']);
  await expect.poll(() => transcriptImages().map(image => image.getAttribute('src'))).toEqual([mediaUrl]);
  await expect.poll(() => transcriptImages()[0]?.naturalWidth).toBe(320);
  await expect(fetch(blobUrl)).rejects.toThrow();
  await page.getByRole('button', { name: 'Image 1 of 1' }).click();
  const lightbox = page.getByRole('dialog', { name: 'Media viewer' });
  await expect.element(lightbox).toBeVisible();
  const zoom = lightbox.getByRole('button', { name: 'Zoom' });
  (await zoom.findElement() as HTMLElement).focus();
  await userEvent.keyboard('{Enter}');
  await expect.element(zoom).toHaveAttribute('aria-pressed', 'true');
});

it('accepts drops, picker files and mixed clipboard images in a new conversation, then sends an image-only first message', async () => {
  await page.viewport(1280, 900);
  await commands.mockConversationMedia(mediaUrl, png);
  const created = fixtureSession('new-with-image', 'daily', { title: 'Image conversation' });
  let attempts = 0;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true, reply: request => {
    if (request.path !== '/api/v1/tracks/daily/sessions' || request.method !== 'POST') return;
    attempts += 1;
    if (attempts === 1) return { status: 503, statusText: 'Unavailable', body: { code: 'service_unavailable', error: 'Unconfirmed' } };
    view.sessions.push(created);
    view.items.set(created.id, [savedItem(created.id, 'first-input', '')]);
    return { status: 201, statusText: 'Created', body: fixtureCreation(created) };
  } });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  const field = await composer().findElement();
  const file = screenshot();

  paste(field, file, true, '截图与文字');
  expect(field.textContent).toBe('截图与文字');
  paste(field, file, false, '和另一张图');
  expect(field.textContent).toContain('和另一张图');
  const dropped = new DataTransfer();
  dropped.items.add(file);
  fireEvent(field, new DragEvent('drop', { dataTransfer: dropped, bubbles: true, cancelable: true }));
  const picker = document.querySelector<HTMLInputElement>('[data-nc-attach] input[type="file"]')!;
  const picked = new DataTransfer();
  picked.items.add(file);
  picker.files = picked.files;
  fireEvent.change(picker);
  await expect.poll(() => previews().map(image => image.naturalWidth)).toEqual([320, 320, 320, 320]);

  const deleted = previews()[0].src;
  await page.getByRole('button', { name: /Remove/ }).first().click();
  expect(previews()).toHaveLength(3);
  await expect(fetch(deleted)).rejects.toThrow();
  await page.getByRole('button', { name: /Remove/ }).first().click();
  await page.getByRole('button', { name: /Remove/ }).first().click();

  await composer().fill('');
  const posts = () => view.requests.filter(request => request.path === '/api/v1/tracks/daily/sessions' && request.method === 'POST');
  expect(posts()).toHaveLength(0);
  await composer().click();
  fireEvent.compositionStart(field);
  fireEvent.keyDown(field, { key: 'Enter', code: 'Enter', isComposing: true, keyCode: 229 });
  expect(posts()).toHaveLength(0);
  expect(previews()).toHaveLength(1);
  fireEvent.compositionEnd(field);
  await userEvent.keyboard('{Enter}');
  const retry = page.getByRole('button', { name: 'Try again', exact: true });
  await expect.element(retry).toBeVisible();
  expect(posts()).toHaveLength(1);
  await expect.poll(() => previews()[0]?.naturalWidth).toBe(320);
  await retry.click();
  await expect.poll(() => posts().length).toBe(2);
  for (const post of posts()) {
    const body = form(post);
    expect(payload(body)).toMatchObject({ prompt: '', provider: 'codex' });
    expect(await (body.get('attachments') as File).bytes()).toEqual(bytes());
  }
  expect(posts()[1].headers?.['Idempotency-Key']).toBe(posts()[0].headers?.['Idempotency-Key']);
  await expect.poll(() => transcriptImages().map(image => image.getAttribute('src'))).toEqual([mediaUrl]);
});

it('rejects unsupported, oversized and surplus images in a draft and releases its previews on close', async () => {
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations' });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  const field = await composer().findElement();
  paste(field, new File(['invalid'], 'image.svg', { type: 'image/svg+xml' }));
  await expect.element(page.getByText('Choose a PNG, JPEG, GIF or WebP image.')).toBeVisible();
  paste(field, new File([new Uint8Array(10 * 1024 * 1024 + 1)], 'large.png', { type: 'image/png' }));
  await expect.element(page.getByText('Each image must be at most 10 MiB.')).toBeVisible();
  expect(previews()).toHaveLength(0);
  const many = new DataTransfer();
  for (let index = 0; index < 9; index += 1) many.items.add(screenshot());
  fireEvent(field, new DragEvent('drop', { dataTransfer: many, bubbles: true, cancelable: true }));
  await expect.element(page.getByRole('alert').getByText('A message can carry at most 8 images.', { exact: true })).toBeVisible();
  await expect.poll(() => previews().length).toBe(8);
  const preview = previews()[0].src;
  await page.getByRole('button', { name: 'Close conversation', exact: true }).click();
  await expect.poll(() => previews().length).toBe(0);
  await expect(fetch(preview)).rejects.toThrow();
  expect(view.requests.some(request => request.method === 'POST')).toBe(false);
});

it.each(['', '这是一张截图'])('shows one canonical message when history wins the POST race (%j)', async text => {
  await page.viewport(1280, 900);
  await commands.mockConversationMedia(mediaUrl, png);
  let respond!: (response: ApiTransportResponse) => void;
  const heldReceipt = new Promise<ApiTransportResponse>(resolve => { respond = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true, reply: request => {
    if (request.method === 'POST' && request.path === `/api/v1/sessions/${session}/messages`) return heldReceipt;
  } });
  const posts = () => view.requests.filter(request => request.method === 'POST' && request.path.endsWith('/messages'));
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  paste(composer().element(), screenshot(), true, text);
  await expect.poll(() => previews().length).toBe(1);
  const blobUrl = previews()[0].src;
  if (text === '') await send();
  else { await composer().click(); await userEvent.keyboard('{Enter}'); }
  await expect.poll(() => posts().length).toBe(1);
  await expect.poll(() => transcriptImages().map(image => image.src)).toEqual([blobUrl]);
  const requestKey = posts()[0].headers?.['Idempotency-Key'];
  expect(requestKey).toBeTruthy();
  const stored = savedItem(session, 'persisted-before-receipt', text);
  view.items.set(session, [{ ...stored, payload: { ...stored.payload, requestKey } }]);
  await view.client.invalidateQueries({ queryKey: queryKeys.conversationHistory(session) });
  await expect.poll(() => transcriptImages().map(image => image.getAttribute('src'))).toEqual([mediaUrl]);
  await expect(fetch(blobUrl)).rejects.toThrow();

  respond(sessionReply(sessionInput(session, 'persisted-before-receipt')));
  await expect.poll(() => transcriptImages().map(image => image.getAttribute('src'))).toEqual([mediaUrl]);
  expect(posts()).toHaveLength(1);
});


it.each([[409, 'attachments_unsupported'], [413, 'payload_too_large']] as const)(
  'unlocks a newly refused image draft after %s %s', async (status, code) => {
    await page.viewport(1280, 900);
    const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
      if (request.method === 'POST' && request.path === '/api/v1/tracks/daily/sessions')
        return { status, statusText: 'Refused', body: { code, error: 'The image was refused.' } };
    } });
    await page.getByRole('button', { name: 'New conversation', exact: true }).click();
    await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
    paste(composer().element(), screenshot(), true, 'Edit this message');
    await composer().click();
    await userEvent.keyboard('{Enter}');
    await expect.element(page.getByRole('complementary', { name: 'Untitled' }).getByRole('alert')).toHaveTextContent('The image was refused.');
    const stripRect = document.querySelector('[data-nc-attachment-strip]')!.getBoundingClientRect();
    const notice = page.getByRole('complementary', { name: 'Untitled' }).getByRole('alert').element();
    expect(notice.getBoundingClientRect().bottom).toBeLessThanOrEqual(stripRect.top + 0.5);
    await expect.element(composer()).toHaveTextContent('Edit this message');
    await expect.element(page.getByRole('button', { name: /^Model:/ })).toBeEnabled();
    await page.getByRole('button', { name: /Remove/ }).click();
    await expect.poll(() => previews().length).toBe(0);
    expect(view.requests.filter(request => request.method === 'POST')).toHaveLength(1);
  },
);

it('keeps the original draft image locked when a refusal follows an unconfirmed creation', async () => {
  await page.viewport(1280, 900);
  let attempts = 0;
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', reply: request => {
    if (request.method !== 'POST' || request.path !== '/api/v1/tracks/daily/sessions') return;
    return ++attempts === 1
      ? { status: 503, statusText: 'Unavailable', body: { code: 'service_unavailable', error: 'Unknown' } }
      : { status: 409, statusText: 'Conflict', body: { code: 'attachments_unsupported', error: 'Image refused.' } };
  } });
  await page.getByRole('button', { name: 'New conversation', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  paste(composer().element(), screenshot(), true);
  await send();
  await page.getByRole('button', { name: 'Try again', exact: true }).click();
  await expect.poll(() => attempts).toBe(2);
  await expect.element(page.getByRole('complementary', { name: 'Untitled' }).getByRole('alert')).toHaveTextContent('Starting the conversation is unconfirmed');
  expect(page.getByRole('button', { name: /Remove/ }).query()).toBeNull();
  expect(previews()).toHaveLength(1);
  await expect.element(page.getByRole('button', { name: /^Model:/ })).toBeDisabled();
  const posts = view.requests.filter(request => request.method === 'POST');
  expect(posts[1].headers?.['Idempotency-Key']).toBe(posts[0].headers?.['Idempotency-Key']);
  expect(form(posts[1]).get('attachments')).toBe(form(posts[0]).get('attachments'));
});

it.each(['existing', 'new', 'queued'] as const)('retains the %s image and its blob after ACK until the server displays it', async kind => {
  await page.viewport(1280, 900);
  await commands.mockConversationMedia(mediaUrl, png);
  const id = kind === 'new' ? 'first-image-session' : session;
  const created = fixtureSession(id, 'daily', { title: 'First image session' });
  const messageId = kind === 'new' ? 'first-input' : 'acked-input';
  const active = kind === 'queued' ? sessionTurn(id) : null;
  let acked = false;
  let release!: () => void;
  const waitForRead = new Promise<void>(resolve => { release = resolve; });
  const view = renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true,
    reply: async request => {
      if (request.method === 'POST' && request.path === (kind === 'new'
        ? '/api/v1/tracks/daily/sessions' : `/api/v1/sessions/${id}/messages`)) {
        acked = true;
        if (kind === 'new') {
          view.sessions.push(created);
          return { status: 201, statusText: 'Created', body: fixtureCreation(created) };
        }
        return sessionReply({ ...sessionInput(id, messageId), ...(kind === 'queued' ? { delivery: 'queued', turnId: null } : {}) });
      }
      if (request.method === 'GET' && request.path.startsWith(`/api/v1/sessions/${id}/items?`) && acked) {
        await waitForRead;
        const base = sessionPage(kind === 'new' ? created : view.sessions[0],
          kind === 'queued' ? [] : [savedItem(id, messageId, '')], active, undefined, true);
        return sessionReply(kind === 'queued' ? { ...base,
          queuedMessages: [{ id: messageId, text: '', queuedAtMs: 1, state: 'waiting', attachments: [attachment] }],
        } : base);
      }
    },
  });
  if (active !== null) view.activeTurns.set(id, active);
  await page.getByRole('button', { name: kind === 'new' ? 'New conversation' : /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  paste(composer().element(), screenshot(), true);
  const blobUrl = previews()[0].src;
  const images = () => [...document.querySelectorAll<HTMLImageElement>('[data-nc-turn-attachments] img')];
  try {
    await send();
    await expect.poll(() => view.requests.some(request => acked && request.method === 'GET'
      && request.path.startsWith(`/api/v1/sessions/${id}/items?`))).toBe(true);
    if (kind === 'new')
      await expect.element(page.getByRole('complementary', { name: 'First image session' })).toBeVisible();
    else
      await expect.element(page.getByRole('group', { name: 'Message composer' })).not.toHaveAttribute('aria-busy', 'true');
    await expect.poll(() => images().map(image => image.src)).toEqual([blobUrl]);
    expect(await (await fetch(blobUrl)).bytes()).toEqual(bytes());
  } finally {
    release();
  }
  await expect.poll(() => images().map(image => image.getAttribute('src'))).toEqual([mediaUrl]);
  await expect(fetch(blobUrl)).rejects.toThrow();
});

it('shows inline composer previews without an external drawer and zooms any clicked preview in a portal lightbox', async () => {
  await page.viewport(1280, 900);
  renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();

  const field = composer().element();
  paste(field, screenshot());
  expect(previews()).toHaveLength(1);
  // Floats directly above the input box inside the composer group, never wrapped in an external collapsible Images drawer or inside [contenteditable].
  await expect.element(page.getByRole('button', { name: /Images/ })).not.toBeInTheDocument();
  const group = field.closest('[role="group"][aria-label="Message composer"]');
  expect(group?.contains(previews()[0])).toBe(true);
  expect(field.parentElement?.contains(previews()[0])).toBe(false);

  await page.getByRole('button', { name: 'Open Image 1' }).click();
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).toBeVisible();
  await expect.element(page.getByRole('button', { name: 'Zoom', exact: true })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).not.toBeInTheDocument();
  expect(previews()).toHaveLength(1);
});

it('pastes, previews, zooms, and sends an initial image from the New Track composer', async () => {
  await page.viewport(1280, 900);
  const createdTrack = fixtureTrack('created-track', { areaId: 'project-area', leadSessionId: 'created-lead' });
  const createdLead = fixtureSession('created-lead', 'created-track', { title: 'Lead planner', isLead: true });
  const view = renderSessionFixture({
    initial: '/area/project-area/new',
    attachmentsSupported: true,
    reply: request => {
      if (request.method === 'POST' && request.path === '/api/v1/tracks') {
        view.sessions.push(createdLead);
        return { status: 201, statusText: 'Created', body: fixtureSnapshot(createdTrack, [createdLead]) };
      }
      if (request.method === 'GET' && request.path === '/api/v1/tracks/created-track') {
        return sessionReply(fixtureSnapshot(createdTrack, [createdLead]));
      }
      if (request.method === 'GET' && request.path === '/api/v1/tracks/created-track/sessions') {
        return sessionReply([createdLead]);
      }
      if (request.method === 'GET' && request.path.startsWith('/api/v1/sessions/created-lead/items?')) {
        return sessionReply(sessionPage(createdLead, [], null, undefined, true));
      }
      if (request.method === 'POST' && request.path === '/api/v1/sessions/created-lead/messages') {
        return sessionReply(sessionInput('created-lead', 'init-msg'));
      }
      return undefined;
    },
  });

  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  const field = page.getByRole('combobox', { name: 'What this track should do' }).element();
  paste(field, screenshot());
  expect(previews()).toHaveLength(1);
  expect(field.closest('[data-nc-new-track-message]')?.contains(previews()[0])).toBe(true);

  await page.getByRole('button', { name: 'Open Image 1' }).click();
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).not.toBeInTheDocument();

  await page.getByRole('button', { name: 'Create track' }).click();
  await expect.poll(() => view.requests.some(request =>
    request.method === 'POST' && request.path === '/api/v1/sessions/created-lead/messages')).toBe(true);
  const initialSend = view.requests.find(request =>
    request.method === 'POST' && request.path === '/api/v1/sessions/created-lead/messages')!;
  const body = form(initialSend);
  expect(payload(body)).toMatchObject({ content: '' });
  expect((body.get('attachments') as File).name).toBe('screenshot.png');
});

it('renders inline composer attachments inside mobile sheet composer and opens portal lightbox', async () => {
  await page.viewport(390, 844);
  renderSessionFixture({ initial: '/track/daily?panel=conversations', attachmentsSupported: true });
  await page.getByRole('button', { name: /Conversation Daily Planner conversation/ }).click();
  await expect.element(composer()).toBeVisible();
  const attach = page.getByRole('button', { name: 'Attach an image' });
  await expect.element(attach).toBeEnabled();
  expect(attach.element().closest('[data-nc-mobile-composer-meta]')).not.toBeNull();

  const field = composer().element();
  paste(field, screenshot());
  expect(previews()).toHaveLength(1);
  expect(document.querySelector('[data-nc-attachment-strip]')).not.toBeNull();
  expect(document.documentElement.scrollWidth).toBe(390);

  await page.getByRole('button', { name: 'Open Image 1' }).click();
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).toBeVisible();
  await userEvent.keyboard('{Escape}');
  await expect.element(page.getByRole('dialog', { name: 'Image 1' })).not.toBeInTheDocument();
});

it('sends an image-only message from the mobile Chat Sheet using its native Send button', async () => {
  await page.viewport(390, 844);
  const path = `/api/v1/sessions/${session}/messages`;
  const view = renderSessionFixture({ initial: '/track/daily', attachmentsSupported: true,
    reply: request => request.method === 'POST' && request.path === path ? sessionReply(sessionInput(session)) : undefined });
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  const sheet = page.getByRole('dialog', { name: 'Daily Planner conversation', exact: true });
  const sendButton = sheet.getByRole('button', { name: 'Send', exact: true });
  await expect.element(sendButton).toBeDisabled();
  await expect.element(sheet.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  await composer().fill('Text draft');
  await expect.element(sendButton).toBeEnabled();
  const textSendFill = getComputedStyle(sendButton.element()).backgroundColor;
  await composer().fill('');
  paste(composer().element(), screenshot());
  await expect.poll(() => previews().length).toBe(1);
  expect(sheet.element().querySelector('[data-nc-send-attachment]')).toBeNull();
  await expect.element(sendButton).toBeEnabled();
  expect(getComputedStyle(sendButton.element()).backgroundColor).toBe(textSendFill);
  await sendButton.click();
  const posts = () => view.requests.filter(request => request.method === 'POST' && request.path === path);
  await expect.poll(() => posts().length).toBe(1);
  const body = form(posts()[0]);
  expect(payload(body)).toMatchObject({ content: '' });
  expect((body.get('attachments') as File).name).toBe('screenshot.png');
});

it.each(['Queue message', 'Send image'] as const)('queues from the icon-only %s button beside Stop at 320px', async label => {
  const width = 320;
  await page.viewport(width, 640);
  const path = `/api/v1/sessions/${session}/messages`;
  const view = renderSessionFixture({ initial: '/track/daily', attachmentsSupported: true,
    reply: request => request.method === 'POST' && request.path === path
      ? sessionReply({ ...sessionInput(session), delivery: 'queued', turnId: null }) : undefined });
  view.activeTurns.set(session, sessionTurn(session));
  await page.getByRole('textbox', { name: 'Chat message', exact: true }).click();
  await expect.element(page.getByRole('button', { name: 'Attach an image' })).toBeEnabled();
  const imageOnly = label === 'Send image';
  const text = imageOnly ? '' : 'Queue text';
  if (imageOnly) paste(composer().element(), screenshot());
  else await composer().fill(text);

  const composerGroup = page.getByRole('group', { name: 'Message composer', exact: true });
  const queue = composerGroup.getByRole('button', { name: label, exact: true });
  const button = await queue.findElement();
  const stop = await composerGroup.getByRole('button', { name: 'Stop', exact: true }).findElement();
  expect(button.textContent?.trim()).toBe('');
  expect(button.querySelector('svg[aria-hidden="true"]')).not.toBeNull();
  expect(button).toHaveAttribute('title', label);
  expect(button).toHaveAttribute('data-nc-queue-send');
  expect(button.getAttribute('data-nc-send-attachment')).toBe(imageOnly ? '' : null);

  const queueBounds = button.getBoundingClientRect();
  const stopBounds = stop.getBoundingClientRect();
  const composerBounds = composerGroup.element().getBoundingClientRect();
  expect(queueBounds.width).toBeCloseTo(queueBounds.height, 0);
  expect(queueBounds.width).toBeCloseTo(stopBounds.width, 0);
  expect(Number.parseFloat(getComputedStyle(button).borderTopLeftRadius)).toBeGreaterThanOrEqual(queueBounds.width / 2);
  expect(queueBounds.left).toBeGreaterThanOrEqual(composer().element().getBoundingClientRect().right);
  expect(queueBounds.right).toBeLessThanOrEqual(stopBounds.left);
  for (const bounds of [queueBounds, stopBounds]) {
    expect(bounds.left).toBeGreaterThanOrEqual(Math.max(0, composerBounds.left));
    expect(bounds.right).toBeLessThanOrEqual(Math.min(width, composerBounds.right));
    expect(bounds.top).toBeGreaterThanOrEqual(composerBounds.top);
    expect(bounds.bottom).toBeLessThanOrEqual(composerBounds.bottom);
  }

  await queue.click();
  const posts = () => view.requests.filter(request => request.method === 'POST' && request.path === path);
  await expect.poll(() => posts().length).toBe(1);
  const expectedPayload = { content: text, mode: 'queue' };
  if (imageOnly) {
    const body = form(posts()[0]);
    expect(payload(body)).toMatchObject(expectedPayload);
    expect((body.get('attachments') as File).name).toBe('screenshot.png');
  } else {
    expect(posts()[0].body).toMatchObject(expectedPayload);
  }
});
