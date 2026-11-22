import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { ReportTaskRow } from '../../../../core/domain/report.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import {
  acceptTaskOperation,
  cancelTaskOperation,
  rejectTaskOperation,
} from '../../../../core/domain/task-recovery.ts';
import { TaskRecoveryDetails } from '../../features/report/task/recovery.tsx';
import { readErrorText } from '../../../../core/domain/read-failure.ts';
import { queryKeys } from '../providers/query-keys.ts';
import { runOperation } from '../providers/query-operation.ts';
import { trackDetailQueryOptions } from '../providers/track-queries.ts';
import { hasReadFailure } from '../providers/query-read-feedback.ts';
import { currentTaskExecution } from '../../../../core/domain/task-execution.ts';
import { useState } from '../../ui/state/public.ts';

export function TaskRecovery({ trackId, taskKey, expanded, transport, unauthorized, openWorker, openableWorkerIds, enableTaskActions = false }: {
  trackId: string; taskKey: string; expanded: boolean;
  transport: ApiTransportPort; unauthorized: UnauthorizedChannel;
  openWorker: (cardId: string) => void; openableWorkerIds: ReadonlySet<string>;
  enableTaskActions?: boolean;
}) {
  const client = useQueryClient();
  const snapshot = useQuery({ ...trackDetailQueryOptions(transport, trackId, unauthorized), enabled: expanded });
  const task = snapshot.data?.tasks.find(task => task.taskKey === taskKey);
  const [actionPending, setActionPending] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const refresh = async () => {
    await Promise.all([
      snapshot.refetch(),
      client.invalidateQueries({ queryKey: queryKeys.trackReport(trackId), exact: true }),
    ]);
  };
  const runTaskAction = (action: 'cancel' | 'accept' | 'reject') => {
    if (actionPending) return;
    setActionPending(true);
    setActionError(null);
    const op = action === 'cancel'
      ? cancelTaskOperation(trackId, taskKey)
      : action === 'accept'
        ? acceptTaskOperation(trackId, taskKey)
        : rejectTaskOperation(trackId, taskKey);
    void runOperation(transport, op, unauthorized)
      .then(() => refresh())
      .catch((error: unknown) => {
        setActionError(readErrorText(error, `Could not ${action} this task.`));
      })
      .finally(() => {
        setActionPending(false);
      });
  };
  const statusLower = (task?.status ?? '').toLowerCase();
  const canCancel = enableTaskActions && ['pending', 'dispatched', 'running', 'verifying'].includes(statusLower);
  const canReview = enableTaskActions && ['done', 'failed'].includes(statusLower);
  return <TaskRecoveryDetails
    current={currentTaskExecution(task)}
    task={task}
    loading={snapshot.isFetching}
    loadError={hasReadFailure(snapshot) ? readErrorText(snapshot.error, 'Could not refresh execution history.') : null}
    onRefresh={() => { void refresh(); }}
    openWorker={openWorker}
    openableWorkerIds={openableWorkerIds}
    taskActions={enableTaskActions ? {
      pending: actionPending,
      error: actionError,
      canCancel,
      canReview,
      onCancel: () => runTaskAction('cancel'),
      onAccept: () => runTaskAction('accept'),
      onReject: () => runTaskAction('reject'),
    } : undefined}
  />;
}

/** Inventory and details share the authoritative track snapshot. */
export function useCurrentTaskRows(
  transport: ApiTransportPort, unauthorized: UnauthorizedChannel,
  trackId: string, rows: readonly ReportTaskRow[],
): readonly ReportTaskRow[] {
  const snapshot = useQuery({ ...trackDetailQueryOptions(transport, trackId, unauthorized), refetchOnMount: false });
  const keyCounts = new Map<string, number>();
  for (const row of rows) keyCounts.set(row.key, (keyCounts.get(row.key) ?? 0) + 1);
  return rows.map(row => {
    if (row.state === 'withdrawn' || row.state === 'unreadable' || keyCounts.get(row.key) !== 1) return row;
    const execution = currentTaskExecution(snapshot.data?.tasks.find(task => task.taskKey === row.key));
    return execution === undefined ? row : { ...row, execution };
  });
}
