import { EmptyState } from '../../ui/empty-state/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import styles from './today.module.css';

export function TodayReportEmpty() {
  return <section className={styles.empty} aria-label="Empty daily log">
    <EmptyState icon={<Icon name="tasks" />} title="No entries for today" description="Keep your work clear, calm, and well-ordered." />
  </section>;
}
