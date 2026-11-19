import { ApiError, NotSentError } from '../../../../core/domain/failure-class.ts';
import type { WorkerMessageBody, WorkerMessageMode } from '../../../../core/domain/worker-session.ts';
import { pendingQueueWithCurrentHumanVersions, pendingQueueOwnsHumanEntry } from '../../../../core/domain/conversation-queue-controls.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { useCurrentUser } from '../auth/current-user.tsx';
import { buildConversationTranscript, currentConversationOutcome, nativeDisplayRows, orderedPendingInputs, removedHumanIds } from '../../../../core/domain/conversation-display.ts';
import type { ConversationPendingInput } from '../../../../core/api/generated/wire.ts';
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
import { useConversationCommands, useConversationSendOps, useConversationQueueWriteOut, useConversationRememberedTurns, useRememberedConversations, useWorkerSendIntent } from './public.tsx';
import type { WorkerSendIntent } from './registry-model.ts';
import { NO_TURNS } from './registry-owner.ts';
import { useConversationStop } from './stop.ts';
import { useConversationRestart } from './restart.ts';
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
const EMPTY_PENDING_INPUTS: readonly ConversationPendingInput[] = Object.freeze([]);

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
  const workerTurn = worker === null ? undefined : run.data?.running_turn?.turn_id;
  const workerSendIntent = useWorkerSendIntent(worker === null ? null : cardId);
  const [deliveryChoice, setDeliveryChoice] = useState<Readonly<{ cardId: string; turnId: string; mode: WorkerMessageMode }> | null>(null);
  const workerMode = worker?.nativeSteer !== true ? 'queue'
    : deliveryChoice?.cardId === cardId && deliveryChoice.turnId === workerTurn ? deliveryChoice.mode : 'steer';
  const pendingInputs = useMemo(() => run.data === undefined ? EMPTY_PENDING_INPUTS : orderedPendingInputs(run.data.pending_inputs), [run.data]);
  /* The catalog rides alongside the run query: the trigger has to render the chosen
       model's name, and `planner-run` gives only its slug. */
  const modelCatalog = useQuery({
    ...modelCatalogQueryOptions(transport, { kind: 'card', cardId, provider: provider ?? 'codex' }, unauthorized), enabled: scope !== null,
  });
  const historyReplacementSupported = run.data?.history_replacement_supported === true;
  const displayItems = history.loadedRows;
  const items = useMemo(() => nativeDisplayRows(displayItems), [displayItems]);
  const removedMessages = useMemo(() => removedHumanIds(displayItems), [displayItems]);
  const rememberedSource = useConversationRememberedTurns(history.data === undefined && scope !== null ? cardId : null);
  const rememberedEntries = history.data === undefined ? rememberedSource : null;
  const queueWriteOut = useConversationQueueWriteOut(scope === null ? null : cardId);
  const serverTurns = useMemo(
    () => (rememberedEntries ?? buildConversationTranscript(displayItems, currentUser?.userId ?? null)).filter(isConversationMessage),
    [displayItems, currentUser?.userId, rememberedEntries],
  );
  const landedRun = runReads.startOf(run.data);
  const servedQueue = run.data?.pending ?? EMPTY_PENDING_QUEUE;
  const queueOps = useConversationSendOps(scope === null ? null : cardId);
  const phase = run.data?.phase ?? null;
  const pergaInputProtocol = run.data?.unknown_session_input !== undefined;
  const recovery = run.data?.recovery ?? null;
  const stalled = recovery !== null;
  const canonical = run.data?.input_contract === 'canonical';
  const inputStalled = stalled && !canonical;
  const executionHeld = run.data?.runtime_state === 'failed' || run.data?.runtime_state === 'exited'
    || run.data?.runtime_state === 'superseded';
  /* Anchored on the response's arrival (`dataUpdatedAt`); the fold keeps the earlier anchor for one turn. */
  const [runningAnchor, setRunningAnchor] = useState<RunningTurnAnchor | null>(null);
  const nextRunningAnchor = anchorRunningTurn(runningAnchor, run.data?.running_turn ?? null, run.dataUpdatedAt);
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
  const [handoff, setHandoff] = useState(() => ({ cardId, source: servedQueue, saved: savedMessageIds, ids: run.data?.queue_handoff_ids, rows: servedQueue }));
  let handoffRows = handoff.rows;
  if (handoff.cardId !== cardId || handoff.source !== servedQueue || handoff.saved !== savedMessageIds || handoff.ids !== run.data?.queue_handoff_ids) {
    const currentIds = new Set(servedQueue.map(entry => entry.entry_id));
    const handoffIds = new Set(run.data?.queue_handoff_ids);
    const waitingForHistory = handoff.cardId === cardId ? handoff.rows.filter(entry =>
      (handoffIds.has(entry.entry_id) || entry.state === 'processing') &&
      !currentIds.has(entry.entry_id) && !savedMessageIds.has(entry.entry_id))
      .map((entry): PendingQueueEntry => handoffIds.has(entry.entry_id) ? { ...entry, state: 'processing' } : entry) : [];
    handoffRows = [...waitingForHistory, ...servedQueue];
    setHandoff({ cardId, source: servedQueue, saved: savedMessageIds, ids: run.data?.queue_handoff_ids, rows: handoffRows });
  }
  const currentQueue = useMemo(() => {
    const entries = pergaInputProtocol ? handoffRows.filter(entry => entry.state === 'unconfirmed' ||
      !savedMessageIds.has(entry.entry_id)) : servedQueue;
    return canonical ? pendingQueueWithCurrentHumanVersions(entries, serverTurns) : entries;
  }, [canonical, handoffRows, pergaInputProtocol, savedMessageIds, servedQueue, serverTurns]);
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
  const pendingQueueOverflow = run.data?.pending_overflow ?? 0;
  const pendingQueueIds = useMemo(
    () => new Map(pendingQueue.map((entry) => [entry.entry_id, entry.rev])), [pendingQueue],
  );
  const mutations = usePlannerMutations(transport, cardId, scope === null ? null : scope.id, unauthorized);
  /** What went wrong with an action of the card it names; another card's is never this one's. */
  const [compactPending, setCompactPending] = useState<ReadonlySet<string>>(() => new Set());
  const compactLeases = useRef(new Set<string>());
  const resumeLeases = useRef(new Set<string>());
  const [resumePending, setResumePending] = useState<ReadonlySet<string>>(() => new Set());
  const providerResume = run.data?.provider_resume ?? null;
  const resumeState = resumePending.has(cardId) ? 'resuming' : providerResume?.state;
  const recoveryHoldsSend = resumeState === 'resuming' || resumeState === 'paused';
  const [actionError, setActionError] = useState<Readonly<{ cardId: string; message: string }> | null>(null);
  /** The card shown now; a model write's answer is that of the card it was made in. */
  const shownCardId = useRef(cardId);
  shownCardId.current = cardId;
  const serverEntries = useMemo(() => {
    if (rememberedEntries !== null) return rememberedEntries.filter(entry => !isOptimisticConversationTurn(entry));
    return buildConversationTranscript(displayItems, currentUser?.userId ?? null)
      .filter(entry => !pendingQueueOwnsHumanEntry(entry,pendingQueueIds))
      .filter(entry => !isUserConversationTurn(entry) || entry.queueEntryId == null || !pendingQueueIds.has(entry.queueEntryId)
        || (entry.inputSource?.entryRev != null && entry.inputSource.entryRev !== pendingQueueIds.get(entry.queueEntryId)));
  }, [displayItems, currentUser?.userId, pendingQueueIds, rememberedEntries]);
  useEffect(() => { setActionError(null); }, [cardId]);
  /* The running turn's streamed replies: drawn at the tail, never remembered, never counted. */
  const liveReplies = useLiveReplies({
    canonical:run.data?.input_contract === 'canonical',
    transport, unauthorized, cardId, enabled: scope !== null && run.data !== undefined && !executionHeld, phase,
    runningTurnId: run.data?.native_turn_id ?? run.data?.running_turn?.turn_id ?? null, nextRead: registry.nextRead,
    transcriptKey: transcriptQuery.queryKey, transcriptReads, items, worker,
  });
  const working = !executionHeld && !stalled && (phase === 'issuing_turn' || phase === 'compacting' || phase === 'turn_running');
  const canStop = resumeState !== 'resuming' && working && (!pergaInputProtocol || phase === 'turn_running');
  const stop = useConversationStop({
    cardId, canStop,
    responseEnded: run.error === null && (canonical && !pergaInputProtocol
      ? run.data?.terminal_outcome != null : phase === 'idle' || phase === 'turn_completed'),
    historyKnown: historyKnown,
    newestRowId: items.reduce((latest, row) => Math.max(latest, row.id), 0),
    completedRowId: items.reduce<number | null>((latest, row) => transcriptRowToTurnOutcome(row) === null
      ? latest : Math.max(latest ?? 0, row.id), null),
    requestStop: () => mutations.interrupt(worker === null || workerTurn === undefined ? undefined
      : { attemptId: worker.attemptId, turnId: workerTurn }),
  });
  const restart = useConversationRestart({ cardId, enabled: run.data?.input_contract === 'historical' || recovery?.action === 'abandon_session' || recovery?.action === 'recover_session', recovery, restart: () => mutations.restart(recovery) });
  const landedTranscript = transcriptReads.startOf(history.data);
  /* A refetch keeping `run.data` restamps it; this re-renders only because `run.dataUpdatedAt` is read above (#2068). */
  const landed = useMemo(() => ({ transcript: landedTranscript, run: landedRun }), [landedTranscript, landedRun]);
  const canQueueWhileRunning = pergaInputProtocol && phase === 'turn_running' && working && run.error === null;
  const steeringTarget = run.data?.native_turn_id ?? null;
  const outbox = useConversationOutbox({
    removedMessages, viewerId:currentUser?.userId ?? null,
    cardId, transport, send: mutations.send, serverEntries, serverTurns, liveReplies, queuedEntries: pendingQueue,
    stalled: inputStalled, landed, queuesInput: kernelQueuesInput(phase), queueIfBusy: canQueueWhileRunning, highWater: displayItems.reduce((maximum,item) => Math.max(maximum,item.cursor.position),0),
    pressed: () => { setActionError(null); stop.clearFeedback(); restart.clearError(); },
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
    placements, view.failed, stalled, queueOps),
  [view.transcript, pendingQueue, view.shown, placements, view.failed, stalled, queueOps]);
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
  const stopping = !stalled && !executionHeld && (phase === 'issuing_interrupt' || (working && stop.pending));
  const stopFeedback: ConversationStopFeedback | null = stalled ? null
    : phase === 'issuing_interrupt' ? { kind: 'stopping' }
    : stop.feedback?.kind === 'requesting' && !working ? null : stop.feedback;
  const facts = useMemo<ConversationFacts | null>(() => trackId === undefined || provider === undefined ? null : {
    sourceCardId, cardId, trackId, trackTitle, provider, cardTitle: cardTitle ?? null, kind: scopeKind, isLead: scope?.isLead,
    state: scopeState, working, stalled:stalled || executionHeld, fallbackUpdatedAt: scopeUpdatedAt ?? 0,
  }, [scope?.isLead, sourceCardId, cardId, cardTitle, provider, scopeKind, scopeState, scopeUpdatedAt, trackId, trackTitle, working, stalled,executionHeld]);
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
  const model = useMemo(() => run.data === undefined ? FOLLOW_INSTALLATION_DEFAULT
    : { model: run.data.model, reasoning_effort: run.data.reasoning_effort }, [run.data]);

  const canonicalCarrier = (): string => {
    const carrier = run.data?.worker_session_id;
    if (!canonical || carrier == null) throw new Error('The current conversation carrier is not confirmed.');
    return carrier;
  };
  const removeQueued = (entry: PendingQueueEntry, answered: (saved: import('../../../../core/domain/conversation.ts').PlannerInputMutation) => void) => canonical
    ? mutations.withdrawCanonicalQueued(entry.entry_id,entry.rev,canonicalCarrier(),answered)
    : mutations.deleteQueued(entry.entry_id,entry.rev,answered);

  const observeQueueOutcome = <T extends PlannerQueueWriteOutcome>(outcome: T): T => {
    return outcome;
  };

  /* Held in the registry, so a strip remounted on the way back still sees its conversation's write out (#2068). */
  const deleteQueuedEntry = (entry: PendingQueueEntry) =>
    registry.holdQueueWrite<PlannerQueueWriteOutcome>(cardId, async () => {
      if (pergaInputProtocol) {
        await mutations.removeQueuedSessionMessage(entry.entry_id);
        outbox.forgetQueuedEntry({ card_id: cardId, entry_id: entry.entry_id, rev: entry.rev, text: null });
        forgetQueuedEntry(entry);
        return { kind: 'done' };
      }
      return removeQueued(entry, removed => {
        // Import the real ACK before the mutation owner starts either reconciliation read.
        outbox.forgetQueuedEntry(removed);
        forgetQueuedEntry(entry);
      }).then(observeQueueOutcome);
    });
  /* A persisted handoff retires the queue copy; canonical Unknown remains a held submission. */
  const steerQueuedEntry = !recoveryHoldsSend && phase === 'turn_running' && !executionHeld && !stalled && run.error === null
    && (!canonical || steeringTarget !== null)
    && (!pergaInputProtocol || (!run.isFetching && run.data?.can_steer_queued === true))
    ? (entry: PendingQueueEntry) => registry.holdQueueWrite<PlannerQueueWriteOutcome>(cardId,
      async () => {
        if (pergaInputProtocol) {
          const result = await mutations.steerQueuedSessionMessage(entry.entry_id, steeringTarget!);
          if (result.delivery === 'queued' || result.delivery === 'rejected') return { kind: 'not_running' };
          if (result.delivery === 'unconfirmed') return { kind: 'unanswered' };
          return { kind: 'done' };
        }
        return (canonical
          ? mutations.steerCanonicalQueued(entry, canonicalCarrier(), steeringTarget!, () => { forgetQueuedEntry(entry); })
          : mutations.steerQueued(entry.entry_id, entry.rev, () => { forgetQueuedEntry(entry); })).then(observeQueueOutcome);
      })
    : undefined;

  const resolveInput = async (decision: 'keep' | 'retry', submission: import('../../../../core/api/generated/wire.ts').ConversationSubmissionDelivery) => {
    const attempt = submission.attempt;
    if (attempt === null || attempt.actions[decision].kind !== 'available') return;
    const intent = registry.beginResolution(cardId, decision, { carrier_id:submission.carrier_id,
      submission_id:submission.submission_id, attempt_id:attempt.attempt_id, revision:attempt.revision }, mintIdempotencyKey());
    if (intent === null) return;
    try {
      await mutations[intent.decision](intent.args, intent.key);
      registry.finishResolution(cardId, intent.key, 'done');
    } catch (error) {
      const terminal = error instanceof ApiError && (error.failure.kind === 'unauthorized' || (error.failure.kind === 'http' && [400, 403, 404, 409, 422].includes(error.failure.status)));
      registry.finishResolution(cardId, intent.key, terminal ? 'done' : 'unknown');
      throw error;
    }
  };
  const deliverWorkerMessage = (intent: WorkerSendIntent): Promise<void> => {
    setActionError(null);
    const finish = (unconfirmed: boolean) => registry.editWorkerSend(cardId, current => current?.key !== intent.key
      ? current : unconfirmed ? { ...intent, state: 'unconfirmed' } : undefined);
    const refill = (message: string) => {
      registry.refillComposer(cardId, { text: intent.body.content, attachments: [] });
      if (shownCardId.current === cardId) setActionError({ cardId, message });
    };
    return mutations.sendWorker(intent.key, intent.body).then(receipt => {
      finish(receipt.delivery === 'unknown' || receipt.delivery === 'dispatching');
      if (receipt.delivery === 'rejected' || receipt.delivery === 'interrupted') {
        refill(receipt.delivery === 'rejected' ? 'The provider did not accept the steer.' : 'The Worker message was interrupted.');
      }
    }, (error: unknown) => {
      // This response is emitted after the Kernel checks the receipt and finds no accepted message for the original turn.
      const staleWorkerTurn = error instanceof ApiError && error.failure.kind === 'http' && error.failure.code === 'worker_turn_changed';
      const refused = error instanceof NotSentError || (error instanceof ApiError && (error.failure.kind === 'unauthorized' ||
        (error.failure.kind === 'http' && [400, 403, 404, 409, 422].includes(error.failure.status))));
      if (staleWorkerTurn || (refused && intent.state === 'sending')) {
        finish(false);
        refill(error.message);
      } else {
        finish(true);
        if (refused && shownCardId.current === cardId)
          setActionError({ cardId, message: error.message });
      }
      void run.refetch();
    });
  };
  const sendWorker: ConversationStore['send'] = (conversationId, content, attachments, _fromComposer, replaces) => {
    if (worker === null || conversationId !== cardId || attachments.length !== 0 || replaces !== null ||
      !['running', 'paused'].includes(worker.phase)) return null;
    const body: WorkerMessageBody = workerTurn === undefined ? { attemptId: worker.attemptId, content }
      : workerMode === 'steer' ? { attemptId: worker.attemptId, content, mode: workerMode, turnId: workerTurn }
        : { attemptId: worker.attemptId, content, mode: workerMode };
    const intent: WorkerSendIntent = { key: mintIdempotencyKey(), body, state: 'sending' };
    if (registry.editWorkerSend(cardId, current => current ?? intent) !== intent) return null;
    return deliverWorkerMessage(intent);
  };
  const unconfirmed = workerSendIntent?.state === 'unconfirmed' ? workerSendIntent : null;
  const toolEvent = worker?.events.findLast(event => event.kind === 'session.tool_progress');
  const toolPayload = toolEvent?.payload;
  const toolName = typeof toolPayload === 'object' && toolPayload !== null && 'name' in toolPayload && typeof toolPayload.name === 'string'
    ? toolPayload.name : null;
  const latestWorkerMessage = worker?.messages[0];
  const workerStatus = worker?.lastError != null ? `Worker turn failed: ${worker.lastError}`
    : worker?.phase === 'interrupt_unknown' ? 'The Worker stop could not be confirmed.'
    : worker?.phase === 'paused' ? 'Worker paused. Send a message to continue.'
    : latestWorkerMessage !== undefined ? `Worker message ${latestWorkerMessage.delivery}: ${latestWorkerMessage.content}`
    : toolName === null ? null : `Worker tool: ${toolName}`;
  return {
    worker,
    updateWorkerSettings: mutations.updateWorkerSettings,
    answerWorkerAsk: (ask, answers) => trackId === undefined ? Promise.reject(new NotSentError())
      : mutations.answerWorkerAsk(trackId, ask.askId, answers, ask.questions),
    workerDelivery: worker?.phase !== 'running' || workerTurn === undefined ? null : {
      mode: workerMode,
      select: mode => setDeliveryChoice({ cardId, turnId: workerTurn, mode }),
    },
    unconfirmedWorkerSend: unconfirmed === null ? null : {
      retry: () => {
        const sending: WorkerSendIntent = { ...unconfirmed, state: 'sending' };
        if (registry.editWorkerSend(cardId, current => current === unconfirmed ? sending : current) === sending)
          void deliverWorkerMessage(unconfirmed);
      },
      dismiss: () => { registry.editWorkerSend(cardId, current => current === unconfirmed ? undefined : current); },
    },
    workerStatus,
    conversations,
    turns: conversation === null ? NO_TURNS : presentation.transcript,
    canQueueWhileRunning,
    nativeQueue: pergaInputProtocol,
    pending: pendingConversationIds(conversation, working, !stalled && view.sending),
    working,
    canStop,
    stalled,
    recovery,
    inputStalled,
    executionHeld,
    stopping,
    stopFeedback,
    sending: view.sending || workerSendIntent?.state === 'sending',
    sendBlocked: recoveryHoldsSend || view.blocked || run.data?.unknown_session_input != null ||
      (worker === null ? pergaInputProtocol && working && !canQueueWhileRunning
        : !['running', 'paused'].includes(worker.phase) || workerSendIntent !== null),
    awaitingConversationUpdate: view.awaitingReconciliation && history.error === null && run.error === null,
    pendingQueue: presentation.queue,
    pendingInputs,
    activeInput: run.data?.active_input ?? null,
    keepInput: submission => resolveInput('keep', submission),
    retryInput: submission => resolveInput('retry', submission),
    pendingSends: presentation.sending,
    queueDisplayAccounting,
    pendingQueueOverflow,
    editQueuedEntry: (entry, text) => registry.holdQueueWrite(cardId, () => (canonical
      ? mutations.editCanonicalQueued(entry,text,canonicalCarrier(),saved => { outbox.acknowledgeQueuedEdit(entry,saved); })
      : mutations.editQueued(entry,text,saved => { outbox.acknowledgeQueuedEdit(entry,saved); })).then(observeQueueOutcome)),
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
    send: worker === null ? outbox.send : sendWorker,
    attachmentsSupported: run.data?.attachments_supported ?? false,
    historyReplacementSupported,
    manualCompactSupported: !recoveryHoldsSend && run.data?.manual_compact_supported === true,
    contextUsage: run.data?.token_usage ?? null,
    compactOutcome: run.data?.compact_outcome ?? null,
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
    restart: { strip: restart.strip, pending: restart.pending, start: restart.start },
    retryHistory: () => { void (history.earlier.failed ? Promise.resolve(history.loadEarlier()) : history.refetch()).catch(() => undefined); },
    retryRun: () => { void run.refetch().catch(() => undefined); },
    loadEarlier,
    blockedReason: run.data?.blocked_reason ?? null,
    providerRecoveryHoldsSend: recoveryHoldsSend,
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
    abandonUnknownInput: run.data?.unknown_session_input == null || (recoveryHoldsSend && (resumeState === 'resuming' || providerResume?.canStartFresh !== true)) ? null : () => {
      const input = run.data?.unknown_session_input;
      if (input == null || input.sessionId !== cardId) return;
      setActionError(null);
      void mutations.abandonSessionInput(input.inputId).catch(() => {
        if (shownCardId.current === cardId) setActionError({ cardId, message: 'Could not start a fresh provider session. Try again.' });
      });
    },
    terminalTimeout: run.data?.terminal_timeout ?? null,
    currentOutcome: run.error !== null || working ? null : currentConversationOutcome(run.data?.terminal_outcome ?? null),
    /* Before the first answer the conversation is following the default, which is
           what a card with no selection really does. */
    model,
    modelCatalog: modelCatalog.data ?? null,
    setModel: (selection) => {
      if (recoveryHoldsSend) return;
      const setFor = cardId;
      setActionError(null);
      /* Dropped when it settles while another conversation is shown: kept, it would show for one commit on the way back (#2068). */
      const fail = (message: string) => { if (shownCardId.current === setFor) setActionError({ cardId: setFor, message }); };
      void mutations.setModel(selection)
        .catch((error: unknown) => {
          fail(refusalText(writeFailureOf(error), PLANNER_MODEL_FAILURES, MODEL_CHANGE_TEXT.refused) ?? MODEL_CHANGE_TEXT.unknown);
        });
    },
    permissionMode: run.data?.permission_mode ?? null,
    setPermissionMode: (mode) => {
      const setFor = cardId;
      setActionError(null);
      void mutations.setPermissionMode(mode).catch((error: unknown) => {
        /* Dropped when it settles while another conversation is shown, as a model change's is (#2068). */
        if (shownCardId.current !== setFor) return;
        setActionError({ cardId: setFor, message: refusalText(writeFailureOf(error), PLANNER_PERMISSION_MODE_FAILURES,
          PERMISSION_MODE_CHANGE_TEXT.refused) ?? PERMISSION_MODE_CHANGE_TEXT.unknown });
      });
    },
  };
}
