import { useCallback, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { useLightbox, type LightboxMedia } from '@astryxdesign/core/Lightbox';
import { useState } from '../state/public.ts';

/**
 * Shared portal Lightbox controller (`hasZoom: true`). Mounts its `<dialog>` into
 * `document.body` on first open and closes automatically if `media` becomes empty.
 */
export function usePortalLightbox(media: LightboxMedia[]) {
  const [mounted, setMounted] = useState(false);
  const lightbox = useLightbox({ media, hasZoom: true });

  useEffect(() => {
    if (media.length === 0 && lightbox.isOpen) lightbox.close();
  }, [lightbox, media.length]);

  const open = useCallback((index?: number) => {
    setMounted(true);
    lightbox.open(index);
  }, [lightbox]);

  const portal = mounted && media.length > 0
    ? createPortal(lightbox.element, document.body)
    : null;

  return { open, close: lightbox.close, isOpen: lightbox.isOpen, portal };
}
