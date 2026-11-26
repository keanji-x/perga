import type { MultipartFile } from '../../../../core/api/multipart.ts';
import { KeyedSendFailure } from '../../../../core/domain/conversation-delivery.ts';
import { type ConversationRun } from '../../../../core/domain/conversation-display.ts';
import type { ConversationSendTarget } from '../../../../core/domain/conversation-outbox.ts';
import { queryKeys } from './query-keys.ts';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { conversationInputMetadata } from '../../../../core/events/invalidation-plan.ts';
import type { AgentProvider, AskAnswer, PlannerPermissionMode } from '../../../../core/domain/presentation-types.ts';
import type { ApiOperation,ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type AskId,type AskQuestion } from '../../../../core/domain/activity.ts';
import { answerAskOperation } from '../../../../core/domain/ask.ts';
import { compactPlannerOperation, interruptPlannerOperation, recoverSessionOperation, removeQueuedSessionMessageOperation, sendPlannerInputOperation, steerQueuedSessionMessageOperation } from '../../../../core/domain/conversation-commands.ts';
import { createSerialWriter, setPlannerModelOperation, type ModelSelectionResult } from '../../../../core/domain/conversation-models.ts';
import { type ModelSelection } from '../../../../core/domain/conversation.ts';
import { ApiError } from '../../../../core/domain/failure-class.ts';
import { setPlannerPermissionModeOperation,type SetPlannerPermissionModeResponse } from '../../../../core/domain/planner-permission-mode.ts';
import type { ConversationRegistry } from '../conversations/registry-model.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { plannerRunQueryOptions } from './conversation-queries.ts';
import { runOperation } from './query-operation.ts';
/** Each intent carries its transport and decoder, including across pane changes.
 * Mutation retries/offline queuing must never replay a user command. */
function usePlannerOperation() {
  const mutation = useMutation({
    networkMode: 'always', retry: false,
    mutationFn: (intent: { transport: ApiTransportPort; operation: ApiOperation<unknown>; unauthorized: UnauthorizedChannel }) =>
      runOperation(intent.transport, intent.operation, intent.unauthorized),
  });
  return <T,>(transport: ApiTransportPort, operation: ApiOperation<T>, unauthorized: UnauthorizedChannel): Promise<T> =>
    mutation.mutateAsync({ transport, operation, unauthorized }) as Promise<T>;
}

