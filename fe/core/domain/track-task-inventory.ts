import type { TaskDto } from '../api/perga-v1.js';
import { normalizeWireAgentProvider } from '../api/schemas.js';
import type { ReportBlock } from './report.js';
import { currentTaskExecution, type CurrentTaskExecution } from './task-execution.js';

export type ReportTaskState = 'ready' | 'not-ready' | 'withdrawn' | 'unreadable';

export type TaskWorkerKind = string;

/**
 * One row of the panel's task inventory. The declaration word, status and kind travel separately
 * because they are rendered separately.
 */
export type ReportTaskRow = Readonly<{
  /** Execution details from the Track snapshot. */
  execution?: CurrentTaskExecution;
  /** The block to reveal in the document — the detail lives there, not here. */
  blockId: string;
  key: string;
  state: ReportTaskState;
  /** The word the *declaration* carries; `null` for the ordinary case and for a row that has a `status`. */
  declaration: string | null;
  /** The kernel's `tasks` row status, or `null` when there is no run this row may report. */
  status: string | null;
  /**
   * The kernel's reason for that status, only ever set alongside a `status`; already bounded to
   * `TASK_STATUS_DETAIL_LIMIT`.
   */
  statusDetail: string | null;
  /** The worker kind, off the *declaration*; a withdrawn or unreadable block has none. */
  kind: TaskWorkerKind | null;
  /** The worker card this task is running on, or `null`. */
  workerCardId: string | null;
  /** A server-rendered, actionable pending/admission explanation. */
  pendingReason: { message: string } | null;
}>;

/** The detail's only destinations are an accessible name and a `title`, neither of which a stylesheet can truncate. */
export const TASK_STATUS_DETAIL_LIMIT = 160;

/** The kernel's reason as one bounded line, or `null`; whitespace is collapsed before the bound. */
export function boundedStatusDetail(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') return null;
  const collapsed = raw.replace(/\s+/g, ' ').trim();
  if (collapsed === '') return null;
  if (collapsed.length <= TASK_STATUS_DETAIL_LIMIT) return collapsed;
  /* The bound counts UTF-16 code units; a trailing lone high surrogate is dropped so the cut never
     lands inside a character. */
  const cut = collapsed.slice(0, TASK_STATUS_DETAIL_LIMIT - 1);
  const head = /[\uD800-\uDBFF]$/.test(cut) ? cut.slice(0, -1) : cut;
  return `${head.trimEnd()}…`;
}

/** The Track snapshot supplies sidebar rows for tasks with surviving Report anchors. */
export function trackTaskInventory(blocks: readonly ReportBlock[] | null, tasks: readonly TaskDto[]): ReportTaskRow[] {
  const byBlock = new Map(tasks.filter(task => task.archivedAtMs == null).map(task => [task.blockId, task]));
  return (blocks ?? []).flatMap((block): ReportTaskRow[] => {
    if (block.kind !== 'unsupported' || block.declaredKind !== 'task') return [];
    const task = byBlock.get(block.id);
    if (task === undefined) return [];
    const provider = normalizeWireAgentProvider(task.provider ?? undefined);
    const kind: TaskWorkerKind = task.kind === 'command' || task.kind === 'terminal' ? 'terminal'
      : task.kind === 'compact' ? 'compact'
        : provider ?? task.kind;
    const status = task.status === 'draft' ? null : task.status;
    return [{
      blockId: block.id, key: task.taskKey, kind, state: task.ready ? 'ready' : 'not-ready',
      declaration: status === null && !task.ready ? 'Not ready' : null,
      status, statusDetail: status === null ? null : boundedStatusDetail(task.statusDetail), workerCardId: task.sessionId ?? null,
      pendingReason: null, execution: status === null ? undefined : currentTaskExecution(task),
    }];
  });
}
