// Platform-independent draft attachment state machine shared by all composer surfaces.

import type { PlannerAttachment } from '../api/generated/wire.js';
import {
  ATTACHABLE_IMAGE_TYPES,
  MAX_ATTACHMENTS_PER_MESSAGE,
  TOO_MANY_IMAGES,
} from './conversation-commands.js';

/** Matches `AttachmentUploadLimits.MaxFileBytes` on the Host (10 MiB). */
export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;

export const UNSUPPORTED_IMAGE_TYPE_MESSAGE = 'That file is not one of PNG, JPEG, GIF or WebP.';
export const IMAGE_TOO_LARGE_MESSAGE = 'Image exceeds the 10 MiB size limit.';
export const EMPTY_IMAGE_FILE_MESSAGE = 'Image file is empty.';
export const UPLOADING_ATTACHMENTS_STATUS = '图片上传中，请稍候…';
export const FAILED_ATTACHMENTS_STATUS = '部分图片上传失败，请重试或移除后再发送。';

export type DraftAttachmentStatus = 'uploading' | 'ready' | 'failed';

export type ComposerAttachmentItem<TFile = unknown> =
  | Readonly<{
      localId: string;
      status: 'uploading';
      name: string;
      contentType: string;
      size: number;
      previewUrl: string;
      file: TFile;
    }>
  | Readonly<{
      localId: string;
      status: 'ready';
      name: string;
      contentType: string;
      size: number;
      previewUrl: string;
      attachment: PlannerAttachment;
    }>
  | Readonly<{
      localId: string;
      status: 'failed';
      name: string;
      contentType: string;
      size: number;
      previewUrl: string;
      file: TFile;
      error: string;
    }>;

export type ComposerAttachmentState<TFile = unknown> = Readonly<{
  items: readonly ComposerAttachmentItem<TFile>[];
  bannerError: string | null;
}>;

export const EMPTY_COMPOSER_ATTACHMENTS: ComposerAttachmentState<never> = Object.freeze({
  items: Object.freeze([]),
  bannerError: null,
});

export type AttachableFileCandidate<TFile = unknown> = Readonly<{
  localId: string;
  name: string;
  contentType: string;
  size: number;
  previewUrl: string;
  file: TFile;
}>;

export type AttachmentSendGuards = Readonly<{
  readyAttachments: readonly PlannerAttachment[];
  readyIds: readonly string[];
  hasUploading: boolean;
  hasFailed: boolean;
  atCapacity: boolean;
  canSendWithoutText: boolean;
  sendBlocked: boolean;
  statusMessage: string | null;
}>;

/** Client-side preflight check mirroring server allowlist and byte cap; server magic-byte sniffing remains authoritative. */
export function validateAttachableFile(meta: Readonly<{ contentType: string; size: number }>): string | null {
  if (!(ATTACHABLE_IMAGE_TYPES as readonly string[]).includes(meta.contentType)) {
    return UNSUPPORTED_IMAGE_TYPE_MESSAGE;
  }
  if (meta.size <= 0) {
    return EMPTY_IMAGE_FILE_MESSAGE;
  }
  if (meta.size > MAX_ATTACHMENT_BYTES) {
    return IMAGE_TOO_LARGE_MESSAGE;
  }
  return null;
}

/** Slots currently occupying the 8-image message budget (`uploading` + `ready`). */
export function activeAttachmentCount<TFile>(state: ComposerAttachmentState<TFile>): number {
  let count = 0;
  for (const item of state.items) {
    if (item.status === 'uploading' || item.status === 'ready') count += 1;
  }
  return count;
}

/**
 * Synchronously admit a batch of pasted/dropped/picked files in exact order.
 * Invalid or over-capacity files are rejected without dropping valid predecessors;
 * callers must revoke every URL returned in `rejectedPreviewUrls`.
 */
