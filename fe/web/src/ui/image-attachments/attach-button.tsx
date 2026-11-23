import { useRef } from 'react';
import { IconButton } from '@astryxdesign/core/IconButton';
import { Icon } from '../icon/public.tsx';
import { ATTACHABLE_IMAGE_MIME_TYPES } from './clipboard-files.ts';
import styles from './image-attachments.module.css';

export type ImageAttachButtonProps = Readonly<{
  onPickFiles: (files: File[]) => void;
  disabled?: boolean;
  loading?: boolean;
  tooltip?: string;
  label?: string;
  accept?: string;
}>;

/**
 * Multi-file image picker button (`<input type="file" multiple>`).
 * `loading` displays an active indicator without blocking additional picks while uploads are in flight.
 */
export function ImageAttachButton({
  onPickFiles,
  disabled = false,
  loading = false,
  tooltip,
  label = 'Attach an image',
  accept = ATTACHABLE_IMAGE_MIME_TYPES.join(','),
}: ImageAttachButtonProps) {
  const pickerRef = useRef<HTMLInputElement | null>(null);
  return (
    <span className={styles.attach} data-nc-attach="">
      <IconButton
        label={label}
        icon={<Icon name="paperclip" size="sm" />}
        variant="ghost"
        size="sm"
        isDisabled={disabled}
        isLoading={loading}
        tooltip={tooltip}
        onClick={() => {
          pickerRef.current?.click();
        }}
      />
      <input
        ref={pickerRef}
        className={styles.attachInput}
        type="file"
        accept={accept}
        multiple
        /* The IconButton above is the accessible control; keep the hidden input out of tab order. */
        aria-hidden="true"
        tabIndex={-1}
        onChange={(event) => {
          const picked = Array.from(event.target.files ?? []);
          /* Always reset so selecting the same file twice in a row still fires change. */
          event.target.value = '';
          if (picked.length > 0) onPickFiles(picked);
        }}
      />
    </span>
  );
}
