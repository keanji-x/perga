import type { ReactNode } from 'react';
import { isClosed, trackDisplayTitle, type Track } from '../../../../../core/domain/track.ts';
import styles from './title.module.css';

/** Track title text only. Hosts own typography, truncation, navigation and editing. */
export function TrackTitle({ track, children }: Readonly<{
  track: Pick<Track, 'title' | 'closedAt'>;
  children?: ReactNode;
}>) {
  return <span className={isClosed(track) ? styles.closed : undefined}>
    {children ?? trackDisplayTitle(track.title)}
  </span>;
}
