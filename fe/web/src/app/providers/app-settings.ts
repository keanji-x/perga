import { queryOptions, useIsMutating, useQueryClient } from '@tanstack/react-query';

import type { SettingsSnapshotDto } from '../../../../core/api/generated/wire.ts';
import { getSettings, patchSettings } from '../../../../core/api/operations.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { NotSentError } from '../../../../core/domain/failure-class.ts';
import { readFailureOf } from '../../../../core/domain/read-failure.ts';
import { cancelThenInvalidate } from '../events/query-refresh.ts';
import { useOnlineMutation } from './online-mutation.ts';
import { queryKeys } from './query-keys.ts';
import { runOperation } from './query-operation.ts';

export function appSettingsQueryOptions(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  return queryOptions({
    queryKey: queryKeys.appSettings(),
    queryFn: ({ signal }) => runOperation(transport, { ...getSettings(), signal }, unauthorized),
    retry: false,
  });
}

type SettingChange = Readonly<{ expectedRev: number; key: string; value: string | number }>;

function saveErrorText(error: Error): string {
  const failure = readFailureOf(error);
  if (failure?.kind === 'http' && failure.status === 409)
    return 'Planner defaults changed on the server. Choose again to apply your change.';
  if (failure?.kind === 'http' && failure.status === 400)
    return `Could not save the planner default. ${failure.message}`;
  if (error instanceof NotSentError) return 'Could not save the planner default.';
  return 'Could not confirm the save.';
}

export function usePatchAppSettings(transport: ApiTransportPort, unauthorized: UnauthorizedChannel) {
  const client = useQueryClient();
  const queryKey = queryKeys.appSettings();
  const filters = { mutationKey: queryKey, exact: true };
  const saving = useIsMutating(filters) > 0;
  const mutation = useOnlineMutation({
    mutationKey: queryKey,
    mutationFn: ({ expectedRev, key, value }: SettingChange) =>
      runOperation(transport, patchSettings({ expectedRev, patch: { [key]: value } }), unauthorized),
    onSuccess: async (snapshot, { expectedRev }) => {
      await client.cancelQueries({ queryKey, exact: true });
      const current = client.getQueryData<SettingsSnapshotDto>(queryKey);
      // A GET that completed during a successful compare-and-set may already hold a newer revision.
      if (current === undefined || current.rev === expectedRev || current.rev === snapshot.rev)
        client.setQueryData(queryKey, snapshot);
    },
    onError: (error) => {
      const failure = readFailureOf(error);
      if (error instanceof NotSentError || (failure?.kind === 'http' && failure.status === 400)) return;
      return cancelThenInvalidate(client, queryKey);
    },
  });
  return {
    saving,
    error: mutation.error === null ? null : saveErrorText(mutation.error),
    save: (change: SettingChange) => {
      if (client.isMutating(filters) === 0) mutation.mutate(change);
    },
  };
}
