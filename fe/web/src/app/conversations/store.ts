import { isSessionRecoveryRequired } from '../../../../core/domain/conversation-delivery.ts';
import { conversationStopTurn } from '../../../../core/domain/conversation-action.ts';
import { sessionContextUsage } from '../../../../core/domain/session-context-usage.ts';
import { ApiError, NotSentError } from '../../../../core/domain/failure-class.ts';
import { withTaskMessageFailures, workerModelCatalog } from '../../../../core/domain/worker-session.ts';
import type { PermissionModeOptionDto, WorkerSettings } from '../../../../core/api/perga-v1.ts';
import { pendingQueueWithCurrentHumanVersions, pendingQueueOwnsHumanEntry } from '../../../../core/domain/conversation-queue-controls.ts';
import { useCurrentUser } from '../auth/current-user.tsx';
import { buildConversationTranscript, conversationPendingQueue, conversationRunPhase, nativeDisplayRows } from '../../../../core/domain/conversation-display.ts';
import { useEffect, useMemo, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { COMPACT_FAILURES, COMPACT_TEXT, FOLLOW_INSTALLATION_DEFAULT, isConversationMessage, isOptimisticConversationTurn, isUserConversationTurn, kernelQueuesInput, MODEL_CHANGE_TEXT, PLANNER_MODEL_FAILURES, type PendingQueueEntry, type PlannerQueueWriteOutcome } from '../../../../core/domain/conversation.ts';
import { describeConversation, pendingConversationIds, withRememberedConversation, type ConversationFacts } from '../../../../core/domain/conversation-summary.ts';
import { useTranscriptReads, useTranscriptHistory } from './history.ts';
import { presentPendingMessages } from '../../../../core/domain/conversation-outbox.ts';
import { refusalText, writeFailureOf } from '../../../../core/domain/failure-class.ts';
import { PERMISSION_MODE_CHANGE_TEXT, PLANNER_PERMISSION_MODE_FAILURES } from '../../../../core/domain/planner-permission-mode.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import type { ConversationStopFeedback } from '../../../../core/domain/conversation-stop.ts';
import { conversationHistoryQueryOptions, modelCatalogQueryOptions, plannerRunQueryOptions, usePlannerMutations } from '../providers/queries.ts';
import { useState } from '../../ui/state/public.ts';
import { useConversationAcceptedTurn, useConversationCommands, useConversationSendOps, useConversationQueueWriteOut, useConversationRememberedTurns, useRememberedConversations } from './public.tsx';
import { NO_TURNS } from './registry-owner.ts';
import { useConversationStop } from './stop.ts';
import { useLiveReplies } from './live-replies.ts';
import { useConversationOutbox } from './outbox.ts';
import type { ConversationRouteIntent, ConversationStore, PlannerConversationScope } from './contracts.ts';

/* A stable identity for "no queue page", so the memo below is not recomputed on
   every render by a fresh array literal. */
const EMPTY_PENDING_QUEUE: readonly PendingQueueEntry[] = Object.freeze([]);

const TASK_APPROVAL_OPTIONS: readonly PermissionModeOptionDto[] = Object.freeze([
  Object.freeze({ id: 'ask', label: 'Ask a person', description: 'Allows the session to request approval.', tone: 'warning', isDefault: true }),
  Object.freeze({ id: 'deny', label: 'Deny requests', description: 'Denies future approval requests.', tone: 'neutral', isDefault: false }),
]);

export function useConversationStore(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  scope: PlannerConversationScope | null,
  routeIntent: ConversationRouteIntent,
): ConversationStore {
  const registry = useConversationCommands();
  const currentUser = useCurrentUser();
  const { remember, rememberedTurnsOf } = registry;
  const rememberedConversations = useRememberedConversations();
  const cardId = scope?.cardId ?? '';
  const trackId = scope?.id;
  const provider = scope?.provider;
  const trackTitle = scope?.title;
  const cardTitle = scope?.cardTitle;
  const scopeUpdatedAt = scope?.updatedAt;
  const scopeKind = scope?.kind ?? 'agent';
  const scopeState = scope?.state ?? null;
  const serverRows = routeIntent.rows;
  const rememberOn = routeIntent.rememberOn;
  const ownedCardIds = routeIntent.ownedCardIds;
  const sourceCardId = serverRows.find((row) => row.id === cardId)?.sourceCardId;
  /* Held across renders: the live replies read and re-read this query by its key, and number its reads. */
  const transcriptReads = useTranscriptReads(registry.nextRead);
  const transcriptQuery = useMemo(
    () => transcriptReads.track(conversationHistoryQueryOptions(transport, cardId, unauthorized)),
    [transcriptReads, transport, cardId, unauthorized],
  );
  const history = useTranscriptHistory(transcriptQuery, scope !== null, transcriptReads);
  const { loadEarlier } = history;
  const historyKnown = history.data !== undefined;
  const runQuery = useMemo(
    () => {
      const options = plannerRunQueryOptions(transport, cardId, unauthorized);
      return { ...options, queryFn: (context: Parameters<typeof options.queryFn>[0]) => {
        // Capture the ACK identity at read start; older in-flight reads cannot retire a later ACK.
        const acceptedAtStart = registry.acceptedTurnOf(cardId);
        return options.queryFn(context).then(page => {
          // Fence deferred work before query completion can release a permission/send continuation.
          registry.observeEpoch(cardId, page.session.epoch);
          if (acceptedAtStart !== null && registry.acceptedTurnOf(cardId) === acceptedAtStart
            && conversationStopTurn(page, acceptedAtStart, true) === null)
            registry.rememberAcceptedTurn(cardId, null);
          return page;
        });
      } };
    }, [transport, cardId, unauthorized, registry],
  );
  const run = useQuery({ ...runQuery, enabled: scope !== null, refetchInterval: query =>
    query.state.data?.providerCleanup != null || query.state.data?.unknownDetail?.recovering === true
    || query.state.data?.worker?.phase === 'running' || query.state.data?.worker?.phase === 'interrupting' ? 1000 : false });
  const [readFailure, setReadFailure] = useState(() => ({ cardId, error: run.error }));
  const runReadError = run.error ?? (run.isFetching && readFailure.cardId === cardId ? readFailure.error : null);
  if (readFailure.cardId !== cardId || (run.error !== readFailure.error && (run.error !== null || !run.isFetching)))
    setReadFailure({ cardId, error: run.error });
  const worker = run.data?.worker ?? null;
  const workerSession = worker !== null || run.data?.session.boundTaskId != null || run.data?.session.boundAttemptId != null;
  const workerTurn = worker === null ? undefined : (run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : undefined);
  const workerEditable = worker?.phase === 'running' || worker?.phase === 'paused';
  const workerCatalog = useMemo(() => worker === null ? null : workerModelCatalog(worker), [worker]);
  /* The catalog rides alongside the run query: the trigger has to render the chosen
       model's name, and `planner-run` gives only its slug. */
  const modelCatalog = useQuery({
    ...modelCatalogQueryOptions(transport, { kind: 'card', cardId, provider: provider ?? 'codex' }, unauthorized), enabled: scope !== null && worker === null,
  });
  const displayItems = history.loadedRows;
  const items = useMemo(() => nativeDisplayRows(displayItems), [displayItems]);
  const rememberedSource = useConversationRememberedTurns(history.data === undefined && scope !== null ? cardId : null);
  const rememberedEntries = history.data === undefined ? rememberedSource : null;
  const queueWriteOut = useConversationQueueWriteOut(scope === null ? null : cardId);
  const taskMessages = worker?.messages;
  const viewerId = currentUser?.userId ?? null;
  const historyEntries = useMemo(() => {
    const entries = rememberedEntries ?? buildConversationTranscript(displayItems, viewerId);
    return taskMessages === undefined ? entries : withTaskMessageFailures(entries, taskMessages, viewerId);
  }, [displayItems, rememberedEntries, taskMessages, viewerId]);
  const serverTurns = useMemo(() => historyEntries.filter(isConversationMessage), [historyEntries]);
  const servedQueue = useMemo(() => run.data === undefined ? EMPTY_PENDING_QUEUE : conversationPendingQueue(run.data), [run.data]);
  const queueOps = useConversationSendOps(scope === null ? null : cardId);
  const phase = run.data === undefined ? null : conversationRunPhase(run.data);
  const sessionState = run.data?.state ?? null;
  const readonlyReason = run.data?.readonlyReason ?? null;
  /* The run page owns pending input identity. Replace its persisted rows with
     the display projection below, keeping one bubble while the queue drains. */
  const savedMessageIds = useMemo(() => new Set(displayItems.flatMap(item =>
    item.source.kind === 'human' ? [item.source.message_id] : [])), [displayItems]);
  const [handoff, setHandoff] = useState(() => ({ cardId, source: servedQueue, saved: savedMessageIds, ids: run.data?.queueHandoffIds, rows: servedQueue }));
  let handoffRows = handoff.rows;
  if (handoff.cardId !== cardId || handoff.source !== servedQueue || handoff.saved !== savedMessageIds || handoff.ids !== run.data?.queueHandoffIds) {
    const currentIds = new Set(servedQueue.map(entry => entry.entry_id));
    const handoffIds = new Set(run.data?.queueHandoffIds);
    const waitingForHistory = handoff.cardId === cardId ? handoff.rows.filter(entry =>
      (handoffIds.has(entry.entry_id) || entry.state === 'processing') &&
      !currentIds.has(entry.entry_id) && !savedMessageIds.has(entry.entry_id))
      .map((entry): PendingQueueEntry => handoffIds.has(entry.entry_id) ? { ...entry, state: 'processing' } : entry) : [];
    handoffRows = [...waitingForHistory, ...servedQueue];
    setHandoff({ cardId, source: servedQueue, saved: savedMessageIds, ids: run.data?.queueHandoffIds, rows: handoffRows });
  }
  const currentQueue = useMemo(() => {
    const entries = handoffRows.filter(entry => entry.state === 'unconfirmed' || !savedMessageIds.has(entry.entry_id));
    return pendingQueueWithCurrentHumanVersions(entries, serverTurns);
  }, [handoffRows, savedMessageIds, serverTurns]);
  const pendingQueue = currentQueue;
  const pendingQueueIds = useMemo(
    () => new Map(pendingQueue.map((entry) => [entry.entry_id, entry.rev])), [pendingQueue],
  );
  const mutations = usePlannerMutations(transport, cardId, scope === null ? null : scope.id, unauthorized, registry);
  /** What went wrong with an action of the card it names; another card's is never this one's. */
  const [compactPending, setCompactPending] = useState<ReadonlySet<string>>(() => new Set());
  const [settingsPending, setSettingsPending] = useState<ReadonlySet<string>>(() => new Set());
  const settingsLeases = useRef(new Set<string>());
  const compactLeases = useRef(new Set<string>());
  const recoveryLeases = useRef(new Set<string>());
  const [recoveryPending, setRecoveryPending] = useState<ReadonlySet<string>>(() => new Set());
  const providerCleanup = run.data?.providerCleanup ?? null;
  const recoveryHoldsSend = sessionState === 'unknown' || providerCleanup !== null || recoveryPending.has(cardId);
  const [actionError, setActionError] = useState<Readonly<{ cardId: string; message: string }> | null>(null);
  /** The card shown now; a model write's answer is that of the card it was made in. */
  const shownCardId = useRef(cardId);
  shownCardId.current = cardId;
  const serverEntries = useMemo(() => {
    if (rememberedEntries !== null) return historyEntries.filter(entry => !isOptimisticConversationTurn(entry));
    return historyEntries
      .filter(entry => !pendingQueueOwnsHumanEntry(entry,pendingQueueIds))
      .filter(entry => !isUserConversationTurn(entry) || entry.queueEntryId == null || !pendingQueueIds.has(entry.queueEntryId)
        || (entry.inputSource?.entryRev != null && entry.inputSource.entryRev !== pendingQueueIds.get(entry.queueEntryId)));
  }, [historyEntries, pendingQueueIds, rememberedEntries]);
  useEffect(() => { setActionError(null); }, [cardId]);
  /* The running turn's streamed replies: drawn at the tail, never remembered, never counted. */
  const liveReplies = useLiveReplies({
    transport, unauthorized, cardId, enabled: scope !== null && run.data !== undefined, phase,
    runningTurnId: run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : null, nextRead: registry.nextRead,
    transcriptKey: transcriptQuery.queryKey, transcriptReads, items, worker,
  });
  const acceptedTurnId = useConversationAcceptedTurn(cardId);
  const startupTurn = run.data?.lastTurn;
  const starting = run.data?.session.status === 'queued' || run.data?.session.status === 'starting'
    || startupTurn?.status === 'queued' || startupTurn?.status === 'starting';
  useEffect(() => {
    if (run.data?.session.epoch !== undefined) registry.observeEpoch(cardId, run.data.session.epoch);
    if (acceptedTurnId !== null && startupTurn?.id === acceptedTurnId &&
      startupTurn.status !== 'queued' && startupTurn.status !== 'starting' && startupTurn.status !== 'running')
      registry.rememberAcceptedTurn(cardId, null);
  }, [acceptedTurnId, cardId, registry, run.data?.session.epoch, startupTurn]);
  const stopTurnId = conversationStopTurn(run.data, acceptedTurnId);
  const working = starting || stopTurnId !== null || sessionState === 'running' || sessionState === 'stopping' || sessionState === 'compacting';
  const canStop = !recoveryHoldsSend && readonlyReason === null && working && stopTurnId !== null && sessionState !== 'stopping' && sessionState !== 'compacting';
  const stop = useConversationStop({
    cardId, canStop,
    responseEnded: !starting && acceptedTurnId === null && runReadError === null && run.data !== undefined && (phase === 'idle' || phase === 'turn_completed'),
    historyKnown: historyKnown,
    newestRowId: items.reduce((latest, row) => Math.max(latest, row.id), 0),
    completedRowId: items.reduce<number | null>((latest, row) => !row.outcome
      ? latest : Math.max(latest ?? 0, row.id), null),
    requestStop: () => {
      if (worker !== null && workerTurn !== undefined)
        return mutations.interrupt({ attemptId: worker.attemptId, turnId: workerTurn });
      return stopTurnId == null ? Promise.reject(new NotSentError()) : mutations.interrupt({ turnId: stopTurnId });
    },
  });
  const canQueueWhileRunning = phase === 'turn_running' && working && runReadError === null;
  const target = worker === null ? undefined : { attemptId: worker.attemptId, queue: worker.phase === 'running' && working };
  const steeringTarget = run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : null;
  const outbox = useConversationOutbox({
    cardId, transport, send: mutations.send, serverEntries, liveReplies,
    stalled: false, target, queuesInput: kernelQueuesInput(phase), queueIfBusy: canQueueWhileRunning,
    pressed: () => { setActionError(null); stop.clearFeedback(); },
    recoveryRequired: () => { void run.refetch(); },
  });
  const { view, accountQueueDisplay } = outbox;
  const presentation = useMemo(() => presentPendingMessages(view.transcript, pendingQueue, queueOps),
    [view.transcript, pendingQueue, queueOps]);
  const queueDisplayAccounting = useMemo(() => accountQueueDisplay(presentation.transcript, []),
    [accountQueueDisplay, presentation.transcript]);
  /* What the reader is looking at, and what the tab may remember: a message is the conversation's only once the
     server has answered it, and only until a read shows it in its place. */
  const turns = useMemo(
    () => [...serverTurns, ...view.shown].sort((left, right) => left.atMs - right.atMs), [serverTurns, view.shown],
  );
  const stopping = sessionState === 'stopping';
  const stopFeedback: ConversationStopFeedback | null = phase === 'issuing_interrupt' ? { kind: 'stopping' }
    : stop.feedback?.kind === 'requesting' && !working ? null : stop.feedback;
  const facts = useMemo<ConversationFacts | null>(() => trackId === undefined || provider === undefined ? null : {
    sourceCardId, cardId, trackId, trackTitle, provider, cardTitle: cardTitle ?? null, kind: scopeKind, isLead: scope?.isLead,
    state: scopeState, working, stalled: sessionState === 'unknown', fallbackUpdatedAt: scopeUpdatedAt ?? 0,
  }, [scope?.isLead, sourceCardId, cardId, cardTitle, provider, scopeKind, scopeState, scopeUpdatedAt, trackId, trackTitle, working, sessionState]);
  /** What the reader is looking at: every turn, echoes included. */
  const conversation = useMemo(
    () => facts === null ? null : describeConversation(facts, turns), [facts, turns],
  );
  /**
   * What the tab will still believe once the drawer is gone: confirmed turns only.
   * The registry is a memory kept for the life of the tab, so a fact that is not
   * yet a fact may never enter it.
   */
  const durableConversation = useMemo(
    () => facts === null ? null : describeConversation(facts, serverTurns),
    [serverTurns, facts],
  );
  useEffect(() => {
    if (durableConversation === null) return;
    /* Server rows enter the registry only under the Track that supplied them. */
    if (durableConversation.trackId !== rememberOn) return;
    /* What the reads showed; the registry adds this conversation's confirmed sends back from its outbox. */
    remember(durableConversation, serverEntries);
  }, [serverEntries, durableConversation, remember, rememberOn]);
  useEffect(() => {
    /* A `'rows'` route remembers every row it lists; `rememberOn` is compared against
         each row so another Track's row cannot write into this scope. */
    for (const row of serverRows) {
      if (row.trackId !== rememberOn) continue;
      /* The open row belongs to the effect above; writing the plain row over it here
               would undo it on every render. */
      if (row.id === conversation?.id || ownedCardIds?.includes(row.id)) continue;
      /* A row arrives with `turns` absent and a nullable `title` on the wire; carrying the
               remembered count keeps confirmed activity from being forgotten on refresh.
               The server owns its nullable title. */
      /* `updatedAt` never goes backwards: the listed row's time does not move when a
               turn is added, but the drawer knows and wrote it here. */
      const known = rememberedConversations.find((candidate) => candidate.id === row.id);
      remember(
        withRememberedConversation(row, known),
        rememberedTurnsOf(row.id),
      );
    }
  }, [conversation?.id, rememberedConversations, rememberedTurnsOf, remember, rememberOn, serverRows, ownedCardIds]);

  /* Server rows own names. The open projection adds local send feedback and activity. */
  const conversations = useMemo(() => conversation === null
    ? serverRows
    : serverRows.map((row) => row.id === conversation.id ? { ...conversation, archivedAt: row.archivedAt } : row), [conversation, serverRows]);
  const model = useMemo(() => worker !== null
    ? { model: worker.nextSettings.model ?? null, reasoning_effort: worker.nextSettings.reasoningEffort ?? null }
    : run.data === undefined ? FOLLOW_INSTALLATION_DEFAULT
      : { model: run.data.session.model ?? null, reasoning_effort: run.data.session.reasoningEffort ?? null }, [run.data, worker]);

  /* Held in the registry, so a strip remounted on the way back still sees its conversation's write out (#2068). */
  const deleteQueuedEntry = (entry: PendingQueueEntry) =>
    registry.holdQueueWrite<PlannerQueueWriteOutcome>(cardId, async () => {
      await mutations.removeQueuedSessionMessage(entry.entry_id);
      return { kind: 'done' };
    });
  const steerQueuedEntry = !recoveryHoldsSend && readonlyReason === null && phase === 'turn_running' && runReadError === null
    // The exact turn ID keeps steering safe while the run read refreshes.
    && steeringTarget !== null && run.data?.canSteer === true
    ? (entry: PendingQueueEntry) => registry.holdQueueWrite<PlannerQueueWriteOutcome>(cardId, async () => {
      const result = await mutations.steerQueuedSessionMessage(entry.entry_id, steeringTarget);
      if (result.delivery === 'queued' || result.delivery === 'rejected') return { kind: 'not_running' };
      if (result.delivery === 'unconfirmed') return { kind: 'unanswered' };
      return { kind: 'done' };
    }) : undefined;

  const updateTaskSettings = async (changes: Partial<Omit<WorkerSettings, 'version'>>, authorization?: string) => {
    if (!workerEditable || worker === null || settingsLeases.current.has(cardId)) {
      throw new Error('Settings are unavailable. Refresh the conversation and try again.');
    }
    settingsLeases.current.add(cardId);
    setSettingsPending(current => new Set([...current, cardId]));
    try {
      await mutations.updateWorkerSettings({ ...worker.nextSettings, ...changes, attemptId: worker.attemptId }, authorization);
    } finally {
      settingsLeases.current.delete(cardId);
      setSettingsPending(current => new Set([...current].filter(id => id !== cardId)));
    }
  };
  const taskApprovalsDenied = worker?.nextSettings.approvalMode === 'deny';
  const canAuthorize = worker?.approvalAuthorizationConfigured === true && globalThis.location?.protocol === 'https:';
  const sessionBlocksComposer = recoveryHoldsSend || readonlyReason !== null || sessionState === null || sessionState === 'stopping'
    || sessionState === 'compacting' || history.error !== null || runReadError !== null;
  const sendBlocked = sessionBlocksComposer || view.blocked;
  const composerBlocked = sessionBlocksComposer || view.sending || view.failed?.delivery === 'unknown';
  return {
    pendingApprovals: worker?.pendingApprovals ?? null,
    session: run.data?.session ?? null,
    sessionState, readonlyReason,
    canReopen: run.data?.canReopen ?? false,
    workerPaused: run.data?.workerPaused ?? false,
    lastTurn: run.data?.lastTurn ?? null,
    unknownDetail: run.data?.unknownDetail ?? null,
    providerCleanup,
    recovering: recoveryPending.has(cardId) || providerCleanup?.recovering === true || run.data?.unknownDetail?.recovering === true,
    dismissActionError: () => { setActionError(null); stop.clearFeedback(); },
    recover: (mode: 'reconnect' | 'reset_context') => {
      const detail = run.data?.unknownDetail;
      if (workerSession || readonlyReason !== null || recoveryLeases.current.has(cardId) || detail?.recovering === true || providerCleanup?.recovering === true
        || (mode === 'reconnect' ? !(detail?.canReconnect ?? providerCleanup?.canReconnect)
          : sessionState !== 'idle' && !(sessionState === 'unknown' && detail?.canResetContext))) return;
      const id = cardId;
      if (mode === 'reset_context') registry.invalidateSends(id);
      recoveryLeases.current.add(id);
      setRecoveryPending(current => new Set([...current, id]));
      setActionError(null);
      void mutations.recover(mode).then(session => {
        // Publish the acknowledged epoch before enabling a fresh composer send.
        registry.observeEpoch(id, session.epoch);
      }).catch((error: unknown) => {
        if (shownCardId.current === id) setActionError({ cardId: id,
          message: error instanceof ApiError && isSessionRecoveryRequired(error.failure) ? '暂时无法恢复会话，请稍后重试。'
            : error instanceof Error ? error.message : '恢复失败，请重试。' });
      }).finally(() => {
        recoveryLeases.current.delete(id);
        setRecoveryPending(current => new Set([...current].filter(value => value !== id)));
      });
    },
    conversations,
    turns: conversation === null ? NO_TURNS : presentation.transcript,
    canQueueWhileRunning,
    pending: pendingConversationIds(conversation, working, view.sending),
    working,
    canStop,
    stopping,
    stopFeedback,
    sending: view.sending,
    sendBlocked, composerBlocked,
    awaitingConversationUpdate: false,
    pendingQueue: presentation.queue,
    pendingSends: presentation.sending,
    queueDisplayAccounting,
    deleteQueuedEntry,
    queueWriteOut,
    steerQueuedEntry,
    historyReady: historyKnown,
    historyLoading: history.isFetching,
    hasEarlier: history.earlier.cursor !== null,
    earlierCursor: history.earlier.cursor,
    loadingEarlier: history.earlier.pending,
    historyError: history.error === null ? null : readErrorText(history.error, '无法加载对话记录。'),
    runError: runReadError === null ? null : readErrorText(runReadError, '无法同步对话状态。'),
    runLoading: run.isFetching,
    runReady: run.data !== undefined,
    actionError: actionError?.cardId === cardId ? actionError.message : stopFeedback?.kind === 'failed' ? stopFeedback.message : null,
    failedSend: view.failed,
    retrySend: outbox.retrySend,
    discardFailedSend: outbox.discardFailedSend,
    dismissFailedSend: outbox.dismissFailedSend,
    send: (id, content, attachments, fromComposer, key) => {
      if (sendBlocked) return null;
      if (worker !== null && (!workerEditable || attachments.length > 0)) return null;
      return outbox.send(id, content, attachments, fromComposer, key);
    },
    manualCompactSupported: sessionState === 'idle' && !recoveryHoldsSend && readonlyReason === null && !workerSession,
    contextUsage: run.data === undefined ? null : sessionContextUsage(run.data.session),
    uploadAttachment: mutations.uploadAttachment,
    compacting: compactPending.has(cardId) || phase === 'compacting',
    compact: () => {
      if (workerSession || sessionState !== 'idle' || recoveryHoldsSend || readonlyReason !== null || compactLeases.current.has(cardId) || cardId === '') return;
      const compactFor = cardId;
      compactLeases.current.add(compactFor);
      setCompactPending((current) => new Set([...current, compactFor]));
      setActionError(null);
      void mutations.compact().catch((error: unknown) => {
        if (shownCardId.current === compactFor) setActionError({ cardId: compactFor,
          message: refusalText(writeFailureOf(error), COMPACT_FAILURES, COMPACT_TEXT.refused) ?? COMPACT_TEXT.unknown });
      }).finally(() => {
        compactLeases.current.delete(compactFor);
        setCompactPending((current) => {
          const next = new Set(current);
          next.delete(compactFor);
          return next;
        });
      });
    },
    interrupt: stop.interrupt,
    retryHistory: () => { void (history.earlier.failed ? Promise.resolve(history.loadEarlier()) : history.refetch()).catch(() => undefined); },
    retryRun: () => { void run.refetch().catch(() => undefined); },
    loadEarlier,
    /* Before the first answer the conversation is following the default, which is
           what a card with no selection really does. */
    model,
    modelCatalog: worker === null ? modelCatalog.data ?? null : workerCatalog,
    modelDefaultAvailable: worker === null || worker.nextSettings.model == null || worker.providerConfiguration?.defaultModel != null,
    modelChangeDisabled: run.data === undefined || recoveryHoldsSend || (worker !== null && (!workerEditable || workerCatalog === null || settingsPending.has(cardId))),
    setModel: (selection) => {
      if (recoveryHoldsSend) return;
      const setFor = cardId;
      setActionError(null);
      /* Dropped when it settles while another conversation is shown: kept, it would show for one commit on the way back (#2068). */
      const fail = (message: string) => { if (shownCardId.current === setFor) setActionError({ cardId: setFor, message }); };
      const save = worker === null ? mutations.setModel(selection)
        : updateTaskSettings({ model: selection.model, reasoningEffort: selection.reasoning_effort });
      void save.catch((error: unknown) => {
        fail(worker !== null && error instanceof Error ? error.message
          : refusalText(writeFailureOf(error), PLANNER_MODEL_FAILURES, MODEL_CHANGE_TEXT.refused) ?? MODEL_CHANGE_TEXT.unknown);
      });
    },
    permissionMode: worker?.nextSettings.approvalMode ?? run.data?.session.permissionMode ?? null,
    permissionOptions: worker === null ? run.data?.permissionModes ?? null : TASK_APPROVAL_OPTIONS,
    permissionChangeDisabled: run.data === undefined || (worker !== null && (!workerEditable || settingsPending.has(cardId))),
    permissionAuthorizationMode: taskApprovalsDenied && canAuthorize ? 'ask' : null,
    unavailablePermissionMode: taskApprovalsDenied && !canAuthorize ? 'ask' : null,
    setPermissionMode: async (mode, authorization) => {
      const setFor = cardId;
      setActionError(null);
      try {
        if (worker === null) await mutations.setPermissionMode(mode);
        else if (mode === 'ask' || mode === 'deny') {
          await registry.holdPermissionWrite(cardId, updateTaskSettings({ approvalMode: mode }, authorization));
        }
      } catch (error: unknown) {
        if (authorization !== undefined) throw error;
        if (shownCardId.current !== setFor) return;
        setActionError({ cardId: setFor, message: worker !== null && error instanceof Error ? error.message
          : refusalText(writeFailureOf(error), PLANNER_PERMISSION_MODE_FAILURES,
            PERMISSION_MODE_CHANGE_TEXT.refused) ?? PERMISSION_MODE_CHANGE_TEXT.unknown });
      }
    },
  };
}
