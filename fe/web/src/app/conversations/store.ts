import { sessionContextUsage } from '../../../../core/domain/session-context-usage.ts';
import { NotSentError } from '../../../../core/domain/failure-class.ts';
import { withTaskMessageFailures, workerModelCatalog } from '../../../../core/domain/worker-session.ts';
import { pendingApprovalQuestions } from '../../../../core/domain/ask.ts';
import type { PermissionModeOptionDto, WorkerSettings } from '../../../../core/api/perga-v1.ts';
import { pendingQueueWithCurrentHumanVersions, pendingQueueOwnsHumanEntry } from '../../../../core/domain/conversation-queue-controls.ts';
import { useCurrentUser } from '../auth/current-user.tsx';
import { conversationRunPhase, conversationRunningTurn, conversationBlockedReason, conversationPendingQueue, buildConversationTranscript, nativeDisplayRows, removedHumanIds } from '../../../../core/domain/conversation-display.ts';
import { useEffect, useMemo, useRef } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { FOLLOW_INSTALLATION_DEFAULT, isOptimisticConversationTurn, isConversationMessage, isUserConversationTurn, kernelQueuesInput, COMPACT_FAILURES, COMPACT_TEXT, MODEL_CHANGE_TEXT, PLANNER_MODEL_FAILURES, transcriptRowToTurnOutcome, type PlannerQueueWriteOutcome, type PendingQueueEntry } from '../../../../core/domain/conversation.ts';
import { describeConversation, pendingConversationIds, withRememberedConversation, type ConversationFacts } from '../../../../core/domain/conversation-summary.ts';
import { useTranscriptReads, useTranscriptHistory } from './history.ts';
import { rememberQueuePlacements, presentPendingMessages, queueTombstoneHides as tombstoneHides } from '../../../../core/domain/conversation-outbox.ts';
import { refusalText, writeFailureOf } from '../../../../core/domain/failure-class.ts';
import { PERMISSION_MODE_CHANGE_TEXT, PLANNER_PERMISSION_MODE_FAILURES } from '../../../../core/domain/planner-permission-mode.ts';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import type { ConversationStopFeedback } from '../../../../core/domain/conversation-stop.ts';
import { anchorRunningTurn, type RunningTurnAnchor } from '../../../../core/domain/conversation-meta.ts';
import { conversationHistoryQueryOptions, modelCatalogQueryOptions, plannerRunQueryOptions, usePlannerMutations } from '../providers/queries.ts';
import { useState } from '../../ui/state/public.ts';
import { useConversationCommands, useConversationSendOps, useConversationQueueWriteOut, useConversationRememberedTurns, useRememberedConversations } from './public.tsx';
import { NO_TURNS } from './registry-owner.ts';
import { useConversationStop } from './stop.ts';
import { useLiveReplies } from './live-replies.ts';
import { useConversationOutbox, useRunReads } from './outbox.ts';
import type { ConversationRouteIntent, ConversationStore, PlannerConversationScope } from './contracts.ts';

