import { LayerDepthProvider, type UseLayerDismissalReturn } from '@astryxdesign/core/Layer';
import { useCallback, useEffect, useRef } from 'react';
import { conversationName, type Conversation } from '../../../../../core/domain/conversation.ts';
import { LayerDismissal } from '../../../ui/dialog/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './conversation-switcher-sheet.module.css';

function compactAge(atMs: number, nowMs: number): string {
  if (!Number.isFinite(atMs) || atMs <= 0) return '';
  const diff = Math.max(0, nowMs - atMs);
  if (diff < 60_000) return 'Just now';
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h`;
  return `${Math.floor(diff / 86_400_000)}d`;
}

export type ConversationSwitcherSheetProps = Readonly<{
  open: boolean;
  onClose: () => void;
  conversations: readonly Conversation[];
  selectedId: string | null;
  onSelect: (conversation: Conversation) => void;
  onNew?: () => void;
  trackTitle?: string;
}>;

/** Native bottom sheet for switching between Lead and Collaborator/Worker conversations in the current Track. */
export function ConversationSwitcherSheet({
  open,
  onClose,
  conversations,
  selectedId,
  onSelect,
  onNew,
  trackTitle,
}: ConversationSwitcherSheetProps) {
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const dismissal = useRef<UseLayerDismissalReturn | null>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const transferFocusRef = useRef(false);

  const dismiss = useCallback(() => {
    transferFocusRef.current = false;
    onClose();
  }, [onClose]);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!open || dialog === null) return;
    previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    transferFocusRef.current = false;
    if (!dialog.open) dialog.showModal();
    const onBackdropClick = (event: MouseEvent) => {
      if (event.target === dialog) dismiss();
    };
    dialog.addEventListener('click', onBackdropClick);
    return () => {
      dialog.removeEventListener('click', onBackdropClick);
      if (dialog.open) dialog.close();
      if (!transferFocusRef.current && previousFocusRef.current?.isConnected) {
        previousFocusRef.current.focus({ preventScroll: true });
      }
      previousFocusRef.current = null;
    };
  }, [open, dismiss]);

  if (!open) return null;

  const activeRows = conversations.filter((row) => row.archivedAt == null);
  const sorted = [...activeRows].toSorted((left, right) => {
    if (left.isLead === true && right.isLead !== true) return -1;
    if (right.isLead === true && left.isLead !== true) return 1;
    return right.updatedAt - left.updatedAt;
  });
  const now = Date.now();

  return (
    <LayerDepthProvider>
      <LayerDismissal
        ref={dismissal}
        isActive={open}
        getContainer={() => dialogRef.current}
        onDismiss={dismiss}
      />
      <dialog
        ref={dialogRef}
        className={styles.dialog}
        aria-label="Switch conversation"
        onCancel={(event) => {
          event.preventDefault();
          if (dismissal.current?.shouldDismissOnCloseRequest() === false) return;
          dismiss();
        }}
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
            <button type="button" className={styles.closeButton} aria-label="Close switcher" onClick={dismiss}>
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
                  const age = compactAge(row.updatedAt, now);
                  return (
                    <li key={row.id} className={`${styles.groupItem} ${selected ? styles.groupItemSelected : ''}`}>
                      <button
                        type="button"
                        className={styles.rowButton}
                        aria-current={selected ? 'true' : undefined}
                        onClick={() => {
                          transferFocusRef.current = true;
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
                  transferFocusRef.current = true;
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
      </dialog>
    </LayerDepthProvider>
  );
}
