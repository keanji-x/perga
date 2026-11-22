import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { plannerSwitchOperation } from '../../../../core/domain/planner-switch.ts';
import { runOperation } from './query-operation.ts';
import { useRecoveryMutation } from './recovery-mutation.ts';

/** Recovery admission and retry:false are shared with the other human writes. */
export function usePlannerSwitchMutation(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return useRecoveryMutation(transport, {
    mutationFn: ({ trackId, leadSessionId }: { trackId: string; leadSessionId: string }, admitted) =>
      runOperation(admitted, plannerSwitchOperation(trackId, leadSessionId), unauthorized),
  });
}