export function enqueueDraftAttachments<TFile>(
  state: ComposerAttachmentState<TFile>,
  candidates: readonly AttachableFileCandidate<TFile>[],
): Readonly<{
  state: ComposerAttachmentState<TFile>;
  accepted: readonly Extract<ComposerAttachmentItem<TFile>, { status: 'uploading' }>[];
  rejectedPreviewUrls: readonly string[];
}> {
  if (candidates.length === 0) {
    return { state, accepted: [], rejectedPreviewUrls: [] };
  }
  let usedSlots = activeAttachmentCount(state);
  const accepted: Extract<ComposerAttachmentItem<TFile>, { status: 'uploading' }>[] = [];
  const rejectedPreviewUrls: string[] = [];
  let nextBannerError: string | null = null;

  for (const candidate of candidates) {
    const validationError = validateAttachableFile(candidate);
    if (validationError !== null) {
      nextBannerError = validationError;
      if (candidate.previewUrl !== '') rejectedPreviewUrls.push(candidate.previewUrl);
      continue;
    }
    if (usedSlots >= MAX_ATTACHMENTS_PER_MESSAGE) {
      nextBannerError = TOO_MANY_IMAGES;
      if (candidate.previewUrl !== '') rejectedPreviewUrls.push(candidate.previewUrl);
      continue;
    }
    usedSlots += 1;
    accepted.push({
      localId: candidate.localId,
      status: 'uploading',
      name: candidate.name,
      contentType: candidate.contentType,
      size: candidate.size,
      previewUrl: candidate.previewUrl,
      file: candidate.file,
    });
  }

  const nextState: ComposerAttachmentState<TFile> = {
    items: accepted.length === 0 ? state.items : [...state.items, ...accepted],
    bannerError: nextBannerError,
  };
  return { state: nextState, accepted, rejectedPreviewUrls };
}

/**
 * Settle an uploading slot in place while preserving paste order and dropping its raw `file` reference.
 * If the slot was removed while uploading, returns `state` unchanged so cancelled uploads never reappear.
 */
export function completeDraftAttachment<TFile>(
  state: ComposerAttachmentState<TFile>,
  localId: string,
  attachment: PlannerAttachment,
): ComposerAttachmentState<TFile> {
  const index = state.items.findIndex((item) => item.localId === localId && item.status === 'uploading');
  if (index < 0) return state;
  const current = state.items[index];
  const settled: ComposerAttachmentItem<TFile> = {
    localId: current.localId,
    status: 'ready',
    name: current.name,
    contentType: attachment.contentType,
    size: attachment.size,
    previewUrl: current.previewUrl !== '' ? current.previewUrl : attachment.url,
    attachment,
  };
  const items = state.items.map((item, idx) => (idx === index ? settled : item));
  return { ...state, items };
}

/**
 * Mark an uploading slot as failed in place, retaining `file` and `previewUrl` for in-place Retry.
 * If the slot was removed while uploading, returns `state` unchanged.
 */
export function failDraftAttachment<TFile>(
  state: ComposerAttachmentState<TFile>,
  localId: string,
  error: string,
): ComposerAttachmentState<TFile> {
  const index = state.items.findIndex((item) => item.localId === localId && item.status === 'uploading');
  if (index < 0) return state;
  const current = state.items[index];
  if (current.status !== 'uploading') return state;
  const failed: ComposerAttachmentItem<TFile> = {
    localId: current.localId,
    status: 'failed',
    name: current.name,
    contentType: current.contentType,
    size: current.size,
    previewUrl: current.previewUrl,
    file: current.file,
    error,
  };
  const items = state.items.map((item, idx) => (idx === index ? failed : item));
  return { items, bannerError: error };
}

/** Re-enqueue a failed slot in place if capacity allows. */
export function retryDraftAttachment<TFile>(
  state: ComposerAttachmentState<TFile>,
  localId: string,
): Readonly<{
  state: ComposerAttachmentState<TFile>;
  item: Extract<ComposerAttachmentItem<TFile>, { status: 'uploading' }> | null;
}> {
  const index = state.items.findIndex((item) => item.localId === localId && item.status === 'failed');
  if (index < 0) return { state, item: null };
  if (activeAttachmentCount(state) >= MAX_ATTACHMENTS_PER_MESSAGE) {
    return { state: { ...state, bannerError: TOO_MANY_IMAGES }, item: null };
  }
  const current = state.items[index];
  if (current.status !== 'failed') return { state, item: null };
  const retried: Extract<ComposerAttachmentItem<TFile>, { status: 'uploading' }> = {
    localId: current.localId,
    status: 'uploading',
    name: current.name,
    contentType: current.contentType,
    size: current.size,
    previewUrl: current.previewUrl,
    file: current.file,
  };
  const items = state.items.map((item, idx) => (idx === index ? retried : item));
  const remainingFailure = items.find((item) => item.status === 'failed');
  return {
    state: { items, bannerError: remainingFailure?.status === 'failed' ? remainingFailure.error : null },
    item: retried,
  };
}

