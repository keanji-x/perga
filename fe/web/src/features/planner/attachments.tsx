// Images a person attaches to a composer message across sessions, drafts and new tracks.
// Picking/pasting/dropping files allocates instant optimistic ObjectURL previews, uploads them
// in order, supports per-item Retry/Remove, and revokes ObjectURLs on remove/release/unmount.

import { useCallback, useEffect, useMemo, useRef } from 'react';
import type {
  PlannerAttachment,
  UploadAttachmentResponse,
} from '../../../../core/api/generated/wire.js';
import {
  attachmentSendGuards,
  completeDraftAttachment,
  EMPTY_COMPOSER_ATTACHMENTS,
  enqueueDraftAttachments,
  failDraftAttachment,
  reconcileReadyAttachments,
  removeDraftAttachment,
  retryDraftAttachment,
  type ComposerAttachmentItem,
  type ComposerAttachmentState,
} from '../../../../core/domain/composer-attachments.js';
import {
  ATTACHABLE_IMAGE_TYPES,
  TOO_MANY_IMAGES,
} from '../../../../core/domain/conversation-commands.js';
import {
  DraftAttachmentStrip,
  ImageAttachButton,
  type DraftAttachmentPreview,
} from '../../ui/image-attachments/public.tsx';
import { useState } from '../../ui/state/public.ts';

/**
 * One upload, already bound to a session or staged scope.
 * Captures recovery admission before reading bytes, and returns a reader that checks the same
 * admission before exposing server facts.
 */
export type UploadAttachment = (
  readBytes: () => Promise<Uint8Array>,
  contentType: string,
  options?: Readonly<{ filename?: string; signal?: AbortSignal }>,
) => Promise<() => UploadAttachmentResponse>;

/** Whether the active session or provider accepts image attachments, and why not when disabled. */
export type AttachmentSupport = Readonly<{ available: boolean; reason?: string }>;

export const IMAGE_UNSUPPORTED_REASON =
  'This provider or session does not support image attachments.';

/** Preserved export for legacy test imports; both managed and direct workspaces now support images. */
export const ATTACHED_WORKSPACE_REASON =
  'This track works in a folder you own, and Perga never writes into one. Images can be attached on tracks with a managed workspace.';

/** One scope's upload counts and refusal banner state held in the owning store. */
export type UploadState = Readonly<{ inFlight: number; refusal: string | null }>;

export const NO_UPLOAD: UploadState = Object.freeze({ inFlight: 0, refusal: null });

/**
 * Where each scope's uploaded ready images and upload counters live so they survive
 * closing, switching and remounts.
 */
export type AttachmentStore = Readonly<{
  /** The ready uploaded images of the active scope's composer. */
  items: readonly PlannerAttachment[];
  update: (cardId: string, next: (items: readonly PlannerAttachment[]) => readonly PlannerAttachment[]) => void;
  /** The active scope's upload state. */
  upload: UploadState;
  editUpload: (cardId: string, next: (current: UploadState) => UploadState) => void;
}>;

export type PlannerAttachments = Readonly<{
  items: readonly PlannerAttachment[];
  /** Ready server attachment IDs in exact send order. */
  ids: readonly string[];
  /** All composer slots (`uploading` | `ready` | `failed`) in exact order for preview strip rendering. */
  draftItems: readonly DraftAttachmentPreview[];
  attach: (file: File) => Promise<void>;
  attachMany: (files: readonly File[]) => Promise<void>;
  remove: (id: string) => void;
  retry: (id: string) => void;
  clear: () => void;
  /** An upload of the active scope is in flight. */
  busy: boolean;
  hasUploading: boolean;
  hasFailed: boolean;
  /** True while uploads are in flight or failed, blocking premature sends. */
  sendBlocked: boolean;
  /** True when at least one ready attachment exists and nothing is uploading or failed. */
  canSendWithoutText: boolean;
  /** Inline status copy shown while uploading or when failed items block send. */
  statusMessage: string | null;
  /** Last refusal/error banner for the active scope. */
  error: string | null;
  atCapacity: boolean;
}>;

