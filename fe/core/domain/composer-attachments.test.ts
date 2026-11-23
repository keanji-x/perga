import { describe, expect, it } from 'vitest';
import type { PlannerAttachment } from '../api/generated/wire.js';
import {
  providerStatusDtoSchema,
  sessionItemsPageDtoSchema,
  uploadAttachmentResponseDtoSchema,
} from '../api/perga-v1.js';
import {
  attachmentSendGuards,
  completeDraftAttachment,
  EMPTY_COMPOSER_ATTACHMENTS,
  enqueueDraftAttachments,
  FAILED_ATTACHMENTS_STATUS,
  failDraftAttachment,
  IMAGE_TOO_LARGE_MESSAGE,
  MAX_ATTACHMENT_BYTES,
  reconcileReadyAttachments,
  removeDraftAttachment,
  retryDraftAttachment,
  UNSUPPORTED_IMAGE_TYPE_MESSAGE,
  UPLOADING_ATTACHMENTS_STATUS,
  validateAttachableFile,
} from './composer-attachments.js';
import {
  sendPlannerInputOperation,
  TOO_MANY_IMAGES,
  uploadSessionAttachmentOperation,
  uploadStagedAttachmentOperation,
} from './conversation-commands.js';
import {
  conversationPendingQueue,
  sessionItemsPageDtoToConversationPage,
} from './conversation-display.js';
import { createTrackConversationOperation } from './conversation.js';
import { createTrackOperation } from './track.js';

function serverAttachment(index: number): PlannerAttachment {
  return {
    id: `att_${String(index).padStart(32, '0')}`,
    contentType: 'image/png',
    size: 128 + index,
    url: `/api/v1/sessions/ses_1/attachments/att_${String(index).padStart(32, '0')}`,
  };
}

