import { useCallback, useRef } from 'react';
import { IconButton } from '@astryxdesign/core/IconButton';

import { Icon } from '../../ui/icon/public.tsx';

import { ATTACHABLE_IMAGE_TYPES, MAX_ATTACHMENT_BYTES, MAX_ATTACHMENTS_PER_MESSAGE, MAX_TOTAL_ATTACHMENT_BYTES } from '../../../../core/api/perga-v1.ts';
import { TOO_MANY_IMAGES, TOO_MANY_IMAGE_BYTES } from '../../../../core/domain/conversation-commands.ts';
import type { ConversationAttachment } from '../../../../core/domain/conversation.ts';
import styles from './attachments.module.css';

export type AttachmentDraft = Readonly<{
  attachments?: readonly ConversationAttachment[];
  attachmentError?: string | null;
}>;

export type PlannerAttachments = Readonly<{
  items: readonly ConversationAttachment[];
  attach: (files: readonly File[]) => void;
  remove: (id: string) => void;
  error: string | null;
  atCapacity: boolean;
}>;

function imageRefusal(file: File): string | null {
  if (!(ATTACHABLE_IMAGE_TYPES as readonly string[]).includes(file.type)) return 'Choose a PNG, JPEG, GIF or WebP image.';
  if (file.size === 0) return 'Choose an image that is not empty.';
  if (file.size > MAX_ATTACHMENT_BYTES) return 'Each image must be at most 10 MiB.';
  return null;
}

/** Both composers use the same validation. The registry keeps preview URLs while a draft or send owns them. */
export function usePlannerAttachments(
  draft: AttachmentDraft,
  update: (change: (current: AttachmentDraft) => Required<AttachmentDraft>) => void,
): PlannerAttachments {
  const items = draft.attachments ?? NO_IMAGES;
  const attach = useCallback((files: readonly File[]) => {
    update(current => {
      const attachments = [...current.attachments ?? []];
      let total = attachments.reduce((bytes, image) => bytes + image.size, 0);
      let attachmentError: string | null = null;
      for (const file of files) {
        const refusal = imageRefusal(file);
        if (refusal !== null) { attachmentError ??= refusal; continue; }
        if (attachments.length >= MAX_ATTACHMENTS_PER_MESSAGE) { attachmentError ??= TOO_MANY_IMAGES; break; }
        if (total + file.size > MAX_TOTAL_ATTACHMENT_BYTES) { attachmentError ??= TOO_MANY_IMAGE_BYTES; continue; }
        const url = URL.createObjectURL(file);
        total += file.size;
        attachments.push({ id: `local-${url.slice('blob:'.length)}`, contentType: file.type, size: file.size, url, file });
      }
      return { attachments, attachmentError };
    });
  }, [update]);
  const remove = useCallback((id: string) => {
    update(current => ({ attachments: (current.attachments ?? NO_IMAGES).filter(image => image.id !== id), attachmentError: null }));
  }, [update]);
  return { items, attach, remove, error: draft.attachmentError ?? null, atCapacity: items.length >= MAX_ATTACHMENTS_PER_MESSAGE };
}

const NO_IMAGES: readonly ConversationAttachment[] = Object.freeze([]);

/** The attach control: an `IconButton` with a hidden `<input type="file">` behind it, opened by `click()`. The unavailable reason rides on `tooltip` rather than `title`: Astryx switches a tooltipped disabled button to `aria-disabled` so it stays focusable. */
export function PlannerAttachButton({ attachments, available, disabled = false }: {
  attachments: PlannerAttachments;
  available: boolean;
  disabled?: boolean;
}) {
  const picker = useRef<HTMLInputElement | null>(null);
  const unavailable = !available;
  const blocked = disabled || unavailable || attachments.atCapacity;
  const tooltip = unavailable
    ? 'Image attachments are unavailable for this conversation.'
    : attachments.atCapacity
      ? TOO_MANY_IMAGES
      : undefined;
  return (
    <span className={styles.attach} data-nc-attach="">
      <IconButton
        label="Attach an image"
        icon={<Icon name="paperclip" size="sm" />}
        variant="ghost"
        size="sm"
        isDisabled={blocked}
        tooltip={tooltip}
        onClick={() => { picker.current?.click(); }}
      />
      <input
        ref={picker}
        className={styles.attachInput}
        type="file"
        multiple
        accept={ATTACHABLE_IMAGE_TYPES.join(',')}
        /* The button above is the control; a second announced file input would be a second thing to tab to that does nothing. */
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const files = Array.from(event.target.files ?? []);
          /* Cleared unconditionally so picking the same file twice in a row still fires a change event. */
          event.target.value = '';
          if (!blocked && files.length > 0) attachments.attach(files);
        }}
      />
    </span>
  );
}
