import { queryKeys } from './query-keys.ts';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { managedProviderProblem, managedProvidersOperation } from '../../../../core/domain/agent-providers.ts';
import { WEB_COMPAT_VERSION } from './public.tsx';
import { serverVersionOperation } from './server-queries.ts';
import { modelCatalogQueryOptions } from './model-queries.ts';
import { runOperation } from './query-operation.ts';


export function managedProvidersQueryOptions(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  targetId?: string,
) {
  return {
    queryKey: queryKeys.managedProviders(targetId),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      runOperation(transport, { ...managedProvidersOperation(false, targetId), signal }, unauthorized),
  };
}

/** Backend registrations own the menu membership; catalogs use their reported provider identity. */
export function useManagedProviders(
  transport: ApiTransportPort,
  unauthorized: UnauthorizedChannel,
  enabled: boolean,
  targetId?: string,
) {
  const client = useQueryClient();
  const query = useQuery({ ...managedProvidersQueryOptions(transport, unauthorized, targetId), enabled });
  const version = useQuery({ queryKey: queryKeys.serverVersion(), queryFn: () => runOperation(transport, serverVersionOperation(), unauthorized), enabled, retry: false });
  const supportsAssistantProviders = !version.isError && version.data?.assistantProviders === true
    && version.data.webCompatVersion >= WEB_COMPAT_VERSION && version.data.minWebCompatVersion <= WEB_COMPAT_VERSION;
  const answers = query.isError ? undefined : query.data;
  const remoteTargetId = targetId !== undefined && targetId !== '' && targetId !== 'local' ? targetId : undefined;
  const catalogs = useQueries({ queries: (answers ?? []).map(({ availability }) => ({
    ...modelCatalogQueryOptions(
      transport,
      { kind: 'provider', provider: availability.provider, ...(remoteTargetId !== undefined ? { targetId: remoteTargetId } : {}) },
      unauthorized,
    ),
    enabled: enabled && managedProviderProblem(answers, availability.provider) === null,
  })) });
  const recheck = async () => {
    const next = await runOperation(transport, managedProvidersOperation(true, targetId), unauthorized);
    client.setQueryData(queryKeys.managedProviders(targetId), next);
    return next;
  };
  return { recheck, answers, version, supportsAssistantProviders, loading: query.isPending && !query.isError, groups: (answers ?? []).map(({ availability }, index) => ({
    provider: availability.provider, availability, catalog: catalogs[index].data ?? null,
    selectionProblem: managedProviderProblem(answers, availability.provider),
  })) };
}

/** New Track catalogs follow target-scoped registrations, including list/order changes. */
export function useManagedProviderCatalogs(transport: ApiTransportPort, unauthorized: UnauthorizedChannel, targetId: string) {
  const providers = useQuery(managedProvidersQueryOptions(transport, unauthorized, targetId));
  const catalogs = useQueries({ queries: (providers.data ?? []).map(({ availability }) =>
    modelCatalogQueryOptions(transport, { kind: 'provider', provider: availability.provider, targetId }, unauthorized)) });
  return { providers, catalogs };
}
