import { useMemo } from 'react';
import { useTranslator } from '@astryxdesign/core/i18n';
import { useLightbox } from '@astryxdesign/core/Lightbox';
import type { PlannerAttachment } from '../../../../../core/domain/presentation-types.ts';
import { useState } from '../../../ui/state/public.ts';
import styles from './thread.module.css';

export function MessageImages({ attachments, edited = false }: {
  attachments: readonly PlannerAttachment[];
  edited?: boolean;
}) {
  const t = useTranslator();
  const [mounted, setMounted] = useState(false);
  const media = useMemo(() => attachments.map(image => ({ src: image.url, alt: '' })), [attachments]);
  const lightbox = useLightbox({ media, hasZoom: true });

  return <>
    <ul className={styles.attachments} data-nc-turn-attachments="" {...(edited ? { 'data-nc-editing': '' } : {})}>
      {attachments.map((image, index) => (
        <li key={`${index}:${image.id}`} className={styles.attachment}>
          <button
            type="button"
            className={styles.attachmentButton}
            aria-haspopup="dialog"
            aria-label={t('@astryx.lightbox.imagePosition', { index: index + 1, total: media.length })}
            onClick={() => {
              setMounted(true);
              lightbox.open(index);
            }}
          >
            <img src={image.url} alt="" />
          </button>
        </li>
      ))}
    </ul>
    {mounted && lightbox.element}
  </>;
}