export function usePlannerMutations(transport: ApiTransportPort, cardId: string, trackId: string | null, unauthorized: UnauthorizedChannel, permissions: ConversationRegistry) {
  const client = useQueryClient();
  const execute = usePlannerOperation();
  const runKey = plannerRunQueryOptions(transport, cardId, unauthorized).queryKey;
  const transcriptKey = queryKeys.harnessItems(cardId);
  const refreshTranscript = () => client.invalidateQueries({ queryKey: transcriptKey });
  const refreshInputMetadata = (error?: unknown) => {
    const canRead = !(error instanceof ApiError && error.failure.kind === 'unauthorized');
    for (const queryKey of conversationInputMetadata(trackId)) {
      // An unauthorized write leaves the metadata stale without issuing another protected request.
      if (canRead) void cancelThenInvalidate(client, queryKey);
      else void client.invalidateQueries({ queryKey, refetchType: 'none' }).catch(() => undefined);
    }
  };
  // One serialized model/provider writer per mounted card, keeping only the latest waiting intent.
  const modelWriter = useRef<{ cardId: string; write: (dispatch: () => Promise<ModelSelectionResult>) => Promise<ModelSelectionResult> } | null>(null);
  const setModelWrite = (selection: ModelSelection, provider?: AgentProvider): Promise<ModelSelectionResult> => {
    if (modelWriter.current === null || modelWriter.current.cardId !== cardId) {
      modelWriter.current = { cardId, write: createSerialWriter((dispatch: () => Promise<ModelSelectionResult>) => dispatch()) };
    }
    return modelWriter.current.write(() => {
      const readVersion = client.getQueryState(runKey)?.dataUpdateCount;
      return execute(transport, setPlannerModelOperation(cardId, selection, provider), unauthorized).then((result) => {
        let providerChanged = provider !== undefined;
        if (client.getQueryState(runKey)?.dataUpdateCount === readVersion) {
          // Cancel any older in-flight GET before publishing the ACK, matching permission writes.
          void cancelThenInvalidate(client, queryKeys.plannerRun(cardId));
          client.setQueryData<ConversationRun>(runKey, (current) => {
            if (current === undefined) return current;
            const changed = current.session.provider !== result.provider;
            if (changed) providerChanged = true;
            return {
              ...current,
              session: { ...current.session, ...result },
              ...(changed ? { permissionModes: [], capabilities: undefined } : {}),
            };
          });
        } else {
          void cancelThenInvalidate(client, queryKeys.plannerRun(cardId));
        }
        void client.invalidateQueries({ queryKey: queryKeys.modelCatalogForCard(cardId) }).catch(() => undefined);
        void refreshTranscript().catch(() => undefined);
        if (providerChanged) refreshInputMetadata();
        return result;
      });
    });
  };
  // The tab registry owns serialization across A → B → A and actual pane remounts.
  const setPermissionModeWrite = (mode: PlannerPermissionMode): Promise<SetPlannerPermissionModeResponse> => {
    const previous = permissions.permissionWriteOf(cardId);
    const key = runKey;
    const dispatch = () => {
      const readVersion = client.getQueryState(key)?.dataUpdateCount;
      return execute(transport, setPlannerPermissionModeOperation(cardId, mode), unauthorized).then(result => {
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
  const refreshAfter = <T,>(result: T): T => {
    /* A 200 from the write is the acknowledgement; refetch is reconciliation and its failure must not
           turn an accepted send into a failed write. Not awaited: a hung read must not retain the send lease. */
    void Promise.all([
      refreshTranscript(),
      client.invalidateQueries({ queryKey: queryKeys.plannerRun(cardId) }),
    ]).catch(() => undefined);
    return result;
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
    send: (text: string, attachments: readonly MultipartFile[], idempotencyKey: string,
      queueIfBusy = false, target?: ConversationSendTarget) => {
      const sent = execute(transport, sendPlannerInputOperation(cardId, text, attachments, idempotencyKey, false, queueIfBusy || target?.queue === true, target?.epoch), unauthorized);
      return sent.then(receipt => {
        if (receipt.delivery === 'unknown' || receipt.delivery === 'dispatching')
          throw new KeyedSendFailure(new Error('Message delivery is unconfirmed.'), 'unknown');
        if (receipt.delivery === 'rejected' || receipt.delivery === 'interrupted' || receipt.delivery === 'withdrawn')
          throw new KeyedSendFailure(new Error('The message was not delivered.'), 'refused');
        return receipt;
      }).then(receipt => refreshAfterInput(receipt, false), (error: unknown) => {
        refreshInputMetadata(error);
        if (target !== undefined && error instanceof ApiError && error.failure.kind === 'http' && error.failure.status === 409)
          refreshAfter(undefined);
        throw error;
      });
    },
    steerQueuedSessionMessage: (inputId: string, turnId: string) =>
      execute(transport, steerQueuedSessionMessageOperation(cardId, inputId, turnId), unauthorized)
        .finally(() => { refreshAfterInput(undefined, false); }),
    removeQueuedSessionMessage: (inputId: string) =>
      execute(transport, removeQueuedSessionMessageOperation(cardId, inputId), unauthorized)
        .finally(() => { refreshAfterInput(undefined, false); }),
    recover: (mode: 'reconnect' | 'reset_context') => execute(transport, recoverSessionOperation(cardId, mode), unauthorized)
      .finally(() => { refreshAfterInput(undefined, false); }),
    compact: () => execute(transport, compactPlannerOperation(cardId), unauthorized).then(refreshAfter),
    interrupt: (worker?: { epoch?: number; turnId: string }) => execute(transport, interruptPlannerOperation(cardId, worker), unauthorized).then(refreshAfter),
    answerInteraction: (sessionId: string, askId: AskId, answers: readonly AskAnswer[], questions: readonly AskQuestion[]) =>
      execute(transport, answerAskOperation(sessionId, askId, answers, questions), unauthorized)
        .finally(() => { refreshAfterInput(undefined, false); }),
    /* The stored selection is read back from `planner-run`; the catalog goes with it because the default
     * this card follows is part of that answer. */
    setModel: (selection: ModelSelection, provider?: AgentProvider): Promise<ModelSelectionResult> =>
      setModelWrite(selection, provider),
    /* Each actual PUT publishes its ACK inside the serial writer, before the next queued intent. */
    setPermissionMode: (mode: PlannerPermissionMode): Promise<SetPlannerPermissionModeResponse> =>
      setPermissionModeWrite(mode),
  };
}
