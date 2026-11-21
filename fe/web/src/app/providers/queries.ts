import type { ConversationCursor,ConversationLiveReplies,UploadAttachmentResponse } from '../../../../core/api/generated/wire.ts';
import { KeyedSendFailure } from '../../../../core/domain/conversation-delivery.ts';
import { conversationItemsOperation,conversationRunOperation,type ConversationRun } from '../../../../core/domain/conversation-display.ts';
import { conversationLiveOperation } from '../../../../core/domain/conversation-live.ts';
import type { ConversationSendTarget } from '../../../../core/domain/conversation-outbox.ts';
import { retireAreaQueries } from './area-query-lifecycle.ts';
import { queryKeys } from './query-keys.ts';
export { queryKeys } from './query-keys.ts';
// Query and mutation wiring shared by app/router, app/shell and every feature slice. It lives under
// app/providers because the router renders the shell and both need the same reads (`no-circular`).

import {
keepPreviousData,useQueries,useQuery,useQueryClient,type QueryClient,type UseQueryResult,
} from '@tanstack/react-query';
import { useMemo,useRef } from 'react';
import { z } from 'zod';
import type { TrackOverlayItemDto } from '../../../../core/api/perga-v1.ts';
import { bootstrapDtoSchema,type ReportReadResultDto,type TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import { type AgentProvider } from "../../../../core/api/schemas.ts";
import { conversationInputMetadata } from '../../../../core/events/invalidation-plan.ts';
import { admitTransport,useRecoveryMutation } from './recovery-mutation.ts';

import { performApiRequest } from '../../../../core/api/client.ts';
import type {
AskAnswer,PlannerPermissionMode
} from '../../../../core/api/generated/wire.ts';
import type { ApiOperation,ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type AskId,type AskQuestion } from '../../../../core/domain/activity.ts';
import { decideApprovalOperation,type PendingApproval } from '../../../../core/domain/approval.ts';
import {
  asFolderConflict, areaListOperation, createAreaOperation,
  createExecutionTargetOperation, deleteAreaOperation,
  executionTargetsOperation, newestArea, sortedAreas, toArea, updateAreaOperation, visibleAreas,
  type Area, type AreaPatchBody, type CreateExecutionTargetBody, type ExecutionTargetDto, type FolderConflict, type NewAreaBody,
} from '../../../../core/domain/area.ts';
import { answerAskOperation } from '../../../../core/domain/ask.ts';
import type { CreatedConversation,SideConversation } from '../../../../core/domain/conversation.ts';
import {
compactPlannerOperation,
createSerialWriter,
createTrackConversationOperation,
interruptPlannerOperation,
modelCatalogOperation,
recoverSessionOperation,
removeQueuedSessionMessageOperation,
sendPlannerInputOperation,
setConversationArchiveOperation,
setPlannerModelOperation,
steerQueuedSessionMessageOperation,
type ModelCatalogScope,type ModelSelection,type ModelSelectionResult
} from '../../../../core/domain/conversation.ts';
import {
ApiError,classifyFailure,DELETE_FAILURES
} from '../../../../core/domain/failure-class.ts';
import { setPlannerPermissionModeOperation,type SetPlannerPermissionModeResponse } from '../../../../core/domain/planner-permission-mode.ts';
import {
type ResolvedSeries
} from '../../../../core/domain/report-series.ts';
import { type TrackSourceDetail } from '../../../../core/domain/report-source.ts';
import {
deriveReportTasks,hasLiveTaskRun,readTrackViewsOperation,
trackTaskVerdictsOperation,
updateTrackReportBlockOperation,
type HumanReportBlockUpdateBody,type ReportBlock,type TaskVerdict,type TemplateViewsProjection
} from '../../../../core/domain/report.ts';
import {
createTerminalCardOperation,
createTrackOperation,
deleteTerminalOperation,deleteTrackOperation,
listTrackTerminalsOperation,
NEUTRAL_ACTIVITY,sortAreaTracksByRecent,
toTrack,
trackActivitiesFromOverlays,trackDetailOperation,
trackOverlaysOperation,
tracksInAreaOperation,
updateTrackOperation,
type CardWire,
type NewTerminalCardBody,
type NewTrackBody,
type Track,type TrackPatchBody
} from '../../../../core/domain/track.ts';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import type { ConversationRegistry } from '../conversations/registry-model.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { WEB_COMPAT_VERSION,type ServerVersionInfo } from './public.tsx';



/** A failed capability preflight guarantees no Area POST was submitted. */
export class AreaCreatePreflightError extends Error {
  constructor(message: string) {
    super(`${message} No new create request was sent.`);
    this.name = 'AreaCreatePreflightError';
  }
}

/** The structured folder clash inside a rejected mutation, or `null`; the decode and wording are `core/domain/area.ts`. */
export function folderConflictOf(error: unknown): FolderConflict | null {
  if (!(error instanceof ApiError)) return null;
  return 'body' in error.failure ? asFolderConflict(error.failure.body) : null;
}

/** TanStack Query wants a rejected promise; core reports failures as data. */
export async function runOperation<T>(
  transport: ApiTransportPort,
  operation: ApiOperation<T>,
  unauthorized: UnauthorizedChannel | undefined,
): Promise<T> {
  const checkpoint = transport.recovery?.checkpoint();
  const result = await performApiRequest(transport, operation, unauthorized);
  checkpoint?.();
  if (result.status === 'failed') throw new ApiError(result.error);
  return result.value;
}

export type TranscriptPosition = Readonly<{ kind: 'latest' } | { kind: 'before'; cursor: ConversationCursor }>;

export function conversationHistoryQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: [...queryKeys.harnessItems(cardId),'conversation_v1'],
    queryFn: async ({ position, signal }: { position: TranscriptPosition; signal?: AbortSignal }) => {
      const cursor = position.kind === 'latest' ? null : position.cursor;
      const page = await runOperation(transport, { ...conversationItemsOperation(cardId, cursor), signal }, unauthorized);
      if (cursor !== null && (page.items.some(item => item.cursor.position >= cursor.position)
        || (page.next_cursor !== null && page.next_cursor.position >= cursor.position))) {
        throw new Error('Conversation history did not advance to an earlier page.');
      }
      return page;
    },
  };
}

