import { queryKeys } from './query-keys.ts';
import { useQueryClient } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createExecutionTargetOperation,executionTargetsOperation,type CreateExecutionTargetBody,type ExecutionTargetDto } from '../../../../core/domain/area.ts';
import { runOperation } from './query-operation.ts';
export function executionTargetsQueryOptions(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
) {
  return {
    queryKey: queryKeys.executionTargets(),
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
    queryClient.setQueryData<ExecutionTargetDto[]>(queryKeys.executionTargets(), (current) => {
      if (current === undefined) return [created];
      return current.some((item) => item.id === created.id)
        ? current.map((item) => (item.id === created.id ? created : item))
        : [...current, created];
    });
    return created;
  };
}
