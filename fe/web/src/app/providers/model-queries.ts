import { queryKeys } from './query-keys.ts';
import { type AgentProvider } from '../../../../core/api/schemas.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { modelCatalogOperation,type ModelCatalogScope } from '../../../../core/domain/conversation.ts';
import { runOperation } from './query-operation.ts';
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
