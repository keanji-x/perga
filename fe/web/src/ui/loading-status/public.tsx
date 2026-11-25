import type { ReactNode } from 'react';
import styles from './loading-status.module.css';

/** Muted live status line for route and panel loading placeholders. */
export function LoadingStatus({ children, padded = false }: Readonly<{
  children: ReactNode;
  padded?: boolean;
}>) {
  return <p className={padded ? `${styles.status} ${styles.padded}` : styles.status} role="status">{children}</p>;
}
