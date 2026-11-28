import { Button } from '@astryxdesign/core/Button';

import { ActivityIndicator } from '../../../ui/activity-indicator/public.tsx';
import styles from './empty.module.css';

type ReportEmptyProps = Readonly<{
  state: 'blank' | 'working' | 'conversing' | 'workspace-waiting';
  compactViewport: boolean;
  onStartConversation?: () => void;
}>;

export function ReportEmpty({ state, compactViewport, onStartConversation }: ReportEmptyProps) {
  if (state === 'workspace-waiting') return null;
  const title = state === 'working' ? 'Drafting the initial report…'
    : state === 'conversing' ? 'Nothing published to the report yet' : 'This page becomes your living report';
  const description = state === 'working' ? 'Lead is reviewing your request and will organize the title, plan, and findings here.'
    : state === 'conversing' ? 'As the direction settles in conversation, Lead will compile decisions and progress on this page.'
    : compactViewport ? 'Start typing below — Lead will organize your plan, findings, and progress into this report.'
    : 'Discuss goals and decisions with Lead; plans, findings, and progress will settle here.';

  return (
    <div className={styles.sheet} data-nc-report-empty="">
      <h2 className={styles.title}>
        {state === 'working' && <ActivityIndicator state="working" />}
        <span>{title}</span>
      </h2>
      <p className={styles.description}>{description}</p>
      {state === 'blank' && !compactViewport && onStartConversation !== undefined && (
        <Button className={styles.start} variant="secondary" size="sm" label="Start with Lead" onClick={onStartConversation} />
      )}
    </div>
  );
}
