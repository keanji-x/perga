import { useLayoutEffect } from 'react';
import { TrackTitle } from '../../../features/track/title/public.tsx';
import { trackDisplayTitle, type Track } from '../../../../../core/domain/track.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import type { EditableTitleProps } from '../../../ui/editable-title/public.tsx';
import styles from './area-selector.module.css';

/** The title opens the existing workspace page; title editing stays with its owner. */
export function TrackSelector({ track, onOpenTracks, onVisible, controls }: Readonly<{
  track: Track;
  onOpenTracks: (trigger: HTMLButtonElement) => void;
  onVisible?: () => void;
  controls: Parameters<NonNullable<EditableTitleProps['readView']>>[0];
}>) {
  const visible = controls.isPageTitle !== false;
  useLayoutEffect(() => { if (visible) onVisible?.(); }, [onVisible, visible]);

  return <div className={styles.trackIdentity}>
    <button type="button" ref={controls.titleRef} className={styles.trackButton} data-nc-page-title={visible ? '' : undefined}
      data-nc-return-focus="report-title"
      aria-label={`Switch track, ${trackDisplayTitle(track.title)}`} onClick={(event) => {
        event.currentTarget.focus({ preventScroll: true });
        onOpenTracks(event.currentTarget);
      }}
      onKeyDown={(event) => { if (event.key === 'F2') { event.preventDefault(); controls.beginEditing(); } }}>
      <span className={styles.trackLabel}><TrackTitle track={track} /></span><Icon name="chevron-down" size="sm" />
    </button>
  </div>;
}
