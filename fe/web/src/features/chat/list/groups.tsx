import { useId, type ReactNode } from 'react';
import { useCollapsible } from '@astryxdesign/core/Collapsible';
import type { Conversation } from '../../../../../core/domain/conversation.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import { ListText } from '../../../ui/list-typography/public.tsx';
import { SpringRotation } from '../../../ui/motion/rotation.tsx';
import styles from './groups.module.css';

/** Shared archive membership and independent disclosures for every conversation history surface. */
export function ConversationGroups({ conversations, children, emptyActive, enabled = true, variant = 'panel' }: Readonly<{
  conversations: readonly Conversation[];
  children: (rows: readonly Conversation[]) => ReactNode;
  emptyActive: ReactNode;
  enabled?: boolean;
  variant?: 'panel' | 'mobile-page';
}>) {
  if (!enabled) return children(conversations);
  const active = conversations.filter(row => row.archivedAt == null);
  const archived = conversations.filter(row => row.archivedAt != null);
  /* Only introduce Active/Archived group headers once archived rows actually exist. */
  if (archived.length === 0) {
    return active.length === 0 ? emptyActive : children(active);
  }
  return <>
    <ConversationGroup title="Active" label="Active conversations" rows={active} defaultIsOpen variant={variant}>
      {active.length === 0 ? emptyActive : children(active)}
    </ConversationGroup>
    <ConversationGroup title="Archived" label="Archived conversations" rows={archived} defaultIsOpen={false} variant={variant}>
      {children(archived)}
    </ConversationGroup>
  </>;
}

function ConversationGroup({ title, label, rows, defaultIsOpen, variant, children }: Readonly<{
  title: string;
  label: string;
  rows: readonly Conversation[];
  defaultIsOpen: boolean;
  variant: 'panel' | 'mobile-page';
  children: ReactNode;
}>) {
  const contentId = useId();
  const disclosure = useCollapsible({ isCollapsible: { defaultIsOpen } });
  return <section className={styles.group} aria-label={label}>
    <button type="button" className={`${styles.heading} ${variant === 'mobile-page' ? styles.mobileHeading : ''}`} aria-label={label}
      aria-expanded={disclosure.isOpen} aria-controls={contentId} onClick={disclosure.toggle}>
      <SpringRotation className={styles.chevron} angle={disclosure.isOpen ? 90 : 0}>
        <Icon name="chevron-right" size="sm" />
      </SpringRotation>
      <ListText tone="section">{title}</ListText>
      <ListText tone="count">{rows.length}</ListText>
    </button>
    <div id={contentId} hidden={!disclosure.isOpen}>{disclosure.isOpen && children}</div>
  </section>;
}
