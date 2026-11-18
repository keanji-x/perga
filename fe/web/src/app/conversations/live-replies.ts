import { hashKey, notifyManager, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { HarnessLiveReplies, HarnessPhaseTag } from '../../../../core/api/generated/wire.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { ConversationActivity, TranscriptEntry } from '../../../../core/domain/conversation.ts';
import {
  LIVE_REPLY_POLL_MS, awaitsSettling, liveReplyTurns, reconcileLiveReplies, replyMayStream,
  type LiveReplyCopy, type LiveReplyObservation, type LiveReplyTranscriptRow,
} from '../../../../core/domain/conversation-live.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import type { SessionLiveBuffer } from '../events/query-invalidation-adapter.ts';
import { useConnectionStatus } from '../providers/connection-status.tsx';
import { harnessLiveQueryOptions, queryKeys } from '../providers/queries.ts';
import { type History, type TranscriptReads } from './history.ts';
import { useState } from '../../ui/state/public.ts';

const NO_COPIES: readonly LiveReplyCopy[] = Object.freeze([]);

type HeldCopies = Readonly<{ cardId: string; read: number; copies: readonly LiveReplyCopy[]; lastReply: HarnessLiveReplies | import('../../../../core/api/generated/wire.ts').ConversationLiveReplies | null }>;

/**
 * The open conversation's streamed replies (#1923 S2), as agent turns for the transcript's tail.
 * Polls `GET …/harness/live` only while this card is mounted and a reply may stream; the query
 * layer's recovery gating applies as it does to every read. The copies are held per card, so a
 * switch never shows one card's text in another.
 */
export function useLiveReplies({ transport, unauthorized, cardId, enabled, phase, runningTurnId, nextRead, transcriptKey, transcriptReads, items, canonical }: {
  canonical:boolean;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  cardId: string;
  enabled: boolean;
  phase: HarnessPhaseTag | null;
  /** Confirmed run identity, or null while no running turn is known. */
  runningTurnId: string | null;
  /** Tab-owned read order; each streaming visit receives a unique boundary. */
  nextRead: () => number;
  /** The key of the transcript query the rows come from: its results are read and re-read here. */
  transcriptKey: readonly unknown[];
  /** The numbering that query's reads were tracked with. */
  transcriptReads: TranscriptReads;
  /** Every loaded transcript row. */
  items: readonly LiveReplyTranscriptRow[];
}): readonly TranscriptEntry[] {
  const client = useQueryClient();
  const wsConnected = useConnectionStatus()?.connected === true;
  const streaming = enabled && replyMayStream(phase);
  const scope = useStreamReadScope(cardId, streaming, phase, runningTurnId, nextRead,canonical);
  const liveOptions = harnessLiveQueryOptions(transport, cardId, unauthorized,canonical);
  const live = useQuery({
    ...liveOptions,
    // A delayed initial read cannot hold up or answer a later streaming visit.
    // Prefix invalidation still reaches the card; cached payload shape is unchanged.
    queryKey: [...liveOptions.queryKey, scope.read, scope.turnId],
    enabled: streaming && !wsConnected,
    refetchInterval: LIVE_REPLY_POLL_MS,
    gcTime: 0,
  });
  const [held, setHeld] = useState<HeldCopies>(() => ({ cardId, read: scope.read, copies: NO_COPIES, lastReply: null }));
  const copies = held.cardId === cardId && held.read === scope.read ? held.copies : NO_COPIES;
  /* Leaving a card forgets its copies, so returning to it never brings back text from before. */
  useEffect(() => {
    setHeld((current) => current.cardId === cardId && current.read === scope.read
      ? current : { cardId, read: scope.read, copies: NO_COPIES, lastReply: null });
  }, [cardId, scope.read]);
  const apply = useCallback((observation: LiveReplyObservation) => {
    setHeld((current) => {
      const base = current.cardId === cardId && current.read === scope.read ? current.copies : NO_COPIES;
      const next = reconcileLiveReplies(base, observation);
      return next === base && current.cardId === cardId && current.read === scope.read
        ? current : { cardId, read: scope.read, copies: next,
          lastReply: current.cardId === cardId && current.read === scope.read ? current.lastReply : null };
    });
  }, [cardId, scope.read]);
  /* Which read the stored transcript's latest observation came from. Numbered rather than timed: a read
     started in the same millisecond as a phase observation would otherwise be ambiguous. */
  const readTranscriptStart = useCallback(
    () => transcriptReads.startOf(client.getQueryData<History>(transcriptKey)),
    [client, transcriptKey, transcriptReads],
  );
  const transcriptHash = useMemo(() => hashKey(transcriptKey), [transcriptKey]);
  const subscribeToQueries = useCallback((notify: () => void) => {
    // Query creation/removal can notify while another view is rendering. Match
    // React Query's own subscription schedule; snapshots still read the same cache.
    const scheduled = notifyManager.batchCalls(notify);
    return client.getQueryCache().subscribe((event) => {
      if (event.query.queryHash === transcriptHash) scheduled();
    });
  }, [client, transcriptHash]);
  const transcriptStart = useSyncExternalStore(subscribeToQueries, readTranscriptStart);

  // Confirmation of the first identity keeps matching text; a different identity
  // retires the previous turn immediately, even if an idle phase was not observed.
  useEffect(() => {
    if (!canonical && scope.turnId !== null) apply({ kind: 'active-turn', turnId: scope.turnId });
  }, [apply, scope.turnId,canonical]);
  const reply = live.data;
  // Consume each cache value before committing children, instead of committing
  // the old body once and then the grown body in a passive effect. The cursor
  // survives transcript retirement so a completed item is not re-added forever.
  if (reply !== undefined && scope.streaming && (held.cardId !== cardId || held.read !== scope.read || held.lastReply !== reply)) {
    setHeld({ cardId, read: scope.read, lastReply: reply,
      copies: reconcileLiveReplies(copies, 'contract' in reply ? {kind:'native-poll',reply,atMs:Date.now()}
        : { kind: 'poll', reply, atMs: Date.now(), activeTurnId: scope.turnId }) });
  }

  const sessionLiveKey = useMemo(() => queryKeys.sessionLive(cardId), [cardId]);
  const sessionLiveHash = useMemo(() => hashKey(sessionLiveKey), [sessionLiveKey]);
  const subscribeToSessionLive = useCallback((notify: () => void) => {
    const scheduled = notifyManager.batchCalls(notify);
    return client.getQueryCache().subscribe((event) => {
      if (event.query.queryHash === sessionLiveHash) scheduled();
    });
  }, [client, sessionLiveHash]);
  const wsLive = useSyncExternalStore(
    subscribeToSessionLive,
    () => client.getQueryData<SessionLiveBuffer>(sessionLiveKey),
  );

  /* Drawn from the copies the current transcript does not retire, plus any WS delta stream in `sessionLive`. */
  const wsPersistedInTranscript = useMemo(
    () => wsLive !== undefined && items.some((item) => item.turn_id === wsLive.turnId && item.method === 'item/completed' && item.item_uuid?.startsWith('item-')),
    [items, wsLive],
  );
  useEffect(() => {
    if (wsLive?.completed === true && wsPersistedInTranscript) {
      void client.removeQueries({ queryKey: sessionLiveKey });
    }
  }, [client, sessionLiveKey, wsLive?.completed, wsPersistedInTranscript]);

  const visible = useMemo(
    () => {
      let current = canonical || scope.turnId === null ? copies
        : reconcileLiveReplies(copies, { kind: 'active-turn', turnId: scope.turnId });
      if (wsLive !== undefined && wsLive.turnId !== null && wsLive.text.trim() !== '' && !wsPersistedInTranscript) {
        current = reconcileLiveReplies(current, {
          kind: 'poll',
          reply: {
            turn_id: wsLive.turnId,
            items: [{ item_id: `ws-${wsLive.turnId}`, text: wsLive.text }],
          },
          atMs: wsLive.updatedAtMs,
          activeTurnId: null,
        });
      }
      return reconcileLiveReplies(current, { kind: 'transcript', items, readStart: transcriptStart });
    },
    [copies, items, transcriptStart, scope.turnId, canonical, wsLive, wsPersistedInTranscript],
  );

  const wsActivities = useMemo((): readonly ConversationActivity[] => {
    if (wsLive === undefined || wsLive.turnId === null || wsLive.completed === true || wsPersistedInTranscript) return [];
    const activities: ConversationActivity[] = [];
    if (wsLive.thinking.trim() !== '' && !items.some((item) => item.turn_id === wsLive.turnId && item.item_uuid?.startsWith('thinking-'))) {
      const doneThinking = wsLive.text.trim() !== '' || wsLive.tools.length > 0;
      activities.push({
        id: `live-thinking-${wsLive.turnId}`,
        author: 'activity',
        verb: doneThinking ? 'Thought' : 'Thinking',
        target: null,
        state: doneThinking ? 'done' : 'running',
        durationMs: null,
        detail: null,
        content: wsLive.thinking,
        tool: null,
        atMs: wsLive.updatedAtMs,
      });
    }
    for (const tool of wsLive.tools) {
      if (items.some((item) => item.item_uuid === tool.callId)) continue;
      const doneTool = tool.phase === 'completed';
      const argsContent = tool.args !== null && tool.args !== undefined
        ? `Input\n${typeof tool.args === 'string' ? tool.args : JSON.stringify(tool.args, null, 2)}`
        : undefined;
      activities.push({
        id: `live-tool-${tool.callId}`,
        author: 'activity',
        verb: doneTool ? 'Called' : 'Calling',
        target: tool.name,
        state: tool.isError ? 'failed' : doneTool ? 'done' : 'running',
        durationMs: null,
        detail: null,
        ...(argsContent === undefined ? {} : { content: argsContent }),
        tool: tool.name,
        atMs: wsLive.updatedAtMs,
      });
    }
    return activities;
  }, [items, wsLive, wsPersistedInTranscript]);

  /* Only a read started after this point stands for the phase, however late an earlier one lands.
     The transcript is re-read here so that one always starts; the cancel comes first because the
     query layer would hand back an initial read still in flight in place of a new one. */
  const rereadAfter = useRef<number | null>(null);
  const reread = useCallback(() => {
    rereadAfter.current = transcriptReads.latest();
    void cancelThenInvalidate(client, transcriptKey);
  }, [client, transcriptKey, transcriptReads]);
  useEffect(() => {
    if (phase === null || !awaitsSettling(visible, phase)) return;
    apply({ kind: 'phase', phase, latestReadStart: transcriptReads.latest() });
    reread();
  }, [apply, phase, reread, transcriptReads, visible]);
  /* Another reader can supersede the re-read and keep the stored latest observation,
     so while a settled copy still shows, an idle transcript is read again until a later read lands.
     Not after a failed read: the query layer's retries are spent by then, and starting another here
     would retry it for as long as the conversation stays open. The next event or refetch resumes. */
  const transcriptLanded = useSyncExternalStore(subscribeToQueries, () => {
    const state = client.getQueryState(transcriptKey);
    return state?.fetchStatus === 'idle' && state.status === 'success';
  });
  const awaitsRead = visible.some((copy) => copy.settledAt !== null);
  useEffect(() => {
    const after = rereadAfter.current;
    if (!transcriptLanded || !awaitsRead || after === null || transcriptReads.latest() <= after) return;
    reread();
  }, [awaitsRead, reread, transcriptLanded, transcriptReads]);
  useEffect(() => {
    if (visible !== copies) apply({ kind: 'transcript', items, readStart: transcriptStart });
  }, [apply, copies, items, transcriptStart, visible]);

  return useMemo(() => [...wsActivities, ...liveReplyTurns(visible)], [wsActivities, visible]);
}


/** A view's streaming read window, not another server execution state. Keep the
 * last confirmed identity through an interrupt; losing an optional run clock
 * must not drop its reply. Entering issuing_turn is a new start even when no
 * non-streaming phase was observed. Query keys bind separately to confirmed ids. */
function useStreamReadScope(
  cardId: string, streaming: boolean, phase: HarnessPhaseTag | null,
  runningTurnId: string | null, nextRead: () => number, canonical:boolean,
) {
  const [scope, setScope] = useState(() => ({ cardId, streaming, phase, turnId: runningTurnId, read: nextRead(),canonical }));
  const starts = scope.cardId !== cardId || scope.canonical !== canonical || (streaming && !scope.streaming)
    || (streaming && phase === 'issuing_turn' && scope.phase !== 'issuing_turn');
  const turnId = starts ? runningTurnId : runningTurnId ?? scope.turnId;
  if (starts || scope.streaming !== streaming || scope.phase !== phase || scope.turnId !== turnId) {
    setScope({ cardId, streaming, phase, turnId, read: starts ? nextRead() : scope.read,canonical });
  }
  return scope;
}
