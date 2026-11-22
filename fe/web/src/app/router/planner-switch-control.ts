import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import type { TrackAggregateSnapshotDto } from "../../../../core/api/perga-v1.ts";
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { NotSentError,writeFailureOf } from '../../../../core/domain/failure-class.ts';
import { PLANNER_SWITCH_UNCONFIRMED,plannerSwitchFailure } from '../../../../core/domain/planner-switch.ts';
import { useCommittedCallback } from '../../ui/state/committed-callback.ts';
import type { PlannerConversationScope } from '../conversations/contracts.ts';
import { usePlannerSwitchMutation } from '../providers/planner-switch-mutation.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';

/** Owns one pane's human intent; scope is the selected conversation, never the page Track. */
export function usePlannerSwitchControl(transport: ApiTransportPort, unauthorized: UnauthorizedChannel, scope: PlannerConversationScope | null) {
  const client = useQueryClient();
  const mutation = usePlannerSwitchMutation(transport, unauthorized);
  const pending = useRef(false);
  return useCommittedCallback(scope === null ? null : `${scope.id}:${scope.cardId}`, async () => {
    if (scope === null || pending.current) return false;
    pending.current = true;
    const options = trackDetailQueryOptions(transport, scope.id, unauthorized);
    let detail: TrackAggregateSnapshotDto | undefined;
    try {
      // Intent reads must reach the bounded transport even if Query considers the browser offline.
      const cached = client.getQueryData<TrackAggregateSnapshotDto>(options.queryKey);
      if (cached === undefined) await client.cancelQueries({ queryKey: options.queryKey, exact: true });
      const snapshot: TrackAggregateSnapshotDto = cached
        ?? await client.fetchQuery({ ...options, retry: false, networkMode: 'always' });
      detail = snapshot;
      const planner = snapshot.sessions.find(session => session.isLead);
      if (planner === undefined) throw new Error(PLANNER_SWITCH_UNCONFIRMED.en);
      const refresh = async () => {
        const keys = [options.queryKey, queryKeys.plannerRun(planner.id), queryKeys.plannerRun(scope.cardId)];
        await Promise.all(keys.map(queryKey => client.cancelQueries({ queryKey, exact: true })));
        // Keep the aggregate and each session run fresh; tool resources have their own cache.
        for (const queryKey of keys) void client.invalidateQueries({ queryKey, exact: true,
          refetchType: queryKey === options.queryKey ? 'none' : 'active' }).catch(() => undefined);
        return client.fetchQuery({ ...options, retry: false, networkMode: 'always' }).catch(() => null);
      };
      let receipt;
      try {
        receipt = await mutation.mutateAsync({ trackId: scope.id, leadSessionId: scope.cardId });
      } catch (error) {
        const writeFailure = writeFailureOf(error);
        const failure = plannerSwitchFailure(writeFailure);
        if (!(writeFailure instanceof NotSentError)) {
          // A known refusal releases the command and draft without waiting for reconciliation.
          if (failure.unconfirmed) await refresh();
          else void refresh().catch(() => undefined);
        }
        const card = snapshot.sessions.find(session => session.id === failure.cardId);
        const role = failure.cardId === planner.id ? 'Original Planner' : failure.cardId === scope.cardId ? 'Target Assistant' : null;
        throw new Error(`${failure.message}${role === null ? '' : ` (${role}: ${card?.title ?? failure.cardId})`}`);
      }
      const confirmed = await refresh();
      if (confirmed === null || receipt.id !== scope.id || receipt.leadSessionId !== scope.cardId
        || confirmed.sessions.find(session => session.isLead)?.id !== scope.cardId
        || !confirmed.sessions.some(session => session.id === planner.id && !session.isLead)) {
        throw new Error(PLANNER_SWITCH_UNCONFIRMED.en);
      }
      return true;
    } catch (error) {
      if (detail === undefined) throw new Error(PLANNER_SWITCH_UNCONFIRMED.en);
      throw error;
    } finally { pending.current = false; }
  });
}
