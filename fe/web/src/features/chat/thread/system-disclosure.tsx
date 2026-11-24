import { ChatToolCalls } from '@astryxdesign/core/Chat';
import { Icon } from '../../../ui/icon/public.tsx';
import type { ConversationSystemEntry } from '../../../../../core/domain/conversation.ts';
import activityStyles from './activity-groups.module.css';
import styles from './thread.module.css';

/** Render one system observation on its own tool-call row, without changing its domain identity. */
export function SystemDisclosure({ turn }: Readonly<{ turn: ConversationSystemEntry }>) {
  if (turn.quiet === true) return <p className={styles.gap} data-nc-turn="system" title={turn.text}>{turn.label}</p>;
  return <div title={turn.text}><ChatToolCalls className={`${activityStyles.group} ${styles.system}`} data-nc-turn="system"
    calls={[{
      key: turn.id,
      name: turn.label,
      status: 'complete',
      stats: <span data-nc-system-icon="" aria-hidden="true"><Icon name="notification" size="sm" /></span>,
      resultDetail: <p className={styles.systemDetail} data-nc-detail={turn.id}>{turn.text}</p>,
    }]} /></div>;
}
