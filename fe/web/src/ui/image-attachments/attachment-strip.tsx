import { useMemo } from 'react';
import { Banner } from '@astryxdesign/core/Banner';
import { ChatComposerDrawer } from '@astryxdesign/core/Chat';
import { HStack } from '@astryxdesign/core/HStack';
import { useTranslator } from '@astryxdesign/core/i18n';
import { useLightbox } from '@astryxdesign/core/Lightbox';
import { Thumbnail } from '@astryxdesign/core/Thumbnail';
import { VStack } from '@astryxdesign/core/VStack';
import { useState } from '../state/public.ts';
import styles from './image-attachments.module.css';

export type DraftAttachmentPreview = Readonly<{
  id: string;
  src: string;
  alt?: string;
  label?: string;
  status: 'uploading' | 'ready' | 'failed';
  error?: string | null;
}>;

export type DraftAttachmentStripProps = Readonly<{
  items: readonly DraftAttachmentPreview[];
  onRemove: (id: string) => void;
  onRetry?: (id: string) => void;
  disabled?: boolean;
  error?: string | null;
  statusMessage?: string | null;
  label?: string;
}>;

/**
 * Composer attachment drawer: optimistic thumbnails, upload spinner overlays,
 * per-item retry/remove actions, and Lightbox click-to-zoom preview.
 */
export function DraftAttachmentStrip({
  items,
  onRemove,
  onRetry,
  disabled = false,
  error = null,
  statusMessage = null,
  label = 'Images',
}: DraftAttachmentStripProps) {
  const [lightboxMounted, setLightboxMounted] = useState(false);

  const { media, lightboxIndexById } = useMemo(() => {
    const nextMedia: { src: string; alt: string }[] = [];
    const indexById = new Map<string, number>();
    items.forEach((item, index) => {
      if (item.status === 'uploading' || item.src === '') return;
      indexById.set(item.id, nextMedia.length);
      nextMedia.push({
        src: item.src,
        alt: item.alt !== undefined && item.alt !== '' ? item.alt : (item.label ?? `Image ${index + 1}`),
      });
    });
    return { media: nextMedia, lightboxIndexById: indexById };
  }, [items]);

  const lightbox = useLightbox({ media, hasZoom: true });

  if (items.length === 0 && error === null && statusMessage === null) return null;

  return (
    <ChatComposerDrawer count={items.length} label={label}>
      <VStack gap={2}>
        {items.length > 0 && (
          <HStack gap={2} wrap="wrap" data-nc-attachments="">
            {items.map((item, index) => {
              const itemLabel = item.label ?? `Image ${index + 1}`;
              const lightboxIndex = lightboxIndexById.get(item.id);
              const isFailed = item.status === 'failed';
              return (
                <div
                  key={item.id}
                  className={`${styles.draftItem} ${isFailed ? styles.draftItemFailed : ''}`}
                  data-nc-attachment-item={item.status}
                >
                  <Thumbnail
                    src={item.src !== '' ? item.src : undefined}
                    alt={item.alt ?? ''}
                    label={itemLabel}
                    isLoading={item.status === 'uploading'}
                    onClick={lightboxIndex === undefined ? undefined : () => {
                      setLightboxMounted(true);
                      lightbox.open(lightboxIndex);
                    }}
                    onRemove={disabled ? undefined : () => {
                      onRemove(item.id);
                    }}
                  />
                  {!disabled && isFailed && onRetry !== undefined && (
                    <button
                      type="button"
                      className={styles.retryButton}
                      data-nc-attachment-retry=""
                      aria-label={`Retry ${itemLabel}`}
                      onClick={() => {
                        onRetry(item.id);
                      }}
                    >
                      Retry
                    </button>
                  )}
                </div>
              );
            })}
          </HStack>
        )}
        {statusMessage !== null && (
          <p className={styles.statusNote} role="status" data-nc-attachment-status="">
            {statusMessage}
          </p>
        )}
        {error !== null && (
          <Banner
            status="error"
            title="That image was not attached"
            description={error}
          />
        )}
        {lightboxMounted && lightbox.element}
      </VStack>
    </ChatComposerDrawer>
  );
}

export type GalleryImageItem = Readonly<{
  id: string;
  src: string;
  alt?: string;
}>;

export type AttachmentGalleryProps = Readonly<{
  images: readonly GalleryImageItem[];
  align?: 'end' | 'start';
  editing?: boolean;
  compact?: boolean;
}>;

/** Read-only image thumbnail gallery with Lightbox click-to-zoom shared by messages, queues and drafts. */
export function AttachmentGallery({
  images,
  align = 'end',
  editing = false,
  compact = false,
}: AttachmentGalleryProps) {
  const t = useTranslator();
  const [lightboxMounted, setLightboxMounted] = useState(false);
  const media = useMemo(
    () => images.map((image) => ({ src: image.src, alt: image.alt ?? '' })),
    [images],
  );
  const lightbox = useLightbox({ media, hasZoom: true });

  if (images.length === 0) return null;

  return (
    <>
      <ul
        className={[
          styles.gallery,
          align === 'start' ? styles.galleryStart : '',
          compact ? styles.galleryCompact : '',
        ].filter(Boolean).join(' ')}
        data-nc-turn-attachments=""
        {...(editing ? { 'data-nc-editing': '' } : {})}
      >
        {images.map((image, index) => (
          <li key={image.id} className={styles.galleryItem}>
            <button
              type="button"
              className={styles.galleryButton}
              aria-haspopup="dialog"
              aria-label={t('@astryx.lightbox.imagePosition', { index: index + 1, total: images.length })}
              onClick={() => {
                setLightboxMounted(true);
                lightbox.open(index);
              }}
            >
              <img src={image.src} alt={image.alt ?? ''} />
            </button>
          </li>
        ))}
      </ul>
      {lightboxMounted && lightbox.element}
    </>
  );
}
