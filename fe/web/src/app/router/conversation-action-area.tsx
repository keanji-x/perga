import { useEffect, useRef, type RefObject } from 'react';
import type { AskAnswer } from '../../../../core/api/generated/wire.ts';
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
  options?: Pick<ConversationPanelOptions, 'onArchive' | 'workspace'> & { sideError?: string | null };
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
      if (shownComposer.current === id) setArchiveFailure({ id, message: error instanceof Error ? error.message : '无法更新归档状态，请重试。' });
    }).finally(() => { archiveLease.current.delete(id); });
  };
  const action = deriveConversationAction({
    readError: store.historyError ?? store.runError,
    readonlyReason: store.readonlyReason, canReopen: store.canReopen,
    state: store.sessionState, unknownDetail: store.unknownDetail,
    hasAsks: conversationAsks.length > 0 || conversationApprovals.length > 0,
    localSend: store.failedSend !== null ? 'failed' : store.sending ? 'sending' : 'idle',
    sendUnconfirmed: store.failedSend?.delivery === 'unknown',
    actionError: store.actionError ?? archiveError ?? options?.sideError ?? null, workerPaused: store.workerPaused,
  });
  const [pausedDismissedFor, setPausedDismissedFor] = useState<string | null>(null);
  useEffect(() => { if (!store.workerPaused) setPausedDismissedFor(null); }, [store.workerPaused]);
  const actionArea = action === null || (action.kind === 'workerPaused' && pausedDismissedFor === existingId)
    ? options?.workspace?.notice == null ? null : <section data-nc-conversation-action="workspace" aria-label="工作区状态">{options.workspace.notice}</section> :
    <section data-nc-conversation-action={action.kind} aria-label="待处理操作">
      {action.kind === 'ask' ? <><ApprovalDrawer approvals={conversationApprovals} onDecide={decideApproval} />
        <PlannerAskDrawer asks={conversationAsks} onAnswer={answerAsk} /></> :
      <ChatFooterNotice tone={action.kind === 'readonly' || action.kind === 'workerPaused' ? 'neutral' : 'error'}>
        {action.kind === 'readError' ? <>
          <span>无法同步最新对话状态</span><span>{action.message}</span><span>刷新仅重新读取数据。</span>
          <ChatFooterRemedy pending={store.historyLoading || store.runLoading} onClick={() => { if (store.historyError !== null) store.retryHistory(); if (store.runError !== null) store.retryRun(); }}>刷新</ChatFooterRemedy>
        </> : action.kind === 'readonly' ? <>
          <span>{action.reason === 'archived' ? '对话已归档' : action.reason === 'track_closed' ? 'Track 已关闭' : '对话已结束'}</span>
          {archiveError !== null && <ChatFooterError message={archiveError} />}
          {action.canReopen && options?.onArchive !== undefined && <ChatFooterRemedy onClick={() => changeArchive(false)}>恢复对话</ChatFooterRemedy>}
        </> : action.kind === 'unknown' ? <>
          <span>会话状态未知</span><span>{action.detail?.detail}</span>
          {action.detail?.lastError && <ChatFooterError message={action.detail.lastError} />}
          {store.actionError !== null && <ChatFooterError message={store.actionError} />}
          {action.detail?.canReconnect && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reconnect')}>重连原会话</ChatFooterRemedy>}
          {action.detail?.canResetContext && <ChatFooterRemedy disabled={store.recovering} onClick={() => store.recover('reset_context')}>重置上下文并继续</ChatFooterRemedy>}
          {store.recovering && <span>正在恢复会话…</span>}
        </> : action.kind === 'sendFailed' ? <>
          <span>{action.unconfirmed ? '发送结果未确认。' : `消息未发送。${store.failedSend?.code === 'session_recovery_required' ? '会话需要恢复，请先恢复会话后再发送。' : store.failedSend?.message ?? ''}`}</span>
          <ChatFooterRemedy disabled={store.sending} onClick={retrySend}>{action.unconfirmed ? '安全重试（同幂等键）' : '重试'}</ChatFooterRemedy>
          <ChatFooterRemedy onClick={() => { if (store.failedSend !== null) {
            if (action.unconfirmed) store.dismissFailedSend(store.failedSend.key);
            else { store.discardFailedSend(store.failedSend.key); if (existingId !== null) focusComposer(existingId); }
          } }}>{action.unconfirmed ? '忽略' : '放回输入框'}</ChatFooterRemedy>
        </> : <>
          <span>{action.kind === 'actionError' ? action.message : '已暂停，发送消息即可继续。'}</span>
          <ChatFooterRemedy onClick={() => { if (action.kind === 'workerPaused') setPausedDismissedFor(existingId); else { store.dismissActionError(); setArchiveFailure(null); } }}>关闭</ChatFooterRemedy>
        </>}
      </ChatFooterNotice>}
    </section>;

  return { actionArea, changeArchive };
}