export function harnessLiveQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: [...queryKeys.harnessLive(cardId),'conversation_v1'],
    queryFn: async ({ signal }: { signal: AbortSignal }):Promise<ConversationLiveReplies> => runOperation(transport,{...conversationLiveOperation(cardId),signal},unauthorized),
  };
}

export function plannerRunQueryOptions(transport: ApiTransportPort, cardId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: [...queryKeys.plannerRun(cardId),'conversation_v1'],
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...conversationRunOperation(cardId), signal }, unauthorized),
  };
}

export function executionTargetsQueryOptions(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
) {
  return {
    queryKey: ['execution-targets'] as const,
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<ExecutionTargetDto[]> =>
      runOperation(transport, { ...executionTargetsOperation(), signal }, unauthorized),
  };
}

export function useCreateExecutionTarget(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
): (body: CreateExecutionTargetBody) => Promise<ExecutionTargetDto> {
  const queryClient = useQueryClient();
  return async (body: CreateExecutionTargetBody): Promise<ExecutionTargetDto> => {
    const created = await runOperation(transport, createExecutionTargetOperation(body), unauthorized);
    queryClient.setQueryData<ExecutionTargetDto[]>(['execution-targets'], (current) => {
      if (current === undefined) return [created];
      return current.some((item) => item.id === created.id)
        ? current.map((item) => (item.id === created.id ? created : item))
        : [...current, created];
    });
    return created;
  };
}

/** No `staleTime` of its own: codex keeps a 300 s disk cache behind this. */
export function modelCatalogQueryOptions(
  transport: ApiTransportPort,
  scope: ModelCatalogScope,
  unauthorized: UnauthorizedChannel,
  options?: Readonly<{ provider?: AgentProvider; refresh?: boolean }>,
) {
  return {
    queryKey: queryKeys.modelCatalog(scope),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...modelCatalogOperation(scope, options), signal }, unauthorized),
  };
}

/**
 * One serialised settings writer per mounted card, held across renders: a fresh writer each render would have nothing
 * in flight to serialise against. Re-created when the card changes. Each call is admitted at the press.
 */
function useCardSettingWriter<A, R>(
  transport: ApiTransportPort, cardId: string, operation: (cardId: string, value: A) => ApiOperation<R>,
  unauthorized: UnauthorizedChannel, accepted: (result: R) => R = result => result,
): (value: A) => Promise<R> {
  const writer = useRef<{ cardId: string; write: (value: A) => Promise<R> } | null>(null);
  if (writer.current === null || writer.current.cardId !== cardId) {
    const serial = createSerialWriter((intent: { value: A; transport: ApiTransportPort }) =>
      runOperation(intent.transport, operation(cardId, intent.value), unauthorized).then(accepted));
    writer.current = {
      cardId,
      write: (value: A) => {
        try { return serial({ value, transport: admitTransport(transport) }); }
        catch (error) { return Promise.reject(error instanceof Error ? error : new Error('连接尚未恢复')); }
      },
    };
  }
  return writer.current.write;
}

