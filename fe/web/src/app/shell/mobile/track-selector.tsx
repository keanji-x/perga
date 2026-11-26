import { TrackTitle } from '../../../features/track/title/public.tsx';
import { trackDisplayTitle, type Track } from '../../../../../core/domain/track.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import type { EditableTitleProps } from '../../../ui/editable-title/public.tsx';
import styles from './area-selector.module.css';

/** The title opens the existing workspace page; title editing stays with its owner. */
export function TrackSelector({ track, areaName, onOpenTracks, controls }: Readonly<{
  track: Track;
  areaName?: string;
  onOpenTracks: () => void;
  controls: Parameters<NonNullable<EditableTitleProps['readView']>>[0];
}>) {
  return <div className={styles.trackIdentity}><h1 className={styles.trackHeading}>
    <button type="button" ref={controls.titleRef} className={styles.trackButton} data-nc-page-title=""
      aria-label={`Switch track, ${areaName ?? trackDisplayTitle(track.title)}`} onClick={onOpenTracks}
      onKeyDown={(event) => { if (event.key === 'F2') { event.preventDefault(); controls.beginEditing(); } }}>
      <span className={styles.trackLabel}>{areaName ?? <TrackTitle track={track} />}</span><Icon name="chevron-down" size="sm" />
    </button>
  </h1></div>;
}