describe('composer-attachments state machine', () => {
  it('validates allowed image MIME types and 10 MiB byte ceiling', () => {
    expect(validateAttachableFile({ contentType: 'image/png', size: 10 })).toBeNull();
    expect(validateAttachableFile({ contentType: 'image/jpeg', size: 10 })).toBeNull();
    expect(validateAttachableFile({ contentType: 'image/gif', size: 10 })).toBeNull();
    expect(validateAttachableFile({ contentType: 'image/webp', size: MAX_ATTACHMENT_BYTES })).toBeNull();

    expect(validateAttachableFile({ contentType: 'image/svg+xml', size: 10 })).toBe(UNSUPPORTED_IMAGE_TYPE_MESSAGE);
    expect(validateAttachableFile({ contentType: 'text/plain', size: 10 })).toBe(UNSUPPORTED_IMAGE_TYPE_MESSAGE);
    expect(validateAttachableFile({ contentType: 'image/png', size: 0 })).toBe('Image file is empty.');
    expect(validateAttachableFile({ contentType: 'image/png', size: MAX_ATTACHMENT_BYTES + 1 })).toBe(IMAGE_TOO_LARGE_MESSAGE);
  });

  it('admits rapid batches in exact order, caps at 8, and reports rejected preview URLs', () => {
    const candidates = Array.from({ length: 10 }, (_, index) => ({
      localId: `loc-${index}`,
      name: `shot-${index}.png`,
      contentType: 'image/png',
      size: 64,
      previewUrl: `blob:preview-${index}`,
      file: { id: index },
    }));

    const { state, accepted, rejectedPreviewUrls } = enqueueDraftAttachments(EMPTY_COMPOSER_ATTACHMENTS, candidates);
    expect(accepted.map((item) => item.localId)).toEqual([
      'loc-0', 'loc-1', 'loc-2', 'loc-3', 'loc-4', 'loc-5', 'loc-6', 'loc-7',
    ]);
    expect(rejectedPreviewUrls).toEqual(['blob:preview-8', 'blob:preview-9']);
    expect(state.bannerError).toBe(TOO_MANY_IMAGES);
    expect(attachmentSendGuards(state)).toMatchObject({
      hasUploading: true,
      hasFailed: false,
      atCapacity: true,
      sendBlocked: true,
      canSendWithoutText: false,
      statusMessage: UPLOADING_ATTACHMENTS_STATUS,
    });
  });

  it('settles in place preserving paste order, drops file reference on ready, and ignores cancelled slots', () => {
    const { state: s1 } = enqueueDraftAttachments(EMPTY_COMPOSER_ATTACHMENTS, [
      { localId: 'a', name: 'a.png', contentType: 'image/png', size: 10, previewUrl: 'blob:a', file: { tag: 'a' } },
      { localId: 'b', name: 'b.png', contentType: 'image/png', size: 20, previewUrl: 'blob:b', file: { tag: 'b' } },
    ]);

    // Second upload finishes before first: order must remain [a, b].
    const s2 = completeDraftAttachment(s1, 'b', serverAttachment(2));
    expect(s2.items.map((item) => item.localId)).toEqual(['a', 'b']);
    expect(s2.items[1].status).toBe('ready');
    expect('file' in s2.items[1]).toBe(false);
    expect(s2.items[1].previewUrl).toBe('blob:b');

    // Remove uploading 'a', then late completion for 'a' must be ignored.
    const { state: s3, removed } = removeDraftAttachment(s2, 'a');
    expect(removed?.localId).toBe('a');
    const s4 = completeDraftAttachment(s3, 'a', serverAttachment(1));
    expect(s4.items.map((item) => item.localId)).toEqual(['b']);
    expect(attachmentSendGuards(s4)).toMatchObject({
      readyIds: [serverAttachment(2).id],
      hasUploading: false,
      hasFailed: false,
      sendBlocked: false,
      canSendWithoutText: true,
      statusMessage: null,
    });
  });

  it('retains file on failure, blocks send, and retries in place', () => {
    const rawFile = { bytes: 42 };
    const { state: s1 } = enqueueDraftAttachments(EMPTY_COMPOSER_ATTACHMENTS, [
      { localId: 'f1', name: 'fail.png', contentType: 'image/png', size: 42, previewUrl: 'blob:f1', file: rawFile },
    ]);

    const s2 = failDraftAttachment(s1, 'f1', 'Quota exceeded');
    expect(s2.bannerError).toBe('Quota exceeded');
    expect(attachmentSendGuards(s2)).toMatchObject({
      hasUploading: false,
      hasFailed: true,
      sendBlocked: true,
      canSendWithoutText: false,
      statusMessage: FAILED_ATTACHMENTS_STATUS,
    });
    const failedItem = s2.items[0];
    expect(failedItem.status).toBe('failed');
    if (failedItem.status === 'failed') expect(failedItem.file).toBe(rawFile);

    const { state: s3, item: retried } = retryDraftAttachment(s2, 'f1');
    expect(retried?.localId).toBe('f1');
    expect(s3.bannerError).toBeNull();
    expect(s3.items[0].status).toBe('uploading');
  });

  it('reconciles released ready items for URL revocation and hydrates refilled server items', () => {
    const { state: s1 } = enqueueDraftAttachments(EMPTY_COMPOSER_ATTACHMENTS, [
      { localId: 'loc-1', name: 'one.png', contentType: 'image/png', size: 10, previewUrl: 'blob:one', file: {} },
    ]);
    const s2 = completeDraftAttachment(s1, 'loc-1', serverAttachment(1));

    const released = reconcileReadyAttachments(s2, []);
    expect(released.state.items).toHaveLength(0);
    expect(released.removedReady.map((item) => item.previewUrl)).toEqual(['blob:one']);

    const refilled = reconcileReadyAttachments(released.state, [serverAttachment(2)]);
    expect(refilled.state.items).toHaveLength(1);
    expect(refilled.state.items[0].previewUrl).toBe(serverAttachment(2).url);
  });
});

