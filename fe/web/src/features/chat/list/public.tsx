// The conversation list module of a route's panel card: presentational, it fetches nothing and opens nothing.

import { ListText } from '../../../ui/list-typography/public.tsx';
import { useId } from 'react';
import { useState } from '../../../ui/state/public.ts';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon } from '../../../ui/icon/public.tsx';
import { ConversationGroups } from './groups.tsx';
import { activityLabelOf, type CardActivity } from '../../../../../core/domain/activity.ts';
import { conversationName, CONVERSATION_KIND_LABEL, type Conversation } from '../../../../../core/domain/conversation.ts';
import { deriveConversationList } from '../../../../../core/view/conversation-list.ts';
import { ContextIconSlot, ContextItem, ContextList } from '../../../ui/panel-card/context.tsx';
import { ActivityIndicator } from '../../../ui/activity-indicator/public.tsx';
import { EmptyState } from '../../../ui/empty-state/public.tsx';
import styles from './list.module.css';

export type ChatListProps = Readonly<{
  conversations: readonly Conversation[];
  /** The kernel's per-card verdicts keyed by card id (a row's id is its card id); a caller with no overlay passes `{}`. */
  cards: Readonly<Record<string, CardActivity>>;
  /** Which row is open in the drawer, if any. */
  activeId?: string | null;
  /** Whether to name the track on each row — false when the page *is* a track. */
  showTrack?: boolean;
  /** Contextual entries reuse the ordinary list activity fold. */
  context?: boolean;
  variant?: 'panel' | 'mobile-page';
  unreadIds?: ReadonlySet<string>;
  /** Explicit question ownership from the host, independent of per-card execution activity. */
  awaitingInputIds?: ReadonlySet<string>;
  onOpen: (conversation: Conversation) => void;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>;

export function ChatList({
  conversations, cards, activeId = null, showTrack = true, onOpen, onArchive, unreadIds, awaitingInputIds, context = false, variant = 'panel',
}: ChatListProps) {
  const descriptionPrefix = useId();
  if (conversations.length === 0) return context ? null : <EmptyState variant="compact" icon={<Icon name="chat" />} title="No conversations yet" description="Conversations started here appear in this list." />;

  const List = context ? ContextList : 'ul';
  const listClassName = variant === 'mobile-page' ? `${styles.list} ${styles.mobilePageList}` : styles.list;
  return (
    <ConversationGroups variant={variant} enabled={onArchive !== undefined || conversations.some(row => row.archivedAt != null)} conversations={conversations} emptyActive={context ? null : <EmptyState variant="compact" icon={<Icon name="chat" />} title="No active conversations" description="Active conversations appear here." />}>
    {shown => <List className={context ? undefined : listClassName}>
      {deriveConversationList({ conversations: shown, cards, unreadIds, awaitingInputIds }).map(({ conversation, activity, unread, name }) => {
        const description = activity === 'working' ? null : activityLabelOf(activity);
        const descriptionId = `${descriptionPrefix}-${encodeURIComponent(conversation.id)}`;
        const active = conversation.id === activeId;
        const trackTitle = conversation.trackTitle;
        const turns = conversation.turns;
        if (context) {
          const role = conversation.isLead === true ? 'Planner' : CONVERSATION_KIND_LABEL[conversation.kind];
          const contextDescription = [name === role ? null : role, description].filter(Boolean).join(' · ') || null;
          return <li key={conversation.id}>
          <ContextConversationItem conversation={conversation} onArchive={onArchive} icon={conversation.isLead === true ? 'tasks' : 'chat'} label={name}
            trailing={activity === 'quiet' ? undefined : <ContextIconSlot><ActivityIndicator state={activity} /></ContextIconSlot>} unread={unread} selected={active} buttonProps={{
            'aria-label': `Conversation ${name}${activity === 'working' ? ', working' : ''}`,
            'aria-current': active ? 'true' : undefined,
            'aria-describedby': contextDescription === null ? undefined : descriptionId,
            title: `${role}: ${name}`,
            onClick: () => onOpen(conversation),
          }} metadata={contextDescription === null ? undefined : <span hidden id={descriptionId}>{contextDescription}</span>} />
        </li>;
        }
        return (
          <li key={conversation.id} className={`${styles.item} ${onArchive !== undefined && conversation.kind === 'agent' && conversation.isLead !== true ? styles.withActions : ''}`}>
            <button
              type="button"
              data-nc-role="row"
              className={`${styles.row} ${active ? styles.rowActive : ''}`}
              aria-current={active ? 'true' : undefined}
              aria-label={`Conversation ${name}`
                + (showTrack && trackTitle !== undefined ? `, on ${trackTitle}` : '')
                + (turns === undefined ? '' : `, ${turns} turns`)
                + (activity === 'working' ? ', working' : '')}
              aria-describedby={description === null ? undefined : descriptionId}
              onClick={() => onOpen(conversation)}
            >
              <span className={styles.label}>
                <ListText tone="group" emphasis={active ? 'selected' : undefined} className={styles.name}>{name}</ListText>
                {showTrack && trackTitle !== undefined && (
                  <ListText tone="secondary" className={styles.track}>{trackTitle}</ListText>
                )}
              </span>
            </button>
            {description !== null && <span hidden id={descriptionId}>{description}</span>}
            {onArchive !== undefined && conversation.kind === 'agent' && conversation.isLead !== true && <ConversationArchiveAction conversation={conversation} onArchive={onArchive} className={styles.actions} triggerClassName={variant === 'mobile-page' ? styles.mobileActionButton : undefined} />}
            {activity !== 'quiet' && <span className={styles.statusSlot} aria-hidden="true">
              <ActivityIndicator state={activity} />
            </span>}
          </li>
        );
      })}
    </List>}
    </ConversationGroups>
  );
}

/** Failed writes leave the row visible. */
function ConversationArchiveAction({ conversation, onArchive, className, triggerClassName }: Readonly<{
  conversation: Conversation;
  onArchive: (cardId: string, archived: boolean) => Promise<void>;
  className?: string;
  triggerClassName?: string;
}>) {
  const { pending, error, change } = useArchiveAction(conversation, onArchive);
  const archived = conversation.archivedAt != null;
  return <>
    <div className={className}>
      <DropdownMenu placement="below" alignment="end" button={{
        label: `Actions for conversation: ${conversationName(conversation)}`, icon: <Icon name="more" size="sm" />,
        isIconOnly: true, variant: 'ghost', size: 'sm', isDisabled: pending, className: triggerClassName,
      }}>
        <DropdownMenuItem label={archived ? 'Restore conversation' : 'Archive conversation'} onClick={() => { void change(); }} />
      </DropdownMenu>
    </div>
    {error !== null && <p role="alert" className={styles.archiveError}>{error}</p>}
  </>;
}


function useArchiveAction(conversation: Conversation, onArchive: ((cardId: string, archived: boolean) => Promise<void>) | undefined) {
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const archived = conversation.archivedAt != null;
  const change = async () => {
    if (pending) return;
    setPending(true); setError(null);
    try { await onArchive?.(conversation.id, !archived); }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Could not update this conversation. Try again.'); }
    finally { setPending(false); }
  };
  return { pending, error, change };
}

/** Archived history stays readable; restoring does not create a replacement session. */
export function ArchivedConversationNotice({ conversation, onArchive }: Readonly<{
  conversation: Conversation;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>) {
  const { pending, error, change } = useArchiveAction(conversation, onArchive);
  return <div className={styles.archiveNotice}>
    <p>This conversation is archived. Restore it to continue.</p>
    {onArchive !== undefined && <button type="button" disabled={pending} onClick={() => { void change(); }}>
      {pending ? 'Restoring…' : 'Restore conversation'}
    </button>}
    {error !== null && <p role="alert">{error}</p>}
  </div>;
}

/** ContextItem owns the current row/menu/status geometry; this feature owns its action. */
function ContextConversationItem({ conversation, onArchive, ...props }: React.ComponentProps<typeof ContextItem> & Readonly<{
  conversation: Conversation;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
}>) {
  const { pending, error, change } = useArchiveAction(conversation, onArchive);
  const canArchive = onArchive !== undefined && conversation.kind === 'agent' && conversation.isLead !== true;
  return <>
    <ContextItem {...props} menuLabel={`Actions for conversation: ${conversationName(conversation)}`}
      menu={canArchive ? <DropdownMenuItem label={conversation.archivedAt != null ? 'Restore conversation' : 'Archive conversation'}
        isDisabled={pending} onClick={() => { void change(); }} /> : undefined} />
    {error !== null && <p role="alert" className={styles.archiveError}>{error}</p>}
  </>;
}
