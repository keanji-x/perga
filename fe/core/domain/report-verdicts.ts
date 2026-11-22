import { z } from 'zod';

import { taskDtoSchema, type TaskDto } from '../api/perga-v1.js';

const taskPendingReasonSchema = z.discriminatedUnion('kind', [
  z.object({
    kind: z.literal('dependencyBlocked'),
    message: z.string().min(1),
    dependencies: z.array(z.string()),
  }),
  z.object({
    kind: z.literal('trackBusy'),
    message: z.string().min(1),
  }),
  z.object({
    kind: z.literal('notAdmitted'),
    message: z.string().min(1),
    diagnosticCodes: z.array(z.string()),
    actions: z.array(z.string()),
  }),
]);

export type TaskPendingReason = z.infer<typeof taskPendingReasonSchema>;

export const taskVerdictSchema = z.object({
  /** The declaring block; the same literal the report card's own `blocks[]` carries. */
  blockId: z.string(),
  key: z.string(),
  /** The projection's verdict on the *declaration*: ready, not withdrawn, and no blocking diagnostic. */
  schedulable: z.boolean(),
  /**
   * The `tasks` row's status; absent when the task has no row. Read as a string so an unknown
   * status is shown, not hidden.
   */
  status: z.string().nullish(),
  /** Why the row is in that status, in the kernel's own words; absent whenever the kernel has nothing to add. */
  statusDetail: z.string().nullish(),
  /** The worker card the task was dispatched onto; absent until claim. */
  workerCardId: z.string().nullish(),
  /** The server's finished answer to "why has this task not started?"; displayed verbatim, never re-derived. */
  pendingReason: taskPendingReasonSchema.nullish(),
});

export type TaskVerdict = z.infer<typeof taskVerdictSchema>;

export function taskDtoToVerdict(task: TaskDto): TaskVerdict {
  return {
    blockId: task.blockId,
    key: task.taskKey,
    schedulable: task.ready && task.status !== 'canceled',
    status: task.status === 'draft' ? null : task.status,
    statusDetail: task.statusDetail ?? task.semanticNote ?? null,
    workerCardId: null,
    pendingReason: null,
  };
}

const legacyTrackReportReadSchema = z.object({ taskDiagnostics: z.array(z.unknown()).default([]) })
  .transform((response) => response.taskDiagnostics.flatMap((candidate) => {
    // One malformed verdict costs only that row's runtime word.
    const parsed = taskVerdictSchema.safeParse(candidate);
    return parsed.success ? [parsed.data] : [];
  }));

export const trackReportReadSchema = z.union([
  z.object({
    tasks: z.array(z.unknown()),
    taskDiagnostics: z.undefined().optional(),
  }).passthrough().transform((response) => response.tasks.flatMap((candidate) => {
    const parsed = taskDtoSchema.safeParse(candidate);
    return parsed.success ? [taskDtoToVerdict(parsed.data)] : [];
  })),
  legacyTrackReportReadSchema,
]);
