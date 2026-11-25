import { useCallback, useMemo, useRef, type Ref } from 'react';
import { Banner } from '@astryxdesign/core/Banner';
import { ChatComposerInput, type ChatComposerInputHandle, type ChatComposerTrigger } from '@astryxdesign/core/Chat';
import { HStack } from '@astryxdesign/core/HStack';
import { Thumbnail } from '@astryxdesign/core/Thumbnail';

import { withEmptyTriggerMarker } from '../empty-state/astryx-trigger.tsx';
import { usePortalLightbox } from '../lightbox/public.tsx';
import styles from './composer-attachments.module.css';

export type ComposerAttachmentItem = Readonly<{
  id: string;
  url: string;
  label?: string;
}>;

/**
 * Floating thumbnail strip rendered directly above the composer without an outer
 * collapsible card, with a portal Lightbox preview (with zoom) on click.
 */
export function ComposerAttachmentStrip({
  items,
  error = null,
  onRemove,
  disabled = false,
}: Readonly<{
  items: readonly ComposerAttachmentItem[];
  error?: string | null;
  onRemove?: (id: string) => void;
  disabled?: boolean;
}>) {
  const media = useMemo(
    () => items.map((item, index) => ({ src: item.url, alt: item.label ?? `Image ${index + 1}` })),
    [items],
  );
  const lightbox = usePortalLightbox(media);

  if (items.length === 0 && (error === null || error === undefined)) return null;

  return (
    <div className={styles.strip} data-nc-attachment-strip="">
      {items.length > 0 && (
        <HStack gap={2} wrap="wrap" data-nc-attachments="">
          {items.map((item, index) => (
            <Thumbnail
              key={item.id}
              className={styles.thumb}
              src={item.url}
              label={item.label ?? `Image ${index + 1}`}
              onClick={() => lightbox.open(index)}
              onRemove={disabled || onRemove === undefined ? undefined : () => onRemove(item.id)}
            />
          ))}
        </HStack>
      )}
      {error !== null && error !== undefined && (
        <Banner
          status="error"
          role="alert"
          title="That image was not attached"
          description={error}
        />
      )}
      {lightbox.portal}
    </div>
  );
}

/**
 * Shared composer input field combining unified clipboard image/mixed-content
 * paste and file-drop handling.
 */
export function ComposerInputField({
  wrapperRef,
  className,
  inputClassName,
  handleRef,
  label,
  placeholder,
  maxRows,
  triggers,
  debounceMs,
  disabled = false,
  onFiles,
}: Readonly<{
  wrapperRef?: Ref<HTMLDivElement>;
  className?: string;
  inputClassName?: string;
  handleRef?: Ref<ChatComposerInputHandle>;
  label: string;
  placeholder: string;
  maxRows?: number;
  triggers?: ChatComposerTrigger[];
  debounceMs?: number;
  disabled?: boolean;
  onFiles?: (files: File[]) => void;
}>) {
  const innerHandle = useRef<ChatComposerInputHandle | null>(null);
  const assignHandle = useCallback((instance: ChatComposerInputHandle | null) => {
    innerHandle.current = instance;
    if (typeof handleRef === 'function') handleRef(instance);
    else if (handleRef != null && 'current' in handleRef) {
      (handleRef as { current: ChatComposerInputHandle | null }).current = instance;
    }
  }, [handleRef]);

  const wrapperClassName = className ? `${styles.field} ${className}` : styles.field;
  const markedTriggers = useMemo(
    () => triggers === undefined || triggers.length === 0 ? undefined : triggers.map(withEmptyTriggerMarker),
    [triggers],
  );

  return (
    <div
      ref={wrapperRef}
      className={wrapperClassName}
      onPasteCapture={disabled || onFiles === undefined ? undefined : (event) => {
        if (!(event.target instanceof Element) || event.target.closest('[contenteditable]') === null) return;
        const files = Array.from(event.clipboardData.files);
        const text = event.clipboardData.getData('text/plain');
        // Astryx consumes the file list before invoking its text paste hook.
        if (files.length === 0 || text === '') return;
        event.preventDefault();
        event.stopPropagation();
        onFiles(files);
        innerHandle.current?.insertText(text);
      }}
    >
      <ChatComposerInput
        className={inputClassName}
        handleRef={assignHandle}
        maxRows={maxRows}
        isDisabled={disabled}
        onFiles={disabled ? undefined : onFiles}
        onPaste={disabled || onFiles === undefined ? undefined : (event, text) => {
          if (event.clipboardData.files.length > 0) return;
          const files = Array.from(event.clipboardData.items ?? []).flatMap((item) => {
            if (item.kind !== 'file' || !item.type.startsWith('image/')) return [];
            const file = item.getAsFile();
            return file === null ? [] : [file];
          });
          if (files.length === 0) return;
          onFiles(files);
          return text === '';
        }}
        label={label}
        placeholder={placeholder}
        {...(markedTriggers === undefined ? {} : { triggers: markedTriggers })}
        {...(debounceMs === undefined ? {} : { debounceMs })}
      />
    </div>
  );
}
