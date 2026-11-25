import { useEffect, useRef, type RefObject } from 'react';
import type { AskAnswer } from '../../../../core/domain/presentation-types.ts';
import type { AskId } from '../../../../core/domain/activity.ts';
import type { PendingApproval } from '../../../../core/domain/approval.ts';
import type { OpenAsk } from '../../../../core/domain/ask.ts';
import { deriveConversationAction } from '../../../../core/domain/conversation-action.ts';
import type { Conversation } from '../../../../core/domain/conversation.ts';
import { ChatFooterError, ChatFooterNotice, ChatFooterRemedy } from '../../features/chat/thread/public.tsx';
import { ApprovalDrawer, PlannerAskDrawer } from '../../features/planner/public.ts';
import { useState } from '../../ui/state/public.ts';
import type { useConversationStore } from '../conversations/store.ts';
import type { ConversationPanelOptions } from './conversation-panel-contract.ts';

/** Recovery, archive and actionable notices for one pane, scoped to its shown resource. */
export function useConversationActionArea({ open, options, store, shownComposer, existingId,
  conversationAsks, conversationApprovals, decideApproval, answerAsk, retrySend, focusComposer }: {
  open: Conversation | null;
  options?: Pick<ConversationPanelOptions, 'onArchive' | 'workspace'>;
  store: ReturnType<typeof useConversationStore>;
  shownComposer: RefObject<string | null>;
  existingId: string | null;
  conversationAsks: readonly OpenAsk[];
  conversationApprovals: readonly PendingApproval[];
  decideApproval: (approval: PendingApproval, optionId: string) => Promise<void>;
  answerAsk: (askId: AskId, answers: readonly AskAnswer[]) => Promise<void>;
  retrySend: () => void;
  focusComposer: (id: string) => void;
}) {
  const [archiveFailure, setArchiveFailure] = useState<Readonly<{ id: string; message: string }> | null>(null);
  const archiveLease = useRef(new Set<string>());
  const archiveError = archiveFailure?.id === existingId ? archiveFailure.message : null;
  const changeArchive = (archived: boolean) => {
    if (open === null || options?.onArchive === undefined || archiveLease.current.has(open.id)) return;
    const id = open.id;
    archiveLease.current.add(id);
    setArchiveFailure(null);
    void options.onArchive(id, archived).catch((error: unknown) => {
      if (shownComposer.current === id) setArchiveFailure({ id, message: error instanceof Error ? error.message : 'Unable to update archive status. Please try again.' });
    }).finally(() => { archiveLease.current.delete(id); });
  };
  const action = deriveConversationAction({
    readError: store.historyError ?? store.runError,
    readonlyReason: store.readonlyReason, canReopen: store.canReopen,
    state: store.sessionState, unknownDetail: store.unknownDetail,
    hasAsks: conversationAsks.length > 0 || conversationApprovals.length > 0,
    localSend: store.failedSend !== null ? 'failed' : store.sending ? 'sending' : 'idle',
    sendUnconfirmed: store.failedSend?.delivery === 'unknown',
    actionError: store.actionError ?? archiveError, workerPaused: store.workerPaused,
  });
  const [pausedDismissedFor, setPausedDismissedFor] = useState<string | null>(null);
  useEffect(() => { if (!store.workerPaused) setPausedDismissedFor(null); }, [store.workerPaused]);
  const actionArea = action === null || (action.kind === 'workerPaused' && pausedDismissedFor === existingId)
    ? options?.workspace?.notice == null ? null : <section data-nc-conversation-action="workspace" aria-label="Workspace status">{options.workspace.notice}</section> :
    <section data-nc-conversation-action={action.kind} aria-label="Pending actions">
      {action.kind === 'ask' ? <><ApprovalDrawer approvals={conversationApprovals} onDecide={decideApproval} />
        <PlannerAskDrawer asks={conversationAsks} onAnswer={answerAsk} /></> :
      <ChatFooterNotice tone={action.kind === 'readonly' || action.kind === 'workerPaused' ? 'neutral' : 'error'}>
        {action.kind === 'readError' ? <>
          <span>Unable to sync latest conversation state</span><span>{action.message}</span><span>Refreshing only re-reads stored data.</span>
          <ChatFooterRemedy pending={store.historyLoading || store.runLoading} onClick={() => { if (store.historyError !== null) store.retryHistory(); if (store.runError !== null) store.retryRun(); }}>Refresh</ChatFooterRemedy>
        </> : action.kind === 'readonly' ? <>
          <span>{action.reason === 'archived' ? 'Conversation archived' : action.reason === 'track_closed' ? 'Track closed' : action.reason === 'task_verifying' ? 'Task verifying' : 'Conversation ended'}</span>
          {archiveError !== null && <ChatFooterError message={archiveError} />}
          {action.canReopen && options?.onArchive !== undefined && <ChatFooterRemedy onClick={() => changeArchive(false)}>Restore conversation</ChatFooterRemedy>}
        </> : action.kind === 'unknown' ? <>
          <span>Session state unknown</span><span>{action.detail?.detail}</span>
          {action.detail?.lastError && <ChatFooterError message={action.detail.lastError} />}
          {store.actionError !== null && <ChatFooterError message={store.actionError} />}
          {action.detail?.canReconnect && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reconnect')}>Reconnect session</ChatFooterRemedy>}
          {action.detail?.canResetContext && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reset_context')}>Reset context and continue</ChatFooterRemedy>}
          {store.recovering && <span>Recovering session…</span>}
        </> : action.kind === 'sendFailed' ? <>
          <span>{action.unconfirmed ? 'Send result unconfirmed.' : `Message not sent. ${store.failedSend?.code === 'session_recovery_required' ? 'Session recovery is required before sending.' : store.failedSend?.message ?? ''}`}</span>
          <ChatFooterRemedy disabled={store.sending} onClick={retrySend}>{action.unconfirmed ? 'Retry safely (same key)' : 'Retry'}</ChatFooterRemedy>
          <ChatFooterRemedy onClick={() => { if (store.failedSend !== null) {
            if (action.unconfirmed) store.dismissFailedSend(store.failedSend.key);
            else { store.discardFailedSend(store.failedSend.key); if (existingId !== null) focusComposer(existingId); }
          } }}>{action.unconfirmed ? 'Dismiss' : 'Restore to input'}</ChatFooterRemedy>
        </> : <>
          <span>{action.kind === 'actionError' ? action.message : 'Paused. Send a message to continue.'}</span>
          <ChatFooterRemedy onClick={() => { if (action.kind === 'workerPaused') setPausedDismissedFor(existingId); else { store.dismissActionError(); setArchiveFailure(null); } }}>Dismiss</ChatFooterRemedy>
        </>}
      </ChatFooterNotice>}
    </section>;

  return { actionArea, changeArchive };
}