export function usePlannerMutations(transport: ApiTransportPort, cardId: string, trackId: string | null, unauthorized: UnauthorizedChannel, permissions: ConversationRegistry) {
  const client = useQueryClient();
  const setModelWrite = useCardSettingWriter<ModelSelection, ModelSelectionResult>(
    transport, cardId, setPlannerModelOperation, unauthorized);
  // The tab registry owns serialization across A → B → A and actual pane remounts.
  // Admission belongs to the press; waiting for the preceding write must not admit an offline press later.
  const setPermissionModeWrite = (mode: PlannerPermissionMode): Promise<SetPlannerPermissionModeResponse> => {
    let admitted: ApiTransportPort;
    try { admitted = admitTransport(transport); } catch (error) { return Promise.reject(error instanceof Error ? error : new Error('Could not change permission mode.')); }
    const previous = permissions.permissionWriteOf(cardId);
    const key = plannerRunQueryOptions(transport, cardId, unauthorized).queryKey;
    const dispatch = () => {
      const readVersion = client.getQueryState(key)?.dataUpdateCount;
      return runOperation(admitted, setPlannerPermissionModeOperation(cardId, mode), unauthorized).then(result => {
        // A completed GET is authoritative too. Neither an older ACK nor its cancellation may revert it.
        const publish = permissions.permissionWriteOf(cardId) === held
          && client.getQueryState(key)?.dataUpdateCount === readVersion;
        if (publish) {
          // Cancellation reverts a pending GET synchronously, before the ACK is published.
          void cancelThenInvalidate(client, queryKeys.plannerRun(cardId));
          client.setQueryData<ConversationRun>(key,
            current => current === undefined ? current : { ...current, session: { ...current.session, permissionMode: result.permissionMode } });
        } else {
          void cancelThenInvalidate(client, queryKeys.plannerRun(cardId));
        }
        return result;
      });
    };
    const write = previous === undefined ? dispatch() : previous.catch(() => undefined).then(dispatch);
    const held = permissions.holdPermissionWrite(cardId, write);
    return held;
  };
  const transcriptKey = queryKeys.harnessItems(cardId);
  const refreshTranscript = () => client.invalidateQueries({ queryKey: transcriptKey });
  const refreshAfter = <T,>(result: T): T => {
    /* A 200 from the write is the acknowledgement; refetch is reconciliation and its failure must not
           turn an accepted send into a failed write. Not awaited: a hung read must not retain the send lease. */
    void Promise.all([
      refreshTranscript(),
      client.invalidateQueries({ queryKey: queryKeys.plannerRun(cardId) }),
    ]).catch(() => undefined);
    return result;
  };
  const refreshInputMetadata = (admitted: ApiTransportPort = transport, error?: unknown) => {
    let canRead = !(error instanceof ApiError && error.failure.kind === 'unauthorized');
    try { admitted.recovery?.capture(); } catch { canRead = false; }
    for (const queryKey of conversationInputMetadata(trackId)) {
      // Recovery owns read admission and resumption. Retain stale metadata while denied;
      // do not turn a refused/unknown write into a business read outside that permission.
      if (canRead) void cancelThenInvalidate(client, queryKey);
      else void client.invalidateQueries({ queryKey, refetchType: 'none' }).catch(() => undefined);
    }
  };
  /* As `refreshAfter`, but each read is cancelled first: the query layer would otherwise hand back a read still in
     flight from before the answer; input acceptance must reconcile against actual reads started after its ACK. */
  const refreshAfterInput = <T,>(result: T, resetTranscript: boolean): T => {
    if (resetTranscript) void client.resetQueries({ queryKey: transcriptKey }).catch(() => undefined);
    else void cancelThenInvalidate(client, transcriptKey);
    void cancelThenInvalidate(client, queryKeys.plannerRun(cardId));
    refreshInputMetadata();
    return result;
  };
  return {
    send: (text: string, attachments: readonly string[], idempotencyKey: string,
      admitted: ApiTransportPort, queueIfBusy = false, target?: ConversationSendTarget) => {
      const sent = runOperation(admitted, sendPlannerInputOperation(cardId, text, attachments, idempotencyKey, false, queueIfBusy || target?.queue === true, target?.epoch), unauthorized);
      return sent.then(receipt => {
        if (receipt.delivery === 'unknown' || receipt.delivery === 'dispatching')
          throw new KeyedSendFailure(new Error('Message delivery is unconfirmed.'), 'unknown');
        if (receipt.delivery === 'rejected' || receipt.delivery === 'interrupted' || receipt.delivery === 'withdrawn')
          throw new KeyedSendFailure(new Error('The message was not delivered.'), 'refused');
        return receipt;
      }).then(receipt => refreshAfterInput(receipt, false), (error: unknown) => {
        refreshInputMetadata(admitted, error);
        if (target !== undefined && error instanceof ApiError && error.failure.kind === 'http' && error.failure.status === 409)
          refreshAfter(undefined);
        throw error;
      });
    },
    steerQueuedSessionMessage: (inputId: string, turnId: string) =>
      Promise.resolve().then(() => admitTransport(transport))
        .then(admitted => runOperation(admitted, steerQueuedSessionMessageOperation(cardId, inputId, turnId), unauthorized))
        .finally(() => { refreshAfterInput(undefined, false); }),
    removeQueuedSessionMessage: (inputId: string) =>
      Promise.resolve().then(() => admitTransport(transport))
        .then(admitted => runOperation(admitted, removeQueuedSessionMessageOperation(cardId, inputId), unauthorized))
        .finally(() => { refreshAfterInput(undefined, false); }),
    recover: (mode: 'reconnect' | 'reset_context') => Promise.resolve().then(() => admitTransport(transport))
      .then(admitted => runOperation(admitted, recoverSessionOperation(cardId, mode), unauthorized))
      .finally(() => { refreshAfterInput(undefined, false); }),
    /* Admitted at the press, as every chat write is: a stop that cannot leave the browser is not sent (#2068). */
    compact: () => Promise.resolve().then(() => admitTransport(transport))
      .then((admitted) => runOperation(admitted, compactPlannerOperation(cardId), unauthorized)).then(refreshAfter),
    interrupt: (worker?: { epoch?: number; turnId: string }) => Promise.resolve().then(() => admitTransport(transport))
      .then((admitted) => runOperation(admitted, interruptPlannerOperation(cardId, worker), unauthorized)).then(refreshAfter),
    answerInteraction: (sessionId: string, askId: AskId, answers: readonly AskAnswer[], questions: readonly AskQuestion[]) =>
      Promise.resolve().then(() => admitTransport(transport))
        .then(admitted => runOperation(admitted, answerAskOperation(sessionId, askId, answers, questions), unauthorized))
        .finally(() => { refreshAfterInput(undefined, false); }),
    /* The stored selection is read back from `planner-run`; the catalog goes with it because the default
     * this card follows is part of that answer. */
    setModel: (selection: ModelSelection): Promise<ModelSelectionResult> =>
      setModelWrite(selection)
        .then((result) => {
          void client.invalidateQueries({ queryKey: queryKeys.modelCatalogForCard(cardId) })
            .catch(() => undefined);
          return refreshAfter(result);
        }),
    /* Each actual PUT publishes its ACK inside the serial writer, before the next queued intent. */
    setPermissionMode: (mode: PlannerPermissionMode): Promise<SetPlannerPermissionModeResponse> =>
      setPermissionModeWrite(mode),
    /* No `refreshAfter`: an upload changes nothing any query holds until a send names it. Every failure, the
     * admission's included, rejects with the sentence the strip shows, read against the route's table. */
    uploadAttachment: (() => Promise.reject(new Error('Image attachments are unavailable.'))) as (readBytes: () => Promise<Uint8Array>, contentType: string) => Promise<() => UploadAttachmentResponse>,
  };
}



