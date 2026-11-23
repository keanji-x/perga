import styles from './image-attachments.module.css';

export {
  ATTACHABLE_IMAGE_MIME_TYPES,
  extractClipboardPayload,
  handleComposerPasteCapture,
  useDropzoneHighlight,
  type ComposerPasteCaptureOptions,
  type DropzoneHighlightOptions,
  type ExtractedClipboardPayload,
} from './clipboard-files.ts';

export {
  ImageAttachButton,
  type ImageAttachButtonProps,
} from './attach-button.tsx';

export {
  AttachmentGallery,
  DraftAttachmentStrip,
  type AttachmentGalleryProps,
  type DraftAttachmentPreview,
  type DraftAttachmentStripProps,
  type GalleryImageItem,
} from './attachment-strip.tsx';

export const dropzoneActiveClassName = styles.dropzoneActive;
