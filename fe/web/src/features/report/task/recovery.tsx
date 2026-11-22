import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import type { CurrentTaskExecution } from '../../../../../core/domain/task-execution.ts';
import type { TaskDto } from '../../../../../core/api/perga-v1.ts';
import { taskEvidence } from '../../../../../core/domain/task-recovery.ts';
import { Button } from '@astryxdesign/core/Button';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import styles from './task.module.css';

export type TaskRecoveryProps = Readonly<{
  task: TaskDto | undefined;
  current: CurrentTaskExecution | undefined;
  loading: boolean;
  loadError: string | null;
  onRefresh: () => void;
  openWorker: ((cardId: string) => void) | undefined;
  openableWorkerIds: ReadonlySet<string>;
  taskActions?: Readonly<{
    pending: boolean;
    error: string | null;
    canCancel: boolean;
    onCancel: () => void;
  }>;
}>;

/** Rendering only: request ownership belongs to app/core. */
export function TaskRecoveryDetails({ task, current, loading, loadError,
  onRefresh, openWorker, openableWorkerIds, taskActions }: TaskRecoveryProps) {
  const empty = !loading && loadError === null && task === undefined && current === undefined;
  return <section className={styles.recovery} aria-label="Task execution">
    {loading && task === undefined && <p role="status">Loading execution history…</p>}
    {loadError !== null && <ErrorBox message={loadError}
      onRetry={onRefresh} actionLabel="Refresh execution history" pending={loading} />}
    {empty && <EmptyState variant="compact" icon={<Icon name="tasks" />} title="No execution yet" description="任务开始执行后，记录会显示在这里。" />}
    {current !== undefined && <>
      <p className={styles.current}>{`Run ${current.runCount} · ${current.label}`}</p>
      {current.statusDetail !== null && <p className={styles.detail}>{current.statusDetail}</p>}
      {current.blockingReason !== null && <p className={styles.detail}>{current.blockingReason}</p>}
    </>}
    {taskActions?.error !== null && taskActions?.error !== undefined && (
      <ErrorBox message={taskActions.error} onRetry={onRefresh} />
    )}
    {!empty && loadError === null && <div className={styles.actions}>
      <Button type="button" variant="secondary" size="sm" label="Refresh execution history"
        isLoading={loading} onClick={onRefresh} />
      {taskActions?.canCancel === true && (
        <Button type="button" variant="secondary" size="sm" label="Cancel task"
          isDisabled={taskActions.pending} onClick={taskActions.onCancel} />
      )}
    </div>}
    {task !== undefined && <TaskEvidenceDetails task={task} openWorker={openWorker} openableWorkerIds={openableWorkerIds} />}
  </section>;
}

function TaskEvidenceDetails({ task, openWorker, openableWorkerIds }: {
  task: TaskDto;
  openWorker: TaskRecoveryProps['openWorker'];
  openableWorkerIds: ReadonlySet<string>;
}) {
  const evidence = taskEvidence(task);
  return <div className={styles.detail}>
    {task.startedAtMs != null && <p>Started <time dateTime={new Date(task.startedAtMs).toISOString()}>{new Date(task.startedAtMs).toLocaleString()}</time></p>}
    {task.finishedAtMs != null && <p>Finished <time dateTime={new Date(task.finishedAtMs).toISOString()}>{new Date(task.finishedAtMs).toLocaleString()}</time></p>}
    {evidence.workerResult !== null && evidence.workerResult !== '' && <div><p>Worker result</p><pre className={styles.logOutput}>{evidence.workerResult}</pre></div>}
    {evidence.gateSteps.length > 0 && <div>
      <p>{`Verification gate: ${evidence.gatePassed ? 'Passed' : 'Failed'}`}</p>
      {evidence.gateSteps.map(step => <div key={step.name}>
        <p>{`${step.name} · exit ${step.exitCode} (${step.durationMs} ms)`}</p>
        {step.output !== '' && <pre className={styles.logOutput}>{step.output}</pre>}
      </div>)}
    </div>}
    {task.sessionId != null && openWorker !== undefined && openableWorkerIds.has(task.sessionId)
      && <Button type="button" variant="secondary" size="sm" label="Open task session" onClick={() => { openWorker(task.sessionId!); }} />}
  </div>;
}
