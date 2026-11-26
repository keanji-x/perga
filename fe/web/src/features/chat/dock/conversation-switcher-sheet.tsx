import { conversationName, type Conversation } from '../../../../../core/domain/conversation.ts';
import { formatCompactAge } from '../../../../../core/domain/relative-time.ts';
import { Dialog } from '../../../ui/dialog/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './conversation-switcher-sheet.module.css';

export type ConversationSwitcherSheetProps = Readonly<{
  open: boolean;
  onClose: () => void;
  conversations: readonly Conversation[];
  selectedId: string | null;
  onSelect: (conversation: Conversation) => void;
  onNew?: () => void;
  trackTitle?: string;
}>;

/** Bottom-sheet conversation switcher built on the shared ui/dialog modal contract. */
export function ConversationSwitcherSheet({
  open,
  onClose,
  conversations,
  selectedId,
  onSelect,
  onNew,
  trackTitle,
}: ConversationSwitcherSheetProps) {
  const activeRows = conversations.filter((row) => row.archivedAt == null);
  const sorted = [...activeRows].toSorted((left, right) => {
    if (left.isLead === true && right.isLead !== true) return -1;
    if (right.isLead === true && left.isLead !== true) return 1;
    return right.updatedAt - left.updatedAt;
  });
  const now = Date.now();

  return (
    <Dialog
      purpose="info"
      origin={{ kind: 'surface-center' }}
      placement="bottom"
      flush
      hideTitleRow
      open={open}
      title="Switch conversation"
      onClose={onClose}
    >
      <div className={styles.sheet}>
        <div className={styles.handleBar} aria-hidden="true">
          <span className={styles.handlePill} />
        </div>
        <header className={styles.header}>
          <div className={styles.headerText}>
            <h2 className={styles.title}>Switch conversation</h2>
            {trackTitle !== undefined && <p className={styles.subtitle}>{trackTitle}</p>}
          </div>
          <button type="button" className={styles.closeButton} aria-label="Close switcher" onClick={onClose}>
            <Icon name="close" size="sm" />
          </button>
        </header>

        <div className={styles.body}>
          <section className={styles.section} aria-label="This track">
            <h3 className={styles.sectionTitle}>This track</h3>
            <ul className={styles.groupCard}>
              {sorted.map((row) => {
                const selected = row.id === selectedId;
                const role = row.isLead === true ? 'Lead' : 'Collaborator';
                const age = formatCompactAge(row.updatedAt, now);
                return (
                  <li key={row.id} className={`${styles.groupItem} ${selected ? styles.groupItemSelected : ''}`}>
                    <button
                      type="button"
                      className={styles.rowButton}
                      aria-current={selected ? 'true' : undefined}
                      onClick={() => {
                        onSelect(row);
                        onClose();
                      }}
                    >
                      <span className={styles.rowIcon} aria-hidden="true">
                        <Icon name={row.isLead === true ? 'agent' : 'chat'} size="sm" />
                      </span>
                      <span className={styles.rowBody}>
                        <span className={styles.rowTitle}>{row.isLead === true ? 'Lead' : conversationName(row)}</span>
                        <span className={styles.rowMeta}>
                          {role}{age !== '' ? ` · ${age}` : ''}
                        </span>
                      </span>
                      {selected && (
                        <span className={styles.checkIcon} aria-hidden="true">
                          <Icon name="check" size="sm" />
                        </span>
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          {onNew !== undefined && (
            <button
              type="button"
              className={styles.newButton}
              onClick={() => {
                onClose();
                onNew();
              }}
            >
              <Icon name="plus" size="sm" />
              <span>New conversation</span>
            </button>
          )}
        </div>
      </div>
    </Dialog>
  );
}
