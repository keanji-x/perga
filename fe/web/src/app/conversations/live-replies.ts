import { nativeTranscriptEntryId } from '../../../../core/domain/conversation-display.ts';
import { conversationToolShape } from '../../../../core/domain/conversation-tool.ts';
import { workerToolActivity } from '../../../../core/domain/worker-session.ts';
import { hashKey, notifyManager, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from 'react';

import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { HarnessLiveReplies, HarnessPhaseTag } from '../../../../core/domain/presentation-types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import type { ConversationActivity, TranscriptEntry } from '../../../../core/domain/conversation.ts';
import {
  LIVE_REPLY_POLL_MS, sessionActivityItemId, awaitsSettling, liveReplyTurns, reconcileLiveReplies, replyMayStream,
  type LiveReplyCopy, type LiveReplyObservation, type LiveReplyTranscriptRow,
} from '../../../../core/domain/conversation-live.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import type { SessionLiveBuffer } from '../events/query-invalidation-adapter.ts';
import { useConnectionStatus } from '../providers/connection-status.tsx';
import { harnessLiveQueryOptions } from '../providers/conversation-queries.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { type History, type TranscriptReads } from './history.ts';
import { useState } from '../../ui/state/public.ts';

const NO_COPIES: readonly LiveReplyCopy[] = Object.freeze([]);

type HeldCopies = Readonly<{ cardId: string; read: number; copies: readonly LiveReplyCopy[]; lastReply: HarnessLiveReplies | import('../../../../core/domain/presentation-types.ts').ConversationLiveReplies | null }>;

/**
 * The open conversation's streamed replies (#1923 S2), as agent turns for the transcript's tail.
 * Polls `GET …/harness/live` only while this card is mounted and a reply may stream; the query
 * layer's recovery gating applies as it does to every read. The copies are held per card, so a
 * switch never shows one card's text in another.
 */
export function useLiveReplies({ transport, unauthorized, cardId, enabled, phase, runningTurnId, nextRead, transcriptKey, transcriptReads, items, activity }: {
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
  activity?: import('../../../../core/api/perga-v1.ts').SessionActivity | null;
}): readonly TranscriptEntry[] {
  const client = useQueryClient();
  const wsConnected = useConnectionStatus()?.connected === true;
  const streaming = enabled && replyMayStream(phase);
  const scope = useStreamReadScope(cardId, streaming, phase, runningTurnId, nextRead);
  const liveOptions = harnessLiveQueryOptions(transport, cardId, unauthorized);
  const live = useQuery({
    ...liveOptions,
    // A delayed initial read cannot hold up or answer a later streaming visit.
    // Prefix invalidation still reaches the card; cached payload shape is unchanged.
    queryKey: queryKeys.conversationLiveRead(cardId, scope.read, scope.turnId),
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
    () => wsLive !== undefined && items.some((item) => item.turnId === wsLive.turnId && item.completed && item.itemId?.startsWith('item-')),
    [items, wsLive],
  );
  useEffect(() => {
    if (wsLive?.completed === true && wsPersistedInTranscript) {
      void client.removeQueries({ queryKey: sessionLiveKey });
    }
  }, [client, sessionLiveKey, wsLive?.completed, wsPersistedInTranscript]);

  const visible = useMemo(
    () => {
      let current = copies;
      const recent = activity?.events.filter(event => event.kind === 'session.text_delta') ?? [];
      const snapshot = activity?.truncated === true ? `…\n${activity.text}` : activity?.text ?? '';
      const tail = wsLive?.turnId === scope.turnId ? wsLive?.parts?.filter(part => part.sequence > (activity?.sequence ?? 0)) : undefined;
      const liveText = activity == null || scope.turnId === null ? wsLive?.text ?? '' : snapshot + (tail?.map(part => part.delta).join('') ?? '');
      const liveTurn = activity == null ? wsLive?.turnId : scope.turnId;
      if (liveTurn != null && liveText.trim() !== '' && !wsPersistedInTranscript) {
        current = reconcileLiveReplies(current, {
          kind: 'poll',
          reply: {
            turn_id: liveTurn,
            items: [{ item_id: sessionActivityItemId(liveTurn), text: liveText }],
          },
          atMs: Math.max(wsLive?.updatedAtMs ?? 0, recent.at(-1)?.createdAtMs ?? 0),
          activeTurnId: null,
        });
      }
      return reconcileLiveReplies(current, { kind: 'transcript', items, readStart: transcriptStart });
    },
    [copies, items, transcriptStart, scope.turnId, wsLive, wsPersistedInTranscript, activity],
  );

  const wsActivities = useMemo((): readonly ConversationActivity[] => {
    const turnId = scope.turnId ?? wsLive?.turnId;
    if (turnId == null || (wsLive?.turnId === turnId && (wsPersistedInTranscript || wsLive.completed === true))) return [];
    const stream = wsLive?.turnId === turnId ? wsLive : undefined;
    const activities: ConversationActivity[] = [];
    if (stream !== undefined && stream.thinking.trim() !== '' && !items.some((item) => item.turnId === turnId && item.itemId?.startsWith('thinking-'))) {
      const doneThinking = stream.text.trim() !== '' || stream.tools.length > 0;
      activities.push({
        id: `live-thinking-${turnId}`,
        author: 'activity',
        verb: doneThinking ? 'Thought' : 'Thinking',
        target: null,
        state: doneThinking ? 'done' : 'running',
        durationMs: null,
        detail: null,
        content: stream.thinking,
        tool: null,
        atMs: stream.updatedAtMs,
      });
    }
    const tools = workerToolActivity(scope.turnId === turnId ? activity?.events ?? [] : []);
    const streamedTools = stream?.tools ?? [];
    const storedTools = new Map(items.filter(item => item.turnId === turnId && item.kind === 'tool_call')
      .map(item => [item.itemId, item]));
    // Polling may only see completions, which can arrive out of start order.
    const firstStored = streamedTools.findIndex(tool => storedTools.has(tool.callId));
    const pagedOut = new Set(firstStored > 0 ? streamedTools.slice(0, firstStored).map(tool => tool.callId) : []);
    for (const update of streamedTools) {
      const prior = tools.get(update.callId);
      const useStreamState = prior?.phase !== 'completed' || update.phase === 'completed';
      tools.set(update.callId, {
        ...update,
        phase: useStreamState ? update.phase : prior.phase,
        isError: update.isError || prior?.isError === true,
        name: update.name ?? prior?.name ?? null,
        args: update.args ?? prior?.args,
        output: prior?.output ?? null,
        atMs: prior?.atMs ?? stream?.updatedAtMs ?? 0,
      });
    }
    for (const tool of tools.values()) {
      const doneTool = tool.phase === 'completed';
      const stored = storedTools.get(tool.callId);
      if (stored !== undefined && (stored.completed || !doneTool)) continue;
      if (stored === undefined && doneTool && pagedOut.has(tool.callId)) continue;
      const id = stored === undefined ? `live-tool-${tool.callId}`
        : stored.threadId !== undefined ? nativeTranscriptEntryId(stored.threadId, tool.callId, 0)
          : `activity-${tool.callId}`;
      const shape = conversationToolShape(tool.name ?? 'tool', tool.args ?? {});
      const input = tool.args == null ? null : `Input\n${typeof tool.args === 'string' ? tool.args : JSON.stringify(tool.args, null, 2)}`;
      const output = tool.output === null ? null : `Output\n${tool.output}`;
      const content = [input, output].filter(Boolean).join('\n\n');
      activities.push({
        id,
        author: 'activity',
        verb: doneTool ? shape.done : shape.running,
        target: shape.target,
        state: tool.isError ? 'failed' : doneTool ? 'done' : 'running',
        durationMs: null,
        detail: null,
        ...(content === '' ? {} : { content }),
        tool: tool.name,
        atMs: tool.atMs,
      });
    }
    return activities;
  }, [items, scope.turnId, activity?.events, wsLive, wsPersistedInTranscript]);

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
  runningTurnId: string | null, nextRead: () => number,
) {
  const [scope, setScope] = useState(() => ({ cardId, streaming, phase, turnId: runningTurnId, read: nextRead() }));
  const starts = scope.cardId !== cardId || (streaming && !scope.streaming)
    || (streaming && phase === 'issuing_turn' && scope.phase !== 'issuing_turn');
  const turnId = starts ? runningTurnId : runningTurnId ?? scope.turnId;
  if (starts || scope.streaming !== streaming || scope.phase !== phase || scope.turnId !== turnId) {
    setScope({ cardId, streaming, phase, turnId, read: starts ? nextRead() : scope.read });
  }
  return scope;
}