/** Tombstone key: entry ids are unique per card, not globally. */
function forgottenKey(card: string, entryId: string): string {
  return `${card}\u0000${entryId}`;
}

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
  /* Numbered as the transcript's are: a send answered after an unknown attempt waits for reads started after it. */
  const runReads = useRunReads(registry.nextRead);
  const runQuery = useMemo(
    () => runReads.track(plannerRunQueryOptions(transport, cardId, unauthorized)), [runReads, transport, cardId, unauthorized],
  );
  const run = useQuery({ ...runQuery, enabled: scope !== null, refetchInterval: query =>
    query.state.data?.worker?.phase === 'running' || query.state.data?.worker?.phase === 'interrupting' ? 1000 : false });
  const worker = run.data?.worker ?? null;
  const workerTurn = worker === null ? undefined : (run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : undefined);
  const openWorkerAsks = worker?.pendingApprovals;
  const openAsks = useMemo(() => openWorkerAsks === undefined ? null : pendingApprovalQuestions(openWorkerAsks), [openWorkerAsks]);
  const workerEditable = worker?.phase === 'running' || worker?.phase === 'paused';
  const workerCatalog = useMemo(() => worker === null ? null : workerModelCatalog(worker), [worker]);
  /* The catalog rides alongside the run query: the trigger has to render the chosen
       model's name, and `planner-run` gives only its slug. */
  const modelCatalog = useQuery({
    ...modelCatalogQueryOptions(transport, { kind: 'card', cardId, provider: provider ?? 'codex' }, unauthorized), enabled: scope !== null && worker === null,
  });
  const displayItems = history.loadedRows;
  const items = useMemo(() => nativeDisplayRows(displayItems), [displayItems]);
  const removedMessages = useMemo(() => removedHumanIds(displayItems), [displayItems]);
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
  const landedRun = runReads.startOf(run.data);
  const servedQueue = useMemo(() => run.data === undefined ? EMPTY_PENDING_QUEUE : conversationPendingQueue(run.data), [run.data]);
  const queueOps = useConversationSendOps(scope === null ? null : cardId);
  const phase = run.data === undefined ? null : conversationRunPhase(run.data);
  const executionHeld = run.data?.session.status === 'failed' || run.data?.session.status === 'exited'
    || run.data?.session.status === 'superseded';
  /* Anchored on the response's arrival (`dataUpdatedAt`); the fold keeps the earlier anchor for one turn. */
  const [runningAnchor, setRunningAnchor] = useState<RunningTurnAnchor | null>(null);
  const nextRunningAnchor = anchorRunningTurn(runningAnchor, run.data === undefined ? null : conversationRunningTurn(run.data, run.dataUpdatedAt), run.dataUpdatedAt);
  if (nextRunningAnchor !== runningAnchor) setRunningAnchor(nextRunningAnchor);
  /* The run page owns pending input identity. Replace its persisted rows with
     the display projection below, keeping one bubble while the queue drains. */
  /* Tombstones for entries this client has had a `done` DELETE or steer for, until the
       cached page catches up. Keyed by card AND entry (entry ids are unique per card);
       the value is the rev written against, so an entry the kernel puts back one rev
       up is shown again (`tombstoneHides`). */
  const [forgotten, setForgotten] = useState<ReadonlyMap<string, number>>(() => new Map());
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
  const pendingQueue = useMemo(
    () => forgotten.size === 0 ? currentQueue : currentQueue.filter(entry =>
      !tombstoneHides(forgotten.get(forgottenKey(cardId, entry.entry_id)), entry.rev)),
    [currentQueue, forgotten, cardId],
  );
  useEffect(() => {
    if (forgotten.size === 0) return;
    /* Retired when the owning page says the entry is gone or lists it at a higher rev;
           keys for other cards are left alone. */
    const servedRev = new Map(servedQueue.map((entry) => [forgottenKey(cardId, entry.entry_id), entry.rev]));
    const mine = (key: string) => key.startsWith(`${cardId}\u0000`);
    const retired = (key: string, wroteAt: number): boolean => {
      const rev = servedRev.get(key);
      return rev === undefined || !tombstoneHides(wroteAt, rev);
    };
    /* Only when there is something to drop — an unconditional `setForgotten`
       here re-renders forever. */
    if (![...forgotten].some(([key, wroteAt]) => mine(key) && retired(key, wroteAt))) return;
    setForgotten((current) => new Map(
      [...current].filter(([key, wroteAt]) => !mine(key) || !retired(key, wroteAt)),
    ));
  }, [servedQueue, forgotten, cardId]);
  const forgetQueuedEntry = (entry: PendingQueueEntry): void => {
    setForgotten((current) => new Map([...current, [forgottenKey(cardId, entry.entry_id), entry.rev]]));
  };
  const pendingQueueIds = useMemo(
    () => new Map(pendingQueue.map((entry) => [entry.entry_id, entry.rev])), [pendingQueue],
  );
  const mutations = usePlannerMutations(transport, cardId, scope === null ? null : scope.id, unauthorized, registry);
  /** What went wrong with an action of the card it names; another card's is never this one's. */
  const [compactPending, setCompactPending] = useState<ReadonlySet<string>>(() => new Set());
  const [settingsPending, setSettingsPending] = useState<ReadonlySet<string>>(() => new Set());
  const settingsLeases = useRef(new Set<string>());
  const compactLeases = useRef(new Set<string>());
  const resumeLeases = useRef(new Set<string>());
  const [resumePending, setResumePending] = useState<ReadonlySet<string>>(() => new Set());
  const providerResume = run.data?.resume ?? null;
  const resumeState = resumePending.has(cardId) ? 'resuming' : providerResume?.state;
  const recoveryHoldsSend = resumeState === 'resuming' || resumeState === 'paused';
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
    transport, unauthorized, cardId, enabled: scope !== null && run.data !== undefined && !executionHeld, phase,
    runningTurnId: run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : null, nextRead: registry.nextRead,
    transcriptKey: transcriptQuery.queryKey, transcriptReads, items, worker,
  });
  const working = !executionHeld && (phase === 'issuing_turn' || phase === 'compacting' || phase === 'turn_running');
  const stopTurnId = run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : null;
  const canStop = resumeState !== 'resuming' && working && stopTurnId !== null && phase === 'turn_running';
  const stop = useConversationStop({
    cardId, canStop,
    responseEnded: run.error === null && run.data !== undefined && (phase === 'idle' || phase === 'turn_completed'),
    historyKnown: historyKnown,
    newestRowId: items.reduce((latest, row) => Math.max(latest, row.id), 0),
    completedRowId: items.reduce<number | null>((latest, row) => transcriptRowToTurnOutcome(row) === null
      ? latest : Math.max(latest ?? 0, row.id), null),
    requestStop: () => {
      if (worker !== null && workerTurn !== undefined)
        return mutations.interrupt({ attemptId: worker.attemptId, turnId: workerTurn });
      return stopTurnId == null ? Promise.reject(new NotSentError()) : mutations.interrupt({ turnId: stopTurnId });
    },
  });
  const landedTranscript = transcriptReads.startOf(history.data);
  /* A refetch keeping `run.data` restamps it; this re-renders only because `run.dataUpdatedAt` is read above (#2068). */
  const landed = useMemo(() => ({ transcript: landedTranscript, run: landedRun }), [landedTranscript, landedRun]);
  const canQueueWhileRunning = phase === 'turn_running' && working && run.error === null;
  const target = worker === null ? undefined : { attemptId: worker.attemptId, queue: worker.phase === 'running' && working };
  const steeringTarget = run.data?.activeTurn?.status === 'running' ? run.data.activeTurn.id : null;
  const outbox = useConversationOutbox({
    removedMessages, viewerId:currentUser?.userId ?? null,
    cardId, transport, send: mutations.send, serverEntries, serverTurns, liveReplies, queuedEntries: pendingQueue,
    stalled: false, landed, target, queuesInput: kernelQueuesInput(phase), queueIfBusy: canQueueWhileRunning, highWater: displayItems.reduce((maximum,item) => Math.max(maximum,item.cursor.position),0),
    pressed: () => { setActionError(null); stop.clearFeedback(); },
  });
  const { view, accountQueueDisplay } = outbox;
  const [placementMemory, setPlacementMemory] = useState(() => ({ cardId, ops: queueOps, queue: pendingQueue,
    placements: rememberQueuePlacements(new Map(), queueOps, pendingQueue) }));
  let placements = placementMemory.placements;
  if (placementMemory.cardId !== cardId || placementMemory.ops !== queueOps || placementMemory.queue !== pendingQueue) {
    placements = rememberQueuePlacements(placementMemory.cardId === cardId ? placementMemory.placements : new Map(), queueOps, pendingQueue);
    setPlacementMemory({ cardId, ops: queueOps, queue: pendingQueue, placements });
  }
  const presentation = useMemo(() => presentPendingMessages(view.transcript, pendingQueue, view.shown,
    placements, view.failed, false, queueOps),
  [view.transcript, pendingQueue, view.shown, placements, view.failed, queueOps]);
  const queueDisplayAccounting = useMemo(() => accountQueueDisplay(presentation.transcript, []),
    [accountQueueDisplay, presentation.transcript]);
  /* What the reader is looking at, and what the tab may remember: a message is the conversation's only once the
     server has answered it, and only until a read shows it in its place. */
  const turns = useMemo(
    () => [...serverTurns, ...view.shown].sort((left, right) => left.atMs - right.atMs), [serverTurns, view.shown],
  );
  const confirmedTurns = useMemo(
    () => [...serverTurns, ...view.confirmed].sort((left, right) => left.atMs - right.atMs), [serverTurns, view.confirmed],
  );
  const stopping = !executionHeld && (phase === 'issuing_interrupt' || (working && stop.pending));
  const stopFeedback: ConversationStopFeedback | null = phase === 'issuing_interrupt' ? { kind: 'stopping' }
    : stop.feedback?.kind === 'requesting' && !working ? null : stop.feedback;
  const facts = useMemo<ConversationFacts | null>(() => trackId === undefined || provider === undefined ? null : {
    sourceCardId, cardId, trackId, trackTitle, provider, cardTitle: cardTitle ?? null, kind: scopeKind, isLead: scope?.isLead,
    state: scopeState, working, stalled: executionHeld, fallbackUpdatedAt: scopeUpdatedAt ?? 0,
  }, [scope?.isLead, sourceCardId, cardId, cardTitle, provider, scopeKind, scopeState, scopeUpdatedAt, trackId, trackTitle, working, executionHeld]);
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
    () => facts === null ? null : describeConversation(facts, confirmedTurns),
    [confirmedTurns, facts],
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
      outbox.forgetQueuedEntry({ card_id: cardId, entry_id: entry.entry_id, rev: entry.rev, text: null });
      forgetQueuedEntry(entry);
      return { kind: 'done' };
    });
  const steerQueuedEntry = !recoveryHoldsSend && phase === 'turn_running' && !executionHeld && run.error === null
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
  const statusNotice = worker?.lastError != null ? `The last response failed: ${worker.lastError}`
    : worker?.phase === 'interrupt_unknown' ? 'The stop could not be confirmed.'
    : worker?.phase === 'paused' ? 'Paused. Send a message to continue.'
    : null;
  return {
    openAsks,
    answerAsk: (ask, answers) => trackId === undefined ? Promise.reject(new NotSentError())
      : mutations.answerWorkerAsk(trackId, ask.askId, answers, ask.questions, ask.executionId),
    statusNotice,
    conversations,
    turns: conversation === null ? NO_TURNS : presentation.transcript,
    canQueueWhileRunning,
    pending: pendingConversationIds(conversation, working, view.sending),
    working,
    canStop,
    executionHeld,
    stopping,
    stopFeedback,
    sending: view.sending,
    sendBlocked: recoveryHoldsSend || view.blocked || run.data?.unknownInput != null ||
      (working && !canQueueWhileRunning) || (worker !== null && !workerEditable),
    awaitingConversationUpdate: view.awaitingReconciliation && history.error === null && run.error === null,
    pendingQueue: presentation.queue,
    pendingSends: presentation.sending,
    queueDisplayAccounting,
    editQueuedEntry: (entry, text) => registry.holdQueueWrite(cardId, () =>
      mutations.editCanonicalQueued(entry, text, cardId, saved => { outbox.acknowledgeQueuedEdit(entry, saved); })),
    deleteQueuedEntry,
    queueWriteOut,
    steerQueuedEntry,
    historyReady: historyKnown,
    historyLoading: history.isFetching,
    hasEarlier: history.earlier.cursor !== null,
    earlierCursor: history.earlier.cursor,
    loadingEarlier: history.earlier.pending,
    historyError: history.error === null ? null : readErrorText(history.error, 'The conversation history could not be loaded.'),
    runError: run.error === null ? null : readErrorText(run.error, 'The conversation’s status could not be loaded.'),
    runLoading: run.isFetching,
    runReady: run.data !== undefined,
    actionError: actionError?.cardId === cardId ? actionError.message : null,
    failedSend: view.failed,
    retrySend: outbox.retrySend,
    discardFailedSend: outbox.discardFailedSend,
    dismissFailedSend: outbox.dismissFailedSend,
    send: (id, content, attachments, fromComposer, replaces, key) => {
      if (worker !== null && (!workerEditable || attachments.length > 0 || replaces !== null)) return null;
      return outbox.send(id, content, attachments, fromComposer, replaces, key);
    },
    manualCompactSupported: !recoveryHoldsSend && run.data !== undefined && worker === null,
    contextUsage: run.data === undefined ? null : sessionContextUsage(run.data.session),
    runningAnchor: nextRunningAnchor,
    uploadAttachment: mutations.uploadAttachment,
    compacting: compactPending.has(cardId) || phase === 'compacting',
    compact: () => {
      if (recoveryHoldsSend || compactLeases.current.has(cardId) || cardId === '') return;
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
    blockedReason: run.data === undefined ? null : conversationBlockedReason(run.data),
    providerResume: providerResume === null || resumeState === undefined || run.error !== null ? null : {
      state: resumeState,
      start: () => {
        if (resumeLeases.current.has(cardId) || resumeState === 'resuming') return;
        const id = cardId;
        resumeLeases.current.add(id);
        setResumePending(current => new Set([...current, id]));
        setActionError(null);
        void mutations.resumeProviderSession(providerResume.turnId).catch((error: unknown) => {
          if (shownCardId.current === id)
            setActionError({ cardId: id, message: error instanceof Error ? error.message : 'Could not resume this provider session.' });
        }).finally(() => {
          resumeLeases.current.delete(id);
          setResumePending(current => {
            const next = new Set(current);
            next.delete(id);
            return next;
          });
        });
      },
    },
    abandonUnknownInput: run.data?.unknownInput == null || (recoveryHoldsSend && (resumeState === 'resuming' || providerResume?.canStartFresh !== true)) ? null : () => {
      const input = run.data?.unknownInput;
      if (input == null || input.sessionId !== cardId) return;
      setActionError(null);
      void mutations.abandonSessionInput(input.inputId).catch(() => {
        if (shownCardId.current === cardId) setActionError({ cardId, message: 'Could not start a fresh provider session. Try again.' });
      });
    },
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
