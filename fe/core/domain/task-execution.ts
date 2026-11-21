import type { TaskDto } from '../api/perga-v1.js';
import { taskStatusLabel } from './task-recovery.js';

/** Current task execution, projected from the task that owns its session. */
export type CurrentTaskExecution = Readonly<{
  sessionId: string | null;
  runCount: number;
  status: string;
  label: string;
  statusDetail: string | null;
  workerCardId: string | null;
  blockingReason: string | null;
  createdAtMs: number;
  finishedAtMs: number | null;
}>;

export function currentTaskExecution(task: TaskDto | undefined): CurrentTaskExecution | undefined {
  if (task === undefined) return undefined;
  return { sessionId: task.sessionId ?? null, runCount: task.runCount, status: task.status,
    label: taskStatusLabel(task.status), statusDetail: task.statusDetail ?? null,
    workerCardId: task.sessionId ?? null, blockingReason: null,
    createdAtMs: task.startedAtMs ?? task.createdAtMs, finishedAtMs: task.finishedAtMs ?? null };
}