export type ConversationMutations = Readonly<{
  archive: (cardId: string, archived: boolean) => Promise<void>;
  /** The key identifies the DRAFT, not the attempt: a retry after a timeout must reuse it, or it mints a second conversation. */
  create: (text: string, idempotencyKey: string, provider: AgentProvider, selection: ModelSelection, side: SideConversation | undefined,
    answered: (result: CreatedConversation) => void, permissionMode?: string | null) => Promise<CreatedConversation>;
}>;

/** Session receipts and recovery reads update the Track's authoritative aggregate. */
export function useTrackConversationMutations(
  transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel,
): ConversationMutations {
  const client = useQueryClient();
  const create = useRecoveryMutation(transport, {
    mutationFn: async ({ text, idempotencyKey, provider, selection, side, permissionMode }: { text: string; idempotencyKey: string; provider: AgentProvider; selection: ModelSelection; side?: SideConversation; answered: (result: CreatedConversation) => void; permissionMode?: string | null }, transport: ApiTransportPort) => {
      return runOperation(
        transport,
        createTrackConversationOperation(trackId, text, idempotencyKey, provider, selection, side, permissionMode),
        unauthorized,
      );
    },
    onSuccess: (result, { answered }) => {
      // Fence an older aggregate read before publishing the create receipt.
      // Query cancellation reverts synchronously, before its Promise settles. Keep
      // this recovery-admitted publish in the same turn, without an await boundary.
      void client.cancelQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
      client.setQueryData<TrackAggregateSnapshotDto>(queryKeys.trackDetail(trackId), current => {
        if (current === undefined || current.track.id !== trackId) return current;
        if (current.sessions.some(session => session.id === result.session.id)) return current;
        return { ...current, sessions: [...current.sessions, result.session] };
      });
      answered(result);
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
    },
  });
  const archive = useRecoveryMutation(transport, {
    mutationFn: ({ cardId, archived }: { cardId: string; archived: boolean }, transport: ApiTransportPort) =>
      runOperation(transport, setConversationArchiveOperation(trackId, cardId, archived), unauthorized),
    onSettled: async () => {
      await client.cancelQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
      await client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId), exact: true });
    },
  });
  const changeArchive = useCommittedCallback(trackId,
    (cardId: string, archived: boolean) => archive.mutateAsync({ cardId, archived }));
  return {
    archive: changeArchive,
    create: (text, idempotencyKey, provider, selection, side, answered, permissionMode) =>
      create.mutateAsync({ text, idempotencyKey, provider, selection, side, answered, permissionMode }),
  };
}

const serverVersionSchema = z.object({
  pergaV1: z.boolean().optional(),
  authRequired: z.boolean().optional(),
  assistantProviders: z.boolean().optional(),
  conversationCreateModel: z.boolean().optional(),
  conversationInitialInput: z.boolean().optional(),
  sessionInputReceipts: z.literal(true).optional(),
  conversationSide: z.boolean().optional(),
  webCompatVersion: z.number(),
  minWebCompatVersion: z.number(),
  syncEventVersion: z.number(),
  dbInstanceId: z.string(),
  databaseId: z.string().optional(),
});

export function serverVersionOperation(): ApiOperation<ServerVersionInfo> {
  return {
    method: 'GET',
    path: '/api/v1/bootstrap',
    responseSchema: z.union([
      bootstrapDtoSchema.transform((bootstrap): ServerVersionInfo => ({
        pergaV1: true,
        authRequired: bootstrap.authRequired ?? false,
        assistantProviders: true,
        conversationCreateModel: true,
        conversationInitialInput: true,
        conversationSide: false,
        webCompatVersion: WEB_COMPAT_VERSION,
        minWebCompatVersion: WEB_COMPAT_VERSION,
        syncEventVersion: 1,
        dbInstanceId: bootstrap.dbInstanceId ?? `perga-${bootstrap.version}`,
        ...(bootstrap.databaseId !== null && bootstrap.databaseId !== undefined ? { databaseId: bootstrap.databaseId } : {}),
      })),
      serverVersionSchema,
    ]),
  };
}

/** Sign-out is a server-side session kill; the caller reloads afterwards so
 *  every persisted cache restarts from an unauthenticated probe. */
export function logoutOperation(): ApiOperation<undefined> {
  return { method: 'POST', path: '/api/auth/logout', responseSchema: z.undefined() };
}

// ---------- reads ----------

export function areaListQueryOptions(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return {
    // The shell owns an explicit Retry action; automatic retry backoff would
    // replace the known read error with an empty pending view on invalidation.
    retry: false,
    queryKey: queryKeys.areas(),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Area[]> =>
      sortedAreas(visibleAreas((await runOperation(transport, { ...areaListOperation(), signal }, unauthorized)).map(toArea))),
  };
}

export function trackOverlaysQueryOptions(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.overlaysByKind('track'),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<TrackOverlayItemDto[]> => {
      try {
        return await runOperation(transport, { ...trackOverlaysOperation(), signal }, unauthorized);
      } catch (error) {
        if (isNotFound(error)) return [];
        throw error;
      }
    },
  };
}