type ScopeLane = {
  draft: ComposerAttachmentState<File>;
  tail: Promise<void> | null;
  abortControllers: Map<string, AbortController>;
  cancelled: Set<string>;
};

function createPreviewObjectUrl(file: File): string {
  if (typeof URL === 'undefined' || typeof URL.createObjectURL !== 'function') return '';
  return URL.createObjectURL(file);
}

function revokePreviewObjectUrl(url: string): void {
  if (url === '') return;
  if (typeof URL !== 'undefined' && typeof URL.revokeObjectURL === 'function') {
    URL.revokeObjectURL(url);
  }
}

function toDraftPreview(item: ComposerAttachmentItem<File>, index: number): DraftAttachmentPreview {
  const label = `Image ${index + 1}`;
  if (item.status === 'ready') {
    return {
      id: item.localId,
      src: item.previewUrl !== '' ? item.previewUrl : item.attachment.url,
      label,
      status: 'ready',
    };
  }
  if (item.status === 'failed') {
    return {
      id: item.localId,
      src: item.previewUrl,
      label,
      status: 'failed',
      error: item.error,
    };
  }
  return {
    id: item.localId,
    src: item.previewUrl,
    label,
    status: 'uploading',
  };
}

/**
 * The composer's images for the open scope.
 * Synchronously allocates optimistic previews in exact paste/pick order and uploads serially per scope.
 */
