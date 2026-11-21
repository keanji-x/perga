import type { CheckpointStatus } from '../../../../../core/domain/report-checkpoints.ts';
import styles from './checkpoints.module.css';

export function markerToneClass(status: CheckpointStatus): string {
  switch (status) {
    case 'done': return styles.markerDone;
    case 'running': return styles.markerRunning;
    case 'blocked': return styles.markerBlocked;
    case 'pending': return styles.markerPending;
  }
}