export function tracksInAreaQueryOptions(transport: ApiTransportPort, areaId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.tracksInArea(areaId),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<Track[]> =>
      (await runOperation(transport, { ...tracksInAreaOperation(areaId), signal }, unauthorized)).map((wire) => toTrack(wire)),
    // Preserve Query's browser default of three retries for transient reads. Only typed
    // session/access/missing failures are terminal; runOperation still notifies on 401.
    retry: (failureCount: number, error: unknown) => {
      if (error instanceof ApiError) {
        const failure = error.failure;
        if (failure.kind === 'unauthorized'
          || (failure.kind === 'http' && (failure.status === 403 || failure.status === 404))) return false;
      }
      return failureCount < 3;
    },
  };
}

export function trackDetailQueryOptions(transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.trackDetail(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TrackAggregateSnapshotDto> =>
      runOperation(transport, { ...trackDetailOperation(trackId), signal }, unauthorized),
  };
}

/** Workbench resources have their own cache and never contain agent sessions. */
export function trackToolsQueryOptions(transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel) {
  return {
    queryKey: queryKeys.trackTools(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...listTrackTerminalsOperation(trackId), signal }, unauthorized),
  };
}

export function trackViewsQueryOptions(
  transport: ApiTransportPort,
  trackId: string,
  unauthorized: UnauthorizedChannel,
  options?: Readonly<{ enabled?: boolean }>,
) {
  return {
    queryKey: queryKeys.trackViews(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TemplateViewsProjection> =>
      runOperation(transport, { ...readTrackViewsOperation(trackId), signal }, unauthorized),
    staleTime: 5_000,
    placeholderData: keepPreviousData,
    ...(options?.enabled !== undefined ? { enabled: options.enabled } : {}),
  };
}

/**
 * The track's task verdicts. Its own cache entry: these change on every dispatch and gate result without
 * any card being written. Events alone do not keep it live — `scheduler::mark_running` stamps
 * `worker_card_id` without emitting anything — hence the timer.
 */
export function trackTaskVerdictsQueryOptions(
  transport: ApiTransportPort, trackId: string, unauthorized: UnauthorizedChannel,
  /* The declarations this report has: the timer is about the ROWS the panel draws, and a verdict is not a row. */
  blocks: readonly ReportBlock[] | null,
) {
  return {
    queryKey: queryKeys.trackReport(trackId),
    queryFn: ({ signal }: { signal: AbortSignal }): Promise<TaskVerdict[]> =>
      runOperation(transport, { ...trackTaskVerdictsOperation(trackId), signal }, unauthorized),
    /* 3 s is priced off the endpoint (p50 14.8 ms for a small report, 104 ms for a 24-task one): ~3.5% of
     * one core for one open track, only while something is running, and O(1) in the number of workers. */
    refetchInterval: taskVerdictsRefetchInterval(blocks),
  };
}

/** The live poll, once the read has landed at least once. */
const TASK_VERDICT_POLL_MS = 3000;
/** The recovery poll, while the read has never landed at all; far slower, since nothing is being tracked. */
const TASK_VERDICT_RECOVERY_POLL_MS = 15_000;
/**
 * Bounded: the report route also fails permanently (deleted track 404s, missing `track-report` card
 * 500s), and an unconditional poll on "no data" would hit a dead route forever.
 */
const TASK_VERDICT_RECOVERY_ATTEMPTS = 4;

/** With data: poll only while the panel holds a live row. With no data: a bounded recovery poll read off `errorUpdateCount` (`failureCount` resets on success).
 * Curried on the declarations because verdicts for deleted declarations (`blockId: ''`) produce no row, so a timer keyed on raw verdicts would refetch with nothing on screen. */
export function taskVerdictsRefetchInterval(blocks: readonly ReportBlock[] | null) {
  return (query: { state: { data?: TaskVerdict[]; errorUpdateCount: number } }): number | false => {
    const { data, errorUpdateCount } = query.state;
    if (data !== undefined) {
      return hasLiveTaskRun(deriveReportTasks(blocks, data)) ? TASK_VERDICT_POLL_MS : false;
    }
    return errorUpdateCount > 0 && errorUpdateCount <= TASK_VERDICT_RECOVERY_ATTEMPTS
      ? TASK_VERDICT_RECOVERY_POLL_MS
      : false;
  };
}

export type Workspace = Readonly<{
  areas: readonly Area[];
  tracksByArea: ReadonlyMap<string, readonly Track[]>;
  tracks: readonly Track[];
  areasLoading: boolean;
  overlaysLoading: boolean;
  areasError: Error | null;
  overlaysError: Error | null;
  trackErrorsByArea: ReadonlyMap<string, Error>;
  tracksLoadingByArea: ReadonlyMap<string, boolean>;
  retryAreas: () => void;
  retryOverlays: () => void;
  retryTracks: (areaId: string) => void;
}>;

// Query structurally shares these plain arrays independently: read status can change
// without replacing unchanged data. The outer useQueries result is not a memo key.
function combineWorkspaceTracks(results: UseQueryResult<Track[]>[]) {
  return {
    data: results.map(query => query.data),
    reads: results.map(query => ({ pending: query.isPending, error: query.error, refetch: query.refetch })),
  };
}

const NO_AREAS: readonly Area[] = Object.freeze([]);

/**
 * The area → tracks fan-out is a page-level `useQueries`, never a route loader await: one slow area
 * must not block the calendar. The workspace-wide overlay read is folded in so every surface sees the same activity.
 */
export function useWorkspace(transport: ApiTransportPort, unauthorized: UnauthorizedChannel): Workspace {
  const areasQuery = useQuery(areaListQueryOptions(transport, unauthorized));
  const overlaysQuery = useQuery(trackOverlaysQueryOptions(transport, unauthorized));
  const areas = areasQuery.data ?? NO_AREAS;
  const trackQueries = useQueries({
    queries: areas.map((area) => tracksInAreaQueryOptions(transport, area.id, unauthorized)),
    combine: combineWorkspaceTracks,
  });
  const activity = useMemo(() => trackActivitiesFromOverlays(overlaysQuery.data ?? []), [overlaysQuery.data]);
  const { tracksByArea, tracks } = useMemo(() => {
    const tracksByArea = new Map<string, readonly Track[]>();
    const tracks: Track[] = [];
    for (const [index, area] of areas.entries()) {
      const data = trackQueries.data[index];
      if (data === undefined) continue;
      const rows = data.map(track => ({ ...track, ...(activity.get(track.id) ?? NEUTRAL_ACTIVITY) }));
      tracksByArea.set(area.id, sortAreaTracksByRecent(rows));
      tracks.push(...rows);
    }
    return { tracksByArea, tracks };
  }, [activity, areas, trackQueries.data]);
  const { trackErrorsByArea, tracksLoadingByArea } = useMemo(() => {
    const trackErrorsByArea = new Map<string, Error>();
    const tracksLoadingByArea = new Map<string, boolean>();
    for (const [index, area] of areas.entries()) {
      const query = trackQueries.reads[index];
      tracksLoadingByArea.set(area.id, query?.pending ?? false);
      if (query?.error instanceof Error) trackErrorsByArea.set(area.id, query.error);
    }
    return { trackErrorsByArea, tracksLoadingByArea };
  }, [areas, trackQueries.reads]);
  return {
    areas, tracksByArea, tracks, areasLoading: areasQuery.isPending,
    overlaysLoading: overlaysQuery.isLoading,
    areasError: areasQuery.error instanceof Error ? areasQuery.error : null,
    overlaysError: overlaysQuery.error instanceof Error ? overlaysQuery.error : null,
    trackErrorsByArea,
    tracksLoadingByArea,
    retryAreas: () => { void areasQuery.refetch(); },
    retryOverlays: () => { void overlaysQuery.refetch(); },
    retryTracks: (areaId) => {
      const index = areas.findIndex((area) => area.id === areaId);
      if (index >= 0) void trackQueries.reads[index]?.refetch();
    },
  };
}

/** Route loaders prime only this one list. */
export function prefetchAreaList(client: QueryClient, transport: ApiTransportPort, unauthorized: UnauthorizedChannel): Promise<void> {
  // Prefetch failures belong to the Areas query; rejecting a loader here would replace the entire app.
  void client.prefetchQuery(areaListQueryOptions(transport, unauthorized));
  // A paused offline query must not hold the route commit either.
  return Promise.resolve();
}

// ---------- mutations ----------
//
// Every mutation invalidates. A write-through may precede it only when the response IS the new cache
// value (an id-keyed row the server just returned) and the very next render needs it.

export type AreaMutations = Readonly<{
  create: (body: NewAreaBody, idempotencyKey: string) => Promise<Area>;
  update: (areaId: string, body: AreaPatchBody) => Promise<Area>;
  remove: (areaId: string, signal?: AbortSignal) => Promise<void>;
}>;

/** A delete answered done (404, `DELETE_FAILURES`): the row is gone, so its cache entry goes as on success. */
function deleteDone(error: unknown): boolean {
  return error instanceof ApiError && classifyFailure(error.failure, DELETE_FAILURES) === 'done';
}

export function useAreaMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel): AreaMutations {
  const client = useQueryClient();
  const create = useRecoveryMutation(transport, {
    mutationFn: async ({ body, idempotencyKey }: { body: NewAreaBody; idempotencyKey: string }, transport: ApiTransportPort) => {
      return runOperation(transport, createAreaOperation(body, idempotencyKey), unauthorized);
    },
    onSuccess: (wire) => {
      const created = toArea(wire);
      client.setQueryData<Area[]>(queryKeys.areas(), (current) => {
        if (current === undefined) return current;
        const existing = current.find((area) => area.id === created.id);
        return sortedAreas([
          ...current.filter((area) => area.id !== created.id),
          existing === undefined ? created : newestArea(existing, created),
        ]);
      });
      void cancelThenInvalidate(client, queryKeys.areas());
    },
    // A lost response can follow a committed creation; the retained creation key makes the next POST safe.
    onError: () => client.invalidateQueries({ queryKey: queryKeys.areas() }),
  });
  const update = useRecoveryMutation(transport, {
    mutationFn: ({ areaId, body }: { areaId: string; body: AreaPatchBody }, transport: ApiTransportPort) =>
      runOperation(transport, updateAreaOperation(areaId, body), unauthorized),
    onSuccess: (wire) => {
      const updated = toArea(wire);
      // The Area editor closes as soon as mutateAsync resolves; without the write-through a click in the
      // refetch window snapshots stale defaults into NewTrackForm's local state.
      client.setQueryData<Area[]>(queryKeys.areas(), (current) => current?.map(
        (area) => area.id === updated.id
          ? newestArea(area, updated)
          : area,
      ));
    },
    // A transport failure may still follow a committed write, so failure cannot leave cached defaults authoritative.
    onSettled: () => client.invalidateQueries({ queryKey: queryKeys.areas() }),
  });
  const remove = useRecoveryMutation(transport, {
    mutationFn: ({ areaId, signal }: { areaId: string; signal?: AbortSignal }, transport: ApiTransportPort) =>
      runOperation(transport, { ...deleteAreaOperation(areaId), signal }, unauthorized),
    // The area is gone, also when the delete was answered done; its track list can never resolve again.
    onSuccess: (_result, { areaId }) => { retireAreaQueries(client, areaId); },
    onError: (error, { areaId }) => {
      if (deleteDone(error)) retireAreaQueries(client, areaId);
    },
    // Abort only ends the client wait: the server may already have committed.
    onSettled: () => { void client.invalidateQueries({ queryKey: queryKeys.areas() }); },
  });
  return {
    create: async (body, idempotencyKey) => {
      const created = toArea(await create.mutateAsync({ body, idempotencyKey }));
      const current = client.getQueryData<Area[]>(queryKeys.areas())?.find(area => area.id === created.id);
      // Route drafts must receive the same newest defaults as the shared list.
      return current === undefined ? created : newestArea(created, current);
    },
    update: async (areaId, body) => toArea(await update.mutateAsync({ areaId, body })),
    remove: async (areaId, signal) => { await remove.mutateAsync({ areaId, signal }); },
  };
}