describe('attachment API operations and wire decoding', () => {
  it('builds binary upload operations with lowercase content-type and encoded filename header', () => {
    const bytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47]);
    const sessionOp = uploadSessionAttachmentOperation('ses/1', bytes, 'image/png', { filename: '截图 1.png' });
    expect(sessionOp.method).toBe('POST');
    expect(sessionOp.path).toBe('/api/v1/sessions/ses%2F1/attachments');
    expect(sessionOp.body).toBe(bytes);
    expect(sessionOp.headers).toEqual({
      'content-type': 'image/png',
      'X-Attachment-Filename': encodeURIComponent('截图 1.png'),
    });
    expect(uploadAttachmentResponseDtoSchema.parse({
      attachmentId: 'att_0123456789abcdef0123456789abcdef',
      contentType: 'image/png',
      size: 4,
      url: '/api/v1/sessions/ses%2F1/attachments/att_0123456789abcdef0123456789abcdef',
    }).attachmentId).toBe('att_0123456789abcdef0123456789abcdef');

    const stagedTrackOp = uploadStagedAttachmentOperation(bytes, 'image/webp', { trackId: 'trk_1', filename: 'a.webp' });
    expect(stagedTrackOp.path).toBe('/api/v1/attachments?trackId=trk_1');
    expect(stagedTrackOp.headers?.['content-type']).toBe('image/webp');

    const stagedGlobalOp = uploadStagedAttachmentOperation(bytes, '');
    expect(stagedGlobalOp.path).toBe('/api/v1/attachments');
    expect(stagedGlobalOp.headers).toEqual({ 'content-type': 'application/octet-stream' });
  });

  it('forwards attachmentIds on send, create-session, and create-track operations', () => {
    const sendOp = sendPlannerInputOperation('ses_1', '', ['att_1', 'att_2'], 'key-1');
    expect(sendOp.body).toEqual({ content: '', mode: 'default', attachmentIds: ['att_1', 'att_2'] });

    const createSessionOp = createTrackConversationOperation(
      'trk_1', '', 'key-2', 'codex', { model: null, reasoning_effort: null }, undefined, null, ['att_1'],
    );
    expect(createSessionOp.body).toMatchObject({ prompt: '', provider: 'codex', attachmentIds: ['att_1'] });
    expect(createSessionOp.body).not.toHaveProperty('title');

    const createTrackOp = createTrackOperation({
      area_id: 'area_1',
      planner_provider: 'codex',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
      attachment_ids: ['att_1', 'att_2'],
    }, 'key-3');
    expect(createTrackOp.body).toMatchObject({ areaId: 'area_1', attachmentIds: ['att_1', 'att_2'] });
    expect(createTrackOp.body).not.toHaveProperty('attachment_ids');
  });

  it('decodes and projects transcript item attachments, queued message attachments, and supportsImages', () => {
    const provider = providerStatusDtoSchema.parse({ provider: 'codex', available: true, supportsImages: true });
    expect(provider.supportsImages).toBe(true);
    expect(providerStatusDtoSchema.parse({ provider: 'antigravity', available: true, supportsImages: null }).supportsImages).toBeNull();

    const att = serverAttachment(1);
    const page = sessionItemsPageDtoSchema.parse({
      items: [{
        id: 10,
        sessionId: 'ses_1',
        turnId: 'turn_1',
        kind: 'user',
        payload: { content: '', inputId: 'msg_1' },
        createdAtMs: 1000,
        attachments: [att],
      }],
      state: 'running',
      readonlyReason: null,
      canReopen: false,
      workerPaused: false,
      unknownDetail: null,
      queuedMessages: [{
        id: 'q_1',
        text: 'queued with image',
        queuedAtMs: 1005,
        state: 'waiting',
        attachments: [att],
      }],
      capabilities: {
        models: [],
        permissionModes: [],
        supportsNativeSteer: true,
        supportsSessionResume: true,
        supportsCompact: true,
        supportsImages: true,
      },
      session: {
        id: 'ses_1',
        trackId: 'trk_1',
        provider: 'codex',
        contextUsage: null,
        createdAtMs: 1,
        updatedAtMs: 2,
      },
    });

    expect(page.capabilities?.supportsImages).toBe(true);
    const conversationPage = sessionItemsPageDtoToConversationPage('ses_1', page, null);
    expect(conversationPage.items[0]?.source).toMatchObject({
      kind: 'human',
      text: '',
      attachments: [att],
    });
    expect(conversationPendingQueue(page)).toEqual([{
      entry_id: 'q_1',
      text: 'queued with image',
      rev: 1,
      queued_at_ms: 1005,
      state: 'waiting',
      attachments: [att],
    }]);
  });
});
