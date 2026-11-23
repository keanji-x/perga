import { useEffect, type ClipboardEvent, type DragEvent } from 'react';
import { useState } from '../state/public.ts';

/** Supported upload MIME types for file pickers and clipboard/drop pre-checks. */
export const ATTACHABLE_IMAGE_MIME_TYPES = Object.freeze(
  ['image/png', 'image/jpeg', 'image/gif', 'image/webp'] as const,
);

export type ExtractedClipboardPayload = Readonly<{
  images: readonly File[];
  otherFiles: readonly File[];
  plainText: string;
}>;

function fileFingerprint(file: File): string {
  return `${file.name}\0${file.size}\0${file.type}\0${file.lastModified}`;
}

/**
 * Extract files and plain text from a clipboard DataTransfer, merging `items` and `files`
 * without double-counting screenshots that Chromium exposes in both lists.
 */
export function extractClipboardPayload(data: DataTransfer | null | undefined): ExtractedClipboardPayload {
  if (data == null) {
    return { images: [], otherFiles: [], plainText: '' };
  }
  const files: File[] = [];
  const itemCounts = new Map<string, number>();

  if (data.items != null) {
    for (const item of Array.from(data.items)) {
      if (item.kind !== 'file') continue;
      const file = item.getAsFile();
      if (file === null) continue;
      files.push(file);
      const key = fileFingerprint(file);
      itemCounts.set(key, (itemCounts.get(key) ?? 0) + 1);
    }
  }

  if (data.files != null) {
    const fileSeen = new Map<string, number>();
    for (const file of Array.from(data.files)) {
      const key = fileFingerprint(file);
      const seen = (fileSeen.get(key) ?? 0) + 1;
      fileSeen.set(key, seen);
      if (seen <= (itemCounts.get(key) ?? 0)) continue;
      files.push(file);
    }
  }

  const images: File[] = [];
  const otherFiles: File[] = [];
  for (const file of files) {
    if (file.type.startsWith('image/')) images.push(file);
    else otherFiles.push(file);
  }

  const plainText = typeof data.getData === 'function' ? data.getData('text/plain') : '';
  return { images, otherFiles, plainText };
}

export type ComposerPasteCaptureOptions = Readonly<{
  disabled?: boolean;
  inputDisabled?: boolean;
  onImages?: (files: File[]) => void;
  onUnsupportedFiles?: (files: File[]) => void;
  insertText?: (text: string) => void;
}>;

/**
 * Capture-phase paste handler for rich contentEditable composers:
 * - Pure image(s): prevents default rich HTML paste and forwards image files.
 * - Mixed text + image(s): forwards images AND inserts plain text via `insertText`.
 * - Non-image file(s) without text: prevents default and reports them so the caller shows a refusal banner.
 * - When attachments are disabled (`disabled: true`) while the editor is still editable (`inputDisabled !== true`),
 *   prevents Astryx from swallowing accompanying plain text on mixed pastes.
 * - Pure text: left untouched for native / Astryx paste handling.
 */
export function handleComposerPasteCapture(
  event: ClipboardEvent<HTMLElement>,
  options: ComposerPasteCaptureOptions,
): boolean {
  if (options.inputDisabled === true || event.clipboardData == null) return false;
  const { images, otherFiles, plainText } = extractClipboardPayload(event.clipboardData);

  if (options.disabled === true) {
    if (images.length > 0 || otherFiles.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      if (plainText !== '') options.insertText?.(plainText);
      return true;
    }
    return false;
  }

  if (images.length > 0) {
    event.preventDefault();
    event.stopPropagation();
    options.onImages?.([...images]);
    if (plainText !== '') options.insertText?.(plainText);
    return true;
  }

  if (otherFiles.length > 0 && plainText === '') {
    event.preventDefault();
    event.stopPropagation();
    if (options.onUnsupportedFiles !== undefined) options.onUnsupportedFiles([...otherFiles]);
    else options.onImages?.([...otherFiles]);
    return true;
  }

  return false;
}

function hasTransferFiles(dataTransfer: DataTransfer | null | undefined): boolean {
  if (dataTransfer == null) return false;
  if (dataTransfer.files != null && dataTransfer.files.length > 0) return true;
  return Array.from(dataTransfer.types ?? []).includes('Files');
}

export type DropzoneHighlightOptions = Readonly<{
  enabled: boolean;
  onDropFiles: (files: File[]) => void;
}>;

/** Drag-enter/leave state and drop handler for composer shells (containment-checked so child crossings never stick). */
export function useDropzoneHighlight({ enabled, onDropFiles }: DropzoneHighlightOptions) {
  const [isDragOver, setIsDragOver] = useState(false);

  useEffect(() => {
    if (!enabled) {
      setIsDragOver(false);
    }
  }, [enabled]);

  return {
    isDragOver: enabled && isDragOver,
    dropzoneProps: {
      onDragEnter: (event: DragEvent<HTMLElement>) => {
        if (!enabled || !hasTransferFiles(event.dataTransfer)) return;
        event.preventDefault();
        setIsDragOver(true);
      },
      onDragOver: (event: DragEvent<HTMLElement>) => {
        if (!enabled || !hasTransferFiles(event.dataTransfer)) return;
        event.preventDefault();
        if (event.dataTransfer != null) event.dataTransfer.dropEffect = 'copy';
      },
      onDragLeave: (event: DragEvent<HTMLElement>) => {
        if (!enabled) return;
        if (event.currentTarget.contains(event.relatedTarget as Node | null)) return;
        setIsDragOver(false);
      },
      onDrop: (event: DragEvent<HTMLElement>) => {
        setIsDragOver(false);
        if (!enabled) return;
        const files = Array.from(event.dataTransfer?.files ?? []);
        if (files.length === 0) return;
        event.preventDefault();
        event.stopPropagation();
        onDropFiles(files);
      },
    },
  };
}