export type TrackMutations = Readonly<{
  /** Mint `idempotencyKey` ONCE per draft: a fresh key on a retry mints a second track. */
  create: (body: NewTrackBody, idempotencyKey: string) => Promise<Track>;
  patch: (trackId: string, areaId: string, body: TrackPatchBody) => Promise<Track>;
  setPinned: (trackId: string, areaId: string, pinned: boolean, nowMs: number) => Promise<Track>;
  createTerminal: (trackId: string, body: NewTerminalCardBody, idempotencyKey: string) => Promise<CardWire>;
  removeCard: (trackId: string, cardId: string, signal?: AbortSignal) => Promise<void>;
  remove: (trackId: string, areaId: string, signal?: AbortSignal) => Promise<void>;
  /** Answer one durable ask, one answer per question in order. A `409` rejects too: `ANSWER_ASK_FAILURES` reads it as done. */
  answerAsk: (trackId: string, askId: AskId, answers: readonly AskAnswer[], questions?: readonly AskQuestion[]) => Promise<void>;
  /** Decide one live execution-scoped approval by structured option ID. */
  decideApproval: (trackId: string, approval: PendingApproval, optionId: string) => Promise<void>;
  /** Update one report block by human CAS edit (`PUT /api/v1/tracks/{trackId}/report/blocks/{blockId}`). */
  updateReportBlock: (trackId: string, blockId: string, body: HumanReportBlockUpdateBody) => Promise<ReportReadResultDto>;
}>;

