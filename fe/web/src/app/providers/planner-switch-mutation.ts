import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { plannerSwitchOperation } from '../../../../core/domain/planner-switch.ts';
import { runOperation } from './query-operation.ts';
import { useOnlineMutation } from './online-mutation.ts';

/** Offline rejection and retry policy are shared with the other user writes. */
export function usePlannerSwitchMutation(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return useOnlineMutation({
    mutationFn: ({ trackId, leadSessionId }: { trackId: string; leadSessionId: string }) =>
      runOperation(transport, plannerSwitchOperation(trackId, leadSessionId), unauthorized),
  });
}
