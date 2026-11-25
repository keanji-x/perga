import { ConversationGroups } from '../../features/chat/list/groups.tsx';
import { ConversationArchiveAction } from '../../features/chat/list/public.tsx';
import { trackEmptyContent } from './track-empty-content.tsx';
import { EmptyState } from '../../ui/empty-state/public.tsx';
import { mobileFontClassName } from '../../ui/mobile-font/public.ts';
import { useCallback, useLayoutEffect, useMemo, useRef } from 'react';
import { useState } from '../../ui/state/public.ts';
import type { Track } from '../../../../core/domain/track.ts';
import { MobileTrackList } from './mobile-track-list.tsx';
import type { SidebarTrackGroups } from './sidebar-track-groups.ts';
import { MobileNav } from '@astryxdesign/core/MobileNav';
import { Button } from '@astryxdesign/core/Button';
import { DropdownMenu } from '@astryxdesign/core/DropdownMenu';
import type { Conversation } from '../../../../core/domain/conversation.ts';
import { conversationName } from '../../../../core/domain/conversation.ts';
import { localDailyDate } from '../../../../core/domain/daily-planner.ts';
import { Icon as UiIcon } from '../../ui/icon/public.tsx';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { LoadingStatus } from '../../ui/loading-status/public.tsx';
import styles from './mobile-history.module.css';

type HistoryView = 'current' | 'pinned' | 'unread' | 'waiting';
const VIEWS: readonly HistoryView[] = Object.freeze(['current', 'pinned', 'unread', 'waiting']);
function viewLabel(view: HistoryView): string {
  switch (view) {
    case 'current': return 'This track';
    case 'pinned': return 'Pinned';
    case 'unread': return 'Unread';
    case 'waiting': return 'Attention';
  }
}

function historyTitle(row: Conversation): string {
  const name = conversationName(row);
  return row.trackTitle && (row.isLead === true || row.title === null) ? row.trackTitle : name;
}

function historySubtitle(row: Conversation): string {
  return historyTitle(row) === row.trackTitle ? conversationName(row) : row.trackTitle ?? '';
}

function historyTimeLabel(at: number): string {
  return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(at);
}

function historyDateLabel(at: number): string {
  return localDailyDate(new Date(at));
}

function historyMetaText(row: Conversation): string {
  const date = historyDateLabel(row.updatedAt);
  // Daily tracks use local YYYY-MM-DD as their title; show local time instead of repeating the date.
  const stamp = historyTitle(row) === date ? historyTimeLabel(row.updatedAt) : date;
  if (row.isLead === true) return `Lead · ${stamp}`;
  const subtitle = historySubtitle(row);
  return subtitle ? `${subtitle} · ${stamp}` : stamp;
}

function groupName(at: number, now: number): string {
  const today = new Date(now); today.setHours(0, 0, 0, 0);
  const yesterday = new Date(today); yesterday.setDate(today.getDate() - 1);
  return at >= today.getTime() ? 'Today' : at >= yesterday.getTime() ? 'Yesterday' : 'Earlier';
}