export function useTrackMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel): TrackMutations {
  const client = useQueryClient();
  const create = useRecoveryMutation(transport, {
    mutationFn: (variables: { body: NewTrackBody; idempotencyKey: string }, transport: ApiTransportPort) =>
      runOperation(transport, createTrackOperation(variables.body, variables.idempotencyKey), unauthorized),
    onSuccess: (track, { body }) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(track.area_id) });
      // Explicit `attach_folder` mints an area_folders row; drop any cached list so a later read cannot serve a stale empty array.
      if (body.attach_folder) {
        client.removeQueries({ queryKey: queryKeys.areaFolders(body.area_id) });
      }
    },
  });
  const patch = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, body }: { trackId: string; areaId: string; body: TrackPatchBody }, transport: ApiTransportPort) =>
      runOperation(transport, updateTrackOperation(trackId, body), unauthorized),
    // A patch can move the track: the area the server just reported is refetched too.
    onSuccess: (track, variables) => {
      if (track.area_id !== variables.areaId) void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(track.area_id) });
    },
    // No write-through: the detail's Close / Reopen capabilities are the server's to derive, so the page keeps its last
    // detail until the refetch (or the `track.updated` event's) replaces it whole. `onSettled`: an unknown answer may
    // still follow a stored patch.
    onSettled: (_track, _error, variables) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(variables.areaId) });
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(variables.trackId) });
    },
  });
  const remove = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, signal }: { trackId: string; areaId: string; signal?: AbortSignal }, transport: ApiTransportPort) =>
      runOperation(transport, { ...deleteTrackOperation(trackId), signal }, unauthorized),
    onSuccess: (_result, variables) => {
      client.removeQueries({ queryKey: queryKeys.trackDetail(variables.trackId) });
    },
    onError: (error, variables) => {
      if (deleteDone(error)) client.removeQueries({ queryKey: queryKeys.trackDetail(variables.trackId) });
    },
    // Reconcile both list-derived surfaces even if abort raced a committed DELETE.
    onSettled: (_result, _error, variables) => {
      void client.invalidateQueries({ queryKey: queryKeys.tracksInArea(variables.areaId) });
      void cancelThenInvalidate(client, queryKeys.overlaysByKind('track'));
    },
  });
  /* Tool receipts go to the independent inventory. Cancellation synchronously fences
   * older reads; publishing stays inside the recovery-admitted callback. */
  const addToolToCache = (card: CardWire): void => {
    void client.cancelQueries({ queryKey: queryKeys.trackTools(card.track_id), exact: true });
    client.setQueryData(queryKeys.trackTools(card.track_id), (previous: CardWire[] | undefined) => {
      if (previous?.some((existing) => existing.id === card.id)) return previous;
      return [...(previous ?? []), card];
    });
    void client.invalidateQueries({ queryKey: queryKeys.trackTools(card.track_id), exact: true });
  };
  const createTerminal = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, body, key }: { trackId: string; body: NewTerminalCardBody; key: string }, transport: ApiTransportPort) =>
      runOperation(transport, createTerminalCardOperation(trackId, body, key), unauthorized),
    onSuccess: addToolToCache,
  });
  /* Delete drops the row from the tool cache before the refetch lands: leaving it on screen would keep
   * a PTY attached to a card the kernel has torn down. A delete answered done (404, `DELETE_FAILURES`) is gone the same
   * way. `onSettled`: an aborted wait says nothing about commit. */
  const dropCard = (_result: unknown, { trackId, cardId }: { trackId: string; cardId: string }) => {
    void client.cancelQueries({ queryKey: queryKeys.trackTools(trackId), exact: true });
    client.setQueryData(queryKeys.trackTools(trackId), (previous: CardWire[] | undefined) => {
      if (previous === undefined) return previous;
      const cards = previous.filter((existing) => existing.id !== cardId);
      return cards.length === previous.length ? previous : cards;
    });
  };
  const removeCard = useRecoveryMutation(transport, {
    mutationFn: async ({ trackId, cardId, signal }: { trackId: string; cardId: string; signal?: AbortSignal }, transport: ApiTransportPort) => {
      if (cardId.startsWith('file-local-')) {
        return undefined;
      }
      return runOperation(transport, { ...deleteTerminalOperation(trackId, cardId), signal }, unauthorized);
    },
    onSuccess: dropCard,
    onError: (error, variables) => { if (deleteDone(error)) dropCard(undefined, variables); },
    onSettled: (_result, _error, { trackId }) => {
      void cancelThenInvalidate(client, queryKeys.trackTools(trackId));
      void cancelThenInvalidate(client, queryKeys.overlaysByKind('track'));
    },
  });
  /* An answer may apply a lifecycle action; refresh detail/lists even after an uncertain response.
   * The projector's overlay remains authoritative for removing the question itself. */
  const answerAsk = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, askId, answers, questions }: { trackId: string; askId: AskId; answers: readonly AskAnswer[]; questions?: readonly AskQuestion[] }, transport: ApiTransportPort) =>
      Promise.resolve().then(() => {
        const snapshot = client.getQueryData<import('../../../../core/api/perga-v1.ts').TrackAggregateSnapshotDto>(queryKeys.trackDetail(trackId));
        const sessionId = snapshot?.openAsks?.find(ask => ask.id === String(askId))?.sessionId;
        if (sessionId == null) throw new Error('Refresh the question before answering.');
        return runOperation(transport, answerAskOperation(sessionId, askId, answers, questions), unauthorized);
      }),
    onSettled: (_answer, _error, { trackId }) => {
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId) });
      void client.invalidateQueries({ queryKey: queryKeys.trackLists() });
    },
  });
  const decideApproval = useRecoveryMutation(transport, {
    mutationFn: ({ approval, optionId }: { trackId: string; approval: PendingApproval; optionId: string }, transport: ApiTransportPort) =>
      runOperation(transport, decideApprovalOperation(approval.sessionId, approval.executionId, approval.id, optionId), unauthorized),
    onSettled: (_answer, _error, { trackId, approval }) => {
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId) });
      void client.invalidateQueries({ queryKey: queryKeys.trackLists() });
      void client.invalidateQueries({ queryKey: queryKeys.plannerRun(approval.sessionId) });
      void client.invalidateQueries({ queryKey: queryKeys.harnessItems(approval.sessionId) });
    },
  });
  const updateReportBlock = useRecoveryMutation(transport, {
    mutationFn: ({ trackId, blockId, body }: { trackId: string; blockId: string; body: HumanReportBlockUpdateBody }, transport: ApiTransportPort) =>
      runOperation(transport, updateTrackReportBlockOperation(trackId, blockId, body), unauthorized),
    onSettled: (_result, _error, { trackId }) => {
      void client.invalidateQueries({ queryKey: queryKeys.trackDetail(trackId) });
      void client.invalidateQueries({ queryKey: queryKeys.trackReport(trackId) });
    },
  });
  const patchTrack = async (trackId: string, areaId: string, body: TrackPatchBody) =>
    toTrack(await patch.mutateAsync({ trackId, areaId, body }));
  return {
    create: async (body, idempotencyKey) => toTrack(await create.mutateAsync({ body, idempotencyKey })),
    patch: patchTrack,
    createTerminal: async (trackId, body, key) => createTerminal.mutateAsync({ trackId, body, key }),
    removeCard: async (trackId, cardId, signal) => {
      await removeCard.mutateAsync({ trackId, cardId, signal });
    },
    // `pinned_at` is both the flag and the ordering key, so unpinning is a
    // null write rather than a delete of some separate row.
    setPinned: (trackId, areaId, pinned, nowMs) =>
      patchTrack(trackId, areaId, { pinned_at: pinned ? nowMs : null }),
    remove: async (trackId, areaId, signal) => { await remove.mutateAsync({ trackId, areaId, signal }); },
    answerAsk: async (trackId, askId, answers, questions) => { await answerAsk.mutateAsync({ trackId, askId, answers, questions }); },
    decideApproval: async (trackId, approval, optionId) => { await decideApproval.mutateAsync({ trackId, approval, optionId }); },
    updateReportBlock: async (trackId, blockId, body) => updateReportBlock.mutateAsync({ trackId, blockId, body }),
  };
}

/** The wire, or the 409 turned into a value: `stale-rev` is a condition the next `['track', id]` refetch fixes, not an error. */
export type SeriesRead = ResolvedSeries | Readonly<{ status: 'stale-rev'; current_rev: number }>;

/** The row, or the 404 turned into a value: a dangling citation is a state the design admits, not an error to retry. */
export type SourceRead =
  | Readonly<{ status: 'found'; source: TrackSourceDetail }>
  | Readonly<{ status: 'missing' }>;

function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.failure.kind === 'http' && error.failure.status === 404;
}
