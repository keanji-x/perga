import type { UploadAttachmentResponse } from '../../../../core/api/generated/wire.ts';
import { KeyedSendFailure } from '../../../../core/domain/conversation-delivery.ts';
import { type ConversationRun } from '../../../../core/domain/conversation-display.ts';
import type { ConversationSendTarget } from '../../../../core/domain/conversation-outbox.ts';
import { queryKeys } from './query-keys.ts';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { conversationInputMetadata } from '../../../../core/events/invalidation-plan.ts';
import { admitTransport } from './recovery-mutation.ts';
import type { AskAnswer,PlannerPermissionMode } from '../../../../core/api/generated/wire.ts';
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
/** Each intent carries its original admission and decoder, including across pane changes.
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

/**
 * One serialised settings writer per mounted card, held across renders: a fresh writer each render would have nothing
 * in flight to serialise against. Re-created when the card changes. Each call is admitted at the press.
 */
function useCardSettingWriter<A, R>(
  transport: ApiTransportPort, cardId: string, operation: (cardId: string, value: A) => ApiOperation<R>,
  unauthorized: UnauthorizedChannel, accepted: (result: R) => R = result => result,
): (value: A) => Promise<R> {
  const execute = usePlannerOperation();
  const writer = useRef<{ cardId: string; write: (value: A) => Promise<R> } | null>(null);
  if (writer.current === null || writer.current.cardId !== cardId) {
    const serial = createSerialWriter((intent: { value: A; transport: ApiTransportPort }) =>
      execute(intent.transport, operation(cardId, intent.value), unauthorized).then(accepted));
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
  const execute = usePlannerOperation();
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
      return execute(admitted, setPlannerPermissionModeOperation(cardId, mode), unauthorized).then(result => {
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
      const sent = execute(admitted, sendPlannerInputOperation(cardId, text, attachments, idempotencyKey, false, queueIfBusy || target?.queue === true, target?.epoch), unauthorized);
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
        .then(admitted => execute(admitted, steerQueuedSessionMessageOperation(cardId, inputId, turnId), unauthorized))
        .finally(() => { refreshAfterInput(undefined, false); }),
    removeQueuedSessionMessage: (inputId: string) =>
      Promise.resolve().then(() => admitTransport(transport))
        .then(admitted => execute(admitted, removeQueuedSessionMessageOperation(cardId, inputId), unauthorized))
        .finally(() => { refreshAfterInput(undefined, false); }),
    recover: (mode: 'reconnect' | 'reset_context') => Promise.resolve().then(() => admitTransport(transport))
      .then(admitted => execute(admitted, recoverSessionOperation(cardId, mode), unauthorized))
      .finally(() => { refreshAfterInput(undefined, false); }),
    /* Admitted at the press, as every chat write is: a stop that cannot leave the browser is not sent (#2068). */
    compact: () => Promise.resolve().then(() => admitTransport(transport))
      .then((admitted) => execute(admitted, compactPlannerOperation(cardId), unauthorized)).then(refreshAfter),
    interrupt: (worker?: { epoch?: number; turnId: string }) => Promise.resolve().then(() => admitTransport(transport))
      .then((admitted) => execute(admitted, interruptPlannerOperation(cardId, worker), unauthorized)).then(refreshAfter),
    answerInteraction: (sessionId: string, askId: AskId, answers: readonly AskAnswer[], questions: readonly AskQuestion[]) =>
      Promise.resolve().then(() => admitTransport(transport))
        .then(admitted => execute(admitted, answerAskOperation(sessionId, askId, answers, questions), unauthorized))
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