export function usePlannerAttachments(
  upload: UploadAttachment,
  cardId: string,
  store: AttachmentStore,
): PlannerAttachments {
  const { items, update, editUpload } = store;
  const [, setVersion] = useState(0);
  const bump = useCallback(() => {
    setVersion((current) => current + 1);
  }, []);

  const seqRef = useRef(0);
  const liveObjectUrls = useRef(new Set<string>());
  const lanes = useRef(new Map<string, ScopeLane>());
  const uploadRef = useRef(upload);
  uploadRef.current = upload;
  const storeItemsByScope = useRef(new Map<string, readonly PlannerAttachment[]>());
  storeItemsByScope.current.set(cardId, items);

  const trackUrl = useCallback((file: File): string => {
    const url = createPreviewObjectUrl(file);
    if (url !== '') liveObjectUrls.current.add(url);
    return url;
  }, []);

  const revokeUrl = useCallback((url: string) => {
    if (url === '' || !liveObjectUrls.current.has(url)) return;
    liveObjectUrls.current.delete(url);
    revokePreviewObjectUrl(url);
  }, []);

  const getLane = useCallback((scopeKey: string): ScopeLane => {
    let existing = lanes.current.get(scopeKey);
    if (existing === undefined) {
      existing = {
        draft: EMPTY_COMPOSER_ATTACHMENTS,
        tail: null,
        abortControllers: new Map(),
        cancelled: new Set(),
      };
      lanes.current.set(scopeKey, existing);
    }
    return existing;
  }, []);

  // Reconcile view purely during render, then commit lane sync & ObjectURL revocation in effect.
  const activeLane = getLane(cardId);
  const reconciled = reconcileReadyAttachments(activeLane.draft, items);

  useEffect(() => {
    const lane = getLane(cardId);
    const next = reconcileReadyAttachments(lane.draft, items);
    if (next.state !== lane.draft) {
      lane.draft = next.state;
      for (const removed of next.removedReady) {
        revokeUrl(removed.previewUrl);
      }
      bump();
    }
  }, [bump, cardId, getLane, items, revokeUrl]);

  // Deterministically abort in-flight uploads and revoke every remaining ObjectURL on unmount.
  useEffect(() => {
    const allLanes = lanes.current;
    const allUrls = liveObjectUrls.current;
    return () => {
      for (const lane of allLanes.values()) {
        for (const [localId, controller] of lane.abortControllers.entries()) {
          lane.cancelled.add(localId);
          controller.abort();
        }
        lane.abortControllers.clear();
      }
      for (const url of allUrls) {
        revokePreviewObjectUrl(url);
      }
      allUrls.clear();
    };
  }, []);

  const runSlotUpload = useCallback((
    target: string,
    targetLane: ScopeLane,
    slot: Extract<ComposerAttachmentItem<File>, { status: 'uploading' }>,
  ): Promise<void> => {
    const controller = new AbortController();
    targetLane.abortControllers.set(slot.localId, controller);
    let inFlightReleased = false;
    const releaseInFlight = () => {
      if (inFlightReleased) return;
      inFlightReleased = true;
      editUpload(target, (current) => ({ ...current, inFlight: Math.max(0, current.inFlight - 1) }));
    };

    const previous = targetLane.tail;
    const pending = (async () => {
      try {
        const consume = await uploadRef.current(
          async () => {
            if (previous !== null) await previous;
            if (targetLane.cancelled.has(slot.localId)) {
              throw new DOMException('Attachment upload cancelled', 'AbortError');
            }
            return new Uint8Array(await slot.file.arrayBuffer());
          },
          slot.contentType,
          { filename: slot.name, signal: controller.signal },
        );
        if (previous !== null) await previous;
        if (targetLane.cancelled.has(slot.localId)) return;
        const uploaded = consume();
        const readyItem: PlannerAttachment = {
          id: uploaded.attachmentId,
          contentType: uploaded.contentType,
          size: uploaded.size,
          url: uploaded.url,
        };
        targetLane.draft = completeDraftAttachment(targetLane.draft, slot.localId, readyItem);
        update(target, (current) => (
          current.some((existing) => existing.id === readyItem.id)
            ? current
            : [...current, readyItem]
        ));
      } catch (cause) {
        if (targetLane.cancelled.has(slot.localId)) return;
        const message = cause instanceof Error ? cause.message : 'The image could not be uploaded.';
        targetLane.draft = failDraftAttachment(targetLane.draft, slot.localId, message);
        editUpload(target, (current) => ({ ...current, refusal: message }));
      } finally {
        targetLane.abortControllers.delete(slot.localId);
        releaseInFlight();
        bump();
      }
    })();

    targetLane.tail = Promise.all([previous, pending]).then(() => undefined);
    return pending;
  }, [bump, editUpload, update]);

  const attachMany = useCallback((files: readonly File[]): Promise<void> => {
    if (files.length === 0) return Promise.resolve();
    const target = cardId;
    const targetLane = getLane(target);
    const synced = reconcileReadyAttachments(targetLane.draft, storeItemsByScope.current.get(target) ?? []);
    if (synced.state !== targetLane.draft) {
      targetLane.draft = synced.state;
      for (const removed of synced.removedReady) revokeUrl(removed.previewUrl);
    }

    const candidates = files.map((file) => {
      seqRef.current += 1;
      const localId = `att-local-${seqRef.current}`;
      const previewUrl = trackUrl(file);
      return {
        localId,
        name: file.name || `image-${seqRef.current}.png`,
        contentType: file.type,
        size: file.size,
        previewUrl,
        file,
      };
    });

    const { state: nextDraft, accepted, rejectedPreviewUrls } = enqueueDraftAttachments(targetLane.draft, candidates);
    for (const url of rejectedPreviewUrls) revokeUrl(url);
    targetLane.draft = nextDraft;
    editUpload(target, (current) => ({
      inFlight: current.inFlight + accepted.length,
      refusal: nextDraft.bannerError,
    }));
    bump();

    if (accepted.length === 0) return Promise.resolve();
    const uploads = accepted.map((slot) => runSlotUpload(target, targetLane, slot));
    return Promise.all(uploads).then(() => undefined);
  }, [bump, cardId, editUpload, getLane, revokeUrl, runSlotUpload, trackUrl]);

  const attach = useCallback((file: File): Promise<void> => attachMany([file]), [attachMany]);

  const remove = useCallback((id: string) => {
    const target = cardId;
    const targetLane = getLane(target);
    const synced = reconcileReadyAttachments(targetLane.draft, storeItemsByScope.current.get(target) ?? []);
    if (synced.state !== targetLane.draft) {
      targetLane.draft = synced.state;
      for (const removed of synced.removedReady) revokeUrl(removed.previewUrl);
    }

    const { state: nextDraft, removed } = removeDraftAttachment(targetLane.draft, id);
    if (removed === null) {
      // Fallback for callers passing a server ID directly.
      update(target, (current) => current.filter((item) => item.id !== id));
      return;
    }
    targetLane.draft = nextDraft;
    if (removed.status === 'uploading') {
      targetLane.cancelled.add(removed.localId);
      targetLane.abortControllers.get(removed.localId)?.abort();
      targetLane.abortControllers.delete(removed.localId);
    }
    revokeUrl(removed.previewUrl);
    if (removed.status === 'ready') {
      update(target, (current) => current.filter((item) => item.id !== removed.attachment.id));
    }
    editUpload(target, (current) => ({ ...current, refusal: nextDraft.bannerError }));
    bump();
  }, [bump, cardId, editUpload, getLane, revokeUrl, update]);

  const retry = useCallback((id: string) => {
    const target = cardId;
    const targetLane = getLane(target);
    const { state: nextDraft, item } = retryDraftAttachment(targetLane.draft, id);
    targetLane.draft = nextDraft;
    if (item === null) {
      editUpload(target, (current) => ({ ...current, refusal: nextDraft.bannerError }));
      bump();
      return;
    }
    targetLane.cancelled.delete(item.localId);
    editUpload(target, (current) => ({
      inFlight: current.inFlight + 1,
      refusal: nextDraft.bannerError,
    }));
    bump();
    void runSlotUpload(target, targetLane, item);
  }, [bump, cardId, editUpload, getLane, runSlotUpload]);

  const clear = useCallback(() => {
    const target = cardId;
    const targetLane = getLane(target);
    for (const item of targetLane.draft.items) {
      if (item.status === 'uploading') {
        targetLane.cancelled.add(item.localId);
        targetLane.abortControllers.get(item.localId)?.abort();
      }
      revokeUrl(item.previewUrl);
    }
    targetLane.abortControllers.clear();
    targetLane.draft = EMPTY_COMPOSER_ATTACHMENTS;
    update(target, () => []);
    editUpload(target, () => NO_UPLOAD);
    bump();
  }, [bump, cardId, editUpload, getLane, revokeUrl, update]);

  const activeState = reconciled.state;
  const guards = useMemo(() => attachmentSendGuards(activeState), [activeState]);
  const draftItems = useMemo(
    () => activeState.items.map((item, index) => toDraftPreview(item, index)),
    [activeState.items],
  );

  return {
    items: guards.readyAttachments,
    ids: guards.readyIds,
    draftItems,
    attach,
    attachMany,
    remove,
    retry,
    clear,
    busy: guards.hasUploading,
    hasUploading: guards.hasUploading,
    hasFailed: guards.hasFailed,
    sendBlocked: guards.sendBlocked,
    canSendWithoutText: guards.canSendWithoutText,
    statusMessage: guards.statusMessage,
    error: activeState.bannerError ?? store.upload.refusal,
    atCapacity: guards.atCapacity,
  };
}