export function MobileHistory({ owner, open, onOpenChange, conversations, loading, failed, onRetry, onNew, onSelect, onArchive,
  shareMode = false, onOpenSettings, accountLabel, accountInitial, onSignOut, now, scopeLabel, selectedConversationId, trackGroups, currentTrackId, onOpenTrack, isUnread, tracksLoading, tracksError, onRetryTracks }: Readonly<{
  shareMode?: boolean;
  /** Shell route/registration lifetime, not a label or callback identity. */
  owner: object;
  selectedConversationId: string | null;
  trackGroups: Pick<SidebarTrackGroups, 'pinned' | 'unread' | 'waiting'>;
  currentTrackId: string | undefined;
  onOpenTrack: (trackId: string) => void;
  isUnread: (track: Track) => boolean;
  tracksLoading: boolean;
  tracksError: string | null;
  onRetryTracks: () => void;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  conversations: readonly Conversation[];
  loading: boolean;
  failed: boolean;
  onRetry: () => void;
  onNew: (() => void) | null;
  onSelect: (conversation: Conversation) => void;
  onArchive?: (cardId: string, archived: boolean) => Promise<void>;
  onOpenSettings: () => void;
  scopeLabel: string;
  accountLabel: string;
  accountInitial: string;
  onSignOut?: (() => void) | undefined;
  now: number;
}>) {
  const [view, setView] = useState<HistoryView>('current');
  const wasOpen = useRef(open);
  useLayoutEffect(() => {
    if (wasOpen.current !== open) { wasOpen.current = open; if (open) setView('current'); }
  }, [open]);
  const list = useRef<HTMLDivElement | null>(null);
  const scrollTop = useRef(0);
  const captureList = useCallback((node: HTMLDivElement | null) => {
    if (list.current !== null) scrollTop.current = list.current.scrollTop;
    list.current = node;
    if (node !== null) node.scrollTop = scrollTop.current;
  }, []);
  const content = useRef<HTMLDivElement | null>(null);
  const [presentation, setPresentation] = useState(() => ({ owner, open, key: 0, blocked: false }));
  useLayoutEffect(() => {
    setPresentation(current => {
      if (current.owner === owner && current.open === open) return current;
      const fresh = current.owner !== owner || open;
      return { owner, open, key: current.key + (fresh ? 1 : 0), blocked: fresh ? false : current.blocked };
    });
  }, [owner, open]);
  // A native close event has no generation. A fresh public MobileNav node on
  // reopen/owner replacement isolates queued old-node events, without keying
  // this History owner (view/selection/scroll state stays here).
  const session = useMemo(() => ({ key: presentation.key, node: null as HTMLDialogElement | null, open: false,
    admitted: false, action: null as (() => void) | null }), [presentation.key]);
  const didClose = useCallback(() => {
    const action = session.action;
    session.action = null; // Consume before invoke, including reentrant close.
    if (!session.open && session.node?.isConnected && !session.node.open) action?.();
  }, [session]);
  const captureDialog = useCallback((node: HTMLDialogElement | null) => {
    session.node?.removeEventListener('close', didClose);
    session.action = null;
    session.node = node;
    node?.addEventListener('close', didClose);
  }, [session, didClose]);
  useLayoutEffect(() => { session.open = open; }, [session, open]);
  useLayoutEffect(() => () => {
    session.action = null;
    session.node?.removeEventListener('close', didClose);
  }, [session, didClose]);
  const leave = (action: () => void) => {
    if (!session.open || session.admitted || !session.node?.open || !session.node.isConnected) return;
    session.admitted = true;
    session.action = action;
    // Revoke the app content in this event, even before the parent commits close.
    if (content.current !== null) content.current.inert = true;
    setPresentation(previous => ({ ...previous, blocked: true }));
    onOpenChange(false);
  };
  return <MobileNav key={session.key} id="mobile-conversation-history" ref={captureDialog} isOpen={open} onOpenChange={onOpenChange} side="start" width={320}
    label={view === 'current' ? 'Conversations' : viewLabel(view)} header={<span className={styles.drawerHeading}>{view === 'current' ? 'Conversations' : viewLabel(view)}</span>} className={`${styles.drawer} ${mobileFontClassName}`}
    >
    <div ref={content} className={styles.layout} inert={presentation.blocked || !open}
      onClickCapture={event => {
        if (session.admitted || !session.open) { event.preventDefault(); event.stopPropagation(); }
      }}>
      <nav className={styles.views} aria-label="Sidebar views">
        {VIEWS.map((candidate) => {
          const count = candidate === 'current' ? conversations.length : trackGroups[candidate].length;
          return <button key={candidate} type="button" className={styles.view} aria-label={viewLabel(candidate)}
            aria-pressed={view === candidate} onClick={() => { setView(candidate); if (list.current !== null) list.current.scrollTop = 0; }}>
            <span>{viewLabel(candidate)}</span>{count > 0 && <span className={styles.count} aria-hidden="true">{count > 99 ? '99+' : count}</span>}
          </button>;
        })}
      </nav>
      {view === 'current' && <div className={styles.functions}>
        <Button className={styles.functionButton} label="New conversation" variant="ghost" size="lg" isDisabled={onNew === null}
          icon={<UiIcon name="plus" />} onClick={() => { if (onNew !== null) leave(onNew); }} />

      </div>}
      <div ref={captureList} className={styles.list}>
        {view === 'current' ? <>
        <p className={styles.scope}>{scopeLabel}</p>
        {failed && <ErrorBox message="Some conversations could not be loaded." actionLabel="Retry" onRetry={onRetry} />}
        {loading && <LoadingStatus>Loading conversations…</LoadingStatus>}
        <ConversationGroups conversations={conversations} emptyActive={!loading && !failed ? <EmptyState variant="compact" icon={<UiIcon name="chat" />} title={conversations.length === 0 ? 'No conversations in this track yet.' : 'No active conversations.'} description="Active conversations for this track appear here." /> : null}>
          {rows => {
            const groups = new Map<string, Conversation[]>();
            for (const row of rows) {
              const name = groupName(row.updatedAt, now);
              const group = groups.get(name) ?? []; group.push(row); groups.set(name, group);
            }
            return <>
              {[...groups].map(([name, rows]) => <section key={name} className={styles.dateGroup} aria-label={name}>
                <h2 className={styles.group}>{name}</h2>
                {rows.map((row) => <div key={row.id} className={styles.conversationRow}><button type="button" className={styles.row} aria-current={row.id === selectedConversationId ? 'true' : undefined}
                  aria-label={`${historyTitle(row)}${historySubtitle(row) ? ` ${historySubtitle(row)}` : ''}`} onClick={() => leave(() => onSelect(row))}>
                  <span className={styles.title}>{historyTitle(row)}</span>
                  <span className={styles.meta} title={historySubtitle(row)}>{historyMetaText(row)}</span>
                </button>
                {onArchive !== undefined && row.kind === 'agent' && row.isLead !== true && <ConversationArchiveAction
                  conversation={row} onArchive={onArchive} className={styles.conversationActions} />}
                </div>)}
              </section>)}
            </>;
          }}
        </ConversationGroups>
        </> : <>
          <p className={styles.scope}>{shareMode ? 'Shared tracks' : 'All areas'}</p>
          {tracksError !== null && <ErrorBox message={tracksError} actionLabel="Retry" onRetry={onRetryTracks} />}
          {tracksLoading && <LoadingStatus>Loading tracks…</LoadingStatus>}
          <MobileTrackList tracks={trackGroups[view]} currentTrackId={currentTrackId} isUnread={isUnread}
            onOpenTrack={(trackId) => leave(() => onOpenTrack(trackId))}
            emptyState={!tracksLoading && tracksError === null ? trackEmptyContent(view) : undefined} />
        </>}
      </div>
      {!shareMode && <div className={styles.footer}>
        {onSignOut !== undefined ? (
          <DropdownMenu placement="above" alignment="start" hasChevron={false}
            button={{ label: accountLabel, variant: 'ghost', size: 'lg', className: styles.footerButton, icon: <span className={styles.avatar}>{accountInitial}</span> }}
            items={[{ label: 'Sign out', onClick: () => leave(onSignOut) }]} />
        ) : (
          <span className={styles.footerButton}><span className={styles.avatar}>{accountInitial}</span>{accountLabel}</span>
        )}
        <Button className={styles.footerButton} label="Settings" variant="ghost" size="lg" icon={<UiIcon name="settings" />}
          onClick={() => leave(onOpenSettings)} />
      </div>}
    </div>
  </MobileNav>;
}
