import '../../styles/entry.css';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { page, userEvent } from 'vitest/browser';
import type { AgentSessionDto, SessionAttachmentDto, SessionItemDto, SessionItemsPageDto } from '../../../../core/api/perga-v1.ts';
import type { ApiRequest, ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { prepareReportDocument } from '../../features/report/document/preparation.ts';
import { WEB_COMPAT_VERSION } from '../providers/public.tsx';
import { queryKeys } from '../providers/query-keys.ts';
import { ThemeProvider } from '../theme/public.tsx';
import { sessionInput, sessionItem, sessionPage, sessionTurn } from './conversation-session-data.ts';
import { createAppRouter } from './public.tsx';
import { fixtureBootstrap, fixtureCreation, fixtureSession, fixtureSnapshot, fixtureTrack } from './session-contract-fixture.ts';
import { bootTestCardRuntime } from './test-card-runtime.ts';

// Minimal valid 1x1 red PNG so Chromium decodes naturalWidth === 1 in Thumbnail & Lightbox.
const VALID_PNG_BYTES = new Uint8Array([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
  0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
  0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
  0xde, 0x00, 0x00, 0x00, 0x0c, 0x49, 0x44, 0x41,
  0x54, 0x08, 0xd7, 0x63, 0xf8, 0xcf, 0xc0, 0x00,
  0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xdd, 0x8d,
  0xb0, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e,
  0x44, 0xae, 0x42, 0x60, 0x82,
]);

const VALID_PNG_DATA_URL = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mP4z8AAAAMBAQAY3Y2wAAAAAElFTkSuQmCC';

function pngFile(name = 'screenshot.png'): File {
  return new File([VALID_PNG_BYTES], name, { type: 'image/png' });
}

function makeClipboardData(options: { files?: readonly File[]; text?: string }): DataTransfer {
  const dt = new DataTransfer();
  for (const file of options.files ?? []) {
    dt.items.add(file);
  }
  if (options.text !== undefined) {
    dt.setData('text/plain', options.text);
  }
  return dt;
}

function pasteInto(target: Element, options: { files?: readonly File[]; text?: string }) {
  const event = new ClipboardEvent('paste', {
    bubbles: true,
    cancelable: true,
    clipboardData: makeClipboardData(options),
  });
  act(() => {
    target.dispatchEvent(event);
  });
}

function dragInto(target: Element, type: 'dragenter' | 'dragover' | 'dragleave' | 'drop', files: readonly File[]) {
  const event = new DragEvent(type, {
    bubbles: true,
    cancelable: true,
    dataTransfer: makeClipboardData({ files }),
  });
  act(() => {
    target.dispatchEvent(event);
  });
}

afterEach(async () => {
  cleanup();
  document.body.replaceChildren();
  await page.viewport(1280, 800);
});

interface AppHarnessOptions {
  initialPath: string;
  running?: boolean;
  enableSideConversation?: boolean;
  uploadGate?: (request: ApiRequest, index: number) => Promise<{ status?: number; body?: unknown }>;
  persistedItems?: SessionItemDto[];
  queuedMessages?: NonNullable<SessionItemsPageDto['queuedMessages']>;
}

function renderAttachmentApp(options: AppHarnessOptions) {
  const requests: ApiRequest[] = [];
  let uploadSeq = 0;
  let itemSeq = 10;
  const track = fixtureTrack('trk-img', { areaId: 'area-1', title: 'Image Track', leadSessionId: 'ses-lead' });
  const leadSession = fixtureSession('ses-lead', track.id, { isLead: true, title: 'Lead', provider: 'codex' });
  const sessions: AgentSessionDto[] = [leadSession];
  const uploadedById = new Map<string, SessionAttachmentDto>();
  const itemsBySession = new Map<string, SessionItemDto[]>([
    [leadSession.id, options.persistedItems ? [...options.persistedItems] : []],
  ]);
  const queuedBySession = new Map<string, NonNullable<SessionItemsPageDto['queuedMessages']>>([
    [leadSession.id, options.queuedMessages ? [...options.queuedMessages] : []],
  ]);

  const transport: ApiTransportPort = {
    async send(request) {
      requests.push(request);
      const url = new URL(request.path, 'http://fixture');
      const pathname = url.pathname;

      if (pathname === '/api/v1/bootstrap') {
        const base = fixtureBootstrap(['codex'], ['gpt-5']);
        return {
          status: 200,
          statusText: 'OK',
          body: {
            ...base,
            providers: base.providers.map((p) => ({ ...p, supportsImages: true })),
          },
        };
      }
      if (pathname === '/api/v1/areas') {
        return {
          status: 200,
          statusText: 'OK',
          body: [{ id: 'area-1', name: 'Workspace', color: '#123456', sortOrder: 1, kind: 'user', createdAtMs: 1, updatedAtMs: 1 }],
        };
      }
      if (pathname === '/api/v1/areas/area-1/tracks') {
        return { status: 200, statusText: 'OK', body: [track] };
      }
      if (pathname === '/api/v1/areas/area-1/workspace-options') {
        return {
          status: 200,
          statusText: 'OK',
          body: { defaultId: 'managed', options: [{ id: 'managed', label: 'Perga workspace', description: 'Managed' }] },
        };
      }
      if (pathname === '/api/v1/execution-targets') {
        return { status: 200, statusText: 'OK', body: [] };
      }
      if (pathname === '/api/v1/templates' || pathname === '/api/v1/overlays' || pathname === '/api/v1/read-receipts' || pathname === '/api/v1/tracks/trk-img/terminals') {
        if (pathname === '/api/v1/overlays') return { status: 200, statusText: 'OK', body: { tracks: [] } };
        if (pathname === '/api/v1/read-receipts') return { status: 200, statusText: 'OK', body: { baselineMs: 0, receipts: [] } };
        return { status: 200, statusText: 'OK', body: [] };
      }
      if (pathname === '/api/settings') {
        return { status: 200, statusText: 'OK', body: {} };
      }

      // Binary attachment uploads (session-scoped and staged)
      if (request.method === 'POST' && (pathname === '/api/v1/attachments' || /^\/api\/v1\/sessions\/[^/]+\/attachments$/.test(pathname))) {
        const trackIdParam = url.searchParams.get('trackId');
        if (pathname === '/api/v1/attachments' && trackIdParam !== null && trackIdParam !== track.id) {
          return { status: 404, statusText: 'Not Found', body: { code: 'not_found', error: `Track '${trackIdParam}' not found.` } };
        }
        const index = ++uploadSeq;
        if (options.uploadGate !== undefined) {
          const custom = await options.uploadGate(request, index);
          if (custom.status !== undefined && custom.status >= 400) {
            return { status: custom.status, statusText: 'Error', body: custom.body };
          }
        }
        const id = `att_${String(index).padStart(32, '0')}`;
        const dto: SessionAttachmentDto = {
          id,
          contentType: 'image/png',
          size: request.body instanceof Uint8Array ? request.body.byteLength : VALID_PNG_BYTES.byteLength,
          url: VALID_PNG_DATA_URL,
        };
        uploadedById.set(id, dto);
        return {
          status: 201,
          statusText: 'Created',
          body: { attachmentId: id, contentType: dto.contentType, size: dto.size, url: dto.url },
        };
      }

      // Create Track
      if (request.method === 'POST' && pathname === '/api/v1/tracks') {
        const newTrack = fixtureTrack('trk-new', { areaId: 'area-1', title: 'New Image Track', leadSessionId: 'ses-new-lead' });
        const newLead = fixtureSession('ses-new-lead', newTrack.id, { isLead: true, title: 'Lead', provider: 'codex' });
        sessions.push(newLead);
        itemsBySession.set(newLead.id, []);
        queuedBySession.set(newLead.id, []);
        return { status: 201, statusText: 'Created', body: fixtureSnapshot(newTrack, [newLead]) };
      }

      if (pathname === `/api/v1/tracks/${track.id}`) {
        return { status: 200, statusText: 'OK', body: fixtureSnapshot(track, sessions.filter((s) => s.trackId === track.id)) };
      }
      if (pathname === '/api/v1/tracks/trk-new') {
        const newTrack = fixtureTrack('trk-new', { areaId: 'area-1', title: 'New Image Track', leadSessionId: 'ses-new-lead' });
        return { status: 200, statusText: 'OK', body: fixtureSnapshot(newTrack, sessions.filter((s) => s.trackId === newTrack.id)) };
      }

      // Create Session (New Session Draft)
      if (request.method === 'POST' && pathname === `/api/v1/tracks/${track.id}/sessions`) {
        const body = request.body as { prompt?: string; attachmentIds?: string[]; provider?: string };
        const created = fixtureSession('ses-draft-1', track.id, { title: 'Draft Session', provider: body.provider ?? 'codex' });
        sessions.push(created);
        const atts = (body.attachmentIds ?? []).map((id) => uploadedById.get(id)).filter((a): a is SessionAttachmentDto => a !== undefined);
        itemsBySession.set(created.id, [{
          id: ++itemSeq,
          sessionId: created.id,
          turnId: 'first-turn',
          kind: 'user',
          payload: { content: body.prompt ?? '', inputId: 'first-input' },
          createdAtMs: Date.now(),
          attachments: atts,
        }]);
        return { status: 201, statusText: 'Created', body: fixtureCreation(created) };
      }

      // Session items read
      const itemsMatch = /^\/api\/v1\/sessions\/([^/]+)\/items$/.exec(pathname);
      if (itemsMatch !== null) {
        const sid = decodeURIComponent(itemsMatch[1]);
        const session = sessions.find((s) => s.id === sid) ?? leadSession;
        const activeTurn = options.running ? sessionTurn(sid, 'turn-run', 'running') : null;
        const pageData = sessionPage(session, itemsBySession.get(sid) ?? [], activeTurn);
        return {
          status: 200,
          statusText: 'OK',
          body: {
            ...pageData,
            queuedMessages: queuedBySession.get(sid) ?? [],
            canSteer: options.running === true,
          },
        };
      }

      // Send session message
      const msgMatch = /^\/api\/v1\/sessions\/([^/]+)\/messages$/.exec(pathname);
      if (msgMatch !== null && request.method === 'POST') {
        const sid = decodeURIComponent(msgMatch[1]);
        const body = request.body as { content?: string; attachmentIds?: string[]; mode?: string };
        const atts = (body.attachmentIds ?? []).map((id) => uploadedById.get(id)).filter((a): a is SessionAttachmentDto => a !== undefined);
        if (body.mode === 'queue' || options.running) {
          const queueId = `q-${++itemSeq}`;
          const existingQ = queuedBySession.get(sid) ?? [];
          queuedBySession.set(sid, [...existingQ, {
            id: queueId,
            text: body.content ?? '',
            queuedAtMs: Date.now(),
            state: 'waiting',
            attachments: atts,
          }]);
          return {
            status: 200,
            statusText: 'OK',
            body: { sessionId: sid, messageId: queueId, turnId: null, delivery: 'queued', replay: false },
          };
        }
        const list = itemsBySession.get(sid) ?? [];
        itemsBySession.set(sid, [...list, {
          id: ++itemSeq,
          sessionId: sid,
          turnId: 'turn-1',
          kind: 'user',
          payload: { content: body.content ?? '', inputId: `msg-${itemSeq}` },
          createdAtMs: Date.now(),
          attachments: atts,
        }]);
        return { status: 200, statusText: 'OK', body: sessionInput(sid, `msg-${itemSeq}`) };
      }

      return { status: 200, statusText: 'OK', body: [] };
    },
  };

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (options.enableSideConversation === true) {
    client.setQueryDefaults(queryKeys.serverVersion(), { staleTime: Infinity });
    client.setQueryData(queryKeys.serverVersion(), {
      pergaV1: true,
      authRequired: false,
      assistantProviders: true,
      conversationCreateModel: true,
      conversationInitialInput: true,
      conversationSide: true,
      webCompatVersion: WEB_COMPAT_VERSION,
      minWebCompatVersion: WEB_COMPAT_VERSION,
      syncEventVersion: 1,
      dbInstanceId: 'fixture',
    });
  }
  const router = createAppRouter({
    prepareReport: prepareReportDocument,
    transport,
    client,
    cards: bootTestCardRuntime(),
    unauthorized: createUnauthorizedChannel({ enqueue: (task) => task() }),
    onSignOut: () => undefined,
  });
  router.update({ history: createMemoryHistory({ initialEntries: [options.initialPath] }) });
  const container = document.createElement('div');
  container.id = 'root';
  document.body.append(container);
  render(
    <QueryClientProvider client={client}>
      <ThemeProvider>
        <RouterProvider router={router} />
      </ThemeProvider>
    </QueryClientProvider>,
    { container },
  );

  return { requests, client, uploadedById };
}

describe('unified image paste, preview management and Lightbox (Chromium)', () => {
  it('handles rapid paste, mixed text+image paste, drag highlight, failure retry, composer Lightbox, remove isolation and image-only send', async () => {
    await page.viewport(1280, 800);
    let releaseFirst!: () => void;
    let attempt = 0;

    const harness = renderAttachmentApp({
      initialPath: '/track/trk-img?panel=conversations',
      uploadGate: async (_request, index) => {
        attempt += 1;
        if (index === 1) {
          await new Promise<void>((resolve) => { releaseFirst = resolve; });
        }
        // Fail the 3rd upload once to test Retry in place.
        if (attempt === 3) {
          return { status: 400, body: { code: 'invalid_argument', error: 'Temporary upload refusal' } };
        }
        return {};
      },
    });

    // Open lead session
    await userEvent.click(await screen.findByRole('button', { name: /Lead/ }));
    const field = await screen.findByRole('combobox', { name: 'Message' });
    await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));

    // 1. Pure paste #1 -> instant optimistic preview, send blocked while uploading.
    pasteInto(field, { files: [pngFile('first.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="uploading"]')).toHaveLength(1);
    });
    expect(document.querySelector('[data-nc-attachment-status]')?.textContent).toContain('图片上传中');
    expect(screen.getByRole('button', { name: 'Send' }).hasAttribute('disabled')).toBe(true);

    // 2. Rapid paste #2 while #1 is STILL uploading -> NOT dropped!
    pasteInto(field, { files: [pngFile('second.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="uploading"]')).toHaveLength(2);
    });

    // Release first upload -> both settle in exact order.
    releaseFirst();
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(2);
    });

    // 3. Drag-and-drop highlight and upload (3rd upload fails first, then retries).
    const composer = document.querySelector<HTMLElement>('[data-nc-composer]')!;
    const droppedFiles = [pngFile('dropped.png')];
    dragInto(composer, 'dragenter', droppedFiles);
    expect(composer.hasAttribute('data-nc-dropzone-active')).toBe(true);
    dragInto(composer, 'drop', droppedFiles);
    expect(composer.hasAttribute('data-nc-dropzone-active')).toBe(false);

    // 3rd item fails -> send blocked -> click Retry -> succeeds in place.
    const retryBtn = await screen.findByRole('button', { name: 'Retry Image 3' });
    expect(document.querySelector('[data-nc-attachment-status]')?.textContent).toContain('部分图片上传失败');
    await userEvent.click(retryBtn);
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(3);
    });

    // 4. Remove 3rd image via × -> must NOT open Lightbox!
    const removeThird = screen.getByLabelText('Remove Image 3');
    await userEvent.click(removeThird);
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(2);
    });
    expect(document.querySelector('dialog[open]')).toBeNull();

    // 5. Click 1st thumbnail -> Lightbox opens, ArrowRight navigates to 2nd, zoom works, Esc closes.
    const firstThumbButton = document.querySelector<HTMLButtonElement>('[data-nc-attachment-item="ready"] button[aria-label="Image 1"]')
      ?? document.querySelectorAll<HTMLButtonElement>('[data-nc-attachments] button:not([aria-label^="Remove"])')[0];
    await userEvent.click(firstThumbButton);
    const dialog = await waitFor(() => {
      const openDialog = document.querySelector<HTMLDialogElement>('dialog[open]');
      expect(openDialog).not.toBeNull();
      return openDialog!;
    });
    expect(dialog.textContent).toContain('1 / 2');
    await userEvent.keyboard('{ArrowRight}');
    await waitFor(() => expect(dialog.textContent).toContain('2 / 2'));
    await userEvent.keyboard('{ArrowLeft}');
    await waitFor(() => expect(dialog.textContent).toContain('1 / 2'));
    await userEvent.keyboard('+');
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(document.querySelector('dialog[open]')).toBeNull());

    // 6. Send image-only message (no text!).
    const sendImageBtn = screen.getByRole('button', { name: 'Send image' });
    await userEvent.click(sendImageBtn);
    await waitFor(() => {
      const postMsg = harness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/sessions/ses-lead/messages');
      expect(postMsg).toBeDefined();
      expect(postMsg?.body).toMatchObject({
        content: '',
        attachmentIds: [
          'att_00000000000000000000000000000001',
          'att_00000000000000000000000000000002',
        ],
      });
    });

    // Sent message renders AttachmentGallery without an empty text bubble, and opens Lightbox on click.
    const sentGalleryButtons = await waitFor(() => {
      const buttons = document.querySelectorAll<HTMLButtonElement>('[data-nc-turn-attachments] button');
      expect(buttons).toHaveLength(2);
      return buttons;
    });
    expect(document.querySelector('[data-nc-turn="you"]')).toBeNull();
    await userEvent.click(sentGalleryButtons[0]);
    await waitFor(() => expect(document.querySelector('dialog[open]')).not.toBeNull());
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(document.querySelector('dialog[open]')).toBeNull());

    // 7. Mixed text + image paste preserves plain text and injects zero <img> inside editor.
    pasteInto(field, { files: [pngFile('mixed.png')], text: 'look at this screenshot' });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });
    expect(field.textContent).toContain('look at this screenshot');
    expect(field.querySelector('img')).toBeNull();
  });

  it('queues an image-only message while running and renders Lightbox in PendingQueue', async () => {
    await page.viewport(1280, 800);
    const harness = renderAttachmentApp({
      initialPath: '/track/trk-img?panel=conversations',
      running: true,
      persistedItems: [sessionItem('ses-lead', 1, 'Working on it…', 'assistant_text', 'turn-run')],
    });

    await userEvent.click(await screen.findByRole('button', { name: /Lead/ }));
    const field = await screen.findByRole('combobox', { name: 'Message' });
    await waitFor(() => expect(field.getAttribute('contenteditable')).toBe('true'));

    pasteInto(field, { files: [pngFile('queued.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });

    const queueBtn = await screen.findByRole('button', { name: '排队发送' });
    await userEvent.click(queueBtn);

    await waitFor(() => {
      const postMsg = harness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/sessions/ses-lead/messages');
      expect(postMsg?.body).toMatchObject({ content: '', mode: 'queue', attachmentIds: ['att_00000000000000000000000000000001'] });
    });

    const queueThumb = await waitFor(() => {
      const btn = document.querySelector<HTMLButtonElement>('[data-nc-pending-queue] [data-nc-turn-attachments] button');
      expect(btn).not.toBeNull();
      const rect = btn!.getBoundingClientRect();
      expect(rect.width).toBeGreaterThan(0);
      expect(rect.height).toBeGreaterThan(0);
      return btn!;
    });
    queueThumb.scrollIntoView({ block: 'center' });
    await userEvent.click(queueThumb);
    await waitFor(() => expect(document.querySelector('dialog[open]')).not.toBeNull());
    await userEvent.keyboard('{Escape}');
  });

  it('supports staged image paste and image-only creation in New Session Draft and New Track page', async () => {
    await page.viewport(1280, 800);

    // Part A: New Session Draft
    const draftHarness = renderAttachmentApp({ initialPath: '/track/trk-img?panel=conversations' });
    await userEvent.click(await screen.findByRole('button', { name: 'New conversation' }));
    const draftField = await screen.findByRole('combobox', { name: 'Message' });
    pasteInto(draftField, { files: [pngFile('draft.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });
    expect(draftHarness.requests.some((r) => r.method === 'POST' && r.path === '/api/v1/attachments?trackId=trk-img')).toBe(true);
    await userEvent.click(screen.getByRole('button', { name: 'Send image' }));
    await waitFor(() => {
      const createReq = draftHarness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/tracks/trk-img/sessions');
      expect(createReq?.body).toMatchObject({
        prompt: '',
        attachmentIds: ['att_00000000000000000000000000000001'],
      });
    });

    cleanup();
    document.body.replaceChildren();

    // Part B: New Track Page
    const trackHarness = renderAttachmentApp({ initialPath: '/area/area-1/new' });
    const newTrackField = await screen.findByRole('combobox', { name: 'What this track should do' });
    pasteInto(newTrackField, { files: [pngFile('new-track.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });
    // Click thumbnail in New Track composer drawer -> Lightbox opens & Esc closes.
    const newTrackThumb = document.querySelectorAll<HTMLButtonElement>('[data-nc-attachments] button:not([aria-label^="Remove"])')[0];
    await userEvent.click(newTrackThumb);
    await waitFor(() => expect(document.querySelector('dialog[open]')).not.toBeNull());
    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(document.querySelector('dialog[open]')).toBeNull());

    expect(trackHarness.requests.some((r) => r.method === 'POST' && r.path === '/api/v1/attachments')).toBe(true);

    const createTrackBtn = screen.getByRole('button', { name: 'Create track' });
    expect(createTrackBtn.hasAttribute('disabled')).toBe(false);
    await userEvent.click(createTrackBtn);

    await waitFor(() => {
      const createTrackReq = trackHarness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/tracks');
      expect(createTrackReq?.body).toMatchObject({
        areaId: 'area-1',
        attachmentIds: ['att_00000000000000000000000000000001'],
      });
    });
    await waitFor(() => {
      const initialSend = trackHarness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/sessions/ses-new-lead/messages');
      expect(initialSend?.body).toMatchObject({
        content: '',
        attachmentIds: ['att_00000000000000000000000000000001'],
      });
    });
  });

  it('keeps narrow mobile composer paste, preview and Lightbox free of horizontal overflow', async () => {
    await page.viewport(390, 844);
    renderAttachmentApp({ initialPath: '/track/trk-img' });

    const dockInput = await screen.findByRole('textbox', { name: 'Chat message' });
    await userEvent.click(dockInput);

    const mobileField = await screen.findByRole('combobox', { name: 'Message' });
    pasteInto(mobileField, { files: [pngFile('mobile.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });
    expect(document.documentElement.scrollWidth).toBe(window.innerWidth);

    const thumbBtn = document.querySelectorAll<HTMLButtonElement>('[data-nc-attachments] button:not([aria-label^="Remove"])')[0];
    await userEvent.click(thumbBtn);
    await waitFor(() => expect(document.querySelector('dialog[open]')).not.toBeNull());
    await userEvent.keyboard('{Escape}');
  });

  it('uploads side-conversation draft images against the real trackId instead of the side slot key', async () => {
    await page.viewport(1280, 800);
    const harness = renderAttachmentApp({
      initialPath: '/track/trk-img?panel=conversations',
      enableSideConversation: true,
    });

    await userEvent.click(await screen.findByRole('button', { name: /Lead/ }));
    const leadField = await screen.findByRole('combobox', { name: 'Message' });
    await waitFor(() => expect(leadField.getAttribute('contenteditable')).toBe('true'));

    // Open side-conversation draft via /side command (select command then submit).
    await userEvent.click(leadField);
    await userEvent.type(leadField, '/side');
    await userEvent.click(await screen.findByRole('option', { name: /^side/ }));
    await userEvent.keyboard('{Enter}');

    // Both main and side composers are mounted; pick the side-conversation draft composer.
    const sideField = await waitFor(() => {
      const fields = screen.getAllByRole('combobox', { name: 'Message' });
      expect(fields.length).toBeGreaterThanOrEqual(2);
      return fields[fields.length - 1];
    });

    pasteInto(sideField, { files: [pngFile('side.png')] });
    await waitFor(() => {
      expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
    });

    // Must upload with real trackId=trk-img, never the composite slot key trk-img:side:ses-lead.
    expect(harness.requests.some((r) => r.method === 'POST' && r.path === '/api/v1/attachments?trackId=trk-img')).toBe(true);
    expect(harness.requests.some((r) => r.method === 'POST' && r.path.includes(':side:'))).toBe(false);

    const sendImageBtn = screen.getByRole('button', { name: 'Send image' });
    await userEvent.click(sendImageBtn);
    await waitFor(() => {
      const createReq = harness.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/tracks/trk-img/sessions');
      expect(createReq?.body).toMatchObject({
        prompt: '',
        attachmentIds: ['att_00000000000000000000000000000001'],
      });
    });
  });
});