/** The attach control: multi-file picker button that stays enabled while uploads are in flight until capacity (8) is reached. */
export function PlannerAttachButton({ attachments, support, disabled = false }: {
  attachments: PlannerAttachments;
  support: AttachmentSupport;
  disabled?: boolean;
}) {
  const unavailable = !support.available;
  const blocked = disabled || unavailable || attachments.atCapacity;
  const tooltip = unavailable
    ? (support.reason ?? IMAGE_UNSUPPORTED_REASON)
    : attachments.atCapacity
      ? TOO_MANY_IMAGES
      : undefined;
  return (
    <ImageAttachButton
      accept={ATTACHABLE_IMAGE_TYPES.join(',')}
      disabled={blocked}
      tooltip={tooltip}
      onPickFiles={(files) => {
        void attachments.attachMany(files);
      }}
    />
  );
}

/** The strip of pending composer images with optimistic previews, Lightbox zoom, and per-item Retry/Remove. */
export function PlannerAttachmentDrawer({ attachments, disabled = false }: {
  attachments: PlannerAttachments;
  disabled?: boolean;
}) {
  return (
    <DraftAttachmentStrip
      items={attachments.draftItems}
      onRemove={attachments.remove}
      onRetry={attachments.retry}
      disabled={disabled}
      error={attachments.error}
      statusMessage={attachments.statusMessage}
    />
  );
}
