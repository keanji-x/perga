import { MESSAGES } from '../../../../core/domain/messages.ts';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { Icon } from '../../ui/icon/public.tsx';
import styles from './today.module.css';

export function TodayReportEmpty() {
  return <section className={styles.empty} aria-label="Empty daily log">
    <EmptyState icon={<Icon name="tasks" />} title="No entries for today" description={MESSAGES.today.mobileSubtitle} />
  </section>;
}