/** Remove an attachment slot (uploading, ready, or failed) by `localId` or server `attachment.id`. */
export function removeDraftAttachment<TFile>(
  state: ComposerAttachmentState<TFile>,
  id: string,
): Readonly<{
  state: ComposerAttachmentState<TFile>;
  removed: ComposerAttachmentItem<TFile> | null;
}> {
  const removed = state.items.find(
    (item) => item.localId === id || (item.status === 'ready' && item.attachment.id === id),
  ) ?? null;
  if (removed === null) return { state, removed: null };
  const items = state.items.filter((item) => item !== removed);
  const remainingFailure = items.find((item) => item.status === 'failed');
  const nextError = remainingFailure?.status === 'failed'
    ? remainingFailure.error
    : (removed.status === 'failed' && state.bannerError === removed.error ? null
      : state.bannerError === TOO_MANY_IMAGES && activeAttachmentCount({ items, bannerError: null }) < MAX_ATTACHMENTS_PER_MESSAGE
        ? null
        : state.bannerError);
  return { state: { items, bannerError: nextError }, removed };
}

/**
 * Reconcile local draft state with authoritative ready server attachments (for example after
 * `releaseComposerImages` clears sent items or `refillComposer` restores failed-send images).
 */
export function reconcileReadyAttachments<TFile>(
  state: ComposerAttachmentState<TFile>,
  serverItems: readonly PlannerAttachment[],
): Readonly<{
  state: ComposerAttachmentState<TFile>;
  removedReady: readonly Extract<ComposerAttachmentItem<TFile>, { status: 'ready' }>[];
}> {
  const serverById = new Map(serverItems.map((item) => [item.id, item] as const));
  const removedReady: Extract<ComposerAttachmentItem<TFile>, { status: 'ready' }>[] = [];
  const kept: ComposerAttachmentItem<TFile>[] = [];
  const seenServerIds = new Set<string>();

  for (const item of state.items) {
    if (item.status !== 'ready') {
      kept.push(item);
      continue;
    }
    const server = serverById.get(item.attachment.id);
    if (server === undefined) {
      removedReady.push(item);
      continue;
    }
    seenServerIds.add(server.id);
    kept.push(server === item.attachment ? item : { ...item, attachment: server });
  }

  for (const server of serverItems) {
    if (seenServerIds.has(server.id)) continue;
    kept.push({
      localId: `server:${server.id}`,
      status: 'ready',
      name: server.id,
      contentType: server.contentType,
      size: server.size,
      previewUrl: server.url,
      attachment: server,
    });
  }

  const unchanged = removedReady.length === 0
    && kept.length === state.items.length
    && kept.every((item, index) => item === state.items[index]);
  if (unchanged) return { state, removedReady };
  return {
    state: {
      items: kept,
      bannerError: kept.length === 0 ? null : state.bannerError,
    },
    removedReady,
  };
}

/** Derive send availability, capacity, and inline status copy from draft attachment state. */
export function attachmentSendGuards<TFile>(state: ComposerAttachmentState<TFile>): AttachmentSendGuards {
  const readyAttachments: PlannerAttachment[] = [];
  const readyIds: string[] = [];
  let hasUploading = false;
  let hasFailed = false;
  for (const item of state.items) {
    if (item.status === 'ready') {
      readyAttachments.push(item.attachment);
      readyIds.push(item.attachment.id);
    } else if (item.status === 'uploading') {
      hasUploading = true;
    } else {
      hasFailed = true;
    }
  }
  const atCapacity = readyAttachments.length + state.items.filter((item) => item.status === 'uploading').length >= MAX_ATTACHMENTS_PER_MESSAGE;
  const sendBlocked = hasUploading || hasFailed;
  const canSendWithoutText = readyAttachments.length > 0 && !sendBlocked;
  const statusMessage = hasUploading
    ? UPLOADING_ATTACHMENTS_STATUS
    : hasFailed
      ? FAILED_ATTACHMENTS_STATUS
      : null;
  return {
    readyAttachments,
    readyIds,
    hasUploading,
    hasFailed,
    atCapacity,
    canSendWithoutText,
    sendBlocked,
    statusMessage,
  };
}
