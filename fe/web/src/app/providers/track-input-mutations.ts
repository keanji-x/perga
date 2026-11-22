import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import { useRecoveryMutation } from './recovery-mutation.ts';
import type { AskAnswer } from '../../../../core/domain/presentation-types.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { type AskId, type AskQuestion } from '../../../../core/domain/activity.ts';
import { decideApprovalOperation, type PendingApproval } from '../../../../core/domain/approval.ts';
import { answerAskOperation } from '../../../../core/domain/ask.ts';
import { runOperation } from './query-operation.ts';
export function useTrackInputMutations(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  const client = useQueryClient();
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
  return {
    answerAsk: async (trackId: string, askId: AskId, answers: readonly AskAnswer[], questions?: readonly AskQuestion[]) => { await answerAsk.mutateAsync({ trackId, askId, answers, questions }); },
    decideApproval: async (trackId: string, approval: PendingApproval, optionId: string) => { await decideApproval.mutateAsync({ trackId, approval, optionId }); },
  };
}
