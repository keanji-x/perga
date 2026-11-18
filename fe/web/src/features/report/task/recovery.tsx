import { EmptyState } from '../../../ui/empty-state/public.tsx';
import { Icon } from '../../../ui/icon/public.tsx';
import type { CurrentTaskExecution } from '../../../../../core/domain/task-execution.ts';
import type { ReactNode } from 'react';
import {
  attemptStatusLabel,
  type PergaAttemptEvidence,
  type TaskAttempt,
  type TaskRecoveryView,
} from '../../../../../core/domain/task-recovery.ts';
import { Button } from '@astryxdesign/core/Button';
import { ErrorBox } from '../../../ui/error-box/public.tsx';
import styles from './task.module.css';

export type TaskRecoveryProps = Readonly<{
  view: TaskRecoveryView | undefined;
  evidenceByAttemptId?: Readonly<Record<string, PergaAttemptEvidence>>;
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
    canReview: boolean;
    onCancel: () => void;
    onAccept: () => void;
    onReject: () => void;
  }>;
}>;

/** Rendering only: request ownership belongs to app/core. */
export function TaskRecoveryDetails({ view, evidenceByAttemptId, current, loading, loadError,
  onRefresh, openWorker, openableWorkerIds, taskActions }: TaskRecoveryProps) {
  const empty = !loading && loadError === null && view?.current === null && current === undefined;
  return <section className={styles.recovery} aria-label="Task execution">
    {loading && view === undefined && <p role="status">Loading execution history…</p>}
    {loadError !== null && <ErrorBox message={loadError}
      onRetry={onRefresh} actionLabel="Refresh execution history" pending={loading} />}
    {empty && <EmptyState variant="compact" icon={<Icon name="tasks" />} title="No attempts yet" description="任务开始执行后，记录会显示在这里。" />}
    {current !== undefined && <>
      <p className={styles.current}>{`Current attempt ${current.generation} · ${current.label}`}</p>
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
      {taskActions?.canReview === true && (
        <>
          <Button type="button" variant="secondary" size="sm" label="Accept task"
            isDisabled={taskActions.pending} onClick={taskActions.onAccept} />
          <Button type="button" variant="secondary" size="sm" label="Reject task"
            isDisabled={taskActions.pending} onClick={taskActions.onReject} />
        </>
      )}
    </div>}
    {view !== undefined && !empty && <details className={styles.history}>
      <summary>Attempt history ({view.attempts.length})</summary>
      <ol className={styles.attempts}>
        {view.attempts.map((attempt) => <li key={attempt.attempt_id}>
          <details>
            <summary>Attempt {attempt.generation} · {attemptStatusLabel(attempt.status)}
              {attempt.attempt_id === current?.attemptId ? ' · Current' : ''}</summary>
            <AttemptEvidence attempt={attempt} evidence={evidenceByAttemptId?.[attempt.attempt_id]}>
              {attempt.worker_card_id !== null && openWorker !== undefined
                && openableWorkerIds.has(attempt.worker_card_id)
                && <Button type="button" variant="secondary" size="sm" label={`Open attempt ${attempt.generation}`}
                  onClick={() => { openWorker(attempt.worker_card_id!); }} />}
            </AttemptEvidence>
          </details>
        </li>)}
      </ol>
    </details>}
  </section>;
}

function AttemptEvidence({ attempt, evidence, children }: {
  attempt: TaskAttempt;
  evidence?: PergaAttemptEvidence;
  children: ReactNode;
}) {
  return <div className={styles.detail}>
    <p>Created <time dateTime={new Date(attempt.created_at_ms).toISOString()}>{new Date(attempt.created_at_ms).toLocaleString()}</time></p>
    {attempt.finished_at_ms !== null && <p>Finished <time dateTime={new Date(attempt.finished_at_ms).toISOString()}>
      {new Date(attempt.finished_at_ms).toLocaleString()}</time></p>}
    {attempt.status_detail !== null && <p>{attempt.status_detail}</p>}
    {evidence?.workerResult !== null && evidence?.workerResult !== undefined && evidence.workerResult !== '' && (
      <div>
        <p>Worker result</p>
        <pre className={styles.logOutput}>{evidence.workerResult}</pre>
      </div>
    )}
    {evidence !== undefined && evidence.gateSteps.length > 0 && (
      <div>
        <p>{`Verification gate: ${evidence.gatePassed ? 'Passed' : 'Failed'}`}</p>
        {evidence.gateSteps.map((step) => (
          <div key={step.name}>
            <p>{`${step.name} · exit ${step.exitCode} (${step.durationMs} ms)`}</p>
            {step.output !== '' && <pre className={styles.logOutput}>{step.output}</pre>}
          </div>
        ))}
      </div>
    )}
    {children}
  </div>;
}
